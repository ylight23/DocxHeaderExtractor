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
        IReadOnlyList<string> AuditCodes, string Verdict, P7F1V6Usage.CacheTelemetry? Cache = null);

    public List<LedgerEntry> Ledger { get; } = new();

    // D4.12d: calls may run in parallel. Seq is taken when the call starts (so it is the order the calls were admitted), entries are added
    // under a lock, and a caller that revises the verdict of its own entry does so through ReviseLast, which finds the entry this thread
    // added last. LedgerBySeq is the ledger in call order.
    int seqCounter;
    readonly object ledgerGate = new();
    readonly ThreadLocal<int> lastIndex = new(() => -1);

    void AddEntry(LedgerEntry e) { lock (ledgerGate) { Ledger.Add(e); lastIndex.Value = Ledger.Count - 1; } }

    public void ReviseLast(Func<LedgerEntry, LedgerEntry> change)
    {
        lock (ledgerGate) { var i = lastIndex.Value; if (i < 0) throw new InvalidOperationException("NO_LEDGER_ENTRY_ON_THIS_THREAD"); Ledger[i] = change(Ledger[i]); }
    }

    public IReadOnlyList<LedgerEntry> LedgerBySeq { get { lock (ledgerGate) return Ledger.OrderBy(e => e.Seq).ToArray(); } }

    static string N(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "UNKNOWN";

    public string Reply(string unitId, int round, P7F1V6Protocol.PreparedRequest request)
    {
        var text = ReplyRaw(unitId, round, request, toolCallsAllowed: false);
        var (content, failure) = P7F1V6ResponseAdapter.ExtractContent(text);
        if (content is null)
        {
            ReviseLast(e => e with { Verdict = "NO_CONTENT" });
            throw new P7F1V6DirectV2.ReplyFailure("CONTENT:" + failure);
        }
        // Passing the audit is not acceptance: the contract parser decides next.
        return content;
    }

    /// <summary>One audited call; returns the response text after the transport, key, store, endpoint and usage checks. The caller reads
    /// the message. toolCallsAllowed (D4.11 agent turns) accepts finish_reason tool_calls; every other check is the same.
    /// A request with "stream": true (D4.11c) gets an SSE body: the raw SSE bytes are what the safe store keeps (the authority); the stream
    /// must end with [DONE], carry no error event and no malformed event, or the call fails before any audit. The audit and the caller then
    /// read a chat-completion view assembled from the stream by <see cref="P7F1QSse"/> (content, reasoning, tool_calls with id, name and
    /// concatenated arguments, finish_reason, usage, provider, model); the view is derived and never stored as the response.</summary>
    public string ReplyRaw(string unitId, int round, P7F1V6Protocol.PreparedRequest request, bool toolCallsAllowed)
    {
        var seq = Interlocked.Increment(ref seqCounter);
        var reserve = P7F1V6Direct.AttemptUsd(request.Bytes);
        string stage;
        bool stream;
        using (var body = JsonDocument.Parse(request.Body))
        using (var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!))
        {
            stage = payload.RootElement.GetProperty("stage").GetString()!;
            stream = body.RootElement.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.True;
        }

        LedgerEntry Entry(string transportOutcome, int? status, string? responseSha, string? rawKey, P7F1V6Usage.Audit? audit, string endpoint,
            string model, string verdict) => new(seq, unitId, stage, round, request.Sha256, request.Bytes, reserve, transportOutcome, status,
            responseSha, rawKey, audit?.FinishReason ?? "UNKNOWN", N(audit?.PromptTokens), N(audit?.CompletionTokens), N(audit?.ReasoningTokens),
            audit?.ProviderCostUsd, audit?.ComputedCostUsd, endpoint, model, audit?.Codes ?? Array.Empty<string>(), verdict, audit?.Cache);

        var r = transport.Send(request, apiKey);
        if (r.Outcome != "HTTP_OK")
        {
            // A boundary or prepared-hash mismatch means the bytes sent were not the bytes planned: nothing after it can be trusted.
            if (r.Outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH") budget.Halt(r.Outcome);
            // No response means no usage: the cost of the attempt is unknown and stays reserved.
            budget.RecordActual(null, reserve);
            // A 2xx body that is not valid UTF-8 is kept as received (never repaired) so the evidence is not lost.
            var keptSha = r.Outcome == "RESPONSE_NOT_UTF8" && r.BodyBytes is { Length: > 0 } nb ? Convert.ToHexStringLower(SHA256.HashData(nb)) : null;
            var keptKey = keptSha is not null ? store.Put(r.BodyBytes!) : null;
            AddEntry(Entry(r.Outcome, r.Status, keptSha, keptKey, null, "UNKNOWN", "UNKNOWN", "TRANSPORT_FAILED"));
            throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:" + r.Outcome);
        }
        var text = r.Body ?? "";
        if (text.Contains(apiKey, StringComparison.Ordinal))
        {
            budget.Halt("API_KEY_ECHOED");
            budget.RecordActual(null, reserve);
            AddEntry(Entry(r.Outcome, r.Status, null, null, null, "UNKNOWN", "UNKNOWN", "KEY_ECHOED_NOT_STORED"));
            throw new P7F1V6DirectV2.ReplyFailure("API_KEY_ECHOED");
        }
        // The stored response is the body as received from HTTP (D4.11d), not the decoded string encoded again.
        var bytes = r.BodyBytes ?? Encoding.UTF8.GetBytes(text);
        var responseSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rawKey = store.Put(bytes);
        if (stream)
        {
            var asm = P7F1QSse.Parse(text);
            // The guard reads the stream line by line before anything is trusted: an event: error, any named event, any field but data, data
            // after [DONE] and a data event without choices or error are all refused, whatever their payload says.
            var broken = P7F1V6SseGuard.Check(text)
                ?? (asm.Error is not null ? "STREAM_ERROR" : !asm.Done ? "STREAM_INCOMPLETE" : asm.MalformedEvents.Count > 0 ? "STREAM_MALFORMED" : null);
            if (broken is not null)
            {
                budget.RecordActual(P7F1QSse.Cost(asm.Usage) is { } c ? (double)c : null, reserve);
                AddEntry(Entry(r.Outcome, r.Status, responseSha, rawKey, null, asm.Provider ?? "UNKNOWN", asm.Model ?? "UNKNOWN", broken));
                throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:" + broken);
            }
            text = AssembledView(asm);
        }
        var audit = P7F1V6Usage.Check(text, request.Bytes, P7F1V6Protocol.MaxTokens, toolCallsAllowed);
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
            AddEntry(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "ENDPOINT_OR_MODEL_UNEXPECTED"));
            throw new P7F1V6DirectV2.ReplyFailure("ENDPOINT_OR_MODEL_UNEXPECTED");
        }
        if (!(audit.Codes.Count == 1 && audit.Codes[0] == "OK"))
        {
            AddEntry(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "AUDIT_FAILED"));
            throw new P7F1V6DirectV2.ReplyFailure("AUDIT:" + string.Join(",", audit.Codes));
        }
        AddEntry(Entry(r.Outcome, r.Status, responseSha, rawKey, audit, endpoint, model, "PASSED_AUDIT_TO_PARSER"));
        return text;
    }

    /// <summary>The chat-completion view of one SSE stream, as the non-streaming reply would state it. Derived; the raw SSE is the authority.
    /// content is null when the stream carried no content (a tool-call turn); tool_calls are listed in stream index order.</summary>
    public static string AssembledView(F1QStreamAssembly asm)
    {
        var message = new System.Text.Json.Nodes.JsonObject
        {
            ["role"] = "assistant",
            ["content"] = asm.Content.Length == 0 ? null : asm.Content,
            ["reasoning"] = asm.Reasoning.Length == 0 ? null : asm.Reasoning,
        };
        if (asm.ToolCalls.Count > 0)
            message["tool_calls"] = new System.Text.Json.Nodes.JsonArray(asm.ToolCalls.Select(t => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = t.Id, ["type"] = "function",
                ["function"] = new System.Text.Json.Nodes.JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments },
            }).ToArray());
        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = asm.GenerationId, ["provider"] = asm.Provider, ["model"] = asm.Model,
            ["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["index"] = 0, ["finish_reason"] = asm.FinishReason, ["native_finish_reason"] = asm.NativeFinishReason, ["message"] = message,
            }),
            ["usage"] = asm.Usage is { } u ? System.Text.Json.Nodes.JsonNode.Parse(u.GetRawText()) : null,
            ["streamAssembled"] = true,
        }.ToJsonString();
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
    /// <summary>D4.7: the user's clarified task A-STRUCTURAL-OCCURRENCES, verbatim. It replaces task A ("Trích xuất tất cả heading"), kept as a
    /// historical baseline. This is the instruction of one research task, not a system-wide definition of a heading: it is user content in
    /// the request, never part of the system prompt, and C# never filters an alias by it.</summary>
    public const string Instruction =
        "Trích xuất tất cả các heading thực sự tổ chức cấu trúc tài liệu, xét theo vai trò của từng lần xuất hiện tại chính vị trí đó.\n" +
        "Chọn những dòng hoặc nhóm dòng có chức năng đặt tên, mở đầu hoặc phân chia một phần nội dung của tài liệu.\n" +
        "Không chọn các dòng trong mục lục chỉ liệt kê hoặc dẫn chiếu đến heading xuất hiện ở vị trí khác. Tuy nhiên, vẫn xem xét các heading thực sự tổ chức chính mục lục, chỉ mục và những phần khác của tài liệu.\n" +
        "Sử dụng văn bản, bố cục và ngữ cảnh để xác định chức năng của từng lần xuất hiện. Khi chưa đủ bằng chứng, hãy yêu cầu thêm ngữ cảnh hoặc báo chưa thể quyết định thay vì suy đoán.";
    /// <summary>D4.12c: the owner's plain request in English, for the corpus runs ("extract all headings", the all-heading Gold's own task, in English).</summary>
    public const string InstructionEnglishAll = "Extract all headings";
    /// <summary>D4.12e: the owner asks for the main headings, those that carry the structure (outline) of the document; in English. The owner's words, not a
    /// definition added by the program: it names no kind of line to include or to exclude.</summary>
    public const string InstructionEnglishMain =
        "Extract the main headings of the document: the headings that carry its structure, that is, the ones that give the document its outline.\n" +
        "Use the text, the layout and the context to decide which lines are such headings.";
    /// <summary>D4.12f: the same request as InstructionEnglishMain, said more plainly for the model: what the main structure is, to use the tools to settle a
    /// doubt that depends on a relationship between lines, and not to guess. The owner's ideas; it names no kind of line to include or to exclude.</summary>
    public const string InstructionEnglishMainTools =
        "Extract the main headings of the document: the headings that carry its structure, that is, the ones that give the document its outline.\n" +
        "The main structure is the frame of the document: its main headings name or open the main parts, and the other lines belong under them. A line is a main heading because of the part of the document it names or opens and because of its relation to the lines around it, not because of how it looks on its own.\n" +
        "Use the text, the layout and the context to decide. When a decision depends on a relationship that you cannot see in the lines shown (the lines before and after it, other lines with the same text, its position on the page or in the document), use the tools to check that relationship.\n" +
        "Do not guess: if the evidence still does not settle a line, say that it is unresolved instead of choosing.";
    /// <summary>D4.12i: the owner's own three sentences, verbatim (2026-10-13). Nothing is added by the program: no definition of a main heading.</summary>
    public const string InstructionEnglishMainPlain =
        "Extract the main headings of the document: the headings that carry its structure (its outline).\n" +
        "When you are unsure about how a line relates to other lines, use the tools to check that relationship.\n" +
        "Do not guess: if the evidence does not settle a line, say that it is unresolved instead of choosing.";
    public const string TaskId = "A-STRUCTURAL-OCCURRENCES";
    public const string EvaluationLabel = "Development evaluation — task clarification informed by previous model errors";
    public const string BaselineTaskId = "A";
    public const string BaselineInstruction = "Trích xuất tất cả heading";
    public const string Registry = "eval/a99-closed-loop/gold-current/registry.v1.json";
    public const int Unit = 48;
    public const int Overlap = P7F1V6Direct.DefaultOverlapLines;
    public const int MaxCalls = 100;   // D4.5a: raised from 64 by the user; the $1.00 ceiling is unchanged and may stop the run first
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

    public static GoldPin ReadGoldPin(string repoRoot) => ReadGoldPin(repoRoot, AuthorityId);

    /// <summary>The Gold pin of any registry authority (D4.11i). Gold is read for the scorer and for choosing test segments offline; it is never sent.</summary>
    public static GoldPin ReadGoldPin(string repoRoot, string authorityId)
    {
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, Registry)));
        var a = reg.RootElement.GetProperty("authorities").EnumerateArray().First(x => x.GetProperty("authorityId").GetString() == authorityId);
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
            ["artifact"] = "p7.f1.v6.d4.8-plan.v1",
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
            ["task"] = TaskId,
            ["evaluationLabel"] = EvaluationLabel,
            ["notHeldOut"] = "The instruction was clarified after the D4.5 run's errors on SRC-095 were seen. Not held-out; F1 is not comparable with task A as an independent improvement.",
            ["historicalBaseline"] = new { task = BaselineTaskId, instruction = BaselineInstruction,
                instructionSha256 = P7F1V6Core.InstructionSha256(BaselineInstruction), consumedPlanSha256 = "340d7cbf5e8694ba411ec06aec6e6fe6a1db7082cf895a880c294f65e86978a6" },
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
            ["earlyStop"] = new { consecutiveContractFailures = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                counts = "parser failures, usage/finish audit failures and missing content; transport failures neither count nor reset" },
            ["liveAuthorization"] = new { required = true, artifact = P7F1V6DirectAuthorization.Artifact, status = P7F1V6DirectAuthorization.Approved,
                campaigns = 1, consumedMarker = "consumed-<planSha256>.json in the safe root" },
            ["safeRootPolicy"] = new { location = "%LOCALAPPDATA%\\DocxHeaderExtractor\\QualificationRaw\\D45-SRC095", mustExistBeforeRun = true,
                outsideRepository = true, fileSystem = "NTFS", noReparsePointsInPath = true, acl = "current user only, inheritance cut", runDirectories = "run-<32 hex>" },
            ["retention"] = new { files = new[] { "<sha256>.bin", "<sha256>.<guid>.tmp", P7F1V6Retention.ReviewFile, P7F1V6Retention.SummaryFile },
                rawRetentionDays = P7F1V6DataPolicy.RawRetentionDays },
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

    /// <summary>What is wrong with a safe root (D4.5a): it must exist before the run, resolve outside the repository, sit on NTFS, have no
    /// reparse point anywhere on its path, and be private to the current user. Empty means usable.</summary>
    public static IReadOnlyList<string> SafeRootFailures(string repoRoot, string safeRoot)
    {
        var failures = new List<string>();
        var full = Path.GetFullPath(safeRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full)) return new[] { "SAFE_ROOT_MISSING" };
        var repo = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(repo, StringComparison.OrdinalIgnoreCase) || full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            failures.Add("SAFE_ROOT_INSIDE_REPOSITORY");
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            if (d.Parent is not null && (d.Attributes & FileAttributes.ReparsePoint) != 0) failures.Add("REPARSE_POINT_IN_PATH:" + d.FullName);
        if (OperatingSystem.IsWindows())
        {
            var format = new DriveInfo(Path.GetPathRoot(full)!).DriveFormat;
            if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)) failures.Add("NOT_NTFS:" + format);
            failures.AddRange(P7F1V6SafeRawStore.RootAccessFailures(full));
        }
        else failures.Add("NOT_WINDOWS_ACL_UNCHECKED");
        return failures;
    }

    public static string ConsumedMarker(string safeRoot, string planSha256) => Path.Combine(Path.GetFullPath(safeRoot), "consumed-" + planSha256 + ".json");

    /// <summary>Verifies the plan, the safe root and the live authorization, marks the campaign consumed, then runs the plan through the
    /// audited model and scores it (Gold read-only). Any failure before the marker is written means no request. With a production handler
    /// this sends requests and needs its own approval; tests pass a fake handler.</summary>
    public static LiveResult Execute(string repoRoot, string planPath, string confirmedSha256, string currentCommit, string authorizationPath,
        string apiKey, HttpMessageHandler handler, string safeRoot)
    {
        var failures = Verify(repoRoot, planPath, confirmedSha256, currentCommit);
        if (failures.Count > 0) throw new InvalidOperationException("PLAN_REFUSED:" + string.Join(",", failures));
        var rootFailures = SafeRootFailures(repoRoot, safeRoot);
        if (rootFailures.Count > 0) throw new InvalidOperationException("SAFE_ROOT_REFUSED:" + string.Join(",", rootFailures));
        var authFailures = P7F1V6DirectAuthorization.Check(authorizationPath, planPath, confirmedSha256, safeRoot);
        if (authFailures.Count > 0) throw new InvalidOperationException("AUTHORIZATION_REFUSED:" + string.Join(",", authFailures));
        // One campaign per plan: the marker is created before the first request and never removed by this code.
        var marker = ConsumedMarker(safeRoot, confirmedSha256);
        try
        {
            using var fs = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { planSha256 = confirmedSha256,
                authorizationSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(authorizationPath))), startedUtc = DateTime.UtcNow.ToString("O") }) + "\n"));
        }
        catch (IOException) when (File.Exists(marker)) { throw new InvalidOperationException("CAMPAIGN_ALREADY_CONSUMED"); }
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

