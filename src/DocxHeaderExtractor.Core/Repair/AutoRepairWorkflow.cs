using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record AutoRepairOptions(
    string OutputDirectory,
    bool AlwaysWriteCase = false,
    bool IncludeOutlineJson = true);

public sealed record AutoRepairRunResult(
    string File,
    string CaseDirectory,
    bool NeedsAnalysis,
    bool PatchCandidateNeeded,
    string Status,
    IReadOnlyList<string> WrittenFiles);

public sealed record DocumentFailureCase(
    string FormatVersion,
    string CaseId,
    string File,
    string SourcePath,
    DateTimeOffset CreatedAt,
    string Status,
    string Reason,
    string? DeterministicRoute,
    int ParagraphCount,
    int HeadingCount,
    int RequiresReviewCount,
    int DisputedCount,
    DocumentModeReport? DocumentMode,
    DocumentDiagnosticReport? Diagnostics,
    IReadOnlyList<HeadingSnapshot> HeadingsSample,
    IReadOnlyList<ParagraphSnapshot> EvidenceParagraphs,
    RuntimeSelfFixPolicy RuntimePolicy);

public sealed record HeadingSnapshot(
    int Index,
    int Level,
    string Text,
    string Source,
    string DecisionStatus,
    string ConfidenceBasis,
    bool Disputed);

public sealed record ParagraphSnapshot(
    int Index,
    string StableId,
    string Text,
    bool Requested);

public sealed record RuntimeSelfFixPolicy(
    bool AllowInProcessAssemblyMutation,
    string Strategy,
    IReadOnlyList<string> RequiredGates,
    IReadOnlyList<string> ForbiddenInputs,
    IReadOnlyList<string> ProductionSwapSteps);

public sealed record RepairLearningRecord(
    string FormatVersion,
    string CaseId,
    DateTimeOffset CreatedAt,
    string Symptom,
    string RootCauseHypothesis,
    string NextCodeAction,
    string ValidationPlan,
    string RuntimeSelfFixStrategy);

/// <summary>
/// Code-first repair workflow: run the normal extractor, persist deterministic diagnostics, prepare
/// an LLM analyst prompt, and write a learning record. It does not mutate production code.
/// </summary>
public sealed class AutoRepairWorkflow
{
    public const string FormatVersion = "dhx-auto-repair/v1";

    private readonly PipelineOptions _pipelineOptions;

    public AutoRepairWorkflow(PipelineOptions pipelineOptions)
    {
        _pipelineOptions = pipelineOptions;
    }

    public async Task<AutoRepairRunResult> RunAsync(
        string inputPath,
        AutoRepairOptions options,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        using var pipeline = new HeaderExtractionPipeline(_pipelineOptions);
        var outline = await pipeline.RunAsync(inputPath, ct);

        var needsAnalysis = NeedsAnalysis(outline);
        if (!needsAnalysis && !options.AlwaysWriteCase)
            return new AutoRepairRunResult(
                outline.File,
                "",
                NeedsAnalysis: false,
                PatchCandidateNeeded: false,
                "normal_no_artifacts",
                []);

        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        SlimDocument slim;
        try
        {
            slim = new DocxSlimExtractor(_pipelineOptions.Extraction).Extract(conversion.Path);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }

        var caseId = BuildCaseId(inputPath, outline);
        var caseDir = Path.Combine(options.OutputDirectory, caseId);
        Directory.CreateDirectory(caseDir);

        var failureCase = BuildFailureCase(caseId, inputPath, outline, slim, needsAnalysis);
        var candidateReport = RepairCandidateRunner.Analyze(outline);
        var validationReport = RepairValidationGate.Validate(outline, candidateReport);
        var learning = BuildLearningRecord(failureCase);

        var written = new List<string>();
        await WriteJsonAsync(Path.Combine(caseDir, "failure-case.json"), failureCase, ct, written);
        await WriteJsonAsync(Path.Combine(caseDir, "probe-report.json"), outline.Diagnostics, ct, written);
        await WriteJsonAsync(Path.Combine(caseDir, "candidate-report.json"), candidateReport, ct, written);
        await WriteJsonAsync(Path.Combine(caseDir, "validation-report.json"), validationReport, ct, written);
        if (options.IncludeOutlineJson)
            await WriteTextAsync(Path.Combine(caseDir, "current-outline.json"),
                OutlineFormatter.Format(outline, OutlineFormat.Json), ct, written);
        await WriteTextAsync(Path.Combine(caseDir, "llm-analysis-prompt.md"),
            BuildLlmAnalystPrompt(failureCase), ct, written);
        await WriteTextAsync(Path.Combine(caseDir, "runtime-self-fix-plan.md"),
            BuildRuntimeSelfFixPlan(failureCase.RuntimePolicy), ct, written);

        var learningLog = Path.Combine(options.OutputDirectory, "repair-learning-log.jsonl");
        await File.AppendAllTextAsync(
            learningLog,
            JsonSerializer.Serialize(learning, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false),
            ct);
        written.Add(learningLog);

        return new AutoRepairRunResult(
            outline.File,
            caseDir,
            needsAnalysis,
            PatchCandidateNeeded: needsAnalysis || candidateReport.PatchCandidateNeeded || !validationReport.Passed,
            needsAnalysis || !validationReport.Passed ? "artifacts_written_needs_analysis" : "artifacts_written_normal",
            written);
    }

