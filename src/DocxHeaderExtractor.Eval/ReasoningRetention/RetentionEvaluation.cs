using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public sealed record ReasoningRouteFinal(
    bool SourcePresent,
    bool RepresentationPresent,
    bool CandidatePresent,
    bool ModelRequestPresent,
    bool ModelProposed,
    bool PostValidatorPresent,
    bool PostResolverPresent,
    bool FinalIncluded,
    string? Role,
    int? Level,
    string? Parent,
    string? SourceId,
    StructuralSpan? Span);

public sealed record ReasoningDocumentEvaluation(
    IReadOnlyList<ReasoningRetentionLedgerEntry> Ledger,
    RetentionPairMetrics Metrics,
    int EvaluatedOccurrenceCount,
    int NotEvaluableOccurrenceCount);

public static class ReasoningGoldArtifactLoader
{
    public static IReadOnlyList<ReasoningGoldOccurrence> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var documentId = root.GetProperty("documentId").GetString() ?? Path.GetFileNameWithoutExtension(path);
        var result = new List<ReasoningGoldOccurrence>();
        if (!root.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var heading in headings.EnumerateArray())
        {
            StructuralSpan? span = null;
            if (heading.TryGetProperty("headingSpan", out var spanElement) &&
                spanElement.TryGetProperty("start", out var start) && start.TryGetInt32(out var s) &&
                spanElement.TryGetProperty("end", out var end) && end.TryGetInt32(out var e))
                span = new StructuralSpan(s, e);
            result.Add(new ReasoningGoldOccurrence
            {
                DocumentId = documentId,
                GoldOccurrenceId = heading.GetProperty("headingOccurrenceId").GetString() ?? "",
                SourceId = heading.GetProperty("sourceId").GetString() ?? "",
                HeadingSpan = span,
                GoldRole = heading.TryGetProperty("role", out var role) ? role.GetString() : null,
                GoldRoleEvaluability = heading.TryGetProperty("role", out var roleValue) &&
                    string.Equals(roleValue.GetString(), "heading", StringComparison.OrdinalIgnoreCase)
                    ? "ROLE_NOT_EVALUABLE" : "EVALUABLE",
                GoldLevel = heading.TryGetProperty("level", out var level) && level.TryGetInt32(out var l) ? l : null,
                GoldParent = heading.TryGetProperty("parentHeadingOccurrenceId", out var parent) ? parent.GetString() : null,
                ExactText = heading.TryGetProperty("exactText", out var text) ? text.GetString() ?? "" : "",
            });
        }
        return result;
    }

    public static IReadOnlyList<string> DiscoverExhaustiveDocuments(string root)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "eval", "a99-closed-loop", "strict-gold-v4"),
                     "*.strict-gold-v4.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = document.RootElement;
            if (rootElement.GetProperty("coverage").GetString() != "EXHAUSTIVE" ||
                !rootElement.GetProperty("headingSetExhaustive").GetBoolean() ||
                !rootElement.GetProperty("capabilities").GetProperty("semanticEvaluable").GetBoolean())
                continue;
            result.Add(rootElement.GetProperty("documentId").GetString()!);
        }
        return result;
    }

    /// <summary>
    /// Returns only documents whose committed Gold artifact explicitly authorizes exact
    /// occurrence/span evaluation. A semantic total or a partially populated heading list is
    /// deliberately insufficient for this denominator.
    /// </summary>
    public static IReadOnlyList<string> DiscoverMetricEvaluableDocuments(string root)
        => DiscoverOccurrenceEvaluableDocuments(root);

    /// <summary>
    /// Loads the exact occurrence-binding artifact. This is the only Gold loader used by the
    /// R1-B retention runner: semantic totals or V4 rows without raw source spans are not a
    /// valid denominator.
    /// </summary>
    public static IReadOnlyList<ReasoningGoldOccurrence> LoadOccurrence(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var artifact = JsonSerializer.Deserialize<StrictGoldOccurrenceArtifact>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"Không đọc được occurrence Gold: {path}");
        if (!string.Equals(artifact.Status, "PASS", StringComparison.Ordinal) ||
            !artifact.ExactApprovedHeadingListMaterialized ||
            artifact.Bindings.Count != artifact.SemanticHeadingTotal ||
            artifact.Bindings.Any(binding => !binding.ExactRawSubstringVerified ||
                                             binding.HeadingSpan.Start < 0 ||
                                             binding.HeadingSpan.End <= binding.HeadingSpan.Start ||
                                             binding.HeadingSpan.End - binding.HeadingSpan.Start != binding.RawSourceText.Length))
            throw new InvalidDataException($"Occurrence Gold chưa đủ điều kiện đo: {path}");

        return artifact.Bindings
            .OrderBy(binding => binding.HeadingOrdinal)
            .Select(binding => new ReasoningGoldOccurrence
            {
                DocumentId = artifact.DocumentId,
                GoldOccurrenceId = binding.HeadingOccurrenceId,
                SourceId = binding.SourceId,
                HeadingSpan = new StructuralSpan(binding.HeadingSpan.Start, binding.HeadingSpan.End),
                GoldRole = binding.SemanticRole,
                GoldRoleEvaluability = string.Equals(binding.SemanticRole, "heading", StringComparison.OrdinalIgnoreCase)
                    ? "ROLE_NOT_EVALUABLE" : "EVALUABLE",
                GoldLevel = binding.Level,
                GoldParent = binding.ParentHeadingOccurrenceId,
                ExactText = binding.RawSourceText,
            })
            .ToArray();
    }

    public static IReadOnlyList<string> DiscoverOccurrenceEvaluableDocuments(string root)
    {
        var occurrenceRoot = Path.Combine(root, "eval", "a99-closed-loop", "strict-gold-occurrence-v1");
        if (!Directory.Exists(occurrenceRoot)) return [];
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(occurrenceRoot, "*.occurrence-gold-v1.json"))
        {
            var artifact = JsonSerializer.Deserialize<StrictGoldOccurrenceArtifact>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (artifact is not null &&
                artifact.Status == "PASS" &&
                artifact.ExactApprovedHeadingListMaterialized &&
                artifact.Bindings.Count == artifact.SemanticHeadingTotal &&
                artifact.Bindings.All(binding => binding.ExactRawSubstringVerified &&
                                                  binding.HeadingSpan.Start >= 0 &&
                                                  binding.HeadingSpan.End > binding.HeadingSpan.Start &&
                                                  binding.HeadingSpan.End - binding.HeadingSpan.Start == binding.RawSourceText.Length))
                result.Add(artifact.DocumentId);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasExactHeadingSpan(JsonElement heading) =>
        heading.TryGetProperty("headingSpan", out var span) &&
        span.TryGetProperty("start", out var start) && start.TryGetInt32(out _) &&
        span.TryGetProperty("end", out var end) && end.TryGetInt32(out _);
}

