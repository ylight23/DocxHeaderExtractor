using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>One-shot H2 continuation probe derived only from frozen SRC-041 F1/G2A captures.</summary>
internal static class P6TEH2ContinuationCanary
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6te-src041-h2-challenge";
    private const string SourcePdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string F1Root = "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge";
    private const string Confirm = "yes-i-authorize-p6te-src041-h2-challenge-one-call";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Request(string System, string User, byte[] Body, string Hash, int Bytes, int MaxCompletion, string[] Edges);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var dir = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(dir, "h2-request-manifest.v1.json");
        var rawPath = Path.Combine(dir, "h2.raw-capture.v1.json");
        if (File.Exists(rawPath)) return Fail("p6te-h2: immutable result exists; no resend");
        var request = Build(repo);
        if (!Parity(manifestPath, request)) return Fail("p6te-h2: frozen request parity failed; no network");
        if (!args.Contains($"--confirm-p6te-h2={Confirm}"))
        {
            Console.WriteLine("P6T-E H2 PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6te-h2: OPENROUTER_API_KEY missing");
        var options = RemoteInferenceOptions.FromEnvironment(); options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none"; options.RequireJsonObjectResponse = true; options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1; options.ProviderTransportTimeoutSeconds = 300; options.Validate();

        OpenRouterExecutionObservation? response = null; string? error = null;
        try
        {
            using var client = OpenRouterHeaderExtractor.CreateOwned(options);
            response = await client.ExecuteObservedAsync(request.Body, request.MaxCompletion, request.System, request.User).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception.Message; }
        var parsed = response is not null && error is null && string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)
            ? Parse(request, response.Content) : null;
        WriteNew(rawPath, new
        {
            schemaVersion = "v5-p6te-src041-h2-raw-capture-v1", documentId = "SRC-041", providerCalls = 1,
            requestHash = request.Hash, providerRequestBytes = request.Bytes, issuedEdges = request.Edges.Length,
            finishReason = response?.FinishReason, retryCount = response?.RetryCount ?? 0,
            reasoningTokens = Usage(response?.Usage, "completion_tokens_details", "reasoning_tokens"),
            promptTokens = Usage(response?.Usage, "prompt_tokens"), completionTokens = Usage(response?.Usage, "completion_tokens"),
            rawSseSha256 = response is null ? null : Hash(response.RawSse), rawResponseSha256 = response is null ? null : Hash(response.Content),
            rawResponse = response?.Content, transportError = error,
            classification = parsed is null ? "H2_TRANSPORT_OR_LEDGER_FAILURE" : "H2_TOTAL_EDGE_LEDGER_ACCEPTED",
            parsed = parsed is null ? null : new { decisions = parsed, continues = parsed.Count(value => value.Boundary == "CONTINUES_STRUCTURAL_UNIT"), stops = parsed.Count(value => value.Boundary == "STOPS_STRUCTURAL_UNIT") },
            goldRead = false, goldMutation = "NONE", runtimeChanged = false, repair = false, fallback = false,
        });
        Console.WriteLine($"SRC-041 H2: {(parsed is null ? "FAILED" : "ACCEPTED")}, finish={response?.FinishReason ?? "n/a"}");
        return 0;
    }

    private static Request Build(string repo)
    {
        using var f1Raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, F1Root.Replace('/', Path.DirectorySeparatorChar), "f1.raw-capture.v1.json")));
        using var g2Raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, F1Root.Replace('/', Path.DirectorySeparatorChar), "g2a.raw-capture.v1.json")));
        var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, SourcePdf.Replace('/', Path.DirectorySeparatorChar)));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), "SRC-041");
        var pack = plan.Packs.Single(value => value.OwnedAliases.Contains("L3526:S0", StringComparer.Ordinal));
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        var functionLedger = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Raw.RootElement.GetProperty("rawResponse").GetString()!);
        if (g2Raw.RootElement.GetProperty("classification").GetString() != "G2A_TOTAL_LEDGER_ACCEPTED") throw new InvalidOperationException("p6te-h2-g2a-capture-invalid");
        var g2Anchor = g2Raw.RootElement.GetProperty("parsed").GetProperty("decisions").EnumerateArray().Single(value => value.GetProperty("alias").GetString() == "L3526:S0");
        if (g2Anchor.GetProperty("anchor").GetString() != "HAS_STRUCTURAL_EXTENT") throw new InvalidOperationException("p6te-h2-anchor-not-has");

        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = pack.OwnedAliases;
        var occurrenceByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var index = Array.IndexOf(owned.ToArray(), "L3526:S0");
        if (index < 0 || index + 3 >= owned.Count) throw new InvalidOperationException("p6te-h2-chain-not-owned");
        var aliases = owned.Skip(index).Take(4).ToArray();
        var rows = aliases.Select(alias => new { occurrence = occurrenceByAlias[alias], page = atoms[alias].Page, text = atoms[alias].Text, selectable = false }).ToArray();
        var edges = Enumerable.Range(0, 3).Select(i => new { anchor = "O4", left = occurrenceByAlias[aliases[i]], right = occurrenceByAlias[aliases[i + 1]], ordinal = i }).ToArray();
        const string system = """
            Judge only source-order continuation boundaries for an already-qualified structural anchor. For each issued edge, decide whether right continues the same local structural unit begun at anchor, or whether the unit stops before right.

            CONTINUES_STRUCTURAL_UNIT means left and right belong to the same exact local structural unit. STOPS_STRUCTURAL_UNIT means the unit begun at anchor ends before right. The edges are consecutive source occurrences; do not use similarity, hierarchy, candidates, spans, locators, or hypothetical text not issued in the request. Once an anchor stops, every later issued edge for that anchor must also be STOPS_STRUCTURAL_UNIT.

            Return exactly one JSON object: {"decisions":[{"anchor":"O4","left":"O4","right":"O5","boundary":"CONTINUES_STRUCTURAL_UNIT"}]}. Return exactly one decision for every issued edge. Echo only issued O# values. Do not output candidate IDs, source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
            """;
        var user = JsonSerializer.Serialize(new { protocolVersion = "v5-function-conditioned-continuation-boundary-1", anchors = new[] { new { anchor = "O4", occurrences = rows, edges } } });
        var request = new V5FreeHeadingRequestV1("v5-function-conditioned-continuation-boundary-1", system, user, Hash(user), Encoding.UTF8.GetByteCount(system), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
        return new Request(system, user, body.PayloadBytes, body.Hash, body.Bytes, pack.MaxCompletionTokens,
            edges.Select(edge => $"{edge.anchor}|{edge.left}|{edge.right}").ToArray());
    }

    private sealed record EdgeDecision(string Anchor, string Left, string Right, string Boundary);
    private static IReadOnlyList<EdgeDecision>? Parse(Request request, string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != request.Edges.Length) return null;
            var result = new Dictionary<string, EdgeDecision>(StringComparer.Ordinal);
            foreach (var item in decisions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 4 || !item.TryGetProperty("anchor", out var a) || !item.TryGetProperty("left", out var l) || !item.TryGetProperty("right", out var r) || !item.TryGetProperty("boundary", out var b)) return null;
                var value = new EdgeDecision(a.GetString()!, l.GetString()!, r.GetString()!, b.GetString()!);
                var key = $"{value.Anchor}|{value.Left}|{value.Right}";
                if (!request.Edges.Contains(key, StringComparer.Ordinal) || value.Boundary is not ("CONTINUES_STRUCTURAL_UNIT" or "STOPS_STRUCTURAL_UNIT") || !result.TryAdd(key, value)) return null;
            }
            return result.Count == request.Edges.Length ? request.Edges.Select(key => result[key]).ToArray() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Parity(string path, Request request)
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement; return root.GetProperty("status").GetString() == "PREPARED_NOT_AUTHORIZED" && root.GetProperty("execution").GetProperty("maximumProviderCalls").GetInt32() == 1 && root.GetProperty("request").GetProperty("providerBodySha256").GetString() == request.Hash && root.GetProperty("request").GetProperty("userMessageSha256").GetString() == Hash(request.User); }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
    private static int? Usage(JsonElement? usage, params string[] path) { if (usage is not { ValueKind: JsonValueKind.Object } current) return null; foreach (var key in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null; return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null; }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6te-h2-immutable-capture-exists"); File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
