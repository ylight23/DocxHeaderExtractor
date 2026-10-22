using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.5 (offline): the scorer gate (official only after finalization), finalization across batches and with nothing
/// selected, the audited reply model over mock HTTP, and the pinned plan with its verification. No connection is opened; every model and
/// every HTTP response here is scripted. The oracle reads the Gold to play a perfect model for capacity checks; it is not a model result.</summary>
[Trait("Category", "Slow")]
public sealed class P7F1V6DirectD45Tests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d45-000000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d45-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD45Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    // ---------------- helpers ----------------

    static readonly Lazy<P7F1V6Universe> U095 = new(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), P7F1V6DirectD45.Document, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f));

    static readonly Lazy<List<List<string>>> Claims095 = new(() =>
    {
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).Path)));
        return g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()).ToList();
    });

    static string A(int i) => $"L{i:0000}:S0";
    static int Idx(string alias) => int.Parse(alias.AsSpan(1, 4));
    static List<P7F1V6Direct.Line> Lines(int n) => Enumerable.Range(0, n).Select(i => new P7F1V6Direct.Line(A(i), 1, $"Dòng {i}")).ToList();
    static P7F1V6DirectV2.Budget Gen() => new(10_000, 1_000, 2_000_000);

    static JsonElement Payload(string body)
    {
        using var doc = JsonDocument.Parse(body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!);
        return p.RootElement.Clone();
    }

    static string[] Decide(P7F1V6Protocol.PreparedRequest req) => Payload(req.Body).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req.Body).GetProperty("stage").GetString()!;

    static string Reply(IEnumerable<string> decide, ISet<string> select, IEnumerable<IReadOnlyList<string>> groups)
    {
        var d = decide.ToArray();
        var owned = d.ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "COMPLETE",
            decisions = d.Select(a => new { alias = a, decision = select.Contains(a) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } }),
            headings = groups.Where(g => g.Any(a => owned.Contains(a) && select.Contains(a))).Select(g => new { parts = g }),
        });
    }

    static string OracleReply(string[] decide)
    {
        var atoms = Claims095.Value.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        return Reply(decide, atoms, Claims095.Value);
    }

    // ---------------- scorer gate ----------------

    static P7F1V6DirectScorer.RunEvidence Perfect095(string status, string finalization, string assurance)
    {
        var u = U095.Value;
        var atoms = Claims095.Value.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        var decisions = u.Aliases.ToDictionary(a => a, a => atoms.Contains(a) ? "SELECT" : "NOT_SELECT", StringComparer.Ordinal);
        return new(u, decisions, Claims095.Value.Select(c => (IReadOnlyList<string>)c).ToList(), status, finalization, assurance);
    }

    [Fact]
    public void Scorer_OfficialOnlyAfterFinalization()
    {
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), P7F1V6DirectD45.Registry, "SRC-095",
            Perfect095("COMPLETE", "DONE", P7F1V6DirectV2.AssuranceFinalSelectionReviewed));
        Assert.True(r.Official, r.Reason);
        Assert.Equal("SCORED", r.Status);
        Assert.Equal(P7F1V6DirectScorer.OfficialLabel, r.Label);
        Assert.Equal(1.0, r.HeadingOccurrence!.F1);
    }

    [Theory]
    [InlineData("NOT_FINALIZED", "NO_CANDIDATES", "SEGMENT_COMPLETE")]
    [InlineData("NOT_FINALIZED", "DISABLED", "SEGMENT_COMPLETE")]
    [InlineData("INCOMPLETE", "OUTLINE_OVER_CAP", "SEGMENT_COMPLETE")]
    [InlineData("INCOMPLETE", "NOT_DONE", "SEGMENT_COMPLETE")]
    [InlineData("COMPLETE", "NOT_DONE", "FINAL_SELECTION_REVIEWED")]
    [InlineData("COMPLETE", "DONE", "SEGMENT_COMPLETE")]
    public void Scorer_EveryOtherOutcome_IsProvisionalOnly(string status, string finalization, string assurance)
    {
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), P7F1V6DirectD45.Registry, "SRC-095", Perfect095(status, finalization, assurance));
        Assert.True(r.Scored, r.Reason);
        Assert.False(r.Official);
        Assert.Equal("SCORED_PROVISIONAL", r.Status);
        Assert.Equal($"PROVISIONAL_DIAGNOSTIC:{status}/{finalization}/{assurance}", r.Label);
        Assert.Contains(r.Notes, n => n.StartsWith("NOT_END_TO_END", StringComparison.Ordinal));
    }

    [Fact]
    public void AllNegative_OnSrc095_IsNotFinalized_AndOnlyProvisional()
    {
        var u = U095.Value;
        var budget = P7F1V6DirectD45.NewBudget();
        var run = P7F1V6DirectV2.Run(P7F1V6DirectD45.Instruction, u, (unit, round, req) => Reply(Decide(req), new HashSet<string>(), Array.Empty<IReadOnlyList<string>>()),
            budget, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
        Assert.Equal("NOT_FINALIZED", run.Status);
        Assert.Equal("NO_CANDIDATES", run.Finalization);
        Assert.Equal(P7F1V6DirectV2.AssuranceSegmentComplete, run.Assurance);
        Assert.Equal(48, run.Calls);
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), P7F1V6DirectD45.Registry, "SRC-095", P7F1V6DirectScorer.RunEvidence.From(u, run));
        Assert.False(r.Official);
        Assert.Equal("0/109", r.AtomMembership!.F1Exact);
    }

    // ---------------- finalization across batches ----------------

    /// <summary>Singles at 1,3,...,17 and one heading [19,20]: with ten candidates per batch, 19 falls in batch one and 20 in batch two.</summary>
    static readonly int[] Singles = { 1, 3, 5, 7, 9, 11, 13, 15, 17 };
    static readonly HashSet<string> Selected = Singles.Append(19).Append(20).Select(A).ToHashSet(StringComparer.Ordinal);
    static readonly List<IReadOnlyList<string>> Groups = Singles.Select(i => (IReadOnlyList<string>)new[] { A(i) }).Append(new[] { A(19), A(20) }).ToList();

    [Fact]
    public void Finalization_AHeadingAcrossTwoBatches_IsKeptWhenBothBatchesAgree()
    {
        var finals = new List<string[]>();
        var run = P7F1V6DirectV2.Run("Trích xuất tất cả heading", "sha", Lines(40), (unit, round, req) =>
        {
            if (Stage(req) == "FINALIZE") finals.Add(Decide(req));
            return Reply(Decide(req), Selected, Groups);
        }, Gen(), overlap: 2, perUnit: 10);

        Assert.Equal(2, finals.Count);
        Assert.Contains(A(19), finals[0]);
        Assert.Equal(new[] { A(20) }, finals[1]);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Contains(run.Headings, h => h.SequenceEqual(new[] { A(19), A(20) }));
    }

    [Fact]
    public void Finalization_AHeadingAcrossTwoBatches_ThatTheBatchesSplit_IsLeftOpen()
    {
        var run = P7F1V6DirectV2.Run("Trích xuất tất cả heading", "sha", Lines(40), (unit, round, req) =>
        {
            var d = Decide(req);
            // The second finalization batch reports L0020 as a heading of its own; the first keeps [19,20].
            if (Stage(req) == "FINALIZE" && d.SequenceEqual(new[] { A(20) }))
                return Reply(d, Selected, new List<IReadOnlyList<string>> { new[] { A(20) } });
            return Reply(d, Selected, Groups);
        }, Gen(), overlap: 2, perUnit: 10);

        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("NOT_DONE", run.Finalization);
        Assert.Contains(A(19), run.Unresolved);
    }

    // ---------------- audited reply model over mock HTTP ----------------

    sealed class Handler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            var (status, text) = respond(body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static string Chat(string content, string requestBody, string finish = "stop", string provider = "Alibaba", bool usage = true, double? cost = null,
        string model = "qwen/qwen3.7-flash")
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        var completion = 400;
        object? u = usage
            ? new { prompt_tokens = prompt, completion_tokens = completion, cost = cost ?? P7F1V6Usage.Cost(prompt, completion),
                completion_tokens_details = new { reasoning_tokens = 300 } }
            : null;
        return JsonSerializer.Serialize(new
        {
            id = "gen-mock", model, provider,
            choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content } } },
            usage = u,
        });
    }

    (P7F1V6DirectV2.RunOutcome Run, P7F1V6DirectAuditedModel Model, Handler Http, P7F1V6DirectV2.Budget Budget) RunMock(
        Func<string, (HttpStatusCode, string)> respond, int lines = 20)
    {
        var http = new Handler(respond);
        var budget = Gen();
        Directory.CreateDirectory(Path.Combine(scratch, "safe"));
        var store = P7F1V6SafeRawStore.CreateRun(Path.Combine(scratch, "safe"));
        var model = new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(http, TimeSpan.FromSeconds(30)), Key, store, budget);
        var run = P7F1V6DirectV2.Run("Trích xuất tất cả heading", "sha", Lines(lines), model.Reply, budget, overlap: 2, perUnit: 10);
        return (run, model, http, budget);
    }

    static string[] DecideOf(string requestBody) => Payload(requestBody).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();

    [Fact]
    public void Audited_GoodResponses_AreStoredInTheSafeStore_AndLedgered()
    {
        var (run, model, http, budget) = RunMock(body => (HttpStatusCode.OK, Chat(Reply(DecideOf(body), new HashSet<string> { A(3) },
            new List<IReadOnlyList<string>> { new[] { A(3) } }), body)));
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(http.Calls, model.Ledger.Count);
        Assert.All(model.Ledger, e => Assert.Equal("PASSED_AUDIT_TO_PARSER", e.Verdict));
        Assert.All(model.Ledger, e => Assert.Equal("300", e.ReasoningTokens));
        Assert.All(model.Ledger, e => Assert.NotNull(e.RawStoreKey));
        Assert.True(budget.ActualUsd > 0 && budget.ActualUsd <= budget.ReservedUsd);
        var files = Directory.EnumerateFiles(Path.Combine(scratch, "safe"), "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(model.Ledger.Count, files.Count(f => f.EndsWith(".bin", StringComparison.Ordinal)));
        Assert.All(files, f => Assert.DoesNotContain(Key, File.ReadAllText(f), StringComparison.Ordinal));
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(model.Ledger), StringComparison.Ordinal);
    }

    [Fact]
    public void Audited_Http200WithAContractBreakingReply_IsNotAUnitSuccess()
    {
        var (run, model, _, _) = RunMock(body => (HttpStatusCode.OK, Chat("{\"decisions\":[]}", body)));
        Assert.All(model.Ledger, e => Assert.Equal("PASSED_AUDIT_TO_PARSER", e.Verdict));
        // Three contract failures in a row stop the campaign: the rest are never sent.
        Assert.Equal(P7F1V6DirectV2.MaxConsecutiveContractFailures, model.Ledger.Count);
        Assert.All(run.Units.Take(3), u => Assert.StartsWith("PROTOCOL:", u.Failure, StringComparison.Ordinal));
        Assert.All(run.Units.Skip(3), u => Assert.Equal("BUDGET:HALTED:CONSECUTIVE_CONTRACT_FAILURES", u.Failure));
        Assert.Equal("INCOMPLETE", run.Status);
    }

    [Theory]
    [InlineData("length", true, "AUDIT:FINISH_LENGTH")]
    [InlineData("stop", false, "AUDIT:USAGE_MISSING")]
    public void Audited_FailedUsageOrFinish_IsAFailedUnit(string finish, bool usage, string failure)
    {
        var (run, model, _, _) = RunMock(body => (HttpStatusCode.OK, Chat(Reply(DecideOf(body), new HashSet<string>(), Array.Empty<IReadOnlyList<string>>()), body, finish, usage: usage)));
        Assert.Equal("CALL:" + failure, run.Units[0].Failure);
        Assert.All(run.Units[0].Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
        Assert.Equal("AUDIT_FAILED", model.Ledger[0].Verdict);
        Assert.Equal("INCOMPLETE", run.Status);
    }

    [Fact]
    public void Audited_UnexpectedEndpoint_HaltsTheRun()
    {
        var (run, model, http, budget) = RunMock(body => (HttpStatusCode.OK, Chat(Reply(DecideOf(body), new HashSet<string>(), Array.Empty<IReadOnlyList<string>>()), body, provider: "OtherCloud")));
        Assert.Equal(1, http.Calls);
        Assert.Equal("ENDPOINT_OR_MODEL_UNEXPECTED", budget.HaltReason);
        Assert.Equal("BUDGET:HALTED:ENDPOINT_OR_MODEL_UNEXPECTED", run.Units[1].Failure);
        Assert.Equal("INCOMPLETE", run.Status);
    }

    [Fact]
    public void Audited_CostAboveTheReserve_HaltsTheRun()
    {
        var (run, _, http, budget) = RunMock(body => (HttpStatusCode.OK, Chat(Reply(DecideOf(body), new HashSet<string>(), Array.Empty<IReadOnlyList<string>>()), body, cost: 5.0)));
        Assert.Equal(1, http.Calls);
        Assert.Equal("PROVIDER_COST_OVER_RESERVE", budget.HaltReason);
        Assert.Equal("INCOMPLETE", run.Status);
    }

    [Fact]
    public void Audited_ServerError_IsOneAttempt_AndLeavesTheUnitUnresolved()
    {
        var (run, model, http, _) = RunMock(body => (HttpStatusCode.InternalServerError, "{}"));
        Assert.Equal(run.Units.Count, http.Calls);
        Assert.All(model.Ledger, e => Assert.Equal("TRANSPORT_FAILED", e.Verdict));
        Assert.Equal("CALL:TRANSPORT:SERVER_ERROR_NO_RETRY", run.Units[0].Failure);
        Assert.All(run.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    [Fact]
    public void Audited_KeyEchoedByTheServer_IsNotStored_AndHalts()
    {
        var (_, model, http, budget) = RunMock(body => (HttpStatusCode.OK, "{\"error\":\"" + Key + "\"}"));
        Assert.Equal(1, http.Calls);
        Assert.Equal("API_KEY_ECHOED", budget.HaltReason);
        Assert.Null(model.Ledger[0].RawStoreKey);
        Assert.False(Directory.EnumerateFiles(Path.Combine(scratch, "safe"), "*.bin", SearchOption.AllDirectories).Any());
    }

    // ---------------- the pinned plan ----------------

    string WritePlan(string commit)
    {
        var path = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(path, P7F1V6DirectD45.Serialize(P7F1V6DirectD45.PinnedValues(TestRepository.Root(), commit)));
        return path;
    }

    /// <summary>A safe root private to the current user, as an operator would prepare it.</summary>
    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    /// <summary>A test approval: the test plays the user. Never written outside the scratch directory.</summary>
    string Authorize(string planPath, string safeRoot, string? dataCollection = "TEST: allow accepted for a public RFC", bool acceptOpenItems = true)
    {
        var draft = P7F1V6DirectAuthorization.Draft(planPath, safeRoot);
        var path = Path.Combine(scratch, "auth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, P7F1V6DirectAuthorization.Approve(draft, "TEST_USER", "TEST approval text", dataCollection!,
            acceptOpenItems ? P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking : Array.Empty<string>(), DateTime.UtcNow));
        return path;
    }

    static string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Plan_PinsTheValuesAndVerifies()
    {
        var path = WritePlan("TEST_COMMIT");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var p = doc.RootElement;
        Assert.Equal("ecc538dc3d8e9cb3f8d73c3c10189f91bdc7b81f80c570ce658760e5d6f5db84", p.GetProperty("sourceSha256").GetString());
        Assert.Equal("1eee2105941098eb50e79fd866fddeb36718c49ca02da9906aeaaf2ba3673ead", p.GetProperty("universeSha256").GetString());
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, p.GetProperty("systemPromptSha256").GetString());
        Assert.Equal(48, p.GetProperty("segments").GetInt32());
        Assert.False(p.GetProperty("goldForScorerOnly").GetProperty("sentToProvider").GetBoolean());
        Assert.False(p.GetProperty("approvalToSend").GetBoolean());
        Assert.Empty(P7F1V6DirectD45.Verify(TestRepository.Root(), path, Sha(path), "TEST_COMMIT"));
    }

    [Fact]
    public void Plan_RefusesAWrongShaCommitOrTamperedFile()
    {
        var path = WritePlan("TEST_COMMIT");
        var sha = Sha(path);
        Assert.Contains("PLAN_SHA_NOT_CONFIRMED", P7F1V6DirectD45.Verify(TestRepository.Root(), path, new string('0', 64), "TEST_COMMIT"));
        Assert.Contains("CODE_COMMIT_DIFFERS", P7F1V6DirectD45.Verify(TestRepository.Root(), path, sha, "OTHER_COMMIT"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"maxCalls\": 100", "\"maxCalls\": 1000"));
        var failures = P7F1V6DirectD45.Verify(TestRepository.Root(), path, Sha(path), "TEST_COMMIT");
        Assert.Contains("PINNED_VALUES_DIFFER", failures);
    }

    [Fact]
    public void Plan_Execute_OverMockHttp_WithTheOracle_CompletesWithinTheBudget()
    {
        var path = WritePlan("TEST_COMMIT");
        var http = new Handler(body => (HttpStatusCode.OK, Chat(OracleReply(DecideOf(body)), body)));
        var root = SafeRoot("safe-plan");
        var auth = Authorize(path, root);
        var result = P7F1V6DirectD45.Execute(TestRepository.Root(), path, Sha(path), "TEST_COMMIT", auth, Key, http, root);

        Assert.Equal("COMPLETE", result.Outcome.Status);
        Assert.Equal("DONE", result.Outcome.Finalization);
        Assert.Equal(51, result.Outcome.Calls);
        Assert.Equal(51, http.Calls);
        Assert.True(result.Outcome.ReservedUsd <= P7F1V6DirectD45.CeilingUsd);
        Assert.True(result.Score.Official, result.Score.Reason);
        Assert.Equal(1.0, result.Score.HeadingOccurrence!.F1);
        Assert.All(result.Ledger, e => Assert.True(e.RequestBytes <= P7F1V6DirectD45.MaxRequestBytes));
    }

    [Fact]
    public void Plan_Execute_RefusesBeforeAnyCall_WhenThePlanIsNotConfirmed()
    {
        var path = WritePlan("TEST_COMMIT");
        var http = new Handler(body => (HttpStatusCode.OK, "{}"));
        var e = Assert.Throws<InvalidOperationException>(() =>
            P7F1V6DirectD45.Execute(TestRepository.Root(), path, new string('0', 64), "TEST_COMMIT", Authorize(path, SafeRoot("safe-x")), Key, http, SafeRoot("safe-x")));
        Assert.StartsWith("PLAN_REFUSED:", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, http.Calls);
    }
}
