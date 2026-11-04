using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>
/// Measures the steps of a gather that call no model, on real files: hashing, reading, comparing two versions, matching by heading chain,
/// writing, loading a stored outline, and how soon a file change is seen. Median of repeated runs after a warm-up. Never changes its inputs.
/// Usage: GatherTrial bench &lt;target.docx&gt; &lt;target-outline.json&gt; &lt;output.json&gt; &lt;id=source.docx=outline.json&gt;...
/// </summary>
static class BenchTrial
{
    const int Runs = 25;

    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: GatherTrial bench <target.docx> <target-outline.json> <output.json> <id=source.docx=outline.json>...");
            return 2;
        }
        var target = Path.GetFullPath(args[1]);
        var outlinePath = Path.GetFullPath(args[2]);
        var targetOutline = DocumentTrial.Outline(outlinePath);
        var sources = args[4..].Select(a => a.Split('=')).Select(p => new GatherSourceFile(p[0], Path.GetFullPath(p[1]), DocumentTrial.Outline(p[2]))).ToList();
        var inputs = new[] { target }.Concat(sources.Select(s => s.Path)).ToList();
        var before = inputs.ToDictionary(p => p, p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
        var work = Directory.CreateTempSubdirectory("dhx-bench-").FullName;
        var results = new List<object>();
        void Measure(string step, string note, Action action)
        {
            action();
            var ms = new List<double>();
            for (var i = 0; i < Runs; i++)
            {
                var clock = Stopwatch.StartNew();
                action();
                ms.Add(clock.Elapsed.TotalMilliseconds);
            }
            ms.Sort();
            results.Add(new { step, note, medianMs = Math.Round(ms[ms.Count / 2], 2), minMs = Math.Round(ms[0], 2), maxMs = Math.Round(ms[^1], 2) });
            Console.WriteLine($"{step,-34} median {ms[ms.Count / 2],9:F2} ms   min {ms[0],8:F2}   max {ms[^1],8:F2}   {note}");
        }
        try
        {
            var size = new FileInfo(target).Length;
            Measure("hash_sha256_target", $"{size / 1024} KB", () => SHA256.HashData(File.ReadAllBytes(target)));

            // A stored outline: write the target's headings as a JSON file once, then time reading it back (what a cache hit costs).
            var stored = Path.Combine(work, "outline.json");
            File.WriteAllText(stored, JsonSerializer.Serialize(targetOutline));
            Measure("load_stored_outline_json", $"{targetOutline.Length} headings", () =>
                JsonSerializer.Deserialize<HeadingRecord[]>(File.ReadAllText(stored)));

            Measure("read_units_target", "GatherUnits.Read with outline", () => GatherUnits.Read(target, "target", targetOutline));
            Measure("read_version_target", "DocxVersionReader.Read", () => DocxVersionReader.Read(target));

            // A new version of one source: the same file with five paragraphs added before its last body paragraph.
            var original = sources[^1].Path;
            var updated = Path.Combine(work, "updated.docx");
            File.Copy(original, updated);
            using (var doc = WordprocessingDocument.Open(updated, true))
            {
                var body = doc.MainDocumentPart!.Document!.Body!;
                var anchor = body.Elements<Paragraph>().Last(p => p.InnerText.Trim().Length > 0);
                for (var i = 5; i >= 1; i--)
                    anchor.InsertBeforeSelf(new Paragraph(new Run(new Text($"Thông tin mới bổ sung số {i} cho mục cuối.") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
                doc.MainDocumentPart.Document.Save();
            }
            var oldVersion = DocxVersionReader.Read(original);
            var newVersion = DocxVersionReader.Read(updated);
            var diff = DocxComparer.Compare(oldVersion, newVersion);
            Measure("compare_versions_read_and_diff", $"{diff.Changes.Count} changes found", () => DocxComparer.Compare(original, updated));
            Measure("compare_versions_diff_only", "both versions already read", () => DocxComparer.Compare(oldVersion, newVersion));

            Measure("gather_build_chain", $"target + {sources.Count} sources", () => GatherByHeading.Build(target, targetOutline, sources, addMissingHeadings: true));
            var plan = GatherByHeading.Build(target, targetOutline, sources, addMissingHeadings: true);
            var decided = GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList();
            var n = 0;
            Measure("writeback_tracked_copy", $"{decided.Sum(s => s.Run.Facts.Count)} facts", () =>
                GatherWriteback.Apply(target, Path.Combine(work, $"out-{n++}.docx"), decided, DateTime.UtcNow, true, before[target]));

            // How soon a saved file is seen: FileSystemWatcher, from the end of the write to the event.
            var watched = Directory.CreateDirectory(Path.Combine(work, "watched")).FullName;
            using var watcher = new FileSystemWatcher(watched) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            var seen = new ManualResetEventSlim();
            watcher.Created += (_, _) => seen.Set();
            watcher.Changed += (_, _) => seen.Set();
            var w = 0;
            Measure("file_change_seen_by_watcher", "write a .docx copy, wait for the event", () =>
            {
                seen.Reset();
                File.Copy(original, Path.Combine(watched, $"f{w++}.docx"));
                if (!seen.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("WATCHER_NO_EVENT");
            });

            foreach (var (path, hash) in before)
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != hash) throw new InvalidDataException("INPUT_CHANGED:" + path);
            File.WriteAllText(Path.GetFullPath(args[3]), JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName, processors = Environment.ProcessorCount, runtime = Environment.Version.ToString(), runs = Runs,
                target, sources = sources.Select(s => s.Path), diffChanges = diff.Changes.Count, results,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
