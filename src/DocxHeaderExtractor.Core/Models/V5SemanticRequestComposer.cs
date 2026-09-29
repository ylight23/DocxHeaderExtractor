using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

public sealed record V5EvidencePacket(
    IReadOnlyList<EvidenceNode> OwnedEvidence,
    IReadOnlyList<EvidenceNode> VisibleEvidence,
    IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    IReadOnlyList<EvidenceNode> LayoutEvidence,
    IReadOnlyList<EvidenceNode> VisualEvidence);

public sealed record V5ComposedSemanticRequest(
    string ComposerVersion,
    string Prompt,
    string PromptHash,
    string SchemaHash,
    string RequestHash,
    int Utf8Bytes);

/// <summary>
/// Deterministic provider-neutral request construction. It serializes observations and task vocabulary;
/// it does not choose a document meaning, invoke a provider, or read evaluation data.
/// </summary>
public static class V5SemanticRequestComposer
{
    public const string Version = "v5-semantic-request-composer-1";

    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacket packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var instructions = string.Join("\n", new[]
        {
            "You are a task-defined semantic reasoner.",
            "The task contract vocabulary is authoritative; use only declared predicates and relations.",
            "Evidence is observation, not truth. Every claim must be grounded in exact source parts.",
            "For unresolved questions emit OPEN with an explicit evidence need; do not invent offsets, coordinates, or facts.",
            "Retrieval, layout, and visual observations are evidence only and do not become claims automatically.",
            "Return only the declared source-backed claim schema.",
        });
        var payload = new
        {
            composerVersion = Version,
            contract,
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
            SemanticClaimContract.SchemaHash(),
            Hashing.Sha256(prompt),
            bytes.Length);
    }
}
