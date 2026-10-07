using System.Collections.ObjectModel;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Binding;

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
            if (proposal.SelectionMode is not null &&
                !string.Equals(proposal.SelectionMode, CanonicalSemanticSelectionMode.VerbatimText, StringComparison.Ordinal) &&
                !string.Equals(proposal.SelectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal))
            {
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.NonVerbatimText, alias.SourceId, null, null, "INVALID_SELECTION_MODE"));
                continue;
            }
            if (string.Equals(proposal.SelectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal))
            {
                if (aliasesForProposal.Count != 1)
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.NonVerbatimText, alias.SourceId, null, null, "WHOLE_ALIAS_REQUIRES_ONE_ALIAS"));
                    continue;
                }
                if (!byAlias.TryGetValue(aliasesForProposal[0], out var wholeAlias))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.UnknownAlias, null, null, null, "UNKNOWN_ALIAS"));
                    continue;
                }
                if (ownedAliases is not null && !ownedAliases.Contains(wholeAlias.Alias))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.OutOfOwnedSegment, wholeAlias.SourceId, null, null, "OUT_OF_OWNED_SEGMENT"));
                    continue;
                }
                var wholeStart = wholeAlias.SourceSpan.Start;
                var wholeEnd = wholeStart + wholeAlias.Text.Length;
                var wholeIdentity = $"{wholeAlias.SourceId}:{wholeStart}:{wholeEnd}";
                if (!seen.Add(wholeIdentity))
                {
                    audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.DuplicateBinding, wholeAlias.SourceId, wholeStart, wholeEnd, "DUPLICATE_BINDING"));
                    continue;
                }
                var wholePart = new CanonicalSemanticBoundPart(wholeAlias.Alias, wholeAlias.SourceId, wholeAlias.SourceOrdinal, wholeAlias.Text, wholeStart, wholeEnd);
                audit.Add(new(index, proposal, CanonicalSemanticBindingStatus.Bound, wholeAlias.SourceId, wholeStart, wholeEnd, null));
                result.Add(new(wholeAlias.Alias, wholeAlias.SourceId, wholeAlias.SourceOrdinal, wholeAlias.Text,
                    proposal.SemanticRole ?? "OTHER_STRUCTURAL_LABEL", proposal.StructuralType ?? "Heading",
                    proposal.Scope ?? "document_body", proposal.RelationHints ?? [], wholeStart, wholeEnd)
                {
                    Parts = [wholePart]
                });
                continue;
            }
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
        var matchingPositions = positions.Where(position =>
        {
            var left = proposal.LeftExactContext is null ||
                (position >= proposal.LeftExactContext.Length &&
                 source.Substring(position - proposal.LeftExactContext.Length, proposal.LeftExactContext.Length) == proposal.LeftExactContext);
            var end = position + text.Length;
            var right = proposal.RightExactContext is null ||
                (end + proposal.RightExactContext.Length <= source.Length &&
                 source.Substring(end, proposal.RightExactContext.Length) == proposal.RightExactContext);
            return left && right;
        }).ToArray();
        return matchingPositions.Length == 1 ? matchingPositions[0] : null;
    }
}