public static class RetentionEvaluation
{
    public static ReasoningDocumentEvaluation Evaluate(
        IReadOnlyList<ReasoningGoldOccurrence> gold,
        IReadOnlyDictionary<string, ReasoningRouteFinal> ceiling,
        IReadOnlyDictionary<string, ReasoningRouteFinal> production,
        IReadOnlyDictionary<string, ReasoningRouteFinal> shadow)
    {
        ArgumentNullException.ThrowIfNull(gold);
        var ledger = new List<ReasoningRetentionLedgerEntry>();
        var evaluated = 0;
        var notEvaluable = 0;
        var cc = 0;
        var cw = 0;
        var wc = 0;
        var ww = 0;

        foreach (var reference in gold)
        {
            var key = reference.HeadingSpan is { } span
                ? OccurrenceKey(reference.SourceId, span)
                : null;
            if (key is null)
            {
                notEvaluable++;
                ledger.Add(NotEvaluable(reference));
                continue;
            }

            evaluated++;
            ceiling.TryGetValue(key, out var ceilingResult);
            production.TryGetValue(key, out var productionResult);
            shadow.TryGetValue(key, out var shadowResult);
            var ceilingCorrect = IsCorrect(reference, ceilingResult);
            var productionCorrect = IsCorrect(reference, productionResult);
            if (ceilingCorrect && productionCorrect) cc++;
            else if (ceilingCorrect) cw++;
            else if (productionCorrect) wc++;
            else ww++;
            ledger.Add(BuildEntry(reference, ceilingResult, productionResult, shadowResult, ceilingCorrect, productionCorrect));
        }

        var ceilingMetric = Metric(gold, ceiling);
        var productionMetric = Metric(gold, production);
        var shadowMetric = Metric(gold, shadow);
        return new ReasoningDocumentEvaluation(
            ledger,
            new RetentionPairMetrics(
                ceilingMetric,
                productionMetric,
                shadowMetric,
                Subtract(ceilingMetric.F1, productionMetric.F1),
                Divide(productionMetric.F1, ceilingMetric.F1),
                cc,
                cw,
                wc,
                ww),
            evaluated,
            notEvaluable);
    }

    public static string OccurrenceKey(string sourceId, StructuralSpan span) =>
        $"{sourceId}:{span.Start}:{span.End}";

