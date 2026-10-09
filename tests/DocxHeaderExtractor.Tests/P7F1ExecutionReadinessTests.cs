using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7F1ExecutionReadinessTests
{
    private static P7F1ExecutionPlan Plan() => P7F1ExecutionReadiness.Prepare(File.ReadAllBytes(TestRepository.Path(
        "artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json")));
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static P7F1AttemptOutcome[] Empty(P7F1ExecutionPlan plan) => plan.Calls.Select(c =>
        new P7F1AttemptOutcome(c.CallHandle, false, "NOT_ATTEMPTED", null, null)).ToArray();

    [Fact] public void Plan_is_exactly_14_frozen_F1_calls_not_downstream_or_historical_reuse()
    {
        var p = Plan(); Assert.Equal(14, p.Calls.Count); Assert.Equal(14, p.PrimaryCallCap); Assert.Equal(14, p.HttpAttemptCap);
        Assert.Equal(Enumerable.Range(1, 14), p.Calls.Select(c => c.Sequence));
        foreach (var arm in Enum.GetValues<P7CaptureArm>()) {
            var calls = p.Calls.Where(c => c.Identity.Arm == arm).ToArray();
            Assert.Equal(7, calls.Length); Assert.Equal(213, calls.Sum(c => c.InScopeOwned)); Assert.Equal(430, calls.Sum(c => c.OutsideScopeOwned));
            Assert.All(calls, c => { Assert.Equal(InterpretationStage.F1, c.Identity.Stage); Assert.Empty(c.Identity.ParentCaptureSha256);
                Assert.Equal(P7ExperimentMode.SharedF1Root, c.Identity.Mode); Assert.Equal(arm == P7CaptureArm.Control ? 49152 : 262144, c.ResponseUtf8ByteCap); });
        }
        Assert.Equal(SpatialCanonical.Bytes(p), SpatialCanonical.Bytes(Plan()));
    }

    [Fact] public void Offline_PASS_does_not_approve_spend_or_execute_plan()
    {
        var p = Plan(); Assert.Null(p.ApprovedUsdCap); Assert.Null(p.ApprovedInputTokenCap);
        Assert.Equal("LOCKED", p.ProviderExecution); Assert.Equal("LOCKED", p.ProductionPromotion);
        Assert.Equal("UNVERIFIED", p.ExactTokenizerMapping); Assert.Equal("NOT_MEASURED", p.UsageStatus);
        Assert.Equal(32768, p.CompletionTokenCeilingPerCall); Assert.Equal(458752, p.TotalRequestedCompletionTokenCeiling);
        Assert.Equal(300, p.TimeoutSecondsPerAttempt); Assert.Equal(1, p.ConcurrentCallCap);
        Assert.Equal(0, p.RetryCount); Assert.False(p.Repair); Assert.False(p.Fallback);
    }

    [Fact] public void Changed_frozen_manifest_cannot_expand_or_shrink_calls()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json"));
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes) + " ");
        Assert.Throws<InvalidOperationException>(() => P7F1ExecutionReadiness.Prepare(changed));
    }

    [Theory] [InlineData("model")] [InlineData("max_tokens")] [InlineData("temperature")]
    [InlineData("reasoning")] [InlineData("provider")] [InlineData("stream")]
    public void Provider_or_generation_setting_drift_is_rejected(string field)
    {
        var body = new OpenRouterQwen37InferenceRequestComposer().Build("System", "User", 32768);
        P7F1ExecutionReadiness.ValidateCarrier(body);
        var node = JsonNode.Parse(body)!;
        switch (field) {
            case "model": node[field] = "different"; break;
            case "max_tokens": node[field] = 12288; break;
            case "temperature": node[field] = 1; break;
            case "reasoning": node[field]!["enabled"] = false; break;
            case "provider": node[field]!["allow_fallbacks"] = true; break;
            default: node[field] = false; break;
        }
        Assert.Throws<InvalidOperationException>(() => P7F1ExecutionReadiness.ValidateCarrier(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact] public void A_different_body_is_not_accepted_as_near_equivalent_frozen_request()
    {
        var p = Plan(); var body = new OpenRouterQwen37InferenceRequestComposer().Build("System", "User", 32768);
        Assert.Throws<InvalidOperationException>(() => P7F1ExecutionReadiness.ValidateBody(p.Calls[0], body));
    }

    [Fact] public void Unknown_usage_or_cost_is_not_zero_and_reasoning_is_not_double_counted()
    {
        var missing = P7F1ExecutionReadiness.Usage(null); Assert.Null(missing.PromptTokens); Assert.Null(missing.ReportedCostUsd);
        var observed = P7F1ExecutionReadiness.Usage(Json("{\"prompt_tokens\":100,\"completion_tokens\":50,\"completion_tokens_details\":{\"reasoning_tokens\":30},\"cost\":0.001}"));
        Assert.Equal(100, observed.PromptTokens); Assert.Equal(50, observed.CompletionTokens); Assert.Equal(30, observed.ReasoningTokens);
        Assert.Equal(0.001m, observed.ReportedCostUsd);
        var partial = P7F1ExecutionReadiness.Usage(Json("{\"prompt_tokens\":100}"));
        Assert.Null(partial.CompletionTokens); Assert.Null(partial.ReportedCostUsd); Assert.Equal("INCOMPLETE", partial.TokenStatus);
        var invalid = P7F1ExecutionReadiness.Usage(Json("{\"prompt_tokens\":-2,\"completion_tokens\":\"50\",\"cost\":-1}"));
        Assert.Null(invalid.PromptTokens); Assert.Null(invalid.CompletionTokens); Assert.Null(invalid.ReportedCostUsd);
    }

    [Theory] [InlineData("MISSING_SLOT")] [InlineData("DUPLICATE_SLOT")] [InlineData("FALSE_SUCCESS")]
    [InlineData("RETRY")] [InlineData("UNBOUND_USAGE")]
    public void Accounting_cannot_hide_missing_calls_or_unbound_success_and_usage(string failure)
    {
        var p = Plan(); var outcomes = Empty(p);
        switch (failure) {
            case "MISSING_SLOT": outcomes = outcomes[1..]; break;
            case "DUPLICATE_SLOT": outcomes[0] = outcomes[1]; break;
            case "FALSE_SUCCESS": outcomes[0] = outcomes[0] with { Status = "ACCEPTED" }; break;
            case "RETRY": outcomes[0] = outcomes[0] with { Attempted = true, Status = "RETRIED" }; break;
            default: outcomes[0] = outcomes[0] with { Attempted = true, Status = "TRANSPORT_FAILURE", Usage = Json("{\"cost\":0.01}") }; break;
        }
        Assert.Throws<InvalidOperationException>(() => P7F1ExecutionReadiness.Account(p, outcomes));
    }

    [Fact] public void Failures_count_separately_from_schema_validity_and_semantic_score()
    {
        var p = Plan(); var outcomes = Empty(p);
        outcomes[0] = outcomes[0] with { Attempted = true, Status = "INVALID_EVIDENCE_REFERENCE" };
        outcomes[1] = outcomes[1] with { Attempted = true, Status = "ACCEPTED", RawObservationSha256 = new string('a', 64) };
        using var json = JsonDocument.Parse(SpatialCanonical.Bytes(P7F1ExecutionReadiness.Account(p, outcomes)));
        var r = json.RootElement; var arms = r.GetProperty("arms").EnumerateArray().ToArray();
        Assert.Equal(1, arms.Sum(a => a.GetProperty("statuses").GetProperty("INVALID_EVIDENCE_REFERENCE").GetInt32()));
        Assert.Equal(1, arms.Sum(a => a.GetProperty("statuses").GetProperty("ACCEPTED").GetInt32()));
        Assert.Equal(12, arms.Sum(a => a.GetProperty("statuses").GetProperty("NOT_ATTEMPTED").GetInt32()));
        Assert.False(r.GetProperty("unknownUsageIsZero").GetBoolean()); Assert.False(r.GetProperty("failuresConvertedToOTHER").GetBoolean());
        Assert.Equal("NOT_COMPUTED_BY_EXECUTION_ACCOUNTING", r.GetProperty("semanticScore").GetString());
    }

    [Fact] public void Attempt_schema_statuses_match_accounting_and_do_not_grant_downstream()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("scripts/P7F1ExecutionReadiness/attempt-receipt.schema.v1.json")));
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(P7F1ExecutionReadiness.AttemptStatuses, properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(v => v.GetString()!));
        Assert.False(properties.GetProperty("downstreamAuthorized").GetProperty("const").GetBoolean());
        Assert.Equal(1, properties.GetProperty("httpAttempts").GetProperty("maximum").GetInt32());
    }

    [Fact] public void Published_execution_manifest_preserves_frozen_identity_and_zero_attempts()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d3.f1-execution-readiness.v1.json")));
        var r = doc.RootElement;
        Assert.Equal(SpatialCanonical.Bytes(Plan()), SpatialCanonical.Bytes(r.GetProperty("plan")));
        Assert.Equal(14, r.GetProperty("capture").GetProperty("templates").GetArrayLength());
        Assert.Equal("LOCKED", r.GetProperty("providerExecution").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("budget").GetProperty("approvedSpend").ValueKind);
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, r.GetProperty("authorizedProviderCalls").GetInt32());
        var arms = r.GetProperty("initialAccounting").GetProperty("arms").EnumerateArray().ToArray();
        Assert.All(arms, a => { Assert.Equal(7, a.GetProperty("planned").GetInt32()); Assert.Equal(0, a.GetProperty("attempted").GetInt32());
            Assert.Equal(7, a.GetProperty("statuses").GetProperty("NOT_ATTEMPTED").GetInt32()); });
        Assert.Equal(0, r.GetProperty("downstream").GetProperty("g2aAuthorizedCalls").GetInt32());
        Assert.Equal(0, r.GetProperty("downstream").GetProperty("h2cAuthorizedCalls").GetInt32());
    }
}
