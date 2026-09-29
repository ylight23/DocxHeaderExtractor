using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>A composed, hashed request ready to send - provider-neutral, produced by any composer version.</summary>
public sealed record V5ComposedSemanticRequest(
    string ComposerVersion,
    string Prompt,
    string PromptHash,
    string SchemaHash,
    string RequestHash,
    int Utf8Bytes);

/// <summary>
/// The harness-resolved coordinates of one claim endpoint (subject or object), after exact binding.
/// Shared by every claim protocol version: it is the binder's own output shape, not a model-facing
/// wire type, so it never needed to change when the wire did.
/// </summary>
public sealed record BoundClaimEndpoint(IReadOnlyList<BoundSourcePart> Parts)
{
    public string Identity => string.Join("|", Parts.Select(part =>
        $"{part.SourceId}:{part.Start}-{part.End}"));
}

/// <summary>
/// One claim after exact binding: the harness's own durable identity plus harness-resolved
/// coordinates. This is the runtime's only currency - the graph, transition policy, projections and
/// provenance all operate on this record and never on a model-facing proposal/response type, which is
/// exactly why the wire protocol could evolve to v2.1 without touching any of them.
/// </summary>
public sealed record BoundSemanticClaim(
    string ClaimId,
    BoundClaimEndpoint Subject,
    string Predicate,
    string? Value,
    BoundClaimEndpoint? Object,
    ClaimResolutionState State,
    IReadOnlyList<EvidenceNeed> EvidenceNeeds)
{
    public string Identity => $"{Subject.Identity}|{Predicate}|{Object?.Identity ?? Value ?? string.Empty}";
}

internal static class Hashing
{
    internal static string Sha256(string text) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
