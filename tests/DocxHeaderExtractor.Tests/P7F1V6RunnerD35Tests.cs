using System.Net;
using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.5 (offline): the C1 runner. A counting fake port stands in for the network; nothing is sent anywhere.</summary>
public sealed class P7F1V6RunnerD35Tests : IDisposable
{
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d35-" + Guid.NewGuid().ToString("N"));

    public P7F1V6RunnerD35Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    static P7F1V6Runner.PinSet Pins(string tag = "a") => new(tag + "-src", tag + "-uni", tag + "-cand", tag + "-man");

    static readonly P7F1V6Protocol.EndpointRecord[] E1 =
    [
        new("alibaba", "qwen/qwen3.7-flash", 1000000, 65536),
        new("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536),
    ];

    static P7F1V6Runner.Inputs Good(bool publicOk = true, bool fresh = true, bool c0 = true, bool live = true) =>
        new(Pins(), Pins(), "OK", publicOk, E1, fresh, true, c0, live);

    static Dictionary<string, bool> ByName(IEnumerable<P7F1V6Runner.Gate> g) => g.ToDictionary(x => x.Name, x => x.Passed);

    /// <summary>A port that replays a fixed list of outcomes and counts every call it receives.</summary>
    sealed class FakePort : P7F1V6Runner.ISendPort
    {
        readonly Queue<P7F1V6OpenRouterTransport.SendResult> outcomes;
        public readonly List<string> SentSha = new();
        public FakePort(params P7F1V6OpenRouterTransport.SendResult[] results) => outcomes = new Queue<P7F1V6OpenRouterTransport.SendResult>(results);

        public P7F1V6OpenRouterTransport.SendResult Send(P7F1V6Protocol.PreparedRequest request)
        {
            SentSha.Add(request.Sha256);
            return outcomes.Count > 0 ? outcomes.Dequeue() : new P7F1V6OpenRouterTransport.SendResult("HTTP_OK", 200, Chat("{}"), request.Sha256, request.Bytes, 1);
        }
    }

    static string Chat(string content) => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" +
        System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    static P7F1V6Protocol.PreparedRequest Prepared(string payload = "{\"owned\":[]}") =>
        P7F1V6Protocol.Prepare(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), payload, "alibaba", 32768);

    static P7F1V6OpenRouterTransport.SendResult Fail(string outcome) => new(outcome, null, null, null, null, 1);

    // ---------------- gates: each refusal has its own reason ----------------

    [Fact] public void The_gates_name_each_unmet_condition_with_its_own_reason()
    {
        var g = P7F1V6Runner.Gates(Good(publicOk: false, fresh: false, c0: false, live: false));
        Assert.Contains("SOURCE_NOT_AUTHORIZED", g.Single(x => x.Name == "SOURCE_AUTHORIZED").Reason);
        Assert.Contains("INVENTORY_NOT_FRESH", g.Single(x => x.Name == "INVENTORY_FRESH").Reason);
        Assert.Contains("DATA_POLICY_UNVERIFIED", g.Single(x => x.Name == "DATA_POLICY").Reason);
        Assert.Equal("C0_NOT_PASSED", g.Single(x => x.Name == "C0_REPLAY").Reason);
        Assert.Equal("LIVE_NOT_AUTHORISED", g.Single(x => x.Name == "LIVE_AUTHORISATION").Reason);
        Assert.False(P7F1V6Runner.AllPassed(g));
    }

    [Theory]
    [InlineData("candidates", "CANDIDATES_NOT_PINNED")]
    [InlineData("source", "SOURCE_NOT_PINNED")]
    [InlineData("universe", "UNIVERSE_NOT_PINNED")]
    [InlineData("manifest", "MANIFEST_NOT_PINNED")]
    public void Each_pin_is_checked_separately(string which, string code)
    {
        var observed = which switch
        {
            "candidates" => Pins() with { CandidatesSha256 = "x" },
            "source" => Pins() with { SourceSha256 = "x" },
            "universe" => Pins() with { UniverseSha256 = "x" },
            _ => Pins() with { ManifestSha256 = "x" },
        };
        var inputs = Good() with { Observed = observed };
        Assert.Contains(code, P7F1V6Runner.Gates(inputs).Single(x => x.Name == "CANDIDATE_PIN").Reason);
    }