/// <summary>
/// The live authorization record for one D4.5 campaign (D4.5a). A plan is not an approval; this record is. It binds the plan SHA and the
/// values a reviewer approves: document and source, model and route, campaigns (one), posts, USD ceiling, request byte cap, retries, the safe
/// root, and the data-sharing decisions. A draft is PENDING_USER_APPROVAL and is refused. <see cref="Approve"/> writes the approval only from
/// values the user gave; the code never approves on its own. approvalSha256 is a SHA-256 over the record without that field: integrity,
/// not a signature. Every data-policy fact that is not VERIFIED must be listed, word for word, in acceptedOpenItems; nothing is skipped.
/// </summary>
internal static class P7F1V6DirectAuthorization
{
    public const string Artifact = "p7.f1.v6.d4.5-live-authorization.v1";
    public const string Pending = "PENDING_USER_APPROVAL";
    public const string Approved = "APPROVED";
    public const string PendingDecision = "PENDING_USER_DECISION";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Digest(System.Text.Json.Nodes.JsonObject o)
    {
        var copy = System.Text.Json.Nodes.JsonNode.Parse(o.ToJsonString())!.AsObject();
        copy.Remove("approvalSha256");
        return P7F1V6Protocol.Sha(copy.ToJsonString());
    }

    static byte[] Seal(System.Text.Json.Nodes.JsonObject o)
    {
        o.Remove("approvalSha256");
        o["approvalSha256"] = Digest(o);
        return Encoding.UTF8.GetBytes(o.ToJsonString(Indented) + "\n");
    }

