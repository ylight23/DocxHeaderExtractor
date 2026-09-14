using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Canonical serializer for the projected identity verifier request. The benchmark and a future
/// transport runner must call this builder with the same frozen packet; neither side owns a second
/// request serializer.
/// </summary>
public static class HdsaCanonicalProjectedPairVerifierRequestBuilder
{
    public const string Version = "hdsa-canonical-projected-pair-verifier-request-builder-v1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static HdsaCanonicalProjectedPairVerifierRequest Build(
        JsonElement packet,
        string model = "qwen/qwen3.7-flash",
        string provider = "OpenRouter",
        int maxOutputTokens = 768)
    {
        if (packet.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Projected evidence packet must be an object.", nameof(packet));
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (maxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));

        var request = new ProjectedRequest(
            HdsaGlobalIdentityRetrieveVerifyContract.Version,
            packet,
            new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" },
            JsonSerializer.SerializeToElement(HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(), Options),
            new ModelConfiguration(provider, model, maxOutputTokens));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Options);
        return new(JsonSerializer.Serialize(request, Options), bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private sealed record ProjectedRequest(
        [property: JsonPropertyName("systemContract")] string SystemContract,
        [property: JsonPropertyName("pairEvidencePacket")] JsonElement PairEvidencePacket,
        [property: JsonPropertyName("decisionOptions")] IReadOnlyList<string> DecisionOptions,
        [property: JsonPropertyName("responseSchema")] JsonElement ResponseSchema,
        [property: JsonPropertyName("modelConfiguration")] ModelConfiguration ModelConfiguration);

    private sealed record ModelConfiguration(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);
}

public sealed record HdsaCanonicalProjectedPairVerifierRequest(
    string Json,
    byte[] Utf8Bytes,
    string Sha256);
