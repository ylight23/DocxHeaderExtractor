using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Provider-neutral V5 v2 request composition. It exposes the complete recursive schema.</summary>
public static class V5SemanticRequestComposerV2
{
    public const string Version = "v5-semantic-request-composer-2";

    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacket packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var instructions = string.Join("\n", new[]
        {
            "You are a task-defined semantic reasoner.",
            "The task contract vocabulary is authoritative; use only declared predicates and relations.",
            "Every claim must have a subject grounded in one or more exact source parts.",
            "For a relation, provide an object grounded in exact source parts; for a unary predicate, provide a value when applicable.",
            "For unresolved questions emit OPEN with at least one evidenceNeeds item; do not invent offsets, coordinates, aliases, or facts.",
            "The harness owns claim identity. Do not emit claimId. Use existingClaimId only to correlate a refinement proposal.",
            "If nothing is claimable, return exactly {\"claims\":[]}.",
            "Return only the declared source-backed claim schema.",
        });
        var payload = new
        {
            composerVersion = Version,
            protocolVersion = V5Protocol.ClaimSchemaVersionV2,
            contract,
            responseSchema = SemanticClaimContractV2.Schema(),
            instructions,
            packet,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(CanonicalJson.Options)
        {
            WriteIndented = false,
        });
        var prompt = json.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(prompt);
        return new V5ComposedSemanticRequest(
            Version,
            prompt,
            Hashing.Sha256(instructions.ReplaceLineEndings("\n")),
            SemanticClaimContractV2.SchemaHash(),
            Hashing.Sha256(prompt),
            bytes.Length);
    }
}
