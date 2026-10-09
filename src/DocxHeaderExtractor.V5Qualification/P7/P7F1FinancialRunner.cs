using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record P7RunnerTransportPolicy(string Endpoint, string Model, string Provider, int Retries,
    bool Fallback, int TimeoutSeconds, bool FixtureOnly);
internal sealed record P7RunnerMetadata(byte[] Bytes, DateTimeOffset ObservedAt);
internal sealed record P7RunnerBound(string CallHandle, string BodySha256, string BindingSha256,
    decimal MaximumUsd, string ProofSha256, string Basis);
internal sealed record P7RunnerApproval(string ExecutionManifestSha256, string FinancialManifestSha256,
    string? ApprovalReceiptSha256, bool CallsAuthorized, decimal? ApprovedUsdCap,
    bool DedicatedKeyVerified, decimal? KeyLimit, decimal? InitialKeyRemaining, string? KeyReceiptSha256,
    IReadOnlyList<P7RunnerBound> Bounds, bool FixtureOnly);
internal sealed record P7RunnerRawObservation(string Content, string? FinishReason, JsonElement? Usage,
    string RawSse, int SseEventCount, int RetryCount);
internal sealed record P7RunnerRawReceipt(string ObservationSha256, string ResponseSha256, string SseSha256, string RawFreezeSha256);
internal sealed record P7RunnerSlot(string CallHandle, string BodySha256, bool Attempted, string Status,
    string? ObservationSha256 = null, decimal? CostUsd = null, decimal? ReservedUsd = null);
internal sealed record P7RunnerResult(string Status, string? HaltReason, IReadOnlyList<P7RunnerSlot> Calls,
    decimal KnownCostUsd, decimal OutstandingExposureUsd, int TransportInvocations, bool FixtureOnly,
    string ProtocolValidation = "NOT_PERFORMED_RAW_CAPTURE_ONLY", string SemanticAccuracy = "NOT_EVALUATED",
    string DownstreamExecution = "LOCKED");

internal interface IP7F1RunnerTransport
{
    P7RunnerTransportPolicy Policy { get; }
    // Exactly one transport attempt. Runner owns the only call site for this seam.
    Task<P7RunnerRawObservation> SendOnceAsync(byte[] frozenBody, CancellationToken cancellationToken);
}
internal interface IP7F1RunnerJournal
{
    Task BeginAsync(byte[] runIdentity, CancellationToken ct);
    Task ReservationAsync(string callHandle, byte[] reservation, CancellationToken ct);
    Task<P7RunnerRawReceipt> FreezeRawAsync(string callHandle, byte[] body, P7RunnerRawObservation observation);
    Task FinancialAsync(string callHandle, byte[] receipt);
    Task CompleteAsync(byte[] result);
}

/// <summary>One-shot qualification F1 capture runner. No Gold, parsing substitutes, downstream, retry or recovery.
/// The verified bound is external approval evidence, never the advertised stress estimate.</summary>
internal sealed class P7F1FinancialRunner
{
    public const string Version = "P7_D3_F1_FINANCIAL_RUNNER_V1";
    public const string FinancialManifestSha256 = "fef44ccb94c4770b3f4a53363f05711a2243296f1226b2bebd2b2ee237c3c51c";
    private readonly P7F1ExecutionPlan plan;
    private readonly IReadOnlyDictionary<string, byte[]> bodies;
    private readonly P7EndpointBinding binding;
    private readonly IP7F1RunnerTransport transport;
    private readonly IP7F1RunnerJournal journal;
    private readonly Func<CancellationToken, Task<P7RunnerMetadata>> metadata;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeSpan deadline;
    private readonly bool fixtureOnly;
    private int started;

    internal P7F1FinancialRunner(P7F1ExecutionPlan plan, IReadOnlyDictionary<string, byte[]> bodies,
        P7EndpointBinding binding, IP7F1RunnerTransport transport, IP7F1RunnerJournal journal,
        Func<CancellationToken, Task<P7RunnerMetadata>> metadata, Func<DateTimeOffset> now,
        bool fixtureOnly, TimeSpan? fixtureDeadline = null)
    {
        this.plan = plan with { Calls = Array.AsReadOnly(plan.Calls.ToArray()) };
        // Caller cannot mutate a future payload after validation or while the first call runs.
        this.bodies = bodies.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        this.binding = binding; this.transport = transport; this.journal = journal; this.metadata = metadata; this.now = now;
        this.fixtureOnly = fixtureOnly;
        P7FinancialQualification.Need(fixtureDeadline is null || fixtureOnly, "LIVE_DEADLINE_OVERRIDE_FORBIDDEN");
        deadline = fixtureDeadline ?? TimeSpan.FromSeconds(300);
    }

