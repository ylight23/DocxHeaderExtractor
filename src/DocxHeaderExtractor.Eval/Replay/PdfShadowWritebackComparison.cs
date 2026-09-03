using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// M9.4 writeback half. Runs both writebacks against fresh copies of the SAME source document and
/// compares their semantic mutations - not a byte-for-byte OOXML diff, since two implementations can
/// serialize identical meaning differently.
/// <para>
/// Both <see cref="OutlineWriteback.Apply"/> and <see cref="PdfProductWriteback.Apply"/> already
/// verify their own output internally and throw (deleting the target) on any text corruption or
/// mismatch, so a successful result from either call already guarantees that lane touched nothing but
/// the outline level/style of the paragraphs it applied to - see each class's own <c>Verify</c>. This
/// comparison does not re-derive that guarantee; it adds the one thing a single lane's own test
/// cannot show: whether the two lanes agree on WHICH original paragraphs they mutated.
/// </para>
/// </summary>
public static class PdfShadowWritebackComparison
{
    public static PdfShadowWritebackReport Compare(
        string sourceDocxPath,
        string legacyTargetPath,
        string newTargetPath,
        DocumentOutline legacyOutline,
        PdfProductOutput newOutput,
        ExtractionOptions extraction)
    {
        var legacyResult = OutlineWriteback.Apply(sourceDocxPath, legacyTargetPath, legacyOutline, extraction,
            new OutlineWritebackOptions { Overwrite = true });
        var newResult = PdfProductWriteback.Apply(sourceDocxPath, newTargetPath, newOutput, extraction,
            new OutlineWritebackOptions { Overwrite = true });

        // Both writebacks key their Applied/Skipped sets on the ORIGINAL paragraph index - splits are
        // performed only after every outlineLvl assignment is placed - so the applied-index sets can
        // be compared directly without reproducing either class's post-split shift arithmetic.
        var legacySkipped = legacyResult.Skipped.Select(s => s.Index).ToHashSet();
        var legacyApplied = legacyOutline.Headings
            .Select(h => h.Index)
            .Where(index => !legacySkipped.Contains(index))
            .ToHashSet();

        var newSkipped = newResult.Skipped.Select(s => s.Index).ToHashSet();
        var newApplied = newOutput.Headings
            .Select(h => h.ParagraphIndex)
            .Where(index => !newSkipped.Contains(index))
            .ToHashSet();

        return new PdfShadowWritebackReport(
            legacyResult.Applied,
            newResult.Applied,
            legacyApplied.Intersect(newApplied).Count(),
            newResult.Skipped.Count(s => s.Reason is "anchor_text_mismatched" or "stable_id_mismatch"),
            newResult.Skipped.Count(s => s.Reason == "level_unresolved"),
            UnexpectedTextChanges: 0);
    }
}

/// <param name="UnexpectedTextChanges">
/// Structurally 0 for any report this method returns: a text corruption in either lane's own output
/// makes that lane's <c>Apply</c> throw before <c>Compare</c> can return at all. Kept in the shape for
/// schema parity with the M9.4 artifact - it is a fail-closed sentinel, not an independently computed
/// cross-lane text diff.
/// </param>
public sealed record PdfShadowWritebackReport(
    int LegacyModifiedParagraphs,
    int NewModifiedParagraphs,
    int SameSemanticMutations,
    int NewAnchorFailures,
    int NewLevelUnresolvedSkips,
    int UnexpectedTextChanges);
