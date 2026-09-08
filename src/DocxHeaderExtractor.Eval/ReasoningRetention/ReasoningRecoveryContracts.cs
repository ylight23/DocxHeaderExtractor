namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Section 1 of the executable-ceiling recovery mission: the two result classes a document can
/// land in. A segmented recovery result must never be reported as a full-context ceiling result.
/// </summary>
public static class RecoveryExecutionMode
{
    public const string FullContext = "FULL_CONTEXT";
    public const string SegmentedReasoningRecovery = "SEGMENTED_REASONING_RECOVERY";
}

/// <summary>The outcome of the single bounded full-context attempt, before any recovery.</summary>
public static class FullContextAttemptStatus
{
    public const string Success = "SUCCESS";
    public const string Timeout = "TIMEOUT";
    public const string OutputLimit = "OUTPUT_LIMIT";
    public const string SchemaFailure = "SCHEMA_FAILURE";
    public const string ProviderFailure = "PROVIDER_FAILURE";
}

/// <summary>
/// Section 2: a failed attempt is either a transient transport hiccup (safe to retry the
/// identical request a small bounded number of times) or a workload-shape failure (the request
/// itself is too large/slow for one shot -- retrying the identical request wastes wall-clock
/// time and must instead trigger segmentation).
/// </summary>
public static class ReasoningRecoveryRetryClass
{
    public const string TransientTransportRetry = "TRANSIENT_TRANSPORT_RETRY";
    public const string WorkloadShapeRecovery = "WORKLOAD_SHAPE_RECOVERY";
    public const string NonRecoverable = "NON_RECOVERABLE";
}

public static class ReasoningRecoveryRetryPolicy
{
    /// <summary>Classifies a provider failure so the caller knows whether an identical retry is
    /// ever appropriate. Timeouts and output-limit failures are workload-shape: the fix is a
    /// smaller request, never the same request again. Connection/5xx-style failures are
    /// transient transport noise and may be retried a small bounded number of times unchanged.
    /// Auth failures and unrecognized schema corruption are treated as non-recoverable by retry
    /// or segmentation -- they need a different fix entirely.</summary>
    public static string Classify(string failureClass) => failureClass switch
    {
        ReasoningCompletionFailureClass.ProviderTotalTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.ProviderSemanticPassTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.ProviderFirstByteTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.DocumentTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.RouteTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.RepeatTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.CampaignTimeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.Timeout => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.ProviderOutputLimit => ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
        ReasoningCompletionFailureClass.ProviderAuthFailure => ReasoningRecoveryRetryClass.NonRecoverable,
        ReasoningCompletionFailureClass.UserCancelled => ReasoningRecoveryRetryClass.NonRecoverable,
        _ => ReasoningRecoveryRetryClass.TransientTransportRetry,
    };

    public static string ToFullContextStatus(string failureClass) => failureClass switch
    {
        ReasoningCompletionFailureClass.ProviderOutputLimit => FullContextAttemptStatus.OutputLimit,
        ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid => FullContextAttemptStatus.SchemaFailure,
        _ when Classify(failureClass) == ReasoningRecoveryRetryClass.WorkloadShapeRecovery => FullContextAttemptStatus.Timeout,
        _ => FullContextAttemptStatus.ProviderFailure,
    };
}

/// <summary>
/// Section 5: a generic, deterministic, capability-derived recovery ladder. It never treats the
/// old fixed 48K-char window as unquestioned authority -- it starts from the real capability
/// window and steps down only after an observed workload-shape failure at the current size,
/// stopping at a minimum safe segment size rather than looping forever.
/// </summary>
public static class ReasoningSegmentSizeLadder
{
    public const int MinimumSegmentCharacters = 12_000;

    /// <summary>Step 0 is always a forced single full-context segment (maxContextCharacters larger
    /// than the whole document). Subsequent steps are capability-window, then successive halvings,
    /// down to <see cref="MinimumSegmentCharacters"/>.</summary>
    public static IReadOnlyList<int> Steps(int capabilityWindowCharacters, int totalCharacters)
    {
        if (capabilityWindowCharacters < 1) throw new ArgumentOutOfRangeException(nameof(capabilityWindowCharacters));
        if (totalCharacters < 0) throw new ArgumentOutOfRangeException(nameof(totalCharacters));

        var steps = new List<int> { totalCharacters + 1 };
        // If the capability window is not the real limiter (it is >= the whole document), the
        // next rung must still be a genuine reduction -- half the document -- rather than
        // collapsing back to (near) the full-document size, which would re-trigger the same
        // pathological windowing shape that just failed.
        var window = capabilityWindowCharacters < totalCharacters ? capabilityWindowCharacters : totalCharacters / 2;
        while (window >= MinimumSegmentCharacters)
        {
            if (steps[^1] != window) steps.Add(window);
            if (window == MinimumSegmentCharacters) break;
            window = Math.Max(MinimumSegmentCharacters, window / 2);
        }
        if (steps[^1] != MinimumSegmentCharacters) steps.Add(MinimumSegmentCharacters);
        return steps;
    }
}

/// <summary>Provenance for one recovered proposal (section 8): final semantic identity is still
/// sourceId + exact span, but the segment and raw response that produced it are recorded.</summary>
public sealed record ReasoningRecoveryProvenance(
    string ProposalKey,
    string SegmentId,
    string RawResponseHash);

/// <summary>Per-segment execution telemetry recorded for the freeze contract (section 10).</summary>
public sealed record ReasoningRecoverySegmentOutcome(
    string SegmentId,
    int Ordinal,
    int WindowCharacters,
    int Attempts,
    bool Succeeded,
    string? FailureClass,
    string? RawResponseHash,
    int? ReasoningTokens,
    int? OutputTokens,
    int? InputTokens,
    long ElapsedMs);
