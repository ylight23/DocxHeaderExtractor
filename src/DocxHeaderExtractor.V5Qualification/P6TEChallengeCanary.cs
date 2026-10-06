using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Cross-PDF E-challenge qualification. F1 is captured first; only a valid total F1
/// ledger permits construction and one-shot execution of the downstream G2A request. No Gold is
/// read by this runner and no decision is repaired or retried.
/// </summary>
internal static class P6TEChallengeCanary
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Spec(string DocumentId, string Root, string SourcePdf, string TargetAlias, string Command);
    private static readonly Spec Doc0256 = new("DOC-0256", "artifacts/v5-p6t-function-membership/p6te-doc0256-e-challenge",
        "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "L0001:S0", "--p6te-doc0256-e-challenge");
    private static readonly Spec Doc0252 = new("DOC-0252", "artifacts/v5-p6t-function-membership/p6te-doc0252-e-challenge",
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "L0514:S0", "--p6te-doc0252-e-challenge");
    private static readonly Spec Src041 = new("SRC-041", "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge",
        "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "L3527:S0", "--p6te-src041-e-challenge");

    private sealed record G2Occurrence(string Id, string Alias, int Page, string Text, string Function, string? PreviousId, int? PreviousPage, string? PreviousText, string? NextId, int? NextPage, string? NextText);
    private sealed record G2Request(string SystemPrompt, string UserMessage, byte[] Body, string BodyHash, int BodyBytes, int MaxCompletionTokens, IReadOnlyList<G2Occurrence> Issued);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var spec = args.Contains(Doc0252.Command) ? Doc0252 : args.Contains(Src041.Command) ? Src041 : Doc0256;
        var dir = Path.Combine(repo, spec.Root.Replace('/', Path.DirectorySeparatorChar));
        var f1Path = Path.Combine(dir, "f1.raw-capture.v1.json");
        var g2Path = Path.Combine(dir, "g2a.raw-capture.v1.json");
        var receiptPath = Path.Combine(dir, "execution-receipt.v1.json");
        if (File.Exists(f1Path) || File.Exists(g2Path) || File.Exists(receiptPath)) return Fail("p6te: immutable capture already exists; no automatic rerun");
        if (!File.Exists(Path.Combine(dir, "f1-request-manifest.v1.json"))) return Fail("p6te: frozen F1 manifest missing");

        var (plan, f1) = Build(repo, spec);
        if (!F1ManifestParity(dir, spec, plan, f1)) return Fail("p6te: F1 manifest/body parity failed; no network");
        var confirm = $"yes-i-authorize-p6te-{spec.DocumentId.ToLowerInvariant()}-e-challenge-maximum-two-calls";
        if (!args.Contains($"--confirm-p6te={confirm}"))
        {
            Console.WriteLine("P6T-E challenge PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p6te: OPENROUTER_API_KEY missing");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        Directory.CreateDirectory(dir);
        var rows = new List<object>();
        var f1Watch = Stopwatch.StartNew();
        OpenRouterExecutionObservation? f1Response = null;
        string? f1Error = null;
        try
        {
            using var client = OpenRouterQualificationTransport.CreateOwned(options);
            f1Response = await client.ExecuteObservedAsync(f1.ProviderBody, f1.SourcePack.MaxCompletionTokens,
                f1.Request.SystemPrompt, f1.Request.UserMessage).ConfigureAwait(false);
        }
        catch (Exception exception) { f1Error = exception.Message; }
        f1Watch.Stop();

        V5TotalOccurrenceFunctionResultF1? functions = null;
        string f1Classification;
        if (f1Response is null || f1Error is not null) f1Classification = "TRANSPORT_ERROR";
        else if (!string.Equals(f1Response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase))
            f1Classification = f1Response.FinishReason == "length" ? "INCOMPLETE_PROVIDER_OUTPUT" : "NONTERMINAL_PROVIDER_OUTPUT";
        else
        {
            try { functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response.Content); f1Classification = "TOTAL_F1_LEDGER_ACCEPTED"; }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { f1Classification = "F1_CONTRACT_FAILURE:" + exception.Message; }
        }

        WriteNew(f1Path, new
        {
            schemaVersion = "v5-p6te-doc0256-f1-raw-capture-v1",
            documentId = spec.DocumentId, packId = f1.SourcePack.PackId, issuedOccurrences = f1.Request.Occurrences.Count,
            sourceSha256 = plan.SourceSha256, sourceUniverseSha256 = plan.SourceUniverseSha256,
            systemPromptSha256 = Hash(f1.Request.SystemPrompt), userMessageSha256 = f1.Request.UserMessageSha256,
            providerRequestSha256 = f1.ProviderRequestHash, providerRequestBytes = f1.ProviderRequestBytes,
            reasoningRequested = true, reasoningTokens = Usage(f1Response?.Usage, "completion_tokens_details", "reasoning_tokens"),
            promptTokens = Usage(f1Response?.Usage, "prompt_tokens"), completionTokens = Usage(f1Response?.Usage, "completion_tokens"),
            reasoningExecutionConfirmed = Usage(f1Response?.Usage, "completion_tokens_details", "reasoning_tokens") is > 0,
            finishReason = f1Response?.FinishReason, retryCount = f1Response?.RetryCount ?? 0,
            latencyMs = f1Watch.Elapsed.TotalMilliseconds, rawSseSha256 = f1Response is null ? null : Hash(f1Response.RawSse),
            rawResponseSha256 = f1Response is null ? null : Hash(f1Response.Content), rawResponse = f1Response?.Content,
            transportError = f1Error, classification = f1Classification,
            parsed = functions is null ? null : new
            {
                decisions = functions.Decisions.Select(value => new { occurrence = value.OccurrenceId, function = value.Function.ToString() }).ToArray(),
                establishesStructure = functions.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE),
                representsStructure = functions.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.REPRESENTS_STRUCTURE),
                other = functions.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.OTHER),
            },
            goldReadDuringCapture = false,
        });
        Console.WriteLine($"[1/2] {spec.DocumentId} F1: {f1Classification}, finish={f1Response?.FinishReason ?? "n/a"}");

        var g2Attempted = false;
        if (functions is not null)
        {
            var g2 = ComposeG2A(plan, f1, functions);
            // Freeze the derived second request after F1 raw capture but before the second network call.
            WriteNew(Path.Combine(dir, "g2a-request-manifest.v1.json"), new
            {
                schemaVersion = "v5-p6te-doc0256-g2a-request-manifest-v1", status = "FROZEN_BEFORE_SECOND_CALL",
                sourceAuthority = new { f1RawCaptureSha256 = Hash(File.ReadAllText(f1Path)), functionLedgerAccepted = true },
                documentId = spec.DocumentId,
                targetChallengeAlias = spec.TargetAlias,
                issuedF1Establishes = g2.Issued.Count,
                targetIncludedByF1Function = g2.Issued.Any(value => value.Alias == spec.TargetAlias),
                systemPromptSha256 = Hash(g2.SystemPrompt), userMessageSha256 = Hash(g2.UserMessage),
                providerRequestSha256 = g2.BodyHash, providerRequestBytes = g2.BodyBytes,
                maximumProviderCalls = 1, providerCallsBeforeSend = 0, retry = 0, repair = false, fallback = false, goldRead = false,
            });

            var g2Watch = Stopwatch.StartNew();
            OpenRouterExecutionObservation? g2Response = null;
            string? g2Error = null;
            try
            {
                g2Attempted = true;
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                g2Response = await client.ExecuteObservedAsync(g2.Body, g2.MaxCompletionTokens, g2.SystemPrompt, g2.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { g2Error = exception.Message; }
            g2Watch.Stop();

            var parsedG2 = g2Response is not null && g2Error is null && string.Equals(g2Response.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)
                ? TryParseG2(g2, g2Response.Content)
                : null;
            WriteNew(g2Path, new
            {
                schemaVersion = "v5-p6te-cross-document-g2a-raw-capture-v1", documentId = spec.DocumentId, sourcePack = f1.SourcePack.PackId,
                issued = g2.Issued.Count, functionUpstreamRawCaptureSha256 = Hash(File.ReadAllText(f1Path)),
                systemPromptSha256 = Hash(g2.SystemPrompt), userMessageSha256 = Hash(g2.UserMessage), providerRequestSha256 = g2.BodyHash,
                providerRequestBytes = g2.BodyBytes, reasoningRequested = true,
                reasoningTokens = Usage(g2Response?.Usage, "completion_tokens_details", "reasoning_tokens"),
                promptTokens = Usage(g2Response?.Usage, "prompt_tokens"), completionTokens = Usage(g2Response?.Usage, "completion_tokens"),
                finishReason = g2Response?.FinishReason, retryCount = g2Response?.RetryCount ?? 0, latencyMs = g2Watch.Elapsed.TotalMilliseconds,
                rawSseSha256 = g2Response is null ? null : Hash(g2Response.RawSse), rawResponseSha256 = g2Response is null ? null : Hash(g2Response.Content),
                rawResponse = g2Response?.Content, transportError = g2Error,
                classification = parsedG2 is null ? "G2A_TRANSPORT_OR_CONTRACT_FAILURE" : "G2A_TOTAL_LEDGER_ACCEPTED",
                parsed = parsedG2, goldReadDuringCapture = false,
            });
            rows.Add(new { stage = "G2A", classification = parsedG2 is null ? "FAILED" : "ACCEPTED", issued = g2.Issued.Count, finishReason = g2Response?.FinishReason });
            Console.WriteLine($"[2/2] {spec.DocumentId} G2A: {(parsedG2 is null ? "FAILED" : "ACCEPTED")}, finish={g2Response?.FinishReason ?? "n/a"}");
        }
        else
        {
            rows.Add(new { stage = "G2A", classification = "NOT_RUN_F1_DID_NOT_PRODUCE_A_VALID_TOTAL_LEDGER", issued = (int?)null, finishReason = (string?)null });
        }

        WriteNew(receiptPath, new
        {
            schemaVersion = "v5-p6te-doc0256-e-challenge-execution-receipt-v1",
            documentId = spec.DocumentId, primaryCalls = 1 + (g2Attempted ? 1 : 0),
            maximumProviderCalls = 2, retry = 0, repair = false, fallback = false,
            goldRead = false, goldMutation = "NONE", runtimeChanged = false,
            f1RawSha256 = Hash(File.ReadAllText(f1Path)), g2aRawSha256 = File.Exists(g2Path) ? Hash(File.ReadAllText(g2Path)) : null,
            stages = rows, challengeVerdict = "NOT_SCORED_UNTIL_SEPARATE_PROVIDER_FREE_AUDIT",
        });
        return 0;
    }

    private static (PdfCandidateAuthorityDocumentPlan Plan, PdfFunctionMembershipPreparedPackF1 Prepared) Build(string repo, Spec spec)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(Path.Combine(repo, spec.SourcePdf.Replace('/', Path.DirectorySeparatorChar)));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            Path.Combine(repo, SnapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceHash + ".json"), spec.DocumentId);
        var pack = plan.Packs.Single(value => value.OwnedAliases.Contains(spec.TargetAlias, StringComparer.Ordinal));
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        if (prepared.Request.Occurrences.Count != 96 || !prepared.Request.Occurrences.Any(value => value.Atom.Alias == spec.TargetAlias))
            throw new InvalidOperationException($"p6te-{spec.DocumentId}-f1-universe-invalid");
        return (plan, prepared);
    }

    private static bool F1ManifestParity(string dir, Spec spec, PdfCandidateAuthorityDocumentPlan plan, PdfFunctionMembershipPreparedPackF1 prepared)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "f1-request-manifest.v1.json")));
            var root = manifest.RootElement;
            return root.GetProperty("status").GetString() == "PREPARED_NOT_AUTHORIZED" &&
                   root.GetProperty("execution").GetProperty("providerCalls").GetInt32() == 0 &&
                   !root.GetProperty("execution").GetProperty("goldReadDuringCapture").GetBoolean() &&
                   root.GetProperty("challenge").GetProperty("sourceSha256").GetString() == plan.SourceSha256 &&
                   root.GetProperty("challenge").GetProperty("continuationAlias").GetString() == spec.TargetAlias &&
                   root.GetProperty("request").GetProperty("providerBodySha256").GetString() == prepared.ProviderRequestHash &&
                   root.GetProperty("request").GetProperty("userMessageSha256").GetString() == prepared.Request.UserMessageSha256;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static G2Request ComposeG2A(PdfCandidateAuthorityDocumentPlan plan, PdfFunctionMembershipPreparedPackF1 f1,
        V5TotalOccurrenceFunctionResultF1 functions)
    {
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var owned = f1.SourcePack.OwnedAliases;
        var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var indexByAlias = owned.Select((alias, index) => (alias, index)).ToDictionary(value => value.alias, value => value.index, StringComparer.Ordinal);
        var issued = functions.Decisions.Where(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE)
            .Select(value => (Id: value.OccurrenceId, Alias: f1.Request.Occurrences.Single(item => item.Id == value.OccurrenceId).Atom.Alias))
            .OrderBy(value => atoms[value.Alias].Ordinal).ToArray();
        var rows = issued.Select(value =>
        {
            var index = indexByAlias[value.Alias];
            var atom = atoms[value.Alias];
            (string? Id, int? Page, string? Text) Neighbor(int at) => at < 0 || at >= owned.Count
                ? (null, null, null)
                : (idByAlias[owned[at]], atoms[owned[at]].Page, atoms[owned[at]].Text);
            var before = Neighbor(index - 1);
            var after = Neighbor(index + 1);
            return new G2Occurrence(value.Id, value.Alias, atom.Page, atom.Text, "ESTABLISHES_STRUCTURE",
                before.Id, before.Page, before.Text, after.Id, after.Page, after.Text);
        }).ToArray();

        const string systemPrompt = """
            Decide anchor existence only. Each issued primary occurrence has an upstream ESTABLISHES_STRUCTURE eligibility signal, but that signal is not proof that a valid local structural heading extent begins at this primary.

            For every issued O#, return exactly one anchor: HAS_STRUCTURAL_EXTENT if at least one valid local structural heading extent begins at that primary; otherwise NO_STRUCTURAL_EXTENT. Do not choose or describe any extent. Do not infer an answer from context-only items.

            Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","anchor":"HAS_STRUCTURAL_EXTENT"},{"primary":"O28","anchor":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and anchor. Do not output source text, candidate IDs, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
            """;
        var user = JsonSerializer.Serialize(new { protocolVersion = "v5-function-conditioned-anchor-existence-1", occurrences = rows.Select(value => new
        {
            primary = value.Id, page = value.Page, text = value.Text, upstreamFunction = value.Function,
            previous = value.PreviousId is null ? null : new { occurrence = value.PreviousId, page = value.PreviousPage, text = value.PreviousText, selectable = false },
            next = value.NextId is null ? null : new { occurrence = value.NextId, page = value.NextPage, text = value.NextText, selectable = false },
        }).ToArray() });
        var request = new V5FreeHeadingRequestV1("v5-function-conditioned-anchor-existence-1", systemPrompt, user, Hash(user), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, f1.SourcePack.MaxCompletionTokens);
        return new G2Request(systemPrompt, user, body.PayloadBytes, body.Hash, body.Bytes, f1.SourcePack.MaxCompletionTokens, rows);
    }

    private static object? TryParseG2(G2Request request, string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != request.Issued.Count) return null;
            var allowed = request.Issued.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in decisions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 || !item.TryGetProperty("primary", out var primary) || !item.TryGetProperty("anchor", out var anchor) || primary.ValueKind != JsonValueKind.String || anchor.ValueKind != JsonValueKind.String) return null;
                if (!allowed.Contains(primary.GetString()!) || anchor.GetString() is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT") || !result.TryAdd(primary.GetString()!, anchor.GetString()!)) return null;
            }
            if (result.Count != allowed.Count) return null;
            return new { decisions = request.Issued.Select(item => new { primary = item.Id, alias = item.Alias, anchor = result[item.Id] }).ToArray(), has = result.Count(value => value.Value == "HAS_STRUCTURAL_EXTENT"), no = result.Count(value => value.Value == "NO_STRUCTURAL_EXTENT") };
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
    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException("p6te-immutable-artifact-exists"); File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false)); }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
