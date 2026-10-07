using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>One frozen P6S candidate-authority request. Qualification only; not the default PDF runtime.</summary>
public sealed record PdfCandidateAuthorityPreparedPack(
    string DocumentId, string PackId, int PackOrdinal, IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases, int MaxCompletionTokens, V5CandidateUniverseV1 Universe,
    V5FreeHeadingRequestV1 Request, byte[] ProviderBody, string ProviderRequestHash, int ProviderRequestBytes);

/// <summary>All P05 requests reconstructed exclusively from a compact canonical source snapshot.</summary>
public sealed record PdfCandidateAuthorityDocumentPlan(
    string DocumentId, string SourceSha256, string SourceUniverseSha256, int SourceOccurrenceTotal,
    IReadOnlyList<SemanticSourceAtom> SourceAtoms, IReadOnlyList<PdfCandidateAuthorityPreparedPack> Packs);

/// <summary>
/// Shared producer for P6S-C and any later promoted candidate lane. It deliberately accepts a
/// rehydrated snapshot instead of an arbitrary experimental request builder, so manifest parity
/// proves the runner uses the exact adapter that a future PDF route would invoke.
/// </summary>
public static class PdfCandidateAuthorityQualificationAdapter
{
    public const int CompletionTokenCeiling = PdfInferenceWireContract.CompletionTokenCeiling;
    public const int ResponseUtf8ByteCap = PdfInferenceWireContract.ResponseUtf8ByteCap;

    public static PdfCandidateAuthorityDocumentPlan PrepareFromSnapshot(string snapshotPath, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var snapshot = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(File.ReadAllText(snapshotPath), SnapshotJson)
            ?? throw new InvalidOperationException("p6s-canonical-source-snapshot-deserialize-failed");
        return Prepare(snapshot.Rehydrate(), documentId);
    }

    internal static PdfCandidateAuthorityDocumentPlan Prepare(PdfCandidateSourceAuthorityV1 authority, string documentId)
    {
        var atomByAlias = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var packs = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        var prepared = new List<PdfCandidateAuthorityPreparedPack>(packs.Count);
        var ownedSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
            foreach (var alias in ownedAliases)
                if (!ownedSeen.Add(alias)) throw new InvalidOperationException("p6s-candidate-owned-source-duplicated");
            var owned = ownedAliases.Select(alias => atomByAlias[alias]).ToArray();
            var ownedSet = ownedAliases.ToHashSet(StringComparer.Ordinal);
            var contextOnly = visibleAliases.Where(alias => !ownedSet.Contains(alias))
                .Select(alias => (atomByAlias[alias].Page, atomByAlias[alias].Text)).ToArray();
            var universe = V5CandidateUniverseV1.Build(owned, authority.Atoms, V5CandidatePolicyV1.Default);
            var request = V5CandidateDecisionProtocolV1.Compose(universe, contextOnly, null);
            var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage,
                CompletionTokenCeiling, Envelope);
            prepared.Add(new PdfCandidateAuthorityPreparedPack(documentId, pack.PackId, pack.Ordinal, ownedAliases,
                visibleAliases, CompletionTokenCeiling, universe, request, body.PayloadBytes, body.Hash, body.Bytes));
        }
        if (ownedSeen.Count != authority.Atoms.Count || authority.Atoms.Any(atom => !ownedSeen.Contains(atom.Alias)))
            throw new InvalidOperationException("p6s-candidate-p05-owned-source-conservation-failed");
        return new PdfCandidateAuthorityDocumentPlan(documentId, authority.SourceSha256, authority.SourceAliasUniverseSha256,
            authority.Atoms.Count, authority.Atoms, prepared);
    }

    public static V5CandidateDecisionResultV1 ParseCandidateDecision(PdfCandidateAuthorityPreparedPack pack, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var response = JsonDocument.Parse(rawResponse);
        return V5CandidateDecisionProtocolV1.Parse(response.RootElement, Encoding.UTF8.GetByteCount(rawResponse),
            ResponseUtf8ByteCap, pack.Universe);
    }

    /// <summary>Builds the qualified P6S OpenRouter carrier body for a prepared or arm-specific request.</summary>
    public static V5ProviderRequestBodyV2_1 BuildProviderBody(V5FreeHeadingRequestV1 request, int maxCompletionTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage,
            maxCompletionTokens, Envelope);
    }

    /// <summary>Same P6S request body, with only the carrier's current reasoning-enabled field changed.</summary>
    public static V5ProviderRequestBodyV2_1 BuildProviderBodyReasoningEnabled(V5FreeHeadingRequestV1 request, int maxCompletionTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(request.SystemPrompt, request.UserMessage,
            maxCompletionTokens, Envelope);
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
}
