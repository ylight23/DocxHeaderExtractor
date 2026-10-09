using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record P7PostAccountedAuthorization(string Version, bool Authorized, int PrimaryCallCap,
    int HttpAttemptCap, int RetriesPerRequest, string RequestManifestSha256, string BudgetPolicy,
    string EndpointBindingSha256, string HumanInstruction, string HumanInstructionSha256);
internal sealed record P7PostAccountedAttempt(string CallHandle, int Attempt, string Status, string? ErrorType,
    string? ErrorCode, string BodySha256, string EndpointSnapshotSha256, string? PreviousAttemptSha256,
    string? RawFreezeSha256, string? ObservationSha256, string? ParsedDecisionSha256,
    string? FinishReason, P7F1UsageSummary Usage, string? SelectedDecisionFile);

/// <summary>Explicit V2 policy: user-authorized call-bounded execution with post-accounting.
/// Not a hard USD limit. All failures, including potentially billed failures, remain in the journal.
/// Does not inspect Gold, retry semantic errors, repair prompts, or start downstream stages.</summary>
internal sealed class P7F1PostAccountedRunner
{
    public const string Version = "P7_D3_F1_POST_ACCOUNTED_AUTHORIZED_EXECUTION_V2";
    public const string BudgetPolicy = "USER_ACCEPTED_POST_ACCOUNTING_NO_APPROVED_USD_CAP";
    private int started;
    public static void ValidateAuthorization(P7PostAccountedAuthorization a, P7F1ExecutionPlan plan, P7EndpointBinding binding)
    {
        P7FinancialQualification.Need(a.Version == Version && a.Authorized && a.PrimaryCallCap == 14 &&
            a.HttpAttemptCap == 28 && a.RetriesPerRequest == 1 && plan.Calls.Count == 14 &&
            a.RequestManifestSha256 == plan.RequestManifestSha256 && a.BudgetPolicy == BudgetPolicy &&
            a.EndpointBindingSha256 == binding.BindingSha256 && !string.IsNullOrWhiteSpace(a.HumanInstruction) &&
            SpatialCanonical.Hash(Encoding.UTF8.GetBytes(a.HumanInstruction)) == a.HumanInstructionSha256,
            "NEW_V2_AUTHORIZATION_REQUIRED");
    }
    public async Task<IReadOnlyList<P7PostAccountedAttempt>> RunAsync(P7PostAccountedAuthorization authorization,
        P7F1ExecutionPlan plan, IReadOnlyDictionary<string, byte[]> frozenBodies, P7EndpointBinding binding,
        IP7F1RunnerTransport transport, string newPrivateDirectory, Func<CancellationToken, Task<P7RunnerMetadata>> metadata,
        Func<P7F1ExecutionCall, string, JsonElement> validate, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("NO_REEXECUTION");
        ValidateAuthorization(authorization, plan, binding);
        var p = transport.Policy;
        P7FinancialQualification.Need(!p.FixtureOnly && p.Endpoint == binding.ChatEndpoint && p.Model == binding.Model &&
            p.Provider == binding.Tag && p.Retries == 0 && !p.Fallback && p.TimeoutSeconds == 300, "TRANSPORT_POLICY_DRIFT");
        var bodies = plan.Calls.ToDictionary(c => c.CallHandle, c => frozenBodies[c.CallHandle].ToArray());
        foreach (var c in plan.Calls) P7F1ExecutionReadiness.ValidateBody(c, bodies[c.CallHandle]);
        P7FinancialQualification.Need(!Directory.Exists(newPrivateDirectory), "NEW_JOURNAL_REQUIRED");
        Directory.CreateDirectory(newPrivateDirectory);
        Write(Path.Combine(newPrivateDirectory, "authorization.json"), SpatialCanonical.Bytes(authorization));
        var attempts = new List<P7PostAccountedAttempt>();
        foreach (var call in plan.Calls)
        {
            string? previous = null;
            for (var number = 1; number <= 2; number++)
            {
                ct.ThrowIfCancellationRequested();
                var current = await metadata(ct);
                P7FinancialQualification.CheckFreshBinding(binding, current.Bytes, current.ObservedAt, DateTimeOffset.UtcNow);
                var directory = Path.Combine(newPrivateDirectory, call.Sequence.ToString("D2"), "attempt-" + number);
                Directory.CreateDirectory(directory);
                Write(Path.Combine(directory, "endpoint-metadata.json"), current.Bytes);
                Write(Path.Combine(directory, "reservation.json"), SpatialCanonical.Bytes(new {
                    call.CallHandle, attempt = number, authorizationSha256 = SpatialCanonical.Hash(SpatialCanonical.Bytes(authorization)),
                    bodySha256 = call.Identity.ProviderBodySha256, previousAttemptSha256 = previous,
                    maximumUsd = (decimal?)null, budgetPolicy = BudgetPolicy, exposure = "POTENTIALLY_BILLABLE_NOT_CAPPED_IN_USD",
                    metadataObservedAt = current.ObservedAt, internalRetries = 0, repair = false, fallback = false }));
                // Persist dispatched identity before network activity; never overwrite a failed primary with its retry.
                Write(Path.Combine(directory, "provider-body.json"), bodies[call.CallHandle]);
                P7RunnerRawObservation? observation = null;
                string status = "TRANSPORT_FAILURE"; string? errorType = null, errorCode = null;
                try
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { call = call.Sequence, arm = call.Identity.Arm.ToString(), attempt = number, status = "DISPATCH" }));
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(300));
                    var pending = transport.SendOnceAsync(bodies[call.CallHandle], deadline.Token);
                    // Observe a late fault without adopting a late result after the independent deadline.
                    _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    observation = await pending.WaitAsync(deadline.Token);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    errorType = e.GetType().Name;
                    errorCode = e is OperationCanceledException ? "ATTEMPT_TIMEOUT_CHARGE_UNKNOWN" : "TRANSPORT_ERROR_CHARGE_UNKNOWN";
                    // Exception details stay private. Public receipt contains classification, not source text or credentials.
                    Write(Path.Combine(directory, "private-error.txt"), Encoding.UTF8.GetBytes(e.ToString()));
                }
                string? rawHash = null, observationHash = null, parsedHash = null, selectedFile = null;
                if (observation is not null)
                {
                    var raw = SpatialCanonical.Bytes(observation);
                    Write(Path.Combine(directory, "observation.json"), raw);
                    var response = Encoding.UTF8.GetBytes(observation.Content); var sse = Encoding.UTF8.GetBytes(observation.RawSse);
                    Write(Path.Combine(directory, "response.txt"), response); Write(Path.Combine(directory, "response.sse"), sse);
                    var freeze = SpatialCanonical.Bytes(new { status = "RAW_FROZEN_BEFORE_PARSE", bodySha256 = call.Identity.ProviderBodySha256,
                        responseSha256 = SpatialCanonical.Hash(response), sseSha256 = SpatialCanonical.Hash(sse), observationSha256 = SpatialCanonical.Hash(raw) });
                    Write(Path.Combine(directory, "raw-freeze.json"), freeze);
                    rawHash = SpatialCanonical.Hash(freeze); observationHash = SpatialCanonical.Hash(raw);
                    try
                    {
                        if (observation.RetryCount != 0) throw new InvalidOperationException("UNAUTHORIZED_INTERNAL_RETRY");
                        if (observation.FinishReason != "stop") throw new InvalidOperationException("NON_STOP_FINISH");
                        if (response.Length > call.ResponseUtf8ByteCap) throw new InvalidOperationException("RESPONSE_CAP_EXCEEDED");
                        var parsed = SpatialCanonical.Bytes(validate(call, observation.Content));
                        Write(Path.Combine(directory, "parsed-decision.json"), parsed);
                        parsedHash = SpatialCanonical.Hash(parsed);
                        selectedFile = Path.GetRelativePath(newPrivateDirectory, Path.Combine(directory, "parsed-decision.json")).Replace('\\', '/');
                        status = "ACCEPTED";
                    }
                    catch (Exception e)
                    {
                        if (e.Message == "UNAUTHORIZED_INTERNAL_RETRY") throw;
                        status = "CONTRACT_FAILURE"; errorType = e.GetType().Name;
                        errorCode = e is JsonException ? "JSON_INVALID" : e is EvidenceAssessmentException ? "EVIDENCE_REFERENCE_OR_ASSERTION_INVALID" :
                            e.Message is "NON_STOP_FINISH" or "RESPONSE_CAP_EXCEEDED" ? e.Message : "STRICT_PROTOCOL_REJECTED";
                        Write(Path.Combine(directory, "private-error.txt"), Encoding.UTF8.GetBytes(e.ToString()));
                    }
                }
                var attempt = new P7PostAccountedAttempt(call.CallHandle, number, status, errorType, errorCode,
                    call.Identity.ProviderBodySha256, SpatialCanonical.Hash(current.Bytes), previous, rawHash, observationHash,
                    parsedHash, observation?.FinishReason, P7F1ExecutionReadiness.Usage(observation?.Usage), selectedFile);
                var receipt = SpatialCanonical.Bytes(attempt); Write(Path.Combine(directory, "attempt-receipt.json"), receipt);
                previous = SpatialCanonical.Hash(receipt); attempts.Add(attempt);
                Console.WriteLine(JsonSerializer.Serialize(new { call = call.Sequence, attempt = number, status, errorType, errorCode,
                    cost = attempt.Usage.ReportedCostUsd, prompt = attempt.Usage.PromptTokens, completion = attempt.Usage.CompletionTokens }));
                if (status == "ACCEPTED") break;
            }
        }
        var capture = SpatialCanonical.Bytes(new { version = Version, status = "PROVIDER_GATE_CLOSED_RAW_CAPTURE_FROZEN",
            authorizationSha256 = SpatialCanonical.Hash(SpatialCanonical.Bytes(authorization)), attempts,
            primaryCalls = attempts.Count(a => a.Attempt == 1), retries = attempts.Count(a => a.Attempt == 2),
            acceptedRequests = attempts.Count(a => a.Status == "ACCEPTED"), knownCostUsd = attempts.Sum(a => a.Usage.ReportedCostUsd ?? 0),
            unknownChargeAttempts = attempts.Count(a => a.Usage.ReportedCostUsd is null), unknownChargeIsZero = false,
            goldReadDuringExecution = false, repair = false, fallback = false, productionChanged = false, downstreamCalls = 0 });
        Write(Path.Combine(newPrivateDirectory, "capture-freeze.json"), capture);
        return attempts.AsReadOnly();
    }
    internal static void Write(string path, byte[] bytes)
    { using var file = new FileStream(path, FileMode.CreateNew); file.Write(bytes); file.Flush(true); }
}
