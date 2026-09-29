using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

public sealed record V5PackedSourceRequest(
    string PackId,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    V5ComposedSemanticRequest Request);

/// <summary>
/// Builds a real PDF source-universe preflight without opening a provider or Gold. The existing
/// parser and named packing policy supply observations; the V5 composer supplies only a deterministic
/// semantic request representation.
/// </summary>
public static class V5PdfPreflightBuilder
{
    /// <summary>Provider-free v2 preflight using the frozen packing policy and recursive v2 schema.</summary>
    public static (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuildV2(
        string pdfPath,
        string documentId,
        DocumentTaskContract contract,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(packingPolicy);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        contract.Validate();
        providerEnvelope = providerEnvelope with { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var policy = packingPolicy switch
        {
            SemanticEvidencePackingPolicies.FixedOwnedCount120Id => SemanticEvidencePackingPolicies.FixedOwnedCount120,
            SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id => SemanticEvidencePackingPolicies.PdfResourceBoundedP05,
            _ => throw new InvalidOperationException($"unknown-v5-packing-policy:{packingPolicy}"),
        };
        var authority = PdfStructuredSourceAuthorityBuilder.Build(pdfPath);
        var graph = EvidenceGraphBuilder.Build(authority.Atoms.Select(atom => new SourceObservation(
            $"V5:{atom.SourceId}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length), new EvidenceGeometry(atom.Page),
            new Dictionary<string, string?> { ["sourceType"] = "PDF", ["documentId"] = documentId })));
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var packs = policy.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        var requests = packs.Select(pack =>
        {
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var owned = ownedAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
            var visible = visibleAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
            var composed = V5SemanticRequestComposerV2.Compose(contract,
                new V5EvidencePacket(owned, visible, [], [], [], []));
            return new V5PackedSourceRequest(pack.PackId, ownedAliases, visibleAliases, composed);
        }).ToArray();
        var preflight = V5ProviderPreflightBuilder.BuildV2(
            authority.SourceSha256,
            graph,
            contract,
            requests.Select(item => item.Request).ToArray(),
            policy.PolicyId,
            providerEnvelope);
        return (preflight, requests);
    }

    public static (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) Build(
        string pdfPath,
        string documentId,
        DocumentTaskContract contract,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(packingPolicy);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        contract.Validate();
        providerEnvelope = providerEnvelope with { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var policy = packingPolicy switch
        {
            SemanticEvidencePackingPolicies.FixedOwnedCount120Id => SemanticEvidencePackingPolicies.FixedOwnedCount120,
            SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id => SemanticEvidencePackingPolicies.PdfResourceBoundedP05,
            _ => throw new InvalidOperationException($"unknown-v5-packing-policy:{packingPolicy}"),
        };
        var authority = PdfStructuredSourceAuthorityBuilder.Build(pdfPath);
        var graph = EvidenceGraphBuilder.Build(authority.Atoms.Select(atom => new SourceObservation(
            $"V5:{atom.SourceId}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length), new EvidenceGeometry(atom.Page),
            new Dictionary<string, string?> { ["sourceType"] = "PDF", ["documentId"] = documentId })));
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var packs = policy.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        var requests = packs.Select(pack =>
        {
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var owned = ownedAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
            var visible = visibleAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
            var composed = V5SemanticRequestComposer.Compose(contract,
                new V5EvidencePacket(owned, visible, [], [], [], []));
            return new V5PackedSourceRequest(pack.PackId, ownedAliases, visibleAliases, composed);
        }).ToArray();
        var preflight = V5ProviderPreflightBuilder.Build(
            authority.SourceSha256,
            graph,
            contract,
            requests.Select(item => item.Request).ToArray(),
            policy.PolicyId,
            providerEnvelope);
        return (preflight, requests);
    }
}