    public static P7F1ExecutionPlan ValidateFrozenPackage(byte[] execution, byte[] financial, byte[] requestManifest, byte[] pinnedMetadata)
    {
        Need(SpatialCanonical.Hash(execution) == P7FinancialQualification.ExecutionManifestSha256, "EXECUTION_MANIFEST_DRIFT");
        Need(SpatialCanonical.Hash(financial) == FinancialManifestSha256, "FINANCIAL_MANIFEST_DRIFT");
        var p = P7F1ExecutionReadiness.Prepare(requestManifest);
        using var f = JsonDocument.Parse(financial);
        var endpoint = P7FinancialQualification.Bind(pinnedMetadata);
        Need(f.RootElement.GetProperty("binding").GetProperty("metadataSha256").GetString() == endpoint.MetadataSha256 &&
            f.RootElement.GetProperty("binding").GetProperty("bindingSha256").GetString() == endpoint.BindingSha256, "PINNED_METADATA_DRIFT");
        return p;
    }

    public async Task<P7RunnerResult> RunAsync(P7RunnerApproval? approval, CancellationToken ct = default)
    {
        Need(Interlocked.Exchange(ref started, 1) == 0, "RUN_ONCE_NO_REPLAY");
        approval = approval is null ? null : approval with { Bounds = Array.AsReadOnly(approval.Bounds.ToArray()) };
        var slots = plan.Calls.Select(c => new P7RunnerSlot(c.CallHandle, c.Identity.ProviderBodySha256, false, "NOT_ATTEMPTED")).ToArray();
        decimal known = 0, outstanding = 0; var invocations = 0; string? halt = null;
        try {
            ValidatePlanAndApproval(approval);
            // Validate all 14 bodies before the first reservation/send, including future slots.
            Need(bodies.Count == 14 && plan.Calls.All(c => bodies.ContainsKey(c.CallHandle)), "BODY_UNIVERSE_DRIFT");
            foreach (var call in plan.Calls) P7F1ExecutionReadiness.ValidateBody(call, bodies[call.CallHandle]);
            ct.ThrowIfCancellationRequested();
            await journal.BeginAsync(SpatialCanonical.Bytes(new { version = Version, fixtureOnly, executionManifestSha256 = P7FinancialQualification.ExecutionManifestSha256,
                financialManifestSha256 = FinancialManifestSha256, approval }), ct).ConfigureAwait(false);
            foreach (var call in plan.Calls) {
                ct.ThrowIfCancellationRequested();
                var fresh = await metadata(ct).ConfigureAwait(false);
                P7FinancialQualification.CheckFreshBinding(binding, fresh.Bytes, fresh.ObservedAt, now());
                ValidateTransportPolicy();
                var bound = approval!.Bounds.Single(b => b.CallHandle == call.CallHandle);
                Need(known + bound.MaximumUsd <= approval.ApprovedUsdCap &&
                    known + bound.MaximumUsd <= approval.InitialKeyRemaining, "NEXT_REQUEST_EXCEEDS_CAP");
                var reservation = new P7FinancialReservation(call.CallHandle, call.Identity.ProviderBodySha256, bound.MaximumUsd);
                // Durable reservation precedes transport. No rollback can erase possible provider exposure.
                await journal.ReservationAsync(call.CallHandle, SpatialCanonical.Bytes(new {
                    executionManifestSha256 = P7FinancialQualification.ExecutionManifestSha256, financialManifestSha256 = FinancialManifestSha256,
                    approvalReceiptSha256 = approval.ApprovalReceiptSha256, keyReceiptSha256 = approval.KeyReceiptSha256,
                    endpointBindingSha256 = binding.BindingSha256, freshMetadataSha256 = SpatialCanonical.Hash(fresh.Bytes),
                    fresh.ObservedAt, reservation, bound, knownCostUsd = known, fixtureOnly }), ct).ConfigureAwait(false);
                outstanding = bound.MaximumUsd;
                ct.ThrowIfCancellationRequested();
                slots[call.Sequence - 1] = slots[call.Sequence - 1] with { Attempted = true, Status = "ATTEMPT_STARTED", ReservedUsd = bound.MaximumUsd };
                invocations++;
                using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task<P7RunnerRawObservation>? task = null;
                P7RunnerRawObservation observation;
                try {
                    // A defensive copy ensures even a faulty transport cannot corrupt other frozen slots.
                    task = transport.SendOnceAsync(bodies[call.CallHandle].ToArray(), callCancellation.Token);
                    observation = await task.WaitAsync(deadline, ct).ConfigureAwait(false);
                } catch {
                    callCancellation.Cancel();
                    if (task is not null) _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    slots[call.Sequence - 1] = slots[call.Sequence - 1] with { Status = "TRANSPORT_FAILURE_UNKNOWN_PAYMENT" };
                    throw;
                }
                // From here capture/accounting must run even if caller cancels: usage may already be billable.
                var raw = await journal.FreezeRawAsync(call.CallHandle, bodies[call.CallHandle], observation).ConfigureAwait(false);
                var observedBytes = JsonSerializer.SerializeToUtf8Bytes(new { observation.Content, observation.FinishReason, observation.Usage,
                    observation.RawSse, observation.SseEventCount, observation.RetryCount });
                Need(raw.ObservationSha256 == SpatialCanonical.Hash(observedBytes) &&
                    raw.ResponseSha256 == SpatialCanonical.Hash(Encoding.UTF8.GetBytes(observation.Content)) &&
                    raw.SseSha256 == SpatialCanonical.Hash(Encoding.UTF8.GetBytes(observation.RawSse)) &&
                    P7FinancialQualification.IsHash(raw.RawFreezeSha256), "RAW_RECEIPT_INVALID");
                var u = P7F1ExecutionReadiness.Usage(observation.Usage);
                if (u.ReportedCostUsd.HasValue) known = checked(known + u.ReportedCostUsd.Value);
                var complete = u.PromptTokens.HasValue && u.CompletionTokens.HasValue && u.ReportedCostUsd.HasValue;
                var exceeded = u.ReportedCostUsd > bound.MaximumUsd || known > approval.ApprovedUsdCap || known > approval.InitialKeyRemaining ||
                    u.PromptTokens > binding.MaxPromptTokens || u.CompletionTokens > 32768 || u.ReasoningTokens > u.CompletionTokens;
                var financialStatus = !complete ? "USAGE_OR_COST_INCOMPLETE_HALT" : exceeded ? "BOUND_EXCEEDED_HALT" : "SETTLED_PROVIDER_REPORTED_NOT_BILLING_AUDIT";
                // Complete usage reconciles a charge; unknown usage retains the full outstanding reservation.
                if (complete) outstanding = 0;
                var settlement = new P7FinancialSettlement(call.CallHandle, call.Identity.ProviderBodySha256, raw.ObservationSha256,
                    u.PromptTokens, u.CompletionTokens, u.ReasoningTokens, u.ReportedCostUsd, known, outstanding, financialStatus);
                slots[call.Sequence - 1] = slots[call.Sequence - 1] with {
                    Status = !complete || exceeded ? financialStatus : "RAW_CAPTURED_PENDING_PROTOCOL_VALIDATION",
                    ObservationSha256 = raw.ObservationSha256, CostUsd = u.ReportedCostUsd };
                await journal.FinancialAsync(call.CallHandle, P7FinancialQualification.Receipt(FinancialManifestSha256,
                    P7FinancialQualification.ExecutionManifestSha256, binding with { MetadataSha256 = SpatialCanonical.Hash(fresh.Bytes) }, reservation, settlement)).ConfigureAwait(false);
                if (!complete || exceeded) { halt = financialStatus; break; }
                if (observation.RetryCount != 0 || observation.FinishReason != "stop" || Encoding.UTF8.GetByteCount(observation.Content) > call.ResponseUtf8ByteCap) {
                    slots[call.Sequence - 1] = slots[call.Sequence - 1] with { Status = "TRANSPORT_PROTOCOL_OR_RESPONSE_CAP_INVALID" };
                    halt = "TRANSPORT_PROTOCOL_OR_RESPONSE_CAP_INVALID"; break;
                }
            }
        } catch (Exception e) {
            halt = e switch { TimeoutException => "TIMEOUT_UNKNOWN_PAYMENT", OperationCanceledException => "CANCELLED_NO_REPLAY", _ => e.Message };
        }
        var result = new P7RunnerResult(halt is null ? "RAW_ATTEMPTS_COMPLETED" : "HALTED", halt,
            Array.AsReadOnly(slots), known, outstanding, invocations, fixtureOnly);
        // A storage failure is a hard stop, never a reason to run again or release outstanding exposure.
        try { await journal.CompleteAsync(SpatialCanonical.Bytes(result)).ConfigureAwait(false); }
        catch { result = result with { Status = "HALTED", HaltReason = "FINAL_JOURNAL_WRITE_FAILED" }; }
        return result;
    }

