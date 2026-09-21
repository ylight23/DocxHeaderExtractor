using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>What the competing proposals actually disagreed about.</summary>
public static class SemanticConflictKind
{
    public const string SemanticRole = "SEMANTIC_ROLE";
    public const string StructuralType = "STRUCTURAL_TYPE";
    public const string Scope = "SCOPE";
    public const string RelationHint = "RELATION_HINT";
    public const string ParentRelation = "PARENT_RELATION";
    public const string RepeatContinuation = "REPEAT_CONTINUATION";

    /// <summary>The disagreement blocks identification or binding, not just an attribute.</summary>
    public const string OccurrenceOrBinding = "OCCURRENCE_OR_BINDING";
}

/// <summary>One disagreement, kept with enough detail to tell whether anything was lost to it.</summary>
public sealed record SemanticConflictRecord(
    [property: JsonPropertyName("sourceIdentity")] string SourceIdentity,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("alternatives")] IReadOnlyList<string> Alternatives,
    [property: JsonPropertyName("droppedBeforeBinding")] bool DroppedBeforeBinding,
    [property: JsonPropertyName("missingFromCanonicalOutput")] bool MissingFromCanonicalOutput);

/// <summary>
/// A census of semantic disagreement, taken so the decision to wire an adjudication model is made
/// from measured conflict rather than from the assumption that conflict exists. A conflict that
/// nothing resolves is not automatically harmless: an occurrence-or-binding conflict is withheld
/// from binding, so it silently removes the occurrence from the canonical output.
/// </summary>
public sealed record SemanticConflictCensus(
    [property: JsonPropertyName("localConflictCount")] int LocalConflictCount,
    [property: JsonPropertyName("unresolvedLocalConflictCount")] int UnresolvedLocalConflictCount,
    [property: JsonPropertyName("globalConflictCount")] int GlobalConflictCount,
    [property: JsonPropertyName("unresolvedGlobalConflictCount")] int UnresolvedGlobalConflictCount,
    [property: JsonPropertyName("occurrencesLostToConflict")] int OccurrencesLostToConflict,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<SemanticConflictRecord> Conflicts)
{
    public static readonly SemanticConflictCensus Empty = new(0, 0, 0, 0, 0, []);

    public bool AnyConflict => LocalConflictCount > 0 || GlobalConflictCount > 0;

    public static SemanticConflictCensus Take(
        SemanticConflictNormalizationResult normalization,
        IReadOnlyList<CanonicalSemanticGlobalConflict> globalConflicts,
        int adjudicationCalls,
        int globalReopenCalls,
        IReadOnlySet<string> boundSourceIds)
    {
        ArgumentNullException.ThrowIfNull(normalization);
        ArgumentNullException.ThrowIfNull(globalConflicts);
        ArgumentNullException.ThrowIfNull(boundSourceIds);

        var records = new List<SemanticConflictRecord>();
        var lost = 0;

        foreach (var conflict in normalization.Conflicts)
        {
            // Withheld from BindingReadyProposals: unless an adjudicator picks one, the occurrence
            // never reaches the binder at all.
            var dropped = adjudicationCalls == 0;
            var missing = dropped && !boundSourceIds.Contains(SourceIdOf(conflict.PhysicalSourceIdentity));
            if (missing) lost++;
            records.Add(new SemanticConflictRecord(
                conflict.PhysicalSourceIdentity,
                SemanticConflictKind.OccurrenceOrBinding,
                "normalization",
                Describe(conflict.Alternatives),
                dropped,
                missing));
        }

        foreach (var conflict in normalization.AttributeConflicts)
        {
            // Binding still happens on the consensus, so the occurrence survives; only the
            // contested attribute stays undecided.
            foreach (var field in conflict.ContestedFields)
                records.Add(new SemanticConflictRecord(
                    conflict.PhysicalSourceIdentity,
                    KindOfField(field.Key, field.Value),
                    "normalization",
                    field.Value.Select(value => value ?? "(none)").ToArray(),
                    DroppedBeforeBinding: false,
                    MissingFromCanonicalOutput: false));
        }

        foreach (var conflict in globalConflicts)
            records.Add(new SemanticConflictRecord(
                conflict.ConflictId,
                SemanticConflictKind.OccurrenceOrBinding,
                "global-resolution",
                [],
                DroppedBeforeBinding: false,
                MissingFromCanonicalOutput: false));

        var localCount = normalization.Conflicts.Count + normalization.AttributeConflicts.Count;
        return new SemanticConflictCensus(
            localCount,
            adjudicationCalls == 0 ? localCount : Math.Max(0, localCount - adjudicationCalls),
            globalConflicts.Count,
            globalReopenCalls == 0 ? globalConflicts.Count : Math.Max(0, globalConflicts.Count - globalReopenCalls),
            lost,
            records);
    }

    private static string KindOfField(string field, IReadOnlyList<string?> values)
    {
        if (field.Contains("role", StringComparison.OrdinalIgnoreCase)) return SemanticConflictKind.SemanticRole;
        if (field.Contains("structuralType", StringComparison.OrdinalIgnoreCase)) return SemanticConflictKind.StructuralType;
        if (field.Contains("scope", StringComparison.OrdinalIgnoreCase)) return SemanticConflictKind.Scope;
        if (!field.Contains("relation", StringComparison.OrdinalIgnoreCase)) return field.ToUpperInvariant();
        // Relation hints carry two different questions in one field; separate them so a placement
        // disagreement is never reported as an identity disagreement.
        var text = string.Join(' ', values.Select(value => value ?? string.Empty));
        if (text.Contains("same-node:", StringComparison.Ordinal) ||
            text.Contains("continuation-node:", StringComparison.Ordinal))
            return SemanticConflictKind.RepeatContinuation;
        return text.Contains("parent-node:", StringComparison.Ordinal)
            ? SemanticConflictKind.ParentRelation
            : SemanticConflictKind.RelationHint;
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<CanonicalSemanticProposal> alternatives) =>
        alternatives
            .Select(item => $"{item.SourceAlias}|{item.SemanticRole}|{item.StructuralType}|{item.Scope}")
            .ToArray();

    /// <summary>Physical identity is "sourceId:start:end"; the census reports loss per source.</summary>
    private static string SourceIdOf(string physicalIdentity)
    {
        var separator = physicalIdentity.LastIndexOf(':');
        if (separator <= 0) return physicalIdentity;
        var previous = physicalIdentity.LastIndexOf(':', separator - 1);
        return previous <= 0 ? physicalIdentity : physicalIdentity[..previous];
    }
}
