using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// M9.4. Compares the legacy PDF-first product against the M9 lane over the SAME frozen upstream
/// facts - this runs no model and re-parses no PDF; both lanes are already-computed products the
/// caller supplies from one generation pass.
/// <para>
/// Both lanes trace every heading back to the same <c>PdfHierarchyFactAudit.Id</c> - legacy via
/// <see cref="HeadingRecord.SourceId"/>, M9 via <c>PdfFinalHeading.PdfEvidence.BlockId</c> - and that
/// fact id, not the DOCX anchor either lane resolved to, is the join key. Two lanes disagreeing about
/// WHICH occurrence a fact grounds to is exactly the regression class <see cref="AnchorMismatch"/> in
/// <see cref="PdfShadowCompatibilityReport"/> exists to catch; joining on the anchor itself would hide it.
/// </para>
/// <para>
/// Hierarchy (level/parent) is graded separately against reviewed gold in
/// <see cref="CompareHierarchy"/>, never against the legacy lane - M9's own doc comments already
/// establish that the legacy PDF route derives level from style clusters while M9 uses the validated
/// DOCX structure, two authorities expected to disagree.
/// </para>
/// </summary>
public static class PdfShadowLaneComparison
{
    public static PdfShadowCompatibilityReport CompareCompatibility(
        IReadOnlyList<HeadingRecord> legacyProduct,
        PdfFinalStructure finalStructure,
        IReadOnlyList<PdfOutputDecision> decisions)
    {
        var legacyById = legacyProduct
            .Where(h => !string.IsNullOrEmpty(h.SourceId))
            .GroupBy(h => h.SourceId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var decisionById = decisions.ToDictionary(d => d.HeadingId, StringComparer.Ordinal);
        var newEmitted = finalStructure.Headings
            .Where(h => h.PdfEvidence is not null && decisionById.TryGetValue(h.Id, out var d) && d.Emit)
            .GroupBy(h => h.PdfEvidence!.BlockId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var missingInNew = new List<string>();
        var anchorMismatch = new List<string>();
        var textMismatch = new List<string>();
        var reviewMismatch = new List<string>();
        var same = new List<string>();

        foreach (var (factId, legacy) in legacyById)
        {
            if (!newEmitted.TryGetValue(factId, out var @new))
            {
                missingInNew.Add(factId);
                continue;
            }

            var anchorsAgree = @new.SourceAnchor is not null
                && @new.SourceAnchor.ParagraphIndex == legacy.Index
                && string.Equals(@new.SourceAnchor.StableId, legacy.StableId, StringComparison.Ordinal);
            if (!anchorsAgree) { anchorMismatch.Add(factId); continue; }

            if (!string.Equals(legacy.Text, @new.Text, StringComparison.Ordinal)) { textMismatch.Add(factId); continue; }

            var legacyRequiresReview = legacy.DecisionStatus == HeadingDecisionStatus.RequiresReview;
            var newRequiresReview = decisionById[@new.Id].RequiresReview;
            if (legacyRequiresReview != newRequiresReview) { reviewMismatch.Add(factId); continue; }

            same.Add(factId);
        }

        var extraInNew = newEmitted.Keys.Where(factId => !legacyById.ContainsKey(factId)).ToArray();
        var orderMismatch = OrderMismatch(legacyById, newEmitted, finalStructure);

        return new PdfShadowCompatibilityReport(
            legacyById.Count, newEmitted.Count, same.Count,
            missingInNew, extraInNew, anchorMismatch, textMismatch, orderMismatch, reviewMismatch);
    }

    /// <summary>
    /// Inversions among facts both lanes emitted: a pair whose relative order under the legacy lane
    /// (paragraph index, then heading-span start) disagrees with their relative order under the new
    /// lane (<see cref="PdfFinalStructure"/>'s own source order).
    /// </summary>
    private static IReadOnlyList<string> OrderMismatch(
        IReadOnlyDictionary<string, HeadingRecord> legacyById,
        IReadOnlyDictionary<string, PdfFinalHeading> newEmitted,
        PdfFinalStructure finalStructure)
    {
        var shared = legacyById.Keys.Where(newEmitted.ContainsKey).ToArray();
        if (shared.Length < 2) return [];

        var legacyRank = shared
            .OrderBy(id => legacyById[id].Index)
            .ThenBy(id => legacyById[id].HeadingSpan?.Start ?? 0)
            .Select((id, rank) => (id, rank))
            .ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);

        var newSourceOrder = finalStructure.Headings
            .Select((h, order) => (h, order))
            .Where(x => x.h.PdfEvidence is not null)
            .ToDictionary(x => x.h.PdfEvidence!.BlockId, x => x.order, StringComparer.Ordinal);

        var newRank = shared
            .OrderBy(id => newSourceOrder.GetValueOrDefault(id))
            .Select((id, rank) => (id, rank))
            .ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);

        var mismatched = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < shared.Length; i++)
            for (var j = i + 1; j < shared.Length; j++)
            {
                var a = shared[i];
                var b = shared[j];
                if (Math.Sign(legacyRank[a].CompareTo(legacyRank[b])) != Math.Sign(newRank[a].CompareTo(newRank[b])))
                {
                    mismatched.Add(a);
                    mismatched.Add(b);
                }
            }

