using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The audited reply model for a DIRECT V2 run (D4.5). It is the only bridge from <see cref="P7F1V6DirectV2.ReplyModel"/> to the network.
/// Each call: one transport attempt with no retry and no redirect; the raw response goes to the safe store (outside the repository), never
/// to a committed file; the usage audit, finish_reason, endpoint and model are checked; the provider cost is recorded against the call's
/// reserve. HTTP 200 alone is not success: a failed audit, an unexpected endpoint or missing content is a <see cref="P7F1V6DirectV2.ReplyFailure"/>,
/// and the reply that passes still has to pass the contract parser. A ledger line is kept per call, with hashes and counts but no source text.
/// </summary>
internal sealed class P7F1V6DirectAuditedModel
{
    readonly P7F1V6OpenRouterTransport transport;
    readonly string apiKey;
    readonly P7F1V6SafeRawStore store;
    readonly P7F1V6DirectV2.Budget budget;

    public P7F1V6DirectAuditedModel(P7F1V6OpenRouterTransport transport, string apiKey, P7F1V6SafeRawStore store, P7F1V6DirectV2.Budget budget)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API_KEY_MISSING", nameof(apiKey));
        this.transport = transport; this.apiKey = apiKey; this.store = store; this.budget = budget;
    }

    public sealed record LedgerEntry(int Seq, string UnitId, string Stage, int Round, string RequestSha256, int RequestBytes, double ReserveUsd,
        string Transport, int? HttpStatus, string? ResponseSha256, string? RawStoreKey, string FinishReason, string PromptTokens,
        string CompletionTokens, string ReasoningTokens, double? ProviderCostUsd, double? ComputedCostUsd, string Endpoint, string Model,
        IReadOnlyList<string> AuditCodes, string Verdict);

    public List<LedgerEntry> Ledger { get; } = new();

    static string N(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "UNKNOWN";

    public string Reply(string unitId, int round, P7F1V6Protocol.PreparedRequest request)
    {
        var seq = Ledger.Count + 1;
        var reserve = P7F1V6Direct.AttemptUsd(request.Bytes);
        string stage;
        using (var body = JsonDocument.Parse(request.Body))
        using (var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!))
            stage = payload.RootElement.GetProperty("stage").GetString()!;

        LedgerEntry Entry(string transportOutcome, int? status, string? responseSha, string? rawKey, P7F1V6Usage.Audit? audit, string endpoint,
            string model, string verdict) => new(seq, unitId, stage, round, request.Sha256, request.Bytes, reserve, transportOutcome, status,
            responseSha, rawKey, audit?.FinishReason ?? "UNKNOWN", N(audit?.PromptTokens), N(audit?.CompletionTokens), N(audit?.ReasoningTokens),
            audit?.ProviderCostUsd, audit?.ComputedCostUsd, endpoint, model, audit?.Codes ?? Array.Empty<string>(), verdict);

        var r = transport.Send(request, apiKey);
        if (r.Outcome != "HTTP_OK")
        {
            // A boundary or prepared-hash mismatch means the bytes sent were not the bytes planned: nothing after it can be trusted.
            if (r.Outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH") budget.Halt(r.Outcome);
            // No response means no usage: the cost of the attempt is unknown and stays reserved.
            budget.RecordActual(null, reserve);
            Ledger.Add(Entry(r.Outcome, r.Status, null, null, null, "UNKNOWN", "UNKNOWN", "TRANSPORT_FAILED"));
            throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:" + r.Outcome);
        }
        var text = r.Body ?? "";
        if (text.Contains(apiKey, StringComparison.Ordinal))
        {
            budget.Halt("API_KEY_ECHOED");
            budget.RecordActual(null, reserve);
            Ledger.Add(Entry(r.Outcome, r.Status, null, null, null, "UNKNOWN", "UNKNOWN", "KEY_ECHOED_NOT_STORED"));
            throw new P7F1V6DirectV2.ReplyFailure("API_KEY_ECHOED");
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        var responseSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rawKey = store.Put(bytes);
        var audit = P7F1V6Usage.Check(text, request.Bytes, P7F1V6Protocol.MaxTokens);
        budget.RecordActual(audit.ProviderCostUsd, reserve);

        string endpoint = "UNKNOWN", model = "UNKNOWN";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String) endpoint = p.GetString()!;
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString()!;
        }
        catch (JsonException) { }

        if (!string.Equals(endpoint, "Alibaba", StringComparison.Ordinal) || model != P7F1V6Protocol.ChatModel)
        {
            budget.Halt("ENDPOINT_OR_MODEL_UNEXPECTED");
            Ledger.Add(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "ENDPOINT_OR_MODEL_UNEXPECTED"));
            throw new P7F1V6DirectV2.ReplyFailure("ENDPOINT_OR_MODEL_UNEXPECTED");
        }
        if (!(audit.Codes.Count == 1 && audit.Codes[0] == "OK"))
        {
            Ledger.Add(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "AUDIT_FAILED"));
            throw new P7F1V6DirectV2.ReplyFailure("AUDIT:" + string.Join(",", audit.Codes));
        }
        var (content, failure) = P7F1V6ResponseAdapter.ExtractContent(text);
        if (content is null)
        {
            Ledger.Add(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "NO_CONTENT"));
            throw new P7F1V6DirectV2.ReplyFailure("CONTENT:" + failure);
        }
        // Passing the audit is not acceptance: the contract parser decides next.
        Ledger.Add(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "PASSED_AUDIT_TO_PARSER"));
        return content;
    }
}

