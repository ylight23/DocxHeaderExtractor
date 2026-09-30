using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// P5C provider-facing experiment: every owned source occurrence has exactly one positional decision.
/// The provider never echoes a subject sourceAlias.  Decision index i is owned subject i, while
/// relation objects may address visible evidence by visibleIndex.  Translation is deterministic back
/// into the existing v2.1 proposal shape so the exact binder remains the sole coordinate authority.
/// </summary>
public static class V5OwnedDecisionProtocolV3
{
    public const string ProtocolVersion = "v5-owned-subject-decision-3.0";
    public const string ComposerVersion = "v5-owned-decision-request-composer-3.0";
    public const string NoClaim = "NO_CLAIM";
    public const string Claims = "CLAIMS";

    private static readonly IReadOnlySet<string> ResponseFields =
        new HashSet<string>(["decisions"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> DecisionFields =
        new HashSet<string>(["disposition", "subjectSelection", "claims"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SelectionFields =
        new HashSet<string>(["verbatimText", "additionalOwnedParts"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> AdditionalPartFields =
        new HashSet<string>(["ownedIndex", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ClaimFields =
        new HashSet<string>(["predicate", "value", "object", "state", "evidenceNeeds", "existingClaimId"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> EndpointFields =
        new HashSet<string>(["sourceParts"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> VisiblePartFields =
        new HashSet<string>(["visibleIndex", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"], StringComparer.Ordinal);

    public static IReadOnlyList<EvidenceNode> VisibleEvidence(V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.SubjectEvidence
            .Concat(packet.ContextOnlyEvidence)
            .OrderBy(node => node.SourceOrdinal)
            .ThenBy(node => node.SourceAlias, StringComparer.Ordinal)
            .ToArray();
    }

    public static object ResponseSchema(DocumentTaskContract contract, V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var ownedCount = packet.SubjectEvidence.Count;
        var visibleCount = VisibleEvidence(packet).Count;
        var predicateNames = contract.Predicates.Select(item => item.Name)
            .Concat(contract.Relations.Select(item => item.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        object IntegerIndexSchema(int count) => count <= 0
            ? new { type = "integer", minimum = 0, maximum = -1 }
            : new { type = "integer", minimum = 0, maximum = count - 1 };

        object VisiblePartSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                visibleIndex = IntegerIndexSchema(visibleCount),
                verbatimText = new { type = "string", minLength = 1 },
                occurrence = new { type = "integer", minimum = 1 },
                leftExactContext = new { type = "string" },
                rightExactContext = new { type = "string" },
            },
            required = new[] { "visibleIndex" },
        };

        object ObjectEndpointSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                sourceParts = new
                {
                    type = "array",
                    minItems = 1,
                    items = VisiblePartSchema(),
                },
            },
            required = new[] { "sourceParts" },
        };

        object ClaimSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                predicate = new { type = "string", @enum = predicateNames },
                value = new { type = "string" },
                @object = ObjectEndpointSchema(),
                state = new { type = "string", @enum = SemanticClaimContractV2_1.ProviderFacingStates },
                evidenceNeeds = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
                existingClaimId = new { type = "string", minLength = 1 },
            },
            required = new[] { "predicate", "state", "evidenceNeeds" },
        };

        object AdditionalOwnedPartSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                ownedIndex = IntegerIndexSchema(ownedCount),
                verbatimText = new { type = "string", minLength = 1 },
                occurrence = new { type = "integer", minimum = 1 },
                leftExactContext = new { type = "string" },
                rightExactContext = new { type = "string" },
            },
            required = new[] { "ownedIndex" },
        };

        object DecisionSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                disposition = new { type = "string", @enum = new[] { NoClaim, Claims } },
                subjectSelection = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        verbatimText = new { type = "string", minLength = 1 },
                        additionalOwnedParts = new
                        {
                            type = "array",
                            items = AdditionalOwnedPartSchema(),
                        },
                    },
                },
                claims = new
                {
                    type = "array",
                    items = ClaimSchema(),
                },
            },
            required = new[] { "disposition", "claims" },
        };

        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                decisions = new
                {
                    type = "array",
                    minItems = ownedCount,
                    maxItems = ownedCount,
                    items = DecisionSchema(),
                },
            },
            required = new[] { "decisions" },
        };
    }

    public static string SchemaHash(DocumentTaskContract contract, V5EvidencePacketV2_1 packet) =>
        Hashing.Sha256(JsonSerializer.Serialize(ResponseSchema(contract, packet), CanonicalJson.Options));

    public static V5OwnedDecisionTranslationV3 ParseAndTranslate(
        JsonElement payload,
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();

        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("decision-payload-not-object");
        EnsureFields(payload, ResponseFields, "response");
        if (!payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("decisions-array-missing");
        if (decisions.GetArrayLength() != packet.SubjectEvidence.Count)
            throw new InvalidOperationException($"decision-count-mismatch:{decisions.GetArrayLength()}:{packet.SubjectEvidence.Count}");

        var visible = VisibleEvidence(packet);
        var proposals = new List<IndexedSemanticClaimProposalV2_1>();
        var proposalOrdinal = 0;
        var decisionIndex = 0;

        foreach (var decision in decisions.EnumerateArray())
        {
            EnsureFields(decision, DecisionFields, "decision");
            var disposition = EnsureRequiredString(decision, "disposition");
            if (disposition is not (NoClaim or Claims))
                throw new InvalidOperationException($"decision-disposition-invalid:{disposition}");
            if (!decision.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("decision-claims-missing");

            var subjectParts = BuildSubjectParts(decision, decisionIndex, packet);
            if (disposition == NoClaim)
            {
                if (claims.GetArrayLength() != 0)
                    throw new InvalidOperationException("no-claim-decision-carries-claims");
                if (decision.TryGetProperty("subjectSelection", out var noClaimSelection) &&
                    noClaimSelection.ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("no-claim-decision-carries-subject-selection");
                decisionIndex++;
                continue;
            }

            if (claims.GetArrayLength() == 0)
                throw new InvalidOperationException("claims-decision-empty");

            foreach (var claim in claims.EnumerateArray())
            {
                EnsureFields(claim, ClaimFields, "claim");
                var predicate = EnsureRequiredString(claim, "predicate");
                var stateText = EnsureRequiredString(claim, "state");
                if (string.Equals(stateText, nameof(ClaimResolutionState.EXHAUSTED), StringComparison.Ordinal))
                    throw new InvalidOperationException("model-may-not-originate-exhausted-state");
                if (!SemanticClaimContractV2_1.ProviderFacingStates.Contains(stateText, StringComparer.Ordinal))
                    throw new InvalidOperationException($"claim-state-not-provider-facing:{stateText}");

                var objectEndpoint = ParseVisibleObject(claim, visible);
                var options = new JsonSerializerOptions(CanonicalJson.Options)
                {
                    Converters = { new JsonStringEnumConverter() },
                };
                var state = JsonSerializer.Deserialize<ClaimResolutionState>($"\"{stateText}\"", options);
                IReadOnlyList<EvidenceNeed>? evidenceNeeds = null;
                if (claim.TryGetProperty("evidenceNeeds", out var needs))
                {
                    if (needs.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("claim-evidenceNeeds-not-array");
                    evidenceNeeds = needs.Deserialize<IReadOnlyList<EvidenceNeed>>(options);
                }

                var proposal = new SemanticClaimProposalV2_1(
                    new ClaimSourceEndpointV2_1(subjectParts),
                    predicate,
                    claim.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                    objectEndpoint,
                    state,
                    evidenceNeeds,
                    claim.TryGetProperty("existingClaimId", out var existing) && existing.ValueKind == JsonValueKind.String ? existing.GetString() : null);

                var issues = SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([proposal]), contract);
                if (issues.Count > 0)
                    throw new InvalidOperationException(string.Join(",", issues));
                proposals.Add(new IndexedSemanticClaimProposalV2_1(proposalOrdinal++, proposal));
            }

            decisionIndex++;
        }

        return new V5OwnedDecisionTranslationV3(proposals);
    }

    private static IReadOnlyList<ProviderSourcePartV2_1> BuildSubjectParts(
        JsonElement decision,
        int decisionIndex,
        V5EvidencePacketV2_1 packet)
    {
        var primary = packet.SubjectEvidence[decisionIndex];
        string? primaryVerbatim = null;
        var additional = new List<ProviderSourcePartV2_1>();

        if (decision.TryGetProperty("subjectSelection", out var selection) && selection.ValueKind != JsonValueKind.Null)
        {
            if (selection.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("subject-selection-not-object");
            EnsureFields(selection, SelectionFields, "subject-selection");
            if (selection.TryGetProperty("verbatimText", out var verbatim))
            {
                if (verbatim.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(verbatim.GetString()))
                    throw new InvalidOperationException("subject-selection-verbatim-text-empty");
                primaryVerbatim = verbatim.GetString();
            }

            if (selection.TryGetProperty("additionalOwnedParts", out var parts))
            {
                if (parts.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("additional-owned-parts-not-array");
                var lastIndex = decisionIndex;
                foreach (var part in parts.EnumerateArray())
                {
                    EnsureFields(part, AdditionalPartFields, "additional-owned-part");
                    if (!part.TryGetProperty("ownedIndex", out var ownedIndexElement) || ownedIndexElement.ValueKind != JsonValueKind.Number ||
                        !ownedIndexElement.TryGetInt32(out var ownedIndex))
                        throw new InvalidOperationException("additional-owned-part-index-missing");
                    if (ownedIndex <= lastIndex || ownedIndex >= packet.SubjectEvidence.Count)
                        throw new InvalidOperationException($"additional-owned-part-index-invalid:{ownedIndex}:{decisionIndex}");
                    lastIndex = ownedIndex;
                    additional.Add(ToProviderPart(packet.SubjectEvidence[ownedIndex].SourceAlias, part));
                }
            }
        }

        return [new ProviderSourcePartV2_1(primary.SourceAlias, primaryVerbatim), .. additional];
    }

    private static ClaimSourceEndpointV2_1? ParseVisibleObject(JsonElement claim, IReadOnlyList<EvidenceNode> visible)
    {
        if (!claim.TryGetProperty("object", out var endpoint) || endpoint.ValueKind == JsonValueKind.Null)
            return null;
        if (endpoint.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("object-not-object");
        EnsureFields(endpoint, EndpointFields, "object");
        if (!endpoint.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            throw new InvalidOperationException("object-parts-missing");

        var translated = new List<ProviderSourcePartV2_1>();
        foreach (var part in parts.EnumerateArray())
        {
            EnsureFields(part, VisiblePartFields, "visible-source-part");
            if (!part.TryGetProperty("visibleIndex", out var indexElement) || indexElement.ValueKind != JsonValueKind.Number ||
                !indexElement.TryGetInt32(out var visibleIndex))
                throw new InvalidOperationException("visible-source-part-index-missing");
            if (visibleIndex < 0 || visibleIndex >= visible.Count)
                throw new InvalidOperationException($"visible-source-part-index-out-of-range:{visibleIndex}:{visible.Count}");
            translated.Add(ToProviderPart(visible[visibleIndex].SourceAlias, part));
        }
        return new ClaimSourceEndpointV2_1(translated);
    }

    private static ProviderSourcePartV2_1 ToProviderPart(string alias, JsonElement part)
    {
        string? StringOrNull(string name) =>
            part.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        int? IntOrNull(string name) =>
            part.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

        var verbatim = StringOrNull("verbatimText");
        if (verbatim is not null && verbatim.Length == 0)
            throw new InvalidOperationException("source-part-verbatim-text-empty");
        return new ProviderSourcePartV2_1(
            alias,
            verbatim,
            IntOrNull("occurrence"),
            StringOrNull("leftExactContext"),
            StringOrNull("rightExactContext"));
    }

    private static void EnsureFields(JsonElement element, IReadOnlySet<string> allowed, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{path}-not-object");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{path}-field-not-in-contract:{property.Name}");
    }

    private static string EnsureRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"{property}-missing");
        return value.GetString()!;
    }
}

public sealed record V5OwnedDecisionTranslationV3(
    IReadOnlyList<IndexedSemanticClaimProposalV2_1> Proposals);

public sealed record V5OwnedDecisionEvidenceV3(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("visibleIndex")] int VisibleIndex,
    [property: JsonPropertyName("evidence")] EvidenceNode Evidence);

public sealed record V5ContextDecisionEvidenceV3(
    [property: JsonPropertyName("visibleIndex")] int VisibleIndex,
    [property: JsonPropertyName("evidence")] EvidenceNode Evidence);

public sealed record V5OwnedDecisionPacketV3(
    [property: JsonPropertyName("subjectEvidence")] IReadOnlyList<V5OwnedDecisionEvidenceV3> SubjectEvidence,
    [property: JsonPropertyName("contextOnlyEvidence")] IReadOnlyList<V5ContextDecisionEvidenceV3> ContextOnlyEvidence,
    [property: JsonPropertyName("openOrConflictedClaims")] IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    [property: JsonPropertyName("retrievedEvidence")] IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    [property: JsonPropertyName("layoutEvidence")] IReadOnlyList<EvidenceNode> LayoutEvidence,
    [property: JsonPropertyName("visualEvidence")] IReadOnlyList<EvidenceNode> VisualEvidence);

public sealed record CanonicalOwnedDecisionRequestV3(
    [property: JsonPropertyName("composerVersion")] string ComposerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("contract")] DocumentTaskContract Contract,
    [property: JsonPropertyName("claimShapes")] IReadOnlyList<V5ClaimShapeV2_1> ClaimShapes,
    [property: JsonPropertyName("responseSchema")] object ResponseSchema,
    [property: JsonPropertyName("subjectSelectionPolicy")] object SubjectSelectionPolicy,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("packet")] V5OwnedDecisionPacketV3 Packet);

public static class V5OwnedDecisionRequestComposerV3
{
    private static readonly string Instructions = string.Join("\n", new[]
    {
        "You are a task-defined semantic reasoner.",
        "Return exactly one positional decision for every subjectEvidence item, in the same order.",
        "Decision i owns subjectEvidence[i]; never return a subject alias and never use contextOnlyEvidence as a subject.",
        "NO_CLAIM means this owned occurrence originates no task claim and must carry an empty claims array.",
        "CLAIMS means this owned occurrence originates one or more task claims.",
        "For the whole owned atom, omit subjectSelection. Use subjectSelection.verbatimText only for a strict substring copied exactly.",
        "For a multi-atom subject, add later owned atoms through additionalOwnedParts using strictly increasing ownedIndex values.",
        "A relation object may reference owned or context-only evidence only through visibleIndex.",
        "The task contract vocabulary and claimShapes are authoritative.",
        "Every claim must contain evidenceNeeds explicitly: RESOLVED sends []; OPEN and CONFLICTED send at least one need.",
        "A relation with an unknown target remains OPEN with GLOBAL_TARGET; do not invent an object.",
        "The harness owns occurrence identity, source aliases, coordinates and claim ids.",
        "Return only the declared decision schema.",
    });

    public static CanonicalOwnedDecisionRequestV3 BuildCanonical(
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var visible = V5OwnedDecisionProtocolV3.VisibleEvidence(packet);
        var visibleIndex = visible.Select((node, index) => (node.SourceAlias, index))
            .ToDictionary(item => item.SourceAlias, item => item.index, StringComparer.Ordinal);
        var owned = packet.SubjectEvidence.Select((node, index) =>
            new V5OwnedDecisionEvidenceV3(index, visibleIndex[node.SourceAlias], node)).ToArray();
        var context = packet.ContextOnlyEvidence
            .OrderBy(node => node.SourceOrdinal)
            .ThenBy(node => node.SourceAlias, StringComparer.Ordinal)
            .Select(node => new V5ContextDecisionEvidenceV3(visibleIndex[node.SourceAlias], node))
            .ToArray();

        var selectionPolicy = new
        {
            wholeOwnedAtom = new { wire = "omit subjectSelection", identity = "implicit decision position" },
            strictSubstring = new { wire = new { subjectSelection = new { verbatimText = "<exact substring>" } } },
            multiAtom = new
            {
                wire = new { subjectSelection = new { additionalOwnedParts = new[] { new { ownedIndex = 1 } } } },
                rule = "additional ownedIndex values must be strictly increasing and greater than the current decision index"
            },
            relationObject = new { wire = new { sourceParts = new[] { new { visibleIndex = 0 } } } },
        };

        return new CanonicalOwnedDecisionRequestV3(
            V5OwnedDecisionProtocolV3.ComposerVersion,
            V5OwnedDecisionProtocolV3.ProtocolVersion,
            contract,
            V5ClaimShapesV2_1.Generate(contract),
            V5OwnedDecisionProtocolV3.ResponseSchema(contract, packet),
            selectionPolicy,
            Instructions,
            new V5OwnedDecisionPacketV3(
                owned,
                context,
                packet.OpenOrConflictedClaims,
                packet.RetrievedEvidence,
                packet.LayoutEvidence,
                packet.VisualEvidence));
    }

    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacketV2_1 packet)
    {
        var request = BuildCanonical(contract, packet);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false });
        var prompt = json.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(prompt);
        return new V5ComposedSemanticRequest(
            request.ComposerVersion,
            prompt,
            Hashing.Sha256(request.Instructions.ReplaceLineEndings("\n")),
            V5OwnedDecisionProtocolV3.SchemaHash(contract, packet),
            Hashing.Sha256(prompt),
            bytes.Length);
    }
}
