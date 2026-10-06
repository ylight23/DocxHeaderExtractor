using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Live V3.2 request composer. The sparse response grammar stays intact, but the provider sees
/// only explicit request-local handles: never a harness alias or source ordinal it could confuse
/// with an ownedIndex.
/// </summary>
public static class V5SemanticSparseDecisionComposerV3_1
{
    public const string Version = "v5-semantic-decision-composer-3.2";

    private static readonly string Instructions = string.Join("\n", [
        "You are a task-defined semantic reasoner.",
        "Return a sparse decisions array: emit a decision only for an owned subject for which you have a semantic assertion. Omission means no proposal.",
        "Each subjectEvidence item explicitly carries its ownedIndex. Copy that ownedIndex exactly; it is the only subject handle. Never infer an array position or use any other identity.",
        "Do not emit the same ownedIndex more than once. The harness owns occurrence identity and will quarantine invalid or duplicate indexes while preserving valid sibling decisions.",
        "For a subject that spans multiple atoms, use additionalSubjectParts with ownedIndex values only, strictly increasing and after the primary ownedIndex.",
        "For a strict substring provide verbatimText exactly as it appears. For a whole atom omit verbatimText; never retype a whole atom.",
        "Each contextOnlyEvidence item explicitly carries its contextIndex. A relation target uses targetParts with sourceGroup OWNED or CONTEXT_ONLY and that explicit local handle. A target may use contextOnlyEvidence.",
        "Use only declared predicates and relations. A UNARY predicate has a value and no targetParts; a RELATION has targetParts when RESOLVED and never has a value.",
        "Every claim contains evidenceNeeds explicitly: RESOLVED sends []; OPEN and CONFLICTED send at least one need.",
        "The harness owns claim identity. Do not emit claimId. existingClaimId may appear only for an explicitly supplied claim being refined.",
        "Return only the declared semantic decision schema.",
    ]);

    public static CanonicalSemanticSparseDecisionRequestV3_2 BuildCanonical(DocumentTaskContract contract, V5SemanticDecisionRequestPacketV3 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        ValidatePacket(packet);
        var oldBounds = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(packet.SubjectEvidence.Count,
            packet.SubjectEvidence.Count + packet.ContextOnlyEvidence.Count);
        // ownedIndex is the only new response field. Reserve 32 UTF-8 bytes per maximum sparse
        // decision (field name, colon, integer and structural punctuation) independently of tokens.
        var bounds = oldBounds with { MaxResponseUtf8Bytes = checked(oldBounds.MaxResponseUtf8Bytes + packet.SubjectEvidence.Count * 32) };
        return new CanonicalSemanticSparseDecisionRequestV3_2(
            Version,
            V5Protocol.ClaimSchemaVersionV3_2,
            contract,
            V5ClaimShapesV2_1.Generate(contract),
            V5SemanticSparseDecisionContractV3_1.Schema(contract, packet.SubjectEvidence.Count, packet.ContextOnlyEvidence.Count, bounds),
            bounds,
            V5SourceSelectionPolicy.Generate(),
            string.Join("\n", Instructions,
                $"The complete response must serialize to at most {bounds.MaxResponseUtf8Bytes} UTF-8 bytes and contain at most {bounds.MaxClaimsTotal} claims total. Never truncate, repair, or add commentary."),
            V5ProviderSemanticPacketV3_2.From(packet));
    }

    public static V5ComposedSemanticDecisionRequestV3 Compose(DocumentTaskContract contract, V5SemanticDecisionRequestPacketV3 packet)
    {
        var request = BuildCanonical(contract, packet);
        var prompt = JsonSerializer.Serialize(request, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false }).ReplaceLineEndings("\n");
        var schema = JsonSerializer.Serialize(request.ResponseSchema, CanonicalJson.Options);
        return new(Version, prompt, Hashing.Sha256(request.Instructions.ReplaceLineEndings("\n")), Hashing.Sha256(schema),
            Hashing.Sha256(prompt), Encoding.UTF8.GetByteCount(prompt), request.ResponseBounds);
    }

    private static void ValidatePacket(V5SemanticDecisionRequestPacketV3 packet)
    {
        var ownedAliases = packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        if (ownedAliases.Count != packet.SubjectEvidence.Count)
            throw new InvalidOperationException("sparse-decision-packet-duplicate-owned-alias");
        if (packet.ContextOnlyEvidence.Any(node => ownedAliases.Contains(node.SourceAlias)) ||
            packet.ContextOnlyEvidence.Select(node => node.SourceAlias).Distinct(StringComparer.Ordinal).Count() != packet.ContextOnlyEvidence.Count)
            throw new InvalidOperationException("sparse-decision-packet-owned-context-overlap");
        foreach (var node in packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence)) node.Validate();
    }
}

