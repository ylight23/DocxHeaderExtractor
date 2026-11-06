using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

if (args.Length >= 1 && args[0] == "headings")
    return HeadingTrial.Run(args);
if (args.Length >= 1 && args[0] == "bench")
    return BenchTrial.Run(args);
if (args.Length >= 1 && args[0] == "e2e")
    return await E2eTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "capture-docx")
    return await CaptureDocxTrial.RunAsync(args);
// These older live probes do not reliably capture every SELECT/GROUP/PLACE/repair attempt.
// Keep their implementation for offline review, but do not call a provider until capture is complete and verified.
if (args.Length >= 1 && args[0] is "split-run" or "whole-run" or "split-capture" or "whole-capture"
    or "place-ab" or "paired-placement" or "paired-frozen21")
    throw new InvalidOperationException("DIAGNOSTIC_CAPTURE_INCOMPLETE: live probe disabled; use a capture-complete diagnostic runner.");
if (args.Length >= 1 && args[0] == "split-replay")
    return PlaceAbTrial.SplitReplay(args);
if (args.Length >= 1 && args[0] == "outline-diff")
    return OutlineDiffTrial.Run(args);
if (args.Length >= 1 && args[0] == "e2e-replay")
    return E2eReplay.Run(args);
if (args.Length >= 1 && args[0] == "variance")
    return await VarianceTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-region")
    return await DocxRegionTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-packet-diff")
    return DocxPacketDiff.Run(args);
if (args.Length >= 1 && args[0] == "docx-region-probe")
    return DocxRegionProbe.Run(args);
if (args.Length >= 1 && args[0] == "region-replay")
    return VarianceTrial.RegionReplay(args);
if (args.Length >= 1 && args[0] == "variance-replay")
    return VarianceTrial.Replay(args);
if (args.Length >= 1 && args[0] == "docx-cache-ab")
    return await DocxCacheTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-cache-changes")
    return await DocxCacheTrial.ChangesAsync(args);
if (args.Length >= 1 && args[0] == "docx-cache-delete-move")
    return await DocxCacheTrial.ChangesAsync(args);
if (args.Length >= 1 && args[0] == "docx-cache-control")
    return await DocxCacheTrial.ControlAsync(args);
if (args.Length >= 1 && args[0] == "docx-diagnose")
    return await DocxCacheTrial.DiagnoseAsync(args);
if (args.Length >= 1 && args[0] == "docx-first-packet")
    return DocxCacheTrial.FirstPacket(args);
if (args.Length >= 1 && args[0] == "docx-boundary-packet")
    return DocxCacheTrial.FirstPacket(args);
if (args.Length >= 1 && args[0] == "docx-format-runs-packet")
    return DocxCacheTrial.FirstPacket(args);
if (args.Length >= 1 && args[0] == "docx-replay-select")
    return DocxCacheTrial.ReplaySelect(args);
if (args.Length >= 1 && args[0] == "docx-boundary-ab")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-boundary-isolate")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-boundary-trim-only")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-boundary-batch-repair")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-production-batch-repair")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-format-runs-ab")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-format-runs-pair")
    return await DocxBoundaryTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "docx-frozen-group")
    return await DocxFrozenGroupTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "headings-chain")
    return HeadingChainTrial.Run(args);
if (args.Length >= 1 && args[0] == "headings-review")
    return await HeadingReviewTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "headings-replay-reviewed")
    return HeadingReplayTrial.Run(args);
if (args.Length >= 1 && args[0] == "monthly-qwen-review")
    return await MonthlyQwenTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "monthly-qwen-pipeline")
    return await MonthlyQwenTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "monthly-qwen-writeback")
    return MonthlyQwenWriteback.Run(args);
if (args.Length >= 1 && args[0] == "monthly-subsection-audit")
    return MonthlySubsectionAudit.Run(args);
if (args.Length >= 1 && args[0] == "monthly-subsection-review")
    return await MonthlySubsectionReview.RunAsync(args);