    /// <summary>A draft for the reviewer, built from the plan file. Status PENDING_USER_APPROVAL; the data-collection decision is open.</summary>
    public static byte[] Draft(string planPath, string safeRoot)
    {
        var planBytes = File.ReadAllBytes(planPath);
        using var plan = JsonDocument.Parse(planBytes);
        var p = plan.RootElement;
        var budget = p.GetProperty("budget");
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["artifact"] = Artifact,
            ["status"] = Pending,
            ["planFile"] = Path.GetFileName(planPath),
            ["planSha256"] = Convert.ToHexStringLower(SHA256.HashData(planBytes)),
            ["codeCommit"] = p.GetProperty("codeCommit").GetString(),
            ["authorityId"] = p.GetProperty("authorityId").GetString(),
            ["document"] = p.GetProperty("document").GetString(),
            ["sourceSha256"] = p.GetProperty("sourceSha256").GetString(),
            ["task"] = p.GetProperty("task").GetString(),
            ["model"] = p.GetProperty("model").GetString(),
            ["providerOnly"] = P7F1V6Protocol.ProviderTag,
            ["providerIgnore"] = string.Join(",", P7F1V6Protocol.DefaultIgnore),
            ["allowFallbacks"] = false,
            ["campaigns"] = 1,
            ["maxPosts"] = budget.GetProperty("maxCalls").GetInt32(),
            ["ceilingUsd"] = budget.GetProperty("ceilingUsd").GetDouble(),
            ["maxRequestBytes"] = budget.GetProperty("maxRequestBytes").GetInt32(),
            ["retries"] = p.GetProperty("retries").GetInt32(),
            ["safeRoot"] = Path.GetFullPath(safeRoot).TrimEnd(Path.DirectorySeparatorChar),
            ["dataSharing"] = new System.Text.Json.Nodes.JsonObject
            {
                // D4.11i: a plan for another document states its own; plans without the field keep the RFC wording.
                ["documentPublicity"] = p.TryGetProperty("documentPublicity", out var pub) && pub.ValueKind == JsonValueKind.String
                    ? pub.GetString() : "IETF RFC 9114, published publicly by the IETF",
                ["zdr"] = false,
                ["zdrDecision"] = "User decision recorded in the session (2026-10-12): \"CHO PHÉP DÂT ZDR = FALSE VÌ QWEN FLASH KHÔNG CẤU HÌNH ZDR = TRUE\". The route policy fact stays WAIVER_NOT_VERIFIED until this record is approved.",
                ["dataCollection"] = "NOT_SET_IN_REQUEST (OpenRouter default: allow). Setting deny would change the pinned request bytes and may make the Alibaba route ineligible.",
                ["dataCollectionDecision"] = PendingDecision,
                ["loggingThroughOpenRouter"] = "UNKNOWN: not verified. Accepted only if listed in acceptedOpenItems.",
                ["openItemsToAccept"] = new System.Text.Json.Nodes.JsonArray(P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking
                    .Select(b => (System.Text.Json.Nodes.JsonNode?)b).ToArray()),
                ["acceptedOpenItems"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["approvedBy"] = null,
            ["approvedAtUtc"] = null,
            ["approvalText"] = null,
        };
        return Seal(o);
    }

    /// <summary>Turns a draft into an approved record with the user's own words and decisions. Called only on the user's explicit approval.</summary>
    public static byte[] Approve(byte[] draft, string approvedBy, string approvalText, string dataCollectionDecision,
        IReadOnlyList<string> acceptedOpenItems, DateTime approvedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(approvedBy) || string.IsNullOrWhiteSpace(approvalText) || string.IsNullOrWhiteSpace(dataCollectionDecision)
            || dataCollectionDecision == PendingDecision) throw new ArgumentException("APPROVAL_FIELDS_MISSING");
        var o = System.Text.Json.Nodes.JsonNode.Parse(draft)!.AsObject();
        if (o["status"]!.GetValue<string>() != Pending) throw new InvalidOperationException("NOT_A_DRAFT");
        o["status"] = Approved;
        o["approvedBy"] = approvedBy;
        o["approvedAtUtc"] = approvedAtUtc.ToUniversalTime().ToString("O");
        o["approvalText"] = approvalText;
        var ds = o["dataSharing"]!.AsObject();
        ds["dataCollectionDecision"] = dataCollectionDecision;
        ds["acceptedOpenItems"] = new System.Text.Json.Nodes.JsonArray(acceptedOpenItems.Select(a => (System.Text.Json.Nodes.JsonNode?)a).ToArray());
        return Seal(o);
    }

