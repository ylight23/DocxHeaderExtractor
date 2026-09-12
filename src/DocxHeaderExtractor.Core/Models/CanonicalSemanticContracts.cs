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
    [property: JsonPropertyName("relationHints")] IReadOnlyList<string>? RelationHints = null,
    [property: JsonPropertyName("sourceAliases")] IReadOnlyList<string>? SourceAliases = null,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);

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
                        sourceAliases = new { type = "array", minItems = 1, items = new { type = "string", minLength = 1 } },
                        isHeading = new { type = "boolean" },
                        verbatimText = new { type = "string" },
                        verbatimParts = new { type = "array", items = new { type = "string" } },
                        semanticRole = new { type = "string" },
                        structuralType = new { type = "string" },
                        scope = new { type = "string" },
                        relationHints = new { type = "array", items = new { type = "string" } },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
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
    bool IsHeading = true)
{
    /// <summary>All exact source pieces for a composite heading, in model-declared order.</summary>
    [JsonPropertyName("parts")]
    public IReadOnlyList<CanonicalSemanticBoundPart> Parts { get; init; } = [];
}

public sealed record CanonicalSemanticBoundPart(
    string Alias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    int Start,
    int End);

/// <summary>
/// Exact source binder for vNext. .NET string indexes are UTF-16 code-unit offsets; no other
/// coordinate system is introduced here. It never reads Gold and never invents a span.
/// </summary>
public static class CanonicalSemanticExactBinder
{
    public static IReadOnlyList<CanonicalSemanticBoundHeading> Bind(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        out IReadOnlyList<CanonicalSemanticBindingObservation> observations) =>
        Bind(proposals, aliases, null, out observations);

