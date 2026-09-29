using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Provider-neutral V5 v2.1 request composition. It exposes the complete recursive schema.</summary>
public static class V5SemanticRequestComposerV2_1
{
    public const string Version = "v5-semantic-request-composer-2.1";

    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacket packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var instructions = string.Join("\n", new[]
        {
            "You are a task-defined semantic reasoner.",
            "The task contract vocabulary is authoritative; use only declared predicates and relations.",
            "Every claim must contain evidenceNeeds explicitly: RESOLVED sends evidenceNeeds: []; OPEN and CONFLICTED send at least one need.",
            "A source part never has a selectionMode field. sourceAlias alone means the whole occurrence; adding verbatimText means an exact substring of it.",
            "Only ownedEvidence may be used as a claim subject.",
            "visibleEvidence is context and may serve as a relation object or target, never as a claim subject.",
            "Never propose a claim whose subject is a visible-only (halo) occurrence.",
            "Use the exact supplied sourceAlias; never invent one, and never invent text or coordinates.",
            "A relation with an unknown target remains OPEN with evidenceNeeds including GLOBAL_TARGET; do not invent an object.",
            "OPEN is preferable to fabricated certainty.",
            "The harness owns claim identity. Do not emit claimId.",
            "existingClaimId appears only for a claim you were explicitly given to refine.",
            "If nothing is claimable, return exactly {\"claims\":[]}.",
            "Return only the declared source-backed claim schema.",
        });
        var payload = new
        {
            composerVersion = Version,
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            contract,
            responseSchema = SemanticClaimContractV2_1.Schema(),
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
            SemanticClaimContractV2_1.SchemaHash(),
            Hashing.Sha256(prompt),
            bytes.Length);
    }
}
