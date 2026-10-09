using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record P7F1ExecutionCall(int Sequence, string CallHandle, P7CaptureIdentity Identity,
    string ProviderBodyFile, int ResponseUtf8ByteCap, int InScopeOwned, int OutsideScopeOwned);
internal sealed record P7F1ExecutionPlan(string Version, string Status, string RequestManifestSha256,
    string OfflineQualificationSha256, string EndpointMetadataSnapshotSha256,
    IReadOnlyList<P7F1ExecutionCall> Calls, int PrimaryCallCap, int HttpAttemptCap,
    int ConcurrentCallCap, int TimeoutSecondsPerAttempt, int RetryCount, bool Repair, bool Fallback,
    int CompletionTokenCeilingPerCall, long TotalRequestedCompletionTokenCeiling,
    long? ApprovedInputTokenCap, decimal? ApprovedUsdCap, string BudgetStatus,
    string ExactTokenizerMapping, string UsageStatus, string ProviderExecution, string ProductionPromotion);
internal sealed record P7F1AttemptOutcome(string CallHandle, bool Attempted, string Status,
    JsonElement? Usage, string? RawObservationSha256);
internal sealed record P7F1UsageSummary(long? PromptTokens, long? CompletionTokens, long? ReasoningTokens,
    decimal? ReportedCostUsd, string TokenStatus, string CostStatus);

/// <summary>Offline F1-only execution plan and failure-accounting contract. No HTTP,
/// authorization grant, request rewrite, Gold decision access, or downstream fan-out.</summary>
internal static class P7F1ExecutionReadiness
{
    public const string Version = "P7_D3_F1_EXECUTION_READINESS_V1";
    public const string FrozenRequestManifestSha256 = "2a5e4491270fd7ec92f536bfa8ac1e9812c8440f9fbb3e0a58a99d06e2965ae1";
    public const string OfflineQualificationSha256 = "2b8b4148c57289077d2af4538800bd1fadc8274ee1e12e6fb046d857d895bd05";
    public const string EndpointSnapshotSha256 = "d38b7fb531a59602d0bf73ffc4b3cb3838756f5270d9bed3545dc22e8b381069";
    public static IReadOnlyList<string> AttemptStatuses { get; } = Array.AsReadOnly(new[] {
        "NOT_ATTEMPTED", "ACCEPTED", "TRANSPORT_FAILURE", "NON_STOP_FINISH", "RESPONSE_CAP_EXCEEDED",
        "JSON_OR_SCHEMA_ERROR", "MISSING_OR_DUPLICATE_DECISIONS", "INVALID_EVIDENCE_REFERENCE",
        "CONTRADICTED_EVIDENCE_ASSERTION", "RAW_FREEZE_OR_IDENTITY_ERROR" });

    public static P7F1ExecutionPlan Prepare(byte[] frozenManifest)
    {
        Need(SpatialCanonical.Hash(frozenManifest) == FrozenRequestManifestSha256, "FROZEN_MANIFEST_DRIFT");
        using var json = JsonDocument.Parse(frozenManifest); var root = json.RootElement;
        var calls = root.GetProperty("requests").EnumerateArray().Select((r, i) => {
            Need(r.GetProperty("stage").GetString() == "F1", "F1_ONLY");
            var arm = r.GetProperty("arm").GetString() switch {
                "CONTROL" => P7CaptureArm.Control, "B" => P7CaptureArm.B, _ => throw new InvalidOperationException("UNKNOWN_ARM") };
            return new P7F1ExecutionCall(i + 1, r.GetProperty("callHandle").GetString()!,
                new(r.GetProperty("document").GetString()!, r.GetProperty("sourceSha256").GetString()!,
                    r.GetProperty("sourceUniverseSha256").GetString()!, r.GetProperty("pack").GetString()!,
                    r.GetProperty("packSha256").GetString()!, r.GetProperty("issuedUniverseSha256").GetString()!,
                    InterpretationStage.F1, arm, P7ExperimentMode.SharedF1Root,
                    r.GetProperty("providerBodySha256").GetString()!, r.GetProperty("systemPromptSha256").GetString()!,
                    r.GetProperty("userMessageSha256").GetString()!, Array.Empty<string>()),
                r.GetProperty("providerBodyFile").GetString()!, arm == P7CaptureArm.Control ? 49152 : 262144,
                r.GetProperty("inScopeOwned").GetInt32(), r.GetProperty("outsideScopeOwned").GetInt32());
        }).ToArray();
        Need(calls.Length == 14 && calls.Select(c => c.CallHandle).Distinct().Count() == 14, "CALL_UNIVERSE_DRIFT");
        foreach (var arm in Enum.GetValues<P7CaptureArm>()) {
            var rows = calls.Where(c => c.Identity.Arm == arm).ToArray();
            Need(rows.Length == 7 && rows.Sum(c => c.InScopeOwned) == 213 && rows.Sum(c => c.OutsideScopeOwned) == 430, "SCOPE_DENOMINATOR_DRIFT");
        }
        return new(Version, "PLAN_PREPARED_BUDGET_AND_AUTHORIZATION_PENDING", FrozenRequestManifestSha256,
            OfflineQualificationSha256, EndpointSnapshotSha256, Array.AsReadOnly(calls), 14, 14, 1, 300, 0, false, false,
            32768, 14L * 32768, null, null, "NOT_APPROVED_NO_HARD_BILLING_BOUND_ESTABLISHED",
            "UNVERIFIED", "NOT_MEASURED", "LOCKED", "LOCKED");
    }