    public static IReadOnlyList<CanonicalSemanticBoundHeading> Bind(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases,
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
            var aliasesForProposal = ResolveAliases(proposal);
            var parts = ComposeVerbatimParts(proposal);
            if (parts.Count == 0)
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.MissingVerbatimText, alias.SourceId, null, null, "MISSING_VERBATIM_TEXT"));
                continue;
            }

            if (aliasesForProposal.Count != parts.Count)
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.NonVerbatimText, alias.SourceId, null, null, "NON_VERBATIM_TEXT"));
                continue;
            }

            var boundParts = new List<CanonicalSemanticBoundPart>(parts.Count);
            var failed = false;
            foreach (var (partText, partIndex) in parts.Select((part, partIndex) => (part, partIndex)))
            {
                if (!byAlias.TryGetValue(aliasesForProposal[partIndex], out var partAlias))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.UnknownAlias, null, null, null, "UNKNOWN_ALIAS"));
                    failed = true;
                    break;
                }
                if (ownedAliases is not null && !ownedAliases.Contains(partAlias.Alias))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.OutOfOwnedSegment, partAlias.SourceId, null, null, "OUT_OF_OWNED_SEGMENT"));
                    failed = true;
                    break;
                }
                var positions = FindExact(partAlias.Text, partText);
                var selectedPosition = SelectExact(partAlias.Text, partText, positions, proposal);
                if (selectedPosition is null)
                {
                    audit.Add(new(index, proposal,
                        positions.Count == 0
                            ? CanonicalSemanticBindingStatus.NonVerbatimText
                            : CanonicalSemanticBindingStatus.AmbiguousBinding,
                        partAlias.SourceId, null, null,
                        positions.Count == 0 ? "NON_VERBATIM_TEXT" : "AMBIGUOUS_BINDING"));
                    failed = true;
                    break;
                }
                var partStart = selectedPosition.Value + partAlias.SourceSpan.Start;
                var partEnd = partStart + partText.Length;
                if (!partAlias.Contains(new StructuralSpan(selectedPosition.Value, selectedPosition.Value + partText.Length)))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.OutOfOwnedSegment, partAlias.SourceId, partStart, partEnd, "OUT_OF_OWNED_SEGMENT"));
                    failed = true;
                    break;
                }
                var identity = $"{partAlias.SourceId}:{partStart}:{partEnd}";
                if (!seen.Add(identity))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.DuplicateBinding, partAlias.SourceId, partStart, partEnd, "DUPLICATE_BINDING"));
                    failed = true;
                    break;
                }
                boundParts.Add(new(partAlias.Alias, partAlias.SourceId, partAlias.SourceOrdinal, partText, partStart, partEnd));
            }
            if (failed) continue;
            var first = boundParts[0];
            var text = string.Concat(boundParts.Select(part => part.Text));
            audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.Bound, first.SourceId, first.Start, boundParts[^1].End, null));
            result.Add(new(alias.Alias, alias.SourceId, alias.SourceOrdinal, text,
                proposal.SemanticRole ?? "OTHER_STRUCTURAL_LABEL",
                proposal.StructuralType ?? "Heading",
                proposal.Scope ?? "document_body",
                proposal.RelationHints ?? [], first.Start, boundParts[^1].End)
            {
                Parts = boundParts
            });
        }
        observations = new ReadOnlyCollection<CanonicalSemanticBindingObservation>(audit);
        return new ReadOnlyCollection<CanonicalSemanticBoundHeading>(result);
    }

    private static IReadOnlyList<string> ComposeVerbatimParts(CanonicalSemanticProposal proposal)
    {
        if (proposal.VerbatimParts is { Count: > 0 }) return proposal.VerbatimParts;
        return !string.IsNullOrEmpty(proposal.VerbatimText) ? [proposal.VerbatimText] : [];
    }

    private static IReadOnlyList<string> ResolveAliases(CanonicalSemanticProposal proposal) =>
        proposal.SourceAliases is { Count: > 0 } ? proposal.SourceAliases : [proposal.SourceAlias];

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

    private static int? SelectExact(
        string source, string text, IReadOnlyList<int> positions, CanonicalSemanticProposal proposal)
    {
        if (positions.Count == 0) return null;
        if (positions.Count == 1 && proposal.Occurrence is null &&
            proposal.LeftExactContext is null && proposal.RightExactContext is null)
            return positions[0];
        if (proposal.Occurrence is { } ordinal)
            return ordinal >= 1 && ordinal <= positions.Count ? positions[ordinal - 1] : null;
        if (proposal.LeftExactContext is null && proposal.RightExactContext is null)
            return null;
        return positions.Where(position =>
        {
            var left = proposal.LeftExactContext is null ||
                (position >= proposal.LeftExactContext.Length &&
                 source.Substring(position - proposal.LeftExactContext.Length, proposal.LeftExactContext.Length) == proposal.LeftExactContext);
            var end = position + text.Length;
            var right = proposal.RightExactContext is null ||
                (end + proposal.RightExactContext.Length <= source.Length &&
                 source.Substring(end, proposal.RightExactContext.Length) == proposal.RightExactContext);
            return left && right;
        }).Select(position => (int?)position).FirstOrDefault();
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
    string? ParentOccurrenceId)
{
    /// <summary>Optional structural facts resolved after semantic binding; absent means unresolved.</summary>
    public int? Level { get; init; }

    /// <summary>Coordinate authority for this canonical occurrence. Text occurrences use
    /// UTF-16; visual-only occurrences use a visual-region identity.</summary>
    public string BindingMode { get; init; } = "TEXT_UTF16";
}

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
            var parentNodeHint = item.RelationHints.FirstOrDefault(hint => hint.StartsWith("parent-node:", StringComparison.Ordinal));
            var parentNodeId = parentNodeHint is null ? null : parentNodeHint["parent-node:".Length..];
            var parentOccurrence = parentNodeId is null
                ? null
                : occurrences.LastOrDefault(existing => existing.SemanticNodeId == parentNodeId)?.OccurrenceId;
            var levelHint = item.RelationHints.FirstOrDefault(hint => hint.StartsWith("level:", StringComparison.Ordinal));
            var level = levelHint is not null && int.TryParse(levelHint["level:".Length..], out var parsedLevel)
                ? parsedLevel : (int?)null;
            occurrences.Add(new(occurrenceId, nodeId, item.Alias, item.SourceId, item.SourceOrdinal,
                item.Text, item.SemanticRole, item.StructuralType, item.Scope, item.Start, item.End, kind, parentOccurrence)
            {
                Level = level
            });
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
