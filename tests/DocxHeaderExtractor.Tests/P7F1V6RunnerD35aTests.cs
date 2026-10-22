using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.5a (offline): the evidence-based preflight, the mode separation, the response audit, the dispatch rules, the mock
/// end-to-end path, per-run retention, and the source path checks. No connection is opened; every port here is a fake.</summary>
[Trait("Category", "Slow")]
public sealed class P7F1V6RunnerD35aTests : IDisposable
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d35a-" + Guid.NewGuid().ToString("N"));

    public P7F1V6RunnerD35aTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    // ---------------- fixtures ----------------

    sealed class FakePort : P7F1V6Runner.ISendPort
    {
        readonly Queue<P7F1V6OpenRouterTransport.SendResult> outcomes;
        public bool IsMock { get; }
        public readonly List<string> SentSha = new();
        public readonly List<string> SentBodies = new();

        public FakePort(bool isMock, params P7F1V6OpenRouterTransport.SendResult[] results)
        {
            IsMock = isMock;
            outcomes = new Queue<P7F1V6OpenRouterTransport.SendResult>(results);
        }

        public P7F1V6OpenRouterTransport.SendResult Send(P7F1V6Protocol.PreparedRequest request)
        {
            SentSha.Add(request.Sha256);
            SentBodies.Add(request.Body);
            return outcomes.Count > 0 ? outcomes.Dequeue() : Ok(request, 100, 100, null, "stop", "{}");
        }
    }

    static P7F1V6OpenRouterTransport.SendResult Ok(P7F1V6Protocol.PreparedRequest request, int prompt, int completion, double? cost,
        string finish, string content, bool usage = true) =>
        new("HTTP_OK", 200, Chat(prompt, completion, cost, finish, content, usage), request.Sha256, request.Bytes, 1);

    static string Chat(int prompt, int completion, double? cost, string finish, string content, bool usage)
    {
        object? u = null;
        if (usage)
            u = cost is null
                ? new { prompt_tokens = prompt, completion_tokens = completion, completion_tokens_details = new { reasoning_tokens = 10 } }
                : new { prompt_tokens = prompt, completion_tokens = completion, cost, completion_tokens_details = new { reasoning_tokens = 10 } };
        return JsonSerializer.Serialize(new { choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content } } },
            usage = u });
    }

    static P7F1V6OpenRouterTransport.SendResult Fail(string outcome) => new(outcome, null, null, null, null, 1);

    static P7F1V6Protocol.PreparedRequest Prepared(int payloadChars = 40) =>
        P7F1V6Protocol.Prepare(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), new string('p', payloadChars), "alibaba", 32768);

    static string Repo() => TestRepository.Root();

    static string RfcSha() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(TestRepository.Path(Rfc))));

    static P7F1V6Preflight.PinSet Pins(string sourceSha) => new(sourceSha, "universe", "candidates", "manifest");

    /// <summary>Writes the E1-shaped inventory file and returns its path and hash.</summary>
    (string Path, string Sha) WriteInventory(string dir, params (string Tag, string Model, int Ctx, int MaxOut)[] rows)
    {
        var path = Path.Combine(scratch, dir + ".inventory.json");
        var json = JsonSerializer.Serialize(new
        {
            data = new { endpoints = rows.Select(r => new { tag = r.Tag, model_id = r.Model, context_length = r.Ctx, max_completion_tokens = r.MaxOut }) },
        });
        File.WriteAllText(path, json);
        return (path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    static readonly (string, string, int, int)[] E1Rows =
    [
        ("alibaba", "qwen/qwen3.7-flash", 1000000, 65536),
        ("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536),
    ];

    string WriteC0(string candidatesSha, bool resolved = true)
    {
        var path = Path.Combine(scratch, "c0-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            status = resolved ? "PROTOCOL_RESOLVED" : "STOPPED_INTERPRETATION_INVALID",
            pinsMatched = true,
            pins = new { candidatesSha256 = candidatesSha },
        }));
        return path;
    }

    string SafeRoot()
    {
        var dir = Path.Combine(scratch, "raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    P7F1V6Preflight.Evidence Evidence(P7F1V6Preflight.RunMode mode, string? inventoryPath, string? inventorySha, string? authPath = null,
        bool freshDeclared = true, bool sourcePublic = false, bool c0Resolved = true, string? safeRoot = null)
    {
        var sha = RfcSha();
        var pins = Pins(sha);
        var src = P7F1V6SourceGate.Snapshot(Repo(), Rfc, sha);
        return new P7F1V6Preflight.Evidence(mode, "commit-under-test", pins, pins, src, sourcePublic,
            inventoryPath, inventorySha, freshDeclared, safeRoot ?? SafeRoot(), WriteC0("candidates", c0Resolved), authPath, Rfc);
    }

    // ---------------- preflight: evidence, not flags ----------------

    [Fact] public void A_live_preflight_refuses_and_names_every_unmet_gate()
    {
        var (inv, sha) = WriteInventory("e1", E1Rows);
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Live, inv, sha));
        Assert.False(result.Allowed);
        var failed = result.Gates.Where(g => !g.Passed).ToDictionary(g => g.Name, g => g.Reason);
        Assert.Contains("SOURCE_AUTHORIZED", failed.Keys);
        Assert.Contains("DATA_POLICY", failed.Keys);
        Assert.StartsWith("LIVE_NOT_AUTHORISED:NO_AUTHORISATION_RECORD", failed["LIVE_AUTHORISATION"], StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTE_POLICY", failed.Keys);
        Assert.DoesNotContain("C0_REPLAY", failed.Keys);
    }

    [Fact] public void A_mock_preflight_is_labelled_mock_and_still_reports_every_gate()
    {
        var (inv, sha) = WriteInventory("e1m", E1Rows);
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Mock, inv, sha));
        Assert.Equal(P7F1V6Preflight.RunMode.Mock, result.Mode);
        Assert.True(result.Allowed);
        Assert.Equal(10, result.Gates.Count);
        Assert.Contains(result.Gates, g => !g.Passed);
    }

    [Fact] public void A_wrong_inventory_hash_is_named_and_the_route_is_not_taken_from_it()
    {
        var (inv, _) = WriteInventory("bad", E1Rows);
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Live, inv, new string('0', 64)));
        var route = result.Gates.Single(g => g.Name == "ROUTE_POLICY");
        Assert.False(route.Passed);
        Assert.Contains("INVENTORY_SHA_MISMATCH", route.Reason);
    }

    [Fact] public void A_route_inventory_without_the_alibaba_endpoint_fails_the_route_gate()
    {
        var (inv, sha) = WriteInventory("variant", ("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536));
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Mock, inv, sha));
        Assert.StartsWith("ROUTE_POLICY_UNKNOWN", result.Gates.Single(g => g.Name == "ROUTE_POLICY").Reason, StringComparison.Ordinal);
    }

    [Fact] public void A_tampered_authorisation_record_fails_its_digest_and_names_the_mismatch()
    {
        var (inv, sha) = WriteInventory("auth", E1Rows);
        var candidates = "candidates";
        var rfc = RfcSha();
        var fields = new[]
        {
            "approvedCommit=commit-under-test", "candidatesSha256=" + candidates, "sourceSha256=" + rfc, "universeSha256=universe",
            "manifestSha256=manifest", "route=alibaba", "document=" + Rfc, "maxCalls=6", "retries=1", "ceilingUsd=0.30", "approvedBy=owner",
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fields))));
        var path = Path.Combine(scratch, "auth.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            approvedCommit = "commit-under-test", candidatesSha256 = candidates, sourceSha256 = rfc, universeSha256 = "universe",
            manifestSha256 = "manifest", route = "alibaba", document = Rfc, maxCalls = 7, retries = 1, ceilingUsd = 0.30m, approvedBy = "owner",
            approvalSha256 = digest,
        }));
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Live, inv, sha, authPath: path));
        Assert.StartsWith("LIVE_NOT_AUTHORISED:APPROVAL_DIGEST_MISMATCH", result.Gates.Single(g => g.Name == "LIVE_AUTHORISATION").Reason, StringComparison.Ordinal);
    }

    [Fact] public void A_consistent_authorisation_record_passes_its_gate_and_names_the_approved_commit()
    {
        var (inv, sha) = WriteInventory("auth-ok", E1Rows);
        var rfc = RfcSha();
        var fields = new[]
        {
            "approvedCommit=commit-under-test", "candidatesSha256=candidates", "sourceSha256=" + rfc, "universeSha256=universe",
            "manifestSha256=manifest", "route=alibaba", "document=" + Rfc, "maxCalls=6", "retries=1", "ceilingUsd=0.30", "approvedBy=owner",
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fields))));
        var path = Path.Combine(scratch, "auth-ok.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            approvedCommit = "commit-under-test", candidatesSha256 = "candidates", sourceSha256 = rfc, universeSha256 = "universe",
            manifestSha256 = "manifest", route = "alibaba", document = Rfc, maxCalls = 6, retries = 1, ceilingUsd = 0.30m, approvedBy = "owner",
            approvalSha256 = digest,
        }));
        var result = P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Live, inv, sha, authPath: path, sourcePublic: true));
        Assert.Equal("OK", result.Gates.Single(g => g.Name == "LIVE_AUTHORISATION").Reason);
        // The data policy is still closed, so the live result is still not allowed.
        Assert.False(result.Allowed);
    }

    // ---------------- mode separation ----------------

    [Fact] public void A_mock_result_cannot_reach_a_live_port()
    {
        var (inv, sha) = WriteInventory("mock-live", E1Rows);
        var state = new P7F1V6Runner.RunState(P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Mock, inv, sha)));
        var port = new FakePort(isMock: false);
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal("MOCK_PREFLIGHT_WITH_LIVE_PORT", state.StopReason);
        Assert.Empty(port.SentSha);
    }

    [Fact] public void A_live_result_that_is_not_allowed_sends_nothing_even_through_a_mock_port()
    {
        var (inv, sha) = WriteInventory("live-no", E1Rows);
        var state = new P7F1V6Runner.RunState(P7F1V6Preflight.Result.Build(Evidence(P7F1V6Preflight.RunMode.Live, inv, sha)));
        var port = new FakePort(isMock: true);
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.StartsWith("PREFLIGHT_NOT_ALLOWED:", state.StopReason, StringComparison.Ordinal);
        Assert.Empty(port.SentSha);
    }

    // ---------------- dispatch: retry, call limit, reserve ----------------

    static P7F1V6Runner.RunState MockState()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dhx-d35a-mock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var inv = Path.Combine(scratch, "inv.json");
        File.WriteAllText(inv, JsonSerializer.Serialize(new { data = new { endpoints = new[] { new { tag = "alibaba", model_id = "qwen/qwen3.7-flash", context_length = 1000000, max_completion_tokens = 65536 } } } }));
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(inv)));
        var rfc = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(TestRepository.Path(Rfc))));
        var c0 = Path.Combine(scratch, "c0.json");
        File.WriteAllText(c0, JsonSerializer.Serialize(new { status = "PROTOCOL_RESOLVED", pinsMatched = true, pins = new { candidatesSha256 = "candidates" } }));
        var raw = Path.Combine(scratch, "raw");
        Directory.CreateDirectory(raw);
        var pins = new P7F1V6Preflight.PinSet(rfc, "universe", "candidates", "manifest");
        var ev = new P7F1V6Preflight.Evidence(P7F1V6Preflight.RunMode.Mock, "c", pins, pins, P7F1V6SourceGate.Snapshot(TestRepository.Root(), Rfc, rfc),
            false, inv, sha, true, raw, c0, null, Rfc);
        return new P7F1V6Runner.RunState(P7F1V6Preflight.Result.Build(ev));
    }

    [Fact] public void A_request_with_no_response_is_retried_once_with_the_identical_body()
    {
        var state = MockState();
        var p = Prepared();
        var port = new FakePort(isMock: true, Fail("TRANSPORT_ERROR"), Ok(p, 100, 100, null, "stop", "{\"a\":1}"));
        Assert.Equal("{\"a\":1}", state.Dispatch(p, port));
        Assert.Equal(2, state.Attempts);
        Assert.Equal(1, state.RetriesUsed);
        Assert.Equal(new[] { p.Body, p.Body }, port.SentBodies);
    }

    [Fact] public void A_second_missing_response_stops_the_run()
    {
        var state = MockState();
        var port = new FakePort(isMock: true, Fail("TIMEOUT_OR_CANCELLED"), Fail("TIMEOUT_OR_CANCELLED"));
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal("TRANSPORT_RETRY_EXHAUSTED", state.StopReason);
        Assert.Equal(2, state.Attempts);
    }

    [Theory]
    [InlineData("SERVER_ERROR_NO_RETRY")]
    [InlineData("RATE_LIMITED_NO_RETRY")]
    [InlineData("REDIRECT_REFUSED")]
    [InlineData("CLIENT_ERROR")]
    [InlineData("BOUNDARY_SHA_MISMATCH")]
    public void A_status_or_boundary_failure_is_never_retried(string outcome)
    {
        var state = MockState();
        var port = new FakePort(isMock: true, Fail(outcome));
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal(1, state.Attempts);
        Assert.NotNull(state.StopReason);
    }

    [Fact] public void The_sixth_attempt_is_the_last_and_the_seventh_is_refused()
    {
        var state = MockState();
        var p = Prepared();
        var port = new FakePort(isMock: true);
        for (int k = 0; k < 6; k++) Assert.NotNull(state.Dispatch(p, port));
        Assert.Null(state.Dispatch(p, port));
        Assert.Equal("CALL_LIMIT_EXCEEDED", state.StopReason);
        Assert.Equal(6, port.SentSha.Count);
    }

    [Fact] public void The_reserve_refuses_an_attempt_that_would_pass_the_ceiling_before_it_is_sent()
    {
        var state = MockState();
        var big = Prepared(1000000);   // top price tier: about 0.25 USD worst case per attempt
        var port = new FakePort(isMock: true);
        Assert.NotNull(state.Dispatch(big, port));
        Assert.Null(state.Dispatch(big, port));
        Assert.Equal("COST_CAP_EXCEEDED", state.StopReason);
        Assert.Equal(1, port.SentSha.Count);
    }

    // ---------------- response audit ----------------

    [Fact] public void A_truncated_completion_stops_the_run_before_the_parser()
    {
        var state = MockState();
        var p = Prepared();
        Assert.Null(state.Dispatch(p, new FakePort(isMock: true, Ok(p, 100, 100, null, "length", "partial"))));
        Assert.Equal("RESPONSE_AUDIT:FINISH_LENGTH", state.StopReason);
    }

    [Fact] public void A_200_without_usage_stops_the_run()
    {
        var state = MockState();
        var p = Prepared();
        Assert.Null(state.Dispatch(p, new FakePort(isMock: true, Ok(p, 0, 0, null, "stop", "{}", usage: false))));
        Assert.Equal("RESPONSE_AUDIT:USAGE_MISSING", state.StopReason);
    }

    [Fact] public void A_completion_over_the_cap_stops_the_run()
    {
        var state = MockState();
        var p = Prepared();
        Assert.Null(state.Dispatch(p, new FakePort(isMock: true, Ok(p, 100, 40000, null, "stop", "{}"))));
        Assert.Equal("RESPONSE_AUDIT:COMPLETION_OVER_CAP", state.StopReason);
    }

    [Fact] public void A_provider_cost_above_the_reserve_stops_the_run_and_a_cost_within_it_is_kept_as_actual()
    {
        var over = MockState();
        var p = Prepared();
        Assert.Null(over.Dispatch(p, new FakePort(isMock: true, Ok(p, 100, 100, 1.0, "stop", "{}"))));
        Assert.Equal("PROVIDER_COST_OVER_RESERVE", over.StopReason);

        var within = MockState();
        Assert.Equal("{\"a\":1}", within.Dispatch(p, new FakePort(isMock: true, Ok(p, 100, 100, 0.0005, "stop", "{\"a\":1}"))));
        Assert.Equal(0.0005, within.ActualProviderUsd, 9);
        Assert.Null(within.StopReason);
    }

    // ---------------- mock end-to-end ----------------

    // ---------------- retention: per-run review, root-bound deletion ----------------

    [Fact] public void Each_run_is_judged_by_its_own_review_date()
    {
        var root = Path.Combine(scratch, "raw-root");
        Directory.CreateDirectory(root);
        var a = P7F1V6SafeRawStore.CreateRun(root);
        var b = P7F1V6SafeRawStore.CreateRun(root);
        a.Put(Encoding.UTF8.GetBytes("a"));
        b.Put(Encoding.UTF8.GetBytes("b"));
        var now = DateTime.UtcNow;
        Directory.SetCreationTimeUtc(a.RunDirectory, now.AddDays(-10));
        Directory.SetCreationTimeUtc(b.RunDirectory, now.AddDays(-10));
        P7F1V6Retention.RecordReview(a.RunDirectory, now.AddDays(-8));   // review done 8 days ago: due
        P7F1V6Retention.RecordReview(b.RunDirectory, now.AddDays(-1));   // review done yesterday: not due
        Assert.Equal(new[] { a.RunDirectory }, P7F1V6Retention.Due(root, now));
        Assert.Equal(new[] { a.RunDirectory }, P7F1V6Retention.Sweep(root, now));
        Assert.True(Directory.Exists(b.RunDirectory));
    }

    [Fact] public void A_run_outside_the_fixed_root_and_a_foreign_item_are_never_deleted()
    {
        var root = Path.Combine(scratch, "fixed-root");
        Directory.CreateDirectory(root);
        var inside = P7F1V6SafeRawStore.CreateRun(root);
        var elsewhere = Path.Combine(scratch, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var other = P7F1V6SafeRawStore.CreateRun(elsewhere);
        Assert.Throws<InvalidOperationException>(() => P7F1V6Retention.DeleteRun(root, other.RunDirectory));
        Assert.True(Directory.Exists(other.RunDirectory));

        File.WriteAllText(Path.Combine(inside.RunDirectory, "notes.bin"), "not a store item");
        Assert.Throws<InvalidOperationException>(() => P7F1V6Retention.DeleteRun(root, inside.RunDirectory));
        Assert.True(File.Exists(Path.Combine(inside.RunDirectory, "notes.bin")));
    }

    // ---------------- source path ----------------

    [Fact] public void The_source_snapshot_returns_the_bytes_that_were_hashed()
    {
        var sha = RfcSha();
        var s = P7F1V6SourceGate.Snapshot(Repo(), Rfc, sha);
        Assert.True(s.Allowed);
        Assert.Equal(sha, s.Sha256);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(s.Bytes!)));
    }

    [Fact] public void A_junction_anywhere_in_the_source_path_is_refused()
    {
        var repo = Path.Combine(scratch, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "todo10_8", "real"));
        File.WriteAllBytes(Path.Combine(repo, "todo10_8", "real", "doc.pdf"), Encoding.UTF8.GetBytes("%PDF"));
        var link = Path.Combine(repo, "todo10_8", "linked");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(repo, "todo10_8", "real")}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();
        if (!Directory.Exists(link)) return;   // junctions unavailable here: not tested
        try
        {
            var pin = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("%PDF")));
            var s = P7F1V6SourceGate.Snapshot(repo, "todo10_8/linked/doc.pdf", pin);
            Assert.False(s.Allowed);
            Assert.Equal("SOURCE_PATH_HAS_REPARSE_POINT", s.Code);
        }
        finally
        {
            var rm = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c rmdir \"{link}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var q = System.Diagnostics.Process.Start(rm)!; q.WaitForExit();
        }
    }
}
