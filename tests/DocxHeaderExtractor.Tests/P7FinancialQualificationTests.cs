using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7FinancialQualificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);
    private static string Hash(char c) => new(c, 64);
    private static byte[] Metadata() => File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.openrouter-endpoint-metadata.20261009.v1.json"));
    private static P7F1ExecutionPlan Plan() {
        var frozen = P7F1ExecutionReadiness.Prepare(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json")));
        // Synthetic accounting fixtures ONLY, not adopted captures or semantic evaluation.
        return frozen with { Calls = frozen.Calls.Select(c => c with { Identity = c.Identity with {
            ProviderBodySha256 = SpatialCanonical.Hash(Body(c)),
            SystemPromptSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes("Financial fixture")),
            UserMessageSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(c.CallHandle)) } }).ToArray() };
    }
    private static P7FinancialApproval Approval(decimal cap = 1m) => new(cap, true, Hash('a'), true, cap, cap, Hash('b'), true, Hash('c'));
    private static P7FinancialLedger Ledger(P7FinancialApproval? a = null) => new(Plan(), P7FinancialQualification.Bind(Metadata()), a ?? Approval());
    private static JsonElement Usage(string text = "{\"prompt_tokens\":200,\"completion_tokens\":40,\"cost\":0.02}") { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    private static byte[] Body(P7F1ExecutionCall call) => new OpenRouterQwen37InferenceRequestComposer().Build("Financial fixture", call.CallHandle, 32768);
    private static byte[] ChangedMetadata(Action<JsonNode> change) { var n = JsonNode.Parse(Metadata())!; change(n["data"]!["endpoints"]![0]!); return JsonSerializer.SerializeToUtf8Bytes(n); }
    private static P7FinancialReservation Reserve(P7FinancialLedger ledger, int index = 0) {
        var call = Plan().Calls[index]; return ledger.Reserve(call.CallHandle, Body(call), Metadata(), Now, Now);
    }

    [Fact] public void Binding_and_estimate_are_deterministic_not_tokenizer_or_billing_proof()
    {
        var b = P7FinancialQualification.Bind(Metadata());
        Assert.Equal(SpatialCanonical.Bytes(b), SpatialCanonical.Bytes(P7FinancialQualification.Bind(Metadata())));
        Assert.Equal("Alibaba", b.Provider); Assert.Equal("alibaba", b.Tag); Assert.Equal(3, b.PricingTiers.Count);
        var e = P7FinancialQualification.Estimate(b);
        Assert.Equal(0.50818624m, e.PerCallUsd); Assert.Equal(7.11460736m, e.FourteenCallsUsd);
        Assert.Contains("NOT_VERIFIED_BILLING_BOUND", e.Status);
    }

    [Theory] [InlineData("name")] [InlineData("max_prompt_tokens")] [InlineData("max_completion_tokens")]
    [InlineData("pricing")] [InlineData("supported_parameters")] [InlineData("quantization")]
    public void Semantic_binding_drift_fails_closed(string field)
    {
        var changed = ChangedMetadata(e => {
            switch (field) {
                case "name": e[field] = "Alibaba | changed"; break;
                case "max_prompt_tokens": e[field] = 983615; break;
                case "max_completion_tokens": e[field] = 65535; break;
                case "pricing": e[field]!["overrides"]![1]!["completion"] = "0.0000009"; break;
                case "supported_parameters": e[field]!.AsArray().Add("new_parameter"); break;
                case "quantization": e[field] = "int8"; break;
            }
        });
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.CheckFreshBinding(P7FinancialQualification.Bind(Metadata()), changed, Now, Now));
    }

    [Fact] public void Volatile_metadata_changes_raw_hash_not_semantic_binding()
    {
        var changed = ChangedMetadata(e => e["uptime_last_30m"] = 98);
        var b = P7FinancialQualification.Bind(Metadata()); var c = P7FinancialQualification.Bind(changed);
        Assert.NotEqual(b.MetadataSha256, c.MetadataSha256); Assert.Equal(b.BindingSha256, c.BindingSha256);
        P7FinancialQualification.CheckFreshBinding(b, changed, Now, Now);
    }

    [Theory] [InlineData("unsupported")] [InlineData("fee")] [InlineData("missing_price")] [InlineData("duplicate_tier")]
    [InlineData("offline")] [InlineData("low_cap")] [InlineData("wrong_provider")]
    public void Unmodeled_or_unusable_endpoint_is_rejected(string kind)
    {
        var changed = ChangedMetadata(e => {
            switch (kind) {
                case "unsupported": e["supported_parameters"] = new JsonArray("temperature"); break;
                case "fee": e["pricing"]!["request"] = "0.01"; break;
                case "missing_price": e["pricing"]!.AsObject().Remove("input_cache_write"); break;
                case "duplicate_tier": e["pricing"]!["overrides"]![1]!["min_prompt_tokens"] = 32000; break;
                case "offline": e["status"] = 1; break;
                case "low_cap": e["max_completion_tokens"] = 32767; break;
                case "wrong_provider": e["provider_name"] = "Other"; break;
            }
        });
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.Bind(changed));
    }

    [Theory] [InlineData(-301)] [InlineData(1)]
    public void Metadata_must_be_fresh_not_future(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.CheckFreshBinding(
            P7FinancialQualification.Bind(Metadata()), Metadata(), Now.AddSeconds(seconds), Now));
    }

    [Theory] [InlineData("authorization")] [InlineData("usd")] [InlineData("key")] [InlineData("bound")]
    [InlineData("key_limit")] [InlineData("key_remaining")]
    public void Candidate_budget_or_missing_proof_cannot_reserve(string missing)
    {
        var a = Approval(); a = missing switch {
            "authorization" => a with { CallsAuthorized = false }, "usd" => a with { ApprovedUsdCap = null },
            "key" => a with { DedicatedKeyVerified = false }, "bound" => a with { BillingBoundVerified = false },
            "key_limit" => a with { KeyLimit = 2 }, "key_remaining" => a with { KeyRemaining = 0.1m }, _ => a };
        var l = Ledger(a); Assert.Throws<InvalidOperationException>(() => Reserve(l)); Assert.Equal(0, l.AttemptCount);
    }

    [Fact] public void Serial_reserve_settle_records_account_cost_and_optional_reasoning_not_upstream_cost()
    {
        var l = Ledger(); var r = Reserve(l);
        Assert.Equal(0.50818624m, l.UnsettledExposureUsd);
        Assert.Throws<InvalidOperationException>(() => Reserve(l, 1));
        var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage("{\"prompt_tokens\":200,\"completion_tokens\":40,\"cost\":0.02,\"cost_details\":{\"upstream_inference_cost\":99},\"completion_tokens_details\":{\"reasoning_tokens\":30}}"), true);
        Assert.Equal(0.02m, s.KnownCumulativeCostUsd); Assert.Equal(40, s.CompletionTokens); Assert.Equal(30, s.ReasoningTokens);
        Assert.Null(l.HaltReason); Assert.Equal(0, l.UnsettledExposureUsd); Reserve(l, 1);
    }

    [Fact] public void Next_request_denied_before_send_when_remaining_budget_insufficient()
    {
        var l = Ledger(Approval(0.6m)); var r = Reserve(l);
        l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage("{\"prompt_tokens\":200,\"completion_tokens\":40,\"cost\":0.1}"), true);
        Assert.Throws<InvalidOperationException>(() => Reserve(l, 1)); Assert.Equal(1, l.AttemptCount);
    }

    [Theory] [InlineData("{}", 0)] [InlineData("{\"prompt_tokens\":20,\"completion_tokens\":2}", 0)]
    [InlineData("{\"cost\":0.03}", 0.03)] [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":2,\"cost\":0.01}", 0.01)]
    public void Missing_usage_preserves_unsettled_exposure_and_known_cost_and_halts(string raw, double known)
    {
        var l = Ledger(); var r = Reserve(l); var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage(raw), false);
        Assert.Equal((decimal)known, l.KnownCostUsd); Assert.Equal(r.ReservedUsd, l.UnsettledExposureUsd);
        Assert.Equal("USAGE_OR_COST_INCOMPLETE_HALT", s.Status); Assert.Throws<InvalidOperationException>(() => Reserve(l, 1));
        Assert.Throws<InvalidOperationException>(() => l.Settle(r.CallHandle, r.BodySha256, Hash('e'), Usage(), true));
        Assert.Equal((decimal)known, l.KnownCostUsd); Assert.Equal(r.ReservedUsd, l.UnsettledExposureUsd);
    }

    [Fact] public void Null_usage_timeout_retains_reservation_not_zero()
    {
        var l = Ledger(); var r = Reserve(l); var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), null, false);
        Assert.Null(s.CostUsd); Assert.Equal(r.ReservedUsd, s.UnsettledExposureUsd); Assert.NotNull(l.HaltReason);
    }

    [Fact] public void Overrun_records_actual_cost_unclamped_then_halts()
    {
        var l = Ledger(); var r = Reserve(l); var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage("{\"prompt_tokens\":200,\"completion_tokens\":40,\"cost\":1.2}"), true);
        Assert.Equal(1.2m, s.CostUsd); Assert.Equal(1.2m, l.KnownCostUsd); Assert.Equal("BOUND_EXCEEDED_HALT", s.Status);
        Assert.Throws<InvalidOperationException>(() => Reserve(l, 1));
    }

    [Fact] public void Completion_cap_violation_halts_even_if_cost_low()
    {
        var l = Ledger(); var r = Reserve(l); var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage("{\"prompt_tokens\":20,\"completion_tokens\":32769,\"cost\":0.001}"), true);
        Assert.Equal("BOUND_EXCEEDED_HALT", s.Status);
    }

    [Fact] public void Retry_identity_and_duplicate_receipt_are_rejected()
    {
        var l = Ledger(); var r = Reserve(l); l.Settle(r.CallHandle, r.BodySha256, Hash('d'), Usage(), true);
        Assert.Throws<InvalidOperationException>(() => Reserve(l));
        var next = Ledger(); var a = Reserve(next); next.Settle(a.CallHandle, a.BodySha256, Hash('d'), Usage(), true);
        var b = Reserve(next, 1); Assert.Throws<InvalidOperationException>(() => next.Settle(b.CallHandle, b.BodySha256, Hash('d'), Usage(), true));
        Assert.NotNull(next.HaltReason); Assert.Equal(b.ReservedUsd, next.UnsettledExposureUsd);
    }

    [Fact] public void Fourteen_unique_calls_ceiling_and_low_actual_costs_do_not_guarantee_all_fit()
    {
        var l = Ledger(Approval(20));
        for (var i = 0; i < 14; i++) { var r = Reserve(l, i); l.Settle(r.CallHandle, r.BodySha256, i.ToString("x64"), Usage(), true); }
        Assert.Equal(14, l.AttemptCount); Assert.Equal(0.28m, l.KnownCostUsd); Assert.Throws<InvalidOperationException>(() => Reserve(l));
    }

    [Fact] public void Receipt_binds_financial_execution_body_metadata_and_observation_hashes_preserves_nulls()
    {
        var l = Ledger(); var r = Reserve(l); var s = l.Settle(r.CallHandle, r.BodySha256, Hash('d'), null, false);
        var b = P7FinancialQualification.Bind(Metadata());
        var bytes = P7FinancialQualification.Receipt(Hash('f'), P7FinancialQualification.ExecutionManifestSha256, b, r, s);
        Assert.Equal(bytes, P7FinancialQualification.Receipt(Hash('f'), P7FinancialQualification.ExecutionManifestSha256, b, r, s));
        using var receipt = JsonDocument.Parse(bytes); var root = receipt.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("costUsd").ValueKind);
        Assert.Equal(r.ReservedUsd, root.GetProperty("unsettledExposureUsd").GetDecimal());
        using var schema = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("scripts/P7FinancialQualification/financial-receipt.schema.v1.json")));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order().ToArray();
        Assert.Equal(required, root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.Receipt(Hash('f'), Hash('e'), b, r, s));
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.Receipt(Hash('f'), P7FinancialQualification.ExecutionManifestSha256, b, r, s with { BodySha256 = Hash('e') }));
    }

    [Fact] public void Ambiguous_metadata_duplicate_keys_cannot_be_adopted()
    {
        var raw = System.Text.Encoding.UTF8.GetString(Metadata()).Replace("\"status\":0", "\"status\":1,\"status\":0", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => P7FinancialQualification.Bind(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    [Fact] public void Wrong_receipt_body_retains_exposure_and_halts()
    {
        var l = Ledger(); var r = Reserve(l);
        Assert.Throws<InvalidOperationException>(() => l.Settle(r.CallHandle, Hash('e'), Hash('d'), Usage(), true));
        Assert.Equal(r.ReservedUsd, l.UnsettledExposureUsd); Assert.Equal("RECEIPT_IDENTITY_INVALID", l.HaltReason);
    }

    [Fact] public void Inherited_retry_policy_cannot_reserve()
    {
        var l = new P7FinancialLedger(Plan() with { RetryCount = 2 }, P7FinancialQualification.Bind(Metadata()), Approval());
        Assert.Throws<InvalidOperationException>(() => Reserve(l)); Assert.Equal(0, l.AttemptCount);
    }
}
