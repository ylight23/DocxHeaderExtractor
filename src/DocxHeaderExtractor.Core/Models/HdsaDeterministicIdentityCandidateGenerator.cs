using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public sealed record HdsaCandidateSourceNode(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder);

public sealed record HdsaDeterministicIdentityCandidate(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

public sealed record HdsaDeterministicIdentityCandidateGeneration(
    IReadOnlyList<HdsaDeterministicIdentityCandidate> Candidates,
    string GeneratorVersion,
    bool GoldUsed)
{
    public bool CandidateGenerationIsRecallGate => false;
}

/// <summary>
/// Gold-free high-recall retrieval for identity verification. It emits inspectable pair hints
/// only; it never merges, labels a relation, or decides semantic identity.
/// </summary>
public static class HdsaDeterministicIdentityCandidateGenerator
{
    public const string Version = "hdsa-deterministic-identity-candidate-generator-v1";

    public static HdsaDeterministicIdentityCandidateGeneration Generate(
        IEnumerable<HdsaCandidateSourceNode> sourceNodes)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        var nodes = sourceNodes
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.NodeId, StringComparer.Ordinal)
            .ToArray();
        var output = new List<HdsaDeterministicIdentityCandidate>();
        for (var i = 0; i < nodes.Length; i++)
        {
            for (var j = i + 1; j < nodes.Length; j++)
            {
                var reasons = new List<string>();
                if (j == i + 1)
                    reasons.Add("ADJACENT_ORDER");
                if (Normalize(nodes[i].CanonicalText) == Normalize(nodes[j].CanonicalText))
                    reasons.Add("NORMALIZED_TEXT_AFFINITY");
                if (reasons.Count == 0)
                    continue;
                output.Add(new(
                    $"P{i + 1:D2}-{j + 1:D2}", nodes[i].NodeId, nodes[j].NodeId,
                    reasons.Order(StringComparer.Ordinal).ToArray()));
            }
        }
        return new(output, Version, false);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
}
