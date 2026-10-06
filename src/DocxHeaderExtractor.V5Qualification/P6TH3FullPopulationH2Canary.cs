using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>One-shot H2 population execution over every frozen full-pack G2A HAS anchor.</summary>
internal static class P6TH3FullPopulationH2Canary
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string G2ACaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string G2APreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string H2PreflightPath = Root + "/p6th3-full-population-h2-preflight/g2a-raw-audit-and-h2-full-request-manifest.v1.json";
    private const string CaptureRoot = Root + "/p6th3-full-population-h2-canary-20261005";
    private const string H2B1PreflightPath = Root + "/p6th3-h2b1-horizon-only-preflight/h2-horizon-only-k4-paired-preflight.v1.json";
    private const string H2B1CaptureRoot = Root + "/p6th3-h2b1-horizon-only-capture-20261005";
    private const string Confirm = "yes-i-authorize-p6th3-full-population-h2-thirty-one-primary-calls";
    private const string H2B1Confirm = "yes-i-authorize-p6th-h2b1-k4-thirty-one-primary-calls";
    private const int BoundedEdgeHorizonK = 4;
    private const int ResponseByteCap = 49_152;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string SystemPrompt = """
        Judge only source-order continuation boundaries for an already-qualified structural anchor. For each issued edge, decide whether right continues the same local structural unit begun at anchor, or whether the unit stops before right.

        CONTINUES_STRUCTURAL_UNIT means left and right belong to the same exact local structural unit. STOPS_STRUCTURAL_UNIT means the unit begun at anchor ends before right. The edges are consecutive source occurrences; do not use similarity, hierarchy, candidates, spans, locators, or hypothetical text not issued in the request. Once an anchor stops, every later issued edge for that anchor must also be STOPS_STRUCTURAL_UNIT.

        Return exactly one JSON object: {"decisions":[{"anchor":"O4","left":"O4","right":"O5","boundary":"CONTINUES_STRUCTURAL_UNIT"}]}. Return exactly one decision for every issued edge. Echo only issued O# values. Do not output candidate IDs, source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
        """;

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Kind.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
    ];

    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Kind Kind, bool F1UsedCorrespondences);
    private enum F1Kind { RawCapture, ResultRow, ResultRows }
    private sealed record Edge(string Anchor, string Left, string Right, int Ordinal, string LeftAlias, string RightAlias);
    private sealed record Request(Source Source, string PackId, string Anchor, string AnchorAlias, string SourceSha256,
        string SourceUniverseSha256, string G2ARawSha256, byte[] Body, string BodyHash, int BodyBytes,
        string SystemPrompt, string UserMessage, int MaxCompletionTokens, IReadOnlyList<Edge> Edges);
    private sealed record Decision(string Anchor, string Left, string Right, string Boundary);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var requests = Sources.SelectMany(source => Build(repo, source)).ToArray();
        if (requests.Length != 31) return Fail($"p6th3-h2-full: frozen request count mismatch:{requests.Length}; no network");
        var manifestPath = Path.Combine(repo, H2PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!ManifestParity(manifest.RootElement, requests)) return Fail("p6th3-h2-full: request manifest parity failed; no network");
        if (!args.Contains($"--confirm-p6th3-h2-full={Confirm}"))
        {
            Console.WriteLine("P6T-H2 full population PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            Console.WriteLine($"Frozen requests={requests.Length}; edges={requests.Sum(value => value.Edges.Count)}; max body={requests.Max(value => value.BodyBytes)} B.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th3-h2-full: OPENROUTER_API_KEY missing; no network");
        var captureDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(captureDir)) return Fail("p6th3-h2-full: immutable capture exists; no rerun");

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
            schemaVersion = "v5-p6th3-full-population-h2-reservation-v1",
            status = "THIRTY_ONE_PRIMARY_CALLS_RESERVED",
            h2PreflightSha256 = Hash(File.ReadAllText(manifestPath)),
            requestCount = requests.Length,
            totalIssuedEdges = requests.Sum(value => value.Edges.Count),
            requestOrder = requests.Select(value => new { value.Source.DocumentId, value.PackId, value.Anchor, value.BodyHash }).ToArray(),
            providerCallsBeforeSend = 0,
            maximumPrimaryCalls = 31,
            retriesAllowed = 0,
            repairAllowed = false,
            fallbackAllowed = false,
            goldRead = false,
        });

        var results = new List<object>();
        var accepted = 0;
        var totalCalls = 0;
        foreach (var request in requests)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            totalCalls++;

            var rawPath = Path.Combine(captureDir, $"{request.Source.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th3-full-population-h2-raw-capture-v1",
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                sourceSha256 = request.SourceSha256,
                sourceUniverseSha256 = request.SourceUniverseSha256,
                g2aRawCaptureSha256 = request.G2ARawSha256,
                providerCallOrdinal = totalCalls,
                providerCalls = 1,
                providerBodySha256 = request.BodyHash,
                providerBodyBytes = request.BodyBytes,
                userMessageSha256 = Hash(request.UserMessage),
                systemPromptSha256 = Hash(request.SystemPrompt),
                issuedEdgeCount = request.Edges.Count,
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

            // Preserve immutable provider output before any parse or contract assessment.
            var parsed = observation is not null && transportError is null && observation.FinishReason == "stop"
                ? Parse(request, observation.Content)
                : null;
            if (parsed is not null) accepted++;
            results.Add(new
            {
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                providerCallOrdinal = totalCalls,
                providerBodySha256 = request.BodyHash,
                issued = request.Edges.Count,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                ledgerAccepted = parsed is not null,
                returned = parsed?.Count,
                continues = parsed?.Count(value => value.Boundary == "CONTINUES_STRUCTURAL_UNIT"),
                stops = parsed?.Count(value => value.Boundary == "STOPS_STRUCTURAL_UNIT"),
                monotonic = parsed is null ? (bool?)null : IsMonotonic(parsed),
                transportError,
            });
            Console.WriteLine($"[{totalCalls}/{requests.Length}] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}, edges={request.Edges.Count}");
        }

        WriteNew(Path.Combine(captureDir, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th3-full-population-h2-result-v1",
            status = accepted == requests.Length ? "ALL_FROZEN_H2_LEDGERS_ACCEPTED" : "ONE_OR_MORE_H2_LEDGERS_NOT_ACCEPTED",
            providerCalls = totalCalls,
            acceptedLedgers = accepted,
            maximumPrimaryCalls = 31,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            rows = results,
        });
        return 0;
    }

    /// <summary>Executes only the frozen K=4 BOUND arm; immutable FULL captures are reused as controls.</summary>
    public static async Task<int> RunH2B1Async(string repo, string[] args)
    {
        var requests = Sources.SelectMany(source => Build(repo, source)).ToArray();
        var bounded = requests.Select(request => BuildBounded(request)).ToArray();
        if (requests.Length != 31 || bounded.Length != 31) return Fail($"p6th-h2b1: expected exactly 31 paired requests, got {requests.Length}/{bounded.Length}; no network");

        var preflightPath = Path.Combine(repo, H2B1PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        var fullManifestPath = Path.Combine(repo, H2PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var preflight = JsonDocument.Parse(File.ReadAllText(preflightPath));
        using var fullManifest = JsonDocument.Parse(File.ReadAllText(fullManifestPath));
        if (!ManifestParity(fullManifest.RootElement, requests) || !H2B1ManifestParity(preflight.RootElement, requests, bounded))
            return Fail("p6th-h2b1: frozen full/bounded request parity failed; no network");

        var captureDir = Path.Combine(repo, H2B1CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(captureDir)) return Fail("p6th-h2b1: immutable capture directory exists; no resend");
        if (!args.Contains($"--confirm-p6th-h2b1={H2B1Confirm}"))
        {
            Console.WriteLine("P6T-H2B1 PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            Console.WriteLine($"Frozen BOUND calls={bounded.Length}; K={BoundedEdgeHorizonK}; max body={bounded.Max(value => value.BodyBytes)} B.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th-h2b1: OPENROUTER_API_KEY missing; no network");

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
            schemaVersion = "v5-p6th-h2b1-k4-execution-reservation-v1",
            status = "THIRTY_ONE_BOUND_PRIMARY_CALLS_RESERVED",
            preflightSha256 = Hash(File.ReadAllText(preflightPath)),
            fullManifestSha256 = Hash(File.ReadAllText(fullManifestPath)),
            requestCount = bounded.Length,
            horizonK = BoundedEdgeHorizonK,
            fullControlCallsReused = requests.Length,
            fullControlCallsToRerun = 0,
            requestOrder = bounded.Select(value => new { value.Source.DocumentId, value.PackId, value.Anchor, providerBodySha256 = value.BodyHash, issuedEdges = value.Edges.Count }).ToArray(),
            providerCallsBeforeSend = 0,
            maximumPrimaryCalls = 31,
            retriesAllowed = 0,
            repairAllowed = false,
            fallbackAllowed = false,
            goldRead = false,
        });

        var summaries = new List<object>();
        var accepted = 0;
        var calls = 0;
        foreach (var request in bounded)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            calls++;
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.Body, request.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            var rawPath = Path.Combine(captureDir, $"{request.Source.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th-h2b1-k4-raw-capture-v1",
                documentId = request.Source.DocumentId,
                packId = request.PackId,
                anchor = request.Anchor,
                anchorAlias = request.AnchorAlias,
                sourceSha256 = request.SourceSha256,
                sourceUniverseSha256 = request.SourceUniverseSha256,
                g2aRawCaptureSha256 = request.G2ARawSha256,
                providerCallOrdinal = calls,
                providerCalls = 1,
                arm = "BOUND_K4",
                horizonK = BoundedEdgeHorizonK,
                providerBodySha256 = request.BodyHash,
                providerBodyBytes = request.BodyBytes,
                userMessageSha256 = Hash(request.UserMessage),
                systemPromptSha256 = Hash(request.SystemPrompt),
                issuedEdgeCount = request.Edges.Count,
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

            // Raw output is persisted before parsing or classifying it.
            var parsed = observation is not null && transportError is null &&
                         string.Equals(observation.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)
                ? Parse(request, observation.Content)
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
                issued = request.Edges.Count,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                ledgerAccepted = parsed is not null,
                returned = parsed?.Count,
                continues = parsed?.Count(value => value.Boundary == "CONTINUES_STRUCTURAL_UNIT"),
                stops = parsed?.Count(value => value.Boundary == "STOPS_STRUCTURAL_UNIT"),
                monotonic = parsed is null ? (bool?)null : IsMonotonic(parsed),
                rawCaptureSha256 = Hash(File.ReadAllBytes(rawPath)),
                transportError,
            });
            Console.WriteLine($"[{calls}/{bounded.Length}] {request.Source.DocumentId} {request.Anchor}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}, edges={request.Edges.Count}");
        }

        WriteNew(Path.Combine(captureDir, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th-h2b1-k4-result-v1",
            status = accepted == bounded.Length ? "ALL_FROZEN_H2B1_LEDGERS_ACCEPTED" : "ONE_OR_MORE_H2B1_LEDGERS_NOT_ACCEPTED",
            providerCalls = calls,
            acceptedLedgers = accepted,
            maximumPrimaryCalls = 31,
            horizonK = BoundedEdgeHorizonK,
            retries = 0,
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

    private static Request BuildBounded(Request full)
    {
        var edges = full.Edges.Take(BoundedEdgeHorizonK).ToArray();
        using var parsed = JsonDocument.Parse(full.UserMessage);
        var anchorRoot = parsed.RootElement.GetProperty("anchors")[0];
        var occurrences = anchorRoot.GetProperty("occurrences").EnumerateArray().Select(row => new
        {
            occurrence = row.GetProperty("occurrence").GetString(),
            page = row.GetProperty("page").GetInt32(),
            text = row.GetProperty("text").GetString(),
            selectable = row.GetProperty("selectable").GetBoolean(),
        }).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = parsed.RootElement.GetProperty("protocolVersion").GetString(),
            anchors = new[] { new { anchor = full.Anchor, occurrences, edges = edges.Select(edge => new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, ordinal = edge.Ordinal }).ToArray() } },
        });
        var headingRequest = new V5FreeHeadingRequestV1("v5-function-conditioned-continuation-boundary-1", full.SystemPrompt, user,
            Hash(user), Encoding.UTF8.GetByteCount(full.SystemPrompt), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(headingRequest, full.MaxCompletionTokens);
        return full with { UserMessage = user, Body = body.PayloadBytes, BodyHash = body.Hash, BodyBytes = body.Bytes, Edges = edges };
    }

    private static bool H2B1ManifestParity(JsonElement manifest, IReadOnlyList<Request> full, IReadOnlyList<Request> bounded)
    {
        if (manifest.GetProperty("schemaVersion").GetString() != "v5-p6t-h2b1-horizon-only-k4-paired-preflight-v1" ||
            manifest.GetProperty("status").GetString() != "PAIRWISE_REQUESTS_FROZEN_NOT_AUTHORIZED_NO_GOLD_READ" ||
            manifest.GetProperty("preregistration").GetProperty("K").GetInt32() != BoundedEdgeHorizonK ||
            manifest.GetProperty("authorization").GetProperty("providerCallsAuthorized").GetInt32() != 0) return false;
        var rows = manifest.GetProperty("arms").GetProperty("rows").EnumerateArray()
            .ToDictionary(row => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}", StringComparer.Ordinal);
        if (rows.Count != 31) return false;
        foreach (var item in full)
        {
            var key = $"{item.Source.DocumentId}|{item.PackId}|{item.Anchor}";
            if (!rows.TryGetValue(key, out var row)) return false;
            var boundedItem = bounded.Single(value => value.Source.DocumentId == item.Source.DocumentId && value.PackId == item.PackId && value.Anchor == item.Anchor);
            if (row.GetProperty("fullProviderBodySha256").GetString() != item.BodyHash ||
                row.GetProperty("boundedProviderBodySha256").GetString() != boundedItem.BodyHash ||
                row.GetProperty("fullUserMessageSha256").GetString() != Hash(item.UserMessage) ||
                row.GetProperty("boundedUserMessageSha256").GetString() != Hash(boundedItem.UserMessage) ||
                row.GetProperty("fullEdgeCount").GetInt32() != item.Edges.Count ||
                row.GetProperty("boundedEdgeCount").GetInt32() != boundedItem.Edges.Count ||
                row.GetProperty("boundedProviderBodyBytes").GetInt32() != boundedItem.BodyBytes) return false;
        }
        return true;
    }

    private static IEnumerable<Request> Build(string repo, Source source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, source.PdfPath.Replace('/', Path.DirectorySeparatorChar)));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), source.DocumentId);
        var f1Path = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar), source.F1Path.Replace('/', Path.DirectorySeparatorChar));
        using var f1Capture = JsonDocument.Parse(File.ReadAllText(f1Path));
        var f1Row = source.Kind switch
        {
            F1Kind.RawCapture => f1Capture.RootElement,
            F1Kind.ResultRow => f1Capture.RootElement.GetProperty("row"),
            F1Kind.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidOperationException("unknown-f1-kind"),
        };
        var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
        var correspondences = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
        var frozenF1Hash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash) ? semanticHash.GetString() : f1Row.GetProperty("userMessageSha256").GetString();
        if (frozenF1Hash != f1.Request.UserMessageSha256) throw new InvalidOperationException($"p6th3-h2-{source.DocumentId}-f1-request-parity-failed");
        var functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Row.GetProperty("rawResponse").GetString()!);
        var g2aPath = Path.Combine(repo, G2ACaptureRoot.Replace('/', Path.DirectorySeparatorChar), source.DocumentId + ".raw-capture.v1.json");
        var g2aBytes = File.ReadAllBytes(g2aPath);
        using var g2a = JsonDocument.Parse(g2aBytes);
        var g2aRoot = g2a.RootElement;
        if (g2aRoot.GetProperty("finishReason").GetString() != "stop" || g2aRoot.GetProperty("retryCount").GetInt32() != 0 ||
            Hash(g2aRoot.GetProperty("rawResponse").GetString()!) != g2aRoot.GetProperty("rawResponseSha256").GetString() ||
            Hash(g2aRoot.GetProperty("rawSse").GetString()!) != g2aRoot.GetProperty("rawSseSha256").GetString())
            throw new InvalidOperationException($"p6th3-h2-{source.DocumentId}-g2a-raw-invalid");
        using var g2aPreflight = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, G2APreflightPath.Replace('/', Path.DirectorySeparatorChar))));
        var frozenDoc = g2aPreflight.RootElement.GetProperty("cohort").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId);
        var issuedPrimaries = frozenDoc.GetProperty("issuedPrimaries").EnumerateArray().Select(value => value.GetProperty("occurrence").GetString()!).ToHashSet(StringComparer.Ordinal);
        var g2aDecisions = ReadG2ADecisions(g2aRoot.GetProperty("rawResponse").GetString()!, issuedPrimaries);
        if (g2aDecisions.Any(value => value.Value == "INVALID")) throw new InvalidOperationException($"p6th3-h2-{source.DocumentId}-g2a-ledger-invalid");

        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = pack.OwnedAliases;
        var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var aliasById = f1.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
        foreach (var anchor in g2aDecisions.Where(value => value.Value == "HAS_STRUCTURAL_EXTENT")
                     .OrderBy(value => int.Parse(value.Key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)))
        {
            var alias = aliasById[anchor.Key];
            if (functions.Decisions.Single(value => value.OccurrenceId == anchor.Key).Function != V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE)
                throw new InvalidOperationException($"p6th3-h2-{source.DocumentId}-g2a-anchor-not-f1-eligible");
            var start = Array.IndexOf(owned.ToArray(), alias);
            if (start < 0) throw new InvalidOperationException($"p6th3-h2-{source.DocumentId}-anchor-not-owned");
            if (start + 1 >= owned.Count) continue;
            var aliases = owned.Skip(start).ToArray();
            var occurrences = aliases.Select(item => new { occurrence = idByAlias[item], page = atoms[item].Page, text = atoms[item].Text, selectable = false }).ToArray();
            var edges = Enumerable.Range(0, aliases.Length - 1).Select(index => new Edge(anchor.Key, idByAlias[aliases[index]], idByAlias[aliases[index + 1]], index, aliases[index], aliases[index + 1])).ToArray();
            var user = JsonSerializer.Serialize(new
            {
                protocolVersion = "v5-function-conditioned-continuation-boundary-1",
                anchors = new[] { new { anchor = anchor.Key, occurrences, edges = edges.Select(edge => new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, ordinal = edge.Ordinal }).ToArray() } },
            });
            var headingRequest = new V5FreeHeadingRequestV1("v5-function-conditioned-continuation-boundary-1", SystemPrompt, user,
                Hash(user), Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
            var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(headingRequest, pack.MaxCompletionTokens);
            yield return new Request(source, pack.PackId, anchor.Key, alias, plan.SourceSha256, plan.SourceUniverseSha256,
                Hash(g2aBytes), body.PayloadBytes, body.Hash, body.Bytes, SystemPrompt, user, pack.MaxCompletionTokens, edges);
        }
    }

    private static Dictionary<string, string> ReadG2ADecisions(string raw, IReadOnlySet<string> issued)
    {
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6th3-g2a-root-invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 || !row.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.String || !row.TryGetProperty("anchor", out var anchor) || anchor.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("p6th3-g2a-decision-schema-invalid");
            var id = primary.GetString()!;
            var value = anchor.GetString()!;
            if (!issued.Contains(id) || value is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT") || !result.TryAdd(id, value))
                throw new InvalidOperationException("p6th3-g2a-decision-invalid-or-duplicate");
        }
        if (result.Count != issued.Count) throw new InvalidOperationException("p6th3-g2a-total-ledger-invalid");
        return result;
    }

    private static IReadOnlyList<Decision>? Parse(Request request, string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (Encoding.UTF8.GetByteCount(raw) > ResponseByteCap || root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != request.Edges.Count) return null;
            var expected = request.Edges.ToDictionary(value => $"{value.Anchor}|{value.Left}|{value.Right}", StringComparer.Ordinal);
            var actual = new Dictionary<string, Decision>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 4 ||
                    !row.TryGetProperty("anchor", out var anchor) || anchor.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("left", out var left) || left.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("right", out var right) || right.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("boundary", out var boundary) || boundary.ValueKind != JsonValueKind.String) return null;
                var decision = new Decision(anchor.GetString()!, left.GetString()!, right.GetString()!, boundary.GetString()!);
                var key = $"{decision.Anchor}|{decision.Left}|{decision.Right}";
                if (!expected.ContainsKey(key) || decision.Boundary is not ("CONTINUES_STRUCTURAL_UNIT" or "STOPS_STRUCTURAL_UNIT") || !actual.TryAdd(key, decision)) return null;
            }
            return actual.Count == expected.Count ? request.Edges.Select(value => actual[$"{value.Anchor}|{value.Left}|{value.Right}"]).ToArray() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsMonotonic(IReadOnlyList<Decision> decisions)
    {
        var stopped = false;
        foreach (var decision in decisions)
        {
            if (decision.Boundary == "STOPS_STRUCTURAL_UNIT") stopped = true;
            else if (stopped) return false;
        }
        return true;
    }

    private static bool ManifestParity(JsonElement manifest, IReadOnlyList<Request> requests)
    {
        if (manifest.GetProperty("status").GetString() != "H2_FULL_REQUEST_UNIVERSE_FROZEN_NOT_AUTHORIZED" ||
            manifest.GetProperty("execution").GetProperty("providerCalls").GetInt32() != 0 ||
            manifest.GetProperty("execution").GetProperty("goldRead").GetBoolean()) return false;
        var frozen = manifest.GetProperty("requestUniverse").EnumerateArray().ToDictionary(value => Key(value), StringComparer.Ordinal);
        return requests.All(request => frozen.TryGetValue($"{request.Source.DocumentId}|{request.PackId}|{request.Anchor}", out var row) &&
            row.GetProperty("providerBodySha256").GetString() == request.BodyHash &&
            row.GetProperty("userMessageSha256").GetString() == Hash(request.UserMessage) &&
            row.GetProperty("providerBodyBytes").GetInt32() == request.BodyBytes &&
            row.GetProperty("edgeCount").GetInt32() == request.Edges.Count);

        static string Key(JsonElement value) => $"{value.GetProperty("documentId").GetString()}|{value.GetProperty("packId").GetString()}|{value.GetProperty("anchor").GetString()}";
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

    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var key in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void WriteNew(string path, object value)
    {
        if (File.Exists(path)) throw new InvalidOperationException("p6th3-h2-immutable-capture-exists");
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