/// <summary>
/// The D4.5 plan: SRC-095 (RFC 9114), task A, unit 48. The plan file pins everything a live run depends on. Its SHA-256 is the value a
/// reviewer confirms. <see cref="Verify"/> recomputes every pinned value from the code and the source before a run, and a single difference
/// refuses the run. Gold is pinned for the scorer only; it is never part of a request.
/// </summary>
internal static class P7F1V6DirectD45
{
    public const string AuthorityId = "SRC-095";
    public const string Document = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    public const string Instruction = "Trích xuất tất cả heading";
    public const string Registry = "eval/a99-closed-loop/gold-current/registry.v1.json";
    public const int Unit = 48;
    public const int Overlap = P7F1V6Direct.DefaultOverlapLines;
    public const int MaxCalls = 64;
    public const int MaxRequestBytes = 131072;
    public const double CeilingUsd = 1.00;
    public const int Retries = 0;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static P7F1V6DirectV2.Budget NewBudget() => new(MaxCalls, CeilingUsd, MaxRequestBytes);

    /// <summary>The first request of every segment, in order: the deterministic part of the run. Later rounds depend on the replies.</summary>
    public static IReadOnlyList<P7F1V6Protocol.PreparedRequest> FirstRequests(P7F1V6Universe u)
    {
        var lines = u.Lines;
        var list = new List<P7F1V6Protocol.PreparedRequest>();
        for (int core = 0, n = 0; core < lines.Count; core += Unit, n++)
        {
            var end = Math.Min(lines.Count, core + Unit);
            var decide = Enumerable.Range(core, end - core).Select(i => lines[i].Alias).ToArray();
            var from = Math.Max(0, core - Overlap);
            var ctx = Enumerable.Range(from, Math.Min(lines.Count, end + Overlap) - from).Select(i => lines[i]).ToArray();
            list.Add(P7F1V6DirectV2.BuildRequest(Instruction, u.SourceSha256, "SEGMENT", $"SEG-{n:000}", 1, decide, ctx, Array.Empty<P7F1V6DirectV2.Retrieval>()));
        }
        return list;
    }

    public sealed record GoldPin(string Path, string Sha256, string SourceSha256, string UniverseSha256);

