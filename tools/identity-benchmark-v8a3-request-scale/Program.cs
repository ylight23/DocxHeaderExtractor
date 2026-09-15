using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV8A3RequestScale;

internal static class Program
{
    private const string V8A2Relative = "artifacts/identity-benchmark/v8a2/new-source-intake-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v8a3/request-scale-audit";
    private const string V8A2Authority = "579b3fd";
    private const int ContextLimitTokens = 1_000_000;
    private const int MaxOutputTokens = 16_000;
    private const long TotalInputBudgetTokens = 5_000_000;
    private const double BytesPerEstimatedToken = 4.0;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8A3_ERROR={ex.Message}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var input = Full(root, V8A2Relative);
        var output = Full(root, OutputRelative);
        Require(Directory.Exists(input), "V8A2_INPUT_NOT_FOUND");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);

        var v8a2Manifest = Load(Path.Combine(input, "manifest.json"));
        Require(v8a2Manifest.GetProperty("status").GetString() == "READY_FOR_V8_PROVIDER_EXECUTION", "V8A2_NOT_READY");
        Require(v8a2Manifest.GetProperty("providerCalls").GetInt32() == 0, "V8A2_PROVIDER_CALLS_NONZERO");
        Require(v8a2Manifest.GetProperty("goldReadCount").GetInt32() == 0, "V8A2_GOLD_READS_NONZERO");

        var proposer = Load(Path.Combine(input, "proposer-requests.json"));
        var falsifier = Load(Path.Combine(input, "falsifier-requests.json"));
        var candidates = Load(Path.Combine(input, "candidate-pairs.json"));
        var occurrences = Load(Path.Combine(input, "occurrences.json"));

        var proposerRows = ParseRequests(proposer, "PROPOSER");
        var falsifierRows = ParseRequests(falsifier, "FALSIFIER");
        Require(proposerRows.Count == 6 && falsifierRows.Count == 6, "V8A2_REQUEST_COUNT_DRIFT");
        Require(proposerRows.Select(x => x.DocumentId).Order(StringComparer.Ordinal).SequenceEqual(falsifierRows.Select(x => x.DocumentId).Order(StringComparer.Ordinal), StringComparer.Ordinal), "V8A2_STREAM_DOCUMENT_SET_DRIFT");

        var requestRows = proposerRows.Concat(falsifierRows).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Stream, StringComparer.Ordinal).ToArray();
        var documentReports = requestRows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Select(group => BuildDocumentReport(group.Key, group.ToArray())).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        var candidateFanout = BuildCandidateFanout(candidates, occurrences);
        var requestSizeDistribution = Distribution(requestRows.Select(x => (long)x.RequestBytes));
        var tokenDistribution = Distribution(requestRows.Select(x => (long)x.EstimatedInputTokens));
        var totalBytes = requestRows.Sum(x => (long)x.RequestBytes);
        var totalTokens = requestRows.Sum(x => (long)x.EstimatedInputTokens);
        var maxTokens = requestRows.Max(x => x.EstimatedInputTokens);
        var safeInput = requestRows.All(x => x.EstimatedInputTokens < ContextLimitTokens * .90);
        var projectedFits = requestRows.All(x => (long)x.EstimatedInputTokens + MaxOutputTokens <= ContextLimitTokens);
        var totalFits = totalTokens <= TotalInputBudgetTokens;
        var status = safeInput && projectedFits && totalFits ? "READY_FOR_V8_PROVIDER_AUTHORIZATION" : "BLOCKED_ON_PRE_PROVIDER_SCALABILITY";
        var blockers = new List<string>();
        if (!safeInput) blockers.Add("REQUEST_INPUT_NEAR_OR_OVER_90_PERCENT_CONTEXT_LIMIT");
        if (!projectedFits) blockers.Add("INPUT_PLUS_CONFIGURED_MAX_OUTPUT_EXCEEDS_CONTEXT_LIMIT");
        if (!totalFits) blockers.Add("TOTAL_ESTIMATED_INPUT_TOKEN_BUDGET_EXCEEDED");

        var hashVerification = new
        {
            v8a2Authority = V8A2Authority,
            proposer = VerifySet(proposer, proposerRows, v8a2Manifest.GetProperty("proposerRequestSetSha256").GetString()!),
            falsifier = VerifySet(falsifier, falsifierRows, v8a2Manifest.GetProperty("falsifierRequestSetSha256").GetString()!),
            allIndividualRequestHashesMatch = requestRows.All(x => x.IndividualHashMatches),
            sourceArtifactsUnchanged = true,
        };
        Require(hashVerification.proposer.SetHashMatches && hashVerification.falsifier.SetHashMatches && hashVerification.allIndividualRequestHashesMatch, $"V8A2_REQUEST_HASH_DRIFT proposerSet={hashVerification.proposer.SetHashMatches} falsifierSet={hashVerification.falsifier.SetHashMatches} proposerIndividual={hashVerification.proposer.IndividualRequestHashesMatch} falsifierIndividual={hashVerification.falsifier.IndividualRequestHashesMatch}");

        var audit = new
        {
            artifactKind = "a99_identity_benchmark_v8a3_request_scale_audit",
            schemaVersion = "a99-v8a3-request-scale-audit-v1",
            status,
            authority = new { v8a2Commit = V8A2Authority, v8a2ManifestSha256 = Sha256File(Path.Combine(input, "manifest.json")), requestBodiesMutated = false },
            firewall = new { providerCalls = 0, modelCalls = 0, goldReadCount = 0, historicalPredictionReadCount = 0, historicalEvaluationReadCount = 0, semanticEvaluation = false, v8a2ArtifactsMutated = false },
            frozenInput = new
            {
                selectedDocuments = v8a2Manifest.GetProperty("selectedDocumentCount").GetInt32(),
                occurrences = v8a2Manifest.GetProperty("occurrenceCount").GetInt32(),
                candidatePairs = v8a2Manifest.GetProperty("candidatePairCount").GetInt32(),
                proposerRequests = proposerRows.Count,
                falsifierRequests = falsifierRows.Count,
                proposerRequestSetSha256 = v8a2Manifest.GetProperty("proposerRequestSetSha256").GetString(),
                falsifierRequestSetSha256 = v8a2Manifest.GetProperty("falsifierRequestSetSha256").GetString(),
            },
            tokenizerEstimate = new { name = "UTF8_BYTES_DIV_4_CEILING_V1", bytesPerToken = BytesPerEstimatedToken, exactTokenizerAvailableLocally = false, valuesAreEstimated = true },
            contextPolicy = new { configuredContextLimitTokens = ContextLimitTokens, safeInputThresholdTokens = (int)(ContextLimitTokens * .90), maxOutputTokens = MaxOutputTokens, totalInputBudgetTokens = TotalInputBudgetTokens, providerLimitVerified = false },
            global = new
            {
                totalRequestBytes = totalBytes,
                totalEstimatedInputTokens = totalTokens,
                maximumRequestBytes = requestRows.Max(x => x.RequestBytes),
                maximumEstimatedInputTokens = maxTokens,
                requestSizeDistribution,
                tokenDistribution,
                repeatedSourceContext = new
                {
                    withinRequestRepeatedOccurrenceTextBytes = documentReports.Sum(x => x.WithinRequestRepeatedOccurrenceTextBytes),
                    crossStreamRepeatedOccurrencePayloadBytes = documentReports.Sum(x => x.CrossStreamRepeatedOccurrencePayloadBytes),
                    crossStreamRepeatedCandidatePayloadBytes = documentReports.Sum(x => x.CrossStreamRepeatedCandidatePayloadBytes),
                    note = "Repeated occurrence text counts exact repeated source strings beyond their first occurrence; cross-stream duplication counts source/candidate payload repeated in proposer and falsifier requests."
                },
                candidateFanout,
            },
            perDocument = documentReports,
            perRequest = requestRows.Select(x => x.ToPublic()).ToArray(),
            hashVerification,
            gate = new { allRequestsBelowSafeContextThreshold = safeInput, allRequestsFitWithConfiguredMaxOutput = projectedFits, totalEstimatedInputWithinBudget = totalFits, blockers, next = status == "READY_FOR_V8_PROVIDER_AUTHORIZATION" ? "Provider authorization may be considered; no authorization is granted by this audit." : "Prepare a new V8B source-only sparse candidate retrieval preflight; do not mutate V8A2." },
        };

        Write(Path.Combine(output, "manifest.json"), audit);
        Write(Path.Combine(output, "per-request.json"), new { schemaVersion = "a99-v8a3-per-request-v1", requests = requestRows.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "per-document.json"), new { schemaVersion = "a99-v8a3-per-document-v1", documents = documentReports });
        Write(Path.Combine(output, "request-size-distribution.json"), new { schemaVersion = "a99-v8a3-request-size-distribution-v1", requestBytes = requestSizeDistribution, estimatedInputTokens = tokenDistribution, contextPolicy = audit.contextPolicy });
        Write(Path.Combine(output, "candidate-fanout-distribution.json"), new { schemaVersion = "a99-v8a3-candidate-fanout-v1", candidateFanout });
        Write(Path.Combine(output, "request-hash-verification.json"), new { schemaVersion = "a99-v8a3-request-hash-verification-v1", hashVerification });
        Write(Path.Combine(output, "firewall.json"), audit.firewall);
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(audit, documentReports), new UTF8Encoding(false));
        Console.WriteLine($"V8A3_STATUS={status} PROVIDER_CALLS=0 GOLD_READ_COUNT=0 TOTAL_INPUT_TOKENS_EST={totalTokens}");
    }

    private static DocumentReport BuildDocumentReport(string documentId, IReadOnlyList<RequestRow> rows)
    {
        var proposer = rows.Single(x => x.Stream == "PROPOSER");
        var falsifier = rows.Single(x => x.Stream == "FALSIFIER");
        var occurrenceTexts = proposer.Request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("text").GetString() ?? "").ToArray();
        var totalTextBytes = occurrenceTexts.Sum(x => (long)Encoding.UTF8.GetByteCount(x));
        var uniqueTextBytes = occurrenceTexts.Distinct(StringComparer.Ordinal).Sum(x => (long)Encoding.UTF8.GetByteCount(x));
        var proposerOccurrencePayload = proposer.OccurrencePayloadBytes;
        var falsifierOccurrencePayload = falsifier.OccurrencePayloadBytes;
        var proposerCandidatePayload = proposer.CandidatePayloadBytes;
        var falsifierCandidatePayload = falsifier.CandidatePayloadBytes;
        var candidatePairs = proposer.Request.GetProperty("candidatePairs").GetArrayLength();
        var occCount = proposer.Request.GetProperty("occurrences").GetArrayLength();
        return new DocumentReport(
            documentId, occCount, candidatePairs, proposer.RequestBytes, falsifier.RequestBytes,
            proposer.EstimatedInputTokens, falsifier.EstimatedInputTokens,
            proposer.OccurrencePayloadBytes, proposer.CandidatePayloadBytes,
            totalTextBytes - uniqueTextBytes, totalTextBytes == 0 ? 0 : (double)(totalTextBytes - uniqueTextBytes) / totalTextBytes,
            Math.Min(proposerOccurrencePayload, falsifierOccurrencePayload), Math.Min(proposerCandidatePayload, falsifierCandidatePayload),
            BuildDistribution(new[] { (long)proposer.RequestBytes, (long)falsifier.RequestBytes }),
            BuildDistribution(new[] { (long)proposer.EstimatedInputTokens, (long)falsifier.EstimatedInputTokens }));
    }

    private static object BuildCandidateFanout(JsonElement candidatesRoot, JsonElement occurrencesRoot)
    {
        var all = new List<int>();
        var perDocument = new List<object>();
        foreach (var doc in candidatesRoot.GetProperty("candidates").EnumerateArray())
        {
            var documentId = doc.GetProperty("sourceId").GetString()!;
            var occurrenceDoc = occurrencesRoot.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("sourceId").GetString() == documentId);
            var degrees = occurrenceDoc.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("occurrenceId").GetString()!, _ => 0, StringComparer.Ordinal);
            foreach (var pair in doc.GetProperty("candidates").EnumerateArray())
            {
                degrees[pair.GetProperty("left").GetString()!]++;
                degrees[pair.GetProperty("right").GetString()!]++;
            }
            all.AddRange(degrees.Values);
            perDocument.Add(new { documentId, candidatePairs = doc.GetProperty("candidates").GetArrayLength(), occurrenceCount = degrees.Count, degree = Distribution(degrees.Values.Select(x => (long)x)) });
        }
        return new { overall = Distribution(all.Select(x => (long)x)), perDocument };
    }

    private static HashVerification VerifySet(JsonElement root, IReadOnlyList<RequestRow> rows, string expectedHash)
    {
        var elements = root.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("request").Clone()).ToArray();
        var prettyOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        var compactOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = false };
        var candidateHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jsonElementArrayPretty"] = Sha256Text(JsonSerializer.Serialize(elements, prettyOptions)),
            ["jsonElementArrayCompact"] = Sha256Text(JsonSerializer.Serialize(elements, compactOptions)),
            ["objectArrayPretty"] = Sha256Text(JsonSerializer.Serialize(elements.Cast<object>().ToArray(), prettyOptions)),
            ["objectArrayCompact"] = Sha256Text(JsonSerializer.Serialize(elements.Cast<object>().ToArray(), compactOptions)),
            ["individualCanonicalJoinedWithNewline"] = Sha256Text(string.Join("\n", rows.Select(x => Canonical(x.Request)))),
        };
        var set = candidateHashes["jsonElementArrayPretty"];
        return new HashVerification(set, expectedHash, string.Equals(set, expectedHash, StringComparison.OrdinalIgnoreCase), rows.All(x => x.IndividualHashMatches), candidateHashes);
    }

    private static IReadOnlyList<RequestRow> ParseRequests(JsonElement root, string stream)
    {
        return root.GetProperty("requests").EnumerateArray().Select(item =>
        {
            var request = item.GetProperty("request").Clone();
            var serialized = Canonical(request);
            var occurrencePayload = Canonical(request.GetProperty("occurrences"));
            var candidatePayload = Canonical(request.GetProperty("candidatePairs"));
            var computedHash = Sha256Text(serialized);
            var frozenHash = item.GetProperty("requestHash").GetString()!;
            return new RequestRow(stream, request.GetProperty("sourceId").GetString()!, request, frozenHash, computedHash, Encoding.UTF8.GetByteCount(serialized), EstimateTokens(Encoding.UTF8.GetByteCount(serialized)), Encoding.UTF8.GetByteCount(occurrencePayload), Encoding.UTF8.GetByteCount(candidatePayload), Sha256Text(occurrencePayload), Sha256Text(candidatePayload), string.Equals(computedHash, frozenHash, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    private static string BuildReport(dynamic audit, IReadOnlyList<DocumentReport> documents)
    {
        var lines = new List<string>
        {
            "# V8A3 — exact request scale audit",
            "",
            $"Status: **{audit.status}**.",
            "",
            $"This audit consumes only the frozen V8A2 request corpus at `{V8A2Authority}`. It does not mutate V8A2, read Gold/predictions, or call a provider.",
            "",
            $"- Requests: **{audit.frozenInput.proposerRequests + audit.frozenInput.falsifierRequests}** (6 proposer + 6 falsifier)",
            $"- Candidate pairs: **{audit.frozenInput.candidatePairs:N0}**; occurrences: **{audit.frozenInput.occurrences:N0}**",
            $"- Total serialized request bytes: **{audit.global.totalRequestBytes:N0}**",
            $"- Estimated input tokens: **{audit.global.totalEstimatedInputTokens:N0}** (`ceil(UTF-8 bytes / 4)`)",
            $"- Maximum request: **{audit.global.maximumRequestBytes:N0} bytes / {audit.global.maximumEstimatedInputTokens:N0} estimated tokens**",
            $"- Configured context: **{ContextLimitTokens:N0}** tokens; configured max output: **{MaxOutputTokens:N0}**",
            $"- Provider/model calls: **0/0**; Gold reads: **0**",
            "",
            "## Per document",
            "",
            "| Document | Occurrences | Candidates | Proposer bytes/tokens | Falsifier bytes/tokens | Repeated text bytes | Cross-stream occurrence payload |",
            "|---|---:|---:|---:|---:|---:|---:|",
        };
        foreach (var d in documents) lines.Add($"| {d.DocumentId} | {d.Occurrences:N0} | {d.CandidatePairs:N0} | {d.ProposerBytes:N0}/{d.ProposerTokens:N0} | {d.FalsifierBytes:N0}/{d.FalsifierTokens:N0} | {d.WithinRequestRepeatedOccurrenceTextBytes:N0} | {d.CrossStreamRepeatedOccurrencePayloadBytes:N0} |");
        lines.AddRange(new[] { "", "## Gate", "", $"- All requests below 90% input context threshold: **{audit.gate.allRequestsBelowSafeContextThreshold}**", $"- All requests fit with configured max output: **{audit.gate.allRequestsFitWithConfiguredMaxOutput}**", $"- Total estimated input within 5,000,000-token budget: **{audit.gate.totalEstimatedInputWithinBudget}**", $"- Decision: **{audit.status}**", "", "Request bodies and candidate semantics were not changed. If blocked, the next experiment is a new V8B source-only sparse-retrieval preflight; V8A2 remains immutable." });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static object Distribution(IEnumerable<long> values) => BuildDistribution(values);
    private static object BuildDistribution(IEnumerable<long> input)
    {
        var values = input.OrderBy(x => x).ToArray();
        if (values.Length == 0) return new { count = 0, min = 0L, p50 = 0L, p90 = 0L, p95 = 0L, p99 = 0L, max = 0L, mean = 0d, total = 0L };
        long Percentile(double p) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * p) - 1)];
        return new { count = values.Length, min = values[0], p50 = Percentile(.50), p90 = Percentile(.90), p95 = Percentile(.95), p99 = Percentile(.99), max = values[^1], mean = values.Average(), total = values.Sum() };
    }

    private static int EstimateTokens(int bytes) => (int)Math.Ceiling(bytes / BytesPerEstimatedToken);
    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Canonical(JsonElement element) => JsonSerializer.Serialize(element, JsonOptions);
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record RequestRow(string Stream, string DocumentId, JsonElement Request, string FrozenHash, string ComputedHash, int RequestBytes, int EstimatedInputTokens, int OccurrencePayloadBytes, int CandidatePayloadBytes, string OccurrencePayloadSha256, string CandidatePayloadSha256, bool IndividualHashMatches)
    {
        public object ToPublic() => new { stream = Stream, documentId = DocumentId, frozenRequestSha256 = FrozenHash, computedRequestSha256 = ComputedHash, individualHashMatches = IndividualHashMatches, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, occurrencePayloadBytes = OccurrencePayloadBytes, candidatePayloadBytes = CandidatePayloadBytes, occurrencePayloadSha256 = OccurrencePayloadSha256, candidatePayloadSha256 = CandidatePayloadSha256 };
    }
    private sealed record DocumentReport(string DocumentId, int Occurrences, int CandidatePairs, int ProposerBytes, int FalsifierBytes, int ProposerTokens, int FalsifierTokens, int OccurrencePayloadBytes, int CandidatePayloadBytes, long WithinRequestRepeatedOccurrenceTextBytes, double WithinRequestRepeatedOccurrenceTextRatio, long CrossStreamRepeatedOccurrencePayloadBytes, long CrossStreamRepeatedCandidatePayloadBytes, object RequestByteDistribution, object TokenDistribution);
    private sealed record HashVerification(string ComputedSetSha256, string FrozenSetSha256, bool SetHashMatches, bool IndividualRequestHashesMatch, IReadOnlyDictionary<string, string> CandidateRebuildHashes);
}
