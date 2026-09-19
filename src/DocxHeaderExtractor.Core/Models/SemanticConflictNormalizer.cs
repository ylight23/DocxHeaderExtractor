using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>A semantic disagreement for one parser-owned physical source occurrence.</summary>
public sealed record SemanticProposalConflict(
    string PhysicalSourceIdentity,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    string Classification = "OCCURRENCE_OR_BINDING_CONFLICT");

/// <summary>
/// A disagreement that does not prevent the harness from identifying and binding the physical
/// occurrence. Contested semantic attributes remain unresolved for a later adjudication stage.
/// </summary>
public sealed record SemanticAttributeConflict(
    string PhysicalSourceIdentity,
    IReadOnlyList<CanonicalSemanticProposal> Alternatives,
    CanonicalSemanticProposal BindingConsensus,
    IReadOnlyDictionary<string, IReadOnlyList<string?>> ContestedFields,
    string Classification = "SEMANTIC_ATTRIBUTE_CONFLICT");

/// <summary>Result of deterministic pre-binder semantic proposal normalization.</summary>
public sealed record SemanticConflictNormalizationResult(
    IReadOnlyList<CanonicalSemanticProposal> NormalizedProposals,
    IReadOnlyList<SemanticProposalConflict> Conflicts,
    int SemanticProposalInputCount,
    int SemanticProposalNormalizedCount,
    int ExactSemanticDuplicatesCollapsed,
    int SemanticConflictProposalCount)
{
    /// <summary>Attribute conflicts are retained for semantic adjudication.</summary>
    public IReadOnlyList<SemanticAttributeConflict> AttributeConflicts { get; init; } = [];

    /// <summary>Proposals safe to pass to the deterministic binder.</summary>
    public IReadOnlyList<CanonicalSemanticProposal> BindingReadyProposals { get; init; } = [];
}

/// <summary>
/// Detects semantic disagreements for the same physical source occurrence before binding.
/// It never selects a semantic winner: identical proposals collapse, conflicting alternatives
/// are withheld from the binder for a future adjudicator.
/// </summary>
public static class SemanticConflictNormalizer
{
    public static SemanticConflictNormalizationResult Normalize(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);

        var byAlias = aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        var grouped = proposals
            .Select((proposal, index) => new Candidate(
                proposal,
                index,
                PhysicalIdentity(proposal, byAlias),
                SemanticFingerprint(proposal)))
            .GroupBy(item => item.PhysicalIdentity, StringComparer.Ordinal)
            .OrderBy(group => group.Min(item => SourceOrder(item.Proposal, byAlias)))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        var normalized = new List<CanonicalSemanticProposal>();
        var conflicts = new List<SemanticProposalConflict>();
        var attributeConflicts = new List<SemanticAttributeConflict>();
        var collapsed = 0;
        var conflictProposalCount = 0;
        foreach (var group in grouped)
        {
            var alternatives = group
                .GroupBy(item => item.SemanticFingerprint, StringComparer.Ordinal)
                .Select(item => item.OrderBy(candidate => JsonSerializer.Serialize(candidate.Proposal), StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.InputIndex)
                    .First())
                .OrderBy(item => item.SemanticFingerprint, StringComparer.Ordinal)
                .ThenBy(item => JsonSerializer.Serialize(item.Proposal), StringComparer.Ordinal)
                .ToArray();

            if (alternatives.Length == 1)
            {
                collapsed += group.Count() - 1;
                normalized.Add(alternatives[0].Proposal);
                continue;
            }

            conflictProposalCount += group.Count();
            var alternativeProposals = alternatives.Select(item => item.Proposal).ToArray();
            if (CanBindConsensus(alternativeProposals))
            {
                attributeConflicts.Add(new(
                    group.Key,
                    alternativeProposals,
                    CreateBindingConsensus(alternativeProposals),
                    ContestedFields(alternativeProposals)));
            }
            else
            {
                conflicts.Add(new(group.Key, alternativeProposals));
            }
        }