    private static bool NeedsAnalysis(DocumentOutline outline) =>
        outline.Diagnostics?.Status != "normal" ||
        outline.Headings.Any(h =>
            h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);

    private static DocumentFailureCase BuildFailureCase(
        string caseId,
        string inputPath,
        DocumentOutline outline,
        SlimDocument slim,
        bool needsAnalysis)
    {
        var reviewIndexes = outline.Headings
            .Where(h => h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed)
            .Select(h => h.Index)
            .ToHashSet();

        var evidenceIndexes = new HashSet<int>(reviewIndexes);
        foreach (var h in outline.Headings.Take(20)) evidenceIndexes.Add(h.Index);
        foreach (var candidate in slim.Candidates.Take(20)) evidenceIndexes.Add(candidate.Index);

        var paragraphs = slim.Paragraphs
            .Where(p => evidenceIndexes.Contains(p.Index))
            .OrderBy(p => p.Index)
            .Take(80)
            .Select(p => new ParagraphSnapshot(
                p.Index,
                p.StableId,
                Truncate(p.Text, 800),
                reviewIndexes.Contains(p.Index)))
            .ToList();

        var headings = outline.Headings
            .Take(120)
            .Select(h => new HeadingSnapshot(
                h.Index,
                h.Level,
                h.Text,
                h.Source.ToString(),
                h.DecisionStatus.ToString(),
                h.ConfidenceBasis,
                h.Disputed))
            .ToList();

        return new DocumentFailureCase(
            FormatVersion,
            caseId,
            outline.File,
            Path.GetFullPath(inputPath),
            DateTimeOffset.UtcNow,
            needsAnalysis ? "needs_analysis" : "normal",
            outline.Diagnostics?.Reason ?? "no_diagnostic_report",
            outline.DeterministicRoute,
            outline.ParagraphCount,
            outline.Headings.Count,
            outline.Headings.Count(h => h.DecisionStatus == HeadingDecisionStatus.RequiresReview),
            outline.DisputedCount,
            outline.DocumentMode,
            outline.Diagnostics,
            headings,
            paragraphs,
            DefaultRuntimePolicy());
    }

    private static RepairLearningRecord BuildLearningRecord(DocumentFailureCase failureCase)
    {
        var best = failureCase.Diagnostics?.Candidates
            .OrderByDescending(c => c.Accepted)
            .ThenByDescending(c => c.BodyAnchorRatio ?? 0)
            .ThenByDescending(c => c.HeadingCount)
            .FirstOrDefault();

        return new RepairLearningRecord(
            FormatVersion,
            failureCase.CaseId,
            failureCase.CreatedAt,
            $"{failureCase.Status}: {failureCase.Reason}",
            best is null
                ? "No deterministic candidate report was available."
                : $"Best current candidate is {best.Route} ({best.Reason}), headings={best.HeadingCount}.",
            "LLM analyst should propose a small deterministic rule or a stricter rejection filter, then agent must implement it in sandbox.",
            "Run the failing file, sibling documents of the same layout, deterministic audit corpus, and full dotnet test.",
            "Sidecar patch branch -> build shadow artifact -> validation gate -> supervised blue/green swap.");
    }

