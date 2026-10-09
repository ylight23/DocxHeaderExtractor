using System.Globalization;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

public sealed record P7PriceTier(long MinPromptTokens, decimal Prompt, decimal Completion, decimal CacheRead, decimal CacheWrite);
public sealed record P7EndpointBinding(string Model, string Provider, string Tag, string EndpointName,
    string ChatEndpoint, long ContextLength, long MaxPromptTokens, long MaxCompletionTokens,
    IReadOnlyList<string> SupportedParameters, IReadOnlyList<P7PriceTier> PricingTiers,
    bool ImplicitCaching, string BindingSha256, string MetadataSha256);
public sealed record P7ExposureEstimate(decimal PerCallUsd, decimal FourteenCallsUsd, string Status, string Formula);
public sealed record P7FinancialApproval(decimal? ApprovedUsdCap, bool CallsAuthorized, string? ApprovalReceiptSha256,
    bool DedicatedKeyVerified, decimal? KeyLimit, decimal? KeyRemaining, string? KeyReceiptSha256,
    bool BillingBoundVerified, string? BillingBoundReceiptSha256);
public sealed record P7FinancialReservation(string CallHandle, string BodySha256, decimal ReservedUsd);
public sealed record P7FinancialSettlement(string CallHandle, string BodySha256, string ObservationSha256,
    long? PromptTokens, long? CompletionTokens, long? ReasoningTokens, decimal? CostUsd,
    decimal KnownCumulativeCostUsd, decimal UnsettledExposureUsd, string Status);

/// <summary>Qualification-only policy simulation. This type neither sends requests nor grants approval.</summary>
public static class P7FinancialQualification
{
    public const string Version = "P7_D3_FINANCIAL_ENDPOINT_BINDING_V1";
    public const string ChatEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    public const string MetadataEndpoint = "https://openrouter.ai/api/v1/models/qwen/qwen3.7-flash/endpoints";
    public const string ExecutionManifestSha256 = "820ec92aeb9ea1e031bcad6815ec7df9a357e3e034c8f11f512b1e4df7262b1a";
    public static P7EndpointBinding Bind(byte[] metadata)
    {
        using var json = JsonDocument.Parse(metadata);
        RejectDuplicateKeys(json.RootElement);
        var data = json.RootElement.GetProperty("data");
        Need(data.GetProperty("id").GetString() == "qwen/qwen3.7-flash", "MODEL_DRIFT");
        var endpoints = data.GetProperty("endpoints").EnumerateArray().Where(e => e.GetProperty("tag").GetString() == "alibaba").ToArray();
        Need(endpoints.Length == 1, "ENDPOINT_NOT_UNIQUE"); var e = endpoints[0];
        Need(e.GetProperty("model_id").GetString() == "qwen/qwen3.7-flash" &&
            e.GetProperty("provider_name").GetString() == "Alibaba" && e.GetProperty("status").GetInt32() == 0, "ENDPOINT_UNAVAILABLE_OR_DRIFT");
        var parameters = e.GetProperty("supported_parameters").EnumerateArray().Select(p => p.GetString()!).Order(StringComparer.Ordinal).ToArray();
        Need(new[] { "max_tokens", "reasoning", "temperature", "response_format" }.All(parameters.Contains), "REQUIRED_PARAMETER_UNSUPPORTED");
        var promptCap = e.GetProperty("max_prompt_tokens").GetInt64(); var outputCap = e.GetProperty("max_completion_tokens").GetInt64();
        var context = e.GetProperty("context_length").GetInt64();
        Need(promptCap > 0 && context > 0 && outputCap >= 32768, "ADVERTISED_TOKEN_CAP_UNSUPPORTED");
        var pricing = e.GetProperty("pricing");
        PriceKeys(pricing, "prompt", "completion", "input_cache_read", "input_cache_write", "discount", "overrides");
        if (pricing.TryGetProperty("discount", out var discount)) Need(discount.GetDecimal() == 0, "UNMODELED_DISCOUNT");
        var tiers = new List<P7PriceTier> { Tier(pricing, 0) };
        if (pricing.TryGetProperty("overrides", out var overrides)) foreach (var tier in overrides.EnumerateArray()) {
            PriceKeys(tier, "min_prompt_tokens", "prompt", "completion", "input_cache_read", "input_cache_write");
            var min = tier.GetProperty("min_prompt_tokens").GetInt64(); Need(min > 0, "INVALID_PRICE_THRESHOLD"); tiers.Add(Tier(tier, min));
        }
        Need(tiers.Select(t => t.MinPromptTokens).Distinct().Count() == tiers.Count, "DUPLICATE_PRICE_THRESHOLD");
        var name = e.GetProperty("name").GetString()!; Need(!string.IsNullOrWhiteSpace(name), "ENDPOINT_NAME_MISSING");
        var sorted = tiers.OrderBy(t => t.MinPromptTokens).ToArray();
        var implicitCaching = e.GetProperty("supports_implicit_caching").GetBoolean();
        // Exclude uptime/latency from semantic binding, but retain the complete raw metadata hash.
        var identity = new { model = "qwen/qwen3.7-flash", provider = "Alibaba", tag = "alibaba", endpointName = name,
            chatEndpoint = ChatEndpoint, context, promptCap, outputCap, parameters, tiers = sorted, implicitCaching,
            quantization = e.GetProperty("quantization").GetString() };
        return new("qwen/qwen3.7-flash", "Alibaba", "alibaba", name, ChatEndpoint, context, promptCap, outputCap,
            Array.AsReadOnly(parameters), Array.AsReadOnly(sorted), implicitCaching,
            SpatialCanonical.Hash(SpatialCanonical.Bytes(identity)), SpatialCanonical.Hash(metadata));
    }

