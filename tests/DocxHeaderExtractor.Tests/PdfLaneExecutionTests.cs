using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLaneExecutionTests
{
    [Fact]
    public async Task Successful_work_wins_with_a_single_terminal_transition()
    {
        var result = await PdfLaneExecution.RunAsync(
            (_, _) => Task.FromResult("ok"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("ok", result.Value);
        Assert.Equal(PdfLaneExecutionState.Succeeded, result.State);
        Assert.Equal(1, result.Lease.TerminalTransitionCount);
        Assert.False(result.Lease.LateCompletionObserved);
    }

    [Fact]
    public async Task Failure_before_deadline_is_the_terminal_failure()
    {
        var result = await PdfLaneExecution.RunAsync<string>(
            (_, _) => Task.FromException<string>(new InvalidOperationException("before-deadline")),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PdfLaneExecutionState.Failed, result.State);
        Assert.Equal("before-deadline", result.Fault?.Message);
        Assert.Equal(1, result.Lease.TerminalTransitionCount);
    }

    [Fact]
    public async Task Timeout_quarantines_late_success_and_blocks_publication_and_fanout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = false;
        var fanoutStarted = false;
        var result = await PdfLaneExecution.RunAsync(
            async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return "late";
            },
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal(PdfLaneExecutionState.TimedOut, result.State);
        Assert.Null(result.Value);
        Assert.NotNull(result.DetachedTask);
        Assert.False(result.Lease.TryPublish(() => published = true));
        Assert.False(result.Lease.TryStartDownstream(() => fanoutStarted = true));

        release.SetResult();
        await result.DetachedTask!;

        Assert.True(result.Lease.LateCompletionObserved);
        Assert.False(published);
        Assert.False(fanoutStarted);
    }

    [Fact]
    public async Task Timeout_observes_and_quarantines_late_failure_without_rethrowing_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await PdfLaneExecution.RunAsync<string>(
            async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException("late-failure");
            },
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal(PdfLaneExecutionState.TimedOut, result.State);
        release.SetResult();
        await result.DetachedTask!;

        Assert.Equal("late-failure", result.Lease.LateFault?.Message);
        Assert.True(result.Lease.LateCompletionObserved);
    }

    [Fact]
    public async Task Cancellation_quarantines_late_success_and_late_failure()
    {
        var releaseSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var successTask = PdfLaneExecution.RunAsync(
            async (_, _) =>
            {
                await releaseSuccess.Task.ConfigureAwait(false);
                return "late";
            },
            TimeSpan.FromSeconds(1),
            cancellation.Token);
        cancellation.Cancel();
        var success = await successTask;

        Assert.Equal(PdfLaneExecutionState.Cancelled, success.State);
        releaseSuccess.SetResult();
        await success.DetachedTask!;
        Assert.True(success.Lease.LateCompletionObserved);

        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondCancellation = new CancellationTokenSource();
        var failureTask = PdfLaneExecution.RunAsync<string>(
            async (_, _) =>
            {
                await releaseFailure.Task.ConfigureAwait(false);
                throw new InvalidOperationException("late-cancelled-failure");
            },
            TimeSpan.FromSeconds(1),
            secondCancellation.Token);
        secondCancellation.Cancel();
        var failure = await failureTask;

        Assert.Equal(PdfLaneExecutionState.Cancelled, failure.State);
        releaseFailure.SetResult();
        await failure.DetachedTask!;
        Assert.Equal("late-cancelled-failure", failure.Lease.LateFault?.Message);
    }

    [Fact]
    public async Task Pre_cancelled_caller_does_not_start_the_lane()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var started = false;

        var result = await PdfLaneExecution.RunAsync(
            (_, _) =>
            {
                started = true;
                return Task.FromResult("must-not-start");
            },
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.Equal(PdfLaneExecutionState.Cancelled, result.State);
        Assert.True(result.Cancelled);
        Assert.False(started);
    }

    [Fact]
    public void Terminal_transition_is_monotonic_and_single_winner()
    {
        var lease = new PdfLaneExecutionLease();

        Assert.True(lease.TryTransition(PdfLaneExecutionState.TimedOut));
        Assert.False(lease.TryTransition(PdfLaneExecutionState.Failed));
        Assert.False(lease.TryPublish(() => { }));
        Assert.False(lease.TryStartDownstream(() => { }));
        Assert.Equal(PdfLaneExecutionState.TimedOut, lease.State);
        Assert.Equal(1, lease.TerminalTransitionCount);
    }

    [Fact]
    public async Task HangingSemanticDoesNotBlockVisualAndLeavesPartialArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-lane-{Guid.NewGuid():N}.json");
        try
        {
            var visual = Task.FromResult(new { scheduled = 43, completed = 43 });
            var semantic = await PdfLaneExecution.RunAsync<string>(
                _ => new TaskCompletionSource<string>().Task,
                TimeSpan.FromMilliseconds(25), CancellationToken.None);
            var visualResult = await visual;

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                runStatus = semantic.TimedOut ? "partial_timeout" : "complete",
                semantic = new { scheduled = 160, completed = 0, timedOut = 160 },
                visual = visualResult,
            }));

            Assert.True(semantic.TimedOut);
            Assert.Equal(43, visualResult.completed);
            Assert.True(File.Exists(path));
            Assert.Contains("partial_timeout", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
