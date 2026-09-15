using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV8H0Execution;

internal static class Program
{
    private const string InputRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-preflight-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-execution-v2";
    private const string FrozenPreflightCommit = "98ce62e";
    private const string ExecutionCampaign = "execution-v2";
    private const string ExpectedManifestSha256 = "2228e9a7948a30b49b061f94190f8ff613fe3f6c418ff0249960a6e1a918eb55";
    private const string ExpectedRequestIndexSha256 = "7f4b41c7d611b628f471e38ea8c60ae3033572bcc23ed3cd1459dfca1b77ef75";
    private const string ExpectedCandidateManifestSha256 = "747ddaaadb7c1f3941e34e232bd401cf5e4c324536a98cff988c89503bcb40b1";
    private const string ExpectedRoleRequestsSha256 = "d9f6f63ca524a40ee8fb76fb475b2771727f13bbf571f979d6f6a203de43f4cb";
    private const string ExpectedSpanRequestsSha256 = "f807f6e14718c624f33ef233175cb1acd51cf253fb2ca2e4d15349095d1713e7";
    private const string ExpectedPromptProfileSha256 = "028ed77b71687bebadd8f5e702b7ad0890e11dca845597cf0a48652e8aec7aab";
    private const int ExpectedMaxAnalystBlocks = 40;
    private const int ExpectedRoleBatchSize = 8;
    private const int ExpectedPointerSpanBatchSize = 4;
    private const int ExpectedRoleRequests = 30;
    private const int MaxSpanRequests = 59;
    private const double HeadingConfidenceThreshold = .65;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> HeadingRoles = new(StringComparer.Ordinal)
    {
        "document_title", "section_heading", "topic_heading", "local_subheading", "legal_chapter",
        "legal_section", "legal_article", "legal_clause", "legal_point", "appendix_heading",
        "meeting_section", "agenda_item", "note_heading",
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
            if (args.Contains("--rebind-only", StringComparer.Ordinal))
                await RebindOnlyAsync(root);
            else
                await RunAsync(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8H0_EXECUTION_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var input = Full(root, InputRelative);
        var output = Full(root, OutputRelative);
        Require(Directory.Exists(input), "V8H0_PREFLIGHT_NOT_FOUND");
        if (Directory.Exists(output) && File.Exists(Path.Combine(output, "execution-manifest.json")))
            throw new InvalidOperationException("V8H0_EXECUTION_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        Directory.CreateDirectory(output);

        var manifestPath = Path.Combine(input, "manifest.json");
        var manifest = Load(manifestPath);
        VerifyFrozenPreflight(input, manifest);
        Require(manifest.GetProperty("status").GetString() == "READY_FOR_V8H0_HEADING_PROVIDER_AUTHORIZATION", "V8H0_PREFLIGHT_NOT_READY");
        Require(manifest.GetProperty("authority").GetProperty("v8a2Commit").GetString() == "579b3fd", "V8H0_AUTHORITY_DRIFT");
        Require(manifest.GetProperty("firewall").GetProperty("goldReadCount").GetInt32() == 0, "V8H0_PREFLIGHT_GOLD_NONZERO");

        var requestIndex = Load(Path.Combine(input, "request-index.json"));
        var requests = ReadRequests(input, requestIndex);
        var roleRequests = requests.Where(x => x.Stage == "ROLE").OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.ShardOrdinal).ToArray();
        var spanRequests = requests.Where(x => x.Stage == "POINTER_SPAN").OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.ShardOrdinal).ToArray();
        Require(roleRequests.Length == ExpectedRoleRequests, "V8H0_ROLE_REQUEST_COUNT_DRIFT");
        Require(spanRequests.Length <= MaxSpanRequests, "V8H0_SPAN_REQUEST_COUNT_DRIFT");

        var roleExecution = new List<Attempt>();
        var spanExecution = new List<Attempt>();
        var startedAttempts = new List<StartedAttempt>();
        var attemptPath = Path.Combine(output, "attempts.incremental.json");
        var roleDecisions = new Dictionary<string, Decision>(StringComparer.Ordinal);
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OPENROUTER_ENDPOINT") ?? "https://openrouter.ai/api/v1/chat/completions");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "qwen/qwen3.5-9b";
        Require(!string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY_MISSING_BEFORE_CALLS");
        Require(endpoint.Scheme == Uri.UriSchemeHttps, "OPENROUTER_ENDPOINT_NOT_HTTPS");

        foreach (var request in roleRequests)
        {
            var started = DateTimeOffset.UtcNow;
            startedAttempts.Add(new StartedAttempt(request, "ROLE", started));
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution, startedAttempts);
            var attempt = await ExecuteAsync(client, endpoint, apiKey, model, request, "ROLE", output, roleExecution.Count + spanExecution.Count + 1, started);
            roleExecution.Add(attempt);
            startedAttempts.RemoveAll(x => x.Request.RequestKey == request.RequestKey);
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution, startedAttempts);
            if (attempt.ValidationStatus == "VALID")
                foreach (var decision in attempt.Decisions)
                    roleDecisions[$"{request.DocumentId}|{decision.OpaqueRef}"] = decision;
        }

