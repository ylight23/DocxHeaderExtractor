using DocxHeaderExtractor.DocumentProcessing.Provenance;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>Two-call, conflict-only probe for adjudicating G2A/H2 disagreements; no Gold during capture.</summary>
internal static class P6TH3ConflictAdjudicationCanary
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6th3-conflict-adjudication-v2";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Confirm = "yes-i-authorize-p6th3-two-conflict-calls";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Spec(string Id, string Pdf, string Pack, string Anchor, string Left, string Right,
        string F1Capture, string G2Capture, string H2Capture);

    private sealed record Built(Spec Spec, string User, string System, byte[] Body, string BodyHash, int BodyBytes,
        int MaxCompletion, string F1RawSha, string G2RawSha, string H2RawSha, string SourceSha);

    private static readonly Spec[] Specs =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf",
            "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", "O17", "O18", "O19",
            "artifacts/v5-p6t-function-membership/p6tf1-preflight/retry-src089-result.v1.json",
            "artifacts/v5-p6t-function-membership/p6tg2a-anchor-existence-canary-20261004/SRC-089.raw-capture.v1.json",
            "artifacts/v5-p6t-function-membership/p6th2-function-conditioned-continuation-canary-20261004/SRC-089.raw-capture.v1.json"),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf",
            "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_060", "O4", "O4", "O5",
            "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge/f1.raw-capture.v1.json",
            "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge/g2a.raw-capture.v1.json",
            "artifacts/v5-p6t-function-membership/p6te-src041-h2-challenge/h2.raw-capture.v1.json"),
    ];

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var built = Specs.Select(spec => Build(repo, spec)).ToArray();
        var manifestPaths = built.Select(item => Path.Combine(directory, $"{item.Spec.Id}.request-manifest.v1.json")).ToArray();
        if (manifestPaths.All(path => !File.Exists(path)))
        {
            foreach (var item in built) WriteNew(Path.Combine(directory, $"{item.Spec.Id}.request-manifest.v1.json"), new
            {
                schemaVersion = "v5-p6th3-conflict-adjudication-preflight-v2", status = "PREPARED_NOT_AUTHORIZED",
                documentId = item.Spec.Id,
                sourceAuthority = new { sourceSha256 = item.SourceSha, f1RawResponseSha256 = item.F1RawSha, g2aRawResponseSha256 = item.G2RawSha, h2RawResponseSha256 = item.H2RawSha },
                conflict = new { id = "X1", anchor = item.Spec.Anchor, left = item.Spec.Left, right = item.Spec.Right },
                request = new { protocolVersion = "v5-function-anchor-continuation-conflict-adjudication-2", systemPromptSha256 = Hash(item.System), userMessageSha256 = Hash(item.User), providerBodySha256 = item.BodyHash, providerBodyBytes = item.BodyBytes },
                execution = new { providerCalls = 0, maximumProviderCalls = 1, retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", runtimeChanged = false },
            });
            Console.WriteLine("P6T-H3 manifests frozen provider-free; ProviderCalls=0, GoldRead=false. Review manifests before authorization.");
            return 0;
        }
        if (manifestPaths.Any(path => !File.Exists(path))) return Fail("p6th3: partial manifest set; no network");
        if (!Parity(repo, built)) return Fail("p6th3: frozen conflict request parity failed; no network");
        if (!args.Contains($"--confirm-p6th3={Confirm}"))
        {
            Console.WriteLine("P6T-H3 PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (built.Any(item => File.Exists(Path.Combine(directory, $"{item.Spec.Id}.raw-capture.v1.json"))))
            return Fail("p6th3: immutable capture exists; no resend");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6th3: OPENROUTER_API_KEY missing");

        var options = RemoteInferenceOptions.FromEnvironment(); options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true; options.TransientRequestRetries = 0;
        options.ProviderTransportTimeoutSeconds = 300; options.Validate();
        using var client = OpenRouterQualificationTransport.CreateOwned(options);
        foreach (var item in built)
        {
            OpenRouterExecutionObservation? response = null; string? error = null;
            try { response = await client.ExecuteObservedAsync(item.Body, item.MaxCompletion, item.System, item.User).ConfigureAwait(false); }
            catch (Exception exception) { error = exception.Message; }
            var parsed = response is not null && error is null && string.Equals(response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)
                ? Parse(response.Content) : null;
            var path = Path.Combine(directory, $"{item.Spec.Id}.raw-capture.v1.json");
            if (File.Exists(path)) return Fail($"p6th3: immutable capture exists:{item.Spec.Id}");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-p6th3-conflict-adjudication-raw-v2", documentId = item.Spec.Id,
                providerCalls = 1, model = options.Model, provider = "Alibaba", reasoningRequested = true,
                reasoningExecutionConfirmed = response?.Usage is { } usage && Usage(usage, "completion_tokens_details", "reasoning_tokens") is > 0,
                sourceSha256 = item.SourceSha, sourceContextSha256 = Hash(item.User), requestHash = item.BodyHash,
                requestBytes = item.BodyBytes, upstreamF1RawSha256 = item.F1RawSha,
                upstreamG2ARawSha256 = item.G2RawSha, upstreamH2RawSha256 = item.H2RawSha,
                finishReason = response?.FinishReason, retryCount = response?.RetryCount ?? 0,
                reasoningTokens = Usage(response?.Usage, "completion_tokens_details", "reasoning_tokens"),
                promptTokens = Usage(response?.Usage, "prompt_tokens"), completionTokens = Usage(response?.Usage, "completion_tokens"),
                rawSseSha256 = response is null ? null : Hash(response.RawSse),
                rawResponseSha256 = response is null ? null : Hash(response.Content), rawResponse = response?.Content,
                transportError = error, classification = parsed is null ? "H3_TRANSPORT_OR_LEDGER_FAILURE" : "H3_SINGLE_CONFLICT_LEDGER_ACCEPTED",
                parsed = parsed is null ? null : new { conflictId = parsed.ConflictId, resolution = parsed.Resolution },
                goldRead = false, goldMutation = "NONE", repair = false, fallback = false, runtimeChanged = false,
            }, Json) + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine($"{item.Spec.Id} H3: {(parsed is null ? "FAILED" : "ACCEPTED")}, finish={response?.FinishReason ?? "n/a"}");
        }
        return 0;
    }

    private static Built Build(string repo, Spec spec)
    {
        string PathOf(string relative) => Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        using var f1Capture = JsonDocument.Parse(File.ReadAllText(PathOf(spec.F1Capture)));
        using var g2Capture = JsonDocument.Parse(File.ReadAllText(PathOf(spec.G2Capture)));
        using var h2Capture = JsonDocument.Parse(File.ReadAllText(PathOf(spec.H2Capture)));
        var f1Raw = RawResponse(f1Capture.RootElement);
        var g2Raw = RawResponse(g2Capture.RootElement);
        var h2Raw = RawResponse(h2Capture.RootElement);
        using var f1Response = JsonDocument.Parse(f1Raw); using var g2Response = JsonDocument.Parse(g2Raw); using var h2Response = JsonDocument.Parse(h2Raw);
        var f1Rows = f1Response.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(
            row => row.GetProperty("occurrence").GetString()!, row => row.GetProperty("function").GetString()!, StringComparer.Ordinal);
        var g2Rows = g2Response.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(
            row => row.GetProperty("primary").GetString()!, row => row.GetProperty("anchor").GetString()!, StringComparer.Ordinal);
        var h2Rows = h2Response.RootElement.GetProperty("decisions").EnumerateArray().ToArray();
        var h2Edge = h2Rows.Single(row => row.GetProperty("anchor").GetString() == spec.Anchor &&
            row.GetProperty("left").GetString() == spec.Left && row.GetProperty("right").GetString() == spec.Right);
        if (!g2Rows.TryGetValue(spec.Right, out var rightAnchor) || rightAnchor != "HAS_STRUCTURAL_EXTENT" ||
            h2Edge.GetProperty("boundary").GetString() != "CONTINUES_STRUCTURAL_UNIT" ||
            !f1Rows.TryGetValue(spec.Right, out var rightFunction) || rightFunction != "ESTABLISHES_STRUCTURE")
            throw new InvalidOperationException($"p6th3-source-captures-no-longer-form-issued-conflict:{spec.Id}");

        var sourceSha = CanonicalSemanticSourceHash.Compute(PathOf(spec.Pdf));
        var snapshot = PathOf($"{SnapshotRoot}/{sourceSha}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshot, spec.Id);
        var pack = plan.Packs.Single(value => value.PackId == spec.Pack);
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        var occurrences = f1.Request.Occurrences;
        var indexById = occurrences.Select((value, index) => (value.Id, index)).ToDictionary(value => value.Id, value => value.index, StringComparer.Ordinal);
        if (!indexById.ContainsKey(spec.Anchor) || !indexById.ContainsKey(spec.Left) || !indexById.ContainsKey(spec.Right))
            throw new InvalidOperationException($"p6th3-conflict-occurrence-not-in-owned-pack:{spec.Id}");
        var min = Math.Max(0, Math.Min(indexById[spec.Anchor], Math.Min(indexById[spec.Left], indexById[spec.Right])) - 1);
        var max = Math.Min(occurrences.Count - 1, Math.Max(indexById[spec.Anchor], Math.Max(indexById[spec.Left], indexById[spec.Right])) + 1);
        var context = occurrences.Skip(min).Take(max - min + 1).Select((value, offset) => new
        {
            occurrence = value.Id, sourceOrder = min + offset,
            page = value.Atom.Page, text = value.Atom.Text,
            function = f1Rows.GetValueOrDefault(value.Id, "OTHER"),
            g2aAnchor = g2Rows.GetValueOrDefault(value.Id, "NOT_ISSUED"),
        }).ToArray();
        const string system = """
            Return the decision as JSON. Resolve only the issued disagreement about whether RIGHT starts a new local structural unit or continues the unit containing LEFT. Use the source-order text and neutral page/order facts in the bounded context. The supplied upstream labels are fallible evidence, not truth; do not automatically prefer either upstream judgment. Do not infer a boundary from an anchor label alone.

            Return exactly {"decisions":[{"conflict":"X1","resolution":"NEW_STRUCTURAL_UNIT"}]}. Return one decision for the issued conflict. Allowed resolution values are NEW_STRUCTURAL_UNIT, CONTINUES_LEFT_STRUCTURAL_UNIT, and UNRESOLVED. UNRESOLVED means the evidence is insufficient; it must not be coerced to either other value. No rationale, candidate, span, locator, hierarchy, confidence, or extra properties.
            """;
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = "v5-function-anchor-continuation-conflict-adjudication-2",
            conflicts = new[] { new
            {
                id = "X1", anchor = spec.Anchor, left = spec.Left, right = spec.Right,
                upstream = new
                {
                    f1RightFunction = f1Rows[spec.Right],
                    g2aRightAnchor = rightAnchor,
                    h2Boundary = h2Edge.GetProperty("boundary").GetString(),
                },
                boundedContext = context,
            } },
        });
        var request = new V5FreeHeadingRequestV1("v5-function-anchor-continuation-conflict-adjudication-2", system, user,
            Hash(user), Encoding.UTF8.GetByteCount(system), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
        return new Built(spec, user, system, body.PayloadBytes, body.Hash, body.Bytes, pack.MaxCompletionTokens,
            Hash(f1Raw), Hash(g2Raw), Hash(h2Raw), sourceSha);
    }

    private sealed record Decision(string ConflictId, string Resolution);
    private static Decision? Parse(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1) return null;
            var row = rows[0];
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 || !row.TryGetProperty("conflict", out var id) || !row.TryGetProperty("resolution", out var resolution) || id.GetString() != "X1") return null;
            var result = resolution.GetString(); return result is "NEW_STRUCTURAL_UNIT" or "CONTINUES_LEFT_STRUCTURAL_UNIT" or "UNRESOLVED" ? new Decision("X1", result) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Parity(string repo, IReadOnlyList<Built> built)
    {
        try
        {
            foreach (var item in built)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar), $"{item.Spec.Id}.request-manifest.v1.json")));
                var root = manifest.RootElement;
                if (root.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" || root.GetProperty("request").GetProperty("providerBodySha256").GetString() != item.BodyHash || root.GetProperty("request").GetProperty("userMessageSha256").GetString() != Hash(item.User)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static string RawResponse(JsonElement root) => root.TryGetProperty("rawResponse", out var raw) && raw.ValueKind == JsonValueKind.String
        ? raw.GetString()! : root.GetProperty("row").GetProperty("rawResponse").GetString()!;
    private static int? Usage(JsonElement? usage, params string[] path) { if (usage is not { ValueKind: JsonValueKind.Object } current) return null; foreach (var key in path) if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null; return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null; }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6th3-preflight-immutable-exists"); File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
