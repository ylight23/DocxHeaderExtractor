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
        scope.DeferCleanup([lane.DetachedTask!]);
        releaseWriter.SetResult();
        await lane.DetachedTask!;
        Assert.Null(await writerFault.Task);
        Assert.True(File.Exists(scope.CheckpointPath));
        await scope.WaitForCleanupAsync();
        Assert.False(Directory.Exists(scope.DirectoryPath));
    }
}
