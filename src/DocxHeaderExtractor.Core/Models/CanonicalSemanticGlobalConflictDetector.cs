using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Finds deterministic contradictions that become visible only after the whole proposal set is
/// available. This class never selects a semantic winner; it emits frozen alternatives for the
/// bounded reopen coordinator.
/// </summary>
public static class CanonicalSemanticGlobalConflictDetector
{
    public static IReadOnlyList<CanonicalSemanticGlobalConflict> Detect(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<SemanticProposalConflict>? alreadyAdjudicatedConflicts = null)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);
        var byAlias = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        var localConflictKeys = (alreadyAdjudicatedConflicts ?? [])
            .Select(item => item.PhysicalSourceIdentity)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<CanonicalSemanticGlobalConflict>();
        foreach (var group in proposals
            .Where(item => item.SourceAlias is not null && byAlias.ContainsKey(item.SourceAlias))
            .GroupBy(item => PhysicalIdentity(item, byAlias), StringComparer.Ordinal))
        {
            var alternatives = group
                .GroupBy(SemanticFingerprint, StringComparer.Ordinal)
                .Select(item => item.First())
                .ToArray();
            if (alternatives.Length <= 1 || localConflictKeys.Contains(group.Key)) continue;

            var parentHints = ExplicitValues(alternatives, "parent-node:");
            if (parentHints.Count > 1)
            {
                result.Add(CreateConflict(
                    "PARENT_RELATION_CONTRADICTION", group.Key, alternatives,
                    "whole-document parent relation contradiction", parentHints, byAlias));
            }

            AddFieldConflict(result, "STRUCTURAL_TYPE_CONTRADICTION", "structuralType",
                alternatives, group.Key, item => item.StructuralType, byAlias);
            AddFieldConflict(result, "SEMANTIC_ROLE_CONTRADICTION", "semanticRole",
                alternatives, group.Key, item => item.SemanticRole, byAlias);
            AddFieldConflict(result, "SCOPE_CONTRADICTION", "scope",
                alternatives, group.Key, item => item.Scope, byAlias);

            var relationEvidence = RelationContradictions(alternatives);
            if (relationEvidence.Count > 0)
            {
                result.Add(CreateConflict(
                    "RELATION_HINT_CONTRADICTION", group.Key, alternatives,
                    "explicit relation hints are mutually incompatible", relationEvidence, byAlias));
            }
        }

        return result;
    }

    private static void AddFieldConflict(
        ICollection<CanonicalSemanticGlobalConflict> conflicts,
        string kind,
        string field,
        IReadOnlyList<CanonicalSemanticProposal> alternatives,
        string physicalIdentity,
        Func<CanonicalSemanticProposal, string?> value,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var values = alternatives.Select(value)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (values.Length <= 1) return;
        conflicts.Add(CreateConflict(
            kind, physicalIdentity, alternatives,
            $"same physical occurrence has incompatible {field} alternatives",
            values, aliases));
    }

    private static CanonicalSemanticGlobalConflict CreateConflict(
        string kind,
        string physicalIdentity,
        IReadOnlyList<CanonicalSemanticProposal> alternatives,
        string evidence,
        IReadOnlyList<string> relationEvidence,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var fingerprints = alternatives.Select(SemanticFingerprint)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var conflictId = StableId(kind, physicalIdentity, string.Join("\n", fingerprints));
        return new CanonicalSemanticGlobalConflict(
            conflictId,
            [physicalIdentity],
            alternatives,
            [evidence, $"physicalIdentity={physicalIdentity}"],
            [SourceContext(physicalIdentity, aliases)])
        {
            ConflictKind = kind,
            RelationEvidence = relationEvidence,
        };
    }

    private static IReadOnlyList<string> ExplicitValues(
        IReadOnlyList<CanonicalSemanticProposal> alternatives,
        string prefix) => alternatives
        .SelectMany(item => item.RelationHints ?? [])
        .Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Select(item => item[prefix.Length..])
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<string> RelationContradictions(
        IReadOnlyList<CanonicalSemanticProposal> alternatives)
    {
        var same = ExplicitValues(alternatives, "same-node:");
        var separate = ExplicitValues(alternatives, "separate-node:");
        var continuation = ExplicitValues(alternatives, "continuation-node:");
        var evidence = new List<string>();
        foreach (var value in same.Intersect(separate, StringComparer.Ordinal))
            evidence.Add($"same-node:{value} vs separate-node:{value}");
        foreach (var value in continuation.Intersect(separate, StringComparer.Ordinal))
            evidence.Add($"continuation-node:{value} vs separate-node:{value}");
        if (same.Count > 1)
            evidence.AddRange(same.Select(value => $"same-node:{value}"));
        if (continuation.Count > 1)
            evidence.AddRange(continuation.Select(value => $"continuation-node:{value}"));
        return evidence.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    public static string PhysicalIdentity(
        CanonicalSemanticProposal proposal,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        var names = proposal.SourceAliases is { Count: > 0 }
            ? proposal.SourceAliases
            : [proposal.SourceAlias];
        return string.Join("|", names.Select(name => aliases.TryGetValue(name, out var alias)
            ? $"{alias.SourceId}:{alias.SourceSpan.Start}:{alias.SourceSpan.End}"
            : $"unknown:{name}").OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string SourceContext(
        string physicalIdentity,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        _ = aliases;
        return string.IsNullOrWhiteSpace(physicalIdentity)
            ? "source-backed physical identity unavailable"
            : $"source-backed physical identity={physicalIdentity}";
    }

    private static string SemanticFingerprint(CanonicalSemanticProposal proposal) =>
        JsonSerializer.Serialize(new
        {
            proposal.SourceAlias,
            proposal.SourceAliases,
            proposal.IsHeading,
            proposal.VerbatimText,
            proposal.VerbatimParts,
            proposal.SemanticRole,
            proposal.StructuralType,
            proposal.Scope,
            proposal.Occurrence,
            proposal.RelationHints,
        });

    private static string StableId(string kind, params string[] parts) =>
        "GSC-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", new[] { kind }.Concat(parts))))).ToLowerInvariant()[..16];
}
