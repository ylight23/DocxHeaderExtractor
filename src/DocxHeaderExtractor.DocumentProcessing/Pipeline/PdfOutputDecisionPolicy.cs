namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// M9.2. Decides which validated facts a document-outline product emits, reading only what
/// <see cref="PdfFinalStructureProjection"/> already materialized.
/// <para>
/// It answers a different question from the validator. The validator asks whether a fact is real;
/// this asks whether a given product should show it. So it may not re-litigate the fact: it never
/// looks at candidates, model proposals, ranks, gold, or geometry, never edits text, role, scope,
/// level, or parent, and never adds or removes a heading from the structure. It returns one
/// decision per heading and leaves the structure itself intact, so an excluded fact stays visible
/// to an audit instead of disappearing from the record.
/// </para>
/// <para>
/// Exclusion rules here preserve the historical M9.4 comparison semantics without depending on the
/// evaluation-only legacy projection; this
/// is a change of input, not of policy. An unresolved hierarchy is deliberately not an exclusion —
/// a heading can be certain while its parent is unknown, which is exactly what M8 measured.
/// </para>
/// </summary>
public static class PdfOutputDecisionPolicy
{
    /// <summary>
    /// The scopes this policy refuses outright. Exposed so an audit can ask what selection spends its
    /// budget on without keeping a second copy of the list, which would silently disagree the first
    /// time one is edited.
    /// </summary>
    internal static readonly string[] ExcludedScopes =
        ["embedded_amendment", "quoted_replacement", "appendix_table"];

    public static IReadOnlyList<PdfOutputDecision> Decide(PdfFinalStructure structure) =>
        structure.Headings.Select(Decide).ToArray();

    public static PdfOutputDecision Decide(PdfFinalHeading heading)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(heading.Text)) reasons.Add("empty_source_text");
        if (Array.IndexOf(ExcludedScopes, heading.Scope) >= 0) reasons.Add($"excluded_scope:{heading.Scope}");
        if (heading.DomainExclusionProposed) reasons.Add($"excluded_role:{heading.Role}");
        if (!string.Equals(heading.ValidationDecision, "requires_review", StringComparison.Ordinal))
            reasons.Add($"unexpected_validation_decision:{heading.ValidationDecision}");
        // A product heading has to be locatable in the canonical source; without that anchor it can
        // be reviewed as a fact but not shown as an occurrence of the document, and not written back.
        if (heading.SourceAnchor is null) reasons.Add(heading.GroundingStatus);

        var emit = reasons.Count == 0;
        // Review state is independent of emission: the product shows the heading and still marks it
        // for a human. Reporting an unresolved hierarchy as a reason must not suppress the heading.
        if (emit && heading.HierarchyStatus != "resolved") reasons.Add($"hierarchy_{heading.HierarchyStatus}");
        return new PdfOutputDecision(heading.Id, emit, emit, reasons);
    }
}

public sealed record PdfOutputDecision(
    string HeadingId,
    bool Emit,
    bool RequiresReview,
    IReadOnlyList<string> Reasons);
