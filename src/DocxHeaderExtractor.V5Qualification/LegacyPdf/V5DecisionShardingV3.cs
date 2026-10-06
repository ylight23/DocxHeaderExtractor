using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Stable mapping from a shard-local decision to the original source-pack subject.</summary>
public sealed record V5OriginalOwnedDecisionCoordinateV3(
    int OriginalOwnedOrdinal,
    int ShardOrdinal,
    int ShardLocalDecisionOrdinal,
    string SourceAlias);

/// <summary>
/// One provider-free execution unit derived from an existing resource-bounded source pack. The
/// original pack remains the visibility universe; only subject ownership is partitioned.
/// </summary>
public sealed record V5ShardedDecisionRequestV3(
    string OriginalPackId,
    string OriginalSemanticRequestHash,
    int OriginalOwnedCount,
    int CandidateMaxOwnedPerShard,
    int ShardOrdinal,
    IReadOnlyList<V5OriginalOwnedDecisionCoordinateV3> Coordinates,
    V5PackedDecisionRequestV3 Request);

/// <summary>
/// Deterministically partitions only v3 subject ownership. Every shard receives the original pack's
/// full visibility universe: its selected subjects plus every other original owned item and halo as
/// context-only evidence in original source order.
/// </summary>
public static class V5DecisionShardingV3
{
    public const string PolicyId = "v5-subject-ownership-sharding-1.0";

    public static IReadOnlyList<V5ShardedDecisionRequestV3> Shard(
        V5PackedDecisionRequestV3 original,
        DocumentTaskContract contract,
        V5ProviderEnvelope providerEnvelope,
        int maxOwnedPerShard)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        if (maxOwnedPerShard < 1) throw new ArgumentOutOfRangeException(nameof(maxOwnedPerShard));

