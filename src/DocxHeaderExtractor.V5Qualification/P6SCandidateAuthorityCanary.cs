using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>P6S-C: four frozen candidate-only calls. Raw capture precedes every score.</summary>
internal static class P6SCandidateAuthorityCanary
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sc-canary";
    private const string Confirm = "yes-i-authorize-p6sc-candidate-authority-canary-4-calls";
    private static readonly (string Role, string Id, int PackOrdinal)[] Roles =
    [ ("MAX_CANDIDATE_UNIVERSE", "SRC-089", 4), ("OWNER_OMISSION_AREA", "SRC-095", 17), ("RETYPING_AREA", "SRC-095", 20), ("MULTIPART_AREA", "SRC-095", 11) ];

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var dir = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(dir, "execution-manifest.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var checkpoint = Path.Combine(dir, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpoint)) return Fail("p6sc: result/checkpoint already exists; stop before network");
        var items = Build(repo);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(dir); Write(manifestPath, new { schemaVersion = "v5-p6sc-candidate-authority-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED", maxResponseUtf8Bytes = PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, executionGate = new { maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, full31 = false }, rows = items.Select(item => new { item.Role, item.Pack.DocumentId, item.Pack.PackOrdinal, item.Pack.PackId, semanticRequestHash = item.Pack.Request.UserMessageSha256, item.Pack.ProviderRequestHash, item.Pack.ProviderRequestBytes, item.Pack.MaxCompletionTokens, sourceSha256 = item.Plan.SourceSha256, candidateUniverseFingerprint = item.Pack.Universe.Fingerprint }).ToArray() });
            Console.WriteLine("P6S-C manifest prepared. ProviderCalls=0; rerun with its explicit four-call sentinel."); return 0;
        }
        if (!args.Contains($"--confirm-p6sc={Confirm}")) { Console.WriteLine("P6S-C body parity is ready; no network without the frozen canary sentinel."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6sc: OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment(); options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none"; options.TransientRequestRetries = 0; options.Validate();
        var rows = new List<object>(); Write(checkpoint, new { providerCallsAlreadySent = 0, maximumProviderCalls = 4, goldRead = false, rows });
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? response = null; string? error = null; var clock = Stopwatch.StartNew();
            try { using var client = OpenRouterQualificationTransport.CreateOwned(options); response = await client.ExecuteObservedAsync(item.Pack.ProviderBody, item.Pack.MaxCompletionTokens, item.Pack.Request.SystemPrompt, item.Pack.Request.UserMessage); }
            catch (Exception ex) { error = ex.Message; }
            clock.Stop();
            object analysis;
            try { analysis = response is null ? new { classification = "TRANSPORT_ERROR", parserAccepted = false, error } : Analyze(item.Pack, response.Content, response.FinishReason, error); }
            catch (Exception ex) { analysis = new { classification = "PARSER_FATAL", parserAccepted = false, error = ex.Message }; }
            rows.Add(new { item.Role, item.Pack.DocumentId, item.Pack.PackOrdinal, item.Pack.PackId, semanticRequestHash = item.Pack.Request.UserMessageSha256, item.Pack.ProviderRequestHash, sourceSha256 = item.Plan.SourceSha256, finishReason = response?.FinishReason, transportError = error, usage = response?.Usage, latencyMs = clock.Elapsed.TotalMilliseconds, retryCount = response?.RetryCount ?? 0, rawSseSha256 = response is null ? null : Hash(response.RawSse), rawSse = response?.RawSse, rawResponseSha256 = response is null ? null : Hash(response.Content), rawResponse = response?.Content, analysis });
            Write(checkpoint, new { providerCallsAlreadySent = rows.Count, maximumProviderCalls = 4, goldRead = false, rows });
        }
        Write(resultPath, new { schemaVersion = "v5-p6sc-candidate-authority-result-v1", providerCalls = rows.Count, maximumAuthorizedProviderCalls = 4, route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = "none", responseFormat = "json_object" }, retry = 0, repair = false, fallback = false, goldRead = false, semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", rows });
        File.Delete(checkpoint); Console.WriteLine("P6S-C raw capture complete. Gold remains unread; score is a separate offline action."); return 0;
    }

    private static object Analyze(PdfCandidateAuthorityPreparedPack pack, string raw, string? finish, string? error)
    {
        if (!string.Equals(finish, "stop", StringComparison.OrdinalIgnoreCase)) return new { classification = "NON_TERMINAL_FINISH", parserAccepted = false, error };
        var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
        return new { classification = "PARSED", parserAccepted = true, rawDecisions = parsed.RawDecisionCount, accepted = parsed.Accepted.Count, headings = parsed.Headings.Count, representations = parsed.Accepted.Count - parsed.Headings.Count, quarantine = parsed.Quarantined.Count, overlapQuarantine = parsed.Quarantined.Count(item => item.Reason == "candidate-overlap-conflict"), acceptedEndpoints = parsed.Headings.Select(item => item.Candidate.Endpoint.Identity).ToArray(), quarantines = parsed.Quarantined };
    }

    private sealed record Item(string Role, PdfCandidateAuthorityDocumentPlan Plan, PdfCandidateAuthorityPreparedPack Pack);
    private static IReadOnlyList<Item> Build(string repo)
    {
        var plans = new Dictionary<string, PdfCandidateAuthorityDocumentPlan>(StringComparer.Ordinal);
        foreach (var id in Roles.Select(role => role.Id).Distinct(StringComparer.Ordinal))
        {
            var pdf = id == "SRC-089" ? "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf" : "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
            var sha = DocxHeaderExtractor.Core.Models.CanonicalSemanticSourceHash.Compute(Path.Combine(repo, pdf.Replace('/', Path.DirectorySeparatorChar)));
            plans[id] = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, "eval/a99-closed-loop/pdf-canonical-source-v1", sha + ".json"), id);
        }
        return Roles.Select(role => new Item(role.Role, plans[role.Id], plans[role.Id].Packs.Single(pack => pack.PackOrdinal == role.PackOrdinal))).ToArray();
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
}
