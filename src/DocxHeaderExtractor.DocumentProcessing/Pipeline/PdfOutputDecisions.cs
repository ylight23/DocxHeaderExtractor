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
/// Authority is split by what a reason actually claims. Source validity may suppress: an empty
/// text, an unexpected validation decision, or a heading with no anchor cannot be shown as an
/// occurrence of the document. A semantic heuristic may not: "this sits in a table", "this looks
/// like a caption", "this scope is usually excluded" are observations about meaning, and meaning
/// belongs to the model. They are recorded on the decision so a reviewer sees the disagreement,
/// and the heading is still emitted. An unresolved hierarchy is likewise not an exclusion — a
/// heading can be certain while its parent is unknown, which is exactly what M8 measured.
/// </para>
/// <para>
/// The rule cuts both ways: nothing here can promote a heading either. This policy only ever reads
/// facts the model proposed and the binder anchored, so a heuristic can neither create a heading
/// nor delete one.
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
        // Only source validity may suppress a heading the model called a heading. Each of these
        // says the fact cannot be anchored in the source, not that it means something else.
        var blocking = new List<string>();
        if (string.IsNullOrWhiteSpace(heading.Text)) blocking.Add("empty_source_text");
        if (!string.Equals(heading.ValidationDecision, "requires_review", StringComparison.Ordinal))
            blocking.Add($"unexpected_validation_decision:{heading.ValidationDecision}");
        // A product heading has to be locatable in the canonical source; without that anchor it can
        // be reviewed as a fact but not shown as an occurrence of the document, and not written back.
        if (heading.SourceAnchor is null) blocking.Add(heading.GroundingStatus);

        var emit = blocking.Count == 0;

        // Recorded, never suppressive. A role or scope heuristic that disagrees with the model is
        // evidence for a reviewer; letting it drop the heading would put meaning back in the hands
        // of a pattern match. Measured cost of the old behaviour: three headings the model had
        // identified correctly, with valid aliases and exact verbatim text, vanished from the
        // output because they sat inside a table and the role heuristic called them table titles.
        var observations = new List<string>();
        if (heading.DomainExclusionProposed) observations.Add($"domain_role_disagreement:{heading.Role}");
        if (Array.IndexOf(ExcludedScopes, heading.Scope) >= 0)
            observations.Add($"scope_disagreement:{heading.Scope}");
        // Review state is independent of emission: the product shows the heading and still marks it
        // for a human. Reporting an unresolved hierarchy as a reason must not suppress the heading.
        if (emit && heading.HierarchyStatus != "resolved") observations.Add($"hierarchy_{heading.HierarchyStatus}");

        return new PdfOutputDecision(heading.Id, emit, emit, [.. blocking, .. observations]);
    }
}

public sealed record PdfOutputDecision(
    string HeadingId,
    bool Emit,
    bool RequiresReview,
    IReadOnlyList<string> Reasons);