if (args.Length >= 1 && args[0] == "monthly-quote-repair")
    return await MonthlyQuoteRepairTrial.RunAsync(args);
if (args.Length >= 1 && args[0] == "monthly-check-replay")
    return MonthlyCheckReplay.Run(args);
if (args.Length >= 1 && args[0] == "monthly-merge-repair")
    return MonthlyMergeRepair.Run(args);
if (args.Length >= 1 && args[0] == "document")
    return await DocumentTrial.RunAsync(args);
if (args.Length is not (5 or 6) || args[0] is not ("dry-run" or "run" or "replay") || (args[0] == "replay" && args.Length != 6))
{
    Console.Error.WriteLine("Usage: GatherTrial <dry-run|run|replay> <goal.txt> <source.docx> <outline.json|--style-fallback> <output-directory> [checkpoint-directory]");
    return 2;
}
var goal = File.ReadAllText(args[1]).Trim();
if (string.IsNullOrWhiteSpace(goal)) throw new InvalidDataException("EMPTY_GOAL");
var sourcePath = Path.GetFullPath(args[2]);
var sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
HeadingRecord[]? outline = args[3] == "--style-fallback" ? null :
    JsonSerializer.Deserialize<HeadingRecord[]>(File.ReadAllText(args[3]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("OUTLINE_NOT_ARRAY");
var source = GatherUnits.Read(sourcePath, "sha256:" + sourceHash, outline);
if (sourceHash != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath))))
    throw new InvalidDataException("SOURCE_CHANGED_DURING_READ");
var batches = GatherBatches.Plan(source.Units);
Console.WriteLine($"source_sha256={sourceHash} units={source.Units.Count} batches={batches.Count} gaps={source.Gaps.Count} headings={(outline is null ? "style-fallback" : "supplied-outline")}");
if (args[0] == "dry-run") return 0;

var apiKey = args[0] == "run" ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") : null;
if (args[0] == "run" && string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
var output = Path.GetFullPath(args[4]);
if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true };
// These are per-invocation ceilings; a future resume must count earlier confirmed and unknown charges separately.
const double ceilingUsd = 1.0;
var limitText = Environment.GetEnvironmentVariable("GATHER_TRIAL_MAX_CALLS");
var maxCalls = string.IsNullOrWhiteSpace(limitText) ? 8 :
    int.TryParse(limitText, out var requested) && requested is >= 1 and <= 8 ? requested :
    throw new InvalidDataException("GATHER_TRIAL_MAX_CALLS_MUST_BE_1_TO_8");
using var provider = args[0] == "run" ? new GatherOpenRouterModel(apiKey!, maxCalls, ceilingUsd, diagnosticsPath: Path.Combine(output, "provider-events.jsonl")) : null;
IGatherModel model = provider is null ? new CheckpointOnlyModel() : provider;
var identity = provider?.CheckpointIdentity ?? GatherOpenRouterModel.CheckpointIdentityFor();
var checkpointPath = args.Length == 6 ? Path.GetFullPath(args[5]) : Path.Combine(output, "checkpoints");
var checkpoints = new GatherCheckpointStore(checkpointPath, identity);
File.WriteAllText(Path.Combine(output, "run-metadata.json"), JsonSerializer.Serialize(new
{
    sourceHash, goalSha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(goal))),
    outlineMode = outline is null ? "style-fallback" : "supplied-outline", model = identity, mode = args[0],
    ceilingUsd, maxCalls, budgetScope = "this invocation only", sourceUnits = source.Units.Count, initialBatches = batches.Count,
}, options));
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var clock = System.Diagnostics.Stopwatch.StartNew();
var result = await GatherRunner.RunAsync(goal, [source], model, cancellation.Token, checkpoints);
File.WriteAllText(Path.Combine(output, "run-result.json"), JsonSerializer.Serialize(new
{
    elapsedSeconds = clock.Elapsed.TotalSeconds, result, attempts = provider?.Attempts,
}, options));
Console.WriteLine($"complete={result.Complete} facts={result.Facts.Count} failed_batches={result.BatchFailures.Count} unreviewed_units={result.UnreviewedUnitIds.Count} seconds={clock.Elapsed.TotalSeconds:F1}");
return result.Complete ? 0 : 1;

