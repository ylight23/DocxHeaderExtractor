using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

public sealed record V5PackedSourceRequest(
    string PackId,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    V5ComposedSemanticRequest Request,
    int MaxCompletionTokens = 0,
    string? ProviderRequestHash = null,
    int ProviderRequestBytes = 0);

/// <summary>A provider-free v3 decision request for one real source pack.</summary>
public sealed record V5PackedDecisionRequestV3(
    string PackId,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    V5SemanticDecisionRequestPacketV3 Packet,
    V5ComposedSemanticDecisionRequestV3 Request,
    int MaxCompletionTokens,
    string ProviderRequestHash,
    int ProviderRequestBytes,
    byte[] ProviderBody);

/// <summary>
/// Builds a real PDF source-universe preflight without opening a provider or Gold. The existing
/// parser and named packing policy supply observations; the V5 composer supplies only a deterministic
/// semantic request representation.
/// </summary>
public static class V5PdfPreflightBuilder
{
    /// <summary>
    /// Ceiling for a V5 semantic-claim completion, matching the OpenRouter transport's own default
    /// (<see cref="DocxHeaderExtractor.Infrastructure.AI.RemoteInferenceOptions.MaxOutputTokens"/>).
    /// </summary>
    private const int ProviderMaxCompletionTokensCeiling = 32768;

    /// <summary>The packing policy id the frozen P05 canary packs use. Exposed so a caller outside
    /// this assembly (a qualification runner, not a test) never needs the internal policy registry.</summary>
    public const string PdfResourceBoundedPackingPolicyId = SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id;

    /// <summary>The exact-coordinate atoms for a PDF's source universe, for a caller that needs to
    /// bind a provider response outside this assembly. Never opens a provider or Gold.</summary>
    public static IReadOnlyList<SemanticSourceAtom> LoadAtoms(string pdfPath, string? frozenSnapshotRoot = null) =>
        PdfFrozenSourceView.Load(pdfPath, frozenSnapshotRoot).Atoms;

