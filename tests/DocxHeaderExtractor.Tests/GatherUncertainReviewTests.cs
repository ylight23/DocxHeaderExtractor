using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Tests;

/// <summary>The model's classification of uncertain sections is checked against the items it was given and kept as advice only.</summary>
public sealed class GatherUncertainReviewTests
{
    sealed class Reply(string text) : IGatherModel
    {
        public string? User { get; private set; }
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { User = user; return Task.FromResult(text); }
    }

    sealed class Failing : IGatherModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => throw new HttpRequestException("down");
    }

    static GatherUncertain Item(params string[] candidates) => new("NEAR_NAME", "Tiếp nhận và hỗ trợ từ thiện", "T8 body[1]/p[39]", "Công tác xã hội:",
        candidates.Select((c, i) => new GatherCandidate(c, $"body[1]/p[{i + 1}]", c.ToLowerInvariant(), ["Tiếp đón 66 lượt."])).ToList(),
        [new GatheredFact([new SourcedPart("T8|body[1]/p[40]", "T8", "body[1]/p[40]", "text", "Tiếp đón 50 lượt.", 0, 17)], [])], "tên gần giống");

    [Fact]
    public async Task Verdicts_are_read_per_item_and_an_invalid_one_becomes_unsure()
    {
        var model = new Reply("""
            {"verdicts":[
              {"id":0,"decision":"MERGE","candidate":0,"reason":"Cùng là tiếp nhận từ thiện."},
              {"id":1,"decision":"MERGE","candidate":5,"reason":"?"},
              {"id":2,"decision":"NEW","reason":"Chủ đề khác."}]}
            """);

        var result = await GatherUncertainReview.ReviewAsync([Item("Công tác tiếp nhận và hỗ trợ từ thiện"), Item("A"), Item("B"), Item("C")], model);

        Assert.Null(result.Error);
        Assert.Equal(["MERGE", "UNSURE", "NEW", "UNSURE"], result.Verdicts.Select(v => v.Decision));
        Assert.Equal(0, result.Verdicts[0].Candidate);
        Assert.StartsWith("INVALID_CANDIDATE", result.Verdicts[1].Reason, StringComparison.Ordinal);
        Assert.Equal("MISSING_VERDICT", result.Verdicts[3].Reason);
        Assert.Contains("Tiếp đón 50 lượt.", model.User);
        Assert.Contains("Công tác tiếp nhận và hỗ trợ từ thiện", model.User);
    }

    [Fact]
    public async Task A_failed_call_or_unreadable_reply_leaves_every_item_unsure()
    {
        var failed = await GatherUncertainReview.ReviewAsync([Item("A")], new Failing());
        var garbled = await GatherUncertainReview.ReviewAsync([Item("A")], new Reply("không phải JSON"));

        Assert.Equal("CALL_FAILED:HttpRequestException", failed.Error);
        Assert.Equal("FORMAT_INVALID", garbled.Error);
        Assert.All(failed.Verdicts.Concat(garbled.Verdicts), v => Assert.Equal("UNSURE", v.Decision));
    }

    [Fact]
    public async Task No_uncertain_items_means_no_call()
    {
        var model = new Reply("{}");

        var result = await GatherUncertainReview.ReviewAsync([], model);

        Assert.Empty(result.Verdicts);
        Assert.Null(model.User);
    }
}