    /// <summary>Every reason the record does not authorize this plan now. Empty means one campaign may run.</summary>
    public static IReadOnlyList<string> Check(string authorizationPath, string planPath, string planSha256, string safeRoot)
    {
        var f = new List<string>();
        if (!File.Exists(authorizationPath)) return new[] { "AUTHORIZATION_MISSING" };
        System.Text.Json.Nodes.JsonObject a;
        try { a = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(authorizationPath))!.AsObject(); }
        catch (JsonException) { return new[] { "AUTHORIZATION_NOT_JSON" }; }
        string? S(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        if (S(a["artifact"]) != Artifact) f.Add("AUTHORIZATION_ARTIFACT_INVALID");
        if (S(a["approvalSha256"]) != Digest(a)) f.Add("AUTHORIZATION_DIGEST_MISMATCH");
        if (S(a["status"]) != Approved) f.Add("AUTHORIZATION_NOT_APPROVED:" + S(a["status"]));
        var planBytes = File.ReadAllBytes(planPath);
        var actualPlanSha = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        if (S(a["planSha256"]) != planSha256 || actualPlanSha != planSha256) f.Add("AUTHORIZATION_PLAN_SHA_DIFFERS");
        using var plan = JsonDocument.Parse(planBytes);
        var p = plan.RootElement;
        var budget = p.GetProperty("budget");
        void Same(string field, string? expected) { if (S(a[field]) != expected) f.Add("AUTHORIZATION_FIELD_DIFFERS:" + field); }
        Same("codeCommit", p.GetProperty("codeCommit").GetString());
        Same("authorityId", p.GetProperty("authorityId").GetString());
        Same("document", p.GetProperty("document").GetString());
        Same("sourceSha256", p.GetProperty("sourceSha256").GetString());
        Same("model", p.GetProperty("model").GetString());
        Same("providerOnly", P7F1V6Protocol.ProviderTag);
        Same("providerIgnore", string.Join(",", P7F1V6Protocol.DefaultIgnore));
        Same("safeRoot", Path.GetFullPath(safeRoot).TrimEnd(Path.DirectorySeparatorChar));
        int? I(string k) => a[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        double? D(string k) => a[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        bool? B(string k) => a[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        if (I("campaigns") != 1) f.Add("AUTHORIZATION_CAMPAIGNS_NOT_ONE");
        if (I("maxPosts") != budget.GetProperty("maxCalls").GetInt32()) f.Add("AUTHORIZATION_FIELD_DIFFERS:maxPosts");
        if (D("ceilingUsd") != budget.GetProperty("ceilingUsd").GetDouble()) f.Add("AUTHORIZATION_FIELD_DIFFERS:ceilingUsd");
        if (I("maxRequestBytes") != budget.GetProperty("maxRequestBytes").GetInt32()) f.Add("AUTHORIZATION_FIELD_DIFFERS:maxRequestBytes");
        if (I("retries") != p.GetProperty("retries").GetInt32()) f.Add("AUTHORIZATION_FIELD_DIFFERS:retries");
        if (B("allowFallbacks") != false) f.Add("AUTHORIZATION_FALLBACKS_NOT_FALSE");

        var ds = a["dataSharing"] as System.Text.Json.Nodes.JsonObject;
        if (ds is null) f.Add("AUTHORIZATION_DATA_SHARING_MISSING");
        else
        {
            if (!(ds["zdr"] is System.Text.Json.Nodes.JsonValue z && z.TryGetValue<bool>(out var zv) && zv == false)) f.Add("AUTHORIZATION_ZDR_NOT_RECORDED_FALSE");
            if (string.IsNullOrWhiteSpace(S(ds["zdrDecision"]))) f.Add("AUTHORIZATION_ZDR_DECISION_MISSING");
            var dc = S(ds["dataCollectionDecision"]);
            if (string.IsNullOrWhiteSpace(dc) || dc == PendingDecision) f.Add("AUTHORIZATION_DATA_COLLECTION_UNDECIDED");
            var accepted = (ds["acceptedOpenItems"] as System.Text.Json.Nodes.JsonArray)?.Select(S).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string?>();
            foreach (var item in P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking)
                if (!accepted.Contains(item)) f.Add("AUTHORIZATION_OPEN_ITEM_NOT_ACCEPTED:" + item);
        }
        if (string.IsNullOrWhiteSpace(S(a["approvedBy"])) || string.IsNullOrWhiteSpace(S(a["approvedAtUtc"])) || string.IsNullOrWhiteSpace(S(a["approvalText"])))
            f.Add("AUTHORIZATION_APPROVER_MISSING");
        return f;
    }
}