    public static void ValidateBody(P7F1ExecutionCall call, byte[] body)
    {
        Need(SpatialCanonical.Hash(body) == call.Identity.ProviderBodySha256, "BODY_HASH_DRIFT");
        ValidateCarrier(body);
        using var json = JsonDocument.Parse(body); var messages = json.RootElement.GetProperty("messages");
        Need(TextHash(messages[0].GetProperty("content").GetString()!) == call.Identity.SystemPromptSha256 &&
            TextHash(messages[1].GetProperty("content").GetString()!) == call.Identity.UserMessageSha256, "MESSAGE_HASH_DRIFT");
    }

    public static void ValidateCarrier(byte[] body)
    {
        using var json = JsonDocument.Parse(body); var r = json.RootElement;
        Need(r.GetProperty("model").GetString() == "qwen/qwen3.7-flash" && r.GetProperty("temperature").GetDecimal() == 0 &&
            r.GetProperty("max_tokens").GetInt32() == 32768 && r.GetProperty("stream").GetBoolean() &&
            r.GetProperty("reasoning").GetProperty("enabled").GetBoolean() &&
            r.GetProperty("response_format").GetProperty("type").GetString() == "json_object" &&
            r.GetProperty("usage").GetProperty("include").GetBoolean(), "MODEL_OR_GENERATION_DRIFT");
        var p = r.GetProperty("provider");
        Need(p.GetProperty("order").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "alibaba" }) &&
            !p.GetProperty("allow_fallbacks").GetBoolean() && p.GetProperty("require_parameters").GetBoolean() &&
            p.GetProperty("data_collection").GetString() == "deny" && !p.GetProperty("zdr").GetBoolean(), "PROVIDER_ROUTE_DRIFT");
        var messages = r.GetProperty("messages");
        Need(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system" &&
            messages[1].GetProperty("role").GetString() == "user", "MESSAGE_ROLE_DRIFT");
    }

    public static P7F1UsageSummary Usage(JsonElement? usage)
    {
        if (usage is null || usage.Value.ValueKind != JsonValueKind.Object)
            return new(null, null, null, null, "NOT_REPORTED", "NOT_REPORTED");
        var u = usage.Value;
        long? Count(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt64(out var n) && n >= 0 ? n : null;
        var prompt = Count(u, "prompt_tokens"); var completion = Count(u, "completion_tokens");
        var reasoning = u.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Count(details, "reasoning_tokens") : null;
        var cost = u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var usd) && usd >= 0 ? (decimal?)usd : null;
        // Reasoning is a reported subcomponent, never added a second time to completion tokens.
        return new(prompt, completion, reasoning, cost, prompt.HasValue && completion.HasValue ? "PROVIDER_REPORTED_NOT_OFFLINE_ESTIMATE" : "INCOMPLETE",
            cost.HasValue ? "PROVIDER_REPORTED_NOT_INDEPENDENT_BILLING_VERIFICATION" : "NOT_REPORTED");
    }

    public static object Account(P7F1ExecutionPlan plan, IReadOnlyList<P7F1AttemptOutcome> outcomes)
    {
        Need(outcomes.Count == plan.Calls.Count && outcomes.Select(o => o.CallHandle).Distinct().Count() == plan.Calls.Count &&
            plan.Calls.All(c => outcomes.Any(o => o.CallHandle == c.CallHandle)), "ACCOUNTING_REQUIRES_ALL_PLANNED_CALLS");
        foreach (var o in outcomes) {
            Need(AttemptStatuses.Contains(o.Status, StringComparer.Ordinal) && o.Attempted == (o.Status != "NOT_ATTEMPTED"), "ATTEMPT_STATUS_INVALID");
            Need(o.RawObservationSha256 is null || HashValid(o.RawObservationSha256), "OBSERVATION_HASH_INVALID");
            Need(o.Usage is null || HashValid(o.RawObservationSha256), "UNBOUND_USAGE");
            Need(o.Status != "ACCEPTED" || HashValid(o.RawObservationSha256), "ACCEPTED_WITHOUT_OBSERVATION");
        }
        return new { mode = "F1_ISOLATED", metricNamespace = "F1_ISOLATED_COMPOSITE_B",
            arms = Enum.GetValues<P7CaptureArm>().Select(arm => {
                var calls = plan.Calls.Where(c => c.Identity.Arm == arm).ToArray();
                var rows = calls.Select(c => outcomes.Single(o => o.CallHandle == c.CallHandle)).ToArray();
                return new { arm, planned = calls.Length, attempted = rows.Count(o => o.Attempted),
                    statuses = AttemptStatuses.ToDictionary(s => s, s => rows.Count(o => o.Status == s)),
                    goldScope = 213, outsideScope = 430, outsideScopeStatus = "UNKNOWN_UNSCORED",
                    acceptedInScopeCoverage = calls.Where(c => rows.Single(o => o.CallHandle == c.CallHandle).Status == "ACCEPTED").Sum(c => c.InScopeOwned),
                    usage = rows.Select(o => new { o.CallHandle, observationSha256 = o.RawObservationSha256, measured = Usage(o.Usage) }).ToArray() };
            }).ToArray(), semanticScore = "NOT_COMPUTED_BY_EXECUTION_ACCOUNTING", unknownUsageIsZero = false,
            failuresConvertedToOTHER = false, validOnlyScoresMustReportCoverageAndCannotClaimCompletePopulation = true };
    }

    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string TextHash(string value) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(value));
    private static void Need(bool valid, string reason) { if (!valid) throw new InvalidOperationException("P7_D3_REJECTED:" + reason); }
}
