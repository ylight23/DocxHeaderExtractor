using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline-only replay of the frozen exhaustive A2 response through the sparse-positive
/// contract. It never calls a provider and never reads Gold.
/// </summary>
public static class HdsaGlobalIdentitySparsePositiveReplayRunner
{
    private const string RequestPath =
        "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1/a2-identity/request.v1.json";
    private const string ResponsePath =
        "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1/a2-identity/response.v1.json";
    private const string OutputPath =
        "eval/a99-closed-loop/hdsa-global-identity-sparse-positive-replay/DOC-0205/replay.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var requestPath = Path.Combine(repoRoot, RequestPath.Replace('/', Path.DirectorySeparatorChar));
        var responsePath = Path.Combine(repoRoot, ResponsePath.Replace('/', Path.DirectorySeparatorChar));
        var outputPath = Path.Combine(repoRoot, OutputPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(requestPath) || !File.Exists(responsePath))
        {
            Console.WriteLine("HDSA_GLOBAL_IDENTITY_SPARSE_REPLAY_STATUS=BLOCKED");
            Console.WriteLine("REASON=FROZEN_A2_ARTIFACT_MISSING");
            return 1;
        }

        var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, ct));
        var request = JsonSerializer.Deserialize<HdsaGlobalIdentityRelationRequest>(
            requestDocument.RootElement.GetProperty("request").GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("SPARSE_REPLAY_REQUEST_INVALID");
        var responseWrapper = JsonSerializer.Deserialize<ResponseWrapper>(
            await File.ReadAllTextAsync(responsePath, ct), JsonOptions)
            ?? throw new InvalidDataException("SPARSE_REPLAY_RESPONSE_WRAPPER_INVALID");
        var exhaustive = HdsaGlobalIdentityRelationContract.Parse(responseWrapper.RawResponse);
        var sparse = HdsaGlobalIdentitySparsePositiveContract.FromExhaustive(exhaustive);
        var validation = HdsaGlobalIdentitySparsePositiveContract.Validate(request, sparse);
        var graph = GraphStats(exhaustive);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-hdsa-global-identity-sparse-positive-replay-v1",
            documentId = "DOC-0205",
            inputRequestSha256 = requestDocument.RootElement.GetProperty("requestSha256").GetString(),
            inputResponseSha256 = responseWrapper.ResponseSha256,
            source = "FROZEN_A2_EXHAUSTIVE_RESPONSE",
            providerCalls = 0,
            modelCalls = 0,
            goldReadBeforeFreeze = false,
            exhaustive = new
            {
                pairCount = exhaustive.Relations.Count,
                positiveRelationCount = graph.PositiveCount,
                distinctRelationCount = graph.DistinctCount,
                nodeCount = graph.NodeCount,
                positiveComponentCount = graph.ComponentSizes.Count,
                positiveComponentSizes = graph.ComponentSizes,
                distinctInsidePositiveComponents = graph.DistinctInsidePositiveComponents,
                nodesWithMultipleContinuationTargets = graph.NodesWithMultipleContinuationTargets,
                continuationCycle = graph.ContinuationCycle,
            },
            sparsePositiveProjection = new
            {
                positiveRelationCount = sparse.PositiveRelations.Count,
                noClaimPairCount = request.Pairs.Count - sparse.PositiveRelations.Count,
                omittedPairsMeanNoClaim = true,
                response = sparse,
                validation,
            },
            causalInterpretation = validation.Accepted
                ? "SPARSE_POSITIVE_CONTRACT_ACCEPTED_FROZEN_POSITIVES"
                : "SPARSE_POSITIVE_VALIDATOR_BLOCKED_FROZEN_POSITIVES",
        }, JsonOptions) + Environment.NewLine, ct);

        Console.WriteLine("HDSA_GLOBAL_IDENTITY_SPARSE_REPLAY_STATUS=" +
            (validation.Accepted ? "PASS" : "BLOCKED"));
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"EXHAUSTIVE_POSITIVE_RELATIONS={graph.PositiveCount}");
        Console.WriteLine($"NO_CLAIM_PAIRS={request.Pairs.Count - sparse.PositiveRelations.Count}");
        Console.WriteLine("REASON=" + (validation.RejectionReason ?? "NONE"));
        return validation.Accepted ? 0 : 1;
    }

    private static GraphSummary GraphStats(HdsaGlobalIdentityRelationResponse response)
    {
        var positive = response.Relations
            .Where(item => item.Relation is "SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF").ToArray();
        var distinct = response.Relations.Where(item => item.Relation == "DISTINCT").ToArray();
        var nodes = response.Relations.SelectMany(item => new[] { item.Left, item.Right })
            .ToHashSet(StringComparer.Ordinal);
        var dsu = new DisjointSet(nodes);
        foreach (var relation in positive) dsu.Union(relation.Left, relation.Right);
        var components = nodes.GroupBy(dsu.Find, StringComparer.Ordinal)
            .Select(group => group.Order(StringComparer.Ordinal).ToArray())
            .ToArray();
        var continuation = positive.Where(item => item.Relation == "CONTINUATION_OF").ToArray();
        return new(
            positive.Length,
            distinct.Length,
            nodes.Count,
            components.Select(item => item.Length).OrderDescending().ToArray(),
            distinct.Count(item => string.Equals(dsu.Find(item.Left), dsu.Find(item.Right), StringComparison.Ordinal)),
            continuation.GroupBy(item => item.Right, StringComparer.Ordinal).Count(group => group.Count() > 1),
            HasCycle(continuation.Select(item => (item.Right, item.Left)).ToArray(), nodes));
    }

    private static bool HasCycle(
        IReadOnlyList<(string From, string To)> edges, IReadOnlySet<string> nodes)
    {
        var adjacency = edges.GroupBy(item => item.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.To).ToArray(),
                StringComparer.Ordinal);
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            state[node] = 1;
            if (adjacency.TryGetValue(node, out var children))
                foreach (var child in children)
                {
                    if (state.TryGetValue(child, out var childState) && childState == 1) return true;
                    if (!state.ContainsKey(child) && Visit(child)) return true;
                }
            state[node] = 2;
            return false;
        }
        return nodes.Any(node => !state.ContainsKey(node) && Visit(node));
    }

    private sealed record GraphSummary(
        int PositiveCount,
        int DistinctCount,
        int NodeCount,
        IReadOnlyList<int> ComponentSizes,
        int DistinctInsidePositiveComponents,
        int NodesWithMultipleContinuationTargets,
        bool ContinuationCycle);

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent;
        public DisjointSet(IEnumerable<string> nodes) =>
            _parent = nodes.ToDictionary(item => item, StringComparer.Ordinal);
        public string Find(string node) =>
            _parent[node] == node ? node : _parent[node] = Find(_parent[node]);
        public void Union(string left, string right)
        {
            left = Find(left); right = Find(right);
            if (!StringComparer.Ordinal.Equals(left, right)) _parent[right] = left;
        }
    }

    private sealed record ResponseWrapper(
        string ResponseSha256,
        [property: System.Text.Json.Serialization.JsonPropertyName("rawResponse")] string RawResponse);
}
