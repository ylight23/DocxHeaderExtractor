using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Parser-owned address used by the semantic contract. The model may name this address, but it
/// cannot manufacture coordinates or source text outside the address.
/// </summary>
public sealed record SemanticSourceAlias(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceSpan")] StructuralSpan SourceSpan)
{
    public bool Contains(StructuralSpan span) =>
        span.Start >= 0 && span.End <= Text.Length && span.IsValidFor(Text);
}

public static class SemanticSourceAliasCatalog
{
    /// <summary>Builds stable aliases from the canonical source catalog, in source order.</summary>
    public static IReadOnlyList<SemanticSourceAlias> FromCatalog(DocumentSourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .ThenBy(unit => unit.SourceId, StringComparer.Ordinal)
            .Select((unit, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", unit.SourceId, unit.SourceOrdinal, unit.Text, unit.SourceSpan))
            .ToArray();
    }
}

/// <summary>Model-facing semantic output. There are intentionally no numeric offsets.</summary>
public sealed record CanonicalSemanticProposal(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("isHeading")] bool IsHeading,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText,
    [property: JsonPropertyName("verbatimParts")] IReadOnlyList<string>? VerbatimParts = null,
    [property: JsonPropertyName("semanticRole")] string? SemanticRole = null,
    [property: JsonPropertyName("structuralType")] string? StructuralType = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("relationHints")] IReadOnlyList<string>? RelationHints = null);

public static class CanonicalSemanticContract
{
    public const string ProtocolVersion = "a99-canonical-semantic-vnext-v1";

    /// <summary>Schema used by the model. Numeric coordinates are deliberately not accepted.</summary>
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        sourceAlias = new { type = "string", minLength = 1 },
                        isHeading = new { type = "boolean" },
                        verbatimText = new { type = "string" },
                        verbatimParts = new { type = "array", items = new { type = "string" } },
                        semanticRole = new { type = "string" },
                        structuralType = new { type = "string" },
                        scope = new { type = "string" },
                        relationHints = new { type = "array", items = new { type = "string" } },
                    },
                    required = new[] { "sourceAlias", "isHeading" },
                },
            },
        },
        required = new[] { "headings" },
    };
}

public enum CanonicalSemanticBindingStatus
{
    Bound,
    NonHeadingIgnored,
    UnknownAlias,
    MissingVerbatimText,
    NonVerbatimText,
    AmbiguousBinding,
    OutOfOwnedSegment,
    DuplicateBinding,
}

public sealed record CanonicalSemanticBindingObservation(
    int Ordinal,
    CanonicalSemanticProposal Proposal,
    CanonicalSemanticBindingStatus Status,
    string? SourceId,
    int? Start,
    int? End,
    string? Reason);

public sealed record CanonicalSemanticBoundHeading(
    string Alias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    string SemanticRole,
    string StructuralType,
    string Scope,
    IReadOnlyList<string> RelationHints,
    int Start,
    int End,
    bool IsHeading = true);

