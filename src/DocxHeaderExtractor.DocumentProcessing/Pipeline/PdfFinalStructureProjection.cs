using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// M9.1 materialization. It projects what the pipeline already validated into the shape a product
/// can consume, and nothing more: it runs no model, produces no candidate, resolves no hierarchy,
/// performs no matching, and never invents a relation the evidence does not carry.
/// <para>
/// Identity is canonical, not observational. For a DOCX product the document is the authority and
/// the rendered PDF is an observation of it, so a heading is identified by its
/// <see cref="DocxSourceAnchor"/> and its text is a slice of that paragraph. The PDF block and span
/// are retained as <see cref="PdfEvidenceAnchor"/> for provenance. A fact whose canonical occurrence
/// was never reconciled stays `grounding_unresolved` rather than being matched by title here.
/// </para>
/// <para>
/// An absent parent or level is a result, not a gap to fill. M8 measured what happens when a
/// missing parent is guessed from source order, so this layer keeps `unresolved` verbatim.
/// </para>
/// <para>
/// This is not the output policy. Deciding which validated facts a particular product emits belongs
/// to <see cref="PdfOutputDecisionPolicy"/>, so every validated heading is materialized here —
/// including roles a given product will later drop — and the policy layer keeps something complete
/// to filter.
/// </para>
/// </summary>
public static class PdfFinalStructureProjection
{
    public const int SchemaVersion = 2;

