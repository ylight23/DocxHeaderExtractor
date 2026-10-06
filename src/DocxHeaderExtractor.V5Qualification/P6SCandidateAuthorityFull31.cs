using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>P6S-D: exact 31-pack snapshot-only execution with no retry, repair, fallback, or Gold access.</summary>
internal static class P6SCandidateAuthorityFull31
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string Confirm = "yes-i-authorize-p6sd-candidate-authority-full31-31-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Item(string DocumentId, string Pdf, PdfCandidateAuthorityDocumentPlan Plan, PdfCandidateAuthorityPreparedPack Pack);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var dir = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(dir, "execution-manifest.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var checkpointPath = Path.Combine(dir, "result.in-progress.v1.json");
        if (File.Exists(resultPath)) return Fail("p6sd: immutable result already exists; stop before network");

        var items = Build(repo);
        var manifest = BuildManifest(repo, items);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(dir);
            WriteNew(manifestPath, manifest);
            Console.WriteLine("P6S-D manifest prepared: 31 exact bodies, 2,884 owned atoms; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (!ManifestParity(manifestPath, manifest)) return Fail("p6sd: frozen manifest/body parity failed; no network call");
        Console.WriteLine("P6S-D manifest parity PASS: 31 exact bodies and 2,884 owned atoms.");

        var authorized = args.Contains($"--confirm-p6sd={Confirm}");
        if (!authorized)
        {
            Console.WriteLine("P6S-D PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6sd: OPENROUTER_API_KEY is not set; providerCalls=0");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var rows = RecoverCheckpoint(checkpointPath, items);
        if (rows is null) return Fail("p6sd: checkpoint is in-flight or does not match frozen request prefix; no call sent");
        if (!File.Exists(checkpointPath))
            AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
        var done = rows.Count;

        foreach (var item in items.Skip(done))
        {
            // If the process dies after this durable marker but before a result is persisted, stop
            // on re-entry: the remote outcome is ambiguous and must never be silently resent.
            AtomicWrite(checkpointPath, Checkpoint("IN_FLIGHT", rows, item));
            OpenRouterExecutionObservation? response = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                response = await client.ExecuteObservedAsync(item.Pack.ProviderBody, item.Pack.MaxCompletionTokens,
                    item.Pack.Request.SystemPrompt, item.Pack.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();

            var parserAccepted = false;
            object analysis;
            try
            {
                if (response is null) analysis = new { classification = "TRANSPORT_ERROR", parserAccepted = false, error = transportError };
                else if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
                    analysis = new { classification = "NON_STOP_FINISH", parserAccepted = false, finishReason = response.FinishReason };
                else
                {
                    var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(item.Pack, response.Content);
                    parserAccepted = true;
                    analysis = new
                    {
                        classification = parsed.Quarantined.Count == 0 ? "PARSED_NO_QUARANTINE" : "PARSED_WITH_QUARANTINE",
                        parserAccepted = true,
                        parsed.RawDecisionCount,
                        acceptedBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count,
                        acceptedAfterOverlapQuarantine = parsed.Accepted.Count,
                        headingBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.HEADING),
                        representationBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.REPRESENTATION),
                        headingAfterOverlapQuarantine = parsed.Headings.Count,
                        quarantineCount = parsed.Quarantined.Count,
                        overlapQuarantineCount = parsed.Quarantined.Count(value => value.Reason == "candidate-overlap-conflict"),
                        quarantineReasons = parsed.Quarantined.GroupBy(value => value.Reason, StringComparer.Ordinal)
                            .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()),
                    };
                }
            }
            catch (Exception exception)
            {
                analysis = new { classification = "PARSER_FATAL", parserAccepted = false, error = exception.Message };
            }

            var row = JsonSerializer.SerializeToElement(new
            {
                documentId = item.DocumentId, packOrdinal = item.Pack.PackOrdinal, item.Pack.PackId,
                ownedAtoms = item.Pack.OwnedAliases.Count, visibleAtoms = item.Pack.VisibleAliases.Count,
                candidateCount = item.Pack.Universe.Candidates.Count, relationCount = item.Pack.Universe.Relations.Count,
                relationsTruncated = item.Pack.Universe.RelationsTruncated,
                semanticRequestHash = item.Pack.Request.UserMessageSha256, providerRequestHash = item.Pack.ProviderRequestHash,
                providerRequestBytes = item.Pack.ProviderRequestBytes, maxCompletionTokens = item.Pack.MaxCompletionTokens,
                sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256,
                candidateUniverseFingerprint = item.Pack.Universe.Fingerprint,
                transportAccepted = response is not null, transportError, finishReason = response?.FinishReason,
                usage = response?.Usage, retryCount = response?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
                sseEventCount = response?.SseEventCount ?? 0,
                rawSseSha256 = response is null ? null : Hash(response.RawSse), rawSse = response?.RawSse,
                rawResponseSha256 = response is null ? null : Hash(response.Content),
                rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content),
                rawResponse = response?.Content, parserAccepted, analysis,
            });
            rows.Add(row);
            AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
            using var analysisDocument = JsonDocument.Parse(JsonSerializer.Serialize(analysis));
            var classified = analysisDocument.RootElement.TryGetProperty("classification", out var classification)
                ? classification.GetString() : "UNKNOWN";
            Console.WriteLine($"[{rows.Count}/31] {item.DocumentId}/{item.Pack.PackId}: {classified}, finish={response?.FinishReason ?? "n/a"}, retry={response?.RetryCount ?? 0}, ms={watch.ElapsedMilliseconds}");
        }

        if (rows.Count != 31) return Fail("p6sd: internal call accounting did not reach 31");
        var result = new
        {
            schemaVersion = "v5-p6sd-candidate-authority-full31-result-v1",
            sourceManifest = $"{Root}/execution-manifest.v1.json", startedFromHead = GitHead(repo),
            providerCalls = rows.Count, maximumAuthorizedProviderCalls = 31,
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = "none", responseFormat = "json_object" },
            retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE",
            semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", rows,
        };
        WriteNew(resultPath, result);
        File.Delete(checkpointPath);
        Console.WriteLine("P6S-D raw capture complete: 31 primary attempts, retry=0, repair/fallback=OFF, GoldRead=false.");
        return 0;
    }

    private static Item[] Build(string repo)
    {
        var result = new List<Item>();
        foreach (var (documentId, relativePdf) in new[] { ("SRC-089", Src089), ("SRC-095", Src095) })
        {
            var pdf = Path.Combine(repo, relativePdf.Replace('/', Path.DirectorySeparatorChar));
            var sourceSha = CanonicalSemanticSourceHash.Compute(pdf);
            var snapshot = Path.Combine(repo, "eval/a99-closed-loop/pdf-canonical-source-v1", sourceSha + ".json");
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshot, documentId);
            result.AddRange(plan.Packs.Select(pack => new Item(documentId, relativePdf, plan, pack)));
        }
        var items = result.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Pack.PackOrdinal).ToArray();
        if (items.Length != 31 || items.Sum(item => item.Pack.OwnedAliases.Count) != 2884 ||
            items.Count(item => item.DocumentId == "SRC-089") != 7 || items.Count(item => item.DocumentId == "SRC-095") != 24)
            throw new InvalidOperationException("p6sd-snapshot-P05-conservation-failed");
        return items;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items)
    {
        var rows = items.Select(item => new
        {
            DocumentId = item.DocumentId, item.Pack.PackOrdinal, item.Pack.PackId,
            ownedAtoms = item.Pack.OwnedAliases.Count, visibleAtoms = item.Pack.VisibleAliases.Count,
            candidates = item.Pack.Universe.Candidates.Count, readOnlyRelations = item.Pack.Universe.Relations.Count,
            relationsTruncated = item.Pack.Universe.RelationsTruncated, semanticRequestHash = item.Pack.Request.UserMessageSha256,
            ProviderRequestHash = item.Pack.ProviderRequestHash, ProviderRequestBytes = item.Pack.ProviderRequestBytes,
            item.Pack.MaxCompletionTokens, sourceSha256 = item.Plan.SourceSha256,
            sourceUniverseSha256 = item.Plan.SourceUniverseSha256, candidateUniverseFingerprint = item.Pack.Universe.Fingerprint,
        }).ToArray();
        return new
        {
            schemaVersion = "v5-p6sd-candidate-authority-full31-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", authority = "P6S-A canonical snapshot only",
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = "none", responseFormat = "json_object" },
            executionGate = new { maximumProviderCalls = 31, onePrimaryAttemptPerPack = true, retry = 0, repair = false, fallback = false, checkpointAfterEveryCall = true, goldRead = false },
            rows,
        };
    }

    private static bool ManifestParity(string path, object expected)
    {
        try
        {
            var current = JsonNode.Parse(File.ReadAllText(path));
            var rebuilt = JsonNode.Parse(JsonSerializer.Serialize(expected));
            return JsonNode.DeepEquals(current, rebuilt);
        }
        catch (Exception exception) { Console.Error.WriteLine($"p6sd manifest parse failed: {exception.Message}"); return false; }
    }

    private static List<JsonElement>? RecoverCheckpoint(string path, IReadOnlyList<Item> items)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var checkpoint = JsonDocument.Parse(File.ReadAllText(path));
            var root = checkpoint.RootElement;
            if (root.GetProperty("status").GetString() != "READY") return null;
            var rows = root.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList();
            if (rows.Count > items.Count) return null;
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index]; var item = items[index];
                if (row.GetProperty("documentId").GetString() != item.DocumentId ||
                    row.GetProperty("packId").GetString() != item.Pack.PackId ||
                    row.GetProperty("providerRequestHash").GetString() != item.Pack.ProviderRequestHash ||
                    row.GetProperty("retryCount").GetInt32() != 0) return null;
            }
            return rows;
        }
        catch { return null; }
    }

    private static object Checkpoint(string status, IReadOnlyList<JsonElement> rows, Item? inFlight) => new
    {
        schemaVersion = "v5-p6sd-full31-checkpoint-v1", status, providerCallsCompletedAndPersisted = rows.Count,
        maximumProviderCalls = 31, retry = 0, repair = false, fallback = false, goldRead = false,
        inFlight = inFlight is null ? null : new { inFlight.DocumentId, inFlight.Pack.PackOrdinal, inFlight.Pack.PackId, inFlight.Pack.ProviderRequestHash },
        rows,
    };

    private static string GitHead(string repo)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
            return process is null ? "UNKNOWN" : process.StandardOutput.ReadToEnd().Trim();
        }
        catch { return "UNKNOWN"; }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteNew(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void AtomicWrite(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
}