    /// <summary>
    /// Builds the same deterministic P05 pack boundaries as the historical cohort, but composes
    /// the live v3 one-decision-per-owned-occurrence protocol and freezes its exact OpenRouter body.
    /// This method is provider-free; it does not open Gold or send requests.
    /// </summary>
    public static IReadOnlyList<V5PackedDecisionRequestV3> BuildV3(
        string pdfPath,
        string documentId,
        DocumentTaskContract contract,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope,
        string? frozenSnapshotRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(packingPolicy);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        contract.Validate();
        if (providerEnvelope.Model != V5RouteIdentity.OpenRouterQwen37ChatCompletions.Model ||
            providerEnvelope.Provider != V5RouteIdentity.OpenRouterQwen37ChatCompletions.Provider ||
            providerEnvelope.ResponseFormat != "json_object")
            throw new InvalidOperationException("v3-preflight-route-envelope-not-qualified");

        providerEnvelope = providerEnvelope with { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var policy = ResolvePolicy(packingPolicy);
        var authority = PdfFrozenSourceView.Load(pdfPath, frozenSnapshotRoot);
        var graph = BuildGraph(authority.Atoms, documentId);
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var packs = policy.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        return packs.Select(pack =>
        {
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var ownedSet = ownedAliases.ToHashSet(StringComparer.Ordinal);
            var owned = ownedAliases.Select(alias => byAlias[alias]).ToArray();
            var contextOnly = visibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => byAlias[alias]).ToArray();
            var packet = new V5SemanticDecisionRequestPacketV3(owned, contextOnly, [], [], [], []);
            var request = V5SemanticDecisionComposerV3.Compose(contract, packet);
            var maxTokens = V5SemanticCompletionBudget.Compute(
                ownedAliases.Length, visibleAliases.Length, request.Utf8Bytes, ProviderMaxCompletionTokensCeiling);
            var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, maxTokens, providerEnvelope);
            return new V5PackedDecisionRequestV3(pack.PackId, ownedAliases, visibleAliases, packet, request,
                maxTokens, body.Hash, body.Bytes, body.PayloadBytes);
        }).ToArray();
    }

    /// <summary>
    /// Rebuilds the frozen v2.1 PDF qualification/replay format (durable claim identity,
    /// ownership-scoped binding, mandatory evidenceNeeds, harness-only EXHAUSTED, and no
    /// selectionMode field on the wire). New runtime qualification uses <see cref="BuildV3"/>.
    /// Also freezes, per pack, the complete deterministic OpenRouter request body - not just the
    /// semantic prompt bytes - so historical replay cannot silently diverge on transport parameters.
    /// </summary>
    public static (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuildV2_1(
        string pdfPath,
        string documentId,
        DocumentTaskContract contract,
        string packingPolicy,
        V5ProviderEnvelope providerEnvelope,
        string? frozenSnapshotRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(packingPolicy);
        ArgumentNullException.ThrowIfNull(providerEnvelope);
        contract.Validate();
        providerEnvelope = providerEnvelope with { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var policy = ResolvePolicy(packingPolicy);
        var authority = PdfFrozenSourceView.Load(pdfPath, frozenSnapshotRoot);
        var graph = BuildGraph(authority.Atoms, documentId);
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var packs = policy.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        var requests = packs.Select(pack =>
        {
            var (ownedAliases, visibleAliases, owned, visible) = ResolvePack(pack, byAlias);
            // Disjoint by construction: visible always contains owned plus halo (the packing
            // policy's margin), so contextOnly is exactly the halo, never an owned alias.
            var ownedSet = ownedAliases.ToHashSet(StringComparer.Ordinal);
            var contextOnly = visible.Where(node => !ownedSet.Contains(node.SourceAlias)).ToArray();
            var composed = V5SemanticRequestComposerV2_1.Compose(contract,
                new V5EvidencePacketV2_1(owned, contextOnly, [], [], [], []));
            var maxTokens = V5SemanticCompletionBudget.Compute(
                ownedAliases.Length, visibleAliases.Length, composed.Utf8Bytes, ProviderMaxCompletionTokensCeiling);
            var body = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, composed.Prompt, maxTokens, providerEnvelope);
            return new V5PackedSourceRequest(pack.PackId, ownedAliases, visibleAliases, composed,
                maxTokens, body.Hash, body.Bytes);
        }).ToArray();
        var preflight = V5ProviderPreflightBuilder.BuildV2_1(
            authority.SourceSha256,
            graph,
            contract,
            requests.Select(item => item.Request).ToArray(),
            policy.PolicyId,
            providerEnvelope);
        return (preflight, requests);
    }

    private static ISemanticEvidencePackingPolicy ResolvePolicy(string packingPolicy) => packingPolicy switch
    {
        SemanticEvidencePackingPolicies.FixedOwnedCount120Id => SemanticEvidencePackingPolicies.FixedOwnedCount120,
        SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id => SemanticEvidencePackingPolicies.PdfResourceBoundedP05,
        _ => throw new InvalidOperationException($"unknown-v5-packing-policy:{packingPolicy}"),
    };

    internal static UniversalEvidenceGraph BuildGraph(SourceOccurrenceUniverse authority, string documentId) =>
        BuildGraph(authority.Atoms, documentId);

    internal static UniversalEvidenceGraph BuildGraph(IReadOnlyList<SemanticSourceAtom> atoms, string documentId) =>
        EvidenceGraphBuilder.Build(atoms.Select(atom => new SourceObservation(
            $"V5:{atom.SourceId}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length), new EvidenceGeometry(atom.Page),
            new Dictionary<string, string?> { ["sourceType"] = "PDF", ["documentId"] = documentId })));

    private static (string[] OwnedAliases, string[] VisibleAliases, EvidenceNode[] Owned, EvidenceNode[] Visible) ResolvePack(
        SemanticEvidencePack pack, IReadOnlyDictionary<string, EvidenceNode> byAlias)
    {
        var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
        var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
        var owned = ownedAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
        var visible = visibleAliases.Where(byAlias.ContainsKey).Select(alias => byAlias[alias]).ToArray();
        return (ownedAliases, visibleAliases, owned, visible);
    }
}
