using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// v2.1's evidence packet. Deliberately disjoint - <see cref="SubjectEvidence"/> (owned) and
/// <see cref="ContextOnlyEvidence"/> (visible minus owned, i.e. halo) never share an alias - so
/// subject eligibility is structural rather than something the model has to infer from prose or
/// from set membership across two overlapping lists. A real canary found the model proposing a halo
/// occurrence as a claim subject when owned/visible overlapped in the wire; the binder correctly
/// refused it, but the ambiguity that produced the proposal is removed here, at the request.
/// </summary>
public sealed record V5EvidencePacketV2_1(
    [property: JsonPropertyName("subjectEvidence")] IReadOnlyList<EvidenceNode> SubjectEvidence,
    [property: JsonPropertyName("contextOnlyEvidence")] IReadOnlyList<EvidenceNode> ContextOnlyEvidence,
    [property: JsonPropertyName("openOrConflictedClaims")] IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    [property: JsonPropertyName("retrievedEvidence")] IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    [property: JsonPropertyName("layoutEvidence")] IReadOnlyList<EvidenceNode> LayoutEvidence,
    [property: JsonPropertyName("visualEvidence")] IReadOnlyList<EvidenceNode> VisualEvidence);

/// <summary>
/// The provider/model-neutral question itself: task vocabulary, arity, response schema,
/// source-reference policy, instructions and evidence - nothing about how it will be transported
/// (no model name, provider, reasoning effort, temperature, response_format or tool_choice). Property
/// names and declaration order intentionally reproduce the shape <see cref="V5SemanticRequestComposerV2_1"/>
/// serialized before this type existed, byte for byte - this is an extraction, not a redesign.
/// </summary>
public sealed record CanonicalSemanticRequestV2_1(
    [property: JsonPropertyName("composerVersion")] string ComposerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("contract")] DocumentTaskContract Contract,
    [property: JsonPropertyName("claimShapes")] IReadOnlyList<V5ClaimShapeV2_1> ClaimShapes,
    [property: JsonPropertyName("responseSchema")] object ResponseSchema,
    [property: JsonPropertyName("sourceSelectionPolicy")] object SourceSelectionPolicy,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("packet")] V5EvidencePacketV2_1 Packet);

/// <summary>Provider-neutral V5 v2.1 request composition. It exposes the complete recursive schema.</summary>
public static class V5SemanticRequestComposerV2_1
{
    public const string Version = "v5-semantic-request-composer-2.1";

    /// <summary>
    /// Current measured Qwen3.7 baseline wording, preserved exactly. This is not the place to shorten
    /// or re-word instructions - doing so at the same time as extracting <see cref="CanonicalSemanticRequestV2_1"/>
    /// would make a later quality change impossible to attribute to the model, the prompt, or the
    /// refactor.
    /// </summary>
    private static readonly string Instructions = string.Join("\n", new[]
    {
        "You are a task-defined semantic reasoner.",
        "The task contract vocabulary is authoritative; use only declared predicates and relations.",
        "claimShapes is authoritative for arity: a UNARY predicate must never carry an object; a RELATION must never carry a value and requires an object once RESOLVED.",
        "Every claim must contain evidenceNeeds explicitly: RESOLVED sends evidenceNeeds: []; OPEN and CONFLICTED send at least one need.",
        "A source part never has a selectionMode field. sourceSelectionPolicy in this request defines the exact wire shape for each of the three legal cases: whole atom, strict substring, multi-atom region.",
        "Default to sourceAlias only. Do NOT include verbatimText when the intended selection is the whole atom.",
        "verbatimText is exceptional: use it only when the intended selection is a strict substring smaller than the atom. Never rewrite, normalize, repair, respell, re-space or paraphrase source text inside verbatimText - copy it exactly.",
        "If the intended source selection spans more than one evidence atom, do not place the combined text into one sourcePart. Emit one sourcePart per contributing sourceAlias, in source order; a fully-included atom uses sourceAlias alone, and only a genuinely partial boundary atom may carry verbatimText.",
        "A claim subject must come from subjectEvidence only. contextOnlyEvidence is never an eligible claim subject; it may serve as a relation's object or as context.",
        "Use the exact supplied sourceAlias; never invent one, and never invent text or coordinates.",
        "A relation with an unknown target remains OPEN with evidenceNeeds including GLOBAL_TARGET; do not invent an object.",
        "OPEN is preferable to fabricated certainty.",
        "The harness owns claim identity. Do not emit claimId.",
        "existingClaimId appears only for a claim you were explicitly given to refine.",
        "If nothing is claimable, return exactly {\"claims\":[]}.",
        "Return only the declared source-backed claim schema.",
    });

    /// <summary>Deterministic construction only - no serialization, no hashing, no provider or model concept.</summary>
    public static CanonicalSemanticRequestV2_1 BuildCanonical(DocumentTaskContract contract, V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        return new CanonicalSemanticRequestV2_1(
            Version,
            V5Protocol.ClaimSchemaVersionV2_1,
            contract,
            V5ClaimShapesV2_1.Generate(contract),
            SemanticClaimContractV2_1.Schema(),
            V5SourceSelectionPolicy.Generate(),
            Instructions,
            packet);
    }

    /// <summary>
    /// The one serialization of <see cref="CanonicalSemanticRequestV2_1"/> every future provider
    /// adapter must consume identically. <see cref="V5ComposedSemanticRequest.RequestHash"/> is the
    /// SemanticRequestHash: it changes only when the canonical request itself changes, never when a
    /// provider carrier does.
    /// </summary>
    public static V5ComposedSemanticRequest Serialize(CanonicalSemanticRequestV2_1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(CanonicalJson.Options)
        {
            WriteIndented = false,
        });
        var prompt = json.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(prompt);
        return new V5ComposedSemanticRequest(
            request.ComposerVersion,
            prompt,
            Hashing.Sha256(request.Instructions.ReplaceLineEndings("\n")),
            SemanticClaimContractV2_1.SchemaHash(),
            Hashing.Sha256(prompt),
            bytes.Length);
    }

    /// <summary>Facade preserved for every existing caller: build then serialize, byte-identical to the pre-extraction output.</summary>
    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacketV2_1 packet) =>
        Serialize(BuildCanonical(contract, packet));
}