/// <summary>Deliberately lossy provider view: harness identity and physical source coordinates stay local.</summary>
public sealed record V5ProviderSemanticPacketV3_2(
    [property: JsonPropertyName("subjectEvidence")] IReadOnlyList<V5ProviderOwnedEvidenceV3_2> SubjectEvidence,
    [property: JsonPropertyName("contextOnlyEvidence")] IReadOnlyList<V5ProviderContextEvidenceV3_2> ContextOnlyEvidence,
    [property: JsonPropertyName("openOrConflictedClaims")] IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    [property: JsonPropertyName("retrievedEvidence")] IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    [property: JsonPropertyName("layoutEvidence")] IReadOnlyList<V5ProviderUnaddressedEvidenceV3_2> LayoutEvidence,
    [property: JsonPropertyName("visualEvidence")] IReadOnlyList<V5ProviderUnaddressedEvidenceV3_2> VisualEvidence)
{
    private static readonly HashSet<string> ApprovedStructuralFactKeys = new(StringComparer.Ordinal)
    {
        "sourceType", "bold", "italic", "fontSize", "relativeFontSize", "bodyFontSize", "fontSizeToBodyRatio",
        "boldRatio", "italicRatio", "lineCount", "width", "height", "grid", "gridType", "structuralScope", "pageRole",
    };

    public static V5ProviderSemanticPacketV3_2 From(V5SemanticDecisionRequestPacketV3 packet) => new(
        packet.SubjectEvidence.Select((node, index) => new V5ProviderOwnedEvidenceV3_2(index, node.Modality, node.Text, StructuralFacts(node.Facts))).ToArray(),
        packet.ContextOnlyEvidence.Select((node, index) => new V5ProviderContextEvidenceV3_2(index, node.Modality, node.Text, StructuralFacts(node.Facts))).ToArray(),
        packet.OpenOrConflictedClaims, packet.RetrievedEvidence,
        packet.LayoutEvidence.Select(ToUnaddressed).ToArray(), packet.VisualEvidence.Select(ToUnaddressed).ToArray());

    private static V5ProviderUnaddressedEvidenceV3_2 ToUnaddressed(EvidenceNode node) =>
        new(node.Modality, node.Text, StructuralFacts(node.Facts));

    // Default-deny projection: new facts cannot silently become a second route to an occurrence.
    // ownedIndex/contextIndex are the sole provider-facing handles; this list is structural only.
    private static IReadOnlyDictionary<string, string?> StructuralFacts(IReadOnlyDictionary<string, string?> facts) =>
        facts.Where(pair => ApprovedStructuralFactKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

public sealed record V5ProviderOwnedEvidenceV3_2(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("modality")] EvidenceModality Modality,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("facts")] IReadOnlyDictionary<string, string?> Facts);

public sealed record V5ProviderContextEvidenceV3_2(
    [property: JsonPropertyName("contextIndex")] int ContextIndex,
    [property: JsonPropertyName("modality")] EvidenceModality Modality,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("facts")] IReadOnlyDictionary<string, string?> Facts);

public sealed record V5ProviderUnaddressedEvidenceV3_2(
    [property: JsonPropertyName("modality")] EvidenceModality Modality,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("facts")] IReadOnlyDictionary<string, string?> Facts);

public sealed record CanonicalSemanticSparseDecisionRequestV3_2(
    [property: JsonPropertyName("composerVersion")] string ComposerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("contract")] DocumentTaskContract Contract,
    [property: JsonPropertyName("claimShapes")] IReadOnlyList<V5ClaimShapeV2_1> ClaimShapes,
    [property: JsonPropertyName("responseSchema")] object ResponseSchema,
    [property: JsonPropertyName("responseBounds")] V5SemanticDecisionResponseBoundsV3 ResponseBounds,
    [property: JsonPropertyName("sourceSelectionPolicy")] object SourceSelectionPolicy,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("packet")] V5ProviderSemanticPacketV3_2 Packet);