    public static void CheckFreshBinding(P7EndpointBinding pinned, byte[] currentMetadata,
        DateTimeOffset observedAt, DateTimeOffset now)
    {
        Need(observedAt <= now && now - observedAt <= TimeSpan.FromMinutes(5), "METADATA_STALE_OR_FUTURE");
        Need(Bind(currentMetadata).BindingSha256 == pinned.BindingSha256, "ENDPOINT_BINDING_DRIFT");
    }

    public static P7ExposureEstimate Estimate(P7EndpointBinding binding)
    {
        // Deliberately sum maximum advertised prompt/cache rates rather than assume cache-charge exclusivity.
        // This is not a verified billing upper bound: fees, token enforcement and billing semantics need separate proof.
        var inputRate = binding.PricingTiers.Max(t => t.Prompt) + binding.PricingTiers.Max(t => t.CacheRead) + binding.PricingTiers.Max(t => t.CacheWrite);
        var estimate = checked(binding.MaxPromptTokens * inputRate + 32768m * binding.PricingTiers.Max(t => t.Completion));
        return new(estimate, checked(14 * estimate), "ADVERTISED_LIMIT_STRESS_ESTIMATE_NOT_VERIFIED_BILLING_BOUND",
            "max_prompt_tokens * (max(prompt) + max(cache_read) + max(cache_write)) + 32768 * max(completion)");
    }

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c));
    public static byte[] Receipt(string financialManifestSha256, string executionManifestSha256,
        P7EndpointBinding binding, P7FinancialReservation reservation, P7FinancialSettlement settlement)
    {
        Need(IsHash(financialManifestSha256) && executionManifestSha256 == ExecutionManifestSha256 &&
            IsHash(binding.BindingSha256) && IsHash(binding.MetadataSha256) && IsHash(settlement.ObservationSha256) &&
            reservation.CallHandle == settlement.CallHandle && reservation.BodySha256 == settlement.BodySha256,
            "FINANCIAL_RECEIPT_LINEAGE_INVALID");
        return SpatialCanonical.Bytes(new { financialManifestSha256, executionManifestSha256,
            settlement.CallHandle, settlement.BodySha256, endpointBindingSha256 = binding.BindingSha256,
            metadataSnapshotSha256 = binding.MetadataSha256, settlement.ObservationSha256, reservation.ReservedUsd,
            settlement.PromptTokens, settlement.CompletionTokens, settlement.ReasoningTokens, settlement.CostUsd,
            settlement.KnownCumulativeCostUsd, settlement.UnsettledExposureUsd, settlement.Status });
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) {
            var properties = element.EnumerateObject().ToArray();
            Need(properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length, "AMBIGUOUS_METADATA_KEYS");
            foreach (var p in properties) RejectDuplicateKeys(p.Value);
        } else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    internal static void Need(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    private static void PriceKeys(JsonElement price, params string[] allowed) => Need(
        price.EnumerateObject().All(p => allowed.Contains(p.Name)) &&
        price.EnumerateObject().Select(p => p.Name).Distinct().Count() == price.EnumerateObject().Count(), "UNMODELED_PRICE_COMPONENT");
    private static P7PriceTier Tier(JsonElement p, long min) => new(min, Rate(p, "prompt"), Rate(p, "completion"), Rate(p, "input_cache_read"), Rate(p, "input_cache_write"));
    private static decimal Rate(JsonElement p, string key)
    {
        Need(p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _), "MISSING_OR_INVALID_PRICE");
        var rate = decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture); Need(rate >= 0, "NEGATIVE_PRICE"); return rate;
    }
}

/// <summary>Serial, no-retry fixture ledger. Future execution must separately bind these gates to transport.</summary>
public sealed class P7FinancialLedger
{
    private readonly P7F1ExecutionPlan plan;
    private readonly P7EndpointBinding binding;
    private readonly P7FinancialApproval approval;
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly HashSet<string> observations = new(StringComparer.Ordinal);
    private P7FinancialReservation? active;
    public decimal KnownCostUsd { get; private set; }
    public decimal UnsettledExposureUsd => active?.ReservedUsd ?? 0;
    public string? HaltReason { get; private set; }
    public int AttemptCount => attempted.Count;
    internal P7FinancialLedger(P7F1ExecutionPlan plan, P7EndpointBinding binding, P7FinancialApproval approval)
    { this.plan = plan; this.binding = binding; this.approval = approval; }

