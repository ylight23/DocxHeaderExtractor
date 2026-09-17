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
/// Offline V2A.2 shadow only. The recovery classes are source-derived probes for a future
/// production prefilter; they do not alter the production role input universe.
/// </summary>
public static class CanonicalDevV2A2RolePrefilterRecoveryShadowRunner
{
    private const string BaselineCheckpoint = "b66e937df1e856fbe6bab5a325c4ca8e06bf2863";
    private const string V7BaselineCheckpoint = "c2d01c2ec42912000fd5155174b7ae906c7472bc";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string V7Path = "artifacts/level-accuracy/canonical-dev-v1-exec-v7-optimized/DOC-0116/prediction.v1.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-dev-v1-v2a2-role-prefilter-recovery-shadow-v2";
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
            return await BlockAsync(repoRoot, startHead, "V2A2_OUTPUT_ALREADY_EXISTS", ct);
        Directory.CreateDirectory(output);

        var inventory = LoadInventory(repoRoot);
        var reports = new List<DocumentAudit>();
        foreach (var documentId in CohortIds)
        {
            ct.ThrowIfCancellationRequested();
            var item = inventory.Single(candidate => candidate.DocumentId == documentId);
            var sourcePath = Path.GetFullPath(Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourcePath) || !Sha256File(sourcePath).Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return await BlockAsync(repoRoot, startHead, $"SOURCE_HASH_MISMATCH:{documentId}", ct);

            var report = BuildDocumentAudit(documentId, sourcePath, item.SourceSha256, repoRoot);
            reports.Add(report);
            var documentDir = Path.Combine(output, documentId);
            Directory.CreateDirectory(documentDir);
            await WriteJsonAsync(Path.Combine(documentDir, "shadow-audit.v1.json"), report, ct);
        }

