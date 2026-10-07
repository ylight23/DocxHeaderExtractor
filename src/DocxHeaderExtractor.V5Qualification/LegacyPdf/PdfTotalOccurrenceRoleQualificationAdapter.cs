using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>P6T-A qualification adapter. It reuses P6S's snapshot-rehydrated P05 plan and never changes the live runtime.</summary>
public sealed record PdfTotalRolePreparedPack(
    PdfCandidateAuthorityPreparedPack SourcePack,
    V5TotalRoleRequestV1 Request,
    byte[] ProviderBody,
    string ProviderRequestHash,
    int ProviderRequestBytes);

public sealed record PdfTotalRolePreparedPackD(
    PdfCandidateAuthorityPreparedPack SourcePack,
    V5TotalRoleRequestD Request,
    byte[] ProviderBody,
    string ProviderRequestHash,
    int ProviderRequestBytes);

public sealed record PdfUnitTopologyPreparedPackE1(
    PdfCandidateAuthorityPreparedPack SourcePack,
    V5SegmentationTopologyRequestE1 Request,
    byte[] ProviderBody,
    string ProviderRequestHash,
    int ProviderRequestBytes);

/// <summary>P6T-F1 prepared function-membership request. Qualification-only; it never consumes P6T-E1 output.</summary>
public sealed record PdfFunctionMembershipPreparedPackF1(
    PdfCandidateAuthorityPreparedPack SourcePack,
    OccurrenceFunctionRequest Request,
    byte[] ProviderBody,
    string ProviderRequestHash,
    int ProviderRequestBytes);

public static class PdfTotalOccurrenceRoleQualificationAdapter
{
    public static PdfTotalRolePreparedPack Prepare(PdfCandidateAuthorityDocumentPlan plan, PdfCandidateAuthorityPreparedPack sourcePack)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sourcePack);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = sourcePack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var ownedSet = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var context = sourcePack.VisibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
        var request = V5TotalOccurrenceRoleProtocolV1.Compose(owned, context);
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage,
            sourcePack.MaxCompletionTokens, Envelope);
        return new PdfTotalRolePreparedPack(sourcePack, request, body.PayloadBytes, body.Hash, body.Bytes);
    }

    public static PdfTotalRolePreparedPack PrepareWithReadOnlyCorrespondences(
        PdfCandidateAuthorityDocumentPlan plan,
        PdfCandidateAuthorityPreparedPack sourcePack,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(correspondences);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = sourcePack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var ownedSet = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var context = sourcePack.VisibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
        var request = V5TotalOccurrenceRoleProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, correspondences);
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage,
            sourcePack.MaxCompletionTokens, Envelope);
        return new PdfTotalRolePreparedPack(sourcePack, request, body.PayloadBytes, body.Hash, body.Bytes);
    }

    public static V5TotalRoleDecisionResultV1 Parse(PdfTotalRolePreparedPack pack, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var json = JsonDocument.Parse(rawResponse);
        return V5TotalOccurrenceRoleProtocolV1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(rawResponse),
            PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, pack.Request.Occurrences);
    }

    public static PdfTotalRolePreparedPackD PrepareWithExplicitAbstentionAndReadOnlyCorrespondences(
        PdfCandidateAuthorityDocumentPlan plan,
        PdfCandidateAuthorityPreparedPack sourcePack,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sourcePack); ArgumentNullException.ThrowIfNull(correspondences);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = sourcePack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var ownedSet = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var context = sourcePack.VisibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
        var request = V5TotalOccurrenceRoleProtocolD.ComposeWithReadOnlyCorrespondences(owned, context, correspondences);
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage,
            sourcePack.MaxCompletionTokens, Envelope);
        return new PdfTotalRolePreparedPackD(sourcePack, request, body.PayloadBytes, body.Hash, body.Bytes);
    }

    public static V5TotalRoleDecisionResultD Parse(PdfTotalRolePreparedPackD pack, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var json = JsonDocument.Parse(rawResponse);
        return V5TotalOccurrenceRoleProtocolD.Parse(json.RootElement, Encoding.UTF8.GetByteCount(rawResponse),
            PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, pack.Request.Occurrences);
    }

    public static PdfUnitTopologyPreparedPackE1 PrepareUnitTopologyE1(
        PdfCandidateAuthorityDocumentPlan plan,
        PdfCandidateAuthorityPreparedPack sourcePack,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sourcePack); ArgumentNullException.ThrowIfNull(correspondences);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = sourcePack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var ownedSet = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var context = sourcePack.VisibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
        var request = V5SegmentationTopologyProtocolE1.ComposeWithReadOnlyCorrespondences(owned, context, correspondences);
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage, sourcePack.MaxCompletionTokens, Envelope);
        return new PdfUnitTopologyPreparedPackE1(sourcePack, request, body.PayloadBytes, body.Hash, body.Bytes);
    }

    public static V5SegmentationTopologyResultE1 ParseUnitTopologyE1(PdfUnitTopologyPreparedPackE1 pack, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var json = JsonDocument.Parse(rawResponse);
        return V5SegmentationTopologyProtocolE1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(rawResponse), PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, pack.Request.Occurrences);
    }

    public static PdfFunctionMembershipPreparedPackF1 PrepareFunctionMembershipF1(
        PdfCandidateAuthorityDocumentPlan plan,
        PdfCandidateAuthorityPreparedPack sourcePack,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sourcePack); ArgumentNullException.ThrowIfNull(correspondences);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = sourcePack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var ownedSet = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var context = sourcePack.VisibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
        var request = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, correspondences);
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage, sourcePack.MaxCompletionTokens, Envelope);
        return new PdfFunctionMembershipPreparedPackF1(sourcePack, request, body.PayloadBytes, body.Hash, body.Bytes);
    }

    public static OccurrenceFunctionResult ParseFunctionMembershipF1(PdfFunctionMembershipPreparedPackF1 pack, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var json = JsonDocument.Parse(rawResponse);
        return OccurrenceFunctionProtocolV1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(rawResponse), PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, pack.Request.Occurrences);
    }

    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
}