        return mismatched.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// M9's level/parent against reviewed hierarchy gold - never against the legacy lane. Matched by
    /// <see cref="DocxSourceAnchor.StableId"/>, which is the identity <see cref="PdfHierarchyGold"/>
    /// already keys on (<c>SourceAnchor</c>); a fact the frozen route never grounded to a paragraph
    /// cannot be graded here and is simply not counted.
    /// <para>
    /// Gold files write <c>SourceAnchor</c> with a leading <c>@</c> - a human-authoring convention
    /// carried over from <c>.key</c> files (<see cref="AnswerKey"/> strips it the same way) - while
    /// <see cref="DocxSourceAnchor.StableId"/> as <see cref="OpenXmlLayer.ParagraphWalker"/> actually
    /// produces it never has one. Comparing the two without normalizing would match nothing.
    /// </para>
    /// </summary>
    public static PdfShadowHierarchyMigrationReport CompareHierarchy(PdfFinalStructure finalStructure, PdfHierarchyGold gold)
    {
        var goldByAnchor = gold.Headings.ToDictionary(h => NormalizeAnchor(h.SourceAnchor), StringComparer.Ordinal);
        var goldById = gold.Headings.ToDictionary(h => h.HeadingId, StringComparer.Ordinal);
        var idToStableId = finalStructure.Headings
            .Where(h => h.SourceAnchor is not null)
            .ToDictionary(h => h.Id, h => h.SourceAnchor!.StableId, StringComparer.Ordinal);

        var matched = 0;
        var resolvedLevels = 0;
        var levelCorrect = 0;
        var resolvedParents = 0;
        var parentCorrect = 0;
        var unresolved = 0;
        var representationConflict = 0;
        var predictedEdges = new List<(string Parent, string Child)>();

        foreach (var heading in finalStructure.Headings)
        {
            var stableId = heading.SourceAnchor?.StableId;
            if (stableId is null || !goldByAnchor.TryGetValue(stableId, out var goldItem)) continue;
            matched++;

            if (heading.Level is { } level)
            {
                resolvedLevels++;
                if (level == goldItem.GoldLevel) levelCorrect++;
            }
            else unresolved++;

            if (heading.LevelReason == "marker_representation_conflict") representationConflict++;

            if (heading.ParentId is { } parentId && idToStableId.TryGetValue(parentId, out var parentStableId) &&
                parentStableId is not null)
            {
                resolvedParents++;
                predictedEdges.Add((parentStableId, stableId));
                var expectedParentAnchor = goldItem.GoldParentId is null ? null
                    : goldById.TryGetValue(goldItem.GoldParentId, out var parentGold) ? NormalizeAnchor(parentGold.SourceAnchor) : null;
                if (expectedParentAnchor is not null &&
                    string.Equals(parentStableId, expectedParentAnchor, StringComparison.Ordinal))
                    parentCorrect++;
            }
        }

        var goldEdges = gold.Headings
            .Where(h => h.GoldParentId is not null && goldById.ContainsKey(h.GoldParentId))
            .Select(h => (Parent: NormalizeAnchor(goldById[h.GoldParentId!].SourceAnchor), Child: NormalizeAnchor(h.SourceAnchor)))
            .ToHashSet();
        var truePositives = predictedEdges.Count(goldEdges.Contains);
        double? precision = predictedEdges.Count == 0 ? null : (double)truePositives / predictedEdges.Count;
        double? recall = goldEdges.Count == 0 ? null : (double)truePositives / goldEdges.Count;
        double? f1 = precision is null || recall is null || precision + recall == 0
            ? null : 2 * precision * recall / (precision + recall);

        return new PdfShadowHierarchyMigrationReport(
            matched,
            resolvedLevels, Ratio(levelCorrect, resolvedLevels),
            resolvedParents, Ratio(parentCorrect, resolvedParents),
            new PdfHierarchyEdgeEvaluation(predictedEdges.Count, goldEdges.Count, truePositives, precision, recall, f1,
                predictedEdges.Count == 0 ? "no_predicted_edges" : "measured"),
            unresolved, representationConflict);
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;

    private static string NormalizeAnchor(string anchor) => anchor.StartsWith('@') ? anchor[1..] : anchor;
}

public sealed record PdfShadowCompatibilityReport(
    int LegacyEmitted,
    int NewEmitted,
    int SameOccurrence,
    IReadOnlyList<string> MissingInNew,
    IReadOnlyList<string> ExtraInNew,
    IReadOnlyList<string> AnchorMismatch,
    IReadOnlyList<string> TextMismatch,
    IReadOnlyList<string> OrderMismatch,
    IReadOnlyList<string> ReviewMismatch)
{
    /// <summary>Any non-empty diff class here is a regression by default, per the M9.4 gate, unless reviewed as intentional.</summary>
    public bool HasUnexplainedDiff =>
        MissingInNew.Count > 0 || ExtraInNew.Count > 0 || AnchorMismatch.Count > 0 ||
        TextMismatch.Count > 0 || OrderMismatch.Count > 0 || ReviewMismatch.Count > 0;
}

public sealed record PdfShadowHierarchyMigrationReport(
    int GoldMatched,
    int ResolvedLevels,
    double? LevelAccuracyGivenResolved,
    int ResolvedParents,
    double? ParentAccuracyGivenResolved,
    PdfHierarchyEdgeEvaluation EdgeMetrics,
    int Unresolved,
    int RepresentationConflict);
