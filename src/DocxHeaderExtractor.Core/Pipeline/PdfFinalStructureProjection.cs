using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// M9.1 materialization. It projects what the pipeline already validated into the shape a product
/// can consume, and nothing more: it runs no model, produces no candidate, resolves no hierarchy,
/// and never invents a relation the evidence does not carry.
/// <para>
/// An absent parent or level is a result, not a gap to fill. M8 measured what happens when a
/// missing parent is guessed from source order, so this layer keeps `unresolved` verbatim.
/// </para>
/// <para>
/// This is not the output policy. Deciding which validated facts a particular product emits — an
/// outline, a caption list, a writeback — belongs to <see cref="PdfValidatedOutputPolicy"/> and its
/// successors. Here every validated heading is materialized, including roles a given product will
/// later drop, so that the policy layer has something complete to filter.
/// </para>
/// </summary>
public static class PdfFinalStructureProjection
{
    public const int SchemaVersion = 1;

    public static PdfFinalStructure Project(
        string sourceDocumentSha256,
        IReadOnlyList<PdfValidatedStructure> structures,
        IReadOnlyList<PdfHierarchyFactAudit> facts)
    {
        var factById = facts
            .GroupBy(fact => fact.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ordered = structures
            .Where(structure => factById.ContainsKey(structure.SourceId))
            .OrderBy(structure => factById[structure.SourceId].SourceOrder)
            .ToArray();
        var emittedIds = ordered.Select(structure => structure.SourceId).ToHashSet(StringComparer.Ordinal);

        var headings = new List<PdfFinalHeading>(ordered.Length);
        foreach (var structure in ordered)
        {
            var fact = factById[structure.SourceId];
            var (level, levelReason) = ResolveLevel(fact);
            var (parentId, parentReason) = ResolveParent(structure, factById, emittedIds);
            headings.Add(new PdfFinalHeading(
                fact.FactId.Length > 0 ? fact.FactId : fact.Id,
                fact.Id,
                fact.Page,
                fact.SourceOrder,
                new PdfFinalHeadingSpan(fact.HeadingSpan.Start, fact.HeadingSpan.End),
                fact.HeadingText,
                structure.DomainRole.ToString(),
                structure.StructuralScope,
                structure.Decision,
                level,
                parentId,
                Status(level, parentId),
                levelReason,
                parentReason,
                "validated"));
        }

        return new PdfFinalStructure(
            sourceDocumentSha256,
            PdfHierarchyFactHash.OfText(string.Join('\n', ordered.Select(structure =>
                string.Join('|', structure.SourceId, structure.Level, structure.ParentId ?? "-",
                    structure.ParentResolution, structure.Decision)))),
            PdfHierarchyFactHash.OfText(string.Join('\n', headings.Select(heading =>
                string.Join('|', heading.Id, heading.Level?.ToString() ?? "-", heading.ParentId ?? "-",
                    heading.HierarchyStatus)))),
            new PdfFinalStructureCounters(
                structures.Count,
                headings.Count,
                structures.Count - headings.Count,
                headings.Count(heading => heading.Level is not null),
                headings.Count(heading => heading.ParentId is not null),
                headings.Count(heading => heading.HierarchyStatus == "unresolved")),
            headings);
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
        return (factById[parent].FactId.Length > 0 ? factById[parent].FactId : parent, null);
    }

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
    [property: JsonPropertyName("levelResolved")] int LevelResolved,
    [property: JsonPropertyName("parentResolved")] int ParentResolved,
    [property: JsonPropertyName("fullyUnresolved")] int FullyUnresolved);

public sealed record PdfFinalHeading(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceFactId")] string SourceFactId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceOrder")] int SourceOrder,
    [property: JsonPropertyName("headingSpan")] PdfFinalHeadingSpan HeadingSpan,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("validationDecision")] string ValidationDecision,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("hierarchyStatus")] string HierarchyStatus,
    [property: JsonPropertyName("levelReason")] string? LevelReason,
    [property: JsonPropertyName("parentReason")] string? ParentReason,
    [property: JsonPropertyName("authority")] string Authority);

/// <summary>End is exclusive, matching the validated span the pipeline enforces.</summary>
public sealed record PdfFinalHeadingSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);