        var primary = reports.Single(item => item.DocumentId == "DOC-0116");
        var summary = new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a2-role-prefilter-recovery-shadow-summary-v1",
            status = "V2A2_GENERIC_RECOVERY_SHADOW_COMPLETE",
            baselineCheckpoint = BaselineCheckpoint,
            v7BaselineCheckpoint = V7BaselineCheckpoint,
            documents = CohortIds,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPrefilterActivated = false,
            devTunedForV7BehaviorPreservation = true,
            documentsAudited = reports.Select(item => item.Summary).ToArray(),
            decisionPrimaryDocument = "DOC-0116",
            gates = new
            {
                projectedRoleBatchCount = primary.ProjectedRoleBatchCount,
                projectedRoleBatchGate = primary.ProjectedRoleBatchCount <= 120,
                sourcePlausibleRiskResidualCount = reports.Sum(item => item.SourcePlausibleRiskResidualCount),
                allSourcePlausibleRiskClassesVisible = reports.All(item => item.SourcePlausibleRiskResidualCount == 0),
                v2bStatus = "BLOCKED_UNTIL_PRODUCTION_PREFILTER_REVIEW",
            },
            nextPhase = "V2B_PRODUCTION_PREFILTER_NOT_STARTED",
        };
        var summaryPath = Path.Combine(output, "summary.v1.json");
        await WriteJsonAsync(summaryPath, summary, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildMarkdownReport(reports), Encoding.UTF8, ct);

        var manifestEntries = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.v1.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new { path = Relative(repoRoot, path), sha256 = Sha256File(path) })
            .ToArray();
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a2-role-prefilter-recovery-shadow-manifest-v1",
            status = "V2A2_GENERIC_RECOVERY_SHADOW_FROZEN",
            baselineCheckpoint = BaselineCheckpoint,
            v7BaselineCheckpoint = V7BaselineCheckpoint,
            artifacts = manifestEntries,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPrefilterActivated = false,
            devTunedForV7BehaviorPreservation = true,
        }, ct);

        Console.WriteLine("V2A2_STATUS=V2A2_GENERIC_RECOVERY_SHADOW_COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        Console.WriteLine($"V2A2_SUMMARY={summaryPath}");
        foreach (var item in reports)
            Console.WriteLine($"{item.DocumentId}: before={item.EligibleBeforeRecovery}, after={item.EligibleAfterRecovery}, batches={item.ProjectedRoleBatchCount}, v7Recovered={item.V7HeadingLikeRecoveredByRule.Values.Sum()}");
        return 0;
    }

    private static DocumentAudit BuildDocumentAudit(string documentId, string sourcePath, string sourceSha256, string repoRoot)
    {
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var mode = DocumentModeClassifier.Measure(policy.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        var authority = DocxAuthorityPipeline.BuildForAudit(policy, mode);
        var ordered = authority.Contexts.Values.OrderBy(context => context.Source.SourceOrdinal).ToArray();
        var baseDecisions = ordered.Select(DocxRoleEligibilityPolicy.Classify).ToArray();
        var v7 = ReadV7Diagnostic(repoRoot, documentId);
        var recoveryRows = new List<RecoveryRow>();
        var recoveredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var context in ordered)
        {
            var baseDecision = baseDecisions.Single(item => item.SourceId == context.Source.SourceId);
            if (baseDecision.EligibleForRoleModel) continue;
            var rule = RecoveryRule(context, baseDecision);
            if (rule is null) continue;
            recoveredIds.Add(context.Source.SourceId);
            recoveryRows.Add(new RecoveryRow(
                context.Source.SourceId,
                context.Source.SourceOrdinal,
                context.Source.Text,
                rule,
                baseDecision.Decision,
                baseDecision.Signals,
                EvidenceSignature(context, baseDecision),
                v7.HeadingLikeIds.Contains(context.Source.SourceId)));
        }

        var eligibleIds = baseDecisions.Where(item => item.EligibleForRoleModel).Select(item => item.SourceId)
            .Concat(recoveredIds).ToHashSet(StringComparer.Ordinal);
        var eligibleBlocks = authority.Blocks
            .Where(block => eligibleIds.Contains(block.Id))
            .OrderBy(block => authority.Contexts[block.Id].Source.SourceOrdinal)
            .ToArray();
        var batches = PdfBlockAnalyst.BuildTokenAwareBatches(
            eligibleBlocks,
            authority.ModelContexts,
            PdfBlockAnalyst.RoleSystemPromptText,
            PdfBlockAnalyst.BuildUserPrompt,
            TargetPromptTokens,
            RoleBatchCap);
        var prompts = batches.Select(batch => PdfBlockAnalyst.BuildUserPrompt(batch, authority.ModelContexts)).ToArray();
        var residual = baseDecisions.Where(item => !item.EligibleForRoleModel && !recoveredIds.Contains(item.SourceId))
            .Select(item => new ResidualRow(item.SourceId, item.SourceOrdinal, item.Text, item.Decision, EvidenceSignature(authority.Contexts[item.SourceId], item), v7.HeadingLikeIds.Contains(item.SourceId)))
            .OrderBy(item => item.SourceOrdinal)
            .ToArray();
        var v7Recovered = recoveryRows.GroupBy(item => item.Rule, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(item => item.V7HeadingLike), StringComparer.Ordinal);
        foreach (var rule in new[] { "R1_INLINE_PREFIX_BODY", "R2_STRUCTURAL_NUMBERING_PLUS_FORMAT", "R3_STANDALONE_TEMPLATE_LABEL" })
            v7Recovered.TryAdd(rule, 0);
        var sourcePlausibleResidual = residual.Count(item => item.V7HeadingLike &&
            item.SkipReason == "SKIP_NO_STRUCTURAL_SIGNAL" &&
            IsSourcePlausibleRisk(authority.Contexts[item.SourceId], baseDecisions.Single(decision => decision.SourceId == item.SourceId)));
        return new DocumentAudit(
            documentId,
            sourceSha256,
            source.Paragraphs.Count,
            authority.Blocks.Count,
            baseDecisions.Count(item => item.EligibleForRoleModel),
            recoveredIds.Count,
            baseDecisions.Count(item => item.EligibleForRoleModel) + recoveredIds.Count,
            baseDecisions.Count(item => !item.EligibleForRoleModel),
            batches.Count,
            prompts.Sum(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
            batches.Count == 0 ? 0 : batches.Max(batch => batch.Count),
            prompts.Length == 0 ? 0 : prompts.Max(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
            recoveryRows.GroupBy(item => item.Rule).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            v7Recovered,
            residual,
            recoveryRows,
            v7.UncertainIds.Count(id => baseDecisions.Any(item => !item.EligibleForRoleModel && item.SourceId == id)),
            sourcePlausibleResidual,
            new
            {
                documentId,
                sourceParagraphCount = source.Paragraphs.Count,
                currentRoleInputCount = authority.Blocks.Count,
                eligibleBeforeRecovery = baseDecisions.Count(item => item.EligibleForRoleModel),
                eligibleAfterRecovery = baseDecisions.Count(item => item.EligibleForRoleModel) + recoveredIds.Count,
                projectedRoleBatchCount = batches.Count,
                projectedRoleInputTokens = prompts.Sum(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
                projectedLargestBatchBlocks = batches.Count == 0 ? 0 : batches.Max(batch => batch.Count),
                projectedLargestBatchTokens = prompts.Length == 0 ? 0 : prompts.Max(prompt => PdfBlockAnalyst.EstimatePromptTokens(PdfBlockAnalyst.RoleSystemPromptText + "\n" + prompt)),
                v7HeadingLikeRecoveredByRule = v7Recovered,
                v7HeadingLikeResidualSkipped = residual.Count(item => item.V7HeadingLike),
                wouldSkipV7UncertainCount = v7.UncertainIds.Count(id => baseDecisions.Any(item => !item.EligibleForRoleModel && !recoveredIds.Contains(item.SourceId) && item.SourceId == id)),
            });
    }

    private static string? RecoveryRule(DocxAuthorityContext context, DocxRoleEligibilityDecision decision)
    {
        // Source-proven exclusions remain exclusions. Recovery is only a shadow expansion of the
        // generic no-signal bucket; it cannot override TOC, caption, form, reference, or other
        // deterministic non-outline decisions.
        if (!string.Equals(decision.Decision, "SKIP_NO_STRUCTURAL_SIGNAL", StringComparison.Ordinal)) return null;
        if (InlinePrefixBody(context.Source.Text)) return "R1_INLINE_PREFIX_BODY";
        if (StructuralNumberingPlusFormat(context)) return "R2_STRUCTURAL_NUMBERING_PLUS_FORMAT";
        if (StandaloneTemplateLabel(context)) return "R3_STANDALONE_TEMPLATE_LABEL";
        return null;
    }

    private static bool InlinePrefixBody(string text)
    {
        var index = text.IndexOf(':');
        if (index <= 1 || index >= text.Length - 2) return false;
        var prefix = text[..index].Trim();
        var suffix = text[(index + 1)..].Trim();
        var prefixWordCount = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return prefix.Length is <= 80 and >= 2 && prefixWordCount is >= 2 and <= 6 && suffix.Length >= 2 &&
               prefix.Any(char.IsLetterOrDigit) && suffix.Any(char.IsLetterOrDigit) &&
               !prefix.Contains(',') && !prefix.Contains(';') && !prefix.Contains('.') &&
               !prefix.Contains('\n') && !prefix.Contains('\r');
    }

    private static bool StructuralNumberingPlusFormat(DocxAuthorityContext context)
    {
        var source = context.Source;
        var paragraph = context.Paragraph;
        var number = source.Numbering;
        var hasNonBulletNumbering = (number.NumberingId is not null || !string.IsNullOrWhiteSpace(number.NumberLabel)) &&
            !string.Equals(number.NumberingFormat, "bullet", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(number.NumberingFormat, "symbol", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(number.NumberingFormat, "picture", StringComparison.OrdinalIgnoreCase);
        var emphasis = source.Style.Bold || source.Style.AllCaps || source.Style.Underline ||
                       source.Style.OutlineLevel is >= 0 and <= 8 || paragraph.KeepNext || paragraph.PageBreakBefore ||
                       paragraph.HasBuiltInHeadingStyle || paragraph.NumberingStyleLevel is not null;
        return hasNonBulletNumbering && emphasis;
    }

    private static bool StandaloneTemplateLabel(DocxAuthorityContext context)
    {
        var text = context.Source.Text;
        var trimmed = text.Trim();
        if (trimmed.Length is < 4 or > 180) return false;
        var braces = trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal);
        var brackets = trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
        if (!braces && !brackets) return false;
        var inner = trimmed[1..^1].Trim();
        var words = inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return inner.Length >= 2 && inner.Length <= 80 && words.Length <= 8 && inner.Any(char.IsLetterOrDigit) &&
               !inner.EndsWith(".", StringComparison.Ordinal) &&
               !inner.Contains(';') && !inner.Contains(':') &&
               context.Source.Layout.TableDepth == 0 &&
               (braces || context.Paragraph.Score > 0 || context.Source.Style.Bold || context.Source.Style.AllCaps || context.Source.Style.Underline);
    }

    private static bool IsSourcePlausibleRisk(DocxAuthorityContext context, DocxRoleEligibilityDecision decision) =>
        string.Equals(decision.Decision, "SKIP_NO_STRUCTURAL_SIGNAL", StringComparison.Ordinal) &&
        (InlinePrefixBody(context.Source.Text) || StructuralNumberingPlusFormat(context) || StandaloneTemplateLabel(context));

    private static string EvidenceSignature(DocxAuthorityContext context, DocxRoleEligibilityDecision decision)
    {
        var source = context.Source;
        var paragraph = context.Paragraph;
        var facts = context.ModelContext.Source;
        var terminal = source.Text.TrimEnd() switch
        {
            var text when text.EndsWith(".", StringComparison.Ordinal) => "period",
            var text when text.EndsWith(":", StringComparison.Ordinal) => "colon",
            _ => "other",
        };
        var length = source.Text.Trim().Length switch { <= 40 => "short", <= 120 => "medium", _ => "long" };
        return string.Join("|", decision.Decision, paragraph.Role, paragraph.IsCandidate ? "candidate" : "not-candidate",
            facts.StructuralScope, facts.DomainRole, source.Layout.TableDepth > 0 ? "table" : "not-table",
            source.Style.Bold || source.Style.AllCaps || source.Style.Underline ? "format-signal" : "no-format-signal",
            source.Numbering.NumberingId is not null || source.Numbering.NumberLabel is not null ? "numbering-signal" : "no-numbering-signal", terminal, length);
    }

    private static V7Diagnostic ReadV7Diagnostic(string repoRoot, string documentId)
    {
        var path = Path.Combine(repoRoot, V7Path.Replace('/', Path.DirectorySeparatorChar).Replace("DOC-0116", documentId, StringComparison.Ordinal));
        if (!File.Exists(path)) return V7Diagnostic.Empty;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var headings = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("occurrencePredictions", out var predictions) && predictions.ValueKind == JsonValueKind.Array)
            foreach (var item in predictions.EnumerateArray())
                if (item.TryGetProperty("occurrenceId", out var id) && id.ValueKind == JsonValueKind.String) headings.Add(id.GetString()!);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("sourceTraces", out var traces) && traces.ValueKind == JsonValueKind.Array)
            foreach (var item in traces.EnumerateArray())
                if (item.TryGetProperty("modelRole", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() is "Unknown" or "Uncertain" &&
                    item.TryGetProperty("sourceId", out var id) && id.ValueKind == JsonValueKind.String) uncertain.Add(id.GetString()!);
        return new V7Diagnostic(headings, uncertain);
    }

    private static string BuildMarkdownReport(IReadOnlyList<DocumentAudit> reports)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# V2A.2 Generic Recovery Shadow");
        builder.AppendLine();
        builder.AppendLine("Source-only shadow simulation. Production prefilter, provider, Gold, and scoring paths were not changed or invoked.");
        builder.AppendLine();
        builder.AppendLine("| Document | Eligible before | Eligible after | Projected batches | Projected tokens | Largest blocks | Largest tokens | V7 heading-like recovered | Residual V7 heading-like |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in reports)
            builder.AppendLine($"| {item.DocumentId} | {item.EligibleBeforeRecovery} | {item.EligibleAfterRecovery} | {item.ProjectedRoleBatchCount} | {item.ProjectedRoleInputTokens} | {item.ProjectedLargestBatchBlocks} | {item.ProjectedLargestBatchTokens} | {item.V7HeadingLikeRecoveredByRule.Values.Sum()} | {item.ResidualSkipped.Count(row => row.V7HeadingLike)} |");
        builder.AppendLine();
        builder.AppendLine("Recovery classes are generic source-derived simulations: R1 inline prefix/body, R2 structural numbering plus format, R3 standalone template label.");
        builder.AppendLine("V7 heading-like coverage is diagnostic only and is not treated as Gold.");
        builder.AppendLine("V2B production prefilter activation: NOT STARTED.");
        return builder.ToString();
    }

    private static SourceEntry[] LoadInventory(string repoRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
        return document.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new SourceEntry(ReadString(item, "documentId")!, ReadString(item, "sourcePath") ?? "", ReadString(item, "sourceSha256") ?? ""))
            .ToArray();
    }

    private static async Task<int> BlockAsync(string repoRoot, string head, string reason, CancellationToken ct)
    {
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "blocked.v1.json"), new { status = "V2A2_GENERIC_RECOVERY_SHADOW_BLOCKED", baselineCheckpoint = BaselineCheckpoint, currentHead = head, reason, providerCalls = 0, goldReads = 0, scoring = false }, ct);
        Console.Error.WriteLine($"V2A2_STATUS=BLOCKED:{reason}");
        return 2;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private static string Relative(string repoRoot, string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
    private static string ReadString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string GitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN";
    }
    private static string Sha256File(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    private sealed record SourceEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record V7Diagnostic(IReadOnlySet<string> HeadingLikeIds, IReadOnlySet<string> UncertainIds)
    { public static readonly V7Diagnostic Empty = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)); }
    private sealed record RecoveryRow(string SourceId, int SourceOrdinal, string SourceText, string Rule, string BaseDecision, IReadOnlyList<string> BaseSignals, string EvidenceSignature, bool V7HeadingLike);
    private sealed record ResidualRow(string SourceId, int SourceOrdinal, string SourceText, string SkipReason, string EvidenceSignature, bool V7HeadingLike);
    private sealed record DocumentAudit(
        string DocumentId, string SourceSha256, int SourceParagraphCount, int CurrentRoleInputCount,
        int EligibleBeforeRecovery, int RecoveryCount, int EligibleAfterRecovery, int PrefilterSkippedCount,
        int ProjectedRoleBatchCount, int ProjectedRoleInputTokens, int ProjectedLargestBatchBlocks, int ProjectedLargestBatchTokens,
        IReadOnlyDictionary<string, int> RecoveryCountsByRule, IReadOnlyDictionary<string, int> V7HeadingLikeRecoveredByRule,
        IReadOnlyList<ResidualRow> ResidualSkipped, IReadOnlyList<RecoveryRow> RecoveryRows, int WouldSkipV7UncertainCount,
        int SourcePlausibleRiskResidualCount, object Summary);
}