sealed class CheckpointOnlyModel : IGatherModel
{
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct) =>
        throw new GatherModelFailureException("CHECKPOINT_MISSING", false);
}

/// <summary>
/// Gathers for the headings of a target from one source, reviews each heading's facts, and writes the result into a copy of the target as tracked
/// insertions. Usage: GatherTrial document &lt;target.docx&gt; &lt;target-outline.json&gt; &lt;source.docx&gt; &lt;source-outline.json|--style-fallback&gt;
/// &lt;output-directory&gt; [heading-regex]. An outline file is either a heading array or the pipeline's JSON result.
/// </summary>
static class DocumentTrial
{
    internal static HeadingRecord[] Outline(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "Outline", "outline" })
                if (root.TryGetProperty(name, out var outline) && outline.TryGetProperty("headings", out var inner)) root = inner;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("headings", out var direct)) root = direct;
        }
        return root.Deserialize<HeadingRecord[]>(options) ?? throw new InvalidDataException("OUTLINE_NOT_ARRAY");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (6 or 7))
        {
            Console.Error.WriteLine("Usage: GatherTrial document <target.docx> <target-outline.json> <source.docx> <source-outline.json|--style-fallback> <output-directory> [heading-regex]");
            return 2;
        }
        var target = Path.GetFullPath(args[1]);
        var targetOutline = Outline(args[2]);
        var source = Path.GetFullPath(args[3]);
        HeadingRecord[]? sourceOutline = args[4] == "--style-fallback" ? null : Outline(args[4]);
        var output = Path.GetFullPath(args[5]);
        var filter = args.Length == 7 ? new System.Text.RegularExpressions.Regex(args[6]) : null;
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        Directory.CreateDirectory(output);
        const double ceilingUsd = 1.0;
        const int maxCalls = 60;
        using var provider = new GatherOpenRouterModel(apiKey, maxCalls, ceilingUsd, diagnosticsPath: Path.Combine(output, "provider-events.jsonl"));
        var checkpoints = new GatherCheckpointStore(Path.Combine(output, "checkpoints"), provider.CheckpointIdentity);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sourceName = Path.GetFileNameWithoutExtension(source)[..3];
        var sourceFile = new GatherSourceFile(sourceName, source, sourceOutline);
        var sections = await GatherDocument.RunAsync(target, targetOutline, [sourceFile], provider, CancellationToken.None, checkpoints,
            filter is null ? null : h => filter.IsMatch(h.Text));

        // Review before writing: an excluded fact is not written, an unsure or unreviewed one is written with a flag.
        var sourceUnits = new[] { GatherUnits.Read(source, sourceName, sourceOutline) };
        var reviewed = new List<GatherSection>();
        var excluded = new List<GatherExcluded>();
        var verdicts = new List<object>();
        foreach (var original in sections)
        {
            // What the target section already says is not written again; only the rest goes to review.
            var (section, repeated) = GatherReviewApply.RemoveAlreadyInTarget(original);
            excluded.AddRange(repeated);
            if (section.Run.Facts.Count == 0) { reviewed.Add(section); continue; }
            var review = await GatherSemanticReview.ReviewAsync(section.Goal, section.Run, sourceUnits, provider);
            verdicts.Add(new { section.Heading, review.Error, review.Verdicts });
            var (applied, dropped) = GatherReviewApply.Apply(section, review);
            reviewed.Add(applied);
            excluded.AddRange(dropped);
        }
        var final = GatherReviewApply.FlagAcrossHeadings(reviewed);

        var copy = Path.Combine(output, Path.GetFileNameWithoutExtension(target) + "_gathered.docx");
        var written = GatherWriteback.Apply(target, copy, final, DateTime.UtcNow);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "sections.json"), JsonSerializer.Serialize(new
        {
            elapsedSeconds = clock.Elapsed.TotalSeconds, model = provider.CheckpointIdentity, ceilingUsd, maxCalls,
            sourceOutline = sourceOutline is null ? "style-fallback" : "pipeline", skipped = written.SkippedHeadings,
            sections = final.Select(s => new
            {
                s.Heading, s.Goal, s.TargetLocation, s.NextHeadingLocation, complete = s.Run.Complete,
                facts = s.Run.Facts.Select(f => new { text = f.Render(), f.Flags, sources = f.Parts.Select(p => p.SourceId + " " + p.Location) }),
                refused = s.Run.Refused, failures = s.Run.BatchFailures, conflicts = s.Conflicts,
            }),
            excluded = excluded.Select(e => new { e.Heading, text = e.Fact.Render(), e.Reason }),
            reviews = verdicts,
            attempts = provider.Attempts,
        }, options));
        Console.WriteLine($"sections={final.Count} facts={final.Sum(s => s.Run.Facts.Count)} excluded={excluded.Count} skipped={written.SkippedHeadings.Count} seconds={clock.Elapsed.TotalSeconds:F1} output={copy}");
        return written.SkippedHeadings.Count == 0 && final.All(s => s.Run.Complete) ? 0 : 1;
    }
}