    private void ValidatePlanAndApproval(P7RunnerApproval? a)
    {
        Need(plan.Calls.Count == 14 && plan.Calls.Select(c => c.Sequence).SequenceEqual(Enumerable.Range(1, 14)) &&
            plan.Calls.Select(c => c.CallHandle).Distinct().Count() == 14 && plan.HttpAttemptCap == 14 && plan.PrimaryCallCap == 14 &&
            plan.ConcurrentCallCap == 1 && plan.TimeoutSecondsPerAttempt == 300 && plan.RetryCount == 0 && !plan.Repair && !plan.Fallback &&
            plan.CompletionTokenCeilingPerCall == 32768 && plan.RequestManifestSha256 == P7F1ExecutionReadiness.FrozenRequestManifestSha256 &&
            plan.Calls.All(c => c.Identity.Stage == InterpretationStage.F1 && c.Identity.Mode == P7ExperimentMode.SharedF1Root && c.Identity.ParentCaptureSha256.Count == 0), "RUNNER_PLAN_DRIFT");
        ValidateTransportPolicy();
        Need(a is not null && a.CallsAuthorized && P7FinancialQualification.IsHash(a.ApprovalReceiptSha256) &&
            a.ExecutionManifestSha256 == P7FinancialQualification.ExecutionManifestSha256 && a.FinancialManifestSha256 == FinancialManifestSha256 &&
            a.FixtureOnly == fixtureOnly, "AUTHORIZATION_MISSING_OR_DRIFT");
        Need(a!.ApprovedUsdCap is > 0, "APPROVED_USD_CAP_MISSING");
        Need(a.DedicatedKeyVerified && P7FinancialQualification.IsHash(a.KeyReceiptSha256) &&
            a.KeyLimit is > 0 && a.KeyLimit <= a.ApprovedUsdCap && a.InitialKeyRemaining is >= 0 && a.InitialKeyRemaining <= a.KeyLimit, "KEY_RECEIPT_NOT_VERIFIED");
        Need(a.Bounds.Count == 14 && a.Bounds.Select(b => b.CallHandle).Distinct().Count() == 14 && plan.Calls.All(c =>
            a.Bounds.Any(b => b.CallHandle == c.CallHandle && b.BodySha256 == c.Identity.ProviderBodySha256 && b.BindingSha256 == binding.BindingSha256 &&
                b.MaximumUsd > 0 && P7FinancialQualification.IsHash(b.ProofSha256) &&
                b.Basis == (fixtureOnly ? "FIXTURE_ONLY_ALL_CHARGES_BOUND" : "EXTERNALLY_VERIFIED_ALL_BILLABLE_CHARGES_BOUND"))), "VERIFIED_EXPOSURE_BOUND_MISSING_OR_DRIFT");
    }
    private void ValidateTransportPolicy()
    {
        var p = transport.Policy;
        Need(p.Endpoint == P7FinancialQualification.ChatEndpoint && p.Model == binding.Model && p.Provider == "alibaba" &&
            p.Retries == 0 && !p.Fallback && p.TimeoutSeconds == 300 && p.FixtureOnly == fixtureOnly, "TRANSPORT_BINDING_INVALID");
    }
    private static void Need(bool c, string reason) => P7FinancialQualification.Need(c, reason);
}
