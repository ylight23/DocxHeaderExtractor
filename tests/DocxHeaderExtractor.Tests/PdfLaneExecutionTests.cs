using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLaneExecutionTests
{
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
