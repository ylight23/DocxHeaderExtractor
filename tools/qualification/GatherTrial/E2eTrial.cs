using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Output;

/// <summary>
/// The whole gather as the admin page runs it, end to end with the live model: the headings of the target and the sources (in parallel, through
/// the outline store), the heading-chain plan and the tracked copy. Four phases on copies of the inputs, each timed from start to written copy:
/// cold (empty store), warm (same files), body-edit (one source with paragraphs added under a heading) and heading-edit (one source with a
/// heading's text changed). The inputs are never changed; every phase checks their hashes.
/// Usage: GatherTrial e2e &lt;output-directory&gt; &lt;target.docx&gt; &lt;edited-source.docx&gt; &lt;other-source.docx&gt;...
/// </summary>
static class E2eTrial
{
    sealed record Doc(string Role, string Id, string Path);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("e2e <output-directory> <target.docx> <edited-source.docx> <other-source.docx>...");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var originals = args[2..].Select(Path.GetFullPath).ToArray();
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var originalHashes = originals.ToDictionary(p => p, Hash);

        // Copies only: the phases read and edit these, never the inputs.
        var inputs = Path.Combine(output, "inputs");
        Directory.CreateDirectory(inputs);
        var docs = originals.Select((p, i) => new Doc(i == 0 ? "target" : "source", i == 0 ? "target" : "S" + i, Path.Combine(inputs, Path.GetFileName(p)))).ToArray();
        foreach (var (from, d) in originals.Zip(docs)) File.Copy(from, d.Path);

