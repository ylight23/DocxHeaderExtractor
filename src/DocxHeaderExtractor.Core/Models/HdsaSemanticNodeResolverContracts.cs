using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Source-owned evidence supplied to a semantic-node resolver.</summary>
public sealed record HdsaSemanticNodeSourceOccurrence(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("styleEvidence")] string? StyleEvidence = null,
    [property: JsonPropertyName("layoutEvidence")] string? LayoutEvidence = null,
    // Compatibility/poison field only. The resolver must not consume legacy hierarchy metadata.
    [property: JsonPropertyName("legacyHints")] IReadOnlyList<string>? LegacyHints = null);

public sealed record HdsaSemanticNodeResolutionInput(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<HdsaSemanticNodeSourceOccurrence> Occurrences,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed = false);

public sealed record HdsaSemanticNodePrediction(
    [property: JsonPropertyName("predictedSemanticNodeId")] string PredictedSemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("mergeEvidence")] string MergeEvidence,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed);

public sealed record HdsaSemanticNodeResolutionResult(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("predictions")] IReadOnlyList<HdsaSemanticNodePrediction> Predictions,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed);

public enum HdsaSemanticIdentityRelationType
{
    [JsonStringEnumMemberName("SAME_SEMANTIC_REPEAT")]
    SameSemanticRepeat,

    [JsonStringEnumMemberName("CONTINUATION_OF")]
    ContinuationOf,
}

