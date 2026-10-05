using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>One-shot execution of the frozen P6T-H2C direct end-pointer request universe.</summary>
internal static class P6TH2CEndPointerCanary
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string G2ACaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string G2APreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string OriginalPreflightPath = Root + "/p6th2c-end-pointer-preflight/h2c-exact-end-pointer-preflight.v1.json";
    private const string PreflightPath = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string CaptureRoot = Root + "/p6th2c-end-pointer-capture-20261005";
    private const string RetryCaptureRoot = Root + "/p6th2c-end-pointer-retry-capture-20261005";
    private const string ClarifiedRetryCaptureRoot = Root + "/p6th2c-end-pointer-clarified-retry-20261005";
    private const string Confirm = "yes-i-authorize-p6th2c-direct-end-pointer-thirty-one-primary-calls";
    private const string RetryConfirm = "yes-i-authorize-p6th2c-one-retry-each-for-two-quarantined-calls";
    private const string ClarifiedRetryConfirm = "yes-i-authorize-p6th2c-clarified-anchor-echo-retries-until-valid";
    private const int ResponseByteCap = 49_152;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string SystemPrompt = """
        Determine the exact source extent of the one heading that begins at the issued anchor occurrence. A heading may consist of one or more consecutive source occurrences. Return every and only consecutive occurrence that belongs literally to this exact heading. Do not include later body content merely because it belongs to the same section, topic, agenda item, or semantic region.

        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
        """;

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Kind.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
    ];

    internal enum F1Kind { RawCapture, ResultRow, ResultRows }
    internal sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Kind Kind, bool F1UsedCorrespondences);
    internal sealed record Request(Source Source, string PackId, string Anchor, string AnchorAlias,
        string SourceSha256, string SourceUniverseSha256, string F1CaptureSha256, string F1ResponseSha256,
        string G2ARawCaptureSha256, string G2AResponseSha256, byte[] Body, string BodyHash, int BodyBytes,
        string UserMessage, int UserBytes, int MaxCompletionTokens, IReadOnlyList<string> IssuedOccurrences);
    private sealed record Parsed(string Anchor, IReadOnlyList<string> HeadingMembers, string EndOccurrence,
        string? FirstOutsideOccurrence, string FirstOutsideRole);

    internal static IReadOnlyList<Request> BuildAllForTreatment(string repo, string treatment)
    {
        var prompt = P6TH2CCleanPairedBoundaryTreatment.SystemPrompt(treatment);
        return Sources.SelectMany(source => Build(repo, source, prompt, "v5-function-conditioned-exact-end-pointer-clean-paired-1")).ToArray();
    }

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        Request[] requests;
        try { requests = Sources.SelectMany(source => Build(repo, source)).ToArray(); }
        catch (Exception exception) { return Fail($"p6th2c: request reconstruction failed before network: {exception}"); }
        if (requests.Length != 31) return Fail($"p6th2c: expected exactly 31 frozen requests, got {requests.Length}; no network");

        var manifestPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!ManifestParity(manifest.RootElement, requests)) return Fail("p6th2c: frozen request manifest parity failed; no network");
        if (!args.Contains($"--confirm-p6th2c={Confirm}"))
        {
            Console.WriteLine("P6T-H2C PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            Console.WriteLine($"Frozen requests={requests.Length}; max body={requests.Max(value => value.BodyBytes)} B; max user={requests.Max(value => value.UserBytes)} B.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th2c: OPENROUTER_API_KEY missing; no network");
        var captureDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(captureDir)) return Fail("p6th2c: immutable capture directory exists; no resend");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        Directory.CreateDirectory(captureDir);
        WriteNew(Path.Combine(captureDir, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-exact-end-pointer-reservation-v1",
            status = "THIRTY_ONE_PRIMARY_CALLS_RESERVED",
            preflightSha256 = Hash(File.ReadAllText(manifestPath)),
            g2aPreflightSha256 = Hash(File.ReadAllText(Path.Combine(repo, G2APreflightPath.Replace('/', Path.DirectorySeparatorChar)))),
            requestCount = requests.Length,
            requestOrder = requests.Select((value, index) => new { providerCallOrdinal = index + 1, value.Source.DocumentId, value.PackId, value.Anchor, providerBodySha256 = value.BodyHash }).ToArray(),
            providerCallsBeforeSend = 0,
            maximumPrimaryCalls = 31,
            retriesAllowed = 0,
            repairAllowed = false,
            fallbackAllowed = false,
            goldRead = false,
            runtimeChanged = false,
        });

        var summaries = new List<object>();
        var accepted = 0;
        var calls = 0;
        foreach (var request in requests)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            calls++;
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens,
                    SystemPrompt, request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            // Persist immutable provider material before parsing or classifying the response.
            var rawPath = Path.Combine(captureDir, $"{request.Source.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th2c-exact-end-pointer-raw-capture-v1",
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                sourceSha256 = request.SourceSha256,
                sourceUniverseSha256 = request.SourceUniverseSha256,
                f1CaptureSha256 = request.F1CaptureSha256,
                f1ResponseSha256 = request.F1ResponseSha256,
                g2aRawCaptureSha256 = request.G2ARawCaptureSha256,
                g2aResponseSha256 = request.G2AResponseSha256,
                providerCallOrdinal = calls,
                providerCalls = 1,
                providerBodySha256 = request.BodyHash,
                providerBodyBytes = request.BodyBytes,
                userMessageSha256 = Hash(request.UserMessage),
                userMessageBytes = request.UserBytes,
                systemPromptSha256 = Hash(SystemPrompt),
                issuedOccurrenceCount = request.IssuedOccurrences.Count,
                issuedOccurrences = request.IssuedOccurrences,
                reasoningRequested = true,
                reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                reasoningExecutionConfirmed = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens") is > 0,
                promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                completionTokens = Usage(observation?.Usage, "completion_tokens"),
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                rawResponse = observation?.Content,
                transportError,
                goldReadDuringCapture = false,
            });

            var parsed = observation is not null && transportError is null && observation.FinishReason == "stop"
                ? TryParse(observation.Content, request.IssuedOccurrences, request.Anchor)
                : null;
            if (parsed is not null) accepted++;
            summaries.Add(new
            {
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                providerCallOrdinal = calls,
                providerBodySha256 = request.BodyHash,
                issued = request.IssuedOccurrences.Count,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                ledgerAccepted = parsed is not null,
                returnedHeadingMembers = parsed?.HeadingMembers.Count,
                endOccurrence = parsed?.EndOccurrence,
                firstOutsideOccurrence = parsed?.FirstOutsideOccurrence,
                firstOutsideRole = parsed?.FirstOutsideRole,
                transportError,
            });
            Console.WriteLine($"[{calls}/{requests.Length}] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}, members={parsed?.HeadingMembers.Count.ToString() ?? "n/a"}");
        }

        WriteNew(Path.Combine(captureDir, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-exact-end-pointer-result-v1",
            status = accepted == requests.Length ? "ALL_FROZEN_END_POINTER_LEDGERS_ACCEPTED" : "ONE_OR_MORE_END_POINTER_LEDGERS_NOT_ACCEPTED",
            providerCalls = calls,
            acceptedLedgers = accepted,
            maximumPrimaryCalls = 31,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            rows = summaries,
        });
        return 0;
    }

    public static int FreezeCaptureHashes(string repo)
    {
        var captureDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(repo, OriginalPreflightPath.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(captureDir, "result.v1.json");
        if (!Directory.Exists(captureDir) || !File.Exists(resultPath)) return Fail("p6th2c: complete raw capture/result not found; no freeze receipt written");
        var rawFiles = Directory.GetFiles(captureDir, "*.raw-capture.v1.json").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (rawFiles.Length != 31) return Fail($"p6th2c: expected 31 raw files, found {rawFiles.Length}; no freeze receipt written");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        if (result.RootElement.GetProperty("providerCalls").GetInt32() != 31 || result.RootElement.GetProperty("retry").GetInt32() != 0 ||
            result.RootElement.GetProperty("goldRead").GetBoolean() || result.RootElement.GetProperty("runtimeChanged").GetBoolean())
            return Fail("p6th2c: execution result violates the frozen no-retry/no-Gold/no-runtime policy");
        var frozen = manifest.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}", StringComparer.Ordinal);
        var hashes = new List<object>();
        var orderedDigestRows = new List<string>();
        foreach (var path in rawFiles)
        {
            var bytes = File.ReadAllBytes(path);
            using var capture = JsonDocument.Parse(bytes);
            var row = capture.RootElement;
            var key = $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}|{row.GetProperty("anchor").GetString()}";
            if (!frozen.TryGetValue(key, out var request)) return Fail($"p6th2c: raw capture is outside frozen request universe: {Path.GetFileName(path)}");
            var rawSha = Hash(bytes);
            var responseSha = Hash(row.GetProperty("rawResponse").GetString()!);
            var sseSha = Hash(row.GetProperty("rawSse").GetString()!);
            if (responseSha != row.GetProperty("rawResponseSha256").GetString() || sseSha != row.GetProperty("rawSseSha256").GetString() ||
                row.GetProperty("providerBodySha256").GetString() != request.GetProperty("ProviderBodySha256").GetString() ||
                row.GetProperty("retryCount").GetInt32() != 0 || row.GetProperty("providerCalls").GetInt32() != 1)
                return Fail($"p6th2c: raw hash/body/retry verification failed: {Path.GetFileName(path)}");
            var ordinal = row.GetProperty("providerCallOrdinal").GetInt32();
            hashes.Add(new
            {
                providerCallOrdinal = ordinal,
                documentId = row.GetProperty("documentId").GetString(),
                packId = row.GetProperty("packId").GetString(),
                anchor = row.GetProperty("anchor").GetString(),
                rawCaptureFile = Path.GetFileName(path),
                rawCaptureFileSha256 = rawSha,
                rawResponseSha256 = responseSha,
                rawSseSha256 = sseSha,
                providerBodySha256 = row.GetProperty("providerBodySha256").GetString(),
                finishReason = row.GetProperty("finishReason").GetString(),
                retryCount = row.GetProperty("retryCount").GetInt32(),
                reasoningTokens = row.TryGetProperty("reasoningTokens", out var usage) && usage.ValueKind == JsonValueKind.Number && usage.TryGetInt32(out var reasoning) ? reasoning : (int?)null,
            });
            orderedDigestRows.Add($"{ordinal:D2}|{key}|{rawSha}");
        }
        if (hashes.Count != 31 || orderedDigestRows.Distinct(StringComparer.Ordinal).Count() != 31 || frozen.Count != 31)
            return Fail("p6th2c: duplicate or incomplete capture/request mapping");
        var captureSetSha = Hash(string.Join("\n", orderedDigestRows.OrderBy(value => value, StringComparer.Ordinal)) + "\n");
        var freezePath = Path.Combine(captureDir, "capture-freeze.v2.json");
        WriteNew(freezePath, new
        {
            schemaVersion = "v5-p6th2c-capture-freeze-v2",
            status = "RAW_CAPTURE_SET_HASH_VERIFIED_GOLD_CLOSED",
            requestManifestSha256 = Hash(File.ReadAllText(manifestPath)),
            executionResultSha256 = Hash(File.ReadAllBytes(resultPath)),
            captureSetSha256 = captureSetSha,
            captureCount = hashes.Count,
            providerCalls = 31,
            acceptedLedgers = result.RootElement.GetProperty("acceptedLedgers").GetInt32(),
            quarantinedLedgers = 31 - result.RootElement.GetProperty("acceptedLedgers").GetInt32(),
            allRawResponseAndSseHashesVerified = true,
            allProviderBodiesMatchFrozenManifest = true,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            captures = hashes.OrderBy(value => (int)value.GetType().GetProperty("providerCallOrdinal")!.GetValue(value)!).ToArray(),
        });
        Console.WriteLine($"P6T-H2C raw capture hash freeze complete: 31/31 files; captureSetSha256={captureSetSha}; GoldRead=false.");
        return 0;
    }

    public static async Task<int> RetryQuarantinedOnceAsync(string repo, string[] args)
    {
        Request[] requests;
        try { requests = Sources.SelectMany(source => Build(repo, source)).ToArray(); }
        catch (Exception exception) { return Fail($"p6th2c retry: request reconstruction failed before network: {exception.Message}"); }
        if (requests.Length != 31) return Fail($"p6th2c retry: expected 31 original requests, got {requests.Length}; no network");
        var preflightPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(File.ReadAllText(preflightPath));
        if (!ManifestParity(manifest.RootElement, requests)) return Fail("p6th2c retry: frozen provider-body parity failed; no network");

        var originalDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var originalResultPath = Path.Combine(originalDir, "result.v1.json");
        if (!File.Exists(originalResultPath)) return Fail("p6th2c retry: original result missing; no network");
        using var originalResult = JsonDocument.Parse(File.ReadAllText(originalResultPath));
        if (originalResult.RootElement.GetProperty("providerCalls").GetInt32() != 31 || originalResult.RootElement.GetProperty("retry").GetInt32() != 0)
            return Fail("p6th2c retry: original cohort is not the expected immutable 31-call capture; no network");

        var quarantined = new List<(Request Request, string OriginalRawSha)>();
        foreach (var row in originalResult.RootElement.GetProperty("rows").EnumerateArray().Where(value => !value.GetProperty("ledgerAccepted").GetBoolean()))
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("packId").GetString()!;
            var anchor = row.GetProperty("anchor").GetString()!;
            var request = requests.SingleOrDefault(value => value.Source.DocumentId == documentId && value.PackId == packId && value.Anchor == anchor);
            if (request is null || row.GetProperty("finishReason").GetString() != "stop" || row.GetProperty("providerBodySha256").GetString() != request.BodyHash)
                return Fail("p6th2c retry: quarantine is not a same-body completed response; no network");
            var originalPath = Path.Combine(originalDir, $"{documentId}_{anchor}.raw-capture.v1.json");
            using var originalRaw = JsonDocument.Parse(File.ReadAllBytes(originalPath));
            var rawRoot = originalRaw.RootElement;
            var response = rawRoot.GetProperty("rawResponse").GetString()!;
            var sse = rawRoot.GetProperty("rawSse").GetString()!;
            if (Hash(response) != rawRoot.GetProperty("rawResponseSha256").GetString() || Hash(sse) != rawRoot.GetProperty("rawSseSha256").GetString() ||
                TryParse(response, request.IssuedOccurrences, request.Anchor) is not null)
                return Fail("p6th2c retry: original invalid ledger/hash evidence mismatch; no network");
            quarantined.Add((request, Hash(File.ReadAllBytes(originalPath))));
        }
        var expected = new HashSet<string>(StringComparer.Ordinal) { "SRC-041|O5", "DOC-0256|O1" };
        if (quarantined.Count != 2 || !quarantined.Select(value => $"{value.Request.Source.DocumentId}|{value.Request.Anchor}").ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            return Fail("p6th2c retry: quarantined call set differs from the two authorized anchors; no network");

        var retryDir = Path.Combine(repo, RetryCaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(retryDir)) return Fail("p6th2c retry: immutable retry directory exists; no resend");
        if (!args.Contains($"--confirm-p6th2c-retry={RetryConfirm}"))
        {
            Console.WriteLine("P6T-H2C RETRY PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            Console.WriteLine("Exactly one replay each for SRC-041/O5 and DOC-0256/O1; request bodies unchanged.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th2c retry: OPENROUTER_API_KEY missing; no network");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();
        Directory.CreateDirectory(retryDir);
        WriteNew(Path.Combine(retryDir, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-quarantined-retry-reservation-v1",
            status = "TWO_EXACT_BODY_RETRIES_RESERVED",
            originalCaptureFreezeSha256 = Hash(File.ReadAllBytes(Path.Combine(originalDir, "capture-freeze.v1.json"))),
            preflightSha256 = Hash(File.ReadAllText(preflightPath)),
            providerCalls = 0,
            maximumAuthorizedRetryCalls = 2,
            requests = quarantined.Select((value, index) => new
            {
                retryOrdinal = index + 1,
                documentId = value.Request.Source.DocumentId,
                value.Request.PackId,
                value.Request.Anchor,
                providerBodySha256 = value.Request.BodyHash,
                value.OriginalRawSha,
                authorizedReplayNumber = 1,
            }).ToArray(),
            transportRetriesPerCall = 0,
            repair = false,
            fallback = false,
            goldRead = false,
        });

        var summaries = new List<object>();
        var accepted = 0;
        var calls = 0;
        foreach (var (request, originalRawSha) in quarantined)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            calls++;
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens, SystemPrompt, request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var rawPath = Path.Combine(retryDir, $"{request.Source.DocumentId}_{request.Anchor}.retry1.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th2c-exact-end-pointer-retry-raw-capture-v1",
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                providerCallOrdinal = calls,
                providerCalls = 1,
                authorizedReplayNumber = 1,
                originalRawCaptureSha256 = originalRawSha,
                providerBodySha256 = request.BodyHash,
                providerBodyBytes = request.BodyBytes,
                userMessageSha256 = Hash(request.UserMessage),
                systemPromptSha256 = Hash(SystemPrompt),
                issuedOccurrenceCount = request.IssuedOccurrences.Count,
                reasoningRequested = true,
                reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                reasoningExecutionConfirmed = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens") is > 0,
                promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                completionTokens = Usage(observation?.Usage, "completion_tokens"),
                finishReason = observation?.FinishReason,
                transportRetryCount = observation?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                rawSse = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                rawResponse = observation?.Content,
                transportError,
                goldReadDuringCapture = false,
            });
            var parsed = observation is not null && transportError is null && observation.FinishReason == "stop"
                ? TryParse(observation.Content, request.IssuedOccurrences, request.Anchor) : null;
            if (parsed is not null) accepted++;
            summaries.Add(new
            {
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                providerCallOrdinal = calls,
                authorizedReplayNumber = 1,
                providerBodySha256 = request.BodyHash,
                finishReason = observation?.FinishReason,
                transportRetryCount = observation?.RetryCount ?? 0,
                ledgerAccepted = parsed is not null,
                returnedHeadingMembers = parsed?.HeadingMembers.Count,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                transportError,
            });
            Console.WriteLine($"[retry {calls}/2] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}");
        }
        WriteNew(Path.Combine(retryDir, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-quarantined-retry-result-v1",
            status = accepted == calls ? "BOTH_RETRY_LEDGERS_ACCEPTED" : "ONE_OR_MORE_RETRY_LEDGERS_NOT_ACCEPTED",
            providerCalls = calls,
            acceptedLedgers = accepted,
            authorizedMaximumRetryCalls = 2,
            transportRetries = 0,
            furtherRetryAuthorized = false,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            rows = summaries,
        });
        return 0;
    }

    public static async Task<int> RetryUntilAcceptedAsync(string repo, string[] args)
    {
        Request[] requests;
        try { requests = Sources.SelectMany(source => Build(repo, source)).ToArray(); }
        catch (Exception exception) { return Fail($"p6th2c retry loop: request reconstruction failed before network: {exception.Message}"); }
        if (requests.Length != 31) return Fail($"p6th2c retry loop: expected 31 requests, got {requests.Length}; no network");
        var preflightPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(File.ReadAllText(preflightPath));
        if (!ManifestParity(manifest.RootElement, requests)) return Fail("p6th2c retry loop: frozen body parity failed; no network");
        if (!args.Contains("--confirm-p6th2c-retry-until-accepted=yes-i-authorize-retries-until-each-quarantined-anchor-has-an-accepted-ledger"))
        {
            Console.WriteLine("P6T-H2C RETRY LOOP PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th2c retry loop: OPENROUTER_API_KEY missing; no network");

        var primaryDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var initialRetryDir = Path.Combine(repo, RetryCaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var primaryResultPath = Path.Combine(primaryDir, "result.v1.json");
        var initialRetryResultPath = Path.Combine(initialRetryDir, "result.v1.json");
        if (!File.Exists(primaryResultPath) || !File.Exists(initialRetryResultPath)) return Fail("p6th2c retry loop: initial immutable capture/retry artifacts missing");
        using var primaryResult = JsonDocument.Parse(File.ReadAllText(primaryResultPath));
        using var initialRetryResult = JsonDocument.Parse(File.ReadAllText(initialRetryResultPath));
        if (primaryResult.RootElement.GetProperty("providerCalls").GetInt32() != 31 ||
            initialRetryResult.RootElement.GetProperty("providerCalls").GetInt32() != 2 ||
            primaryResult.RootElement.GetProperty("goldRead").GetBoolean() || initialRetryResult.RootElement.GetProperty("goldRead").GetBoolean())
            return Fail("p6th2c retry loop: immutable execution history is incomplete or Gold was read");

        var requestByKey = requests.ToDictionary(value => $"{value.Source.DocumentId}|{value.PackId}|{value.Anchor}", StringComparer.Ordinal);
        var targetKeys = new HashSet<string>(StringComparer.Ordinal) { "SRC-041|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_060|O5", "DOC-0256|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001|O1" };
        var accepted = new Dictionary<string, (int Attempt, string Path, string RawSha, string ResponseSha, string SseSha)>(StringComparer.Ordinal);
        var attemptCountByKey = targetKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var key in targetKeys)
        {
            if (!requestByKey.ContainsKey(key)) return Fail($"p6th2c retry loop: authorized key is outside frozen request universe: {key}");
            var pieces = key.Split('|');
            var primaryPath = Path.Combine(primaryDir, $"{pieces[0]}_{pieces[2]}.raw-capture.v1.json");
            using var primaryRaw = JsonDocument.Parse(File.ReadAllBytes(primaryPath));
            VerifyReplayMaterial(primaryRaw.RootElement, requestByKey[key], expectedOrdinal: null);
            var initialPath = Path.Combine(initialRetryDir, $"{pieces[0]}_{pieces[2]}.retry1.raw-capture.v1.json");
            if (File.Exists(initialPath))
            {
                using var initialRaw = JsonDocument.Parse(File.ReadAllBytes(initialPath));
                VerifyReplayMaterial(initialRaw.RootElement, requestByKey[key], expectedOrdinal: 1);
                attemptCountByKey[key] = 1;
                RegisterIfAccepted(key, requestByKey[key], initialPath, 1, initialRaw.RootElement);
            }
        }

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var round = 2;
        var totalLoopCalls = 0;
        while (accepted.Count < targetKeys.Count)
        {
            var pending = targetKeys.Where(key => !accepted.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var roundDir = Path.Combine(initialRetryDir, $"attempt-{round:D3}");
            if (Directory.Exists(roundDir)) return Fail($"p6th2c retry loop: attempt directory exists without safe resume authority: {roundDir}");
            Directory.CreateDirectory(roundDir);
            WriteNew(Path.Combine(roundDir, "execution-reservation.v1.json"), new
            {
                schemaVersion = "v5-p6th2c-retry-round-reservation-v1",
                status = "EXACT_FROZEN_BODY_RETRY_RESERVED",
                attempt = round,
                requests = pending.Select((key, index) => new
                {
                    retryOrdinal = index + 1,
                    documentId = requestByKey[key].Source.DocumentId,
                    requestByKey[key].PackId,
                    requestByKey[key].Anchor,
                    providerBodySha256 = requestByKey[key].BodyHash,
                    previousRawCaptureSha256 = LatestRawSha(initialRetryDir, requestByKey[key], round - 1),
                }).ToArray(),
                providerCallsBeforeSend = 0,
                retriesPerCall = 0,
                repair = false,
                fallback = false,
                goldRead = false,
            });

            var rows = new List<object>();
            foreach (var key in pending)
            {
                var request = requestByKey[key];
                OpenRouterExecutionObservation? observation = null;
                string? transportError = null;
                var watch = Stopwatch.StartNew();
                try
                {
                    using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                    observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens, SystemPrompt, request.UserMessage).ConfigureAwait(false);
                }
                catch (Exception exception) { transportError = exception.Message; }
                watch.Stop();
                totalLoopCalls++;
                var rawPath = Path.Combine(roundDir, $"{request.Source.DocumentId}_{request.Anchor}.retry{round}.raw-capture.v1.json");
                WriteNew(rawPath, new
                {
                    schemaVersion = "v5-p6th2c-exact-end-pointer-retry-raw-capture-v1",
                    documentId = request.Source.DocumentId,
                    packId = request.PackId,
                    anchor = request.Anchor,
                    anchorAlias = request.AnchorAlias,
                    attempt = round,
                    providerCallOrdinal = totalLoopCalls,
                    providerCalls = 1,
                    providerBodySha256 = request.BodyHash,
                    providerBodyBytes = request.BodyBytes,
                    userMessageSha256 = Hash(request.UserMessage),
                    systemPromptSha256 = Hash(SystemPrompt),
                    issuedOccurrenceCount = request.IssuedOccurrences.Count,
                    reasoningRequested = true,
                    reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                    reasoningExecutionConfirmed = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens") is > 0,
                    promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                    completionTokens = Usage(observation?.Usage, "completion_tokens"),
                    finishReason = observation?.FinishReason,
                    transportRetryCount = observation?.RetryCount ?? 0,
                    latencyMs = watch.Elapsed.TotalMilliseconds,
                    rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                    rawSse = observation?.RawSse,
                    rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                    rawResponse = observation?.Content,
                    transportError,
                    goldReadDuringCapture = false,
                });
                var parsed = observation is not null && transportError is null && observation.FinishReason == "stop"
                    ? TryParse(observation.Content, request.IssuedOccurrences, request.Anchor) : null;
                if (parsed is not null)
                {
                    var fileBytes = File.ReadAllBytes(rawPath);
                    accepted.Add(key, (round, rawPath, Hash(fileBytes), Hash(observation!.Content), Hash(observation.RawSse)));
                }
                attemptCountByKey[key]++;
                rows.Add(new
                {
                    documentId = request.Source.DocumentId,
                    packId = request.PackId,
                    anchor = request.Anchor,
                    attempt = round,
                    providerBodySha256 = request.BodyHash,
                    finishReason = observation?.FinishReason,
                    transportRetryCount = observation?.RetryCount ?? 0,
                    ledgerAccepted = parsed is not null,
                    returnedHeadingMembers = parsed?.HeadingMembers.Count,
                    rawCaptureFileSha256 = Hash(File.ReadAllBytes(rawPath)),
                    rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                    rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                    transportError,
                });
                Console.WriteLine($"[retry attempt {round}] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}");
            }
            WriteNew(Path.Combine(roundDir, "result.v1.json"), new
            {
                schemaVersion = "v5-p6th2c-retry-round-result-v1",
                status = rows.All(value => (bool)value.GetType().GetProperty("ledgerAccepted")!.GetValue(value)!) ? "ROUND_LEDGERS_ACCEPTED" : "ROUND_HAS_QUARANTINE",
                attempt = round,
                providerCalls = rows.Count,
                acceptedLedgers = rows.Count(value => (bool)value.GetType().GetProperty("ledgerAccepted")!.GetValue(value)!),
                transportRetries = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                rows,
            });
            round++;
        }

        var finalRows = accepted.OrderBy(value => value.Key, StringComparer.Ordinal).Select(pair => new
        {
            key = pair.Key,
            documentId = requestByKey[pair.Key].Source.DocumentId,
            packId = requestByKey[pair.Key].PackId,
            anchor = requestByKey[pair.Key].Anchor,
            providerBodySha256 = requestByKey[pair.Key].BodyHash,
            firstPrimaryCallAccepted = false,
            retryAttemptAccepted = pair.Value.Attempt,
            retriesForAnchor = attemptCountByKey[pair.Key],
            acceptedRawCaptureSha256 = pair.Value.RawSha,
            acceptedRawResponseSha256 = pair.Value.ResponseSha,
            acceptedRawSseSha256 = pair.Value.SseSha,
        }).ToArray();
        WriteNew(Path.Combine(initialRetryDir, "retry-until-accepted-summary.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-retry-until-accepted-summary-v1",
            status = "BOTH_QUARANTINED_ANCHORS_HAVE_CONTRACT_VALID_RAW_RESULTS",
            originalPrimaryProviderCalls = 31,
            initialAuthorizedRetries = 2,
            additionalLoopProviderCalls = totalLoopCalls,
            totalProviderCallsIncludingRetries = 33 + totalLoopCalls,
            anchorsResolved = accepted.Count,
            maximumRetryCountPerAnchor = attemptCountByKey.Values.Max(),
            transportRetries = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            acceptedAnchors = finalRows,
        });
        Console.WriteLine($"P6T-H2C retry loop complete: 2/2 anchors accepted; additional calls={totalLoopCalls}; GoldRead=false.");
        return 0;

        void RegisterIfAccepted(string key, Request request, string path, int attempt, JsonElement raw)
        {
            VerifyReplayMaterial(raw, request, attempt);
            var response = raw.GetProperty("rawResponse").GetString()!;
            if (raw.GetProperty("finishReason").GetString() == "stop" && TryParse(response, request.IssuedOccurrences, request.Anchor) is not null)
                accepted[key] = (attempt, path, Hash(File.ReadAllBytes(path)), Hash(response), Hash(raw.GetProperty("rawSse").GetString()!));
        }
    }

    public static async Task<int> ClarifiedRetryUntilAcceptedAsync(string repo, string[] args)
    {
        Request[] allRequests;
        try { allRequests = Sources.SelectMany(source => Build(repo, source)).ToArray(); }
        catch (Exception exception) { return Fail($"p6th2c clarified retry: request reconstruction failed before network: {exception.Message}"); }
        var targetKeys = new[] { "SRC-041|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_060|O5", "DOC-0256|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001|O1" };
        var requestByKey = allRequests.ToDictionary(value => $"{value.Source.DocumentId}|{value.PackId}|{value.Anchor}", StringComparer.Ordinal);
        if (allRequests.Length != 31 || targetKeys.Any(key => !requestByKey.ContainsKey(key))) return Fail("p6th2c clarified retry: frozen request population/targets invalid; no network");
        var manifestPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        var oldManifestPath = Path.Combine(repo, OriginalPreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var oldManifest = JsonDocument.Parse(File.ReadAllText(oldManifestPath));
        if (!ManifestParity(manifest.RootElement, allRequests)) return Fail("p6th2c clarified retry: v2 manifest parity failed; no network");

        var primaryDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var previousRetryDir = Path.Combine(repo, RetryCaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var clarifiedDir = Path.Combine(repo, ClarifiedRetryCaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        var summaryPath = Path.Combine(clarifiedDir, "retry-until-accepted-summary.v1.json");
        if (File.Exists(summaryPath))
        {
            Console.WriteLine("P6T-H2C clarified retries already have a frozen success summary; no calls resent.");
            return 0;
        }
        if (!args.Contains($"--confirm-p6th2c-clarified-retry={ClarifiedRetryConfirm}"))
        {
            Console.WriteLine("P6T-H2C CLARIFIED RETRY PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            Console.WriteLine("Only SRC-041/O5 and DOC-0256/O1; prompt removes fixed O17 example and requires exact input-anchor echo.");
            return 0;
        }
        if (Directory.Exists(clarifiedDir)) return Fail("p6th2c clarified retry: incomplete immutable retry directory exists; no resend");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th2c clarified retry: OPENROUTER_API_KEY missing; no network");

        var priorPrimaryResultPath = Path.Combine(primaryDir, "result.v1.json");
        var priorRetryResultPath = Path.Combine(previousRetryDir, "result.v1.json");
        using var priorPrimaryResult = JsonDocument.Parse(File.ReadAllText(priorPrimaryResultPath));
        using var priorRetryResult = JsonDocument.Parse(File.ReadAllText(priorRetryResultPath));
        var priorFailed = priorPrimaryResult.RootElement.GetProperty("rows").EnumerateArray()
            .Where(row => !row.GetProperty("ledgerAccepted").GetBoolean())
            .Select(row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}|{row.GetProperty("anchor").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        if (!priorFailed.SetEquals(targetKeys) || priorPrimaryResult.RootElement.GetProperty("providerCalls").GetInt32() != 31 ||
            priorRetryResult.RootElement.GetProperty("providerCalls").GetInt32() != 2 || priorPrimaryResult.RootElement.GetProperty("goldRead").GetBoolean())
            return Fail("p6th2c clarified retry: prior quarantine history differs from authorized targets; no network");

        var priorRawByKey = targetKeys.ToDictionary(key => key, _ => new List<(string Path, string RawSha)>(), StringComparer.Ordinal);
        var oldFrozenRows = oldManifest.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}", StringComparer.Ordinal);
        foreach (var key in targetKeys)
        {
            if (!oldFrozenRows.TryGetValue(key, out var oldRequest) || !requestByKey.TryGetValue(key, out var newRequest) ||
                !oldRequest.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()).SequenceEqual(newRequest.IssuedOccurrences))
                return Fail($"p6th2c clarified retry: v1/v2 occurrence universe differs: {key}");
            var pieces = key.Split('|');
            var primaryPath = Path.Combine(primaryDir, $"{pieces[0]}_{pieces[2]}.raw-capture.v1.json");
            if (!ValidateHistoricalRaw(primaryPath, oldRequest, newRequest)) return Fail($"p6th2c clarified retry: primary quarantine hash/authority mismatch: {key}");
            var retryPaths = Directory.GetFiles(previousRetryDir, $"{pieces[0]}_{pieces[2]}*.raw-capture.v1.json", SearchOption.AllDirectories)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            foreach (var retryPath in retryPaths)
            {
                if (!ValidateHistoricalRaw(retryPath, oldRequest, newRequest)) return Fail($"p6th2c clarified retry: historical retry hash/authority mismatch: {key}");
                priorRawByKey[key].Add((retryPath, Hash(File.ReadAllBytes(retryPath))));
            }
            if (priorRawByKey[key].Count == 0 || priorRawByKey[key].Any(value =>
                {
                    using var raw = JsonDocument.Parse(File.ReadAllBytes(value.Path));
                    return TryParse(raw.RootElement.GetProperty("rawResponse").GetString()!, newRequest.IssuedOccurrences, newRequest.Anchor) is not null;
                })) return Fail($"p6th2c clarified retry: historical result already valid or missing for {key}");
        }
        var totalPriorRetryCalls = priorRawByKey.Values.Sum(value => value.Count);
        Directory.CreateDirectory(clarifiedDir);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var accepted = new Dictionary<string, (int Attempt, string RawSha, string ResponseSha, string SseSha)>(StringComparer.Ordinal);
        var attempts = targetKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var round = 1;
        var additionalCalls = 0;
        while (accepted.Count < targetKeys.Length)
        {
            var pending = targetKeys.Where(key => !accepted.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var roundDir = Path.Combine(clarifiedDir, $"attempt-{round:D3}");
            if (Directory.Exists(roundDir)) return Fail($"p6th2c clarified retry: incomplete round already exists; no resend: {roundDir}");
            Directory.CreateDirectory(roundDir);
            WriteNew(Path.Combine(roundDir, "execution-reservation.v1.json"), new
            {
                schemaVersion = "v5-p6th2c-clarified-retry-round-reservation-v1",
                status = "EXACT_CLARIFIED_REQUEST_RETRIES_RESERVED",
                promptVersion = "v5-function-conditioned-exact-end-pointer-2",
                removedFailureSource = "FIXED_O17_EXAMPLE_HANDLE",
                round,
                requests = pending.Select((key, index) => new
                {
                    retryOrdinal = index + 1,
                    documentId = requestByKey[key].Source.DocumentId,
                    requestByKey[key].PackId,
                    requestByKey[key].Anchor,
                    providerBodySha256 = requestByKey[key].BodyHash,
                    previousPrimaryRawSha256 = Hash(File.ReadAllBytes(Path.Combine(primaryDir, $"{requestByKey[key].Source.DocumentId}_{requestByKey[key].Anchor}.raw-capture.v1.json"))),
                    previousInvalidRetryCount = priorRawByKey[key].Count,
                }).ToArray(),
                providerCallsBeforeSend = 0,
                transportRetries = 0,
                repair = false,
                fallback = false,
                goldRead = false,
            });

            var rows = new List<object>();
            foreach (var key in pending)
            {
                var request = requestByKey[key];
                OpenRouterExecutionObservation? observation = null;
                string? transportError = null;
                var watch = Stopwatch.StartNew();
                try
                {
                    using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                    observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens, SystemPrompt, request.UserMessage).ConfigureAwait(false);
                }
                catch (Exception exception) { transportError = exception.Message; }
                watch.Stop();
                additionalCalls++;
                attempts[key]++;
                var rawPath = Path.Combine(roundDir, $"{request.Source.DocumentId}_{request.Anchor}.attempt{round}.raw-capture.v2.json");
                WriteNew(rawPath, new
                {
                    schemaVersion = "v5-p6th2c-exact-end-pointer-clarified-retry-raw-capture-v2",
                    documentId = request.Source.DocumentId,
                    packId = request.PackId,
                    anchor = request.Anchor,
                    anchorAlias = request.AnchorAlias,
                    attempt = round,
                    priorOriginalAndInvalidRetries = 1 + priorRawByKey[key].Count,
                    providerCallOrdinal = additionalCalls,
                    providerCalls = 1,
                    providerBodySha256 = request.BodyHash,
                    providerBodyBytes = request.BodyBytes,
                    userMessageSha256 = Hash(request.UserMessage),
                    systemPromptSha256 = Hash(SystemPrompt),
                    issuedOccurrenceCount = request.IssuedOccurrences.Count,
                    reasoningRequested = true,
                    reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                    reasoningExecutionConfirmed = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens") is > 0,
                    promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                    completionTokens = Usage(observation?.Usage, "completion_tokens"),
                    finishReason = observation?.FinishReason,
                    transportRetryCount = observation?.RetryCount ?? 0,
                    latencyMs = watch.Elapsed.TotalMilliseconds,
                    rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                    rawSse = observation?.RawSse,
                    rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                    rawResponse = observation?.Content,
                    transportError,
                    goldReadDuringCapture = false,
                });
                var parsed = observation is not null && transportError is null && observation.FinishReason == "stop"
                    ? TryParse(observation.Content, request.IssuedOccurrences, request.Anchor) : null;
                if (parsed is not null)
                    accepted.Add(key, (round, Hash(File.ReadAllBytes(rawPath)), Hash(observation!.Content), Hash(observation.RawSse)));
                rows.Add(new
                {
                    documentId = request.Source.DocumentId,
                    packId = request.PackId,
                    anchor = request.Anchor,
                    attempt = round,
                    providerBodySha256 = request.BodyHash,
                    finishReason = observation?.FinishReason,
                    transportRetryCount = observation?.RetryCount ?? 0,
                    ledgerAccepted = parsed is not null,
                    returnedHeadingMembers = parsed?.HeadingMembers.Count,
                    rawCaptureFileSha256 = Hash(File.ReadAllBytes(rawPath)),
                    rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                    rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                    transportError,
                });
                Console.WriteLine($"[clarified retry {round}] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}");
            }
            WriteNew(Path.Combine(roundDir, "result.v1.json"), new
            {
                schemaVersion = "v5-p6th2c-clarified-retry-round-result-v1",
                status = rows.All(value => (bool)value.GetType().GetProperty("ledgerAccepted")!.GetValue(value)!) ? "ROUND_LEDGERS_ACCEPTED" : "ROUND_HAS_QUARANTINE",
                round,
                providerCalls = rows.Count,
                acceptedLedgers = rows.Count(value => (bool)value.GetType().GetProperty("ledgerAccepted")!.GetValue(value)!),
                transportRetries = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                rows,
            });
            round++;
        }

        WriteNew(summaryPath, new
        {
            schemaVersion = "v5-p6th2c-clarified-retry-until-accepted-summary-v1",
            status = "BOTH_QUARANTINED_ANCHORS_HAVE_CONTRACT_VALID_CLARIFIED_RESULTS",
            clarification = "REMOVED_FIXED_O17_OUTPUT_EXAMPLE_AND_REQUIRED_EXACT_CURRENT_INPUT_ANCHOR_ECHO",
            originalPrimaryProviderCalls = 31,
            priorExactBodyRetries = totalPriorRetryCalls,
            clarifiedRetryProviderCalls = additionalCalls,
            totalProviderCallsIncludingRetries = 31 + totalPriorRetryCalls + additionalCalls,
            acceptedAnchorCount = accepted.Count,
            transportRetries = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            priorInvalidExactBodyRetriesByAnchor = priorRawByKey.ToDictionary(value => value.Key, value => value.Value.Count, StringComparer.Ordinal),
            clarifiedRetryAttemptsByAnchor = attempts.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            accepted = accepted.OrderBy(value => value.Key, StringComparer.Ordinal).Select(pair => new
            {
                key = pair.Key,
                documentId = requestByKey[pair.Key].Source.DocumentId,
                packId = requestByKey[pair.Key].PackId,
                anchor = requestByKey[pair.Key].Anchor,
                providerBodySha256 = requestByKey[pair.Key].BodyHash,
                clarifiedRetryAttemptAccepted = pair.Value.Attempt,
                acceptedRawCaptureSha256 = pair.Value.RawSha,
                acceptedRawResponseSha256 = pair.Value.ResponseSha,
                acceptedRawSseSha256 = pair.Value.SseSha,
            }).ToArray(),
        });
        Console.WriteLine($"P6T-H2C clarified retry complete: both anchors accepted; clarified calls={additionalCalls}; GoldRead=false.");
        return 0;

        bool ValidateHistoricalRaw(string rawPath, JsonElement oldRequest, Request newRequest)
        {
            if (!File.Exists(rawPath)) return false;
            try
            {
                using var raw = JsonDocument.Parse(File.ReadAllBytes(rawPath));
                var row = raw.RootElement;
                var response = row.GetProperty("rawResponse").GetString()!;
                var sse = row.GetProperty("rawSse").GetString()!;
                return Hash(response) == row.GetProperty("rawResponseSha256").GetString() && Hash(sse) == row.GetProperty("rawSseSha256").GetString() &&
                    row.GetProperty("providerBodySha256").GetString() == oldRequest.GetProperty("ProviderBodySha256").GetString() &&
                    oldRequest.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()).SequenceEqual(newRequest.IssuedOccurrences) &&
                    TryParse(response, newRequest.IssuedOccurrences, newRequest.Anchor) is null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
        }
    }

    private static void VerifyReplayMaterial(JsonElement raw, Request request, int? expectedOrdinal)
    {
        var response = raw.GetProperty("rawResponse").GetString()!;
        var sse = raw.GetProperty("rawSse").GetString()!;
        var transportRetries = raw.TryGetProperty("retryCount", out var legacyRetryCount)
            ? legacyRetryCount.GetInt32()
            : raw.GetProperty("transportRetryCount").GetInt32();
        if (Hash(response) != raw.GetProperty("rawResponseSha256").GetString() || Hash(sse) != raw.GetProperty("rawSseSha256").GetString() ||
            raw.GetProperty("providerBodySha256").GetString() != request.BodyHash || transportRetries != 0 ||
            (expectedOrdinal is not null && raw.GetProperty("authorizedReplayNumber").GetInt32() != expectedOrdinal.Value))
            throw new InvalidDataException("retry replay evidence hash/body mismatch");
    }

    private static string LatestRawSha(string retryRoot, Request request, int attempt)
    {
        var path = attempt == 1
            ? Path.Combine(retryRoot, $"{request.Source.DocumentId}_{request.Anchor}.retry1.raw-capture.v1.json")
            : Path.Combine(retryRoot, $"attempt-{attempt:D3}", $"{request.Source.DocumentId}_{request.Anchor}.retry{attempt}.raw-capture.v1.json");
        return Hash(File.ReadAllBytes(path));
    }

    private static IEnumerable<Request> Build(string repo, Source source) =>
        Build(repo, source, SystemPrompt, "v5-function-conditioned-exact-end-pointer-2");

    private static IEnumerable<Request> Build(string repo, Source source, string systemPrompt, string protocolVersion)
    {
        string PathOf(string relative) => Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        var sourceSha = CanonicalSemanticSourceHash.Compute(PathOf(source.PdfPath));
        var snapshotPath = PathOf($"{SnapshotRoot}/{sourceSha}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, source.DocumentId);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        if (plan.SourceSha256 != sourceSha || snapshot.RootElement.GetProperty("SourceSha256").GetString() != sourceSha)
            throw new InvalidDataException("source snapshot hash mismatch");
        var evidence = snapshot.RootElement.GetProperty("Evidence").EnumerateArray()
            .ToDictionary(item => item.GetProperty("SourceAlias").GetString()!, item => item.Clone(), StringComparer.Ordinal);
        var f1Path = PathOf($"{Root}/{source.F1Path}");
        var f1Bytes = File.ReadAllBytes(f1Path);
        using var f1Capture = JsonDocument.Parse(f1Bytes);
        var f1Row = source.Kind switch
        {
            F1Kind.RawCapture => f1Capture.RootElement,
            F1Kind.ResultRow => f1Capture.RootElement.GetProperty("row"),
            F1Kind.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("unknown F1 capture shape"),
        };
        var packId = f1Row.GetProperty("packId").GetString()!;
        var pack = plan.Packs.Single(value => value.PackId == packId);
        var correspondence = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondence);
        var f1RequestHash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash)
            ? semanticHash.GetString() : f1Row.GetProperty("userMessageSha256").GetString();
        if (f1.Request.UserMessageSha256 != f1RequestHash || f1.Request.Occurrences.Count != 96 || f1Row.GetProperty("finishReason").GetString() != "stop")
            throw new InvalidDataException("frozen F1 request/response authority mismatch");
        var f1Response = f1Row.GetProperty("rawResponse").GetString()!;
        var f1ResponseHash = Hash(f1Response);
        var f1ExpectedResponseHash = ReadStringOrAlternative(f1Row, "rawResponseSha256", "acceptedRawResponseSha256");
        if (f1ResponseHash != f1ExpectedResponseHash) throw new InvalidDataException("F1 raw response hash mismatch");
        var functionLedger = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response);
        if (functionLedger.Decisions.Count != 96) throw new InvalidDataException("F1 total ledger invalid");
        var functionById = functionLedger.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function, StringComparer.Ordinal);

        var g2aPath = PathOf($"{G2ACaptureRoot}/{source.DocumentId}.raw-capture.v1.json");
        var g2aBytes = File.ReadAllBytes(g2aPath);
        using var g2a = JsonDocument.Parse(g2aBytes);
        var g2aRoot = g2a.RootElement;
        if (g2aRoot.GetProperty("documentId").GetString() != source.DocumentId || g2aRoot.GetProperty("packId").GetString() != packId ||
            g2aRoot.GetProperty("sourceSha256").GetString() != plan.SourceSha256 || g2aRoot.GetProperty("sourceUniverseSha256").GetString() != plan.SourceUniverseSha256 ||
            g2aRoot.GetProperty("finishReason").GetString() != "stop" || g2aRoot.GetProperty("retryCount").GetInt32() != 0)
            throw new InvalidDataException("frozen G2A capture metadata mismatch");
        var g2aRaw = g2aRoot.GetProperty("rawResponse").GetString()!;
        if (Hash(g2aRaw) != g2aRoot.GetProperty("rawResponseSha256").GetString() || Hash(g2aRoot.GetProperty("rawSse").GetString()!) != g2aRoot.GetProperty("rawSseSha256").GetString())
            throw new InvalidDataException("G2A raw response/SSE hash mismatch");
        var g2aRows = ParseG2A(g2aRaw);
        var f1ById = f1.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
        if (g2aRows.Count == 0 || g2aRows.Any(row => !f1ById.ContainsKey(row.Key)) ||
            g2aRows.Keys.Any(id => !functionById.ContainsKey(id))) throw new InvalidDataException("G2A ledger does not match F1 issued occurrences");

        var owned = pack.OwnedAliases;
        var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var atomByAlias = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var has = g2aRows.Where(value => value.Value == "HAS_STRUCTURAL_EXTENT")
            .OrderBy(value => int.Parse(value.Key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (anchorId, _) in has)
        {
            if (functionById[anchorId] != V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE) throw new InvalidDataException("G2A HAS lacks F1 ESTABLISHES authority");
            var anchorAlias = f1ById[anchorId];
            var start = Array.IndexOf(owned.ToArray(), anchorAlias);
            if (start < 0 || start + 1 >= owned.Count) throw new InvalidDataException("H2C anchor has no visible successor");
            var tail = owned.Skip(start).ToArray();
            var occurrenceRows = tail.Select(alias =>
            {
                var item = evidence[alias];
                var style = item.GetProperty("StyleFacts");
                JsonElement? location = item.TryGetProperty("LocationFacts", out var locationValue) && locationValue.ValueKind != JsonValueKind.Null ? locationValue : null;
                return new
                {
                    occurrence = idByAlias[alias],
                    page = atomByAlias[alias].Page,
                    text = atomByAlias[alias].Text,
                    style = new
                    {
                        fontSize = style.GetProperty("fontSize"),
                        bodyFontSize = style.GetProperty("bodyFontSize"),
                        fontSizeToBodyRatio = style.GetProperty("fontSizeToBodyRatio"),
                        boldRatio = style.GetProperty("boldRatio"),
                        italicRatio = style.GetProperty("italicRatio"),
                        lineCount = style.GetProperty("lineCount"),
                    },
                    location = new
                    {
                        verticalPosition = location?.GetProperty("verticalPosition") ?? default,
                        sameNormalizedTextPageCount = location?.GetProperty("sameNormalizedTextPageCount") ?? default,
                        sameNormalizedTextFirstPage = location?.GetProperty("sameNormalizedTextFirstPage") ?? default,
                        sameNormalizedTextLastPage = location?.GetProperty("sameNormalizedTextLastPage") ?? default,
                    },
                };
            }).ToArray();
            var user = JsonSerializer.Serialize(new
            {
                protocolVersion,
                anchors = new[] { new { anchor = anchorId, occurrences = occurrenceRows } },
            });
            var request = new V5FreeHeadingRequestV1(protocolVersion, systemPrompt, user,
                Hash(user), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(user));
            var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
            yield return new Request(source, packId, anchorId, anchorAlias, plan.SourceSha256, plan.SourceUniverseSha256,
                Hash(f1Bytes), f1ResponseHash, Hash(g2aBytes), g2aRoot.GetProperty("rawResponseSha256").GetString()!,
                body.PayloadBytes, body.Hash, body.Bytes, user, Encoding.UTF8.GetByteCount(user), pack.MaxCompletionTokens,
                occurrenceRows.Select(value => value.occurrence).ToArray());
        }
    }

    private static bool ManifestParity(JsonElement manifest, IReadOnlyList<Request> requests)
    {
        if (manifest.GetProperty("schemaVersion").GetString() != "v5-p6th2c-exact-end-pointer-preflight-v2" ||
            manifest.GetProperty("status").GetString() != "FULL_G2A_HAS_REQUESTS_FROZEN_NOT_AUTHORIZED_NO_GOLD_READ_PROMPT_EXAMPLE_HANDLES_REMOVED" ||
            manifest.GetProperty("execution").GetProperty("providerCalls").GetInt32() != 0 ||
            manifest.GetProperty("execution").GetProperty("goldRead").GetBoolean()) return false;
        var frozen = manifest.GetProperty("requestUniverse").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        if (frozen.Count != requests.Count) return false;
        return requests.All(request => frozen.TryGetValue($"{request.Source.DocumentId}|{request.PackId}|{request.Anchor}", out var row) &&
            row.GetProperty("SourceSha256").GetString() == request.SourceSha256 &&
            row.GetProperty("SourceUniverseSha256").GetString() == request.SourceUniverseSha256 &&
            row.GetProperty("F1CaptureSha256").GetString() == request.F1CaptureSha256 &&
            row.GetProperty("F1ResponseSha256").GetString() == request.F1ResponseSha256 &&
            row.GetProperty("G2ARawCaptureSha256").GetString() == request.G2ARawCaptureSha256 &&
            row.GetProperty("G2AResponseSha256").GetString() == request.G2AResponseSha256 &&
            row.GetProperty("UserMessageSha256").GetString() == Hash(request.UserMessage) &&
            row.GetProperty("UserBytes").GetInt32() == request.UserBytes &&
            row.GetProperty("ProviderBodySha256").GetString() == request.BodyHash &&
            row.GetProperty("ProviderBodyBytes").GetInt32() == request.BodyBytes &&
            row.GetProperty("MaxCompletionTokens").GetInt32() == request.MaxCompletionTokens &&
            row.GetProperty("IssuedOccurrenceCount").GetInt32() == request.IssuedOccurrences.Count &&
            row.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()).SequenceEqual(request.IssuedOccurrences));

        static string Key(JsonElement row) => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}";
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var list = result.TryGetValue(key, out var existing) ? existing.ToList() : [];
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }

    private static Dictionary<string, string> ParseG2A(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("G2A ledger root invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2) throw new InvalidDataException("G2A ledger row invalid");
            var primary = row.GetProperty("primary").GetString()!;
            var anchor = row.GetProperty("anchor").GetString()!;
            if (anchor is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT") || !result.TryAdd(primary, anchor)) throw new InvalidDataException("G2A ledger value invalid");
        }
        return result;
    }

    private static Parsed? TryParse(string raw, IReadOnlyList<string> issuedOccurrences, string anchor)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(raw) > ResponseByteCap) return null;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) return null;
            var decision = decisions[0];
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 5 ||
                !decision.TryGetProperty("anchor", out var anchorValue) || anchorValue.ValueKind != JsonValueKind.String || anchorValue.GetString() != anchor ||
                !decision.TryGetProperty("headingMembers", out var membersValue) || membersValue.ValueKind != JsonValueKind.Array ||
                !decision.TryGetProperty("endOccurrence", out var endValue) || endValue.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("firstOutsideOccurrence", out var outsideValue) ||
                !decision.TryGetProperty("firstOutsideRole", out var roleValue) || roleValue.ValueKind != JsonValueKind.String) return null;
            var members = membersValue.EnumerateArray().ToArray();
            if (members.Length == 0 || members.Length > issuedOccurrences.Count || members.Any(value => value.ValueKind != JsonValueKind.String)) return null;
            var ids = members.Select(value => value.GetString()!).ToArray();
            if (!ids.SequenceEqual(issuedOccurrences.Take(ids.Length), StringComparer.Ordinal) || ids[0] != anchor || endValue.GetString() != ids[^1]) return null;
            var role = roleValue.GetString()!;
            if (ids.Length == issuedOccurrences.Count)
            {
                if (outsideValue.ValueKind != JsonValueKind.Null || role != "NO_VISIBLE_SUCCESSOR") return null;
                return new Parsed(anchor, ids, ids[^1], null, role);
            }
            if (outsideValue.ValueKind != JsonValueKind.String || outsideValue.GetString() != issuedOccurrences[ids.Length] ||
                role is not ("NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING")) return null;
            return new Parsed(anchor, ids, ids[^1], outsideValue.GetString(), role);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static string ReadStringOrAlternative(JsonElement element, string first, string second) =>
        element.TryGetProperty(first, out var value) ? value.GetString()! : element.GetProperty(second).GetString()!;
    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var key in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }
    private static int? Usage(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void WriteNew(string path, object value)
    {
        if (File.Exists(path)) throw new InvalidOperationException("p6th2c immutable artifact already exists");
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