    public static PdfFinalStructure Project(
        string sourceDocumentSha256,
        IReadOnlyList<PdfValidatedStructure> structures,
        IReadOnlyList<PdfHierarchyFactAudit> facts,
        IReadOnlyList<PdfCanonicalGrounding> groundings)
    {
        var factById = facts
            .GroupBy(fact => fact.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var groundingById = groundings
            .GroupBy(item => item.SourceFactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ordered = structures
            .Where(structure => factById.ContainsKey(structure.SourceId))
            .OrderBy(structure => factById[structure.SourceId].SourceOrder)
            .ToArray();
        var emittedIds = ordered.Select(structure => structure.SourceId).ToHashSet(StringComparer.Ordinal);
        // A parent is referenced by the canonical identity of the parent heading, not by the block
        // that happened to observe it, so the mapping is resolved before any relation is emitted.
        var canonicalIdBySourceId = ordered.ToDictionary(
            structure => structure.SourceId,
            structure => groundingById.TryGetValue(structure.SourceId, out var item)
                ? CanonicalId(item)
                : factById[structure.SourceId].FactId,
            StringComparer.Ordinal);

        var headings = new List<PdfFinalHeading>(ordered.Length);
        foreach (var structure in ordered)
        {
            var fact = factById[structure.SourceId];
            var (level, levelReason) = ResolveLevel(fact);
            var (parentSourceId, parentReason) = ResolveParent(structure, factById, emittedIds);
            var parentId = parentSourceId is null ? null : canonicalIdBySourceId[parentSourceId];
            groundingById.TryGetValue(structure.SourceId, out var grounding);
            var (text, groundingStatus) = ResolveText(grounding, fact);
            headings.Add(new PdfFinalHeading(
                canonicalIdBySourceId[structure.SourceId],
                grounding is null
                    ? null
                    : new DocxSourceAnchor(grounding.ParagraphIndex, grounding.StableId, grounding.Span),
                new PdfEvidenceAnchor(fact.Page, fact.Id, new PdfTextSpan(fact.HeadingSpan.Start, fact.HeadingSpan.End),
                    fact.HeadingText, fact.LineIds),
                text,
                structure.DomainRole.ToString(),
                structure.StructuralScope,
                structure.Decision,
                groundingStatus,
                level,
                parentId,
                Status(level, parentId),
                levelReason,
                parentReason,
                "validated",
                grounding?.ParagraphText ?? fact.SourceBlockText)
            {
                // Rehydrate the proposal for legacy serialized facts that predate the explicit
                // evidence field. New producer output already carries this value from the source
                // detector; the fallback does not create or validate a structural element.
                DomainExclusionProposed = structure.DomainExclusionProposed ||
                    DocumentDomainPolicy.EvidenceForRole(structure.DomainRole, "legacy-domain-fact").ProposesOutlineExclusion,
            });
        }

        return new PdfFinalStructure(
            sourceDocumentSha256,
            PdfHierarchyFactHash.OfText(string.Join('\n', ordered.Select(structure =>
                string.Join('|', structure.SourceId, structure.Level, structure.ParentId ?? "-",
                    structure.ParentResolution, structure.Decision)))),
            PdfHierarchyFactHash.OfText(string.Join('\n', headings.Select(heading =>
                string.Join('|', heading.Id, heading.GroundingStatus, heading.Level?.ToString() ?? "-",
                    heading.ParentId ?? "-", heading.HierarchyStatus)))),
            new PdfFinalStructureCounters(
                structures.Count,
                headings.Count,
                structures.Count - headings.Count,
                headings.Count(heading => heading.SourceAnchor is null),
                headings.Count(heading => heading.Level is not null),
                headings.Count(heading => heading.ParentId is not null),
                headings.Count(heading => heading.HierarchyStatus == "unresolved")),
            headings);
    }

    /// <summary>
    /// A grounded heading's text is a slice of the canonical paragraph, so what the product shows is
    /// what the document says. Without a canonical occurrence there is no such text: the observed
    /// PDF line is kept so the fact stays reviewable, but it is marked ungrounded, and a heading
    /// that cannot be located in the source cannot be written back to it.
    /// </summary>
    private static (string Text, string Status) ResolveText(PdfCanonicalGrounding? grounding, PdfHierarchyFactAudit fact)
    {
        if (grounding is null) return (fact.HeadingText, "grounding_unresolved");
        var paragraph = grounding.ParagraphText;
        var span = grounding.Span;
        if (span.Start < 0 || span.End <= span.Start || span.End > paragraph.Length)
            return (fact.HeadingText, "grounding_span_out_of_range");
        return (paragraph[span.Start..span.End], "grounded");
    }

    /// <summary>
    /// A level is emitted only where the evidence still supports it. The inventory derives
    /// <c>ResolvedLevel</c> from the strict dotted path, while <c>MarkerComponents</c> records every
    /// component the parser observed; where a source lost its separators these disagree, and the
    /// strict depth is known to be short. Asserting it anyway would ship a confident wrong level,
    /// so the disagreement is reported as unresolved instead.
    /// </summary>
    private static (int? Level, string? Reason) ResolveLevel(PdfHierarchyFactAudit fact)
    {
        if (fact.ResolvedLevel is not { } level) return (null, "no_deterministic_level_evidence");
        if (fact.MarkerComponents.Count > 0 && fact.MarkerComponents.Count != level)
            return (null, "marker_representation_conflict");
        return (level, null);
    }

    /// <summary>
    /// A parent survives only when the validated structure claims one, the claim resolves to a
    /// heading that is itself emitted, and that heading precedes this one. A parent pointing outside
    /// the emitted set would be a dangling edge, which is worse than an honest null.
    /// </summary>
    private static (string? ParentId, string? Reason) ResolveParent(
        PdfValidatedStructure structure,
        IReadOnlyDictionary<string, PdfHierarchyFactAudit> factById,
        IReadOnlySet<string> emittedIds)
    {
        if (structure.ParentId is not { } parent) return (null, "no_parent_in_validated_structure");
        if (string.Equals(structure.ParentResolution, "unresolved", StringComparison.Ordinal))
            return (null, "validated_structure_reports_unresolved");
        if (!emittedIds.Contains(parent) || !factById.TryGetValue(parent, out var parentFact))
            return (null, "parent_not_in_emitted_set");
        if (parentFact.SourceOrder >= factById[structure.SourceId].SourceOrder)
            return (null, "parent_does_not_precede_child");
        return (parent, null);
    }

    private static string CanonicalId(PdfCanonicalGrounding grounding) =>
        $"{grounding.StableId ?? $"p[{grounding.ParagraphIndex}]"}#{grounding.Span.Start}-{grounding.Span.End}";

    private static string Status(int? level, string? parentId) => (level, parentId) switch
    {
        (not null, not null) => "resolved",
        (not null, null) => "parent_unresolved",
        (null, not null) => "level_unresolved",
        _ => "unresolved",
    };
}

public sealed record PdfFinalStructure(
    [property: JsonPropertyName("sourceDocumentSha256")] string SourceDocumentSha256,
    [property: JsonPropertyName("validatedStructureFingerprint")] string ValidatedStructureFingerprint,
    [property: JsonPropertyName("finalStructureFingerprint")] string FinalStructureFingerprint,
    [property: JsonPropertyName("counters")] PdfFinalStructureCounters Counters,
    [property: JsonPropertyName("headings")] IReadOnlyList<PdfFinalHeading> Headings)
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion => PdfFinalStructureProjection.SchemaVersion;

    [JsonPropertyName("artifactKind")]
    public string ArtifactKind => "pdf_final_structure";
}

public sealed record PdfFinalStructureCounters(
    [property: JsonPropertyName("validatedStructures")] int ValidatedStructures,
    [property: JsonPropertyName("emittedHeadings")] int EmittedHeadings,
    [property: JsonPropertyName("droppedWithoutSourceFact")] int DroppedWithoutSourceFact,
    [property: JsonPropertyName("groundingUnresolved")] int GroundingUnresolved,
    [property: JsonPropertyName("levelResolved")] int LevelResolved,
    [property: JsonPropertyName("parentResolved")] int ParentResolved,
    [property: JsonPropertyName("fullyUnresolved")] int FullyUnresolved);

public sealed record PdfFinalHeading(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceAnchor")] DocxSourceAnchor? SourceAnchor,
    [property: JsonPropertyName("pdfEvidence")] PdfEvidenceAnchor? PdfEvidence,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("validationDecision")] string ValidationDecision,
    [property: JsonPropertyName("groundingStatus")] string GroundingStatus,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("hierarchyStatus")] string HierarchyStatus,
    [property: JsonPropertyName("levelReason")] string? LevelReason,
    [property: JsonPropertyName("parentReason")] string? ParentReason,
    [property: JsonPropertyName("authority")] string Authority,
    // Additive in schema v2: old artifacts simply omit this field; new products use it to preserve
    // the canonical paragraph when the heading span starts after offset zero.
    [property: JsonPropertyName("sourceText")] string SourceText)
{
    /// <summary>Domain detector evidence used by product policy; excluded from serialized output.</summary>
    [JsonIgnore]
    public bool DomainExclusionProposed { get; init; }
}