        var triggeredSpanIds = spanRequests.Where(request => request.BlockRefs.Any(blockRef =>
            roleDecisions.TryGetValue($"{request.DocumentId}|{blockRef}", out var decision) &&
            decision.IsHeading && decision.Confidence >= HeadingConfidenceThreshold))
            .Select(x => x.RequestKey).ToHashSet(StringComparer.Ordinal);
        foreach (var request in spanRequests.Where(x => triggeredSpanIds.Contains(x.RequestKey)))
        {
            var started = DateTimeOffset.UtcNow;
            startedAttempts.Add(new StartedAttempt(request, "POINTER_SPAN", started));
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution, startedAttempts);
            var attempt = await ExecuteAsync(client, endpoint, apiKey, model, request, "POINTER_SPAN", output, roleExecution.Count + spanExecution.Count + 1, started);
            spanExecution.Add(attempt);
            startedAttempts.RemoveAll(x => x.Request.RequestKey == request.RequestKey);
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution, startedAttempts);
        }

        var allAttempts = roleExecution.Concat(spanExecution).ToArray();
        var bindingEligibleDocuments = roleRequests.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal)
            .Where(documentId => roleExecution.Where(x => x.Request.DocumentId == documentId).All(x => x.Status == "COMPLETED" && x.ValidationStatus == "VALID"))
            .ToHashSet(StringComparer.Ordinal);
        var bindingExcludedDocuments = roleRequests.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Where(x => !bindingEligibleDocuments.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var bindings = BuildBindings(input, roleRequests, spanRequests, roleExecution, spanExecution, roleDecisions, bindingEligibleDocuments);
        var headingSummary = bindings.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Select(g => new
        {
            documentId = g.Key,
            modelSelectedHeadingRefs = g.Select(x => x.BlockRef).Distinct(StringComparer.Ordinal).Count(),
            successfullyBoundHeadings = g.Count(x => x.BindingStatus == "BOUND"),
            unknownRefs = g.Count(x => x.BindingStatus == "UNKNOWN_REF"),
            duplicateOrIncompatibleRefs = g.Count(x => x.BindingStatus == "DUPLICATE_OR_INCOMPATIBLE"),
            unboundRefs = g.Count(x => x.BindingStatus == "UNBOUND"),
            exactSourceMismatches = g.Count(x => x.BindingStatus == "EXACT_SOURCE_MISMATCH"),
        }).OrderBy(x => x.documentId, StringComparer.Ordinal).ToArray();
        var frozenSet = bindings.Where(x => x.BindingStatus == "BOUND").ToArray();
        var providerFailures = allAttempts.Count(x => x.Status == "PROVIDER_ERROR");
        var invalid = allAttempts.Count(x => x.ValidationStatus == "INVALID_VALIDATION");
        var manifestOut = new
        {
            artifactKind = "a99_identity_benchmark_v8h0_heading_extraction_execution",
            schemaVersion = "a99-v8h0-heading-extraction-execution-v2",
            status = providerFailures == 0 && invalid == 0 ? "COMPLETED_WITHOUT_PROVIDER_FAILURE" : "COMPLETED_WITH_FAILURES_PRESERVED",
            authority = new
            {
                preflightCommit = FrozenPreflightCommit,
                preflightManifestSha256 = Sha256File(manifestPath),
                preflightRequestIndexSha256 = Sha256File(Path.Combine(input, "request-index.json")),
                preflightCandidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")),
                preflightRoleRequestsSha256 = Sha256File(Path.Combine(input, "heading-role-requests.json")),
                preflightSpanRequestsSha256 = Sha256File(Path.Combine(input, "heading-pointer-span-requests.json")),
                executionCampaign = ExecutionCampaign,
                requestsImmutable = true,
                bindingRepair = false,
            },
            provider = new { provider = "OpenRouter", model, endpoint = endpoint.ToString(), retry = 0, roleCalls = roleExecution.Count, spanCalls = spanExecution.Count, totalCalls = allAttempts.Length },
            role = new { scheduled = roleRequests.Length, executed = roleExecution.Count, valid = roleExecution.Count(x => x.ValidationStatus == "VALID"), invalidValidation = roleExecution.Count(x => x.ValidationStatus == "INVALID_VALIDATION"), providerError = roleExecution.Count(x => x.Status == "PROVIDER_ERROR") },
            span = new { materializedUpperBound = spanRequests.Length, triggered = triggeredSpanIds.Count, executed = spanExecution.Count, valid = spanExecution.Count(x => x.ValidationStatus == "VALID"), invalidValidation = spanExecution.Count(x => x.ValidationStatus == "INVALID_VALIDATION"), providerError = spanExecution.Count(x => x.Status == "PROVIDER_ERROR") },
            headingOutput = new { totalFrozenBoundHeadingOccurrences = frozenSet.Length, bindingEligibleDocuments = bindingEligibleDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray(), bindingExcludedDocuments, perDocument = headingSummary },
            firewall = new { goldReadCount = 0, identityLabelReadCount = 0, historicalEvaluationReadCount = 0, v4hToV8PredictionReadCount = 0, v5ToV8PredictionReadCount = 0, v6ToV8PredictionReadCount = 0, v7ToV8PredictionReadCount = 0, identityCandidateGeneration = false },
        };

        Write(Path.Combine(output, "attempts.json"), new { schemaVersion = "a99-v8h0-attempts-v2", executionCampaign = ExecutionCampaign, attempts = allAttempts.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "role-decisions.json"), new { schemaVersion = "a99-v8h0-role-decisions-v1", decisions = roleDecisions.Values.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "frozen-bound-heading-occurrence-set.json"), new { schemaVersion = "a99-v8h0-frozen-bound-heading-occurrence-set-v1", sourceUniverseSha256 = Sha256File(Path.Combine(input, "source-universe.json")), candidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")), immutable = true, occurrences = frozenSet.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "heading-summary.json"), new { schemaVersion = "a99-v8h0-heading-summary-v1", role = manifestOut.role, span = manifestOut.span, perDocument = headingSummary });
        Write(Path.Combine(output, "execution-manifest.json"), manifestOut);
        Write(Path.Combine(output, "source-authority-sha.json"), new { sourceUniverseSha256 = Sha256File(Path.Combine(input, "source-universe.json")), candidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")), sourceUniverseRead = true, selectedCandidateManifestRead = true });
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(manifestOut.status, roleExecution.Count, spanExecution.Count, allAttempts.Length, frozenSet.Length), new UTF8Encoding(false));
        Console.WriteLine($"V8H0_EXECUTION_STATUS={manifestOut.status} ROLE_CALLS={roleExecution.Count} SPAN_CALLS={spanExecution.Count} TOTAL_CALLS={allAttempts.Length} BOUND={frozenSet.Length}");
    }

    private static async Task<Attempt> ExecuteAsync(HttpClient client, Uri endpoint, string apiKey, string model, Request request, string stage, string output, int attemptOrdinal, DateTimeOffset started)
    {
        var attemptDirectory = Path.Combine(output, "attempts");
        Directory.CreateDirectory(attemptDirectory);
        var prefix = $"{attemptOrdinal:000}-{stage.ToLowerInvariant()}-{SanitizeFilePart(request.RequestKey)}";
        try
        {
            using var requestBody = JsonDocument.Parse(request.SerializedRequest);
            var root = requestBody.RootElement;
            var systemPrompt = root.GetProperty("systemPrompt").GetString()!;
            var userPrompt = root.GetProperty("userPrompt").GetString()!;
            var maxTokens = BoundaryOutputBudget(userPrompt);
            var providerBody = new
            {
                model,
                temperature = 0,
                max_tokens = maxTokens,
                reasoning = new { effort = "none" },
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } },
                response_format = new { type = "json_object" },
                provider = new { zdr = true, data_collection = "deny", require_parameters = true, allow_fallbacks = true },
            };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(providerBody) };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor");
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(providerBody, JsonOptions);
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseText = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            var rawHash = Sha256(responseText);
            Write(Path.Combine(attemptDirectory, prefix + ".raw.json"), new
            {
                schemaVersion = "a99-v8h0-provider-raw-v2",
                executionCampaign = ExecutionCampaign,
                documentId = request.DocumentId,
                stage,
                requestKey = request.RequestKey,
                requestSha256 = request.RequestSha256,
                started,
                received = DateTimeOffset.UtcNow,
                httpStatus = (int)response.StatusCode,
                providerStatus = response.IsSuccessStatusCode ? "HTTP_SUCCESS" : "HTTP_ERROR",
                rawResponseSha256 = rawHash,
                rawProviderResponse = responseText,
            });
            var content = response.IsSuccessStatusCode ? ExtractContent(responseText) : "";
            Write(Path.Combine(attemptDirectory, prefix + ".parsed.json"), new
            {
                schemaVersion = "a99-v8h0-provider-parsed-v2",
                executionCampaign = ExecutionCampaign,
                documentId = request.DocumentId,
                stage,
                requestKey = request.RequestKey,
                requestSha256 = request.RequestSha256,
                parsedAt = DateTimeOffset.UtcNow,
                status = response.IsSuccessStatusCode ? "PARSED" : "NOT_PARSED_HTTP_ERROR",
                content,
            });
            var validation = response.IsSuccessStatusCode ? ValidateResponse(stage, request, content) : Validation.Invalid("provider-http-status");
            var mappedDecisions = validation.Decisions.Select(decision => decision with
            {
                DocumentId = request.DocumentId,
                OpaqueRef = OpaqueRefFor(request, decision.Ref),
            }).ToArray();
            validation = validation with { Decisions = mappedDecisions };
            Write(Path.Combine(attemptDirectory, prefix + ".validation.json"), new
            {
                schemaVersion = "a99-v8h0-provider-validation-v2",
                executionCampaign = ExecutionCampaign,
                documentId = request.DocumentId,
                stage,
                requestKey = request.RequestKey,
                requestSha256 = request.RequestSha256,
                validatedAt = DateTimeOffset.UtcNow,
                validationStatus = validation.Status,
                validationReason = validation.Reason,
                decisions = validation.Decisions,
            });
            return new Attempt(request, stage, started, DateTimeOffset.UtcNow, response.IsSuccessStatusCode ? "COMPLETED" : "PROVIDER_ERROR", (int)response.StatusCode, responseText, content, rawHash, validation.Status, validation.Reason, validation.Decisions, stopwatch.ElapsedMilliseconds, requestBytes.Length, maxTokens);
        }
        catch (Exception ex)
        {
            Write(Path.Combine(attemptDirectory, prefix + ".validation.json"), new
            {
                schemaVersion = "a99-v8h0-provider-validation-v2",
                executionCampaign = ExecutionCampaign,
                documentId = request.DocumentId,
                stage,
                requestKey = request.RequestKey,
                requestSha256 = request.RequestSha256,
                validatedAt = DateTimeOffset.UtcNow,
                validationStatus = "INVALID_VALIDATION",
                validationReason = ex.GetType().Name + ":" + ex.Message,
                decisions = Array.Empty<object>(),
            });
            return new Attempt(request, stage, started, DateTimeOffset.UtcNow, "PROVIDER_ERROR", null, "", "", Sha256(""), "INVALID_VALIDATION", ex.GetType().Name + ":" + ex.Message, [], 0, 0, 0);
        }
    }

    private static void VerifyFrozenPreflight(string input, JsonElement manifest)
    {
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.json"] = ExpectedManifestSha256,
            ["request-index.json"] = ExpectedRequestIndexSha256,
            ["production-candidate-manifest.json"] = ExpectedCandidateManifestSha256,
            ["heading-role-requests.json"] = ExpectedRoleRequestsSha256,
            ["heading-pointer-span-requests.json"] = ExpectedSpanRequestsSha256,
        };
        foreach (var pair in expectedFiles)
            Require(Sha256File(Path.Combine(input, pair.Key)) == pair.Value, "V8H0_FROZEN_PREflight_HASH_DRIFT:" + pair.Key);
        var detector = manifest.GetProperty("detector");
        Require(detector.GetProperty("maxAnalystBlocks").GetInt32() == ExpectedMaxAnalystBlocks, "V8H0_MAX_ANALYST_BLOCKS_DRIFT");
        Require(detector.GetProperty("roleBatchSize").GetInt32() == ExpectedRoleBatchSize, "V8H0_ROLE_BATCH_DRIFT");
        Require(detector.GetProperty("pointerSpanBatchSize").GetInt32() == ExpectedPointerSpanBatchSize, "V8H0_SPAN_BATCH_DRIFT");
        Require(detector.GetProperty("promptProfileSha256").GetString() == ExpectedPromptProfileSha256, "V8H0_PROMPT_PROFILE_DRIFT");
        Require(detector.GetProperty("modelCalls").GetInt32() == 0 && detector.GetProperty("providerCalls").GetInt32() == 0, "V8H0_PREFLIGHT_PROVIDER_COUNTS_NONZERO");
        var scale = manifest.GetProperty("requestScale");
        Require(scale.GetProperty("roleRequestCount").GetInt32() == ExpectedRoleRequests, "V8H0_ROLE_SCALE_DRIFT");
        Require(scale.GetProperty("pointerSpanRequestCount").GetInt32() == MaxSpanRequests, "V8H0_SPAN_SCALE_DRIFT");
        Require(manifest.GetProperty("binding").GetProperty("version").GetString() == "v8h0-source-bound-heading-binding-v1", "V8H0_BINDING_CONTRACT_DRIFT");
        Require(manifest.GetProperty("gate").GetProperty("allRequestsMaterializedBeforeProvider").GetBoolean(), "V8H0_REQUESTS_NOT_MATERIALIZED");
    }

    private static IReadOnlyList<Request> ReadRequests(string input, JsonElement requestIndex)
    {
        var indexed = requestIndex.GetProperty("requests").EnumerateArray().ToDictionary(
            x => $"{x.GetProperty("documentId").GetString()}|{x.GetProperty("shardId").GetString()}", StringComparer.Ordinal);
        var result = new List<Request>();
        foreach (var (stage, fileName) in new[] { ("ROLE", "heading-role-requests.json"), ("POINTER_SPAN", "heading-pointer-span-requests.json") })
        {
            var root = Load(Path.Combine(input, fileName));
            foreach (var item in root.GetProperty("requests").EnumerateArray())
            {
                var documentId = item.GetProperty("documentId").GetString()!;
                var shardId = item.GetProperty("shardId").GetString()!;
                var key = $"{documentId}|{shardId}";
                Require(indexed.TryGetValue(key, out var indexRow), "V8H0_REQUEST_INDEX_MISSING:" + key);
                var serialized = JsonSerializer.Serialize(item.GetProperty("request"), JsonOptions);
                Require(Sha256(serialized) == indexRow.GetProperty("requestSha256").GetString(), "V8H0_REQUEST_HASH_DRIFT:" + key);
                using var requestDoc = JsonDocument.Parse(serialized);
                using var promptDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("userPrompt").GetString()!);
                var productionIds = promptDoc.RootElement.GetProperty("blocks").EnumerateArray()
                    .Select(x => x.GetProperty("id").GetString()!).ToArray();
                var blockRefs = indexRow.GetProperty("blockRefs").EnumerateArray().Select(x => x.GetString()!).ToArray();
                Require(blockRefs.Length == productionIds.Length, "V8H0_BLOCK_HANDLE_DRIFT:" + key);
                result.Add(new Request(documentId, stage, key, indexRow.GetProperty("shardOrdinal").GetInt32(), serialized,
                    indexRow.GetProperty("requestSha256").GetString()!, blockRefs, productionIds));
            }
        }
        return result;
    }

    private static string OpaqueRefFor(Request request, string productionRef)
    {
        var index = Array.IndexOf(request.ProductionIds.ToArray(), productionRef);
        if (index < 0 || index >= request.BlockRefs.Count) throw new FormatException("provider-ref-not-in-frozen-request:" + productionRef);
        return request.BlockRefs[index];
    }

    private static Validation ValidateResponse(string stage, Request request, string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(content));
            if (!doc.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return Validation.Invalid("missing-blocks-array");
            var allowed = request.ProductionIds.ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var decisions = new List<Decision>();
            foreach (var item in blocks.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
                    return Validation.Invalid("missing-id");
                var id = idProperty.GetString()!;
                if (!allowed.Contains(id) || !seen.Add(id)) return Validation.Invalid("unknown-or-duplicate-id");
                if (stage == "ROLE")
                {
                    if (!item.TryGetProperty("role", out var roleProperty) || roleProperty.ValueKind != JsonValueKind.String) return Validation.Invalid("missing-role");
                    var role = roleProperty.GetString()!.Trim().ToLowerInvariant();
                    var confidence = item.TryGetProperty("confidence", out var confidenceProperty) && confidenceProperty.TryGetDouble(out var value) ? Math.Clamp(value, 0, 1) : 0;
                    if (item.TryGetProperty("level", out _) || item.TryGetProperty("parent", out _) || item.TryGetProperty("semanticNodeId", out _)) return Validation.Invalid("forbidden-structural-field");
                    decisions.Add(new Decision(id, role, confidence, HeadingRoles.Contains(role), null));
                }
                else
                {
                    if (!item.TryGetProperty("heading_span", out var span) || span.ValueKind == JsonValueKind.Null) decisions.Add(new Decision(id, "", 0, false, null));
                    else if (span.ValueKind != JsonValueKind.Object || !span.TryGetProperty("start", out var start) || !start.TryGetInt32(out var from) || !span.TryGetProperty("end", out var end) || !end.TryGetInt32(out var to) || from < 0 || to < from)
                        return Validation.Invalid("malformed-pointer-span");
                    else decisions.Add(new Decision(id, "", 0, false, new Span(from, to)));
                }
            }
            if (!seen.SetEquals(allowed)) return Validation.Invalid("missing-or-extra-id");
            return Validation.Valid(decisions);
        }
        catch (Exception ex) { return Validation.Invalid("parse:" + ex.GetType().Name); }
    }

    private static async Task RebindOnlyAsync(string root)
    {
        var input = Full(root, InputRelative);
        var output = Full(root, OutputRelative);
        var rebind = Path.Combine(output, "binding-revalidation-v3");
        Require(File.Exists(Path.Combine(output, "attempts.json")), "V8H0_EXECUTION_ATTEMPTS_NOT_FOUND");
        Require(!File.Exists(Path.Combine(rebind, "manifest.json")), "V8H0_REBIND_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        Directory.CreateDirectory(rebind);
        var requests = ReadRequests(input, Load(Path.Combine(input, "request-index.json")));
        var roleRequests = requests.Where(x => x.Stage == "ROLE").ToArray();
        var attempts = Load(Path.Combine(output, "attempts.json")).GetProperty("attempts").EnumerateArray().ToArray();
        var roleAttemptsByDocument = attempts.Where(x => x.GetProperty("stage").GetString() == "ROLE").GroupBy(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var bindingEligibleDocuments = roleRequests.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal)
            .Where(documentId => roleAttemptsByDocument.TryGetValue(documentId, out var rows) && rows.Length == roleRequests.Count(x => x.DocumentId == documentId) && rows.All(x => x.GetProperty("status").GetString() == "COMPLETED" && x.GetProperty("validationStatus").GetString() == "VALID"))
            .ToHashSet(StringComparer.Ordinal);
        var bindingExcludedDocuments = roleRequests.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Where(x => !bindingEligibleDocuments.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var roleDecisions = new Dictionary<string, Decision>(StringComparer.Ordinal);
        foreach (var item in Load(Path.Combine(output, "role-decisions.json")).GetProperty("decisions").EnumerateArray())
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            var opaqueRef = item.GetProperty("ref").GetString()!;
            var productionRef = item.GetProperty("productionRef").GetString()!;
            roleDecisions[$"{documentId}|{opaqueRef}"] = new Decision(productionRef, item.GetProperty("role").GetString()!, item.GetProperty("confidence").GetDouble(), item.GetProperty("isHeading").GetBoolean(), null) { DocumentId = documentId, OpaqueRef = opaqueRef };
        }
        var spanByDocRef = new Dictionary<string, Span?>(StringComparer.Ordinal);
        foreach (var attempt in attempts.Where(x => x.GetProperty("stage").GetString() == "POINTER_SPAN" && x.GetProperty("validationStatus").GetString() == "VALID"))
        {
            var documentId = attempt.GetProperty("documentId").GetString()!;
            foreach (var decision in attempt.GetProperty("decisions").EnumerateArray())
            {
                if (!decision.TryGetProperty("span", out var span) || span.ValueKind != JsonValueKind.Object) continue;
                spanByDocRef[$"{documentId}|{decision.GetProperty("opaqueRef").GetString()}"] = new Span(span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32());
            }
        }
        var blockByDocRef = LoadBlockBindings(input);
        var bindings = BuildBindingsCore(blockByDocRef, roleDecisions, spanByDocRef, bindingEligibleDocuments);
        var summary = bindings.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Select(g => new
        {
            documentId = g.Key,
            modelSelectedHeadingRefs = g.Select(x => x.BlockRef).Distinct(StringComparer.Ordinal).Count(),
            successfullyBoundHeadings = g.Count(x => x.BindingStatus == "BOUND"),
            unknownRefs = g.Count(x => x.BindingStatus == "UNKNOWN_REF"),
            duplicateOrIncompatibleRefs = g.Count(x => x.BindingStatus == "DUPLICATE_OR_INCOMPATIBLE"),
            unboundRefs = g.Count(x => x.BindingStatus == "UNBOUND"),
            exactSourceMismatches = g.Count(x => x.BindingStatus == "EXACT_SOURCE_MISMATCH"),
        }).OrderBy(x => x.documentId, StringComparer.Ordinal).ToArray();
        var frozenSet = bindings.Where(x => x.BindingStatus == "BOUND").ToArray();
        var manifestPath = Path.Combine(output, "execution-manifest.json");
        Write(Path.Combine(rebind, "frozen-bound-heading-occurrence-set.json"), new { schemaVersion = "a99-v8h0-frozen-bound-heading-occurrence-set-v2", sourceUniverseSha256 = Sha256File(Path.Combine(input, "source-universe.json")), candidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")), primaryExecutionManifestSha256 = Sha256File(manifestPath), bindingPolicy = "FAIL_CLOSED_DOCUMENT_ROLE_COVERAGE", bindingEligibleDocuments = bindingEligibleDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray(), bindingExcludedDocuments, immutable = true, occurrences = frozenSet.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(rebind, "heading-summary.json"), new { schemaVersion = "a99-v8h0-heading-summary-v2", bindingPolicy = "FAIL_CLOSED_DOCUMENT_ROLE_COVERAGE", bindingEligibleDocuments = bindingEligibleDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray(), bindingExcludedDocuments, perDocument = summary });
        Write(Path.Combine(rebind, "manifest.json"), new { artifactKind = "a99_identity_benchmark_v8h0_heading_binding_revalidation", schemaVersion = "a99-v8h0-heading-binding-revalidation-v2", status = "COMPLETED_WITH_FAILURES_PRESERVED", primaryExecutionManifestSha256 = Sha256File(manifestPath), providerCalls = 0, goldReadCount = 0, identityEvaluationReadCount = 0, bindingPolicy = "FAIL_CLOSED_DOCUMENT_ROLE_COVERAGE", bindingEligibleDocuments = bindingEligibleDocuments.OrderBy(x => x, StringComparer.Ordinal).ToArray(), bindingExcludedDocuments, totalFrozenBoundHeadingOccurrences = frozenSet.Length, perDocument = summary });
        File.WriteAllText(Path.Combine(rebind, "report.md"), $"# V8H0 execution-v2 binding revalidation\n\nProvider calls: **0**. Gold reads: **0**.\n\nBinding policy: fail closed when any role shard for a document is invalid or provider-error.\n\nEligible documents: **{bindingEligibleDocuments.Count}**. Excluded documents: **{bindingExcludedDocuments.Length}**. Frozen bound headings: **{frozenSet.Length}**.\n", new UTF8Encoding(false));
        Console.WriteLine($"V8H0_REBIND_STATUS=COMPLETED_WITH_FAILURES_PRESERVED ELIGIBLE_DOCS={bindingEligibleDocuments.Count} EXCLUDED_DOCS={bindingExcludedDocuments.Length} BOUND={frozenSet.Length}");
        await Task.CompletedTask;
    }

    private static IReadOnlyDictionary<string, BlockBinding> LoadBlockBindings(string input)
    {
        var candidate = Load(Path.Combine(input, "production-candidate-manifest.json"));
        var result = new Dictionary<string, BlockBinding>(StringComparer.Ordinal);
        foreach (var doc in candidate.GetProperty("documents").EnumerateArray())
        {
            var documentId = doc.GetProperty("documentId").GetString()!;
            foreach (var block in doc.GetProperty("selectedBlocks").EnumerateArray())
            {
                var binding = new BlockBinding(documentId, block.GetProperty("ref").GetString()!, block.GetProperty("productionBlockId").GetString()!, block.GetProperty("text").GetString()!, block.GetProperty("lineRefs").EnumerateArray().Select(x => x.GetString()!).ToArray(), block.GetProperty("exactSourceBinding").GetProperty("sourceOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
                result[$"{documentId}|{binding.OpaqueRef}"] = binding;
            }
        }
        return result;
    }

    private static IReadOnlyList<Binding> BuildBindings(string input, IReadOnlyList<Request> roleRequests, IReadOnlyList<Request> spanRequests, IReadOnlyList<Attempt> roleAttempts, IReadOnlyList<Attempt> spanAttempts, IReadOnlyDictionary<string, Decision> roleDecisions, IReadOnlySet<string> eligibleDocuments)
    {
        var candidate = Load(Path.Combine(input, "production-candidate-manifest.json"));
        var blockByDocRef = new Dictionary<string, BlockBinding>(StringComparer.Ordinal);
        foreach (var doc in candidate.GetProperty("documents").EnumerateArray())
        {
            var documentId = doc.GetProperty("documentId").GetString()!;
            foreach (var block in doc.GetProperty("selectedBlocks").EnumerateArray())
            {
                var b = new BlockBinding(documentId, block.GetProperty("ref").GetString()!, block.GetProperty("productionBlockId").GetString()!, block.GetProperty("text").GetString()!, block.GetProperty("lineRefs").EnumerateArray().Select(x => x.GetString()!).ToArray(), block.GetProperty("exactSourceBinding").GetProperty("sourceOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
                blockByDocRef[$"{documentId}|{b.OpaqueRef}"] = b;
            }
        }
        var spanByDocRef = spanAttempts.Where(x => x.ValidationStatus == "VALID").SelectMany(x => x.Decisions.Select(d => (Key: $"{x.Request.DocumentId}|{d.OpaqueRef}", d.Span))).ToDictionary(x => x.Key, x => x.Span, StringComparer.Ordinal);
        return BuildBindingsCore(blockByDocRef, roleDecisions, spanByDocRef, eligibleDocuments);
    }

    private static IReadOnlyList<Binding> BuildBindingsCore(IReadOnlyDictionary<string, BlockBinding> blockByDocRef, IReadOnlyDictionary<string, Decision> roleDecisions, IReadOnlyDictionary<string, Span?> spanByDocRef, IReadOnlySet<string> eligibleDocuments)
    {
        return roleDecisions.Values.Where(x => x.IsHeading && eligibleDocuments.Contains(x.DocumentId)).Select(decision =>
        {
            var key = $"{decision.DocumentId}|{decision.OpaqueRef}";
            if (!blockByDocRef.TryGetValue(key, out var block)) return new Binding(decision.DocumentId, decision.OpaqueRef, "UNKNOWN_REF", "", [], null, decision.Role, decision.Confidence);
            if (!spanByDocRef.TryGetValue(key, out var span) || span is null) return new Binding(decision.DocumentId, decision.OpaqueRef, "UNBOUND", block.Text, block.SourceRefs, null, decision.Role, decision.Confidence);
            if (span.Value.Start < 0 || span.Value.End > block.Text.Length || span.Value.Start >= span.Value.End) return new Binding(decision.DocumentId, decision.OpaqueRef, "EXACT_SOURCE_MISMATCH", block.Text, block.SourceRefs, span, decision.Role, decision.Confidence);
            return new Binding(decision.DocumentId, decision.OpaqueRef, "BOUND", block.Text, block.SourceRefs, span, decision.Role, decision.Confidence);
        }).ToArray();
    }

    private static string BuildReport(string status, int roleCalls, int spanCalls, int totalCalls, int bound) => $"# V8H0 heading extraction execution\n\nStatus: **{status}**.\n\nExecution used frozen preflight `{FrozenPreflightCommit}` with retry=0. Gold, identity labels, historical predictions and evaluation artifacts were not read.\n\n- Role calls: **{roleCalls}** / {ExpectedRoleRequests}\n- Pointer calls: **{spanCalls}** / {MaxSpanRequests} upper bound\n- Total calls: **{totalCalls}**\n- Frozen bound heading occurrences: **{bound}**\n\nRole output was required to cover every block ID exactly once. Pointer shards were triggered only when their frozen shard contained a valid role-selected heading at confidence >= 0.65. Failures are retained; no retry or response repair was performed.\n";

    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void WriteAttemptsIncremental(string path, IReadOnlyList<Attempt> roles, IReadOnlyList<Attempt> spans, IReadOnlyList<StartedAttempt> started) =>
        Write(path, new { schemaVersion = "a99-v8h0-attempts-incremental-v2", executionCampaign = ExecutionCampaign, immutableAttempts = true, started = started.Select(x => x.ToPublic()).ToArray(), role = roles.Select(x => x.ToPublic()).ToArray(), span = spans.Select(x => x.ToPublic()).ToArray() });
    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ExtractContent(string response) { using var doc = JsonDocument.Parse(response); return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? ""; }
    private static string ExtractJsonObject(string raw) { var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}'); return start >= 0 && end > start ? raw[start..(end + 1)] : raw; }
    private static int BoundaryOutputBudget(string prompt) => Math.Clamp(96 + Count(prompt, "\"id\"") * 64, 256, 768);
    private static int Count(string value, string token) { var count = 0; var index = 0; while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; } return count; }
    private static string SanitizeFilePart(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed record Request(string DocumentId, string Stage, string RequestKey, int ShardOrdinal, string SerializedRequest, string RequestSha256, IReadOnlyList<string> BlockRefs, IReadOnlyList<string> ProductionIds)
    {
    }

    private sealed record Decision(string Ref, string Role, double Confidence, bool IsHeading, Span? Span)
    {
        public string DocumentId { get; init; } = "";
        public string OpaqueRef { get; init; } = Ref;
        public object ToPublic() => new { documentId = DocumentId, @ref = OpaqueRef, productionRef = Ref, role = Role, confidence = Confidence, isHeading = IsHeading, span = Span };
    }
    private readonly record struct Span(int Start, int End);
    private sealed record Validation(string Status, string Reason, IReadOnlyList<Decision> Decisions)
    {
        public static Validation Valid(IReadOnlyList<Decision> decisions) => new("VALID", "ok", decisions);
        public static Validation Invalid(string reason) => new("INVALID_VALIDATION", reason, []);
    }
    private sealed record Attempt(Request Request, string Stage, DateTimeOffset Started, DateTimeOffset Finished, string Status, int? HttpStatus, string RawProviderResponse, string ParsedContent, string RawResponseSha256, string ValidationStatus, string ValidationReason, IReadOnlyList<Decision> Decisions, long LatencyMs, int ProviderRequestBytes, int MaxOutputTokens)
    {
        public object ToPublic() => new { documentId = Request.DocumentId, stage = Stage, requestKey = Request.RequestKey, requestSha256 = Request.RequestSha256, started = Started, finished = Finished, status = Status, httpStatus = HttpStatus, rawProviderResponseSha256 = RawResponseSha256, rawProviderResponse = RawProviderResponse, parsedContent = ParsedContent, validationStatus = ValidationStatus, validationReason = ValidationReason, decisions = Decisions, latencyMs = LatencyMs, providerRequestBytes = ProviderRequestBytes, maxOutputTokens = MaxOutputTokens };
    }
    private sealed record StartedAttempt(Request Request, string Stage, DateTimeOffset Started)
    {
        public object ToPublic() => new { documentId = Request.DocumentId, stage = Stage, requestKey = Request.RequestKey, requestSha256 = Request.RequestSha256, started = Started, status = "STARTED", rawPersisted = false, parsedPersisted = false, validationPersisted = false };
    }
    private sealed record BlockBinding(string DocumentId, string OpaqueRef, string ProductionRef, string Text, IReadOnlyList<string> LineRefs, IReadOnlyList<string> SourceRefs);
    private sealed record Binding(string DocumentId, string BlockRef, string BindingStatus, string SourceText, IReadOnlyList<string> SourceRefs, Span? Span, string Role, double Confidence)
    {
        public object ToPublic() => new { documentId = DocumentId, blockRef = BlockRef, bindingStatus = BindingStatus, sourceText = SourceText, sourceRefs = SourceRefs, span = Span, role = Role, confidence = Confidence, exactTextOwnedByHarness = true };
    }
}
