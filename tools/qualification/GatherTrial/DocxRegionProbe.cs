using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>Offline diagnostic on copies; assumed dependency hashes are not authorization to reuse any outline.</summary>
static class DocxRegionProbe
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("docx-region-probe <e2e-trial-directory> <new-output-directory>");
        var trial = Path.GetFullPath(args[1]); var output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        Directory.CreateDirectory(output);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(trial, "e2e.json")));
        var key = run.RootElement.GetProperty("configKey").GetString()!;
        var store = new OutlineStore(Path.Combine(trial, "outline-store"));
        var rows = new List<object>();
        foreach (var name in new[] { "08.docx", "09.docx", "10.docx" })
        {
            var input = Path.Combine(trial, "inputs", name); var copy = Path.Combine(output, name);
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)));
            var old = store.Find(sha, key) ?? throw new InvalidDataException("STORED_OUTLINE_MISSING:" + name);
            var original = DocxVersionReader.Read(input);
            var originalTexts = original.Paragraphs.ToDictionary(p => p.Location, p => p.RawText, StringComparer.Ordinal);
            var bindingAudit = ExperimentalHeadingBindings.Check(old.Headings, originalTexts);
            File.Copy(input, copy);
            using (var doc = WordprocessingDocument.Open(copy, true))
            {
                var body = doc.MainDocumentPart!.Document!.Body!;
                var paragraphs = body.Elements<Paragraph>().ToArray();
                if (paragraphs.Length < 2) throw new InvalidDataException("INSUFFICIENT_BODY_PARAGRAPHS:" + name);
                paragraphs[paragraphs.Length / 2].InsertAfterSelf(new Paragraph(new Run(new Text("Đoạn thử nghiệm vùng được thêm vào bản sao."))));
                doc.MainDocumentPart.Document.Save();
            }
            var version = DocxVersionReader.Read(copy);
            var current = OutlineStore.Paragraphs(version);
            var verified = ExperimentalOutlineRegion.Plan(old, current, version.UnsupportedRegions, null, null, false);
            // Isolate the duplicate heuristic only; these placeholders do not establish matching dependencies or complete bindings.
            var assumed = ExperimentalOutlineRegion.Plan(old, current, version.UnsupportedRegions, "diagnostic-assumption", "diagnostic-assumption", true);
            rows.Add(new { name, sourceSha256 = sha, changedSha256 = version.FileSha256, originalHeadings = old.Headings.Count,
                bindingAudit = new { bindingAudit.Complete, problemCount = bindingAudit.Problems.Count },
                actualGate = new { verified.Status, verified.Reason }, duplicateProbeOnly = new { assumed.Status, assumed.Reason, assumed.Spans, carried = assumed.CarriedHeadings.Count, redecide = assumed.HeadingsToDecide.Count },
                warning = "Dependency hashes and heading bindings were assumed solely for diagnostic; do not reuse the result." });
            Console.WriteLine($"{name}: actual={verified.Status}/{verified.Reason}; duplicate-probe={assumed.Status}/{assumed.Reason}");
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "region-probe.json"), JsonSerializer.Serialize(rows, options));
        return 0;
    }
}
