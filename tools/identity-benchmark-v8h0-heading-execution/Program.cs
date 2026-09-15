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
    private const string OutputRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-execution-v1";
    private const string FrozenPreflightCommit = "98ce62e";
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
        if (Directory.Exists(output) && Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("V8H0_EXECUTION_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        Directory.CreateDirectory(output);

        var manifestPath = Path.Combine(input, "manifest.json");
        var manifest = Load(manifestPath);
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
            var attempt = await ExecuteAsync(client, endpoint, apiKey, model, request, "ROLE");
            roleExecution.Add(attempt);
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution);
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
            var attempt = await ExecuteAsync(client, endpoint, apiKey, model, request, "POINTER_SPAN");
            spanExecution.Add(attempt);
            WriteAttemptsIncremental(attemptPath, roleExecution, spanExecution);
        }

        var allAttempts = roleExecution.Concat(spanExecution).ToArray();
        var bindings = BuildBindings(input, roleRequests, spanRequests, roleExecution, spanExecution, roleDecisions);
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
            schemaVersion = "a99-v8h0-heading-extraction-execution-v1",
            status = providerFailures == 0 && invalid == 0 ? "COMPLETED_WITHOUT_PROVIDER_FAILURE" : "COMPLETED_WITH_FAILURES_PRESERVED",
            authority = new
            {
                preflightCommit = FrozenPreflightCommit,
                preflightManifestSha256 = Sha256File(manifestPath),
                preflightRequestIndexSha256 = Sha256File(Path.Combine(input, "request-index.json")),
                requestsImmutable = true,
                bindingRepair = false,
            },
            provider = new { provider = "OpenRouter", model, endpoint = endpoint.ToString(), retry = 0, roleCalls = roleExecution.Count, spanCalls = spanExecution.Count, totalCalls = allAttempts.Length },
            role = new { scheduled = roleRequests.Length, executed = roleExecution.Count, valid = roleExecution.Count(x => x.ValidationStatus == "VALID"), invalidValidation = roleExecution.Count(x => x.ValidationStatus == "INVALID_VALIDATION"), providerError = roleExecution.Count(x => x.Status == "PROVIDER_ERROR") },
            span = new { materializedUpperBound = spanRequests.Length, triggered = triggeredSpanIds.Count, executed = spanExecution.Count, valid = spanExecution.Count(x => x.ValidationStatus == "VALID"), invalidValidation = spanExecution.Count(x => x.ValidationStatus == "INVALID_VALIDATION"), providerError = spanExecution.Count(x => x.Status == "PROVIDER_ERROR") },
            headingOutput = new { totalFrozenBoundHeadingOccurrences = frozenSet.Length, perDocument = headingSummary },
            firewall = new { goldReadCount = 0, identityLabelReadCount = 0, historicalEvaluationReadCount = 0, v4hToV8PredictionReadCount = 0, v5ToV8PredictionReadCount = 0, v6ToV8PredictionReadCount = 0, v7ToV8PredictionReadCount = 0, identityCandidateGeneration = false },
        };

        Write(Path.Combine(output, "attempts.json"), new { schemaVersion = "a99-v8h0-attempts-v1", attempts = allAttempts.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "role-decisions.json"), new { schemaVersion = "a99-v8h0-role-decisions-v1", decisions = roleDecisions.Values.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "frozen-bound-heading-occurrence-set.json"), new { schemaVersion = "a99-v8h0-frozen-bound-heading-occurrence-set-v1", sourceUniverseSha256 = Sha256File(Path.Combine(input, "source-universe.json")), candidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")), immutable = true, occurrences = frozenSet.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "heading-summary.json"), new { schemaVersion = "a99-v8h0-heading-summary-v1", role = manifestOut.role, span = manifestOut.span, perDocument = headingSummary });
        Write(Path.Combine(output, "execution-manifest.json"), manifestOut);
        Write(Path.Combine(output, "source-authority-sha.json"), new { sourceUniverseSha256 = Sha256File(Path.Combine(input, "source-universe.json")), candidateManifestSha256 = Sha256File(Path.Combine(input, "production-candidate-manifest.json")), sourceUniverseRead = true, selectedCandidateManifestRead = true });
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(manifestOut.status, roleExecution.Count, spanExecution.Count, allAttempts.Length, frozenSet.Length), new UTF8Encoding(false));
        Console.WriteLine($"V8H0_EXECUTION_STATUS={manifestOut.status} ROLE_CALLS={roleExecution.Count} SPAN_CALLS={spanExecution.Count} TOTAL_CALLS={allAttempts.Length} BOUND={frozenSet.Length}");
    }

    private static async Task<Attempt> ExecuteAsync(HttpClient client, Uri endpoint, string apiKey, string model, Request request, string stage)
    {
        var started = DateTimeOffset.UtcNow;
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
            var content = response.IsSuccessStatusCode ? ExtractContent(responseText) : "";
            var rawHash = Sha256(responseText);
            var validation = response.IsSuccessStatusCode ? ValidateResponse(stage, request, content) : Validation.Invalid("provider-http-status");
            var mappedDecisions = validation.Decisions.Select(decision => decision with
            {
                DocumentId = request.DocumentId,
                OpaqueRef = OpaqueRefFor(request, decision.Ref),
            }).ToArray();
            validation = validation with { Decisions = mappedDecisions };
            return new Attempt(request, stage, started, DateTimeOffset.UtcNow, response.IsSuccessStatusCode ? "COMPLETED" : "PROVIDER_ERROR", (int)response.StatusCode, responseText, content, rawHash, validation.Status, validation.Reason, validation.Decisions, stopwatch.ElapsedMilliseconds, requestBytes.Length, maxTokens);
        }
        catch (Exception ex)
        {
            return new Attempt(request, stage, started, DateTimeOffset.UtcNow, "PROVIDER_ERROR", null, "", "", Sha256(""), "INVALID_VALIDATION", ex.GetType().Name + ":" + ex.Message, [], 0, 0, 0);
        }
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

    private static IReadOnlyList<Binding> BuildBindings(string input, IReadOnlyList<Request> roleRequests, IReadOnlyList<Request> spanRequests, IReadOnlyList<Attempt> roleAttempts, IReadOnlyList<Attempt> spanAttempts, IReadOnlyDictionary<string, Decision> roleDecisions)
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
        var spanByDocRef = spanAttempts.Where(x => x.ValidationStatus == "VALID").SelectMany(x => x.Decisions.Select(d => (Key: $"{x.Request.DocumentId}|{d.Ref}", d.Span))).ToDictionary(x => x.Key, x => x.Span, StringComparer.Ordinal);
        return roleDecisions.Values.Where(x => x.IsHeading).Select(decision =>
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
    private static void WriteAttemptsIncremental(string path, IReadOnlyList<Attempt> roles, IReadOnlyList<Attempt> spans) =>
        Write(path, new { schemaVersion = "a99-v8h0-attempts-incremental-v1", immutableAttempts = true, role = roles.Select(x => x.ToPublic()).ToArray(), span = spans.Select(x => x.ToPublic()).ToArray() });
    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ExtractContent(string response) { using var doc = JsonDocument.Parse(response); return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? ""; }
    private static string ExtractJsonObject(string raw) { var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}'); return start >= 0 && end > start ? raw[start..(end + 1)] : raw; }
    private static int BoundaryOutputBudget(string prompt) => Math.Clamp(96 + Count(prompt, "\"id\"") * 64, 256, 768);
    private static int Count(string value, string token) { var count = 0; var index = 0; while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; } return count; }
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
    private sealed record BlockBinding(string DocumentId, string OpaqueRef, string ProductionRef, string Text, IReadOnlyList<string> LineRefs, IReadOnlyList<string> SourceRefs);
    private sealed record Binding(string DocumentId, string BlockRef, string BindingStatus, string SourceText, IReadOnlyList<string> SourceRefs, Span? Span, string Role, double Confidence)
    {
        public object ToPublic() => new { documentId = DocumentId, blockRef = BlockRef, bindingStatus = BindingStatus, sourceText = SourceText, sourceRefs = SourceRefs, span = Span, role = Role, confidence = Confidence, exactTextOwnedByHarness = true };
    }
}