    private static bool IsCorrect(ReasoningGoldOccurrence gold, ReasoningRouteFinal? result) =>
        result is not null &&
        result.FinalIncluded &&
        string.Equals(result.SourceId, gold.SourceId, StringComparison.Ordinal) &&
        gold.HeadingSpan is { } span &&
        result.Span == span &&
        (gold.GoldRoleEvaluability == "ROLE_NOT_EVALUABLE" || gold.GoldRole is null || string.Equals(gold.GoldRole, result.Role, StringComparison.OrdinalIgnoreCase)) &&
        (gold.GoldLevel is null || gold.GoldLevel == result.Level);

    private static ReasoningRetentionLedgerEntry BuildEntry(
        ReasoningGoldOccurrence gold,
        ReasoningRouteFinal? ceiling,
        ReasoningRouteFinal? production,
        ReasoningRouteFinal? shadow,
        bool ceilingCorrect,
        bool productionCorrect) =>
        new()
        {
            DocumentId = gold.DocumentId,
            GoldOccurrenceId = gold.GoldOccurrenceId,
            GoldRole = gold.GoldRole,
            GoldLevel = gold.GoldLevel,
            GoldParent = gold.GoldParent,
            SourcePresent = true,
            SourceOccurrenceId = OccurrenceKey(gold.SourceId, gold.HeadingSpan!),
            Ceiling = Snapshot(ceiling),
            Production = new ReasoningProductionSnapshot(
                production?.SourcePresent == true,
                production?.RepresentationPresent == true,
                production?.CandidatePresent == true,
                production?.ModelRequestPresent == true,
                production?.ModelProposed == true,
                production?.PostValidatorPresent == true,
                production?.PostResolverPresent == true,
                production?.FinalIncluded == true,
                production?.Role,
                production?.Level,
                production?.Parent),
            Shadow = Snapshot(shadow),
            FirstSystemLossStage = FirstLoss(production),
            Classification = ceilingCorrect && productionCorrect
                ? RetentionClassification.BothCorrect
                : ceilingCorrect
                    ? RetentionClassification.SystemInducedLoss
                    : productionCorrect
                        ? RetentionClassification.SystemRescue
                        : RetentionClassification.ModelCapabilityGap,
        };

    private static ReasoningRetentionLedgerEntry NotEvaluable(ReasoningGoldOccurrence gold) =>
        new()
        {
            DocumentId = gold.DocumentId,
            GoldOccurrenceId = gold.GoldOccurrenceId,
            GoldRole = gold.GoldRole,
            GoldLevel = gold.GoldLevel,
            GoldParent = gold.GoldParent,
            SourcePresent = true,
            Ceiling = new(false, false, null, null, null, false),
            Production = new(false, false, false, false, false, false, false, false, null, null, null),
            Shadow = new(false, false, null, null, null, false),
            FirstSystemLossStage = FirstSystemLossStage.None,
            Classification = RetentionClassification.NotEvaluable,
        };

    private static ReasoningObservationSnapshot Snapshot(ReasoningRouteFinal? result) =>
        new(
            result?.SourcePresent == true,
            result?.ModelProposed == true,
            result?.Role,
            result?.Level,
            result?.Parent,
            result?.FinalIncluded == true);

    private static FirstSystemLossStage FirstLoss(ReasoningRouteFinal? result)
    {
        if (result is null || !result.SourcePresent) return FirstSystemLossStage.Source;
        if (!result.RepresentationPresent) return FirstSystemLossStage.Representation;
        if (!result.CandidatePresent) return FirstSystemLossStage.Candidate;
        if (!result.ModelRequestPresent) return FirstSystemLossStage.ModelRequest;
        if (!result.ModelProposed) return FirstSystemLossStage.ModelProposalTransfer;
        if (!result.PostValidatorPresent) return FirstSystemLossStage.SemanticValidator;
        if (!result.PostResolverPresent) return FirstSystemLossStage.StructuralResolver;
        if (!result.FinalIncluded) return FirstSystemLossStage.FinalOutput;
        return FirstSystemLossStage.None;
    }

    private static RetentionMetric Metric(
        IReadOnlyList<ReasoningGoldOccurrence> gold,
        IReadOnlyDictionary<string, ReasoningRouteFinal> result)
    {
        var evaluable = gold.Where(item => item.HeadingSpan is not null).ToArray();
        if (evaluable.Length == 0) return new(null, null, null, null, null, null, null);
        var correct = evaluable.Count(item =>
        {
            var key = OccurrenceKey(item.SourceId, item.HeadingSpan!);
            return result.TryGetValue(key, out var value) && IsCorrect(item, value);
        });
        var recall = (double)correct / evaluable.Length;
        return new(recall, recall, recall, null, null, null, null);
    }

    private static double? Subtract(double? left, double? right) =>
        left is null || right is null ? null : left - right;

    private static double? Divide(double? numerator, double? denominator) =>
        numerator is null || denominator is null || denominator == 0 ? null : numerator / denominator;
}