        var options = new PipelineOptions { MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
        options.Log = _ => { };
        var store = new OutlineStore(Path.Combine(output, "outline-store"));
        var configKey = OutlineReuse.ConfigKey(options);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var phases = new List<object>();
        var contentHashes = new Dictionary<string, string>();

        async Task Phase(string name, Doc[] phaseDocs)
        {
            Console.WriteLine($"== {name}");
            var clock = Stopwatch.StartNew();
            var perDoc = await Task.WhenAll(phaseDocs.Select(async d =>
            {
                var docClock = Stopwatch.StartNew();
                RunResult? run = null;
                string? error = null;
                async Task<IReadOnlyList<HeadingRecord>> Extract()
                {
                    run = await new DocumentRunner(options, provider).ExtractAsync(d.Path);
                    return run.Outline.Headings;
                }
                OutlineReuseResult? reuse = null;
                try { reuse = await OutlineReuse.GetAsync(d.Path, Path.GetFileName(d.Path), store, configKey, Extract); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                var audit = run?.HeadingPipeline?.Audit;
                Console.WriteLine($"   {d.Id} {reuse?.Status ?? "ERROR"} {docClock.Elapsed.TotalSeconds:F1}s selectionCalls={audit?.SelectionProviderCalls ?? 0} placementCalls={audit?.PlacementExecution?.LogicalCalls ?? 0}");
                return (Doc: d, Reuse: reuse, Seconds: docClock.Elapsed.TotalSeconds, Error: error,
                    SelectionCalls: audit?.SelectionProviderCalls ?? 0, PlacementCalls: audit?.PlacementExecution?.LogicalCalls ?? 0,
                    Budget: run?.ResourceBudget, Failures: run?.HeadingPipeline?.Audit?.SelectionResources);
            }));
            var extractSeconds = clock.Elapsed.TotalSeconds;
            if (perDoc.Any(p => p.Reuse is null))
            {
                phases.Add(new { phase = name, totalSeconds = clock.Elapsed.TotalSeconds, documents = perDoc.Select(p => new { p.Doc.Id, p.Error }) });
                return;
            }
            var target = perDoc.Single(p => p.Doc.Role == "target");
            var sources = perDoc.Where(p => p.Doc.Role == "source").Select(p => new GatherSourceFile(p.Doc.Id, p.Doc.Path, p.Reuse!.Headings)).ToList();
            var gatherClock = Stopwatch.StartNew();
            var plan = OutlineReuse.FlagAdded(GatherByHeading.Build(target.Doc.Path, target.Reuse!.Headings, sources, addMissingHeadings: true),
                perDoc.Where(p => p.Reuse!.Status == "MAPPED").ToDictionary(p => p.Doc.Id, p => p.Reuse!.AddedParagraphs));
            var decided = GatherByHeading.DecidedByChain(plan.Sections);
            var copy = Path.Combine(output, name + "_tong_hop.docx");
            var written = GatherWriteback.Apply(target.Doc.Path, copy, decided.Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList(),
                DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target.Doc.Path));
            var gatherSeconds = gatherClock.Elapsed.TotalSeconds;
            var content = string.Join("\n", decided.SelectMany(s => s.Run.Facts.Select(f => s.Heading + "␟" + f.Render())));
            contentHashes[name] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
            phases.Add(new
            {
                phase = name, totalSeconds = clock.Elapsed.TotalSeconds, extractSeconds, gatherAndWriteSeconds = gatherSeconds,
                modelCalls = perDoc.Sum(p => p.SelectionCalls + p.PlacementCalls),
                documents = perDoc.Select(p => new
                {
                    p.Doc.Id, file = Path.GetFileName(p.Doc.Path), status = p.Reuse!.Status, p.Reuse.Reason, addedParagraphs = p.Reuse.AddedParagraphs,
                    headings = p.Reuse.Headings.Count, seconds = Math.Round(p.Seconds, 2), p.SelectionCalls, p.PlacementCalls, budget = p.Budget,
                }),
                facts = decided.Sum(s => s.Run.Facts.Count), newHeadings = decided.Count(s => s.NewHeadingDepth is not null),
                flaggedUnverified = decided.Sum(s => s.Run.Facts.Count(f => f.Flags.Any(x => x.StartsWith("UNVERIFIED_STRUCTURE", StringComparison.Ordinal)))),
                uncertain = plan.Uncertain.Count, excluded = plan.Excluded.Count, skipped = written.SkippedHeadings.Count,
                contentHash = contentHashes[name],
            });
            Console.WriteLine($"   total {clock.Elapsed.TotalSeconds:F1}s (extract {extractSeconds:F1}s, gather+write {gatherSeconds:F2}s) content={contentHashes[name]}");
        }

        await Phase("cold", docs);
        await Phase("warm", docs);

        // One source edited: the first source after the target, in its own folder under the same file name so the store can find its earlier version.
        var edited = docs[1];
        var stored = store.Latest(Path.GetFileName(edited.Path), configKey) ?? throw new InvalidDataException("NO_STORED_OUTLINE_FOR_EDIT");
        var headingLocations = stored.Headings.OrderBy(h => h.Index).Select(h => h.StableId!)
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^body\[1\]/p\[\d+\]$")).ToArray();
        Doc EditedCopy(string phase, Action<Body, Dictionary<string, Paragraph>> edit)
        {
            var dir = Path.Combine(output, phase);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Path.GetFileName(edited.Path));
            File.Copy(edited.Path, path);
            using (var doc = WordprocessingDocument.Open(path, true))
            {
                var body = doc.MainDocumentPart!.Document!.Body!;
                // The walker names a body paragraph body[1]/p[N]: the N-th paragraph directly in the body. Checked against the stored heading text.
                var top = body.Elements<Paragraph>().ToList();
                var byLocation = new Dictionary<string, Paragraph>(StringComparer.Ordinal);
                foreach (var h in stored.Headings.Where(h => h.StableId is not null))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(h.StableId!, @"^body\[1\]/p\[(\d+)\]$");
                    if (!m.Success) continue;
                    var paragraph = top[int.Parse(m.Groups[1].Value) - 1];
                    // A heading may span paragraphs ("BÁO CÁO" + the title line): its text starts with this paragraph's text.
                    var own = paragraph.InnerText.Trim();
                    if (own.Length == 0 || !h.Text.Trim().StartsWith(own[..Math.Min(12, own.Length)], StringComparison.Ordinal))
                        throw new InvalidDataException("HEADING_LOCATION_MISMATCH:" + h.StableId);
                    byLocation[h.StableId!] = paragraph;
                }
                edit(body, byLocation);
                doc.MainDocumentPart.Document.Save();
            }
            return edited with { Path = path };
        }
        var bodyEdit = EditedCopy("body-edit", (_, at) =>
        {
            // Two plain paragraphs right after the second heading: added content under an existing heading.
            var anchor = at[headingLocations[1]];
            anchor.InsertAfterSelf(new Paragraph(new Run(new Text("Thông tin bổ sung thứ hai cho mục này."))));
            anchor.InsertAfterSelf(new Paragraph(new Run(new Text("Thông tin bổ sung thứ nhất cho mục này."))));
        });
        await Phase("body-edit", docs.Select(d => d == edited ? bodyEdit : d).ToArray());
        var headingEdit = EditedCopy("heading-edit", (_, at) =>
        {
            var text = at[headingLocations[1]].Descendants<Text>().Last();
            text.Text += " (sửa)";
        });
        await Phase("heading-edit", docs.Select(d => d == edited ? headingEdit : d).ToArray());

        foreach (var (path, hash) in originalHashes)
            if (Hash(path) != hash) throw new InvalidDataException("INPUT_CHANGED:" + path);
        File.WriteAllText(Path.Combine(output, "e2e.json"), JsonSerializer.Serialize(new
        {
            machine = Environment.MachineName, configKey, inputs = originalHashes, editedSource = edited.Id, editedHeading = headingLocations[1],
            warmSameAsCold = contentHashes.GetValueOrDefault("warm") == contentHashes.GetValueOrDefault("cold"), phases,
        }, json));
        return 0;
    }
}
