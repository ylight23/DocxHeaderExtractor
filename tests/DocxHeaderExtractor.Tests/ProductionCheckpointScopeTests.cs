using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionCheckpointScopeTests
{
    [Fact]
    public async Task Each_scope_has_a_unique_run_owned_path_and_cleans_up()
    {
        await using var first = ProductionCheckpointScope.Create();
        await using var second = ProductionCheckpointScope.Create();

        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.Contains(Path.Combine("DocxHeaderExtractor", "authority-runs"),
            first.CheckpointPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("input.docx", first.CheckpointPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(second.DirectoryPath));

        var firstDirectory = first.DirectoryPath;
        var secondDirectory = second.DirectoryPath;
        await first.DisposeAsync();
        await second.DisposeAsync();

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
    }

    [Fact]
    public async Task Cleanup_is_idempotent_after_fault_or_cancellation_path()
    {
        var scope = ProductionCheckpointScope.Create();
        var directory = scope.DirectoryPath;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                try
                {
                    throw new InvalidOperationException("test fault");
                }
                finally
                {
                    await scope.DisposeAsync();
                }
            });
        }
        finally
        {
            await scope.DisposeAsync();
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Detached_writer_finishes_before_checkpoint_directory_is_removed()
    {
        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, false, "test.pdf");
        await checkpoint.RecordSpanBatchAsync(
            [("b0", 1, "l0", (IReadOnlyList<string>)["l0"], new TextOffsetSpan(0, 1))],
            null, CancellationToken.None);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerFault = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var lane = await PdfLaneExecution.RunAsync(async ct =>
        {
            await releaseWriter.Task.ConfigureAwait(false);
            try
            {
                await checkpoint.RecordSpanBatchAsync(
                    [("b1", 1, "l1", (IReadOnlyList<string>)["l1"], new TextOffsetSpan(0, 1))],
                    null, CancellationToken.None);
                writerFault.SetResult(null);
            }
            catch (Exception ex)
            {
                writerFault.SetResult(ex);
                throw;
            }
            return "done";
        }, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.True(lane.TimedOut);
        Assert.NotNull(lane.DetachedTask);
        var directory = scope.DirectoryPath;
        await checkpoint.StopAcceptingWritesAndDrainAsync();
        await checkpoint.DisposeAsync();
        await scope.DisposeAsync();
        Assert.False(Directory.Exists(directory));

        // The detached provider is still allowed to complete, but its late write is blocked.
        releaseWriter.SetResult();
        await lane.DetachedTask!;
        Assert.Null(await writerFault.Task);
        Assert.False(File.Exists(Path.Combine(directory, "span-checkpoint.jsonl")));
    }

    [Fact]
    public async Task Admitted_write_with_pre_cancelled_token_exits_and_drains()
    {
        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, false, "test.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint.RecordSpanBatchAsync(
            [("b1", 1, "l1", (IReadOnlyList<string>)["l1"], new TextOffsetSpan(0, 1))],
            null, cancellation.Token));

        await checkpoint.StopAcceptingWritesAndDrainAsync();
        await checkpoint.DisposeAsync();
        await scope.DisposeAsync();
        Assert.False(Directory.Exists(scope.DirectoryPath));
    }

    [Fact]
    public async Task Admitted_write_fault_exits_and_drains()
    {
        await using var scope = ProductionCheckpointScope.Create();
        // The directory is a valid checkpoint parent, but cannot be opened as an append file.
        await using var checkpoint = new PdfStageCheckpoint(scope.DirectoryPath, false, "test.pdf");

        await Assert.ThrowsAnyAsync<Exception>(() => checkpoint.RecordSpanBatchAsync(
            [("b1", 1, "l1", (IReadOnlyList<string>)["l1"], new TextOffsetSpan(0, 1))],
            null, CancellationToken.None));

        var drain = checkpoint.StopAcceptingWritesAndDrainAsync();
        var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(drain, completed);
        await drain;

        await checkpoint.DisposeAsync();
        await scope.DisposeAsync();
        Assert.False(Directory.Exists(scope.DirectoryPath));
    }
}
