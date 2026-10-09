using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7F1FinancialRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static string Hash(char c) => new(c, 64);
    private static byte[] Metadata() => File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-endpoint-snapshot.v1/endpoint-metadata.json"));
    private static P7RunnerRawObservation Observation(JsonElement? usage = null) => new("{\"fixture\":true}", "stop",
        usage ?? SpatialCanonical.Element(new { prompt_tokens = 200, completion_tokens = 40, cost = 0.01m }), "fixture SSE", 1, 0);
    private static JsonElement Json(string raw) { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
    private sealed class FakeTransport : IP7F1RunnerTransport {
        public P7RunnerTransportPolicy Policy { get; set; } = new(P7FinancialQualification.ChatEndpoint, "qwen/qwen3.7-flash", "alibaba", 0, false, 300, true);
        public Func<byte[], CancellationToken, Task<P7RunnerRawObservation>> OnSend { get; set; } = (_, _) => Task.FromResult(Observation());
        public List<byte[]> Bodies { get; } = [];
        public Action? BeforeSend { get; set; }
        public Task<P7RunnerRawObservation> SendOnceAsync(byte[] body, CancellationToken ct) { BeforeSend?.Invoke(); Bodies.Add(body); return OnSend(body, ct); }
    }
    private sealed class MemoryJournal : IP7F1RunnerJournal {
        public List<string> Events { get; } = [];
        public List<byte[]> Financial { get; } = [];
        public string? Fail { get; set; }
        public bool ForgeRawReceipt { get; set; }
        private void Event(string kind) { if (Fail == kind) throw new IOException("FIXTURE_STORAGE_" + kind); Events.Add(kind); }
        public Task BeginAsync(byte[] identity, CancellationToken ct) { Event("begin"); return Task.CompletedTask; }
        public Task ReservationAsync(string h, byte[] b, CancellationToken ct) { Event("reserve"); return Task.CompletedTask; }
        public Task<P7RunnerRawReceipt> FreezeRawAsync(string h, byte[] b, P7RunnerRawObservation o) {
            Event("raw");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { o.Content, o.FinishReason, o.Usage, o.RawSse, o.SseEventCount, o.RetryCount });
            return Task.FromResult(new P7RunnerRawReceipt(ForgeRawReceipt ? Hash('e') : SpatialCanonical.Hash(bytes),
                SpatialCanonical.Hash(Encoding.UTF8.GetBytes(o.Content)), SpatialCanonical.Hash(Encoding.UTF8.GetBytes(o.RawSse)), Hash('d'))); }
        public Task FinancialAsync(string h, byte[] r) { Event("financial"); Financial.Add(r); return Task.CompletedTask; }
        public Task CompleteAsync(byte[] r) { Event("complete"); return Task.CompletedTask; }
    }
    private sealed class Harness {
        public P7F1ExecutionPlan Plan { get; }
        public Dictionary<string, byte[]> Bodies { get; } = [];
        public P7EndpointBinding Binding { get; } = P7FinancialQualification.Bind(Metadata());
        public FakeTransport Transport { get; } = new();
        public MemoryJournal Journal { get; } = new();
        public Func<CancellationToken, Task<P7RunnerMetadata>> GetMetadata { get; set; } = _ => Task.FromResult(new P7RunnerMetadata(Metadata(), Now));
        public Harness() {
            var p = P7F1ExecutionReadiness.Prepare(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json")));
            // Synthetic test bodies ONLY. Production hashes are tested by the full frozen-body dry-run CLI, not replaced here.
            var calls = p.Calls.Select(c => {
                var user = JsonSerializer.Serialize(new { occurrences = new[] { new { id = "O1" } }, fixtureCall = c.CallHandle });
                var body = new OpenRouterQwen37InferenceRequestComposer().Build("Financial runner fixture", user, 32768);
                Bodies.Add(c.CallHandle, body);
                return c with { Identity = c.Identity with { ProviderBodySha256 = SpatialCanonical.Hash(body),
                    SystemPromptSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes("Financial runner fixture")),
                    UserMessageSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(user)) } };
            }).ToArray();
            Plan = p with { Calls = calls };
        }
        public P7RunnerApproval Grant(decimal cap = 1m) => new(P7FinancialQualification.ExecutionManifestSha256,
            P7F1FinancialRunner.FinancialManifestSha256, Hash('a'), true, cap, true, cap, cap, Hash('b'),
            Plan.Calls.Select(c => new P7RunnerBound(c.CallHandle, c.Identity.ProviderBodySha256, Binding.BindingSha256,
                0.05m, Hash('c'), "FIXTURE_ONLY_ALL_CHARGES_BOUND")).ToArray(), true);
        public P7F1FinancialRunner Runner(IP7F1RunnerTransport? transport = null, bool fixture = true, P7F1ExecutionPlan? plan = null) => new(
            plan ?? Plan, Bodies, Binding, transport ?? Transport, Journal, ct => GetMetadata(ct), () => Now, fixture,
            fixture ? TimeSpan.FromMilliseconds(100) : null);
    }

    [Fact] public async Task All_fourteen_calls_reserve_send_freeze_account_in_order_exact_bytes_no_semantic_claim()
    {
        var h = new Harness();
        h.Transport.BeforeSend = () => Assert.Equal("reserve", h.Journal.Events.Last());
        var r = await h.Runner().RunAsync(h.Grant());
        Assert.Equal("RAW_ATTEMPTS_COMPLETED", r.Status); Assert.Null(r.HaltReason); Assert.Equal(14, r.TransportInvocations);
        Assert.Equal(0.14m, r.KnownCostUsd); Assert.Equal(0, r.OutstandingExposureUsd);
        Assert.Equal("NOT_PERFORMED_RAW_CAPTURE_ONLY", r.ProtocolValidation); Assert.Equal("NOT_EVALUATED", r.SemanticAccuracy);
        Assert.Equal("LOCKED", r.DownstreamExecution); Assert.All(r.Calls, c => Assert.Equal("RAW_CAPTURED_PENDING_PROTOCOL_VALIDATION", c.Status));
        Assert.Equal(new[] { "begin" }.Concat(Enumerable.Range(0, 14).SelectMany(_ => new[] { "reserve", "raw", "financial" })).Append("complete"), h.Journal.Events);
        for (var i = 0; i < 14; i++) Assert.Equal(h.Bodies[h.Plan.Calls[i].CallHandle], h.Transport.Bodies[i]);
    }

    [Theory] [InlineData("missing")] [InlineData("calls")] [InlineData("cap")] [InlineData("key")] [InlineData("key_cap")]
    [InlineData("remaining")] [InlineData("approval_hash")] [InlineData("key_hash")]
    [InlineData("execution")] [InlineData("financial")] [InlineData("fixture_mode")]
    public async Task No_authorization_budget_or_key_binding_means_zero_dispatches(string error)
    {
        var h = new Harness(); var a = h.Grant(); a = error switch {
            "calls" => a with { CallsAuthorized = false }, "cap" => a with { ApprovedUsdCap = null },
            "key" => a with { DedicatedKeyVerified = false }, "key_cap" => a with { KeyLimit = 2 },
            "remaining" => a with { InitialKeyRemaining = null }, "approval_hash" => a with { ApprovalReceiptSha256 = null },
            "key_hash" => a with { KeyReceiptSha256 = null }, "execution" => a with { ExecutionManifestSha256 = Hash('e') },
            "financial" => a with { FinancialManifestSha256 = Hash('e') }, "fixture_mode" => a with { FixtureOnly = false }, _ => a };
        var r = await h.Runner().RunAsync(error == "missing" ? null : a);
        Assert.Equal("HALTED", r.Status); Assert.Empty(h.Transport.Bodies); Assert.Equal(0, r.TransportInvocations);
        Assert.DoesNotContain("reserve", h.Journal.Events); Assert.All(r.Calls, c => Assert.False(c.Attempted));
    }

    [Theory] [InlineData("stress")] [InlineData("missing")] [InlineData("body")] [InlineData("endpoint")] [InlineData("proof")] [InlineData("zero")]
    public async Task Missing_or_advertised_stress_bounds_are_not_accepted_as_exposure_proof(string error)
    {
        var h = new Harness(); var a = h.Grant(); var bounds = a.Bounds.ToArray();
        bounds[0] = error switch {
            "stress" => bounds[0] with { Basis = "ADVERTISED_LIMIT_STRESS_ESTIMATE_NOT_VERIFIED_BILLING_BOUND" },
            "body" => bounds[0] with { BodySha256 = Hash('e') }, "endpoint" => bounds[0] with { BindingSha256 = Hash('e') },
            "proof" => bounds[0] with { ProofSha256 = "" }, "zero" => bounds[0] with { MaximumUsd = 0 }, _ => bounds[0] };
        var r = await h.Runner().RunAsync(a with { Bounds = error == "missing" ? [] : bounds });
        Assert.Equal("VERIFIED_EXPOSURE_BOUND_MISSING_OR_DRIFT", r.HaltReason); Assert.Empty(h.Transport.Bodies);
    }

    [Theory] [InlineData("endpoint")] [InlineData("model")] [InlineData("provider")] [InlineData("retries")] [InlineData("fallback")] [InlineData("timeout")]
    public async Task Transport_cannot_bypass_pinned_policy(string error)
    {
        var h = new Harness(); var p = h.Transport.Policy;
        h.Transport.Policy = error switch {
            "endpoint" => p with { Endpoint = "https://different/v1/chat/completions" }, "model" => p with { Model = "different" },
            "provider" => p with { Provider = "different" }, "retries" => p with { Retries = 2 },
            "fallback" => p with { Fallback = true }, "timeout" => p with { TimeoutSeconds = 90 }, _ => p };
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal("TRANSPORT_BINDING_INVALID", r.HaltReason); Assert.Empty(h.Transport.Bodies);
    }

    [Fact] public async Task Fixture_approval_cannot_reach_OpenRouter_or_even_read_a_key()
    {
        var h = new Harness(); var reads = 0;
        using var live = new P7OpenRouterF1RunnerTransport(() => { reads++; throw new InvalidOperationException("NO_KEY_ACCESS"); });
        var r = await h.Runner(live, fixture: false).RunAsync(h.Grant());
        Assert.Equal("AUTHORIZATION_MISSING_OR_DRIFT", r.HaltReason); Assert.Equal(0, r.TransportInvocations); Assert.Equal(0, reads);
    }

    [Theory] [InlineData("price")] [InlineData("stale")] [InlineData("future")]
    public async Task Fresh_endpoint_binding_is_checked_before_every_start(string error)
    {
        var h = new Harness(); var reads = 0;
        h.GetMetadata = _ => {
            reads++; var bytes = Metadata(); var time = Now;
            if (reads == 2) {
                if (error == "price") { var n = JsonNode.Parse(bytes)!; n["data"]!["endpoints"]![0]!["pricing"]!["prompt"] = "0.00000004"; bytes = JsonSerializer.SerializeToUtf8Bytes(n); }
                else time = error == "stale" ? Now.AddSeconds(-301) : Now.AddSeconds(1);
            }
            return Task.FromResult(new P7RunnerMetadata(bytes, time));
        };
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal(1, r.TransportInvocations); Assert.Single(h.Transport.Bodies);
        Assert.False(r.Calls[1].Attempted); Assert.Equal(0, r.OutstandingExposureUsd);
    }

    [Fact] public async Task Entire_universe_is_validated_before_first_call()
    {
        var h = new Harness(); h.Bodies[h.Plan.Calls[13].CallHandle][0] ^= 1;
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal(0, r.TransportInvocations); Assert.Empty(h.Transport.Bodies);
    }

    [Fact] public async Task Payload_is_snapshotted_so_caller_mutation_cannot_change_a_future_request()
    {
        var h = new Harness(); var expected = h.Bodies[h.Plan.Calls[13].CallHandle].ToArray(); var runner = h.Runner();
        h.Transport.BeforeSend = () => h.Bodies[h.Plan.Calls[13].CallHandle][0] ^= 1;
        var r = await runner.RunAsync(h.Grant()); Assert.Equal(14, r.TransportInvocations); Assert.Equal(expected, h.Transport.Bodies[13]);
    }

    [Fact] public async Task Policy_drift_after_first_response_stops_before_second_reservation()
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => { h.Transport.Policy = h.Transport.Policy with { Retries = 2 }; return Task.FromResult(Observation()); };
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal("TRANSPORT_BINDING_INVALID", r.HaltReason); Assert.Equal(1, r.TransportInvocations);
    }

    [Fact] public async Task Exposure_proofs_are_snapshotted_before_async_work()
    {
        var h = new Harness(); var a = h.Grant(); var original = (P7RunnerBound[])a.Bounds;
        h.Transport.BeforeSend = () => original[13] = original[13] with { MaximumUsd = 0 };
        var r = await h.Runner().RunAsync(a); Assert.Equal(14, r.TransportInvocations); Assert.Equal(0.05m, r.Calls[13].ReservedUsd);
    }

    [Fact] public async Task Forged_capture_acknowledgement_halts_without_settling_or_fanout()
    {
        var h = new Harness(); h.Journal.ForgeRawReceipt = true;
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal("RAW_RECEIPT_INVALID", r.HaltReason);
        Assert.Equal(1, r.TransportInvocations); Assert.Equal(0.05m, r.OutstandingExposureUsd); Assert.Empty(h.Journal.Financial);
    }

    [Fact] public async Task Budget_is_checked_before_next_request_not_after_it()
    {
        var h = new Harness(); var r = await h.Runner().RunAsync(h.Grant(0.055m));
        Assert.Equal("NEXT_REQUEST_EXCEEDS_CAP", r.HaltReason); Assert.Equal(1, r.TransportInvocations);
        Assert.Equal(0.01m, r.KnownCostUsd); Assert.False(r.Calls[1].Attempted);
    }

    [Theory] [InlineData("{}", 0)] [InlineData("{\"cost\":0.02}", 0.02)]
    [InlineData("{\"prompt_tokens\":200,\"completion_tokens\":40}", 0)]
    public async Task Incomplete_usage_halts_and_retains_outstanding_exposure(string usage, double known)
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => Task.FromResult(Observation(Json(usage)));
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal("USAGE_OR_COST_INCOMPLETE_HALT", r.HaltReason);
        Assert.Equal(0.05m, r.OutstandingExposureUsd); Assert.Equal((decimal)known, r.KnownCostUsd); Assert.Equal(1, r.TransportInvocations);
        Assert.All(r.Calls.Skip(1), c => Assert.Equal("NOT_ATTEMPTED", c.Status));
    }

    [Fact] public async Task Null_usage_is_unknown_not_zero_even_when_response_arrives()
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => Task.FromResult(Observation() with { Usage = null });
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Null(r.Calls[0].CostUsd); Assert.Equal(0.05m, r.OutstandingExposureUsd);
    }

    [Fact] public async Task Cost_overrun_records_actual_unclamped_charge_and_halts()
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => Task.FromResult(Observation(Json("{\"prompt_tokens\":200,\"completion_tokens\":40,\"cost\":1.2}")));
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal("BOUND_EXCEEDED_HALT", r.HaltReason); Assert.Equal(1.2m, r.KnownCostUsd);
        Assert.Equal(0, r.OutstandingExposureUsd); Assert.Equal(1, r.TransportInvocations);
    }

    [Fact] public async Task Timeout_ignoring_cancellation_keeps_exposure_and_quarantines_late_result()
    {
        var h = new Harness(); var late = new TaskCompletionSource<P7RunnerRawObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Transport.OnSend = (_, _) => late.Task; var runner = h.Runner(); var r = await runner.RunAsync(h.Grant());
        Assert.Equal("TIMEOUT_UNKNOWN_PAYMENT", r.HaltReason); Assert.Equal(0.05m, r.OutstandingExposureUsd); Assert.Equal(1, r.TransportInvocations);
        var events = h.Journal.Events.ToArray(); late.SetResult(Observation()); await Task.Yield();
        Assert.Equal(events, h.Journal.Events); Assert.Single(h.Transport.Bodies); Assert.Empty(h.Journal.Financial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(h.Grant()));
    }

    [Fact] public async Task Cancel_during_call_does_not_retry_or_release_unknown_payment()
    {
        var h = new Harness(); using var cancel = new CancellationTokenSource();
        h.Transport.OnSend = (_, _) => { cancel.Cancel(); return new TaskCompletionSource<P7RunnerRawObservation>().Task; };
        var r = await h.Runner().RunAsync(h.Grant(), cancel.Token); Assert.Equal("CANCELLED_NO_REPLAY", r.HaltReason);
        Assert.Equal(1, r.TransportInvocations); Assert.Equal(0.05m, r.OutstandingExposureUsd);
    }

    [Fact] public async Task Cancel_before_start_has_zero_provider_exposure()
    {
        var h = new Harness(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var r = await h.Runner().RunAsync(h.Grant(), cancel.Token); Assert.Empty(h.Transport.Bodies); Assert.Equal(0, r.OutstandingExposureUsd);
    }

    [Fact] public async Task Already_received_usage_is_accounted_even_when_cancel_arrives_at_completion()
    {
        var h = new Harness(); using var cancel = new CancellationTokenSource();
        h.Transport.OnSend = (_, _) => { cancel.Cancel(); return Task.FromResult(Observation()); };
        var r = await h.Runner().RunAsync(h.Grant(), cancel.Token);
        Assert.Equal(1, r.TransportInvocations); Assert.Equal(0.01m, r.KnownCostUsd); Assert.Equal(0, r.OutstandingExposureUsd);
        Assert.Single(h.Journal.Financial); Assert.Equal("CANCELLED_NO_REPLAY", r.HaltReason);
    }

    [Fact] public void Frozen_package_rejects_changed_financial_execution_request_or_metadata_bytes()
    {
        var execution = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d3.f1-execution-readiness.v1.json"));
        var financial = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-qualification.v2.json"));
        var requests = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json"));
        Assert.Equal(14, P7F1FinancialRunner.ValidateFrozenPackage(execution, financial, requests, Metadata()).Calls.Count);
        byte[] Change(byte[] bytes) => bytes.Concat(new byte[] { 32 }).ToArray();
        Assert.Throws<InvalidOperationException>(() => P7F1FinancialRunner.ValidateFrozenPackage(Change(execution), financial, requests, Metadata()));
        Assert.Throws<InvalidOperationException>(() => P7F1FinancialRunner.ValidateFrozenPackage(execution, Change(financial), requests, Metadata()));
        Assert.Throws<InvalidOperationException>(() => P7F1FinancialRunner.ValidateFrozenPackage(execution, financial, Change(requests), Metadata()));
        Assert.Throws<InvalidOperationException>(() => P7F1FinancialRunner.ValidateFrozenPackage(execution, financial, requests, Change(Metadata())));
    }

    [Fact] public async Task Transport_exception_is_not_a_retry_or_free_request()
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => throw new HttpRequestException("FIXTURE_TRANSPORT_ERROR");
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Single(h.Transport.Bodies); Assert.Equal(0.05m, r.OutstandingExposureUsd);
        Assert.Equal("TRANSPORT_FAILURE_UNKNOWN_PAYMENT", r.Calls[0].Status);
    }

    [Theory] [InlineData("begin", 0, 0)] [InlineData("reserve", 0, 0)] [InlineData("raw", 1, 0.05)]
    [InlineData("financial", 1, 0)] [InlineData("complete", 14, 0)]
    public async Task Storage_failure_never_fans_out_or_replays(string phase, int calls, double exposure)
    {
        var h = new Harness(); h.Journal.Fail = phase; var runner = h.Runner(); var r = await runner.RunAsync(h.Grant());
        Assert.Equal("HALTED", r.Status); Assert.Equal(calls, r.TransportInvocations); Assert.Equal((decimal)exposure, r.OutstandingExposureUsd);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(h.Grant()));
    }

    [Theory] [InlineData("retry")] [InlineData("finish")] [InlineData("cap")]
    public async Task Invalid_transport_result_is_accounted_then_halts(string error)
    {
        var h = new Harness(); h.Transport.OnSend = (_, _) => Task.FromResult(error switch {
            "retry" => Observation() with { RetryCount = 1 }, "finish" => Observation() with { FinishReason = "length" },
            _ => Observation() with { Content = new string('x', 262145) } });
        var r = await h.Runner().RunAsync(h.Grant()); Assert.Equal(1, r.TransportInvocations); Assert.Equal(0.01m, r.KnownCostUsd);
        Assert.Equal("TRANSPORT_PROTOCOL_OR_RESPONSE_CAP_INVALID", r.HaltReason);
    }

    [Fact] public async Task Concurrent_Run_or_second_Run_cannot_double_spend()
    {
        var h = new Harness(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource<P7RunnerRawObservation>();
        h.Transport.OnSend = (_, _) => { entered.TrySetResult(); return release.Task; };
        var runner = h.Runner(); var first = runner.RunAsync(h.Grant()); await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(h.Grant()));
        release.SetResult(Observation()); var r = await first; Assert.True(r.TransportInvocations <= 14);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(h.Grant()));
    }

    [Fact] public async Task Dry_run_sealed_transport_has_no_provider_and_file_journal_is_create_new()
    {
        var h = new Harness(); var fake = new P7F1DryRunTransport(); var root = Path.Combine(Path.GetTempPath(), "p7-financial-runner-" + Guid.NewGuid().ToString("N"));
        var writer = new P7F1FileJournal(root, h.Plan, true);
        var runner = new P7F1FinancialRunner(h.Plan, h.Bodies, h.Binding, fake, writer, h.GetMetadata, () => Now, true);
        var r = await runner.RunAsync(h.Grant()); Assert.Equal(14, r.TransportInvocations);
        Assert.Throws<InvalidOperationException>(() => new P7F1FileJournal(root, h.Plan, true));
        Assert.Equal(100, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "01", "raw-freeze.json")));
        Assert.Equal("SYNTHETIC_FINANCIAL_DRY_RUN", marker.RootElement.GetProperty("origin").GetString());
        Assert.False(marker.RootElement.GetProperty("protocolValidated").GetBoolean());
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "01", "financial-receipt.json")));
        Assert.Equal(0.01m, receipt.RootElement.GetProperty("knownCumulativeCostUsd").GetDecimal());
        // Preserve test evidence in temp; no recursive delete or mutation of tracked/raw source files.
    }
}
