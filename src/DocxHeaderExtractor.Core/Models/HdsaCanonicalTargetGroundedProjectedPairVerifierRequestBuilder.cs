using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Canonical V4G serializer. It changes only the representation of the two target
/// occurrences: target identity is rendered in a first-class envelope while the
/// frozen projected packet remains supporting evidence. It does not add evidence.
/// </summary>
public static class HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder
{
    public const string Version = "hdsa-canonical-target-grounded-projected-pair-verifier-request-builder-v1";

    public const string SystemContract = """
You are an A99 semantic-identity pair verifier. Classify the relation between LEFT_TARGET and
RIGHT_TARGET only. The exact occurrence IDs and verbatim target text in TARGET PAIR are authoritative.
Supporting evidence is context, not another candidate endpoint. Do not choose endpoints from supporting
evidence. CONTINUATION_OF means the right target continues the logical heading begun at the left target;
use RIGHT_TO_LEFT. SAME_SEMANTIC_REPEAT means the same logical heading is repeated without continuation;
use NONE. DISTINCT means separate logical headings, even if nearby or semantically related. UNRESOLVED
means the evidence is insufficient. In the unchanged response contract, echo the candidate pair ID and
the exact LEFT_TARGET/RIGHT_TARGET occurrence IDs in the existing pair fields; do not return target text
in those fields. Return no explanation, groups, merge instruction, parent, hierarchy, level, offsets, Gold,
or legacy fields.
""";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static HdsaCanonicalTargetGroundedProjectedPairVerifierRequest Build(
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

        var pairCore = packet.GetProperty("pairCore");
        if (pairCore.ValueKind != JsonValueKind.Array || pairCore.GetArrayLength() != 2)
            throw new FormatException("TARGET_GROUNDING_PAIR_CORE_MUST_HAVE_TWO_OCCURRENCES");

        var left = Target(pairCore[0], "LEFT_TARGET");
        var right = Target(pairCore[1], "RIGHT_TARGET");
        var supporting = Canonicalize(JsonNode.Parse(packet.GetRawText()), omitPairCore: true)!.AsObject();

        var request = new TargetGroundedRequest(
            SystemContract,
            new TargetPair(packet.GetProperty("candidateId").GetString()!, left, right),
            supporting,
            new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" },
            JsonSerializer.SerializeToElement(HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(), Options),
            new ModelConfiguration(provider, model, maxOutputTokens));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Options);
        return new(JsonSerializer.Serialize(request, Options), bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static TargetOccurrence Target(JsonElement occurrence, string role)
    {
        return new TargetOccurrence(
            role,
            occurrence.GetProperty("sourceOccurrenceId").GetString()!,
            occurrence.GetProperty("rawSurface").GetString()!,
            occurrence.GetProperty("canonicalComparisonSurface").GetString()!,
            occurrence.GetProperty("documentOrder").GetInt32(),
            occurrence.GetProperty("sourceContainer").GetString()!);
    }

    private static JsonNode? Canonicalize(JsonNode? node, bool omitPairCore = false)
    {
        if (node is JsonObject source)
        {
            var result = new JsonObject();
            foreach (var property in source.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (omitPairCore && string.Equals(property.Key, "pairCore", StringComparison.Ordinal))
                    continue;
                result.Add(property.Key, Canonicalize(property.Value));
            }
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(Canonicalize(item));
            return result;
        }
        return node?.DeepClone();
    }

    private sealed record TargetGroundedRequest(
        [property: JsonPropertyName("systemContract")] string SystemContract,
        [property: JsonPropertyName("targetPair")] TargetPair TargetPair,
        [property: JsonPropertyName("supportingEvidence")] JsonObject SupportingEvidence,
        [property: JsonPropertyName("decisionOptions")] IReadOnlyList<string> DecisionOptions,
        [property: JsonPropertyName("responseSchema")] JsonElement ResponseSchema,
        [property: JsonPropertyName("modelConfiguration")] ModelConfiguration ModelConfiguration);

    private sealed record TargetPair(
        [property: JsonPropertyName("candidateId")] string CandidateId,
        [property: JsonPropertyName("leftTarget")] TargetOccurrence LeftTarget,
        [property: JsonPropertyName("rightTarget")] TargetOccurrence RightTarget);

    private sealed record TargetOccurrence(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
        [property: JsonPropertyName("verbatimText")] string VerbatimText,
        [property: JsonPropertyName("canonicalComparisonText")] string CanonicalComparisonText,
        [property: JsonPropertyName("sourceOrder")] int SourceOrder,
        [property: JsonPropertyName("pageOrSourceLocation")] string PageOrSourceLocation);

    private sealed record ModelConfiguration(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);
}

public sealed record HdsaCanonicalTargetGroundedProjectedPairVerifierRequest(
    string Json,
    byte[] Utf8Bytes,
    string Sha256);
