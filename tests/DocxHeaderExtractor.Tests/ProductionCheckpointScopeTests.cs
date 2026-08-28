using DocxHeaderExtractor.Core.Pipeline;

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
}