    private static RuntimeSelfFixPolicy DefaultRuntimePolicy() => new(
        AllowInProcessAssemblyMutation: false,
        Strategy: "sidecar_patch_branch_shadow_build_blue_green_swap",
        RequiredGates:
        [
            "patch touches only extractor/probe/test files relevant to the case",
            "no hard-coded filename, expected heading count, or answer key dependency",
            "heading spans must validate against source text",
            "failing document passes",
            "same-layout sibling documents pass",
            "deterministic audit corpus has no blank route regression",
            "dotnet test passes"
        ],
        ForbiddenInputs:
        [
            "old answer keys as truth",
            "LLM visual similarity as final judgment",
            "runtime overwrite of loaded production assemblies",
            "patches that only special-case file names"
        ],
        ProductionSwapSteps:
        [
            "create isolated worktree or branch",
            "apply generated patch",
            "build and test shadow artifact",
            "run canary extraction against failure case and regression corpus",
            "publish new version beside current version",
            "switch traffic/process to the validated version",
            "keep previous version for rollback"
        ]);

    private static string BuildLlmAnalystPrompt(DocumentFailureCase failureCase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LLM Analyst Task");
        sb.AppendLine();
        sb.AppendLine("You are an analyst, not the final judge. Use only the evidence in this folder.");
        sb.AppendLine("Do not choose a heading because it looks right. Explain the route/rule by deterministic signals.");
        sb.AppendLine();
        sb.AppendLine("## Required Output");
        sb.AppendLine("- Document layout diagnosis");
        sb.AppendLine("- Current output defect: missed heading, false heading, wrong boundary, wrong level, or no defect");
        sb.AppendLine("- Route-specific metrics from `candidate-report.json`; do not treat `Score` / `BestScore` as truth unless calibration says trusted");
        sb.AppendLine("- Failed validation gates from `validation-report.json`");
        sb.AppendLine("- Proposed generic rule or rejection filter");
        sb.AppendLine("- Files likely to change");
        sb.AppendLine("- Tests/invariants required");
        sb.AppendLine();
        sb.AppendLine("## Case Summary");
        sb.AppendLine($"- caseId: `{failureCase.CaseId}`");
        sb.AppendLine($"- file: `{failureCase.File}`");
        sb.AppendLine($"- status: `{failureCase.Status}`");
        sb.AppendLine($"- reason: `{failureCase.Reason}`");
        sb.AppendLine($"- currentRoute: `{failureCase.DeterministicRoute ?? "(none)"}`");
        sb.AppendLine($"- headings: {failureCase.HeadingCount}");
        sb.AppendLine($"- requiresReview: {failureCase.RequiresReviewCount}");
        sb.AppendLine($"- disputed: {failureCase.DisputedCount}");
        sb.AppendLine();
        sb.AppendLine("Read `failure-case.json`, `probe-report.json`, `candidate-report.json`, `validation-report.json`, and `current-outline.json` before proposing code.");
        return sb.ToString();
    }

    private static string BuildRuntimeSelfFixPlan(RuntimeSelfFixPolicy policy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Runtime Production Self-Fix Policy");
        sb.AppendLine();
        sb.AppendLine("Production runtime must not rewrite the assembly that is currently loaded.");
        sb.AppendLine("Self-fix means a supervised sidecar creates and validates a new version, then swaps runtime to that version.");
        sb.AppendLine();
        sb.AppendLine($"Strategy: `{policy.Strategy}`");
        sb.AppendLine($"Allow in-process assembly mutation: `{policy.AllowInProcessAssemblyMutation}`");
        sb.AppendLine();
        sb.AppendLine("## Required Gates");
        foreach (var gate in policy.RequiredGates) sb.AppendLine($"- {gate}");
        sb.AppendLine();
        sb.AppendLine("## Forbidden Inputs");
        foreach (var input in policy.ForbiddenInputs) sb.AppendLine($"- {input}");
        sb.AppendLine();
        sb.AppendLine("## Production Swap Steps");
        foreach (var step in policy.ProductionSwapSteps) sb.AppendLine($"- {step}");
        return sb.ToString();
    }

    private static string BuildCaseId(string inputPath, DocumentOutline outline)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var reason = outline.Diagnostics?.Reason ?? "normal";
        return Sanitize($"{stem}_{reason}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = text.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        return text[..max] + "...";
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken ct,
        ICollection<string> written)
    {
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false),
            ct);
        written.Add(path);
    }

    private static async Task WriteTextAsync(
        string path,
        string text,
        CancellationToken ct,
        ICollection<string> written)
    {
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
        written.Add(path);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