/// <summary>
/// The heading-name contract, without a model: content of a source heading goes under the original heading with the same name.
/// Usage: GatherTrial headings &lt;original.docx&gt; &lt;original-outline.json&gt; &lt;source.docx&gt; &lt;source-outline.json|--style-fallback&gt; &lt;output-directory&gt;
/// </summary>
static class HeadingTrial
{
    public static int Run(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("Usage: GatherTrial headings <original.docx> <original-outline.json> <source.docx> <source-outline.json|--style-fallback> <output-directory>");
            return 2;
        }
        var original = Path.GetFullPath(args[1]);
        var originalOutline = DocumentTrial.Outline(args[2]);
        var source = Path.GetFullPath(args[3]);
        HeadingRecord[]? sourceOutline = args[4] == "--style-fallback" ? null : DocumentTrial.Outline(args[4]);
        var output = Path.GetFullPath(args[5]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        Directory.CreateDirectory(output);
        var (sections, excluded) = GatherByHeading.Build(original, originalOutline,
            [new GatherSourceFile(Path.GetFileNameWithoutExtension(source)[..3], source, sourceOutline)]);
        var copy = Path.Combine(output, Path.GetFileNameWithoutExtension(original) + "_by_heading.docx");
        // Only sections that receive something are written; a heading with no same-named source heading is reported, not marked in the document.
        var written = GatherWriteback.Apply(original, copy, sections.Where(s => s.Run.Facts.Count > 0).ToList(), DateTime.UtcNow);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "sections.json"), JsonSerializer.Serialize(new
        {
            skipped = written.SkippedHeadings,
            sections = sections.Select(s => new
            {
                s.Heading, name = GatherByHeading.Name(s.Heading), s.Note, s.TargetLocation, s.NextHeadingLocation, complete = s.Run.Complete, gaps = s.Run.Gaps,
                facts = s.Run.Facts.Select(f => new { text = f.Render(), f.Flags, sources = f.Parts.Select(p => p.SourceId + " " + p.Location) }),
            }),
            excluded = excluded.Select(e => new { e.Heading, text = e.Fact.Render(), e.Reason }),
        }, options));
        Console.WriteLine($"sections={sections.Count} matched={sections.Count(s => s.Run.Facts.Count > 0)} facts={sections.Sum(s => s.Run.Facts.Count)} excluded={excluded.Count} skipped={written.SkippedHeadings.Count} output={copy}");
        return written.SkippedHeadings.Count == 0 && sections.All(s => s.Run.Complete) ? 0 : 1;
    }
}
