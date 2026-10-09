using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7F1PostAccountedRunnerTests
{
    private sealed class Fake : IP7F1RunnerTransport
    {
        // Only a no-network test double. Real adapter is separately fixed to the same policy.
        public P7RunnerTransportPolicy Policy { get; set; } = new(P7FinancialQualification.ChatEndpoint, "qwen/qwen3.7-flash", "alibaba", 0, false, 300, false);
        public List<byte[]> Bodies { get; } = [];
        public int FailFirst { get; set; }
        public Task<P7RunnerRawObservation> SendOnceAsync(byte[] body, CancellationToken ct)
        {
            Bodies.Add(body.ToArray());
            if (Bodies.Count <= FailFirst) throw new HttpRequestException("synthetic transport failure");
            return Task.FromResult(new P7RunnerRawObservation("{\"decisions\":[]}", "stop", null, "SYNTHETIC", 1, 0));
        }
    }
    private sealed class Harness
    {
        public byte[] Metadata { get; } = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-endpoint-snapshot.v1/endpoint-metadata.json"));
        public P7EndpointBinding Binding => P7FinancialQualification.Bind(Metadata);
        public Dictionary<string, byte[]> Bodies { get; } = [];
        public P7F1ExecutionPlan Plan { get; }
        public Fake Transport { get; } = new();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "p7-postaccount-test-" + Guid.NewGuid().ToString("N"));
        public P7PostAccountedAuthorization Approval => new(P7F1PostAccountedRunner.Version, true, 14, 28, 1,
            Plan.RequestManifestSha256, P7F1PostAccountedRunner.BudgetPolicy, Binding.BindingSha256, "fixture human approval",
            SpatialCanonical.Hash(Encoding.UTF8.GetBytes("fixture human approval")));
        public Harness()
        {
            var p = P7F1ExecutionReadiness.Prepare(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json")));
            Plan = p with { Calls = p.Calls.Select(c => {
                var user = JsonSerializer.Serialize(new { fixture = c.CallHandle });
                var body = new OpenRouterQwen37InferenceRequestComposer().Build("JSON test", user, 32768);
                Bodies.Add(c.CallHandle, body);
                return c with { Identity = c.Identity with { ProviderBodySha256 = SpatialCanonical.Hash(body),
                    SystemPromptSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes("JSON test")), UserMessageSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(user)) } };
            }).ToArray() };
        }
        public Task<IReadOnlyList<P7PostAccountedAttempt>> Run(P7PostAccountedAuthorization? auth = null, Func<P7F1ExecutionCall, string, JsonElement>? validate = null) =>
            new P7F1PostAccountedRunner().RunAsync(auth ?? Approval, Plan, Bodies, Binding, Transport, Directory,
                _ => Task.FromResult(new P7RunnerMetadata(Metadata, DateTimeOffset.UtcNow)), validate ?? ((_, raw) => SpatialCanonical.Element(new { fixture = true })));
    }
    [Fact] public async Task Fourteen_primary_calls_missing_costs_stay_unknown()
    {
        var h = new Harness(); var attempts = await h.Run();
        Assert.Equal(14, attempts.Count); Assert.All(attempts, a => { Assert.Equal("ACCEPTED", a.Status); Assert.Null(a.Usage.ReportedCostUsd); });
        using var freeze = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(h.Directory, "capture-freeze.json")));
        Assert.Equal(14, freeze.RootElement.GetProperty("unknownChargeAttempts").GetInt32());
        Assert.False(freeze.RootElement.GetProperty("unknownChargeIsZero").GetBoolean());
    }
    [Fact] public async Task Transport_failure_retries_once_identical_bytes_with_lineage()
    {
        var h = new Harness(); h.Transport.FailFirst = 1; var a = await h.Run();
        Assert.Equal(15, a.Count); Assert.Equal(h.Transport.Bodies[0], h.Transport.Bodies[1]);
        Assert.Equal("TRANSPORT_FAILURE", a[0].Status); Assert.Equal(2, a[1].Attempt);
        Assert.NotNull(a[1].PreviousAttemptSha256); Assert.Null(a[0].Usage.ReportedCostUsd);
        Assert.True(File.Exists(Path.Combine(h.Directory, "01", "attempt-1", "attempt-receipt.json")));
    }
    [Fact] public async Task Both_attempts_fail_no_third_attempt_and_no_silent_OTHER()
    {
        var h = new Harness(); h.Transport.FailFirst = 2; var a = await h.Run();
        Assert.Equal(15, a.Count); Assert.Equal(2, a.Count(x => x.CallHandle == h.Plan.Calls[0].CallHandle));
        Assert.All(a.Take(2), x => { Assert.Equal("TRANSPORT_FAILURE", x.Status); Assert.Null(x.SelectedDecisionFile); });
    }
    [Fact] public async Task Invalid_contract_retried_once_without_prompt_repair()
    {
        var h = new Harness(); int parses = 0;
        var a = await h.Run(validate: (_, _) => ++parses == 1 ? throw new InvalidOperationException("fixture rejection") : SpatialCanonical.Element(new { fixture = true }));
        Assert.Equal(15, a.Count); Assert.Equal("CONTRACT_FAILURE", a[0].Status); Assert.NotNull(a[0].RawFreezeSha256);
        Assert.Equal(h.Transport.Bodies[0], h.Transport.Bodies[1]);
    }
    [Theory] [InlineData("authorization")] [InlineData("cap")] [InlineData("retry")] [InlineData("budget")] [InlineData("hash")]
    public async Task Invalid_authorization_zero_network(string variant)
    {
        var h = new Harness(); var a = h.Approval;
        a = variant switch { "authorization" => a with { Authorized = false }, "cap" => a with { HttpAttemptCap = 29 },
            "retry" => a with { RetriesPerRequest = 2 }, "budget" => a with { BudgetPolicy = "HARD_USD_BOUND" }, _ => a with { HumanInstructionSha256 = "wrong" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(a)); Assert.Empty(h.Transport.Bodies);
    }
    [Fact] public async Task Frozen_body_drift_zero_dispatch()
    {
        var h = new Harness(); h.Bodies[h.Plan.Calls[0].CallHandle] = Encoding.UTF8.GetBytes("{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run()); Assert.Empty(h.Transport.Bodies);
    }
    [Fact] public async Task Transport_internal_retry_not_allowed()
    {
        var h = new Harness(); h.Transport.Policy = h.Transport.Policy with { Retries = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run()); Assert.Empty(h.Transport.Bodies);
    }
}