        var orderedOwned = original.Packet.SubjectEvidence
            .OrderBy(node => node.SourceOrdinal).ThenBy(node => node.SourceAlias, StringComparer.Ordinal).ToArray();
        var orderedVisible = original.Packet.SubjectEvidence.Concat(original.Packet.ContextOnlyEvidence)
            .OrderBy(node => node.SourceOrdinal).ThenBy(node => node.SourceAlias, StringComparer.Ordinal).ToArray();
        if (orderedOwned.Length != original.OwnedAliases.Count || orderedVisible.Length != original.VisibleAliases.Count ||
            orderedOwned.Select(node => node.SourceAlias).Distinct(StringComparer.Ordinal).Count() != orderedOwned.Length ||
            orderedVisible.Select(node => node.SourceAlias).Distinct(StringComparer.Ordinal).Count() != orderedVisible.Length ||
            !orderedOwned.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal)
                .SetEquals(original.OwnedAliases) ||
            !orderedVisible.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal)
                .SetEquals(original.VisibleAliases))
            throw new InvalidOperationException("decision-sharding-original-pack-identity-invalid");
        if (orderedOwned.Length == 0) throw new InvalidOperationException("decision-sharding-empty-owned-pack");

        var shardCount = (int)Math.Ceiling(orderedOwned.Length / (double)maxOwnedPerShard);
        var shards = new List<V5ShardedDecisionRequestV3>(shardCount);
        for (var shardOrdinal = 0; shardOrdinal < shardCount; shardOrdinal++)
        {
            var owned = orderedOwned.Skip(shardOrdinal * maxOwnedPerShard).Take(maxOwnedPerShard).ToArray();
            var ownedAliases = owned.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
            var contextOnly = orderedVisible.Where(node => !ownedAliases.Contains(node.SourceAlias)).ToArray();
            var packet = new V5SemanticDecisionRequestPacketV3(owned, contextOnly,
                original.Packet.OpenOrConflictedClaims, original.Packet.RetrievedEvidence,
                original.Packet.LayoutEvidence, original.Packet.VisualEvidence);
            var request = V5SemanticDecisionComposerV3.Compose(contract, packet);
            var maxCompletionTokens = V5SemanticCompletionBudget.Compute(owned.Length, orderedVisible.Length,
                request.Utf8Bytes, V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
            var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, maxCompletionTokens, providerEnvelope);
            var packed = new V5PackedDecisionRequestV3(
                $"{original.PackId}:SUBJECT_SHARD_{shardOrdinal + 1:D3}_OF_{shardCount:D3}",
                owned.Select(node => node.SourceAlias).ToArray(),
                orderedVisible.Select(node => node.SourceAlias).ToArray(),
                packet, request, maxCompletionTokens, body.Hash, body.Bytes, body.PayloadBytes);
            var coordinates = owned.Select((node, localOrdinal) => new V5OriginalOwnedDecisionCoordinateV3(
                Array.FindIndex(orderedOwned, candidate => candidate.SourceAlias == node.SourceAlias),
                shardOrdinal, localOrdinal, node.SourceAlias)).ToArray();
            shards.Add(new V5ShardedDecisionRequestV3(original.PackId, original.Request.RequestHash, orderedOwned.Length,
                maxOwnedPerShard, shardOrdinal, coordinates, packed));
        }

        ValidatePartition(original, shards);
        return shards;
    }

    /// <summary>Checks the partition before any execution: no alias may be lost, duplicated or hidden.</summary>
    public static void ValidatePartition(V5PackedDecisionRequestV3 original, IReadOnlyList<V5ShardedDecisionRequestV3> shards)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(shards);
        if (shards.Count == 0) throw new InvalidOperationException("decision-sharding-no-shards");
        var originalOwned = original.Packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        var originalVisible = original.Packet.SubjectEvidence.Concat(original.Packet.ContextOnlyEvidence)
            .Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        var coordinates = shards.SelectMany(shard => shard.Coordinates).ToArray();
        if (coordinates.Select(coordinate => coordinate.SourceAlias).Distinct(StringComparer.Ordinal).Count() != coordinates.Length ||
            !coordinates.Select(coordinate => coordinate.SourceAlias).ToHashSet(StringComparer.Ordinal).SetEquals(originalOwned) ||
            coordinates.Select(coordinate => coordinate.OriginalOwnedOrdinal).Distinct().Count() != coordinates.Length ||
            !coordinates.Select(coordinate => coordinate.OriginalOwnedOrdinal).Order().SequenceEqual(Enumerable.Range(0, originalOwned.Count)))
            throw new InvalidOperationException("decision-sharding-ownership-partition-invalid");

        foreach (var shard in shards)
        {
            if (shard.OriginalPackId != original.PackId || shard.OriginalSemanticRequestHash != original.Request.RequestHash ||
                shard.OriginalOwnedCount != originalOwned.Count || shard.Request.OwnedAliases.Count != shard.Coordinates.Count ||
                shard.Request.OwnedAliases.Count > shard.CandidateMaxOwnedPerShard ||
                !shard.Coordinates.Select(coordinate => coordinate.SourceAlias).SequenceEqual(shard.Request.OwnedAliases) ||
                shard.Coordinates.Select(coordinate => coordinate.ShardLocalDecisionOrdinal).Order()
                    .SequenceEqual(Enumerable.Range(0, shard.Coordinates.Count)) == false ||
                shard.Coordinates.Any(coordinate => coordinate.ShardOrdinal != shard.ShardOrdinal))
                throw new InvalidOperationException("decision-sharding-coordinate-invalid");
            var shardVisible = shard.Request.Packet.SubjectEvidence.Concat(shard.Request.Packet.ContextOnlyEvidence)
                .Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
            if (!shardVisible.SetEquals(originalVisible) ||
                shard.Request.Packet.ContextOnlyEvidence.Any(node => shard.Request.OwnedAliases.Contains(node.SourceAlias, StringComparer.Ordinal)))
                throw new InvalidOperationException("decision-sharding-visibility-invalid");
        }
    }

    /// <summary>
    /// Binds with original-pack identity, not a shard-local request hash or decision index. This is
    /// the only binder entry point intended for a future sharded execution lane.
    /// </summary>
    public static V5DecisionBindingResultV3 Bind(
        V5ShardedDecisionRequestV3 shard,
        V5SemanticDecisionResponseV3 response,
        DocumentTaskContract contract,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        ArgumentNullException.ThrowIfNull(shard);
        var scope = ClaimBindingScope.Create(shard.Request.OwnedAliases, shard.Request.VisibleAliases);
        return V5SemanticDecisionContractV3.Bind(shard.OriginalSemanticRequestHash, response, contract,
            shard.Request.Packet.SubjectEvidence, shard.Request.Packet.ContextOnlyEvidence, atoms, scope,
            shard.Coordinates.Select(coordinate => coordinate.OriginalOwnedOrdinal).ToArray());
    }
}
