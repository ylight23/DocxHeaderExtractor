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

/// <summary>
/// P6S-R is the causal reasoning arm for P6S-D: snapshot, P05 packs, candidates, prompt, JSON
/// object schema, parser, binder, route, and temperature remain identical.  Only the raw carrier
/// member changes from reasoning.effort=none to reasoning.enabled=true (effort omitted).
/// </summary>
internal static class P6SRMatchedReasoningFull31
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sr-matched-reasoning-full31";
    private const string ControlRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Confirm = "yes-i-authorize-p6sr-matched-reasoning-full31-31-calls";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Item(string DocumentId, PdfCandidateAuthorityDocumentPlan Plan,
        PdfCandidateAuthorityPreparedPack Control, PdfCandidateAuthorityPreparedPack Treatment, string ControlBodyHash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath)) return Fail("p6sr: immutable result already exists; stop before network");
        var items = Build(repo);
        var manifest = BuildManifest(repo, items);
        Directory.CreateDirectory(directory);
        if (!File.Exists(manifestPath))
        {
            WriteNew(manifestPath, manifest);
            Console.WriteLine("P6S-R manifest prepared: 31 matched P6S-D treatment bodies; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (!ManifestParity(manifestPath, manifest)) return Fail("p6sr: manifest/body/control parity failed; no network call");
        Console.WriteLine("P6S-R matched-control parity PASS: P05/prompt/candidates/parser unchanged; reasoning is the only request delta.");
        if (!args.Contains($"--confirm-p6sr={Confirm}"))
        {
            Console.WriteLine("P6S-R PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6sr: OPENROUTER_API_KEY is not set; providerCalls=0");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        // The frozen payload, not this client default, expresses reasoning.enabled=true/effort omitted.
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var rows = RecoverCheckpoint(checkpointPath, items);
        if (rows is null) return Fail("p6sr: ambiguous checkpoint or request mismatch; no call sent");
        if (!File.Exists(checkpointPath)) AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
        foreach (var item in items.Skip(rows.Count))
        {
            AtomicWrite(checkpointPath, Checkpoint("IN_FLIGHT", rows, item));
            OpenRouterExecutionObservation? response = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                response = await client.ExecuteObservedAsync(item.Treatment.ProviderBody, item.Treatment.MaxCompletionTokens,
                    item.Treatment.Request.SystemPrompt, item.Treatment.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var analysis = Analyze(item, response, transportError);
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                documentId = item.DocumentId, packOrdinal = item.Treatment.PackOrdinal, item.Treatment.PackId,
                ownedAtoms = item.Treatment.OwnedAliases.Count, visibleAtoms = item.Treatment.VisibleAliases.Count,
                candidateCount = item.Treatment.Universe.Candidates.Count, relationCount = item.Treatment.Universe.Relations.Count,
                semanticRequestHash = item.Treatment.Request.UserMessageSha256,
                systemPromptSha256 = Hash(item.Treatment.Request.SystemPrompt), sourceSha256 = item.Plan.SourceSha256,
                sourceUniverseSha256 = item.Plan.SourceUniverseSha256, candidateUniverseFingerprint = item.Treatment.Universe.Fingerprint,
                controlProviderRequestHash = item.ControlBodyHash, treatmentProviderRequestHash = item.Treatment.ProviderRequestHash,
                providerRequestBytes = item.Treatment.ProviderRequestBytes, maxCompletionTokens = item.Treatment.MaxCompletionTokens,
                transportAccepted = response is not null, transportError, finishReason = response?.FinishReason,
                usage = response?.Usage, retryCount = response?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
                sseEventCount = response?.SseEventCount ?? 0, rawSseSha256 = response is null ? null : Hash(response.RawSse), rawSse = response?.RawSse,
                rawResponseSha256 = response is null ? null : Hash(response.Content), rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content),
                rawResponse = response?.Content, analysis,
            }));
            AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
            Console.WriteLine($"[{rows.Count}/31] {item.DocumentId}/{item.Treatment.PackId}: {Classification(analysis)}, finish={response?.FinishReason ?? "n/a"}, retry={response?.RetryCount ?? 0}, ms={watch.ElapsedMilliseconds}");
        }
        if (rows.Count != 31) return Fail("p6sr: call accounting did not reach 31");
        WriteNew(resultPath, new
        {
            schemaVersion = "v5-p6sr-matched-reasoning-full31-result-v1", sourceManifest = $"{Root}/execution-manifest.v1.json",
            controlCapture = $"{ControlRoot}/result.v1.json", startedFromHead = GitHead(repo), providerCalls = 31, maximumAuthorizedProviderCalls = 31,
            treatment = new { reasoning = new { enabled = true }, effort = "OMITTED" }, control = new { reasoning = new { effort = "none" } },
            retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6S-R raw capture complete: exactly 31 reasoning-enabled primary attempts; score remains an offline P6S-F-equivalent step.");
        return 0;
    }

    private static Item[] Build(string repo)
    {
        var controlPath = Path.Combine(repo, ControlRoot.Replace('/', Path.DirectorySeparatorChar), "result.v1.json");
        using var controlCapture = JsonDocument.Parse(File.ReadAllText(controlPath));
        if (controlCapture.RootElement.GetProperty("providerCalls").GetInt32() != 31 ||
            controlCapture.RootElement.GetProperty("goldRead").GetBoolean())
            throw new InvalidOperationException("p6sr-control-capture-authority-invalid");
        var all = new List<Item>();
        foreach (var (id, source, count) in new[] { ("SRC-089", Src089, 7), ("SRC-095", Src095, 24) })
        {
            var pdf = Path.Combine(repo, source.Replace('/', Path.DirectorySeparatorChar));
            var sourceHash = CanonicalSemanticSourceHash.Compute(pdf);
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), id);
            if (plan.Packs.Count != count) throw new InvalidOperationException($"p6sr-pack-count:{id}");
            foreach (var control in plan.Packs)
            {
                var captured = controlCapture.RootElement.GetProperty("rows").EnumerateArray().Single(row =>
                    row.GetProperty("documentId").GetString() == id && row.GetProperty("PackId").GetString() == control.PackId);
                var controlBody = PdfCandidateAuthorityQualificationAdapter.BuildProviderBody(control.Request, control.MaxCompletionTokens);
                if (controlBody.Hash != control.ProviderRequestHash || captured.GetProperty("providerRequestHash").GetString() != controlBody.Hash ||
                    captured.GetProperty("semanticRequestHash").GetString() != control.Request.UserMessageSha256)
                    throw new InvalidOperationException($"p6sr-control-parity:{id}:{control.PackId}");
                var treatmentBody = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(control.Request, control.MaxCompletionTokens);
                AssertOnlyReasoningDelta(controlBody.PayloadBytes, treatmentBody.PayloadBytes, id, control.PackId);
                var treatment = control with { ProviderBody = treatmentBody.PayloadBytes, ProviderRequestHash = treatmentBody.Hash, ProviderRequestBytes = treatmentBody.Bytes };
                all.Add(new Item(id, plan, control, treatment, controlBody.Hash));
            }
        }
        var ordered = all.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Treatment.PackOrdinal).ToArray();
        if (ordered.Length != 31 || ordered.Sum(item => item.Treatment.OwnedAliases.Count) != 2884) throw new InvalidOperationException("p6sr-owned-source-conservation-failed");
        return ordered;
    }

    private static object BuildManifest(string repo, IReadOnlyList<Item> items) => new
    {
        schemaVersion = "v5-p6sr-matched-reasoning-full31-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", preparedAtHead = GitHead(repo),
        providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        hypothesis = "P6S-D control versus P6S-R treatment changes only the OpenRouter reasoning object; all P05 source/candidate/context/prompt/response contract/parser/binder fields are identical.",
        control = new { capture = $"{ControlRoot}/result.v1.json", reasoning = new { effort = "none" } },
        treatment = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object" },
        execution = new { maximumProviderCalls = 31, onePrimaryAttemptPerPack = true, retry = 0, repair = false, fallback = false, goldDuringRun = false },
        rows = items.Select(item => new
        {
            item.DocumentId, item.Treatment.PackOrdinal, item.Treatment.PackId, ownedAtoms = item.Treatment.OwnedAliases.Count, visibleAtoms = item.Treatment.VisibleAliases.Count,
            candidateCount = item.Treatment.Universe.Candidates.Count, relationCount = item.Treatment.Universe.Relations.Count,
            semanticRequestHash = item.Treatment.Request.UserMessageSha256, systemPromptSha256 = Hash(item.Treatment.Request.SystemPrompt),
            candidateUniverseFingerprint = item.Treatment.Universe.Fingerprint, sourceSha256 = item.Plan.SourceSha256, sourceUniverseSha256 = item.Plan.SourceUniverseSha256,
            controlProviderRequestHash = item.ControlBodyHash, treatmentProviderRequestHash = item.Treatment.ProviderRequestHash,
            controlProviderRequestBytes = item.Control.ProviderRequestBytes, treatmentProviderRequestBytes = item.Treatment.ProviderRequestBytes,
            item.Treatment.MaxCompletionTokens,
        }).ToArray(),
    };

    private static object Analyze(Item item, OpenRouterExecutionObservation? response, string? transportError)
    {
        if (response is null) return new { classification = "TRANSPORT_ERROR", parserAccepted = false, error = transportError };
        if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
            return new { classification = "NON_STOP_FINISH", parserAccepted = false, finishReason = response.FinishReason };
        try
        {
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(item.Treatment, response.Content);
            return new { classification = parsed.Quarantined.Count == 0 ? "PARSED_NO_QUARANTINE" : "PARSED_WITH_QUARANTINE", parserAccepted = true,
                parsed.RawDecisionCount, acceptedBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count,
                headingBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.HEADING),
                representationBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.REPRESENTATION),
                acceptedAfterOverlapQuarantine = parsed.Accepted.Count, headingAfterOverlapQuarantine = parsed.Headings.Count,
                quarantineCount = parsed.Quarantined.Count, quarantineReasons = parsed.Quarantined.GroupBy(value => value.Reason, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()) };
        }
        catch (Exception exception) { return new { classification = "PARSER_FATAL", parserAccepted = false, error = exception.Message }; }
    }

    private static void AssertOnlyReasoningDelta(byte[] controlBody, byte[] treatmentBody, string documentId, string packId)
    {
        var control = JsonNode.Parse(controlBody)!.AsObject(); var treatment = JsonNode.Parse(treatmentBody)!.AsObject();
        if (control["reasoning"]?["effort"]?.GetValue<string>() != "none" || control["reasoning"]?.AsObject().ContainsKey("enabled") != false ||
            treatment["reasoning"]?["enabled"]?.GetValue<bool>() != true || treatment["reasoning"]?.AsObject().ContainsKey("effort") != false)
            throw new InvalidOperationException($"p6sr-reasoning-shape:{documentId}:{packId}");
        control.Remove("reasoning"); treatment.Remove("reasoning");
        if (!JsonNode.DeepEquals(control, treatment)) throw new InvalidOperationException($"p6sr-nonreasoning-body-delta:{documentId}:{packId}");
    }

    private static List<JsonElement>? RecoverCheckpoint(string path, IReadOnlyList<Item> items)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.GetProperty("status").GetString() != "READY") return null;
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().Select(value => value.Clone()).ToList();
            if (rows.Count > items.Count) return null;
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].GetProperty("documentId").GetString() != items[i].DocumentId || rows[i].GetProperty("PackId").GetString() != items[i].Treatment.PackId ||
                    rows[i].GetProperty("treatmentProviderRequestHash").GetString() != items[i].Treatment.ProviderRequestHash || rows[i].GetProperty("retryCount").GetInt32() != 0) return null;
            return rows;
        }
        catch { return null; }
    }

    private static object Checkpoint(string status, IReadOnlyList<JsonElement> rows, Item? inFlight) => new
    {
        schemaVersion = "v5-p6sr-matched-reasoning-checkpoint-v1", status, providerCallsCompletedAndPersisted = rows.Count, maximumProviderCalls = 31,
        retry = 0, repair = false, fallback = false, goldRead = false,
        inFlight = inFlight is null ? null : new { inFlight.DocumentId, inFlight.Treatment.PackOrdinal, inFlight.Treatment.PackId, inFlight.Treatment.ProviderRequestHash }, rows,
    };
    private static bool ManifestParity(string path, object expected)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(JsonSerializer.Serialize(expected))); }
        catch { return false; }
    }
    private static string Classification(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.GetProperty("classification").GetString() ?? "UNKNOWN";
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void WriteNew(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void AtomicWrite(string path, object value) { var temp = path + ".tmp"; WriteNew(temp, value); File.Move(temp, path, true); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
    private static string GitHead(string repo)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN";
    }
}
