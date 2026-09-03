namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Execution capability, deliberately independent of any provider or model name.</summary>
public enum ModelTier
{
    Deterministic,
    Small,
    Medium,
    Frontier,
    Review,
}

/// <summary>
/// A lossless rank record. Rank changes processing order and tier only: every retrieved candidate
/// remains in the plan and is auditable even when a budget postpones it.
/// </summary>
public sealed record RankedCandidate(
    string SourceId,
    int Page,
    string Text,
    double CandidateScore,
    double EscalationScore,
    ModelTier Tier,
    IReadOnlyList<string> PositiveSignals,
    IReadOnlyList<string> NegativeSignals,
    IReadOnlyList<string> AmbiguitySignals,
    string Scope = "unknown",
    string? OccurrenceKey = null);

public sealed record PdfCandidateRankingAudit(
    string Status,
    int CandidateCount,
    IReadOnlyList<RankedCandidate> Candidates);

/// <summary>Feature-only ordering; it has no model dependency and never removes a candidate.</summary>
internal static class PdfCandidateRanker
{
    /// <summary>
    /// Orders candidates by score. <paramref name="structuralMarkerCountsAsStrong"/> is
    /// evaluation-only: it admits the existing strict structural-marker fact to the existing strong
    /// marker path, changing no weight and adding no signal, so a counterfactual can ask whether the
    /// two kinds of marker evidence are equivalent. It is false in production.
    /// </summary>
    public static IReadOnlyList<RankedCandidate> Rank(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        bool structuralMarkerCountsAsStrong = false)
    {
        var maxFont = Math.Max(1, blocks.Select(block => block.PrimaryStyle.FontSizeBucket).DefaultIfEmpty(1).Max());
        return blocks.Select(block => Build(block, contexts[block.Id], maxFont,
                HasMarkerPrefixPredecessor(block, blocks), structuralMarkerCountsAsStrong))
            .OrderByDescending(item => item.CandidateScore)
            .ThenByDescending(item => item.EscalationScore)
            .ThenBy(item => item.Page)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static RankedCandidate Build(PdfSemanticBlock block, PdfCandidateContext context, double maxFont,
        bool hasMarkerPrefixPredecessor, bool structuralMarkerCountsAsStrong = false)
    {
        var positive = new List<string>();
        var negative = new List<string>();
        var ambiguity = new List<string>();
        var labelledMarker = PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(block.DisplayText) is not null ||
                             (structuralMarkerCountsAsStrong &&
                              PdfLineBlockAnnotation.HasStructuralMarker(block.DisplayText));
        var genericMarker = NumberingAudit.Parse(block.DisplayText) is not null;
        var standalone = block.LineCount == 1;
        var markerTitleComposite = IsMarkerTitleComposite(block);
        var longMarkerWindow = IsLongMarkerWindow(block, markerTitleComposite);
        var prominence = Math.Clamp(block.PrimaryStyle.FontSizeBucket / maxFont, 0, 1);
        var likelyBodyAfter = context.NextBlocks.FirstOrDefault() is { } next &&
            next.Length > 70 && next.EndsWith(".", StringComparison.Ordinal);

        var likelihood = 0.10;
        if (labelledMarker) { likelihood += 0.42; positive.Add("labelled_numbering_marker"); }
        else if (genericMarker) { likelihood += 0.10; positive.Add("unlabelled_numbering_prefix"); ambiguity.Add("unlabelled_numbering"); }
        if (standalone) { likelihood += 0.18; positive.Add("standalone"); }
        if (markerTitleComposite) { likelihood += 0.28; positive.Add("marker_title_composite"); }
        if (hasMarkerPrefixPredecessor) { likelihood += 0.22; positive.Add("canonical_marker_title"); }
        if (prominence >= 0.75) { likelihood += 0.16; positive.Add("layout_prominence"); }
        if (likelyBodyAfter) { likelihood += 0.12; positive.Add("opens_content"); }
        if (context.Source.StructuralScope == "table") { likelihood -= 0.60; negative.Add("table_scope"); }
        if (context.Source.StructuralScope == "running_page_artifact") { likelihood -= 0.75; negative.Add("running_page_scope"); }
        if (context.Source.ObservedEvidence.Contains("header_footer_zone")) { likelihood -= 0.15; negative.Add("header_footer_zone"); }
        if (longMarkerWindow) { likelihood -= 0.52; negative.Add("long_marker_body_window"); ambiguity.Add("marker_body_boundary"); }
        if (!labelledMarker) ambiguity.Add("no_labelled_structural_marker");
        if (!standalone && !markerTitleComposite) ambiguity.Add("multi_line_boundary");
        if (!likelyBodyAfter) ambiguity.Add("no_body_opening_evidence");
        if (context.Source.StructuralScope != "document_body") ambiguity.Add("scope_conflict");

        likelihood = Math.Clamp(likelihood, 0, 1);
        var escalation = Math.Clamp(
            0.15 + ambiguity.Count * 0.20 + (labelledMarker ? 0 : 0.10) +
            (context.Source.StructuralScope == "document_body" ? 0 : 0.25), 0, 1);
        var tier = PdfEscalationPolicy.Decide(likelihood, escalation, labelledMarker, context.Source.StructuralScope);
        return new RankedCandidate(block.Id, block.Page, block.DisplayText, likelihood, escalation, tier,
            positive, negative, ambiguity, context.Source.StructuralScope,
            PdfProductionOccurrenceResolver.FamilyKey(block.Lines.FirstOrDefault()?.Text ?? block.DisplayText));
    }

    private static bool IsMarkerTitleComposite(PdfSemanticBlock block)
    {
        if (block.LineCount is < 2 or > 3) return false;
        var first = block.Lines[0];
        var firstText = PdfTextUtilities.Readable(first.Text);
        if (PdfMarkerFactsParser.Parse(firstText) is null || firstText.Length is < 2 or > 48 ||
            firstText.EndsWith('.') || firstText.EndsWith(';') || firstText.EndsWith(':')) return false;

        return block.Lines.Skip(1).All(line =>
        {
            var text = PdfTextUtilities.Readable(line.Text);
            return text.Length is > 1 and <= 120 &&
                Math.Abs(line.Left - first.Left) <= 24 &&
                Math.Abs(line.FontSize - first.FontSize) <= 1.1 &&
                Math.Abs(line.BoldRatio - first.BoldRatio) <= 0.30;
        });
    }

    private static bool IsLongMarkerWindow(PdfSemanticBlock block, bool markerTitleComposite)
    {
        // Supplement windows retain recall, but a marker followed by several lines is usually
        // marker plus body. Let the tight or atomic source block lead under a bounded budget.
        return !markerTitleComposite && block.LineCount >= 4 &&
            PdfMarkerFactsParser.Parse(block.DisplayText) is not null;
    }

    private static bool HasMarkerPrefixPredecessor(PdfSemanticBlock block, IReadOnlyList<PdfSemanticBlock> all)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 8 or > 220 || PdfMarkerFactsParser.Parse(text) is null) return false;
        return all.Any(other => other.Id != block.Id && other.Page == block.Page && other.LineCount == 1 &&
            other.DisplayText.Length is >= 2 and <= 48 && PdfMarkerFactsParser.Parse(other.DisplayText) is not null &&
            text.StartsWith(other.DisplayText.Trim() + " ", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class PdfEscalationPolicy
{
    public static ModelTier Decide(double likelihood, double escalation, bool marker, string scope) =>
        scope is "table" or "running_page_artifact"
            ? ModelTier.Review
            : marker && likelihood >= 0.70 && escalation <= 0.35
                ? ModelTier.Deterministic
                : escalation >= 0.75
                    ? ModelTier.Frontier
                    : escalation >= 0.45
                        ? ModelTier.Medium
                        : ModelTier.Small;
}