    [Fact] public void The_route_gate_refuses_an_inventory_that_leaves_no_alibaba_endpoint()
    {
        var inputs = Good() with { Inventory = new[] { new P7F1V6Protocol.EndpointRecord("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536) } };
        var route = P7F1V6Runner.Gates(inputs).Single(x => x.Name == "ROUTE_POLICY");
        Assert.False(route.Passed);
        Assert.StartsWith("ROUTE_POLICY_UNKNOWN", route.Reason, StringComparison.Ordinal);
    }

    [Fact] public void The_real_current_state_is_refused_at_every_gate_that_is_not_met_and_sends_nothing()
    {
        // Real source gate result for 073 (prefix and pin ok), public status not confirmed, inventory not freshly read,
        // data policy unverified, no live authorisation.
        var repo = TestRepository.Root();
        var rel = "todo10_8/heading_corpus_100/05_bien_ban_hop/073_FORTIS_GC_Minutes_Mar_2026.pdf";
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, rel))));
        var gate = P7F1V6SourceGate.Check(repo, rel, sha);
        var pins = new P7F1V6Runner.PinSet(sha, Pins().UniverseSha256, Pins().CandidatesSha256, Pins().ManifestSha256);
        var inputs = new P7F1V6Runner.Inputs(pins, pins, gate.Code, PublicConfirmed: false,
            new[] { new P7F1V6Protocol.EndpointRecord("alibaba", "qwen/qwen3.7-flash", 1000000, 65536),
                    new P7F1V6Protocol.EndpointRecord("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536) },
            InventoryFresh: false, SafeStore: true, C0Passed: true, LiveAuthorised: false);
        var gates = P7F1V6Runner.Gates(inputs);
        Assert.Equal(true, gate.Allowed);
        Assert.False(P7F1V6Runner.AllPassed(gates));
        var failed = gates.Where(x => !x.Passed).Select(x => x.Name).ToArray();
        Assert.Contains("SOURCE_AUTHORIZED", failed);
        Assert.Contains("INVENTORY_FRESH", failed);
        Assert.Contains("DATA_POLICY", failed);
        Assert.Contains("LIVE_AUTHORISATION", failed);
        Assert.DoesNotContain("ROUTE_POLICY", failed);
    }

    // ---------------- cost reserve ----------------

    [Fact] public void The_worst_attempt_at_the_full_envelope_cap_matches_the_cost_bound()
    {
        var usd = P7F1V6Runner.WorstAttemptUsd(147456);
        Assert.Equal(147456 * 0.0000001 + 65536 * 0.0000004, usd, 12);
        Assert.True(6 * usd <= (double)P7F1V6Runner.CeilingUsd, "six worst-case attempts stay under the 0.30 ceiling");
        Assert.True(8 * usd > (double)P7F1V6Runner.CeilingUsd, "the ceiling is what limits the number of attempts, not only the count");
    }

    [Fact] public void The_dry_run_marks_the_line_where_the_reserve_would_pass_the_ceiling()
    {
        var planned = Enumerable.Range(0, 7).Select(_ => Prepared(new string('x', 140000))).ToArray();
        var lines = P7F1V6Runner.DryRun(planned);
        Assert.True(lines[5].WithinCeiling || lines[5].ReserveAfterUsd > 0.30);
        Assert.False(lines[6].WithinCeiling, "the seventh request is beyond the call limit and is not within the ceiling");
    }

    // ---------------- dispatch: the send path ----------------

    [Fact] public void A_request_with_no_response_is_retried_once_with_the_identical_body()
    {
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort(Fail("TRANSPORT_ERROR"), new P7F1V6OpenRouterTransport.SendResult("HTTP_OK", 200, Chat("{\"a\":1}"), "x", 1, 1));
        var p = Prepared();
        var content = state.Dispatch(p, port);
        Assert.Equal("{\"a\":1}", content);
        Assert.Equal(2, state.Attempts);
        Assert.Equal(1, state.RetriesUsed);
        Assert.Equal(new[] { p.Sha256, p.Sha256 }, port.SentSha);
    }

    [Fact] public void After_one_retry_a_second_missing_response_stops_the_run()
    {
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort(Fail("TIMEOUT_OR_CANCELLED"), Fail("TIMEOUT_OR_CANCELLED"));
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal("TRANSPORT_RETRY_EXHAUSTED", state.StopReason);
        Assert.Equal(2, state.Attempts);
    }

    [Theory]
    [InlineData("SERVER_ERROR_NO_RETRY")]
    [InlineData("RATE_LIMITED_NO_RETRY")]
    [InlineData("REDIRECT_REFUSED")]
    [InlineData("CLIENT_ERROR")]
    public void A_response_with_a_status_is_never_retried(string outcome)
    {
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort(Fail(outcome));
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal(1, state.Attempts);
        Assert.Equal("HTTP_NOT_RETRIED:" + outcome, state.StopReason);
    }

    [Fact] public void The_call_limit_stops_the_seventh_attempt()
    {
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort(Enumerable.Range(0, 6).Select(_ => new P7F1V6OpenRouterTransport.SendResult("HTTP_OK", 200, Chat("{}"), "x", 1, 1)).ToArray());
        var p = Prepared();
        for (int k = 0; k < 6; k++) Assert.NotNull(state.Dispatch(p, port));
        Assert.Null(state.Dispatch(p, port));
        Assert.Equal("CALL_LIMIT_EXCEEDED", state.StopReason);
        Assert.Equal(6, port.SentSha.Count);
    }

    [Fact] public void The_cost_reserve_stops_an_attempt_before_it_is_sent()
    {
        // A body of about one million bytes falls in the top price tier: about 0.25 USD per attempt in the worst case.
        // The first attempt fits under 0.30; the second would pass it, so it is refused before anything is sent.
        var big = Prepared(new string('y', 1000000));
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort();
        Assert.NotNull(state.Dispatch(big, port));
        Assert.Null(state.Dispatch(big, port));
        Assert.Equal("COST_CAP_EXCEEDED", state.StopReason);
        Assert.Equal(1, port.SentSha.Count);
        Assert.True(state.ReservedUsd <= (double)P7F1V6Runner.CeilingUsd);
    }

    [Fact] public void A_boundary_hash_mismatch_is_never_retried()
    {
        var state = new P7F1V6Runner.RunState(Passing());
        var port = new FakePort(Fail("BOUNDARY_SHA_MISMATCH"));
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal("BOUNDARY_SHA_MISMATCH", state.StopReason);
        Assert.Equal(1, state.Attempts);
    }

    // ---------------- retention and early deletion ----------------

    [Fact] public void The_retention_rule_deletes_at_thirty_days_or_seven_days_after_review()
    {
        var now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(P7F1V6RetentionPolicy.ShouldDelete(now.AddDays(-31), null, now));
        Assert.True(P7F1V6RetentionPolicy.ShouldDelete(now.AddDays(-5), now.AddDays(-8), now));
        Assert.False(P7F1V6RetentionPolicy.ShouldDelete(now.AddDays(-5), now.AddDays(-6), now));
        Assert.False(P7F1V6RetentionPolicy.ShouldDelete(now.AddDays(-29), null, now));
    }

    [Fact] public void Deleting_a_run_never_touches_the_source_pdf_it_was_built_from()
    {
        var pdf = Path.Combine(scratch, "todo10_8", "corpus", "sample.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 original source bytes");
        File.WriteAllBytes(pdf, bytes);
        var before = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf)));

        var run = P7F1V6SafeRawStore.CreateRun(scratch);
        run.Put(Encoding.UTF8.GetBytes("request body"));
        Directory.SetLastWriteTimeUtc(run.RunDirectory, DateTime.UtcNow.AddDays(-40));
        var expired = P7F1V6Retention.ExpiredRuns(scratch, DateTime.UtcNow, P7F1V6RetentionPolicy.MaxRetentionDays);
        Assert.Contains(run.RunDirectory, expired);
        P7F1V6Retention.DeleteRun(run.RunDirectory);

        Assert.False(Directory.Exists(run.RunDirectory));
        Assert.True(File.Exists(pdf));
        Assert.Equal(before, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf))));
    }

    [Fact] public void The_sweep_deletes_only_runs_past_the_rule_and_leaves_the_source_alone()
    {
        var pdf = Path.Combine(scratch, "todo10_8", "corpus", "keep.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllBytes(pdf, Encoding.UTF8.GetBytes("%PDF source"));
        var old = P7F1V6SafeRawStore.CreateRun(scratch);
        old.Put(Encoding.UTF8.GetBytes("old"));
        var recent = P7F1V6SafeRawStore.CreateRun(scratch);
        recent.Put(Encoding.UTF8.GetBytes("recent"));
        var now = DateTime.UtcNow;
        Directory.SetCreationTimeUtc(old.RunDirectory, now.AddDays(-31));
        var removed = P7F1V6Retention.Sweep(scratch, now, reviewCompletedUtc: null);
        Assert.Equal(new[] { old.RunDirectory }, removed);
        Assert.False(Directory.Exists(old.RunDirectory));
        Assert.True(Directory.Exists(recent.RunDirectory));
        Assert.True(File.Exists(pdf));
    }

    // A fixture that stands for "every gate met", so that the send mechanics can be tested on their own. The real gates are tested above.
    static IReadOnlyList<P7F1V6Runner.Gate> Passing() => new[] { new P7F1V6Runner.Gate("FIXTURE_ALL_GATES_MET", true, "test fixture") };

    [Fact] public void Nothing_is_sent_while_any_gate_is_unmet_and_the_refusal_names_the_gates()
    {
        var gates = P7F1V6Runner.Gates(Good(publicOk: false, fresh: false, c0: true, live: false));
        var state = new P7F1V6Runner.RunState(gates);
        var port = new FakePort();
        Assert.Null(state.Dispatch(Prepared(), port));
        Assert.Equal(0, port.SentSha.Count);
        Assert.StartsWith("GATES_NOT_PASSED:", state.StopReason, StringComparison.Ordinal);
        Assert.Contains("SOURCE_AUTHORIZED", state.StopReason);
        Assert.Contains("LIVE_AUTHORISATION", state.StopReason);
    }
}
