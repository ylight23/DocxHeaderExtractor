using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV7B;

internal static class Program
{
    private const string V7APreflightRelative = "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive";
    private const string V7AExecutionRelative = "artifacts/identity-benchmark/v7/identity-proposal/execution-v1-sparse-positive";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/identity-verification/preflight-v1-independent-positive-edge";
    private const int ExpectedOccurrences = 226;
    private const int ExpectedProposals = 58;
    private static readonly string[] ExpectedDocuments = ["DOC-0123", "DOC-0133", "DOC-0252"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await PrepareAsync(root);
            Console.WriteLine("V7B_STATUS=INDEPENDENT_EDGE_VERIFIER_PREFLIGHT_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V7B_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task PrepareAsync(string root)
    {
        var v7aPreflight = Full(root, V7APreflightRelative);
        var v7aExecution = Full(root, V7AExecutionRelative);
        var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V7B_PREFLIGHT_ALREADY_EXISTS_NO_OVERWRITE");

        using var v7aRequestsDoc = Read(Path.Combine(v7aPreflight, "requests.json"));
        using var v7aPredictionDoc = Read(Path.Combine(v7aExecution, "prediction-freeze.json"));
        using var v7aSummaryDoc = Read(Path.Combine(v7aExecution, "prediction-summary.json"));
        ValidateV7AInputs(v7aRequestsDoc.RootElement, v7aPredictionDoc.RootElement, v7aSummaryDoc.RootElement);

        var records = v7aRequestsDoc.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var attempts = v7aPredictionDoc.RootElement.GetProperty("attempts").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        var verifierRequests = new List<VerifierRequest>();
        foreach (var record in records.OrderBy(x => x.GetProperty("request").GetProperty("documentId").GetString(), StringComparer.Ordinal))
        {
            var request = record.GetProperty("request");
            var documentId = request.GetProperty("documentId").GetString()!;
            var attempt = attempts[documentId];
            Require(attempt.GetProperty("status").GetString() == "VALID", $"V7B_V7A_ATTEMPT_NOT_VALID_{documentId}");
            var response = attempt.GetProperty("response").GetProperty("response");
            var proposals = response.GetProperty("positiveProposals").EnumerateArray().ToArray();
            var candidateMap = request.GetProperty("candidatePairs").EnumerateArray().ToDictionary(x => x.GetProperty("pairId").GetString()!, x => x, StringComparer.Ordinal);
            var evidence = new List<object>();
            var verifierProposals = new List<object>();
            foreach (var proposal in proposals)
            {
                var pairId = proposal.GetProperty("pairId").GetString()!;
                Require(candidateMap.TryGetValue(pairId, out var candidate), $"V7B_UNKNOWN_V7A_PAIR_{documentId}_{pairId}");
                var left = proposal.GetProperty("left").GetString()!;
                var right = proposal.GetProperty("right").GetString()!;
                var reasons = candidate.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var evidenceStart = evidence.Count;
                var evidenceRefs = reasons.Select((_, i) => $"E{evidenceStart + i + 1:000}").ToArray();
                for (var i = 0; i < reasons.Length; i++) evidence.Add(new { @ref = evidenceRefs[i], kind = reasons[i], pairId });
                verifierProposals.Add(new
                {
                    proposalId = pairId,
                    left,
                    right,
                    proposedRelation = proposal.GetProperty("relation").GetString(),
                    proposedDirection = proposal.TryGetProperty("from", out var from) && proposal.TryGetProperty("to", out var to)
                        ? new { from = from.GetString(), to = to.GetString() }
                        : null,
                    evidenceRefs,
                });
            }

            var verifierRequest = new
            {
                schemaVersion = "a99-v7b-independent-positive-edge-verification-v1",
                documentId,
                verifierMode = "INDEPENDENT_SOURCE_BACKED_EDGE_VERIFICATION",
                independence = new
                {
                    v7aRationaleIncluded = false,
                    v7aDecisionExplanationIncluded = false,
                    v7aProposalRelationIncluded = true,
                    goldDerivedInput = false,
                    historicalPredictionIncluded = false,
                    evaluationIncluded = false,
                },
                occurrences = request.GetProperty("occurrences").Clone(),
                sourceEvidence = new { evidence = evidence.ToArray() },
                proposedEdges = verifierProposals.ToArray(),
                outputContract = new
                {
                    decisions = "exactly one decision per proposedEdges.proposalId",
                    decisionsAllowed = new[] { "ACCEPT", "REJECT", "UNRESOLVED" },
                    acceptedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
                    rejectedRelation = "NONE",
                    confidence = new[] { "HIGH", "MEDIUM", "LOW" },
                    evidenceRefs = "must reference supplied evidence refs",
                    defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
                    autoCollapse = false,
                    connectedComponentsAuthority = false,
                    clusteringRequested = false,
                },
                forbidden = new[] { "Gold", "historical predictions", "evaluation artifacts", "connected-component collapse", "automatic merge", "parent", "ROOT", "level", "new proposal IDs" },
            };
            var json = JsonSerializer.Serialize(verifierRequest, JsonOptions);
            verifierRequests.Add(new VerifierRequest(documentId, Sha256Text(json), JsonDocument.Parse(json).RootElement.Clone(), proposals.Length));
        }

        Require(verifierRequests.Count == 3 && verifierRequests.Sum(x => x.ProposalCount) == ExpectedProposals, "V7B_REQUEST_COUNTS");
        var requestHashes = verifierRequests.Select(x => x.RequestHash).ToArray();
        var requestSetHash = Sha256Text(string.Join("\n", requestHashes));
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-contract-v1",
            input = new[] { "frozen V7A positive proposals", "exact source occurrences", "source-derived evidence" },
            output = new[] { "ACCEPT", "REJECT", "UNRESOLVED" },
            acceptedRelationPolicy = "ACCEPT is eligible for a later constrained graph only; it is not auto-collapse authority.",
            defaultPolicy = "REJECT_AND_UNRESOLVED_KEEP_SPLIT",
            confidencePolicy = "audit-only; no threshold promotion",
            independence = "V7A rationale and decision explanation are excluded; only frozen proposal identity/relation is shown.",
            forbidden = new[] { "Gold", "V4H/V5/V6 predictions", "evaluation artifacts", "connected-component collapse", "automatic merge", "parent", "ROOT", "level" },
        });
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-preflight-v1",
            status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION",
            requestCount = verifierRequests.Count,
            occurrenceCount = ExpectedOccurrences,
            frozenV7AProposalCount = ExpectedProposals,
            requestHashes,
            requestSetSha256 = requestSetHash,
            sourcePreflightPath = V7APreflightRelative,
            v7aPredictionFreezePath = V7AExecutionRelative + "/prediction-freeze.json",
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            evaluationReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            autoCollapse = false,
            connectedComponentsAuthority = false,
            generalizationClaim = false,
        });
        await WriteAsync(Path.Combine(output, "requests.json"), new
        {
            schemaVersion = "a99-v7b-independent-positive-edge-verification-requests-v1",
            status = "FROZEN_SOURCE_BACKED_VERIFIER_REQUESTS",
            requestCount = verifierRequests.Count,
            occurrenceCount = ExpectedOccurrences,
            frozenV7AProposalCount = ExpectedProposals,
            requestSetSha256 = requestSetHash,
            goldDerivedInput = false,
            v7aRationaleIncluded = false,
            requests = verifierRequests.Select(x => new { request = x.Request, requestHash = x.RequestHash, proposalCount = x.ProposalCount }).ToArray(),
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V7B — independent positive-edge verification preflight\n\nFrozen V7A proposals: **{ExpectedProposals}** across **{verifierRequests.Count}** document-global requests. The verifier sees exact source occurrences and source-derived evidence, but not V7A rationale, Gold, historical predictions, evaluation artifacts, clustering consequences, or hierarchy. Provider calls: **0**.\n\nAccepted decisions are only eligible input to a later constrained graph; they are not automatic collapse authority. `REJECT` and `UNRESOLVED` keep split.\n\nRequest-set SHA256: `{requestSetHash}`.\n");
    }

    private static void ValidateV7AInputs(JsonElement requests, JsonElement prediction, JsonElement summary)
    {
        Require(requests.GetProperty("status").GetString() == "FROZEN_SOURCE_ONLY_SPARSE_POSITIVE_REQUESTS", "V7B_V7A_REQUEST_STATUS");
        Require(requests.GetProperty("requestCount").GetInt32() == 3 && requests.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences && requests.GetProperty("candidatePairCount").GetInt32() == 291, "V7B_V7A_REQUEST_COUNTS");
        Require(!requests.GetProperty("goldDerivedInput").GetBoolean() && !requests.GetProperty("v4hEvaluationIncluded").GetBoolean() && !requests.GetProperty("v5PredictionIncluded").GetBoolean() && !requests.GetProperty("v6dPredictionIncluded").GetBoolean(), "V7B_V7A_INPUT_FIREWALL");
        Require(prediction.GetProperty("status").GetString() == "V7A_PROPOSALS_FROZEN_BEFORE_EVALUATION_OR_PROMOTION" && prediction.GetProperty("providerCalls").GetInt32() == 3 && prediction.GetProperty("goldReadCount").GetInt32() == 0 && prediction.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && prediction.GetProperty("evaluationReadCount").GetInt32() == 0 && prediction.GetProperty("modelProposedOnly").GetBoolean() && !prediction.GetProperty("autoCollapse").GetBoolean(), "V7B_V7A_PREDICTION_FIREWALL");
        Require(summary.GetProperty("valid").GetInt32() == 3 && summary.GetProperty("invalidValidation").GetInt32() == 0 && summary.GetProperty("providerErrors").GetInt32() == 0 && summary.GetProperty("proposedPositiveEdges").GetInt32() == ExpectedProposals, "V7B_V7A_SUMMARY");
    }

    private sealed record VerifierRequest(string DocumentId, string RequestHash, JsonElement Request, int ProposalCount);
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
