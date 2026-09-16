using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Source-only eligibility proposal for V2A. It is deliberately not consumed by the production
/// authority pipeline; V2A only measures what a future role-lane prefilter would do.
/// </summary>
public sealed record DocxRoleEligibilityDecision(
    string SourceId,
    int SourceOrdinal,
    bool EligibleForRoleModel,
    string Decision,
    IReadOnlyList<string> Signals,
    string StructuralScope,
    string DomainRole,
    bool PolicyCandidate,
    string Text);

internal static class DocxRoleEligibilityPolicy
{
    private static readonly HashSet<PdfDomainRole> HardExcludedRoles =
    [
        PdfDomainRole.AmendmentAnnotation,
        PdfDomainRole.EditorialInstruction,
        PdfDomainRole.InlineClauseReference,
        PdfDomainRole.FormFieldLabel,
        PdfDomainRole.OutlineReference,
        PdfDomainRole.FigureOrBoxCaption,
        PdfDomainRole.RunningArtifact,
    ];

    internal static DocxRoleEligibilityDecision Classify(DocxAuthorityContext context)
    {
        var source = context.Source;
        var paragraph = context.Paragraph;
        var facts = context.ModelContext.Source;
        var signals = new List<string>();

        if (paragraph.InTableOfContents || facts.StructuralScope == "table_of_contents")
            return Decision("HARD_SKIP_TOC_ENTRY", false, ["source_proven_toc_entry"], context);

        if (HardExcludedRoles.Contains(facts.DomainRole))
            return Decision("HARD_SKIP_SOURCE_PROVEN_NON_OUTLINE_ROLE", false,
                [$"source_proven_domain_role:{facts.DomainRole}"], context);

        if (IsExplicitCaption(source.Text))
            return Decision("HARD_SKIP_SOURCE_PROVEN_CAPTION", false, ["source_proven_caption_shape"], context);

        if (paragraph.IsCandidate) signals.Add("existing_policy_candidate");
        if (PdfMarkerFactsParser.Parse(source.Text) is not null) signals.Add("structural_marker");
        if (paragraph.HasBuiltInHeadingStyle || source.Style.BuiltInHeadingStyleLevel is not null ||
            source.Style.OutlineLevel is >= 0 and <= 8)
            signals.Add("trusted_style_or_outline_structure");
        if (paragraph.NumberingStyleLevel is >= 1 and <= 9 ||
            source.Numbering.NumberingStyleHeadingLevel is not null)
            signals.Add("trusted_numbering_structure");
        if (facts.DomainEvidence.IsStructuralRole)
            signals.Add("positive_domain_structural_role");
        if (IsConservativeRecoveryCandidate(source, paragraph))
            signals.Add("conservative_recovery_candidate");

        return signals.Count > 0
            ? Decision("ELIGIBLE_FOR_ROLE_MODEL", true, signals, context)
            : Decision("SKIP_NO_STRUCTURAL_SIGNAL", false, ["no_structural_signal"], context);
    }

    private static DocxRoleEligibilityDecision Decision(
        string decision,
        bool eligible,
        IReadOnlyList<string> signals,
        DocxAuthorityContext context) => new(
        context.Source.SourceId,
        context.Source.SourceOrdinal,
        eligible,
        decision,
        signals,
        context.ModelContext.Source.StructuralScope,
        context.ModelContext.Source.DomainRole.ToString(),
        context.Paragraph.IsCandidate,
        context.Source.Text);

    private static bool IsConservativeRecoveryCandidate(SourceParagraph source, IPolicyParagraph paragraph)
    {
        var text = source.Text.Trim();
        if (text.Length is < 2 or > 180) return false;
        if (text.Any(char.IsControl)) return false;
        if (text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith("?", StringComparison.Ordinal) ||
            text.EndsWith("!", StringComparison.Ordinal) || text.EndsWith(";", StringComparison.Ordinal))
            return false;

        var formatSignal = source.Style.Bold || source.Style.AllCaps || paragraph.KeepNext ||
                           paragraph.PageBreakBefore || source.Style.Underline;
        if (!formatSignal) return false;

        // A recovery candidate is intentionally only a shape signal. It does not assert that the
        // paragraph is a heading and does not inspect any Gold or historical annotation.
        return text.Count(char.IsWhiteSpace) <= 24;
    }

