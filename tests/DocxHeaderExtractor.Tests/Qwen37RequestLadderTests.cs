using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class Qwen37RequestLadderTests
{
    [Fact]
    public void Persisted_ladder_proves_privacy_filter_is_first_failure_without_gold()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DocxHeaderExtractor.sln")))
            root = root.Parent;
        Assert.NotNull(root);

        var path = Path.Combine(root!.FullName, "eval", "a99-closed-loop", "qwen37-flash-control", "request-ladder.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var json = document.RootElement;

        Assert.Equal("qwen/qwen3.7-flash", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal("P2A_PRIVACY_ZDR", json.GetProperty("firstFailingStage").GetString());
        Assert.Equal(404, json.GetProperty("firstFailureStatus").GetInt32());
        Assert.Contains("Zero data retention", json.GetProperty("firstFailureErrorBody").GetString());

        var documented = json.GetProperty("documentedWorkingRequest");
        Assert.False(documented.TryGetProperty("provider", out _));
        Assert.True(documented.GetProperty("reasoning").GetProperty("enabled").GetBoolean());

        var requests = json.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("P0_DOCUMENTED_MINIMAL", requests[0].GetProperty("stage").GetString());
        Assert.True(requests[0].GetProperty("isSuccess").GetBoolean());
        Assert.True(requests[1].GetProperty("isSuccess").GetBoolean());
        Assert.False(requests[2].GetProperty("isSuccess").GetBoolean());
    }
}
