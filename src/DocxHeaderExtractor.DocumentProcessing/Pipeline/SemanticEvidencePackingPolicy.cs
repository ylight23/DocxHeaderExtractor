using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// One deterministic request partition produced before semantic inference. Packing is an
/// execution concern: it does not change the coordinate profile, the response contract, or the
/// meaning assigned to any source atom.
/// </summary>
internal sealed record SemanticEvidencePack(
    string PackId,
    int Ordinal,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Owned,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Visible)
{
    public string RegionKey { get; init; } = string.Empty;
}

/// <summary>The execution capability that partitions source evidence into bounded model requests.</summary>
internal interface ISemanticEvidencePackingPolicy
{
    string PolicyId { get; }
    string PolicyVersion { get; }

    IReadOnlyList<SemanticEvidencePack> BuildPacks(
        IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
        IReadOnlyDictionary<string, string>? layoutBlockBySourceId);
}

/// <summary>
/// The two approved packing policies. The default is deliberately the historical fixed-120
/// policy, so adding the seam cannot silently activate an experiment.
/// </summary>
internal static class SemanticEvidencePackingPolicies
{
    internal const int OwnedPerPack = 120;
    internal const int VisibleMargin = 20;
    internal const int MinimumCoherentRun = VisibleMargin;

    internal const string FixedOwnedCount120Id = "FIXED_OWNED_COUNT_120";
    internal const string CoherentRegionSegmentationV1Id = "COHERENT_REGION_SEGMENTATION_V1";

    internal static ISemanticEvidencePackingPolicy FixedOwnedCount120 { get; } =
        new FixedOwnedCount120PackingPolicy();

    internal static ISemanticEvidencePackingPolicy CoherentRegionSegmentationV1 { get; } =
        new CoherentRegionSegmentationV1PackingPolicy();

    internal static ISemanticEvidencePackingPolicy Default => FixedOwnedCount120;

    internal static ISemanticEvidencePackingPolicy Resolve(string policyId) => policyId switch
    {
        FixedOwnedCount120Id => FixedOwnedCount120,
        CoherentRegionSegmentationV1Id => CoherentRegionSegmentationV1,
        _ => throw new InvalidOperationException($"Unknown semantic evidence packing policy: {policyId}"),
    };

    private sealed class FixedOwnedCount120PackingPolicy : ISemanticEvidencePackingPolicy
    {
        public string PolicyId => FixedOwnedCount120Id;
        public string PolicyVersion => "a99-fixed-owned-count-120-v1";

        public IReadOnlyList<SemanticEvidencePack> BuildPacks(
            IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
            IReadOnlyDictionary<string, string>? layoutBlockBySourceId) =>
            Partition(evidence, SegmentBoundaries(evidence.Count), PolicyId);
    }

    private sealed class CoherentRegionSegmentationV1PackingPolicy : ISemanticEvidencePackingPolicy
    {
        public string PolicyId => CoherentRegionSegmentationV1Id;
        public string PolicyVersion => "a99-coherent-region-segmentation-v1";

        public IReadOnlyList<SemanticEvidencePack> BuildPacks(
            IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
            IReadOnlyDictionary<string, string>? layoutBlockBySourceId)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (layoutBlockBySourceId is null)
                throw new InvalidOperationException("COHERENT_PACKING_LAYOUT_METADATA_REQUIRED");

            var blockCounts = evidence
                .Select(item => layoutBlockBySourceId.TryGetValue(item.SourceId, out var block)
                    ? block
                    : throw new InvalidOperationException("COHERENT_PACKING_LAYOUT_BLOCK_MISSING"))
                .GroupBy(block => block, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var keys = evidence.Select(item =>
            {
                var block = layoutBlockBySourceId[item.SourceId];
                var rowRegime = blockCounts[block] == 1;
                var annex = item.StructuralScope.StartsWith("appendix", StringComparison.Ordinal);
                return $"{(rowRegime ? "ROW" : "FLOW")}/{(annex ? "ANNEX" : "MAIN")}";
            }).ToArray();

            var segments = new List<(int Start, int End, string Key)>();
            for (var index = 0; index < keys.Length;)
            {
                var end = index;
                while (end < keys.Length && keys[end] == keys[index])
                    end++;

                if (segments.Count > 0 && end - index < MinimumCoherentRun)
                    segments[^1] = segments[^1] with { End = end - 1 };
                else
                    segments.Add((index, end - 1, keys[index]));

                index = end;
            }

            return Partition(evidence, segments, PolicyId);
        }
    }

    private static IReadOnlyList<(int Start, int End, string Key)> SegmentBoundaries(int count)
    {
        var segments = new List<(int Start, int End, string Key)>();
        for (var start = 0; start < count; start += OwnedPerPack)
            segments.Add((start, Math.Min(count - 1, start + OwnedPerPack - 1), "FIXED"));
        return segments;
    }

    private static IReadOnlyList<SemanticEvidencePack> Partition(
        IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
        IReadOnlyList<(int Start, int End, string Key)> segments,
        string policyId)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var packs = new List<SemanticEvidencePack>();
        foreach (var segment in segments)
        {
            for (var start = segment.Start; start <= segment.End; start += OwnedPerPack)
            {
                var end = Math.Min(segment.End, start + OwnedPerPack - 1);
                var owned = evidence.Skip(start).Take(end - start + 1).ToArray();
                var from = Math.Max(0, start - VisibleMargin);
                var to = Math.Min(evidence.Count, end + VisibleMargin + 1);
                var visible = evidence.Skip(from).Take(to - from).ToArray();
                packs.Add(new SemanticEvidencePack(
                    $"{policyId}:PACK_{packs.Count + 1:000}",
                    packs.Count + 1,
                    owned,
                    visible)
                {
                    RegionKey = segment.Key,
                });
            }
        }

        var ownedItems = packs.SelectMany(pack => pack.Owned).ToArray();
        if (ownedItems.Length != evidence.Count ||
            ownedItems.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
            !ownedItems.Select(item => item.SourceId).SequenceEqual(
                evidence.Select(item => item.SourceId), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("SEMANTIC_PACKING_SOURCE_CONSERVATION_FAILED");
        }

        return packs;
    }
}