    public P7FinancialReservation Reserve(string callHandle, byte[] body, byte[] freshMetadata, DateTimeOffset observedAt, DateTimeOffset now)
    {
        P7FinancialQualification.Need(HaltReason is null, "LEDGER_HALTED");
        P7FinancialQualification.Need(active is null, "CONCURRENT_RESERVATION_FORBIDDEN");
        try {
            P7FinancialQualification.Need(plan.HttpAttemptCap == 14 && plan.PrimaryCallCap == 14 && plan.ConcurrentCallCap == 1 &&
                plan.RetryCount == 0 && !plan.Repair && !plan.Fallback && plan.CompletionTokenCeilingPerCall == 32768,
                "EXECUTION_POLICY_DRIFT");
            P7FinancialQualification.CheckFreshBinding(binding, freshMetadata, observedAt, now);
            P7FinancialQualification.Need(approval.CallsAuthorized && P7FinancialQualification.IsHash(approval.ApprovalReceiptSha256), "CALLS_NOT_AUTHORIZED");
            P7FinancialQualification.Need(approval.ApprovedUsdCap is > 0, "USD_CAP_NOT_APPROVED");
            P7FinancialQualification.Need(approval.DedicatedKeyVerified && P7FinancialQualification.IsHash(approval.KeyReceiptSha256) &&
                approval.KeyLimit is > 0 && approval.KeyLimit <= approval.ApprovedUsdCap && approval.KeyRemaining is >= 0,
                "DEDICATED_KEY_LIMIT_NOT_VERIFIED");
            P7FinancialQualification.Need(approval.BillingBoundVerified && P7FinancialQualification.IsHash(approval.BillingBoundReceiptSha256), "BILLING_BOUND_NOT_VERIFIED");
            P7FinancialQualification.Need(attempted.Count < plan.HttpAttemptCap && !attempted.Contains(callHandle), "CALL_CAP_OR_RETRY_FORBIDDEN");
            var call = plan.Calls.Single(c => c.CallHandle == callHandle); P7F1ExecutionReadiness.ValidateBody(call, body);
            var reserve = P7FinancialQualification.Estimate(binding).PerCallUsd;
            P7FinancialQualification.Need(KnownCostUsd + reserve <= approval.ApprovedUsdCap && reserve <= approval.KeyRemaining - KnownCostUsd, "NEXT_CALL_EXCEEDS_CAP");
            active = new(callHandle, call.Identity.ProviderBodySha256, reserve); attempted.Add(callHandle); return active;
        } catch { HaltReason = "PRE_SEND_GATE_REJECTED"; throw; }
    }

    public P7FinancialSettlement Settle(string callHandle, string bodySha256, string observationSha256, JsonElement? usage, bool transportSucceeded)
    {
        P7FinancialQualification.Need(HaltReason is null, "LEDGER_HALTED");
        P7FinancialQualification.Need(active is not null, "NO_ACTIVE_RESERVATION");
        if (active!.CallHandle != callHandle || active.BodySha256 != bodySha256 || !P7FinancialQualification.IsHash(observationSha256) || !observations.Add(observationSha256)) {
            HaltReason = "RECEIPT_IDENTITY_INVALID"; throw new InvalidOperationException(HaltReason);
        }
        var u = P7F1ExecutionReadiness.Usage(usage);
        if (u.ReportedCostUsd.HasValue) KnownCostUsd = checked(KnownCostUsd + u.ReportedCostUsd.Value);
        var complete = u.PromptTokens.HasValue && u.CompletionTokens.HasValue && u.ReportedCostUsd.HasValue;
        var exceeded = u.ReportedCostUsd > active.ReservedUsd || KnownCostUsd > approval.ApprovedUsdCap ||
            u.PromptTokens > binding.MaxPromptTokens || u.CompletionTokens > 32768 || u.ReasoningTokens > u.CompletionTokens;
        var status = !complete ? "USAGE_OR_COST_INCOMPLETE_HALT" : exceeded ? "BOUND_EXCEEDED_HALT" :
            !transportSucceeded ? "TRANSPORT_FAILURE_HALT" : "SETTLED_PROVIDER_REPORTED_NOT_BILLING_AUDIT";
        // Unknown failed-call exposure is never released as zero. Preserve known cost independently.
        if (complete) active = null;
        if (!complete || exceeded || !transportSucceeded) HaltReason = status;
        return new(callHandle, bodySha256, observationSha256, u.PromptTokens, u.CompletionTokens, u.ReasoningTokens,
            u.ReportedCostUsd, KnownCostUsd, UnsettledExposureUsd, status);
    }
}
