using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class ReasoningRecoveryContractsTests
{
    [Fact]
    public void TimeoutIsClassifiedAsWorkloadShapeRecovery()
    {
        Assert.Equal(ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
            ReasoningRecoveryRetryPolicy.Classify(ReasoningCompletionFailureClass.ProviderTotalTimeout));
    }

    [Fact]
    public void OutputLimitIsClassifiedAsWorkloadShapeRecovery()
    {
        Assert.Equal(ReasoningRecoveryRetryClass.WorkloadShapeRecovery,
            ReasoningRecoveryRetryPolicy.Classify(ReasoningCompletionFailureClass.ProviderOutputLimit));
    }

    [Fact]
    public void ProviderUnavailableIsClassifiedAsTransientTransportRetry()
    {
        Assert.Equal(ReasoningRecoveryRetryClass.TransientTransportRetry,
            ReasoningRecoveryRetryPolicy.Classify(ReasoningCompletionFailureClass.ProviderUnavailable));
    }

    [Fact]
    public void AuthFailureIsNonRecoverable()
    {
        Assert.Equal(ReasoningRecoveryRetryClass.NonRecoverable,
            ReasoningRecoveryRetryPolicy.Classify(ReasoningCompletionFailureClass.ProviderAuthFailure));
    }

    [Fact]
    public void WorkloadShapeFailureNeverSchedulesAnIdenticalRetry()
    {
        // The regression this guards: the previous campaign retried an identical giant request
        // three times on timeout. A workload-shape classification must mean "change the request
        // shape", never "try the same request again".
        foreach (var failure in new[]
        {
            ReasoningCompletionFailureClass.ProviderTotalTimeout,
            ReasoningCompletionFailureClass.ProviderOutputLimit,
            ReasoningCompletionFailureClass.ProviderSemanticPassTimeout,
        })
        {
            var retryClass = ReasoningRecoveryRetryPolicy.Classify(failure);
            Assert.Equal(ReasoningRecoveryRetryClass.WorkloadShapeRecovery, retryClass);
            Assert.NotEqual(ReasoningRecoveryRetryClass.TransientTransportRetry, retryClass);
        }
    }

    [Fact]
    public void FullContextStatusMapsTimeoutAndOutputLimitDistinctly()
    {
        Assert.Equal(FullContextAttemptStatus.Timeout, ReasoningRecoveryRetryPolicy.ToFullContextStatus(ReasoningCompletionFailureClass.ProviderTotalTimeout));
        Assert.Equal(FullContextAttemptStatus.OutputLimit, ReasoningRecoveryRetryPolicy.ToFullContextStatus(ReasoningCompletionFailureClass.ProviderOutputLimit));
        Assert.Equal(FullContextAttemptStatus.SchemaFailure, ReasoningRecoveryRetryPolicy.ToFullContextStatus(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid));
        Assert.Equal(FullContextAttemptStatus.ProviderFailure, ReasoningRecoveryRetryPolicy.ToFullContextStatus(ReasoningCompletionFailureClass.ProviderUnavailable));
    }

    [Fact]
    public void LadderStartsWithForcedSingleFullContextSegment()
    {
        var steps = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters: 400_000, totalCharacters: 60_919);
        Assert.Equal(60_920, steps[0]);
    }

    [Fact]
    public void LadderStepsDownMonotonicallyToTheFloor()
    {
        var steps = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters: 400_000, totalCharacters: 224_731);
        Assert.True(steps.Count > 2);
        for (var i = 1; i < steps.Count; i++)
            Assert.True(steps[i] < steps[i - 1], $"step {i} ({steps[i]}) did not decrease from {steps[i - 1]}");
        Assert.Equal(ReasoningSegmentSizeLadder.MinimumSegmentCharacters, steps[^1]);
    }

    [Fact]
    public void LadderNeverProducesASizeBelowTheMinimum()
    {
        var steps = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters: 15_000, totalCharacters: 224_731);
        Assert.All(steps, step => Assert.True(step >= ReasoningSegmentSizeLadder.MinimumSegmentCharacters));
    }

    [Fact]
    public void LadderIsDeterministicAcrossCalls()
    {
        var a = ReasoningSegmentSizeLadder.Steps(400_000, 224_731);
        var b = ReasoningSegmentSizeLadder.Steps(400_000, 224_731);
        Assert.Equal(a, b);
    }

    [Fact]
    public void LadderNeverRepeatsANearFullSizeStepWhenCapabilityIsNotTheLimiter()
    {
        // Regression: when the capability window comfortably exceeds the whole document, the
        // naive `min(capabilityWindow, total)` second rung collapses back to ~the same size as
        // the forced full-context step -- re-triggering the exact windowing shape that just
        // failed instead of a genuine reduction. DOC-0205 (60,919 chars) hit this: step 1 came
        // back as 60,919 (one character short of step 0's 60,920), reproduced the same
        // pathological overlap, and produced garbage headings. The second rung must be a real
        // fraction of the document.
        var steps = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters: 400_000, totalCharacters: 60_919);
        Assert.True(steps[1] <= 60_919 / 2 + 1, $"step 1 ({steps[1]}) was not a genuine reduction from the full document (60919)");
    }

    [Fact]
    public void SmallDocumentLadderCollapsesToFullContextAndFloorOnly()
    {
        var steps = ReasoningSegmentSizeLadder.Steps(capabilityWindowCharacters: 400_000, totalCharacters: 1_000);
        // The capability window already exceeds the document, so the only meaningful steps are
        // the forced full-context attempt and the floor -- no redundant intermediate windows.
        Assert.True(steps.Count <= 2);
    }
}