/// <summary>
/// A relation assertion supplied by a future semantic reasoner or a source-owned deterministic
/// evidence builder. The resolver validates it; it does not infer relations from text alone.
/// </summary>
public sealed record HdsaSemanticIdentityRelationProposal(
    [property: JsonPropertyName("fromOccurrenceId")] string FromOccurrenceId,
    [property: JsonPropertyName("toOccurrenceId")] string ToOccurrenceId,
    [property: JsonPropertyName("relation")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaSemanticIdentityRelationType Relation,
    [property: JsonPropertyName("parserEvidenceHash")] string? ParserEvidenceHash,
    [property: JsonPropertyName("structuralBoundaryCompatible")] bool StructuralBoundaryCompatible);

public sealed record HdsaSemanticIdentityRelation(
    [property: JsonPropertyName("fromOccurrenceId")] string FromOccurrenceId,
    [property: JsonPropertyName("toOccurrenceId")] string ToOccurrenceId,
    [property: JsonPropertyName("relation")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaSemanticIdentityRelationType Relation,
    [property: JsonPropertyName("parserEvidenceHash")] string ParserEvidenceHash,
    [property: JsonPropertyName("structuralBoundaryCompatible")] bool StructuralBoundaryCompatible);

public sealed record HdsaSemanticIdentityRelationRejection(
    [property: JsonPropertyName("proposal")] HdsaSemanticIdentityRelationProposal Proposal,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HdsaSemanticNodeResolutionV3Result(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("predictions")] IReadOnlyList<HdsaSemanticNodePrediction> Predictions,
    [property: JsonPropertyName("acceptedRelations")] IReadOnlyList<HdsaSemanticIdentityRelation> AcceptedRelations,
    [property: JsonPropertyName("rejectedRelations")] IReadOnlyList<HdsaSemanticIdentityRelationRejection> RejectedRelations,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Deterministic no-merge baseline for the resolver boundary. It intentionally does not infer
/// repeat/continuation identity; a future semantic resolver may replace it behind this contract.
/// Legacy role/level/parent hints are never read.
/// </summary>
public static class HdsaSemanticNodeResolver
{
    public const string Version = "identity-no-merge-v1";

    public static HdsaSemanticNodeResolutionResult Resolve(HdsaSemanticNodeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: semantic-node resolver input is marked as Gold-derived.");

        var occurrences = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!seen.Add(occurrence.OccurrenceId)) throw new InvalidDataException($"DUPLICATE_OCCURRENCE_ID:{occurrence.OccurrenceId}");
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException($"INVALID_DOCUMENT_ORDER:{occurrence.OccurrenceId}");
            if (occurrence.Text is null) throw new InvalidDataException($"NULL_OCCURRENCE_TEXT:{occurrence.OccurrenceId}");
        }

        var predictions = occurrences.Select(occurrence =>
        {
            var nodeId = "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.OccurrenceId))).ToLowerInvariant()[..16];
            return new HdsaSemanticNodePrediction(
                nodeId,
                [occurrence.OccurrenceId],
                occurrence.Text,
                "IDENTITY_ONLY_NO_MERGE",
                Version,
                false);
        }).ToArray();

        return new(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, Version, false);
    }
}

/// <summary>
/// Conservative evidence-based resolver. It only merges adjacent source occurrences when
/// normalized text and non-empty parser-owned style/layout evidence are exactly compatible.
/// Ambiguous or weakly evidenced occurrences remain separate.
/// </summary>
public static class HdsaSemanticNodeResolverV2
{
    public const string Version = "exact-safe-equivalence-v2";

    public static HdsaSemanticNodeResolutionResult Resolve(HdsaSemanticNodeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: semantic-node resolver input is marked as Gold-derived.");
        ArgumentNullException.ThrowIfNull(input.Occurrences);

        var occurrences = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!seen.Add(occurrence.OccurrenceId)) throw new InvalidDataException($"DUPLICATE_OCCURRENCE_ID:{occurrence.OccurrenceId}");
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException($"INVALID_DOCUMENT_ORDER:{occurrence.OccurrenceId}");
            if (occurrence.Text is null) throw new InvalidDataException($"NULL_OCCURRENCE_TEXT:{occurrence.OccurrenceId}");
        }

        var predictions = new List<HdsaSemanticNodePrediction>();
        var index = 0;
        while (index < occurrences.Length)
        {
            var first = occurrences[index];
            var members = new List<HdsaSemanticNodeSourceOccurrence> { first };
            var next = index + 1;
            while (next < occurrences.Length && IsSafeEquivalent(members[^1], occurrences[next]))
            {
                members.Add(occurrences[next]);
                next++;
            }

            var normalizedText = NormalizeText(first.Text);
            var nodeKey = string.Join('\u001f', first.OccurrenceId, normalizedText,
                first.StyleEvidence ?? string.Empty, first.LayoutEvidence ?? string.Empty);
            var nodeId = "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nodeKey))).ToLowerInvariant()[..16];
            var evidence = members.Count == 1
                ? "IDENTITY_ONLY_NO_SAFE_MERGE"
                : "NORMALIZED_TEXT+ADJACENT+EXACT_STYLE+EXACT_LAYOUT";

            predictions.Add(new HdsaSemanticNodePrediction(
                nodeId,
                members.Select(item => item.OccurrenceId).ToArray(),
                first.Text,
                evidence,
                Version,
                false));
            index = next;
        }

        return new(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, Version, false);
    }

    private static bool IsSafeEquivalent(
        HdsaSemanticNodeSourceOccurrence previous,
        HdsaSemanticNodeSourceOccurrence current)
    {
        return current.DocumentOrder == previous.DocumentOrder + 1
            && string.Equals(NormalizeText(previous.Text), NormalizeText(current.Text), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.StyleEvidence)
            && string.Equals(previous.StyleEvidence, current.StyleEvidence, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.LayoutEvidence)
            && string.Equals(previous.LayoutEvidence, current.LayoutEvidence, StringComparison.Ordinal);
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Relation-aware semantic identity resolver. Repeat and continuation are distinct input
/// relations, and neither is inferred from adjacency, style, layout, or equal text alone.
/// Only explicitly evidenced, validated relations can collapse source occurrences.
/// </summary>
public static class HdsaSemanticNodeResolverV3
{
    public const string Version = "repeat-continuation-evidence-v3";

    public static HdsaSemanticNodeResolutionV3Result Resolve(
        HdsaSemanticNodeResolutionInput input,
        IEnumerable<HdsaSemanticIdentityRelationProposal> relationProposals)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(relationProposals);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: semantic-node resolver input is marked as Gold-derived.");
        ArgumentNullException.ThrowIfNull(input.Occurrences);

        var occurrences = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        ValidateOccurrences(occurrences);
        var byId = occurrences.ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
        var rejected = new List<HdsaSemanticIdentityRelationRejection>();
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<HdsaSemanticIdentityRelation>();
        var seen = new HashSet<(string From, string To, HdsaSemanticIdentityRelationType Relation)>();

        foreach (var proposal in relationProposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.FromOccurrenceId) || string.IsNullOrWhiteSpace(proposal.ToOccurrenceId))
            {
                Reject(proposal, "EMPTY_ENDPOINT", rejected, errors);
                continue;
            }
            if (!byId.ContainsKey(proposal.FromOccurrenceId) || !byId.ContainsKey(proposal.ToOccurrenceId))
            {
                Reject(proposal, "UNKNOWN_OCCURRENCE", rejected, errors);
                continue;
            }
            if (string.Equals(proposal.FromOccurrenceId, proposal.ToOccurrenceId, StringComparison.Ordinal))
            {
                Reject(proposal, "SELF_RELATION", rejected, errors);
                continue;
            }
            if (string.IsNullOrWhiteSpace(proposal.ParserEvidenceHash))
            {
                Reject(proposal, "MISSING_PARSER_EVIDENCE", rejected, errors);
                continue;
            }
            if (!proposal.StructuralBoundaryCompatible)
            {
                Reject(proposal, "INCOMPATIBLE_STRUCTURAL_BOUNDARY", rejected, errors);
                continue;
            }

            var from = byId[proposal.FromOccurrenceId];
            var to = byId[proposal.ToOccurrenceId];
            if (proposal.Relation == HdsaSemanticIdentityRelationType.SameSemanticRepeat)
            {
                var endpoints = CompareDocumentOrder(from, to) <= 0
                    ? (from.OccurrenceId, to.OccurrenceId)
                    : (to.OccurrenceId, from.OccurrenceId);
                if (!seen.Add((endpoints.Item1, endpoints.Item2, proposal.Relation)))
                {
                    Reject(proposal, "DUPLICATE_RELATION", rejected, errors);
                    continue;
                }

                if (!string.Equals(NormalizeText(from.Text), NormalizeText(to.Text), StringComparison.Ordinal))
                {
                    Reject(proposal, "REPEAT_TEXT_NOT_EQUIVALENT", rejected, errors);
                    continue;
                }

                accepted.Add(new(endpoints.Item1, endpoints.Item2, proposal.Relation,
                    proposal.ParserEvidenceHash, true));
                continue;
            }

            if (!seen.Add((from.OccurrenceId, to.OccurrenceId, proposal.Relation)))
            {
                Reject(proposal, "DUPLICATE_RELATION", rejected, errors);
                continue;
            }

            accepted.Add(new(from.OccurrenceId, to.OccurrenceId, proposal.Relation,
                proposal.ParserEvidenceHash, true));
        }

        RejectContinuationConflicts(accepted, rejected, errors);
        RejectContinuationCycles(accepted, rejected, errors);
        RejectForwardContinuations(byId, accepted, rejected, errors);

        var union = new DisjointSet(occurrences.Select(item => item.OccurrenceId));
        foreach (var relation in accepted)
        {
            union.Union(relation.FromOccurrenceId, relation.ToOccurrenceId);
        }

        var groups = occurrences
            .GroupBy(item => union.Find(item.OccurrenceId), StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0].DocumentOrder)
            .ThenBy(group => group[0].OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        var predictions = groups.Select(group =>
        {
            var memberIds = group.Select(item => item.OccurrenceId).ToArray();
            var nodeKey = string.Join('\u001f', memberIds);
            var nodeId = "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nodeKey))).ToLowerInvariant()[..16];
            var groupRelations = accepted
                .Where(relation => memberIds.Contains(relation.FromOccurrenceId, StringComparer.Ordinal)
                    && memberIds.Contains(relation.ToOccurrenceId, StringComparer.Ordinal))
                .Select(relation => RelationName(relation.Relation))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var evidence = groupRelations.Length == 0
                ? "IDENTITY_ONLY_NO_RELATION"
                : "EXPLICIT_RELATIONS:" + string.Join(',', groupRelations);
            return new HdsaSemanticNodePrediction(nodeId, memberIds, group[0].Text, evidence, Version, false);
        }).ToArray();

        return new(input.SourceSha256, input.PreprocessingSnapshotHash, predictions,
            accepted.OrderBy(item => item.Relation).ThenBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
                .ThenBy(item => item.ToOccurrenceId, StringComparer.Ordinal).ToArray(),
            rejected.OrderBy(item => item.Reason, StringComparer.Ordinal).ThenBy(item => item.Proposal.FromOccurrenceId, StringComparer.Ordinal).ToArray(),
            errors.Order(StringComparer.Ordinal).ToArray(), Version, false);
    }

    private static string RelationName(HdsaSemanticIdentityRelationType relation) => relation switch
    {
        HdsaSemanticIdentityRelationType.SameSemanticRepeat => "SAME_SEMANTIC_REPEAT",
        HdsaSemanticIdentityRelationType.ContinuationOf => "CONTINUATION_OF",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };

    private static void ValidateOccurrences(IReadOnlyList<HdsaSemanticNodeSourceOccurrence> occurrences)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!seen.Add(occurrence.OccurrenceId)) throw new InvalidDataException($"DUPLICATE_OCCURRENCE_ID:{occurrence.OccurrenceId}");
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException($"INVALID_DOCUMENT_ORDER:{occurrence.OccurrenceId}");
            if (occurrence.Text is null) throw new InvalidDataException($"NULL_OCCURRENCE_TEXT:{occurrence.OccurrenceId}");
        }
    }

    private static void RejectContinuationConflicts(
        ICollection<HdsaSemanticIdentityRelation> accepted,
        ICollection<HdsaSemanticIdentityRelationRejection> rejected,
        ISet<string> errors)
    {
        var conflicts = accepted
            .Where(item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf)
            .GroupBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.ToOccurrenceId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .ToArray();
        foreach (var conflict in conflicts)
        {
            foreach (var relation in conflict.ToArray())
            {
                accepted.Remove(relation);
                rejected.Add(new(
                    new(relation.FromOccurrenceId, relation.ToOccurrenceId, relation.Relation,
                        relation.ParserEvidenceHash, relation.StructuralBoundaryCompatible),
                    "MULTIPLE_CONTINUATION_PARENTS"));
            }
            errors.Add("MULTIPLE_CONTINUATION_PARENTS");
        }
    }

    private static void RejectContinuationCycles(
        ICollection<HdsaSemanticIdentityRelation> accepted,
        ICollection<HdsaSemanticIdentityRelationRejection> rejected,
        ISet<string> errors)
    {
        var continuation = accepted.Where(item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf).ToArray();
        var edges = continuation.GroupBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ToOccurrenceId).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cycleNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edges.Keys) DetectCycle(node, edges, visiting, visited, cycleNodes);
        if (cycleNodes.Count == 0) return;

        foreach (var relation in continuation.Where(item => cycleNodes.Contains(item.FromOccurrenceId) && cycleNodes.Contains(item.ToOccurrenceId)).ToArray())
        {
            accepted.Remove(relation);
            rejected.Add(new(
                new(relation.FromOccurrenceId, relation.ToOccurrenceId, relation.Relation,
                    relation.ParserEvidenceHash, relation.StructuralBoundaryCompatible),
                "CONTINUATION_CYCLE"));
        }
        errors.Add("CONTINUATION_CYCLE");
    }

    private static void RejectForwardContinuations(
        IReadOnlyDictionary<string, HdsaSemanticNodeSourceOccurrence> byId,
        ICollection<HdsaSemanticIdentityRelation> accepted,
        ICollection<HdsaSemanticIdentityRelationRejection> rejected,
        ISet<string> errors)
    {
        foreach (var relation in accepted
            .Where(item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf)
            .Where(item => byId[item.FromOccurrenceId].DocumentOrder <= byId[item.ToOccurrenceId].DocumentOrder)
            .ToArray())
        {
            accepted.Remove(relation);
            rejected.Add(new(
                new(relation.FromOccurrenceId, relation.ToOccurrenceId, relation.Relation,
                    relation.ParserEvidenceHash, relation.StructuralBoundaryCompatible),
                "CONTINUATION_MUST_POINT_BACKWARD"));
            errors.Add("CONTINUATION_MUST_POINT_BACKWARD");
        }
    }

    private static bool DetectCycle(
        string node,
        IReadOnlyDictionary<string, string[]> edges,
        ISet<string> visiting,
        ISet<string> visited,
        ISet<string> cycleNodes)
    {
        if (visiting.Contains(node))
        {
            cycleNodes.Add(node);
            return true;
        }
        if (!visited.Add(node)) return false;
        visiting.Add(node);
        var found = false;
        if (edges.TryGetValue(node, out var targets))
        {
            foreach (var target in targets)
            {
                if (DetectCycle(target, edges, visiting, visited, cycleNodes))
                {
                    cycleNodes.Add(node);
                    found = true;
                }
            }
        }
        visiting.Remove(node);
        return found;
    }

    private static int CompareDocumentOrder(
        HdsaSemanticNodeSourceOccurrence left,
        HdsaSemanticNodeSourceOccurrence right) =>
        left.DocumentOrder != right.DocumentOrder
            ? left.DocumentOrder.CompareTo(right.DocumentOrder)
            : string.CompareOrdinal(left.OccurrenceId, right.OccurrenceId);

    private static void Reject(
        HdsaSemanticIdentityRelationProposal proposal,
        string reason,
        ICollection<HdsaSemanticIdentityRelationRejection> rejected,
        ISet<string> errors) {
        rejected.Add(new(proposal, reason));
        errors.Add(reason);
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent;

        public DisjointSet(IEnumerable<string> items) => _parent = items.ToDictionary(item => item, StringComparer.Ordinal);

        public string Find(string item)
        {
            if (!string.Equals(_parent[item], item, StringComparison.Ordinal)) _parent[item] = Find(_parent[item]);
            return _parent[item];
        }

        public void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (!string.Equals(leftRoot, rightRoot, StringComparison.Ordinal)) _parent[rightRoot] = leftRoot;
        }
    }
}
