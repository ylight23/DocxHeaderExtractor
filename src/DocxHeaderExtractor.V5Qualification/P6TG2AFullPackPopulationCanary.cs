using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>One-shot five-pack G2A population run derived from the complete provider-free preflight.</summary>
internal static class P6TG2AFullPackPopulationCanary
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string PreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string CaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string Confirm = "yes-i-authorize-p6tg2a-full-pack-population-five-primary-calls";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Kind.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
    ];
    private static readonly string SystemPrompt = PdfFunctionConditionedHeadingAuthorityAdapter.G2APrompt;

    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Kind Kind, bool F1UsedCorrespondences);
    private enum F1Kind { RawCapture, ResultRow, ResultRows }
    private sealed record Primary(string Occurrence, string Alias, int OwnedIndex, int Page, int Ordinal);
    private sealed record Request(Source Source, PdfCandidateAuthorityDocumentPlan Plan, PdfCandidateAuthorityPreparedPack Pack,
        string F1CaptureSha256, string F1ResponseSha256, IReadOnlyList<Primary> Primaries,
        string SystemPrompt, string UserMessage, byte[] Body, string BodyHash, int BodyBytes);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var requests = Sources.Select(source => Build(repo, source)).ToArray();
        var preflightPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var preflight = JsonDocument.Parse(File.ReadAllText(preflightPath));
        if (!ManifestParity(preflight.RootElement, requests)) return Fail("p6tg2a-full: preflight/request parity failed; no network");
        if (!args.Contains($"--confirm-p6tg2a-full-pack={Confirm}"))
        {
            Console.WriteLine("P6T-G2A full-pack population PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            foreach (var request in requests) Console.WriteLine($"{request.Source.DocumentId} {request.Pack.PackId}: {request.Primaries.Count} primaries, {request.BodyBytes} body bytes");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6tg2a-full: OPENROUTER_API_KEY missing; no network");
        var captureDir = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(captureDir)) return Fail("p6tg2a-full: immutable capture directory already exists; no rerun");

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
            schemaVersion = "v5-p6tg2a-full-pack-population-reservation-v1",
            status = "FIVE_PRIMARY_CALLS_RESERVED",
            preflightSha256 = Hash(File.ReadAllText(preflightPath)),
            order = requests.Select(value => value.Source.DocumentId).ToArray(),
            requestHashes = requests.Select(value => new { documentId = value.Source.DocumentId, providerBodySha256 = value.BodyHash, primaryCount = value.Primaries.Count }).ToArray(),
            providerCallsBeforeSend = 0,
            maximumPrimaryCalls = 5,
            retriesAllowed = 0,
            repairAllowed = false,
            fallbackAllowed = false,
            goldRead = false,
        });

        var summaries = new List<object>();
        var calls = 0;
        foreach (var request in requests)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            calls++;
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.Body, request.Pack.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            var rawPath = Path.Combine(captureDir, request.Source.DocumentId + ".raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6tg2a-full-pack-population-raw-capture-v1",
                documentId = request.Source.DocumentId,
                packId = request.Pack.PackId,
                sourceSha256 = request.Plan.SourceSha256,
                sourceUniverseSha256 = request.Plan.SourceUniverseSha256,
                providerCallOrdinal = calls,
                providerCalls = 1,
                providerBodySha256 = request.BodyHash,
                providerBodyBytes = request.BodyBytes,
                userMessageSha256 = Hash(request.UserMessage),
                systemPromptSha256 = Hash(request.SystemPrompt),
                issuedPrimaryCount = request.Primaries.Count,
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

            // The raw capture is immutable before parsing or classifying the provider response.
            var parsed = observation is not null && transportError is null &&
                         string.Equals(observation.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)
                ? Parse(request, observation.Content)
                : null;
            summaries.Add(new
            {
                documentId = request.Source.DocumentId,
                packId = request.Pack.PackId,
                providerCallOrdinal = calls,
                providerBodySha256 = request.BodyHash,
                issued = request.Primaries.Count,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                ledgerAccepted = parsed is not null,
                returned = parsed?.Count,
                hasStructuralExtent = parsed?.Count(value => value.Anchor == "HAS_STRUCTURAL_EXTENT"),
                noStructuralExtent = parsed?.Count(value => value.Anchor == "NO_STRUCTURAL_EXTENT"),
                quarantineCount = parsed is null ? (int?)null : 0,
                transportError,
            });
            Console.WriteLine($"[{calls}/5] {request.Source.DocumentId}: {(parsed is null ? "NOT_ACCEPTED" : "LEDGER_ACCEPTED")}, finish={observation?.FinishReason ?? "n/a"}, issued={request.Primaries.Count}");
        }

        WriteNew(Path.Combine(captureDir, "result.v1.json"), new
        {
            schemaVersion = "v5-p6tg2a-full-pack-population-result-v1",
            status = summaries.All(value => JsonSerializer.Serialize(value).Contains("\"ledgerAccepted\":true", StringComparison.Ordinal))
                ? "ALL_FIVE_TOTAL_LEDGERS_ACCEPTED" : "ONE_OR_MORE_CALLS_NOT_ACCEPTED",
            providerCalls = calls,
            maximumPrimaryCalls = 5,
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

    private static Request Build(string repo, Source source)
    {
        var sourcePath = Path.Combine(repo, source.PdfPath.Replace('/', Path.DirectorySeparatorChar));
        var sourceHash = CanonicalSemanticSourceHash.Compute(sourcePath);
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), source.DocumentId);
        var capturePath = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar), source.F1Path.Replace('/', Path.DirectorySeparatorChar));
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        var f1Row = source.Kind switch
        {
            F1Kind.RawCapture => capture.RootElement,
            F1Kind.ResultRow => capture.RootElement.GetProperty("row"),
            F1Kind.ResultRows => capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidOperationException("unknown-f1-kind"),
        };
        var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
        var correspondences = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
        var frozenF1Hash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash) ? semanticHash.GetString() : f1Row.GetProperty("userMessageSha256").GetString();
        if (frozenF1Hash != f1.Request.UserMessageSha256) throw new InvalidOperationException($"p6tg2a-{source.DocumentId}-f1-request-parity-failed");
        var f1Response = f1Row.GetProperty("rawResponse").GetString() ?? throw new InvalidOperationException("p6tg2a-f1-response-missing");
        if (f1Row.GetProperty("finishReason").GetString() != "stop") throw new InvalidOperationException($"p6tg2a-{source.DocumentId}-f1-not-terminal");
        var f1Result = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response);
        if (f1Result.Decisions.Count != 96) throw new InvalidOperationException($"p6tg2a-{source.DocumentId}-f1-ledger-invalid");

        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = pack.OwnedAliases;
        var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var indexByAlias = owned.Select((alias, index) => (alias, index)).ToDictionary(value => value.alias, value => value.index, StringComparer.Ordinal);
        var issued = f1Result.Decisions.Where(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE)
            .Select(value => (value.OccurrenceId, Alias: f1.Request.Occurrences.Single(item => item.Id == value.OccurrenceId).Atom.Alias))
            .OrderBy(value => atoms[value.Alias].Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        var user = PdfFunctionConditionedHeadingAuthorityAdapter.ComposeG2AUserMessage(
            owned.Select(alias => atoms[alias]).ToArray(),
            idByAlias,
            issued.Select(value => (value.OccurrenceId, atoms[value.Alias])).ToArray());
        var request = new V5FreeHeadingRequestV1("v5-function-conditioned-anchor-existence-1", SystemPrompt, user, Hash(user), Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
        var preflightPath = Path.Combine(repo, PreflightPath.Replace('/', Path.DirectorySeparatorChar));
        using var preflight = JsonDocument.Parse(File.ReadAllText(preflightPath));
        var frozenDoc = preflight.RootElement.GetProperty("cohort").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId);
        if (frozenDoc.GetProperty("packId").GetString() != pack.PackId ||
            frozenDoc.GetProperty("sourceSha256").GetString() != plan.SourceSha256 ||
            frozenDoc.GetProperty("sourceUniverseSha256").GetString() != plan.SourceUniverseSha256 ||
            frozenDoc.GetProperty("g2a").GetProperty("userMessageSha256").GetString() != request.UserMessageSha256 ||
            frozenDoc.GetProperty("g2a").GetProperty("providerBodySha256").GetString() != body.Hash)
            throw new InvalidOperationException($"p6tg2a-{source.DocumentId}-frozen-manifest-parity-failed");
        return new Request(source, plan, pack, Hash(File.ReadAllText(capturePath)), Hash(f1Response),
            issued.Select(value => new Primary(value.OccurrenceId, value.Alias, indexByAlias[value.Alias], atoms[value.Alias].Page, atoms[value.Alias].Ordinal)).ToArray(),
            SystemPrompt, user, body.PayloadBytes, body.Hash, body.Bytes);
    }

    private static bool ManifestParity(JsonElement manifest, IReadOnlyList<Request> requests)
    {
        if (manifest.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" || manifest.GetProperty("execution").GetProperty("providerCalls").GetInt32() != 0 || manifest.GetProperty("execution").GetProperty("goldRead").GetBoolean()) return false;
        var frozenRows = manifest.GetProperty("cohort").EnumerateArray().ToDictionary(value => value.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        return requests.All(request => frozenRows.TryGetValue(request.Source.DocumentId, out var row) &&
            row.GetProperty("g2a").GetProperty("providerBodySha256").GetString() == request.BodyHash &&
            row.GetProperty("g2a").GetProperty("expectedLedgerCardinality").GetInt32() == request.Primaries.Count);
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

    private sealed record Decision(string Primary, string Anchor);
    private static IReadOnlyList<Decision>? Parse(Request request, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != request.Primaries.Count) return null;
            var issued = request.Primaries.Select(value => value.Occurrence).ToHashSet(StringComparer.Ordinal);
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 || !row.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.String || !row.TryGetProperty("anchor", out var anchor) || anchor.ValueKind != JsonValueKind.String) return null;
                var key = primary.GetString()!;
                var value = anchor.GetString()!;
                if (!issued.Contains(key) || value is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT") || !parsed.TryAdd(key, value)) return null;
            }
            return parsed.Count == issued.Count ? request.Primaries.Select(value => new Decision(value.Occurrence, parsed[value.Occurrence])).ToArray() : null;
        }
        catch (JsonException) { return null; }
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
        if (File.Exists(path)) throw new InvalidOperationException("p6tg2a-immutable-capture-exists");
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