public sealed record V5SemanticSparseDecisionResponseV3_1(
    [property: JsonPropertyName("decisions")] IReadOnlyList<V5SemanticSparseSubjectDecisionV3_1> Decisions)
{
    /// <summary>Raw-wire parse quarantines, kept out of the provider wire and merged by Bind.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ParseRefusals { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public static V5SemanticSparseDecisionResponseV3_1 FromExhaustive(V5SemanticDecisionResponseV3 exhaustive)
    {
        ArgumentNullException.ThrowIfNull(exhaustive);
        return new(exhaustive.Decisions.Select((decision, index) =>
            new V5SemanticSparseSubjectDecisionV3_1(index, decision.Claims)).ToArray());
    }
}

public sealed record V5SemanticSparseSubjectDecisionV3_1(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("claims")] IReadOnlyList<V5SemanticDecisionClaimV3> Claims)
{
    /// <summary>Original raw array index, not model authority. It makes quarantine evidence stable.</summary>
    [JsonIgnore]
    public int WireOrdinal { get; init; } = -1;
}

/// <summary>
/// V3.1 parser/binder. Out-of-range or duplicate ownedIndex values are quarantined at their sparse
/// decision only; bounded envelope failures remain response-wide because they are unsafe to accept.
/// </summary>
public static class V5SemanticSparseDecisionContractV3_1
{
    public const string SchemaVersion = "v5-source-backed-decision-3.1";

