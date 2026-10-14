using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

/// <summary>
/// One deterministic request partition produced before semantic inference. Packing is an
/// execution concern: it does not change the coordinate profile, the response contract, or the
/// meaning assigned to any source atom.
/// </summary>
internal sealed record SemanticEvidencePack(
    string PackId,
    int Ordinal,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Owned,
    IReadOnlyList<CanonicalSemanticSourceEvidence> Visible);

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
/// The packing policies, one per lane. There is no shared default: each lane names the policy it
/// sends under, so a lane can never silently inherit another lane's request shape.
/// </summary>
internal static class SemanticEvidencePackingPolicies
{
    internal const int OwnedPerPack = 120;
    internal const int VisibleMargin = 20;

    internal const string FixedOwnedCount120Id = "FIXED_OWNED_COUNT_120";

    /// <summary>The DOCX lane's policy.</summary>
    internal static ISemanticEvidencePackingPolicy FixedOwnedCount120 { get; } =
        new FixedOwnedCount120PackingPolicy();

    private sealed class FixedOwnedCount120PackingPolicy : ISemanticEvidencePackingPolicy
    {
        public string PolicyId => FixedOwnedCount120Id;
        public string PolicyVersion => "a99-fixed-owned-count-120-v1";

        public IReadOnlyList<SemanticEvidencePack> BuildPacks(
            IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
            IReadOnlyDictionary<string, string>? layoutBlockBySourceId) =>
            Partition(evidence, SegmentBoundaries(evidence.Count), PolicyId);
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
                    visible));
            }
        }

        AssertConservation(evidence, packs);

        return packs;
    }

    private static void AssertConservation(
        IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
        IReadOnlyList<SemanticEvidencePack> packs)
    {
        var ownedItems = packs.SelectMany(pack => pack.Owned).ToArray();
        if (ownedItems.Length != evidence.Count ||
            ownedItems.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
            !ownedItems.Select(item => item.SourceId).SequenceEqual(
                evidence.Select(item => item.SourceId), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("SEMANTIC_PACKING_SOURCE_CONSERVATION_FAILED");
        }

    }
}
