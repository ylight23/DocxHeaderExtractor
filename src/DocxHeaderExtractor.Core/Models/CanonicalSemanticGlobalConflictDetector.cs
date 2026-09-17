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
            // Parent hints are intentionally not part of local semantic normalization. A whole
            // document can nevertheless expose that one physical semantic interpretation claims
            // two incompatible parents. Reopen only the existing alternatives.
            var alternatives = group
                .Where(item => item.RelationHints?.Any(hint =>
                    hint.StartsWith("parent-node:", StringComparison.Ordinal)) == true)
                .GroupBy(SemanticFingerprint, StringComparer.Ordinal)
                .Select(item => item.First())
                .ToArray();
            var parents = alternatives
                .SelectMany(item => item.RelationHints ?? [])
                .Where(hint => hint.StartsWith("parent-node:", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (parents.Length <= 1 || localConflictKeys.Contains(group.Key)) continue;

            var conflictId = StableId("parent-relation-contradiction", group.Key,
                string.Join("\n", alternatives.Select(SemanticFingerprint).OrderBy(item => item, StringComparer.Ordinal)));
            result.Add(new CanonicalSemanticGlobalConflict(
                conflictId,
                [group.Key],
                alternatives,
                [
                    "whole-document parent relation contradiction",
                    $"physicalIdentity={group.Key}",
                    $"parentHints={string.Join(",", parents.OrderBy(item => item, StringComparer.Ordinal))}",
                ],
                [SourceContext(group.Key, byAlias)])
            {
                ConflictKind = "PARENT_RELATION_CONTRADICTION",
                RelationEvidence = parents,
            });
        }

        return result;
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
