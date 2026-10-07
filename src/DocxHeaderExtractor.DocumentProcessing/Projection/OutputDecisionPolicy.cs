namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>
/// M9.2. Decides which validated facts a document-outline product emits, reading only what
/// <see cref="CanonicalFinalStructureProjection"/> already materialized.
/// <para>
/// It answers a different question from the validator. The validator asks whether a fact is real;
/// this asks whether a given product should show it. So it may not re-litigate the fact: it never
/// looks at source-occurrence queues, model proposals, ranks, gold, or geometry, never edits text, role, scope,
/// level, or parent, and never adds or removes a heading from the structure. It returns one
/// decision per heading and leaves the structure itself intact, so an excluded fact stays visible
/// to an audit instead of disappearing from the record.
/// </para>
/// <para>
/// Only source validity may suppress: an empty text, an unexpected validation decision, or a
/// heading with no anchor cannot be shown as an occurrence of the document. Meaning belongs to the
/// model, so no parser scope or domain observation is consulted. An unresolved hierarchy is not an
/// exclusion either — a heading can be certain while its parent is unknown.
/// </para>
/// <para>
/// Nothing here can promote a heading either: it only reads facts the model proposed and the
/// binder anchored.
/// </para>
/// </summary>
public static class OutputDecisionPolicy
{
    public static IReadOnlyList<OutputDecision> Decide(CanonicalFinalStructure structure) =>
        structure.Headings.Select(Decide).ToArray();

    public static OutputDecision Decide(CanonicalFinalHeading heading)
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

        var observations = new List<string>();
        // Review state is independent of emission: the product shows the heading and still marks it
        // for a human. Reporting an unresolved hierarchy as a reason must not suppress the heading.
        if (emit && heading.HierarchyStatus != "resolved") observations.Add($"hierarchy_{heading.HierarchyStatus}");

        return new OutputDecision(heading.Id, emit, emit, [.. blocking, .. observations]);
    }
}

public sealed record OutputDecision(
    string HeadingId,
    bool Emit,
    bool RequiresReview,
    IReadOnlyList<string> Reasons);
