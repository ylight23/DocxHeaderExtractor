using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace IdentityPromotionReadinessAudit;

internal static class Program
{
    private const string BenchmarkRoot = "artifacts/identity-benchmark/v1";
    private const string ManifestFile = "manifest.json";
    private const string RequestManifestFile = "request-manifest.json";
    private const string CandidateFile = "candidate-set.json";
    private const string SourceCatalogFile = "source-catalog.json";
    private const string RunnerFile = "src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs";
    private const int ContextLimitTokens = 1_000_000;
    private const int MaxOutputTokens = 16_000;
    private const int TypicalOutputTokens = 256;
    private const int MinimumOutputTokens = 64;
    private const double BytesPerEstimatedToken = 4.0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ModelRequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"READINESS_AUDIT_ERROR={ex}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var artifactRoot = Full(root, BenchmarkRoot);
        var manifestPath = Path.Combine(artifactRoot, ManifestFile);
        var requestPath = Path.Combine(artifactRoot, RequestManifestFile);
        var candidatePath = Path.Combine(artifactRoot, CandidateFile);
        var sourcePath = Path.Combine(artifactRoot, SourceCatalogFile);

        var manifest = Read<BenchmarkManifest>(manifestPath);
        var requests = Read<RequestManifest>(requestPath);
        var candidates = Read<CandidateManifest>(candidatePath);
        var source = Read<SourceCatalog>(sourcePath);
        var sourceByDocument = source.SourceOccurrences
            .GroupBy(item => DocumentOf(item.NodeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidatesByDocument = candidates.Candidates
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var requestsByDocument = requests.Requests
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.RequestId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var requestStats = requests.Requests.Select(item => new RequestStat(
            item.RequestId, item.DocumentId, item.RequestSha256, item.RequestBytes, EstimateTokens(item.RequestBytes))).ToArray();
        var distributions = new
        {
            requestBytes = Distribution(requestStats.Select(item => item.Bytes)),
            estimatedInputTokens = Distribution(requestStats.Select(item => item.InputTokens)),
            estimator = new
            {
                name = "UTF8_BYTES_DIV_4_CEILING_V1",
                bytesPerToken = BytesPerEstimatedToken,
                valuesAreEstimated = true,
                exactTokenizerAvailableLocally = false,
            },
        };

        var contextValidation = BuildContextValidation(requestStats);
        var documentReports = source.SourceDocuments.Select(document => BuildDocumentReport(
            document, source.CatalogFingerprint, sourceByDocument[document.DocumentId], candidatesByDocument.GetValueOrDefault(document.DocumentId, []), requestsByDocument.GetValueOrDefault(document.DocumentId, []))).ToArray();

        var currentSourceHash = Sha256File(sourcePath);
        var currentCandidateHash = Sha256File(candidatePath);
        var currentRequestHash = Sha256File(requestPath);
        var serializationCompatibility = CheckCurrentSerializationCompatibility(source, candidates, requests, sourceByDocument);
        var runnerAnalysis = AnalyzeRunner(root);
        var allReasonIncidences = candidates.Candidates.SelectMany(item => item.Reasons).ToArray();
        var candidateReasons = allReasonIncidences
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReasonCount(group.Key, group.Count(), Ratio(group.Count(), allReasonIncidences.Length)))
            .ToArray();
        var fanout = BuildFanout(sourceByDocument, candidatesByDocument);

        var totalInputTokens = requestStats.Sum(item => (long)item.InputTokens);
        var outputWorkload = new
        {
            minimum = Workload(totalInputTokens, requests.Requests.Count, MinimumOutputTokens),
            typical = Workload(totalInputTokens, requests.Requests.Count, TypicalOutputTokens),
            maximum = Workload(totalInputTokens, requests.Requests.Count, MaxOutputTokens),
            outputAssumptions = new
            {
                minimumOutputTokens = MinimumOutputTokens,
                typicalOutputTokens = TypicalOutputTokens,
                maximumOutputTokens = MaxOutputTokens,
                status = "PLANNING_ESTIMATES_NOT_OBSERVED",
            },
        };

        var latency = new[] { 1, 5, 10, 25, 50 }.Select(rate => new
        {
            requestsPerSecond = rate,
            seconds = requests.Requests.Count / (double)rate,
            duration = FormatDuration(requests.Requests.Count / (double)rate),
            status = "THEORETICAL_THROUGHPUT_SCENARIO",
        }).ToArray();

        var redundancy = BuildRedundancy(source, requestStats, documentReports);
        var pipelineMismatch = !serializationCompatibility.AllSampledRequestsMatch || !runnerAnalysis.ConsumesFrozenV1Manifest;
        var recommendedGate = pipelineMismatch
            ? "BLOCKED_ON_BENCHMARK_PIPELINE_MISMATCH"
            : "BLOCKED_ON_PRE_VERIFIER_SCALABILITY";

        var audit = new
        {
            artifactKind = "a99_identity_promotion_provider_execution_readiness_audit",
            schemaVersion = "a99-identity-promotion-provider-execution-readiness-audit-v1",
            benchmarkVersion = "a99-identity-promotion-benchmark-v1",
            benchmarkHead = manifest.Head,
            branch = manifest.Branch,
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            execution = new
            {
                providerCalls = 0,
                modelCalls = 0,
                goldReads = 0,
                semanticGoldOpened = false,
                productionBehaviorChanged = false,
                v1ArtifactsMutated = false,
            },
            frozenInputs = new
            {
                sourceDocuments = source.SourceDocuments.Count,
                sourceUnits = source.SourceOccurrences.Count,
                candidatePairs = candidates.Candidates.Count,
                requests = requests.Requests.Count,
                candidateSetSha256 = manifest.CandidateSetSha256,
                requestManifestSha256 = manifest.RequestManifestSha256,
                sourceCatalogFingerprint = source.CatalogFingerprint,
                sourceCatalogFileSha256 = currentSourceHash,
                sourceCatalogManifestSha256 = manifest.SourceCatalogSha256,
                candidateFileSha256 = currentCandidateHash,
                requestManifestFileSha256 = currentRequestHash,
            },
            requestSizeDistribution = distributions,
            contextLimitValidation = contextValidation,
            totalTokenEstimate = new
            {
                estimatedInputTokens = totalInputTokens,
                valuesAreEstimated = true,
                projected = outputWorkload,
                perDocument = documentReports.Select(item => new
                {
                    item.DocumentId,
                    item.RequestCount,
                    item.EstimatedInputTokens,
                    projectedMinimumTokens = item.EstimatedInputTokens + (long)item.RequestCount * MinimumOutputTokens,
                    projectedTypicalTokens = item.EstimatedInputTokens + (long)item.RequestCount * TypicalOutputTokens,
                    projectedMaximumTokens = item.EstimatedInputTokens + (long)item.RequestCount * MaxOutputTokens,
                }).ToArray(),
            },
            costEstimate = new
            {
                provider = "OpenRouter",
                model = "qwen/qwen3.7-flash",
                status = "COST_UNKNOWN",
                inputPrice = (decimal?)null,
                outputPrice = (decimal?)null,
                pricingTimestamp = (string?)null,
                pricingSource = (string?)null,
                reason = "No authoritative price metadata is available locally without executing an external request.",
            },
            latencyScenarios = new
            {
                requestCount = requests.Requests.Count,
                configuredMaxParallelRequests = 1,
                configuredTransientRetries = 0,
                scenarios = latency,
                providerConcurrency = "UNKNOWN_WITHOUT_PROVIDER_CALL",
                retryAmplificationRisk = "Configured transient retries are zero; transport/provider policy may still impose throttling or failure handling outside this runner.",
                malformedResponseRetryRisk = "Runner has no configured transient retry; malformed responses are a per-request failure risk, not silently retried in this audit.",
                expectedArtifactVolume = "One request/prediction/status artifact per candidate in the current per-candidate runner; exact full-universe volume is not created by this audit.",
            },
            requestRedundancy = redundancy,
            candidateDensity = new
            {
                perDocument = documentReports,
                generatorReasonBreakdown = candidateReasons,
                reasonIncidenceCount = allReasonIncidences.Length,
                candidatesWithMultipleReasons = candidates.Candidates.Count(item => item.Reasons.Count > 1),
                topReasonShare = candidateReasons.FirstOrDefault(),
            },
            fanoutHotspots = fanout,
            pipelineIntent = new
            {
                classification = pipelineMismatch ? "D_FOR_FROZEN_V1_EXECUTION_INTEGRATION" : "A",
                frozenBenchmarkRequestConstruction = "A: every generated candidate has one frozen verifier request.",
                currentRunnerEvidence = runnerAnalysis,
                existingPruningStage = runnerAnalysis.ExistingPruningStage,
                note = "The checked-in live runner is DOC-0205-specific and does not consume the three-document v1 request manifest; this is reported as an integration mismatch, not silently treated as a full-universe executor.",
            },
            recommendedGate,
            gateRationale = pipelineMismatch
                ? "Frozen v1 request hashes do not match the current live JSON serialization on sampled documents and/or no runner consumes the frozen three-document manifest. Do not call provider until the execution path is reconciled; v1 remains unchanged."
                : "The frozen requests are within the configured context contract, but the current path sends one whole-outline verifier request per generated candidate with no pre-verifier pruning/ranking stage."
        };

        var outputJson = JsonSerializer.Serialize(audit, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }) + Environment.NewLine;
        var outputPath = Path.Combine(artifactRoot, "provider-execution-readiness-audit.json");
        File.WriteAllText(outputPath, outputJson, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(artifactRoot, "provider-execution-readiness-audit.md"), BuildMarkdown(audit, documentReports, serializationCompatibility, runnerAnalysis, candidateReasons, fanout), new UTF8Encoding(false));
        Console.WriteLine($"READINESS_AUDIT={outputPath}");
        Console.WriteLine($"RECOMMENDED_GATE={recommendedGate}");
        Console.WriteLine($"REQUESTS={requests.Requests.Count};INPUT_TOKENS_EST={totalInputTokens};UNIQUE_REQUEST_HASHES={redundancy.UniqueRequestHashes}");
    }

    private static DocumentReport BuildDocumentReport(SourceDocument document, string catalogFingerprint, IReadOnlyList<SourceOccurrence> sourceNodes, IReadOnlyList<Candidate> candidates, IReadOnlyList<RequestEntry> requests)
    {
        var degrees = sourceNodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
        var reasons = sourceNodes.ToDictionary(node => node.NodeId, _ => new Dictionary<string, int>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var endpoint in new[] { candidate.Left, candidate.Right })
            {
                if (!degrees.ContainsKey(endpoint)) continue;
                degrees[endpoint]++;
                foreach (var reason in candidate.Reasons)
                    reasons[endpoint][reason] = reasons[endpoint].GetValueOrDefault(reason) + 1;
            }
        }
        var estimatedTokens = requests.Sum(item => (long)EstimateTokens(item.RequestBytes));
        var sourceContextBytes = SerializeSourceContext(catalogFingerprint, sourceNodes).Length;
        var reasonIncidenceCount = candidates.Sum(item => item.Reasons.Count);
        return new DocumentReport(
            document.DocumentId,
            document.SourceSha256,
            sourceNodes.Count,
            PairCount(sourceNodes.Count),
            candidates.Count,
            Ratio(candidates.Count, PairCount(sourceNodes.Count)),
            requests.Count,
            Distribution(requests.Select(item => item.RequestBytes)),
            estimatedTokens,
            sourceNodes.Count == 0 ? 0 : candidates.Count * 2.0 / sourceNodes.Count,
            Distribution(degrees.Values),
            candidates.SelectMany(item => item.Reasons).GroupBy(item => item, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new ReasonCount(group.Key, group.Count(), Ratio(group.Count(), reasonIncidenceCount))).ToArray(),
            reasonIncidenceCount,
            candidates.Count(item => item.Reasons.Count > 1),
            sourceContextBytes,
            Math.Max(0L, requests.Sum(item => (long)item.RequestBytes) - (long)sourceContextBytes * requests.Count));
    }

    private static object BuildContextValidation(IEnumerable<RequestStat> stats)
    {
        var values = stats.ToArray();
        var within = values.Count(item => item.InputTokens < ContextLimitTokens * .90);
        var near = values.Count(item => item.InputTokens >= ContextLimitTokens * .90 && item.InputTokens <= ContextLimitTokens);
        var exceeds = values.Count(item => item.InputTokens > ContextLimitTokens);
        return new
        {
            model = "qwen/qwen3.7-flash",
            provider = "OpenRouter",
            contextLimitTokens = ContextLimitTokens,
            contextLimitSource = "CURRENT_LIVE_RUNNER_CONFIGURATION",
            providerAdvertisedLimitVerifiedWithoutCall = false,
            nearThresholdDefinition = "estimated input tokens >= 90% and <= configured limit",
            counts = new { within, near, exceeds, total = values.Length },
            percentages = new { within = Ratio(within, values.Length), near = Ratio(near, values.Length), exceeds = Ratio(exceeds, values.Length) },
            status = exceeds == 0 ? "WITHIN_CONFIGURED_LIMIT_NOT_PROVIDER_VERIFIED" : "EXCEEDS_CONFIGURED_LIMIT",
            valuesAreEstimated = true,
        };
    }

    private static SerializationCompatibility CheckCurrentSerializationCompatibility(SourceCatalog source, CandidateManifest candidates, RequestManifest requests, IReadOnlyDictionary<string, SourceOccurrence[]> sourceByDocument)
    {
        var sampled = new List<object>();
        foreach (var document in source.SourceDocuments.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            var request = requests.Requests.Where(item => item.DocumentId == document.DocumentId).OrderBy(item => item.RequestId, StringComparer.Ordinal).FirstOrDefault();
            if (request is null) continue;
            var candidate = candidates.Candidates.First(item => item.PairId == request.RequestId);
            var nodes = sourceByDocument[document.DocumentId].Select(item => new HdsaIdentityRoleNodeInput(
                item.NodeId, [item.NodeId], item.Text, item.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
            var current = new HdsaIdentityPairVerificationRequest(source.CatalogFingerprint, nodes, target, false);
            var currentBytes = JsonSerializer.SerializeToUtf8Bytes(current, ModelRequestJsonOptions);
            var currentHash = Sha256(currentBytes);
            sampled.Add(new
            {
                documentId = document.DocumentId,
                requestId = request.RequestId,
                frozenHash = request.RequestSha256,
                currentRunnerSerializationHash = currentHash,
                frozenBytes = request.RequestBytes,
                currentRunnerSerializationBytes = currentBytes.Length,
                equal = string.Equals(request.RequestSha256, currentHash, StringComparison.Ordinal) && request.RequestBytes == currentBytes.Length,
                comparison = "same request DTO and role placeholder; current runner uses JsonSerializer with WriteIndented=true",
            });
        }
        var matches = sampled.Count(item => GetBool(item, "equal"));
        return new SerializationCompatibility(
            sampled,
            sampled.Count,
            matches,
            sampled.Count > 0 && matches == sampled.Count,
            sampled.Count > 0 && matches == sampled.Count ? "COMPATIBLE_ON_SAMPLES" : "SERIALIZATION_MISMATCH_ON_SAMPLES",
            true);
    }

    private static RunnerAnalysis AnalyzeRunner(string root)
    {
        var path = Full(root, RunnerFile);
        var text = File.ReadAllText(path);
        var perCandidate = text.Contains("foreach (var candidate in candidateInput.Candidates)", StringComparison.Ordinal);
        var oneRequest = text.Contains("new HdsaIdentityPairVerificationRequest", StringComparison.Ordinal);
        var allCandidates = text.Contains("candidateInput.Candidates", StringComparison.Ordinal);
        var consumesV1 = text.Contains("artifacts/identity-benchmark/v1", StringComparison.Ordinal) || text.Contains("request-manifest.json", StringComparison.Ordinal);
        var pruningTerms = new[] { "TopK", "top-k", "MaxCandidates", "CandidateCap", "Prune", "RankCandidates", "shortlist", "pair budget" };
        var foundPruning = pruningTerms.Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(
            perCandidate && oneRequest && allCandidates,
            consumesV1,
            foundPruning.Length == 0 ? "NO_PRE_VERIFIER_PRUNING_STAGE" : "POSSIBLE_PRUNING_TERMS_FOUND_REQUIRES_REVIEW",
            new[]
            {
                $"{RunnerFile}:111 foreach candidateInput.Candidates",
                $"{RunnerFile}:115 new HdsaIdentityPairVerificationRequest",
                $"{RunnerFile}:116 JsonSerializer.Serialize(request, JsonOptions)",
                $"{RunnerFile}:96-99 ContextSize=1,000,000; MaxOutputTokens=16,000; MaxParallelRequests=1",
            },
            foundPruning);
    }

    private static RedundancyReport BuildRedundancy(SourceCatalog source, IReadOnlyList<RequestStat> requests, IReadOnlyList<DocumentReport> documentReports)
    {
        var totalBytes = requests.Sum(item => (long)item.Bytes);
        var documentContext = documentReports.Sum(item => (long)item.SourceContextBytes * item.RequestCount);
        var unique = requests.Select(item => item.RequestSha256).Distinct(StringComparer.Ordinal).Count();
        return new RedundancyReport(
            unique,
            requests.Count - unique,
            source.SourceDocuments.Count,
            totalBytes,
            documentContext,
            documentContext,
            Math.Max(0, totalBytes - documentContext),
            Ratio(documentContext, totalBytes),
            Ratio(Math.Max(0, totalBytes - documentContext), totalBytes),
            "The frozen manifest does not persist bodies. Shared context is measured from the reconstructed source-node array; outer envelope and target fields are pair-specific. Current-runner serialization compatibility is reported separately.",
            "Request hashes are counted from frozen request entries.");
    }

    private static FanoutRow[] BuildFanout(IReadOnlyDictionary<string, SourceOccurrence[]> sourceByDocument, IReadOnlyDictionary<string, Candidate[]> candidatesByDocument)
    {
        var rows = new List<FanoutRow>();
        foreach (var document in sourceByDocument)
        {
            var byNode = document.Value.ToDictionary(item => item.NodeId, _ => new FanoutRowBuilder(), StringComparer.Ordinal);
            foreach (var candidate in candidatesByDocument.GetValueOrDefault(document.Key, []))
            {
                foreach (var endpoint in new[] { candidate.Left, candidate.Right })
                {
                    if (!byNode.TryGetValue(endpoint, out var row)) continue;
                    row.Degree++;
                    foreach (var reason in candidate.Reasons) row.Reasons[reason] = row.Reasons.GetValueOrDefault(reason) + 1;
                }
            }
            rows.AddRange(byNode.Select(item => new FanoutRow(item.Key, document.Key, item.Value.Degree,
                item.Value.Reasons.OrderByDescending(reason => reason.Value).ThenBy(reason => reason.Key, StringComparer.Ordinal)
                    .Select(reason => new FanoutReason(reason.Key, reason.Value)).ToArray())));
        }
        return rows.OrderByDescending(item => item.Degree).ThenBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).Take(50).ToArray();
    }

    private static string BuildMarkdown(object audit, IReadOnlyList<DocumentReport> documents, SerializationCompatibility compatibility, RunnerAnalysis runner, IReadOnlyList<ReasonCount> reasons, IReadOnlyList<FanoutRow> fanout)
    {
        var json = JsonSerializer.Serialize(audit, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        var root = JsonDocument.Parse(json).RootElement;
        var gate = root.GetProperty("recommendedGate").GetString();
        var requestCount = root.GetProperty("frozenInputs").GetProperty("requests").GetInt32();
        var sourceUnits = root.GetProperty("frozenInputs").GetProperty("sourceUnits").GetInt32();
        var candidates = root.GetProperty("frozenInputs").GetProperty("candidatePairs").GetInt32();
        var totalInput = root.GetProperty("totalTokenEstimate").GetProperty("estimatedInputTokens").GetInt64();
        var context = root.GetProperty("contextLimitValidation");
        var lines = new List<string>
        {
            "# A99 Identity Promotion Benchmark v1 — Provider Execution Readiness Audit",
            "",
            $"Recommended gate: **`{gate}`**",
            "",
            "This audit is source/request-manifest-only. It made **0 model calls**, **0 provider calls**, and opened **0 semantic Gold files**. Frozen v1 artifacts were not modified.",
            "",
            "## Frozen workload",
            "",
            $"- Source units: `{sourceUnits:N0}`",
            $"- Candidate pairs / frozen requests: `{candidates:N0}` / `{requestCount:N0}`",
            $"- Estimated input workload: `{totalInput:N0}` tokens (UTF-8 bytes / 4, ceiling; estimated)",
            "- Provider/model: `OpenRouter / qwen/qwen3.7-flash`",
            "",
            "## Request and context validity",
            "",
            "Request-byte and estimated-token distributions are in the JSON artifact (min, p50, p90, p95, p99, max, mean, total).",
            $"- Configured context limit: `{context.GetProperty("contextLimitTokens").GetInt32():N0}` tokens; provider-advertised value was not verified because no call was allowed.",
            $"- Estimated classifications: within `{context.GetProperty("counts").GetProperty("within").GetInt32():N0}`, near `{context.GetProperty("counts").GetProperty("near").GetInt32():N0}`, exceeds `{context.GetProperty("counts").GetProperty("exceeds").GetInt32():N0}`.",
            $"- Context status: `{context.GetProperty("status").GetString()}`.",
            "",
            "## Workload / cost / latency",
            "",
            "- Output projection uses explicitly labeled planning assumptions: minimum 64, typical 256, configured maximum 16,000 tokens per request.",
            "- Pricing: `COST_UNKNOWN`; no authoritative local price metadata was available without external execution.",
            "- Theoretical completion times for 95,999 requests: 1 rps ≈ 26h 40m; 5 rps ≈ 5h 20m; 10 rps ≈ 2h 40m; 25 rps ≈ 1h 4m; 50 rps ≈ 32m. These are throughput scenarios, not provider guarantees.",
            "- Current runner config says max parallelism 1 and transient retries 0; provider throttling/concurrency remains unknown.",
            "",
            "## Redundancy and candidate density",
            "",
            "- Candidate reasons and per-document density are recorded in JSON.",
            "- The generator is high-recall pair retrieval: adjacency and normalized-text affinity are evidence only; no Gold reduction was performed.",
            "- Top-50 occurrence fan-out is recorded in JSON to expose pathological degree without tuning it.",
            "",
            "## Pipeline intent / compatibility",
            "",
            $"- Frozen v1 request construction is intent **A**: one verifier request per generated candidate.",
            $"- Current checked-in runner evidence: `{string.Join("; ", runner.Evidence)}`.",
            $"- Existing pruning stage: `{runner.ExistingPruningStage}`.",
            $"- Current serialization sample status: `{compatibility.Status}` ({compatibility.MatchingSamples}/{compatibility.SampledCount} samples match).",
            "- The current live runner is DOC-0205-specific and does not consume the three-document v1 request manifest. This is retained as a compatibility/integration finding; v1 was not rewritten.",
            "",
            "## Decision",
            "",
            $"`{gate}`. The audit does not authorize provider execution. Resolve the frozen-request/current-runner compatibility issue first; if the v1 contract is confirmed compatible, the remaining blocker is the unbounded one-request-per-candidate workload with no pre-verifier pruning/ranking stage.",
            "",
            "## Reproducibility",
            "",
            "Run the audit tool from the repository root with no network dependency. It reads only `artifacts/identity-benchmark/v1/{manifest,request-manifest,candidate-set,source-catalog}.json` and the checked-in verifier runner source.",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static object Distribution(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(item => item).ToArray();
        if (sorted.Length == 0) return new { count = 0, min = 0, p50 = 0, p90 = 0, p95 = 0, p99 = 0, max = 0, mean = 0d, total = 0L };
        return new
        {
            count = sorted.Length,
            min = sorted[0],
            p50 = Percentile(sorted, .50),
            p90 = Percentile(sorted, .90),
            p95 = Percentile(sorted, .95),
            p99 = Percentile(sorted, .99),
            max = sorted[^1],
            mean = sorted.Average(),
            total = sorted.Sum(item => (long)item),
        };
    }

    private static int Percentile(int[] sorted, double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    private static int EstimateTokens(int bytes) => (int)Math.Ceiling(bytes / BytesPerEstimatedToken);
    private static long PairCount(int count) => (long)count * (count - 1) / 2;
    private static double Ratio(long numerator, long denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string FormatDuration(double seconds)
    {
        var value = TimeSpan.FromSeconds(seconds);
        return $"{value.Days}d {value.Hours}h {value.Minutes}m {value.Seconds}s";
    }
    private static object Workload(long input, int requests, int output) => new { inputTokens = input, outputTokens = (long)requests * output, projectedTotalTokens = input + (long)requests * output };
    private static byte[] SerializeSourceContext(string catalogFingerprint, IReadOnlyList<SourceOccurrence> sourceNodes)
    {
        var nodes = sourceNodes.Select(item => new HdsaIdentityRoleNodeInput(
            item.NodeId, [item.NodeId], item.Text, item.DocumentOrder, "UNAVAILABLE", false)).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(nodes, ModelRequestJsonOptions);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException(path);
    private static bool GetBool(object value, string property) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty(property).GetBoolean();
    private static long GetLong(object value, string property) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty(property).GetInt64();
    private static int GetInt(object value, string property) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty(property).GetInt32();

    private sealed record BenchmarkManifest([property: JsonPropertyName("branch")] string Branch, [property: JsonPropertyName("head")] string Head, [property: JsonPropertyName("candidateSetSha256")] string CandidateSetSha256, [property: JsonPropertyName("requestManifestSha256")] string RequestManifestSha256, [property: JsonPropertyName("sourceCatalogSha256")] string SourceCatalogSha256);
    private sealed record RequestManifest([property: JsonPropertyName("requests")] IReadOnlyList<RequestEntry> Requests);
    private sealed record RequestEntry([property: JsonPropertyName("requestId")] string RequestId, [property: JsonPropertyName("documentId")] string DocumentId, [property: JsonPropertyName("requestSha256")] string RequestSha256, [property: JsonPropertyName("requestBytes")] int RequestBytes, [property: JsonPropertyName("left")] string Left, [property: JsonPropertyName("right")] string Right);
    private sealed record CandidateManifest([property: JsonPropertyName("candidates")] IReadOnlyList<Candidate> Candidates);
    private sealed record Candidate([property: JsonPropertyName("pairId")] string PairId, [property: JsonPropertyName("documentId")] string DocumentId, [property: JsonPropertyName("left")] string Left, [property: JsonPropertyName("right")] string Right, [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);
    private sealed record SourceCatalog([property: JsonPropertyName("sourceDocuments")] IReadOnlyList<SourceDocument> SourceDocuments, [property: JsonPropertyName("sourceOccurrences")] IReadOnlyList<SourceOccurrence> SourceOccurrences, [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint);
    private sealed record SourceDocument([property: JsonPropertyName("documentId")] string DocumentId, [property: JsonPropertyName("sourceSha256")] string SourceSha256, [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount);
    private sealed record SourceOccurrence([property: JsonPropertyName("nodeId")] string NodeId, [property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("documentOrder")] int DocumentOrder);
    private sealed record RequestStat(string RequestId, string DocumentId, string RequestSha256, int Bytes, int InputTokens);
    private sealed record RunnerAnalysis(bool OneRequestPerCandidate, bool ConsumesFrozenV1Manifest, string ExistingPruningStage, IReadOnlyList<string> Evidence, IReadOnlyList<string> PruningTermsFound);
    private sealed record DocumentReport(
        string DocumentId, string SourceSha256, int SourceUnits, long EligiblePairUniverse, int GeneratedCandidates,
        double CandidateDensity, int RequestCount, object RequestBytes, long EstimatedInputTokens,
        double AverageCandidatesPerOccurrence, object Degree, IReadOnlyList<ReasonCount> ReasonBreakdown,
        int ReasonIncidenceCount, int CandidatesWithMultipleReasons, int SourceContextBytes, long PairSpecificBytes);
    private sealed record ReasonCount(string Reason, int Count, double Share);
    private sealed record SerializationCompatibility(IReadOnlyList<object> SampledDocuments, int SampledCount, int MatchingSamples, bool AllSampledRequestsMatch, string Status, bool NoV1ArtifactChanged);
    private sealed record RedundancyReport(
        int UniqueRequestHashes, int DuplicateRequestHashes, int UniqueSourceCatalogs, long TotalRequestBytes,
        long RepeatedCatalogBytes, long SharedDocumentContextBytes, long PairSpecificBytes,
        double SharedContextShare, double PairSpecificShare, string Measurement, string Note);
    private sealed record FanoutReason(string Reason, int Count);
    private sealed record FanoutRow(string OccurrenceId, string DocumentId, int Degree, IReadOnlyList<FanoutReason> Reasons);
    private sealed class FanoutRowBuilder { public int Degree; public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal); }
}
