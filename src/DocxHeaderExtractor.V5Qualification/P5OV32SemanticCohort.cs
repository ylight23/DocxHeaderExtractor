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

/// <summary>Exactly four frozen V3.2 calls. This stage records survival/binding only; Gold is never read.</summary>
internal static class P5OV32SemanticCohort
{
    public const string ConfirmSentinel = "yes-i-authorize-p5o-v32-semantic-cohort-4-frozen-calls";
    private const string Root = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private static readonly (string Role, string Doc, string Pdf, string Pack)[] Roles =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];
    private sealed record Item(string Role, string Doc, string Pdf, V5PackedDecisionRequestV3 Pack, V5ComposedSemanticDecisionRequestV3 Request, byte[] Body, string Hash);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p5o-v32-semantic-cohort={ConfirmSentinel}");
        var artifact = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var result = Path.Combine(artifact, "result.v1.json"); var checkpoint = Path.Combine(artifact, "result.in-progress.v1.json");
        if (File.Exists(result) || File.Exists(checkpoint)) return Fail("p5o: result or checkpoint exists; stop before network");
        var manifestPath = Path.Combine(artifact, "execution-manifest.v1.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidOperationException("p5o-manifest-invalid");
        if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" || manifest["protocol"]?.GetValue<string>() != V5Protocol.ClaimSchemaVersionV3_2 || manifest["requestCount"]?.GetValue<int>() != 4 || manifest["providerCalls"]?.GetValue<int>() != 0 || manifest["goldRead"]?.GetValue<bool>() != false) return Fail("p5o: manifest gate invalid");
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var items = Resolve(repo, artifact, manifest, contract, envelope);
        Console.WriteLine("p5o parity PASS: four V3.2 bodies reproduce byte-for-byte.");
        if (!authorized) { Console.WriteLine("p5o: providerCalls=0; explicit authorization sentinel required."); return 0; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p5o: OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment(); options.Model = envelope.Model; options.OpenRouterProviderRoute = envelope.Provider; options.OpenRouterReasoningEffort = envelope.Reasoning; options.TransientRequestRetries = 0; options.Validate();
        var rows = new List<object>(); Directory.CreateDirectory(artifact);
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observed = null; string? transportError = null; var watch = Stopwatch.StartNew();
            try { using var client = OpenRouterHeaderExtractor.CreateOwned(options); observed = await client.ExecuteObservedAsync(item.Body, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Request.Prompt); }
            catch (Exception ex) { transportError = ex.Message; }
            watch.Stop(); var analysis = Analyze(item, contract, observed?.Content, observed?.FinishReason, transportError);
            rows.Add(new { role = item.Role, documentId = item.Doc, packId = item.Pack.PackId, semanticRequestHash = item.Request.RequestHash, providerRequestHash = item.Hash, decisionCountExpected = item.Pack.OwnedAliases.Count, maxResponseUtf8Bytes = item.Request.ResponseBounds.MaxResponseUtf8Bytes, maxCompletionTokens = item.Pack.MaxCompletionTokens, transportError, finishReason = observed?.FinishReason, usage = observed?.Usage, retryCount = observed?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observed?.SseEventCount ?? 0, sseRawSha256 = observed is null ? null : Hash(observed.RawSse), sseRaw = observed?.RawSse, rawResponseSha256 = observed is null ? null : Hash(observed.Content), rawResponseBytes = observed is null ? 0 : Encoding.UTF8.GetByteCount(observed.Content), rawResponse = observed?.Content, response = analysis });
            Write(checkpoint, new { schemaVersion = "v5-p5o-v32-semantic-cohort-checkpoint-v1", providerCallsAlreadySent = rows.Count, maximumAuthorizedProviderCalls = 4, stopBeforeNetworkOnSubsequentInvocation = true, rows });
            Console.WriteLine($"[{item.Role}] {analysis.Classification} usable={analysis.UsableSparseDecisions} bound={analysis.BoundClaims} refused={analysis.BinderRefusals}");
        }
        Write(result, new { schemaVersion = "v5-p5o-v32-semantic-cohort-result-v1", head = GitHead(repo), sourceManifest = $"{Root}/execution-manifest.v1.json", protocol = V5Protocol.ClaimSchemaVersionV3_2, model = envelope.Model, provider = envelope.Provider, reasoning = envelope.Reasoning, providerCalls = rows.Count, maximumAuthorizedProviderCalls = 4, goldRead = false, semanticScore = "NOT_RUN", semanticRetries = 0, responseRepairApplied = false, fallbackProviderCalls = 0, stopGate = "CLOSED_AFTER_4_CALLS", rows }); File.Delete(checkpoint); return 0;
    }

    private static List<Item> Resolve(string repo, string artifact, JsonObject manifest, DocumentTaskContract contract, V5ProviderEnvelope envelope)
    {
        var cache = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(); var result = new List<Item>();
        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.Doc, out var packs)) { packs = V5PdfPreflightBuilder.BuildV3(Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar)), role.Doc, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope); cache[role.Doc] = packs; }
            var pack = packs.Single(x => x.PackId == role.Pack); var request = V5SemanticSparseDecisionComposerV3_1.Compose(contract, pack.Packet); var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, pack.MaxCompletionTokens, envelope);
            var row = manifest["rows"]?.AsArray().SingleOrDefault(x => x?["Role"]?.GetValue<string>() == role.Role) ?? throw new InvalidOperationException($"p5o-row-missing:{role.Role}"); var relative = row["providerBodyFile"]?.GetValue<string>() ?? throw new InvalidOperationException("p5o-body-missing"); var frozen = File.ReadAllBytes(Path.Combine(artifact, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!frozen.AsSpan().SequenceEqual(body.PayloadBytes) || Hash(frozen) != body.Hash || row["semanticRequestHash"]?.GetValue<string>() != request.RequestHash || row["providerRequestHash"]?.GetValue<string>() != body.Hash || pack.OwnedAliases.Count != 96) throw new InvalidOperationException($"p5o-parity-failed:{role.Role}"); result.Add(new(role.Role, role.Doc, Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar)), pack, request, frozen, body.Hash));
        }
        return result;
    }

    private sealed record Analysis(string Classification, int? DecisionCountActual, bool TransportValid, bool ParserValid, bool BinderExecuted, int UsableSparseDecisions, int ClaimsProduced, int BoundClaims, int BinderRefusals, string? Error);
    private static Analysis Analyze(Item item, DocumentTaskContract contract, string? raw, string? finish, string? error)
    {
        if (error is not null || raw is null) return new("TRANSPORT_ERROR", null, false, false, false, 0, 0, 0, 0, error);
        try { using var doc = JsonDocument.Parse(raw); var root = doc.RootElement; var actual = root.TryGetProperty("decisions", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : (int?)null;
            try { var parsed = V5SemanticSparseDecisionContractV3_1.Parse(root, contract, item.Pack.OwnedAliases.Count, item.Pack.Packet.ContextOnlyEvidence.Count); var binding = V5SemanticSparseDecisionContractV3_1.Bind(item.Request.RequestHash, parsed, contract, item.Pack.Packet.SubjectEvidence, item.Pack.Packet.ContextOnlyEvidence, V5PdfPreflightBuilder.LoadAtoms(item.Pdf), ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases)); var usable = parsed.Decisions.Count(x => x.Claims.Count > 0) - binding.Refusals.Keys.Count(key => key.StartsWith("sparse-decision-", StringComparison.Ordinal)); var claims = parsed.Decisions.Sum(x => x.Claims.Count); return new(string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) ? "LENGTH" : binding.Bound.Count > 0 ? "PARSER_VALID_WITH_USABLE_CLAIMS" : "PARSER_VALID_NO_USABLE_CLAIMS", actual, true, true, true, Math.Max(0, usable), claims, binding.Bound.Count, binding.Refusals.Count, null); }
            catch (Exception ex) { return new("CONTRACT_INVALID", actual, true, false, false, 0, 0, 0, 0, ex.Message); } }
        catch (JsonException ex) { return new("JSON_INVALID", null, true, false, false, 0, 0, 0, 0, ex.Message); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes)); private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var output = p!.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return p.ExitCode == 0 ? output : null; } catch { return null; } }
}
