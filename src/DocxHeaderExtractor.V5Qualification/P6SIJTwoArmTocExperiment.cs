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

/// <summary>Two one-pack P6S diagnostic arms. It never changes P6S production/runtime behavior.</summary>
internal static class P6SIJTwoArmTocExperiment
{
    private const string ArtifactRoot = "artifacts/v5-p6s-candidate-authority/p6sij-two-arm-experiment-v2";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const string P6SHPath = "artifacts/v5-p6s-candidate-authority/p6sh-toc-presentation-sufficiency/reviewed-toc-hard-negative-audit.v1.json";
    private const string P6SDPath = "artifacts/v5-p6s-candidate-authority/p6sd-full31/result.v1.json";
    private const string Confirm = "yes-i-authorize-p6sij-two-single-pack-calls-no-retry";
    private const string PromptAddition = "A heading opens or continues structure at its own occurrence location. If an occurrence is used only to list, index, or point to structure elsewhere, classify it as REPRESENTATION, even when its text matches a heading at that other location.";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Arm(string Id, PdfCandidateAuthorityPreparedPack Pack,
        string ChangedDimension, string BaselineProviderRequestHash);
    private sealed record PackRef(string DocumentId, PdfCandidateAuthorityDocumentPlan Plan, PdfCandidateAuthorityPreparedPack Pack);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var dir = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(dir, "execution-manifest.v1.json");
        var preparedPath = Path.Combine(dir, "prepared-request-bodies.v1.json");
        var resultPath = Path.Combine(dir, "raw-result.v1.json");
        var checkpointPath = Path.Combine(dir, "raw-result.in-progress.v1.json");
        var arms = BuildArms(repo);
        var manifest = BuildManifest(arms);
        Directory.CreateDirectory(dir);
        if (!File.Exists(manifestPath))
        {
            WriteNew(manifestPath, manifest);
            WriteNew(preparedPath, BuildPreparedBodies(arms));
            Console.WriteLine("P6S-I/J manifest frozen: two exact single-pack bodies; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (!ManifestParity(manifestPath, manifest)) return Fail("p6sij: frozen manifest/body parity failed; no network call");
        if (!File.Exists(preparedPath))
        {
            WriteNew(preparedPath, BuildPreparedBodies(arms));
            Console.WriteLine("P6S-I/J exact prepared-body artifact added; rerun after reviewing parity. ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (!ManifestParity(preparedPath, BuildPreparedBodies(arms))) return Fail("p6sij: frozen raw-body artifact parity failed; no network call");
        if (File.Exists(resultPath)) return Fail("p6sij: immutable raw result already exists; stop before network");
        var authorized = args.Contains($"--confirm-p6sij={Confirm}");
        if (!authorized)
        {
            Console.WriteLine("P6S-I/J PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p6sij: OPENROUTER_API_KEY is not set; providerCalls=0");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var rows = Recover(checkpointPath, arms);
        if (rows is null) return Fail("p6sij: ambiguous checkpoint; no request sent");
        if (!File.Exists(checkpointPath)) AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
        foreach (var arm in arms.Skip(rows.Count))
        {
            // Persist before network. On an interrupted/ambiguous attempt this arm is never resent.
            AtomicWrite(checkpointPath, Checkpoint("IN_FLIGHT", rows, arm));
            OpenRouterExecutionObservation? response = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                response = await client.ExecuteObservedAsync(arm.Pack.ProviderBody, arm.Pack.MaxCompletionTokens,
                    arm.Pack.Request.SystemPrompt, arm.Pack.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            object parse;
            try
            {
                if (response is null) parse = new { parserAccepted = false, classification = "TRANSPORT_ERROR", error = transportError };
                else if (!string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
                    parse = new { parserAccepted = false, classification = "NON_STOP_FINISH", finishReason = response.FinishReason };
                else
                {
                    var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(arm.Pack, response.Content);
                    parse = new { parserAccepted = true, classification = parsed.Quarantined.Count == 0 ? "PARSED_NO_QUARANTINE" : "PARSED_WITH_QUARANTINE",
                        parsed.RawDecisionCount, acceptedBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count,
                        acceptedAfterOverlapQuarantine = parsed.Accepted.Count, headingBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.HEADING),
                        representationBeforeOverlapQuarantine = parsed.AcceptedBeforeOverlapQuarantine.Count(value => value.Kind == V5CandidateDecisionKind.REPRESENTATION),
                        quarantineCount = parsed.Quarantined.Count,
                        quarantineReasons = parsed.Quarantined.GroupBy(value => value.Reason, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count()) };
                }
            }
            catch (Exception exception) { parse = new { parserAccepted = false, classification = "PARSER_FATAL", error = exception.Message }; }
            rows.Add(JsonSerializer.SerializeToElement(new
            {
                arm = arm.Id, arm.ChangedDimension, packId = arm.Pack.PackId, documentId = "SRC-095",
                sourceUniverseSha256 = arm.Pack.Universe.Fingerprint,
                baselineProviderRequestHash = arm.BaselineProviderRequestHash,
                treatmentProviderRequestHash = arm.Pack.ProviderRequestHash, providerRequestBytes = arm.Pack.ProviderRequestBytes,
                maxCompletionTokens = arm.Pack.MaxCompletionTokens, transportAccepted = response is not null, transportError,
                finishReason = response?.FinishReason, usage = response?.Usage, retryCount = response?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = response?.SseEventCount ?? 0,
                rawSseSha256 = response is null ? null : Hash(response.RawSse), rawSse = response?.RawSse,
                rawResponseSha256 = response is null ? null : Hash(response.Content),
                rawResponseUtf8Bytes = response is null ? 0 : Encoding.UTF8.GetByteCount(response.Content), rawResponse = response?.Content, parse,
            }));
            AtomicWrite(checkpointPath, Checkpoint("READY", rows, null));
            Console.WriteLine($"[{rows.Count}/2] {arm.Id}: finish={response?.FinishReason ?? "n/a"}, retry={response?.RetryCount ?? 0}, ms={watch.ElapsedMilliseconds}");
        }
        if (rows.Count != 2) return Fail("p6sij: execution must finish exactly two attempts");
        WriteNew(resultPath, new
        {
            schemaVersion = "v5-p6sij-two-arm-toc-experiment-raw-v1", sourceManifest = $"{ArtifactRoot}/execution-manifest.v1.json",
            startedFromHead = GitHead(repo), providerCalls = 2, maximumAuthorizedProviderCalls = 2,
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = "none", responseFormat = "json_object" },
            retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED", rows,
        });
        File.Delete(checkpointPath);
        Console.WriteLine("P6S-I/J raw responses frozen. Offline scoring is a separate command; no retries were performed.");
        return 0;
    }

    public static int Score(string repo)
    {
        var root = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(root, "raw-result.v1.json");
        if (!File.Exists(resultPath)) return Fail("p6sij score: raw result missing");
        var arms = BuildArms(repo).ToDictionary(item => item.Id, StringComparer.Ordinal);
        using var raw = JsonDocument.Parse(File.ReadAllText(resultPath));
        if (raw.RootElement.GetProperty("providerCalls").GetInt32() != 2 || raw.RootElement.GetProperty("retry").GetInt32() != 0)
            return Fail("p6sij score: execution accounting mismatch");
        var p6shPath = Path.Combine(repo, P6SHPath.Replace('/', Path.DirectorySeparatorChar));
        var p6sdPath = Path.Combine(repo, P6SDPath.Replace('/', Path.DirectorySeparatorChar));
        using var p6sh = JsonDocument.Parse(File.ReadAllText(p6shPath));
        using var p6sd = JsonDocument.Parse(File.ReadAllText(p6sdPath));
        var targetRows = p6sh.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var scoredArms = new List<object>();
        foreach (var arm in arms.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var captureRow = raw.RootElement.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("arm").GetString() == arm.Id);
            var rawText = captureRow.GetProperty("rawResponse").GetString()!;
            if (Hash(rawText) != captureRow.GetProperty("rawResponseSha256").GetString()) return Fail($"p6sij score: raw response hash mismatch:{arm.Id}");
            var actualPack = arm.Pack;
            var actual = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(actualPack, rawText);
            using var treatmentJson = JsonDocument.Parse(rawText);
            var emittedIds = treatmentJson.RootElement.GetProperty("decisions").EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.String)
                .Select(value => value.GetProperty("candidate").GetString()!).ToHashSet(StringComparer.Ordinal);
            var baselineRow = p6sd.RootElement.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("documentId").GetString() == "SRC-095" && item.GetProperty("PackId").GetString() == arm.Pack.PackId);
            var baselineText = baselineRow.GetProperty("rawResponse").GetString()!;
            if (Hash(baselineText) != baselineRow.GetProperty("rawResponseSha256").GetString()) return Fail($"p6sij score: baseline raw hash mismatch:{arm.Id}");
            var baseline = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(arm.Pack, baselineText);
            var targetRowsForArm = targetRows.Where(item => item.GetProperty("PackId").GetString() == arm.Pack.PackId &&
                (arm.Id == "P6S-I" ? item.GetProperty("presentationStatus").GetString() == "PRESENTATION_CONTEXT_WEAK" : item.GetProperty("navigationSignalInFrozenRequest").GetBoolean())).ToArray();
            var expectedTargets = arm.Id == "P6S-I" ? 34 : 53;
            if (targetRowsForArm.Length != expectedTargets) return Fail($"p6sij score: reviewed target authority mismatch:{arm.Id}");
            var idSet = targetRowsForArm.Select(item => item.GetProperty("CandidateId").GetString()!).ToHashSet(StringComparer.Ordinal);
            var targetScores = targetRowsForArm.Select(row =>
            {
                var id = row.GetProperty("CandidateId").GetString()!;
                var baselineDecision = baseline.AcceptedBeforeOverlapQuarantine.SingleOrDefault(value => value.Candidate.Id == id);
                var treatmentDecision = actual.AcceptedBeforeOverlapQuarantine.SingleOrDefault(value => value.Candidate.Id == id);
                var baselineFinal = baseline.Accepted.SingleOrDefault(value => value.Candidate.Id == id);
                var treatmentFinal = actual.Accepted.SingleOrDefault(value => value.Candidate.Id == id);
                return new
                {
                    candidateId = id, identity = row.GetProperty("Identity").GetString(), text = row.GetProperty("candidateText").GetString(),
                    baseline = baselineDecision?.Kind.ToString() ?? "NO_PROPOSAL",
                    treatment = treatmentDecision?.Kind.ToString() ?? (emittedIds.Contains(id) ? "QUARANTINED_DECISION" : "NO_PROPOSAL"),
                    baselineFinalFailClosed = baselineFinal?.Kind.ToString() ?? (baselineDecision is null ? "NO_PROPOSAL" : "OVERLAP_QUARANTINED"),
                    treatmentFinalFailClosed = treatmentFinal?.Kind.ToString() ?? (treatmentDecision is null ? (emittedIds.Contains(id) ? "QUARANTINED_DECISION" : "NO_PROPOSAL") : "OVERLAP_QUARANTINED"),
                };
            }).ToArray();
            var before = targetScores.GroupBy(item => item.baseline, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var after = targetScores.GroupBy(item => item.treatment, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var final = targetScores.GroupBy(item => item.treatmentFinalFailClosed, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            scoredArms.Add(new
            {
                arm = arm.Id, packId = arm.Pack.PackId, targets = targetScores.Length,
                baselineSemanticDecisions = before, treatmentSemanticDecisions = after, treatmentFinalFailClosedDecisions = final,
                headingToRepresentation = targetScores.Count(item => item.baseline == "HEADING" && item.treatment == "REPRESENTATION"),
                representationToHeading = targetScores.Count(item => item.baseline == "REPRESENTATION" && item.treatment == "HEADING"),
                headingToNoProposal = targetScores.Count(item => item.baseline == "HEADING" && item.treatment == "NO_PROPOSAL"),
                noProposalToHeading = targetScores.Count(item => item.baseline == "NO_PROPOSAL" && item.treatment == "HEADING"),
                parserQuarantine = actual.Quarantined.Count, overlapQuarantine = actual.Quarantined.Count(item => item.Reason == "candidate-overlap-conflict"),
                targetScores,
            });
        }
        var artifact = new
        {
            schemaVersion = "v5-p6sij-two-arm-toc-offline-score-v1",
            authority = new { input = $"{ArtifactRoot}/raw-result.v1.json", hardNegativeTargets = P6SHPath, baselineResponses = P6SDPath,
                p6shSha256 = Hash(File.ReadAllBytes(p6shPath)), p6sdSha256 = Hash(File.ReadAllBytes(p6sdPath)),
                rawCaptureSha256 = Hash(File.ReadAllBytes(resultPath)), goldMutation = "NONE", runtimeChanged = false },
            measurement = "Only reviewed TOC false-positive candidates: compare prior P6S-D decision with each one-pack treatment. This is a targeted diagnostic, not full-pack or cohort F1.",
            arms = scoredArms,
        };
        WriteNew(Path.Combine(root, "offline-score.v1.json"), artifact);
        Console.WriteLine(JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static Arm[] BuildArms(string repo)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, Src095.Replace('/', Path.DirectorySeparatorChar)));
        var snapshotPath = Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, "SRC-095");
        var p6siBase = plan.Packs.Single(item => item.PackId == "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002");
        var p6sjBase = plan.Packs.Single(item => item.PackId == "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001");
        var treatmentI = ExpandReadOnlyContext(plan, p6siBase);
        var treatmentJ = ClarifySemanticPrompt(p6sjBase);
        return
        [
            new Arm("P6S-I", treatmentI,
                "contextOnlyEvidence source-order window only; system prompt, candidates, and relations unchanged", p6siBase.ProviderRequestHash),
            new Arm("P6S-J", treatmentJ,
                "system prompt semantic rule only; user payload, candidates, relations, and context unchanged", p6sjBase.ProviderRequestHash),
        ];
    }

    private static PdfCandidateAuthorityPreparedPack ExpandReadOnlyContext(PdfCandidateAuthorityDocumentPlan plan, PdfCandidateAuthorityPreparedPack pack)
    {
        using var doc = JsonDocument.Parse(pack.Request.UserMessage);
        var obj = JsonNode.Parse(pack.Request.UserMessage)!.AsObject();
        var source = plan.SourceAtoms.OrderBy(item => item.Ordinal).ToArray();
        var ownedOrdinals = pack.OwnedAliases.Select(alias => source.Single(item => item.Alias == alias).Ordinal).ToArray();
        var first = Array.FindIndex(source, item => item.Ordinal == ownedOrdinals.Min());
        var last = Array.FindIndex(source, item => item.Ordinal == ownedOrdinals.Max());
        if (first < 0 || last < first) throw new InvalidOperationException("p6sij-source-window-invalid");
        const int beforeCount = 64;
        const int afterCount = 16;
        var window = source.Skip(Math.Max(0, first - beforeCount)).Take(Math.Min(source.Length, last + afterCount + 1) - Math.Max(0, first - beforeCount))
            .Select(atom => new { page = atom.Page, text = atom.Text }).ToArray();
        obj["contextOnlyEvidence"] = JsonSerializer.SerializeToNode(window);
        var message = obj.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (!message.Contains("Table of Contents", StringComparison.Ordinal)) throw new InvalidOperationException("p6si-context-window-did-not-recover-neutral-toc-marker");
        var request = pack.Request with { UserMessage = message, UserMessageSha256 = Hash(message), UserMessageUtf8Bytes = Encoding.UTF8.GetByteCount(message) };
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBody(request, pack.MaxCompletionTokens);
        return pack with { Request = request, ProviderBody = body.PayloadBytes, ProviderRequestHash = body.Hash, ProviderRequestBytes = body.Bytes };
    }

    private static PdfCandidateAuthorityPreparedPack ClarifySemanticPrompt(PdfCandidateAuthorityPreparedPack pack)
    {
        var prompt = pack.Request.SystemPrompt + "\n\n" + PromptAddition;
        if (prompt.Contains("TABLE OF CONTENTS", StringComparison.OrdinalIgnoreCase) || prompt.Contains("NAVIGATION", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("p6sj-prompt-must-remain-document-generic");
        var request = pack.Request with { SystemPrompt = prompt, SystemPromptUtf8Bytes = Encoding.UTF8.GetByteCount(prompt) };
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBody(request, pack.MaxCompletionTokens);
        return pack with { Request = request, ProviderBody = body.PayloadBytes, ProviderRequestHash = body.Hash, ProviderRequestBytes = body.Bytes };
    }

    private static object BuildManifest(IReadOnlyList<Arm> arms) => new
    {
        schemaVersion = "v5-p6sij-two-arm-toc-experiment-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
        providerCalls = 0, goldRead = false, goldMutation = "NONE", runtimeChanged = false,
        sharedAdapter = "PdfCandidateAuthorityQualificationAdapter snapshot replay + same P6S protocol/parser/OpenRouter body builder",
        route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = "none", responseFormat = "json_object" },
        execution = new { maximumProviderCalls = 2, exactlyOnePerArm = true, retries = 0, repair = false, fallback = false, goldDuringRun = false },
        frozen = new { sourceDocument = "SRC-095", promptBase = V5CandidateDecisionProtocolV1.SystemPrompt, locatorAndCandidateUniverse = "P6S-D PACK_001/PACK_002 exact snapshot reconstruction", relations = "unchanged", parser = "unchanged" },
        arms = arms.Select(arm => new
        {
            arm.Id, arm.Pack.PackId, changedDimension = arm.ChangedDimension,
            baselineProviderRequestHash = arm.BaselineProviderRequestHash, treatmentProviderRequestHash = arm.Pack.ProviderRequestHash,
            userMessageSha256 = arm.Pack.Request.UserMessageSha256, systemPromptSha256 = Hash(arm.Pack.Request.SystemPrompt),
            providerRequestBytes = arm.Pack.ProviderRequestBytes, ownedAtoms = arm.Pack.OwnedAliases.Count,
            candidateCount = arm.Pack.Universe.Candidates.Count, relationCount = arm.Pack.Universe.Relations.Count,
            contextOnlyCount = JsonDocument.Parse(arm.Pack.Request.UserMessage).RootElement.GetProperty("contextOnlyEvidence").GetArrayLength(),
            maxCompletionTokens = arm.Pack.MaxCompletionTokens,
        }).ToArray(),
    };

    private static object BuildPreparedBodies(IReadOnlyList<Arm> arms) => new
    {
        schemaVersion = "v5-p6sij-prepared-request-bodies-v1",
        bodiesAreExactOpenRouterJsonUtf8 = true,
        arms = arms.Select(arm => new
        {
            arm.Id, arm.Pack.PackId,
            systemPrompt = arm.Pack.Request.SystemPrompt,
            userMessage = arm.Pack.Request.UserMessage,
            bodyBase64 = Convert.ToBase64String(arm.Pack.ProviderBody),
            bodySha256 = arm.Pack.ProviderRequestHash,
            bodyBytes = arm.Pack.ProviderRequestBytes,
        }).ToArray(),
    };

    private static List<JsonElement>? Recover(string path, IReadOnlyList<Arm> arms)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "READY") return null;
            var rows = root.GetProperty("rows").EnumerateArray().Select(item => item.Clone()).ToList();
            if (rows.Count > arms.Count) return null;
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].GetProperty("arm").GetString() != arms[i].Id || rows[i].GetProperty("treatmentProviderRequestHash").GetString() != arms[i].Pack.ProviderRequestHash || rows[i].GetProperty("retryCount").GetInt32() != 0) return null;
            return rows;
        }
        catch { return null; }
    }

    private static object Checkpoint(string status, IReadOnlyList<JsonElement> rows, Arm? inFlight) => new
    {
        schemaVersion = "v5-p6sij-checkpoint-v1", status, providerCallsCompletedAndPersisted = rows.Count,
        maximumProviderCalls = 2, retry = 0, repair = false, fallback = false, goldRead = false,
        inFlight = inFlight is null ? null : new { inFlight.Id, inFlight.Pack.PackId, inFlight.Pack.ProviderRequestHash }, rows,
    };

    private static bool ManifestParity(string path, object expected)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(JsonSerializer.Serialize(expected))); }
        catch { return false; }
    }
    private static string GitHead(string repo)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN";
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WriteNew(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void AtomicWrite(string path, object value)
    {
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, overwrite: true);
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
}