    public static GoldPin ReadGoldPin(string repoRoot)
    {
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, Registry)));
        var a = reg.RootElement.GetProperty("authorities").EnumerateArray().First(x => x.GetProperty("authorityId").GetString() == AuthorityId);
        var path = a.GetProperty("canonicalGoldPath").GetString()!;
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, path)));
        return new GoldPin(path, a.GetProperty("goldSha256").GetString()!, a.GetProperty("sourceSha256").GetString()!,
            g.RootElement.GetProperty("occurrence").GetProperty("sourceUniverseSha256").GetString()!);
    }

    /// <summary>Every pinned value, computed now from the code, the source and the registry. The plan file is this object.</summary>
    public static Dictionary<string, object?> PinnedValues(string repoRoot, string codeCommit)
    {
        var gold = ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, Document, gold.SourceSha256, out var failure) ?? throw new InvalidOperationException(failure);
        var first = FirstRequests(u);
        return new Dictionary<string, object?>
        {
            ["artifact"] = "p7.f1.v6.d4.5-plan.v1",
            ["approvalToSend"] = false,
            ["codeCommit"] = codeCommit,
            ["authorityId"] = AuthorityId,
            ["document"] = Document,
            ["sourceSha256"] = u.SourceSha256,
            ["universeSha256"] = u.UniverseSha256,
            ["evidenceStoreSha256"] = u.EvidenceStoreSha256,
            ["atoms"] = u.Aliases.Count,
            ["evidenceParserVersion"] = P7F1QEvidenceTools.VersionV3,
            ["goldForScorerOnly"] = new { path = gold.Path, sha256 = gold.Sha256, universeSha256 = gold.UniverseSha256, sentToProvider = false },
            ["task"] = "A",
            ["instruction"] = Instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(Instruction),
            ["promptVersion"] = P7F1V6DirectV2.PromptVersion,
            ["systemPromptSha256"] = P7F1V6DirectV2.ComputedSystemPromptSha256(),
            ["requestVersion"] = P7F1V6DirectV2.RequestVersion,
            ["responseVersion"] = P7F1V6DirectV2.ResponseVersion,
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (user decision recorded as WAIVER_NOT_VERIFIED)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["reasoning"] = "not declared (endpoint default)",
            ["retries"] = Retries,
            ["unit"] = Unit,
            ["overlap"] = Overlap,
            ["finalization"] = "REQUIRED",
            ["budget"] = new { maxCalls = MaxCalls, maxRequestBytes = MaxRequestBytes, ceilingUsd = CeilingUsd, promptTokenBudget = P7F1V6Direct.PromptTokenBudget,
                tokenBound = "request UTF-8 bytes bound prompt tokens from above" },
            ["limits"] = new { P7F1V6DirectV2.MaxRounds, P7F1V6DirectV2.MaxRetrievePerRound, P7F1V6DirectV2.MaxAddedLinesPerUnit,
                P7F1V6DirectV2.MaxResolveContextLines, P7F1V6DirectV2.MaxFinalizeOutlineLines },
            ["rawResponses"] = "safe store outside the repository, per-run directory, retention policy; never committed",
            ["segments"] = first.Count,
            ["firstRequests"] = first.Select((r, i) => new { unitId = $"SEG-{i:000}", sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
            ["firstRequestsSha256"] = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
        };
    }

    public static byte[] Serialize(Dictionary<string, object?> plan) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, Indented) + "\n");

    /// <summary>Checks a plan file against its confirmed SHA and against the values computed now. Returns the failures; empty means the
    /// plan still describes exactly what a run would send.</summary>
    public static IReadOnlyList<string> Verify(string repoRoot, string planPath, string confirmedSha256, string currentCommit)
    {
        var failures = new List<string>();
        if (!File.Exists(planPath)) return new[] { "PLAN_FILE_MISSING" };
        var bytes = File.ReadAllBytes(planPath);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != confirmedSha256) failures.Add("PLAN_SHA_NOT_CONFIRMED");
        using var doc = JsonDocument.Parse(bytes);
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString();
        if (planCommit != currentCommit) failures.Add("CODE_COMMIT_DIFFERS");
        var now = Serialize(PinnedValues(repoRoot, planCommit ?? ""));
        if (!now.AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
        return failures;
    }

    public sealed record LiveResult(P7F1V6DirectV2.RunOutcome Outcome, IReadOnlyList<P7F1V6DirectAuditedModel.LedgerEntry> Ledger,
        P7F1V6DirectScorer.Result Score, string SafeRunDirectory);

    /// <summary>Verifies the plan, then runs it through the audited model and scores it (Gold read-only). With a production handler this
    /// sends requests; it is not run in D4.5. Tests pass a fake handler.</summary>
    public static LiveResult Execute(string repoRoot, string planPath, string confirmedSha256, string currentCommit, string apiKey,
        HttpMessageHandler handler, string safeRoot)
    {
        var failures = Verify(repoRoot, planPath, confirmedSha256, currentCommit);
        if (failures.Count > 0) throw new InvalidOperationException("PLAN_REFUSED:" + string.Join(",", failures));
        var gold = ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var budget = NewBudget();
        var store = P7F1V6SafeRawStore.CreateRun(safeRoot);
        var model = new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, store, budget);
        var outcome = P7F1V6DirectV2.Run(Instruction, u, model.Reply, budget, Overlap, Unit, finalize: true);
        var score = P7F1V6DirectScorer.Score(repoRoot, Registry, AuthorityId, P7F1V6DirectScorer.RunEvidence.From(u, outcome));
        return new LiveResult(outcome, model.Ledger, score, store.RunDirectory);
    }
}