    private static bool IsExplicitCaption(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text.Trim(),
            @"^(?:TABLE|FIGURE|FIG\.|BOX|EXHIBIT)\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public static class CanonicalDevV2ARolePrefilterShadowRunner
{
    private const string BaselineCheckpoint = "c2d01c2ec42912000fd5155174b7ae906c7472bc";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-dev-v1-v2a-role-prefilter-shadow-v2";
    private const int TargetPromptTokens = 5000;
    private const int RoleBatchCap = 32;

    private static readonly string[] CohortIds = ["DOC-0001", "DOC-0116"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var startHead = GitSha(repoRoot);
        if (!startHead.Equals(BaselineCheckpoint, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(repoRoot, startHead, "BASELINE_CHECKPOINT_MISMATCH", ct);

        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            return await BlockAsync(repoRoot, startHead, "V2A_OUTPUT_ALREADY_EXISTS", ct);
        Directory.CreateDirectory(output);

        var inventory = LoadInventory(repoRoot);
        var documents = new List<object>();
        var reports = new List<AuditDocument>();
        foreach (var documentId in CohortIds)
        {
            ct.ThrowIfCancellationRequested();
            var item = inventory.SingleOrDefault(candidate => candidate.DocumentId == documentId);
            if (item is null)
                return await BlockAsync(repoRoot, startHead, $"SOURCE_NOT_IN_INVENTORY:{documentId}", ct);

            var sourcePath = Path.GetFullPath(Path.Combine(repoRoot,
                item.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourcePath) || !Sha256File(sourcePath).Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return await BlockAsync(repoRoot, startHead, $"SOURCE_HASH_MISMATCH:{documentId}", ct);

            var report = BuildDocumentAudit(documentId, sourcePath, item.SourceSha256, repoRoot);
            reports.Add(report);
            var documentDir = Path.Combine(output, documentId);
            Directory.CreateDirectory(documentDir);
            await WriteJsonAsync(Path.Combine(documentDir, "shadow-audit.v1.json"), report, ct);
            documents.Add(report.Summary);
        }

        var summary = new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a-role-prefilter-shadow-summary-v1",
            status = "V2A_SHADOW_AUDIT_COMPLETE",
            baselineCheckpoint = BaselineCheckpoint,
            startHead,
            endHead = GitSha(repoRoot),
            documents = CohortIds,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPredictionMutation = false,
            decisionPrimaryDocument = "DOC-0116",
            documentsAudited = documents,
            gates = new
            {
                wouldSkipV7HeadingLikeCountIsZero = reports.All(item => item.WouldSkipV7HeadingLikeCount == 0),
                doc0116ProjectedRoleBatchCount = reports.Single(item => item.DocumentId == "DOC-0116").ProjectedRoleBatchCount,
                doc0116ProjectedRoleBatchGate = reports.Single(item => item.DocumentId == "DOC-0116").ProjectedRoleBatchCount <= 120,
            },
            nextPhase = "V2B_PRODUCTION_PREFILTER_NOT_STARTED",
        };
        var summaryPath = Path.Combine(output, "summary.v1.json");
        await WriteJsonAsync(summaryPath, summary, ct);

        var reportText = BuildMarkdownReport(reports);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), reportText, Encoding.UTF8, ct);

        var manifestEntries = Directory.EnumerateFiles(output, "*.json", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.v1.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new { path = Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), sha256 = Sha256File(path) })
            .ToArray();
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a-role-prefilter-shadow-manifest-v1",
            status = "V2A_SHADOW_AUDIT_FROZEN",
            baselineCheckpoint = BaselineCheckpoint,
            startHead,
            endHead = GitSha(repoRoot),
            artifacts = manifestEntries,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPredictionMutation = false,
        }, ct);

        Console.WriteLine("V2A_STATUS=V2A_SHADOW_AUDIT_COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"V2A_SUMMARY={summaryPath}");
        foreach (var item in reports)
            Console.WriteLine($"{item.DocumentId}: input={item.CurrentRoleInputCount}, eligible={item.PrefilterEligibleCount}, skipped={item.PrefilterSkippedCount}, projectedBatches={item.ProjectedRoleBatchCount}, skippedV7HeadingLike={item.WouldSkipV7HeadingLikeCount}, skippedV7Uncertain={item.WouldSkipV7UncertainCount}");
        return 0;
    }