    public static object Schema(DocumentTaskContract contract, int ownedCount, int contextOnlyCount,
        V5SemanticDecisionResponseBoundsV3 bounds)
    {
        var schema = JsonNode.Parse(JsonSerializer.Serialize(
            V5SemanticDecisionContractV3.Schema(contract, ownedCount, contextOnlyCount, bounds), CanonicalJson.Options))!.AsObject();
        var decisions = schema["properties"]!["decisions"]!.AsObject();
        decisions["minItems"] = 0;
        decisions["maxItems"] = ownedCount;
        var item = decisions["items"]!.AsObject();
        var properties = item["properties"]!.AsObject();
        properties["ownedIndex"] = new JsonObject
        {
            ["type"] = "integer",
            ["enum"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, ownedCount).ToArray(), CanonicalJson.Options),
        };
        item["required"] = new JsonArray(JsonValue.Create("ownedIndex"), JsonValue.Create("claims"));
        return schema;
    }

    public static V5SemanticSparseDecisionResponseV3_1 Parse(JsonElement payload, DocumentTaskContract contract,
        int ownedCount, int contextOnlyCount)
    {
        var bounds = Bounds(ownedCount, contextOnlyCount);
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > bounds.MaxResponseUtf8Bytes)
            throw new InvalidOperationException($"sparse-decision-response-byte-budget-exceeded:max={bounds.MaxResponseUtf8Bytes}");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(property => !property.NameEquals("decisions")) ||
            !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() > ownedCount)
            throw new InvalidOperationException("sparse-decision-response-shape-invalid");
        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        };
        var parsed = new List<V5SemanticSparseSubjectDecisionV3_1>();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (decision, wireOrdinal) in decisions.EnumerateArray().Select((item, index) => (item, index)))
        {
            var key = $"sparse-decision-{wireOrdinal}";
            try
            {
                if (decision.ValueKind != JsonValueKind.Object ||
                    decision.EnumerateObject().Any(property => !property.NameEquals("ownedIndex") && !property.NameEquals("claims")) ||
                    !decision.TryGetProperty("ownedIndex", out _) || !decision.TryGetProperty("claims", out var claims) ||
                    claims.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("missing-or-extra-decision-field");
                var typed = decision.Deserialize<V5SemanticSparseSubjectDecisionV3_1>(options)
                    ?? throw new InvalidOperationException("empty-decision");
                if (typed.Claims is null) throw new InvalidOperationException("claims-missing");
                parsed.Add(typed with { WireOrdinal = wireOrdinal });
            }
            catch (JsonException)
            {
                refusals[key] = "json-decision-schema-invalid";
            }
            catch (InvalidOperationException ex)
            {
                refusals[key] = ex.Message;
            }
        }
        var response = new V5SemanticSparseDecisionResponseV3_1(parsed) { ParseRefusals = refusals };
        ValidateEnvelope(response, contract, ownedCount, contextOnlyCount, bounds);
        return response;
    }

    public static V5DecisionBindingResultV3 Bind(string requestId, V5SemanticSparseDecisionResponseV3_1 response,
        DocumentTaskContract contract, IReadOnlyList<EvidenceNode> ownedEvidence, IReadOnlyList<EvidenceNode> contextOnlyEvidence,
        IReadOnlyList<SemanticSourceAtom> atoms, ClaimBindingScope scope, IReadOnlyList<int>? originalOwnedOrdinals = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var bounds = Bounds(ownedEvidence.Count, ownedEvidence.Count + contextOnlyEvidence.Count);
        try
        {
            ValidateEnvelope(response, contract, ownedEvidence.Count, contextOnlyEvidence.Count, bounds);
        }
        catch (InvalidOperationException ex)
        {
            return new(null, new Dictionary<string, string>(StringComparer.Ordinal) { ["response-bounds"] = ex.Message });
        }
        if (originalOwnedOrdinals is not null &&
            (originalOwnedOrdinals.Count != ownedEvidence.Count || originalOwnedOrdinals.Any(ordinal => ordinal < 0) ||
             originalOwnedOrdinals.Distinct().Count() != originalOwnedOrdinals.Count))
            throw new InvalidOperationException("sparse-decision-original-owned-ordinal-map-invalid");

        var dense = Enumerable.Range(0, ownedEvidence.Count).Select(_ => new V5SemanticSubjectDecisionV3([])).ToArray();
        var refusals = new Dictionary<string, string>(response.ParseRefusals, StringComparer.Ordinal);
        var seen = new HashSet<int>();
        foreach (var (decision, parsedIndex) in response.Decisions.Select((decision, index) => (decision, index)))
        {
            var wireOrdinal = decision.WireOrdinal >= 0 ? decision.WireOrdinal : parsedIndex;
            var key = $"sparse-decision-{wireOrdinal}";
            if (decision.OwnedIndex < 0 || decision.OwnedIndex >= ownedEvidence.Count)
            {
                refusals[key] = "owned-index-out-of-range";
                continue;
            }
            if (!seen.Add(decision.OwnedIndex))
            {
                refusals[key] = "duplicate-owned-index";
                continue;
            }
            try
            {
                // Reuse V3's inner field, relation, multipart and task-contract validation on a
                // single owned slot. A malformed decision is quarantined without discarding peers.
                var probe = Enumerable.Range(0, ownedEvidence.Count).Select(_ => new V5SemanticSubjectDecisionV3([])).ToArray();
                probe[decision.OwnedIndex] = new V5SemanticSubjectDecisionV3(decision.Claims);
                V5SemanticDecisionContractV3.ValidateBounds(new V5SemanticDecisionResponseV3(probe), contract,
                    ownedEvidence.Count, contextOnlyEvidence.Count, bounds);
                dense[decision.OwnedIndex] = new V5SemanticSubjectDecisionV3(decision.Claims);
            }
            catch (InvalidOperationException ex)
            {
                refusals[key] = ex.Message;
            }
        }
        var bound = V5SemanticDecisionContractV3.Bind(requestId, new V5SemanticDecisionResponseV3(dense), contract,
            ownedEvidence, contextOnlyEvidence, atoms, scope,
            originalOwnedOrdinals ?? Enumerable.Range(0, ownedEvidence.Count).ToArray());
        return new(bound.Binding, Merge(refusals, bound.ResponseRefusals));
    }

    private static V5SemanticDecisionResponseBoundsV3 Bounds(int ownedCount, int contextOnlyCount)
    {
        var oldBounds = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount);
        return oldBounds with { MaxResponseUtf8Bytes = checked(oldBounds.MaxResponseUtf8Bytes + ownedCount * 32) };
    }

    private static void ValidateEnvelope(V5SemanticSparseDecisionResponseV3_1 response, DocumentTaskContract contract,
        int ownedCount, int contextOnlyCount, V5SemanticDecisionResponseBoundsV3 bounds)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Decisions is null || response.Decisions.Count > ownedCount || response.Decisions.Any(decision => decision is null))
            throw new InvalidOperationException("sparse-decision-response-cardinality-invalid");
        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(response, CanonicalJson.Options).Length;
        if (canonicalBytes > bounds.MaxResponseUtf8Bytes)
            throw new InvalidOperationException($"sparse-decision-response-byte-budget-exceeded:max={bounds.MaxResponseUtf8Bytes}:actual={canonicalBytes}");
        var totalClaims = response.Decisions.Sum(decision => decision.Claims?.Count ?? throw new InvalidOperationException("sparse-decision-claims-missing"));
        if (totalClaims > bounds.MaxClaimsTotal)
            throw new InvalidOperationException($"sparse-decision-total-claims-bound-exceeded:max={bounds.MaxClaimsTotal}:actual={totalClaims}");
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) => left.Concat(right).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
