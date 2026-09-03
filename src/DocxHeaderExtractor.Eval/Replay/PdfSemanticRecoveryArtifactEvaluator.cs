using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// Evaluation-only replay for a frozen semantic recovery artifact. It never invokes a model,
/// selector, PDF parser, or candidate producer.
/// </summary>
public static class PdfSemanticRecoveryArtifactEvaluator
{
    public static PdfSemanticRecoveryResultEvaluation Evaluate(
        string recoveryArtifactJson,
        string baselineOccurrenceJson,
        AnswerKey key)
    {
        using var recoveryDocument = JsonDocument.Parse(recoveryArtifactJson);
        using var baselineDocument = JsonDocument.Parse(baselineOccurrenceJson);
        var root = RootObject(recoveryDocument.RootElement);
        if (root.TryGetProperty("usesGold", out var usesGold) && usesGold.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Recovery artifact declared usesGold=true and cannot be replayed as a production-safe run.");

        var report = root.GetProperty("report");
        var baselineOccurrence = RootObject(baselineDocument.RootElement).GetProperty("occurrence");
        var baselineCorrect = baselineOccurrence.GetProperty("GoldOccurrencesResolved").GetInt32();
        var baselineResolved = new HashSet<string>(StringComparer.Ordinal);
        var expectedPages = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in baselineOccurrence.GetProperty("Entries").EnumerateArray())
        {
            var title = Canonical(entry.GetProperty("Gold").GetString());
            if (title.Length == 0) continue;
            if (entry.TryGetProperty("ExpectedPdfPage", out var page) && page.TryGetInt32(out var expectedPage))
                expectedPages[title] = expectedPage;
            if (entry.TryGetProperty("Status", out var status) &&
                string.Equals(status.GetString(), "correct_occurrence_resolved", StringComparison.Ordinal))
                baselineResolved.Add(title);
        }

        var goldTitles = key.PositiveEntries
            .Select(entry => Canonical(entry.Text))
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var decisions = new List<PdfSemanticRecoveryItemEvaluation>();
        foreach (var item in report.GetProperty("Decisions").EnumerateArray())
        {
            var sourceText = item.GetProperty("SourceText").GetString() ?? "";
            var sourceTitle = Canonical(sourceText);
            var page = item.GetProperty("Page").GetInt32();
            var sourceGold = goldTitles.Contains(sourceTitle) &&
                (!expectedPages.TryGetValue(sourceTitle, out var expectedPage) || expectedPage == page);
            var role = item.GetProperty("Role").GetString() ?? "Uncertain";
            var canonicalSpan = item.TryGetProperty("CanonicalSpan", out var span) && span.ValueKind == JsonValueKind.String
                ? span.GetString()
                : null;
            var proposedGold = Canonical(canonicalSpan);
            var accepted = string.Equals(item.GetProperty("ValidationStatus").GetString(), "accepted", StringComparison.Ordinal);
            var goldMatch = accepted && goldTitles.Contains(proposedGold) &&
                (!expectedPages.TryGetValue(proposedGold, out var proposalPage) || proposalPage == page);
            var reason = item.TryGetProperty("Reason", out var reasonValue) ? reasonValue.GetString() : null;
            var outcome = ResolveOutcome(accepted, goldMatch, sourceGold, role, reason, canonicalSpan);
            decisions.Add(new PdfSemanticRecoveryItemEvaluation(
                item.GetProperty("Id").GetString() ?? "",
                item.GetProperty("SourceBlockId").GetString() ?? "",
                item.GetProperty("SourceLineIndex").GetInt32(), page, sourceText, role,
                accepted, sourceGold, goldMatch, outcome, reason));
        }

        var eligible = report.GetProperty("EligibleUnresolvedBlocks").GetInt32();
        var usable = report.GetProperty("HeadingRoleProposals").GetInt32();
        var canonicalUnique = report.GetProperty("CanonicalUniqueProposals").GetInt32();
        var validated = report.GetProperty("ValidatorAccepted").GetInt32();
        var goldCorrect = decisions.Count(item => item.GoldMatch);
        var falsePositive = decisions.Count(item => item.ValidatorAccepted && !item.GoldMatch);
        var gain = decisions.Count(item => item.GoldMatch && !baselineResolved.Contains(Canonical(item.SourceText)));
        var eligibleGold = decisions.Count(item => item.SourceGoldOpportunity);
        return new PdfSemanticRecoveryResultEvaluation(
            true, baselineCorrect, eligible, usable, canonicalUnique, validated, goldCorrect, falsePositive,
            gain, baselineCorrect + gain,
            Ratio(usable, eligible), Ratio(canonicalUnique, usable), Ratio(goldCorrect, validated),
            Ratio(goldCorrect, eligibleGold), eligibleGold, "not_measured", decisions);
    }

    private static string ResolveOutcome(bool accepted, bool goldMatch, bool sourceGold, string role, string? reason, string? canonicalSpan)
    {
        if (accepted) return goldMatch ? "validated_true_recovery" : "validated_false_positive";
        // A missing reply is an unknown model decision for the recovery experiment. The original
        // transport detail stays in Reason; it must not disguise a semantic false negative.
        if (string.Equals(reason, "missing-model-decision", StringComparison.Ordinal)) return "model_unknown";
        if (sourceGold && string.Equals(role, "BodySentence", StringComparison.Ordinal)) return "semantic_false_negative";
        if (string.Equals(role, "Uncertain", StringComparison.Ordinal)) return "model_unknown";
        if (string.Equals(role, "HeadingTopic", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(canonicalSpan))
            return "canonical_unresolved";
        if (string.Equals(role, "HeadingTopic", StringComparison.Ordinal)) return "validator_rejected";
        return "non_gold_eligible";
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;

    private static JsonElement RootObject(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object => root,
        JsonValueKind.Array when root.GetArrayLength() == 1 => root[0],
        _ => throw new InvalidOperationException("Artifact JSON phải là object hoặc mảng đúng một object."),
    };

    private static string Canonical(string? text) => string.Concat((text ?? "")
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant));
}

public sealed record PdfSemanticRecoveryItemEvaluation(
    string Id,
    string SourceBlockId,
    int SourceLineIndex,
    int Page,
    string SourceText,
    string ModelRole,
    bool ValidatorAccepted,
    bool SourceGoldOpportunity,
    bool GoldMatch,
    string Outcome,
    string? Reason);

public sealed record PdfSemanticRecoveryResultEvaluation(
    bool EvaluationOnly,
    int BaselineCorrectOccurrence,
    int Eligible,
    int UsableProposal,
    int CanonicalUnique,
    int Validated,
    int GoldCorrect,
    int FalsePositive,
    int NetCorrectOccurrenceGain,
    int CombinedCorrectOccurrence,
    double? RecoveryCoverage,
    double? CanonicalRecoveryRate,
    double? ValidatedPrecision,
    double? GoldOpportunityRecall,
    int EligibleGoldOpportunities,
    string Hierarchy,
    IReadOnlyList<PdfSemanticRecoveryItemEvaluation> Items);
