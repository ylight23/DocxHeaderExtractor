using System.Net;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Tests;

public sealed class GatherOpenRouterTests
{
    static GatherSource Source()
    {
        var path = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Đoạn một</w:t></w:r></w:p><w:p><w:r><w:t>Đoạn hai</w:t></w:r></w:p>");
        try { return GatherUnits.Read(path, "src"); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Length_finish_splits_and_records_usage_and_cost_for_all_attempts()
    {
        using var handler = new FakeOpenRouter((_, n) => (HttpStatusCode.OK,
            n == 1 ? FakeOpenRouter.Stream(new { content = "{\"facts\":" }, "length")
                   : FakeOpenRouter.Stream(new { content = "{\"facts\":[]}" }, "stop")));
        using var model = new GatherOpenRouterModel("test-key", 10, 2, handler: handler);
        var result = await GatherRunner.RunAsync("Thông tin", [Source()], model);
        Assert.True(result.Complete);
        Assert.Equal(3, handler.Posts);
        Assert.Equal(3, model.Attempts.Count);
        Assert.All(model.Attempts, a => { Assert.Equal(1000, a.PromptTokens); Assert.Equal(200, a.CompletionTokens); Assert.Equal(150, a.ReasoningTokens); Assert.Equal(0.001, a.ProviderCostUsd); });
        Assert.Equal("AUDIT:FINISH_LENGTH", model.Attempts[0].Outcome);
    }

    [Fact]
    public async Task Budget_refusal_does_not_restart_the_call()
    {
        using var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{\"facts\":[]}" }, "stop")));
        using var model = new GatherOpenRouterModel("test-key", 0, 2, handler: handler);
        var result = await GatherRunner.RunAsync("Thông tin", [Source()], model);
        Assert.False(result.Complete);
        Assert.Equal(0, handler.Posts);
        Assert.Contains("BUDGET:CALL_LIMIT", Assert.Single(result.BatchFailures));
    }

    [Fact]
    public async Task Diagnostics_survive_the_call_without_writing_key_or_source_text()
    {
        var log = Path.Combine(Path.GetTempPath(), "dhx-gather-log-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{\"facts\":[]}" }, "stop")));
            using var model = new GatherOpenRouterModel("test-private-key", 5, 2, handler: handler, diagnosticsPath: log);
            await GatherRunner.RunAsync("Thông tin", [Source()], model);
            var text = File.ReadAllText(log);
            Assert.Contains("call_start", text);
            Assert.Contains("call_end", text);
            Assert.Contains("PromptTokens", text);
            Assert.Contains("0.001", text);
            Assert.DoesNotContain("test-private-key", text);
            Assert.DoesNotContain("Đoạn một", text);
        }
        finally { File.Delete(log); }
    }
}
