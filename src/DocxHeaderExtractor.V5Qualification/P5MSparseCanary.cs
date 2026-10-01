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

/// <summary>Runs the four frozen V3.1 sparse bodies exactly once, without Gold, retry, repair or fallback.</summary>
internal static class P5MSparseCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-p5m-v31-sparse-canary-4-frozen-calls";
    private const string Root = "artifacts/v5-p5m-v31-sparse-canary-manifest";
    private const string ManifestFile = "execution-manifest.v1.json";
    private static readonly (string Role, string Doc, string Pdf, string Pack)[] Roles =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    private sealed record Resolved(string Role, string DocumentId, string PdfPath, V5PackedDecisionRequestV3 Pack,
        V5ComposedSemanticDecisionRequestV3 Request, byte[] Body, string ProviderHash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p5m-v31-sparse-canary={ConfirmSentinel}");
        var artifact = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(artifact, "result.v1.json");
        var inProgressPath = Path.Combine(artifact, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(inProgressPath)) return Fail("p5m: result or execution checkpoint already exists; stop before network");
        var manifestPath = Path.Combine(artifact, ManifestFile);
        if (!File.Exists(manifestPath)) return Fail("p5m: missing frozen manifest");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidOperationException("p5m-manifest-invalid");
        if (manifest["schemaVersion"]?.GetValue<string>() != "v5-p5m-v31-sparse-provider-execution-manifest-v1" ||
            manifest["status"]?.GetValue<string>() != "PREPARED_AUTHORIZED" || manifest["requestCount"]?.GetValue<int>() != 4 ||
            manifest["providerCalls"]?.GetValue<int>() != 0 || manifest["goldRead"]?.GetValue<bool>() != false)
            return Fail("p5m: manifest does not meet four-call authorization prerequisites");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var resolved = Resolve(repo, manifest, contract, envelope);
        if (resolved.Count != 4) return Fail("p5m: resolved call count is not exactly four");
        Console.WriteLine("p5m parity PASS: four V3.1 sparse bodies reproduce byte-for-byte.");
        if (!authorized) { Console.WriteLine("p5m: providerCalls=0. Explicit authorization sentinel required."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p5m: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model; options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning; options.TransientRequestRetries = 0; options.Validate();
        Directory.CreateDirectory(artifact);
        var results = new List<object>();
        foreach (var item in resolved)
        {
            OpenRouterExecutionObservation? observation = null; string? error = null;
            var timer = Stopwatch.StartNew();
            try { using var client = OpenRouterHeaderExtractor.CreateOwned(options); observation = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Request.Prompt); }
            catch (Exception ex) { error = ex.Message; }
            timer.Stop();
            var analysis = Analyze(item, contract, observation?.Content, observation?.FinishReason, error);
            results.Add(new { role = item.Role, documentId = item.DocumentId, packId = item.Pack.PackId, semanticRequestHash = item.Request.RequestHash,
                providerRequestHash = item.ProviderHash, decisionCountExpected = item.Pack.OwnedAliases.Count, maxResponseUtf8Bytes = item.Request.ResponseBounds.MaxResponseUtf8Bytes,
                maxCompletionTokens = item.Pack.MaxCompletionTokens, transportError = error, finishReason = observation?.FinishReason, usage = observation?.Usage,
                retryCount = observation?.RetryCount ?? 0, latencyMs = timer.Elapsed.TotalMilliseconds, sseEventCount = observation?.SseEventCount ?? 0,
                sseRawSha256 = observation is null ? null : Hash(observation.RawSse), sseRaw = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content), rawResponseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content), rawResponse = observation?.Content, response = analysis });
            Write(inProgressPath, new { schemaVersion = "v5-p5m-v31-sparse-provider-canary-checkpoint-v1", providerCallsAlreadySent = results.Count,
                maximumAuthorizedProviderCalls = 4, stopBeforeNetworkOnSubsequentInvocation = true, results });
            Console.WriteLine($"[{item.Role}] {analysis.Classification} decisions={analysis.DecisionCountActual}/{item.Pack.OwnedAliases.Count}");
        }
        Write(resultPath, new { schemaVersion = "v5-p5m-v31-sparse-provider-canary-result-v1", head = GitHead(repo), sourceManifest = $"{Root}/{ManifestFile}", protocol = "v5-source-backed-decision-3.1", model = envelope.Model, provider = envelope.Provider, reasoning = envelope.Reasoning, responseFormat = envelope.ResponseFormat, providerCalls = results.Count, maximumAuthorizedProviderCalls = 4, goldRead = false, semanticScore = "NOT_RUN", semanticRetries = 0, responseRepairApplied = false, fallbackProviderCalls = 0, stopGate = "CLOSED_AFTER_4_CALLS", results });
        File.Delete(inProgressPath);
        return 0;
    }

    private static List<Resolved> Resolve(string repo, JsonObject manifest, DocumentTaskContract contract, V5ProviderEnvelope envelope)
    {
        var built = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(); var resolved = new List<Resolved>();
        foreach (var role in Roles)
        {
            if (!built.TryGetValue(role.Doc, out var packs)) { packs = V5PdfPreflightBuilder.BuildV3(Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar)), role.Doc, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope); built[role.Doc] = packs; }
            var pack = packs.Single(x => x.PackId == role.Pack); var request = V5SemanticSparseDecisionComposerV3_1.Compose(contract, pack.Packet);
            var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, pack.MaxCompletionTokens, envelope);
            var row = manifest["rows"]?.AsArray().SingleOrDefault(x => x?["Role"]?.GetValue<string>() == role.Role) ?? throw new InvalidOperationException($"p5m-row-missing:{role.Role}");
            var relative = row["providerBodyFile"]?.GetValue<string>() ?? throw new InvalidOperationException("p5m-body-file-missing");
            var frozen = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.Combine(repo, Root, ManifestFile))!, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!frozen.AsSpan().SequenceEqual(body.PayloadBytes) || Hash(frozen) != body.Hash || row["semanticRequestHash"]?.GetValue<string>() != request.RequestHash || row["providerRequestHash"]?.GetValue<string>() != body.Hash || row["ownedCount"]?.GetValue<int>() != pack.OwnedAliases.Count || pack.OwnedAliases.Count != 96) throw new InvalidOperationException($"p5m-parity-failed:{role.Role}");
            resolved.Add(new(role.Role, role.Doc, Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar)), pack, request, frozen, body.Hash));
        }
        return resolved;
    }

    private sealed record Analysis(string Classification, int? DecisionCountActual, bool JsonComplete, bool ParserAccepted, bool BinderAccepted, int ClaimsProduced, int Bound, int Refused, int EmptyDecisionCount, string? Error);
    private static Analysis Analyze(Resolved item, DocumentTaskContract contract, string? raw, string? finish, string? transportError)
    {
        if (transportError is not null || raw is null) return new("TRANSPORT_ERROR", null, false, false, false, 0, 0, 0, 0, transportError);
        try { using var json = JsonDocument.Parse(raw); var root = json.RootElement; var count = root.TryGetProperty("decisions", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : (int?)null;
            try { var parsed = V5SemanticSparseDecisionContractV3_1.Parse(root, contract, item.Pack.OwnedAliases.Count, item.Pack.Packet.ContextOnlyEvidence.Count); var atoms = V5PdfPreflightBuilder.LoadAtoms(item.PdfPath); var binding = V5SemanticSparseDecisionContractV3_1.Bind(item.Request.RequestHash, parsed, contract, item.Pack.Packet.SubjectEvidence, item.Pack.Packet.ContextOnlyEvidence, atoms, ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases)); var claims = parsed.Decisions.Sum(x => x.Claims.Count); var empty = parsed.Decisions.Count(x => x.Claims.Count == 0); return new(string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) ? "LENGTH" : "CONTRACT_VALID", count, true, true, binding.Binding is not null, claims, binding.Bound.Count, binding.Refusals.Count, empty, null); }
            catch (Exception ex) { return new("CONTRACT_INVALID", count, true, false, false, 0, 0, 0, 0, ex.Message); } }
        catch (JsonException ex) { return new("JSON_INVALID", null, false, false, false, 0, 0, 0, 0, ex.Message); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string value) { Console.Error.WriteLine(value); return 1; }
    private static string? GitHead(string repo) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var x = p!.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return p.ExitCode == 0 ? x : null; } catch { return null; } }
}
