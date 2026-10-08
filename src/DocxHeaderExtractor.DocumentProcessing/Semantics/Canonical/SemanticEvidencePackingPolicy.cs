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
    internal const string ResourceBoundedSourcePackingV1Id = "RESOURCE_BOUNDED_SOURCE_PACKING_V1";

    internal sealed record ResourcePackingBudget(
        int MaxSerializedInputBytes,
        int MaxEstimatedInputTokens,
        int MaxVisibleAtoms,
        int MaxOwnedAtoms,
        int HaloAtoms,
        int ReservedCompletionTokens);

    /// <summary>
    /// P05, the budget every scored PDF V4 run was sent under (V4R2 P05 medium, T3B none):
    /// 90,000 serialized input bytes, 28,000 estimated input tokens, 128 visible atoms,
    /// 96 owned atoms, 8 halo atoms, 12,288 reserved completion tokens.
    /// </summary>
    internal static ResourcePackingBudget PdfP05Budget { get; } =
        new(90_000, 28_000, 128, 96, 8, 12_288);

    /// <summary>The DOCX lane's policy.</summary>
    internal static ISemanticEvidencePackingPolicy FixedOwnedCount120 { get; } =
        new FixedOwnedCount120PackingPolicy();

    /// <summary>The PDF lane's policy.</summary>
    internal static ISemanticEvidencePackingPolicy PdfResourceBoundedP05 { get; } =
        CreateResourceBounded(PdfP05Budget);

    /// <summary>A resource-bounded policy under another budget - for a named packing experiment only.</summary>
    internal static ISemanticEvidencePackingPolicy CreateResourceBounded(ResourcePackingBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaxSerializedInputBytes <= 0 || budget.MaxEstimatedInputTokens <= 0 ||
            budget.MaxVisibleAtoms <= 0 || budget.MaxOwnedAtoms <= 0 || budget.HaloAtoms < 0 ||
            budget.ReservedCompletionTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(budget));
        return new ResourceBoundedSourcePackingPolicy(budget);
    }

    private sealed class FixedOwnedCount120PackingPolicy : ISemanticEvidencePackingPolicy
    {
        public string PolicyId => FixedOwnedCount120Id;
        public string PolicyVersion => "a99-fixed-owned-count-120-v1";

        public IReadOnlyList<SemanticEvidencePack> BuildPacks(
            IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
            IReadOnlyDictionary<string, string>? layoutBlockBySourceId) =>
            Partition(evidence, SegmentBoundaries(evidence.Count), PolicyId);
    }

    /// <summary>
    /// The PDF packer.  Its stopping condition is serialized source-evidence bytes,
    /// estimated tokens, visible atoms and a reserved completion ceiling -- never a heading
    /// predicate, salience score, Gold fact, or a fixed owned-item count.  The estimator is
    /// intentionally conservative and deterministic; the preflight additionally records the
    /// bytes from the real request composer, which is the value a provider experiment must pin.
    /// </summary>
    private sealed class ResourceBoundedSourcePackingPolicy : ISemanticEvidencePackingPolicy
    {
        private const int StaticEnvelopeBytes = 24_000;
        private readonly ResourcePackingBudget _budget;

        public ResourceBoundedSourcePackingPolicy(ResourcePackingBudget budget) => _budget = budget;

        public string PolicyId => ResourceBoundedSourcePackingV1Id;
        public string PolicyVersion => "a99-resource-bounded-source-packing-v1";

        public IReadOnlyList<SemanticEvidencePack> BuildPacks(
            IReadOnlyList<CanonicalSemanticSourceEvidence> evidence,
            IReadOnlyDictionary<string, string>? layoutBlockBySourceId)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            var packs = new List<SemanticEvidencePack>();
            for (var start = 0; start < evidence.Count;)
            {
                var endExclusive = start;
                var ownedBytes = StaticEnvelopeBytes;
                while (endExclusive < evidence.Count)
                {
                    var nextSourceBytes = SourceWireBytes(evidence[endExclusive]);
                    var projectedOwnedCount = endExclusive - start + 1;
                    var visibleCount = Math.Min(evidence.Count, endExclusive + 1 + _budget.HaloAtoms) -
                        Math.Max(0, start - _budget.HaloAtoms);
                    var projectedBytes = ownedBytes + nextSourceBytes;
                    var projectedTokens = EstimateTokens(projectedBytes);
                    // A single exceptionally large atom is still owned once: rejecting it would
                    // violate exact-once conservation.  All following packs again obey bounds.
                    if (endExclusive > start &&
                        (projectedBytes > _budget.MaxSerializedInputBytes ||
                         projectedTokens + _budget.ReservedCompletionTokens > _budget.MaxEstimatedInputTokens ||
                         visibleCount > _budget.MaxVisibleAtoms ||
                         projectedOwnedCount > _budget.MaxOwnedAtoms))
                        break;

                    ownedBytes = projectedBytes;
                    endExclusive++;
                }

                var visibleFrom = Math.Max(0, start - _budget.HaloAtoms);
                var visibleTo = Math.Min(evidence.Count, endExclusive + _budget.HaloAtoms);
                var owned = evidence.Skip(start).Take(endExclusive - start).ToArray();
                var visible = evidence.Skip(visibleFrom).Take(visibleTo - visibleFrom).ToArray();
                packs.Add(new SemanticEvidencePack(
                    $"{PolicyId}:PACK_{packs.Count + 1:000}", packs.Count + 1, owned, visible));
                start = endExclusive;
            }

            AssertConservation(evidence, packs);
            return packs;
        }

        private static int SourceWireBytes(CanonicalSemanticSourceEvidence item)
        {
            // This is a serialization of observable source facts, not a semantic ranking feature.
            // It deliberately includes the raw source text verbatim and is stable across machines.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                alias = item.SourceAlias,
                text = item.ExactSourceText,
                style = item.StyleFacts,
                numbering = item.NumberingFacts,
                location = item.LocationFacts,
            });
            return bytes.Length;
        }

        private static int EstimateTokens(int bytes) => (int)Math.Ceiling(bytes / 6.0);
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