/// <summary>
/// Exact source binder for vNext. .NET string indexes are UTF-16 code-unit offsets; no other
/// coordinate system is introduced here. It never reads Gold and never invents a span.
/// </summary>
public static class CanonicalSemanticExactBinder
{
    public static IReadOnlyList<CanonicalSemanticBoundHeading> Bind(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        out IReadOnlyList<CanonicalSemanticBindingObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);
        var byAlias = aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        var result = new List<CanonicalSemanticBoundHeading>();
        var audit = new List<CanonicalSemanticBindingObservation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            if (!proposal.IsHeading)
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.NonHeadingIgnored, null, null, null, null));
                continue;
            }
            if (!byAlias.TryGetValue(proposal.SourceAlias, out var alias))
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.UnknownAlias, null, null, null, "UNKNOWN_ALIAS"));
                continue;
            }
            var text = ComposeVerbatimText(proposal);
            if (string.IsNullOrEmpty(text))
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.MissingVerbatimText, alias.SourceId, null, null, "MISSING_VERBATIM_TEXT"));
                continue;
            }
            var positions = FindExact(alias.Text, text);
            if (positions.Count == 0)
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.NonVerbatimText, alias.SourceId, null, null, "NON_VERBATIM_TEXT"));
                continue;
            }
            if (positions.Count > 1)
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.AmbiguousBinding, alias.SourceId, null, null, "AMBIGUOUS_BINDING"));
                continue;
            }
            var start = positions[0] + alias.SourceSpan.Start;
            var end = start + text.Length;
            var span = new StructuralSpan(start, end);
            if (!alias.Contains(new StructuralSpan(positions[0], positions[0] + text.Length)))
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.OutOfOwnedSegment, alias.SourceId, start, end, "OUT_OF_OWNED_SEGMENT"));
                continue;
            }
            var identity = $"{alias.SourceId}:{start}:{end}";
            if (!seen.Add(identity))
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.DuplicateBinding, alias.SourceId, start, end, "DUPLICATE_BINDING"));
                continue;
            }
            audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.Bound, alias.SourceId, start, end, null));
            result.Add(new(alias.Alias, alias.SourceId, alias.SourceOrdinal, text,
                proposal.SemanticRole ?? "OTHER_STRUCTURAL_LABEL",
                proposal.StructuralType ?? "Heading",
                proposal.Scope ?? "document_body",
                proposal.RelationHints ?? [], start, end));
        }
        observations = new ReadOnlyCollection<CanonicalSemanticBindingObservation>(audit);
        return new ReadOnlyCollection<CanonicalSemanticBoundHeading>(result);
    }

    private static string? ComposeVerbatimText(CanonicalSemanticProposal proposal)
    {
        if (!string.IsNullOrEmpty(proposal.VerbatimText)) return proposal.VerbatimText;
        return proposal.VerbatimParts is { Count: > 0 }
            ? string.Concat(proposal.VerbatimParts)
            : null;
    }

    private static IReadOnlyList<int> FindExact(string source, string text)
    {
        var positions = new List<int>();
        var offset = 0;
        while (offset <= source.Length - text.Length)
        {
            var position = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (position < 0) break;
            positions.Add(position);
            offset = position + Math.Max(1, text.Length);
        }
        return positions;
    }
}

public sealed record CanonicalSemanticGraphOccurrence(
    string OccurrenceId,
    string SemanticNodeId,
    string SourceAlias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    string SemanticRole,
    string StructuralType,
    string Scope,
    int Start,
    int End,
    string OccurrenceKind,
    string? ParentOccurrenceId);

public sealed record CanonicalSemanticGraph(
    IReadOnlyList<CanonicalSemanticGraphOccurrence> Occurrences,
    IReadOnlyList<CanonicalSemanticGraphOccurrence> OutlineProjection);

/// <summary>
/// Global semantic resolution after binding. Repeated display headings remain occurrences even
/// when they share one semantic node; the outline projection is the only collapsing step.
/// </summary>
public static class CanonicalSemanticGraphResolver
{
    public static CanonicalSemanticGraph Resolve(IReadOnlyList<CanonicalSemanticBoundHeading> bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ThenBy(item => item.Alias, StringComparer.Ordinal).ToArray();
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var occurrences = new List<CanonicalSemanticGraphOccurrence>(ordered.Length);
        foreach (var (item, index) in ordered.Select((item, index) => (item, index)))
        {
            var nodeKey = $"{item.Text}\u001f{item.SemanticRole}\u001f{item.StructuralType}";
            if (!nodes.TryGetValue(nodeKey, out var nodeId))
            {
                nodeId = $"semantic-node:{nodes.Count + 1:0000}";
                nodes[nodeKey] = nodeId;
            }
            var occurrenceId = $"semantic-occurrence:{index + 1:0000}";
            var kind = occurrences.Any(existing => existing.SemanticNodeId == nodeId)
                ? (item.Scope.Contains("continuation", StringComparison.OrdinalIgnoreCase) ? "CONTINUATION" : "REPEAT")
                : "PRIMARY";
            occurrences.Add(new(occurrenceId, nodeId, item.Alias, item.SourceId, item.SourceOrdinal,
                item.Text, item.SemanticRole, item.StructuralType, item.Scope, item.Start, item.End, kind, null));
        }
        var projection = occurrences
            .GroupBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start)
            .ToArray();
        return new CanonicalSemanticGraph(occurrences, projection);
    }
}

public static class CanonicalSemanticSourceHash
{
    public static string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