    private static AuditDocument BuildDocumentAudit(string documentId, string sourcePath, string sourceSha256, string repoRoot)
    {
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var mode = DocumentModeClassifier.Measure(policy.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        var authoritySource = DocxAuthorityPipeline.BuildForAudit(policy, mode);
        var decisions = authoritySource.Contexts.Values
            .OrderBy(context => context.Source.SourceOrdinal)
            .Select(DocxRoleEligibilityPolicy.Classify)
            .ToArray();
        var eligibleIds = decisions.Where(item => item.EligibleForRoleModel)
            .Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var eligibleBlocks = authoritySource.Blocks
            .Where(block => eligibleIds.Contains(block.Id))
            .OrderBy(block => authoritySource.Contexts[block.Id].Source.SourceOrdinal)
            .ToArray();
        var batches = PdfBlockAnalyst.BuildTokenAwareBatches(
            eligibleBlocks,
            authoritySource.ModelContexts,
            PdfBlockAnalyst.RoleSystemPromptText,
            PdfBlockAnalyst.BuildUserPrompt,
            TargetPromptTokens,
            RoleBatchCap);
        var prompts = batches.Select(batch => PdfBlockAnalyst.BuildUserPrompt(batch, authoritySource.ModelContexts)).ToArray();
        var v7 = ReadV7Diagnostic(repoRoot, documentId);
        var skippedIds = decisions.Where(item => !item.EligibleForRoleModel).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var wouldSkipHeadingLike = v7.HeadingLikeIds.Count(skippedIds.Contains);
        var wouldSkipUncertain = v7.UncertainIds.Count(skippedIds.Contains);
        var reasons = decisions.Where(item => !item.EligibleForRoleModel)
            .GroupBy(item => item.Decision, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var policyCandidateCount = decisions.Count(item => item.PolicyCandidate);
        var markerRecoveryCount = decisions.Count(item => item.EligibleForRoleModel &&
            !item.PolicyCandidate && item.Signals.Contains("structural_marker"));
        var styleRecoveryCount = decisions.Count(item => item.EligibleForRoleModel &&
            !item.PolicyCandidate && item.Signals.Contains("trusted_style_or_outline_structure"));
        var domainRecoveryCount = decisions.Count(item => item.EligibleForRoleModel &&
            !item.PolicyCandidate && item.Signals.Contains("positive_domain_structural_role"));
        var otherRecoveryCount = decisions.Count(item => item.EligibleForRoleModel && !item.PolicyCandidate &&
            !item.Signals.Contains("structural_marker") && !item.Signals.Contains("trusted_style_or_outline_structure") &&
            !item.Signals.Contains("positive_domain_structural_role"));

        return new AuditDocument(
            documentId,
            sourceSha256,
            source.Paragraphs.Count,
            authoritySource.Blocks.Count,
            decisions.Count(item => item.EligibleForRoleModel),
            decisions.Count(item => !item.EligibleForRoleModel),
            reasons,
            policyCandidateCount,
            markerRecoveryCount,
            styleRecoveryCount,
            domainRecoveryCount,
            otherRecoveryCount,
            batches.Count,
            prompts.Sum(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
            batches.Count == 0 ? 0 : batches.Max(batch => batch.Count),
            prompts.Length == 0 ? 0 : prompts.Max(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
            wouldSkipHeadingLike,
            wouldSkipUncertain,
            mode.Mode.ToString(),
            v7.RoleBatchCount,
            v7.RoleProviderCalls,
            v7.RoleMissingIdRetryCalls,
            v7.TotalResponses,
            decisions,
            new
            {
                documentId,
                sourceParagraphCount = source.Paragraphs.Count,
                currentRoleInputCount = authoritySource.Blocks.Count,
                prefilterEligibleCount = decisions.Count(item => item.EligibleForRoleModel),
                prefilterSkippedCount = decisions.Count(item => !item.EligibleForRoleModel),
                projectedRoleBatchCount = batches.Count,
                wouldSkipV7HeadingLikeCount = wouldSkipHeadingLike,
                wouldSkipV7UncertainCount = wouldSkipUncertain,
                v7RoleBatchCount = v7.RoleBatchCount,
                v7RoleProviderCalls = v7.RoleProviderCalls,
                v7RoleMissingIdRetryCalls = v7.RoleMissingIdRetryCalls,
                v7TotalResponses = v7.TotalResponses,
            });
    }

    private static V7Diagnostic ReadV7Diagnostic(string repoRoot, string documentId)
    {
        var path = Path.Combine(repoRoot, "artifacts", "level-accuracy", "canonical-dev-v1-exec-v7-optimized", documentId, "prediction.v1.json");
        if (!File.Exists(path)) return V7Diagnostic.Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var headings = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("occurrencePredictions", out var predictions) && predictions.ValueKind == JsonValueKind.Array)
            foreach (var item in predictions.EnumerateArray())
                if (item.TryGetProperty("occurrenceId", out var id) && id.ValueKind == JsonValueKind.String)
                    headings.Add(id.GetString()!);

        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("sourceTraces", out var traces) && traces.ValueKind == JsonValueKind.Array)
            foreach (var item in traces.EnumerateArray())
            {
                var role = item.TryGetProperty("modelRole", out var roleProperty) ? roleProperty.GetString() : null;
                if (role is "Unknown" or "Uncertain" && item.TryGetProperty("sourceId", out var id) && id.ValueKind == JsonValueKind.String)
                    uncertain.Add(id.GetString()!);
            }
        var telemetry = root.TryGetProperty("batchTelemetry", out var telemetryElement) &&
                        telemetryElement.ValueKind == JsonValueKind.Object
            ? telemetryElement
            : default;
        var roleBatchCount = ReadTelemetryInt(telemetry, "roleBatchCount");
        var roleProviderCalls = ReadTelemetryInt(telemetry, "roleProviderCalls");
        var totalResponses = ReadTelemetryInt(telemetry, "totalResponses");
        return new V7Diagnostic(headings, uncertain, roleBatchCount, roleProviderCalls,
            Math.Max(0, roleProviderCalls - roleBatchCount), totalResponses);
    }

    private static string BuildMarkdownReport(IReadOnlyList<AuditDocument> reports)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# V2A Conservative Role Prefilter Shadow Audit");
        builder.AppendLine();
        builder.AppendLine("Source-only shadow projection from the V7-frozen context universe. Production blocks were not changed; no provider, Gold, or scoring path was invoked.");
        builder.AppendLine();
        builder.AppendLine("| Document | Source paragraphs | Current role input | Eligible | Skipped | Projected batches | Projected tokens | Largest blocks | Largest tokens | V7 heading-like skipped | V7 uncertain skipped |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in reports)
            builder.AppendLine($"| {item.DocumentId} | {item.SourceParagraphCount} | {item.CurrentRoleInputCount} | {item.PrefilterEligibleCount} | {item.PrefilterSkippedCount} | {item.ProjectedRoleBatchCount} | {item.ProjectedRoleInputTokens} | {item.ProjectedLargestBatchBlocks} | {item.ProjectedLargestBatchTokens} | {item.WouldSkipV7HeadingLikeCount} | {item.WouldSkipV7UncertainCount} |");
        builder.AppendLine();
        builder.AppendLine("V2A status: SHADOW_ONLY. V2B production prefilter was not started.");
        builder.AppendLine("Provider calls: 0. Gold reads: 0. Scoring: false.");
        return builder.ToString();
    }

    private static SourceEntry[] LoadInventory(string repoRoot)
    {
        var path = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new SourceEntry(item.GetProperty("documentId").GetString()!, item.GetProperty("sourcePath").GetString() ?? "", item.GetProperty("sourceSha256").GetString() ?? ""))
            .ToArray();
    }

    private static async Task<int> BlockAsync(string repoRoot, string head, string reason, CancellationToken ct)
    {
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "blocked.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a-role-prefilter-shadow-blocked-v1",
            status = "V2A_SHADOW_AUDIT_BLOCKED",
            baselineCheckpoint = BaselineCheckpoint,
            currentHead = head,
            reason,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
        }, ct);
        Console.Error.WriteLine($"V2A_STATUS=BLOCKED:{reason}");
        return 2;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private static string GitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN";
    }

    private static int ReadTelemetryInt(JsonElement telemetry, string property) =>
        telemetry.ValueKind == JsonValueKind.Object &&
        telemetry.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record SourceEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record V7Diagnostic(
        IReadOnlySet<string> HeadingLikeIds,
        IReadOnlySet<string> UncertainIds,
        int RoleBatchCount,
        int RoleProviderCalls,
        int RoleMissingIdRetryCalls,
        int TotalResponses)
    {
        public static readonly V7Diagnostic Empty = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 0, 0, 0, 0);
    }

    private sealed record AuditDocument(
        string DocumentId,
        string SourceSha256,
        int SourceParagraphCount,
        int CurrentRoleInputCount,
        int PrefilterEligibleCount,
        int PrefilterSkippedCount,
        IReadOnlyDictionary<string, int> SkipReasonCounts,
        int PolicyCandidateCount,
        int MarkerRecoveryCount,
        int StyleRecoveryCount,
        int DomainStructuralRecoveryCount,
        int OtherRecoveryCount,
        int ProjectedRoleBatchCount,
        int ProjectedRoleInputTokens,
        int ProjectedLargestBatchBlocks,
        int ProjectedLargestBatchTokens,
        int WouldSkipV7HeadingLikeCount,
        int WouldSkipV7UncertainCount,
        string DocumentMode,
        int V7RoleBatchCount,
        int V7RoleProviderCalls,
        int V7RoleMissingIdRetryCalls,
        int V7TotalResponses,
        IReadOnlyList<DocxRoleEligibilityDecision> Decisions,
        object Summary);
}