        var result = new SemanticConflictNormalizationResult(
            normalized,
            conflicts,
            proposals.Count,
            normalized.Count,
            collapsed,
            conflictProposalCount)
        {
            AttributeConflicts = attributeConflicts,
            // A consensus with null contested attributes is not a semantic winner. It is
            // therefore never passed to the binder by default. The production control plane
            // may append the original frozen alternative selected by adjudication.
            BindingReadyProposals = normalized
                .OrderBy(item => SourceOrder(item, byAlias))
                .ThenBy(item => PhysicalIdentity(item, byAlias), StringComparer.Ordinal)
                .ToArray(),
        };
        return result;
    }

    private static bool CanBindConsensus(IReadOnlyList<CanonicalSemanticProposal> alternatives)
    {
        if (alternatives.Count == 0 || alternatives.Any(item => !item.IsHeading)) return false;
        var first = alternatives[0];
        var bindingFingerprint = BindingFingerprint(first);
        return alternatives.All(item => string.Equals(bindingFingerprint, BindingFingerprint(item), StringComparison.Ordinal));
    }

    private static CanonicalSemanticProposal CreateBindingConsensus(IReadOnlyList<CanonicalSemanticProposal> alternatives)
    {
        var first = alternatives[0];
        return first with
        {
            SemanticRole = ConsensusValue(alternatives.Select(item => item.SemanticRole)),
            StructuralType = ConsensusValue(alternatives.Select(item => item.StructuralType)),
            Scope = ConsensusValue(alternatives.Select(item => item.Scope)),
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string?>> ContestedFields(
        IReadOnlyList<CanonicalSemanticProposal> alternatives)
    {
        var fields = new Dictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
        AddIfContested(fields, "semanticRole", alternatives.Select(item => item.SemanticRole));
        AddIfContested(fields, "structuralType", alternatives.Select(item => item.StructuralType));
        AddIfContested(fields, "scope", alternatives.Select(item => item.Scope));
        return fields;
    }

    private static void AddIfContested(
        IDictionary<string, IReadOnlyList<string?>> fields,
        string name,
        IEnumerable<string?> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1) fields[name] = distinct;
    }

    private static string? ConsensusValue(IEnumerable<string?> values) =>
        values.Distinct(StringComparer.Ordinal).Count() == 1 ? values.First() : null;

    private static int SourceOrder(CanonicalSemanticProposal proposal, IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var names = ResolveAliases(proposal);
        return names.Select(name => aliases.TryGetValue(name, out var alias) ? alias.SourceOrdinal : int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
    }

    private static string PhysicalIdentity(CanonicalSemanticProposal proposal, IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var parts = ResolveAliases(proposal).Select(name =>
        {
            if (!aliases.TryGetValue(name, out var alias))
                return $"unknown-alias:{name}";
            return $"source:{alias.SourceId}:{alias.SourceSpan.Start}:{alias.SourceSpan.End}";
        });
        return string.Join("|", parts);
    }

    private static string BindingFingerprint(CanonicalSemanticProposal proposal) =>
        JsonSerializer.Serialize(new
        {
            sourceAliases = ResolveAliases(proposal),
            selectionMode = proposal.SelectionMode ?? CanonicalSemanticSelectionMode.VerbatimText,
            isHeading = proposal.IsHeading,
            occurrence = proposal.Occurrence,
            verbatimText = proposal.SelectionMode == CanonicalSemanticSelectionMode.WholeAlias ? null : proposal.VerbatimText,
            verbatimParts = proposal.SelectionMode == CanonicalSemanticSelectionMode.WholeAlias ? [] : (proposal.VerbatimParts ?? []),
        });

    private static string SemanticFingerprint(CanonicalSemanticProposal proposal)
    {
        var mode = proposal.SelectionMode ?? CanonicalSemanticSelectionMode.VerbatimText;
        var includeSourceText = !string.Equals(mode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal);
        return JsonSerializer.Serialize(new
        {
            sourceAliases = ResolveAliases(proposal),
            selectionMode = mode,
            isHeading = proposal.IsHeading,
            semanticRole = proposal.SemanticRole,
            structuralType = proposal.StructuralType,
            scope = proposal.Scope,
            occurrence = proposal.Occurrence,
            // Hierarchy/relationship reconstruction is a later graph stage, not semantic
            // adjudication. In particular, a different level must not create a role conflict.
            relationHints = SemanticRelationHints(proposal.RelationHints),
            verbatimText = includeSourceText ? proposal.VerbatimText : null,
            verbatimParts = includeSourceText ? (proposal.VerbatimParts ?? []) : [],
        });
    }

    private static IReadOnlyList<string> ResolveAliases(CanonicalSemanticProposal proposal) =>
        proposal.SourceAliases is { Count: > 0 } ? proposal.SourceAliases : [proposal.SourceAlias];

    private static IReadOnlyList<string> SemanticRelationHints(IReadOnlyList<string>? hints) =>
        (hints ?? [])
            .Where(hint => !IsHierarchyHint(hint))
            .ToArray();

    private static bool IsHierarchyHint(string hint) =>
        hint.StartsWith("level:", StringComparison.OrdinalIgnoreCase) ||
        hint.StartsWith("parent-node:", StringComparison.OrdinalIgnoreCase) ||
        hint.StartsWith("sibling-node:", StringComparison.OrdinalIgnoreCase);

    private sealed record Candidate(
        CanonicalSemanticProposal Proposal,
        int InputIndex,
        string PhysicalIdentity,
        string SemanticFingerprint);
}
