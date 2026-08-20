using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Learning;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CorrectionMemoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dhx-memory-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var stem = Path.GetFileNameWithoutExtension(_path);
        var decisionsPath = Path.Combine(Path.GetDirectoryName(_path)!, $"{stem}.decisions.jsonl");
        if (File.Exists(decisionsPath)) File.Delete(decisionsPath);
    }

    [Fact]
    public async Task Saves_only_real_changes_and_deduplicates()
    {
        var memory = new CorrectionMemory(_path);
        var bundle = Bundle(
            new ReviewRow { StableId = "p1", Index = 1, Text = "2.3.1. Thành công", PredictedLevel = 2, CorrectedLevel = 3 },
            new ReviewRow { StableId = "p2", Index = 2, Text = "Nội dung", PredictedLevel = 0, CorrectedLevel = 0 });

        Assert.Equal(1, await memory.SaveChangedAsync(bundle));
        Assert.Equal(0, await memory.SaveChangedAsync(bundle));
        Assert.Equal(1, memory.Count);
        Assert.Single(File.ReadLines(_path));
    }

    [Fact]
    public async Task Retrieves_only_high_similarity_same_numbering_shape_as_advisory_xml()
    {
        var memory = new CorrectionMemory(_path);
        await memory.SaveChangedAsync(Bundle(new ReviewRow
        {
            StableId = "p1", Index = 1, Text = "2.3.1. Thành công và kết quả", PredictedLevel = 2, CorrectedLevel = 3,
        }));

        var similar = memory.FindExamples("<doc><p i=\"9\">4.2.1. Thành công và kết quả</p></doc>");
        var unrelated = memory.FindExamples("<doc><p i=\"9\">4.2.1. Phương pháp nghiên cứu định lượng</p></doc>");

        Assert.Single(similar);
        Assert.Empty(unrelated);
        var injected = CorrectionMemory.InjectExamples("<doc><p i=\"9\">x</p></doc>", similar);
        Assert.Contains("verified_examples", injected);
        Assert.Contains("advisory=\"1\"", injected);
    }

    [Fact]
    public async Task Exact_same_file_stable_id_and_text_overrides_model_without_generalizing()
    {
        var memory = new CorrectionMemory(_path);
        await memory.SaveChangedAsync(Bundle(new ReviewRow
        {
            StableId = "body[1]/p[1]", Index = 11, Text = "Đơn vị Alpha kính gửi: Đơn vị Beta",
            PredictedLevel = 1, CorrectedLevel = 0,
        }));
        var paragraph = new SlimParagraph
        {
            Index = 11, StableId = "body[1]/p[1]", Text = "Đơn vị Alpha kính gửi: Đơn vị Beta",
        };
        var doc = new SlimDocument
        {
            FileName = "test.docx", SourcePath = "test.docx", Paragraphs = [paragraph],
        }.Build();
        var headings = new List<HeadingRecord>
        {
            new() { Index = 11, Level = 1, Text = paragraph.Text, Source = HeadingSource.Model },
        };

        Assert.Equal(1, memory.ApplyExact("test.docx", doc, headings));
        Assert.Empty(headings);

        headings.Add(new HeadingRecord { Index = 11, Level = 1, Text = paragraph.Text, Source = HeadingSource.Model });
        Assert.Equal(0, memory.ApplyExact("other.docx", doc, headings));
        Assert.Single(headings);
    }

    [Fact]
    public async Task AppendDecision_persists_across_reload_and_deduplicates_same_content()
    {
        var memory = new CorrectionMemory(_path);
        var ctx = new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "none", ["pageGap"] = "1" };

        var first = await memory.AppendDecisionAsync(
            "continuation-merge", ctx, ["Portfolio at a Glance (tr.5)", "Portfolio at a Glance (tr.6)"],
            "merge", "user");
        var duplicate = await memory.AppendDecisionAsync(
            "continuation-merge", ctx, ["Portfolio at a Glance (tr.5)", "Portfolio at a Glance (tr.6)"],
            "merge", "user");

        Assert.NotNull(first);
        Assert.Null(duplicate); // trùng nội dung + verdict -> cùng Id, không ghi thêm dòng
        Assert.Single(File.ReadLines(memory.DecisionsPathOnDisk));

        var reloaded = new CorrectionMemory(_path);
        Assert.Single(reloaded.ActiveDecisions);
        Assert.Equal("merge", reloaded.ActiveDecisions[0].Verdict);
    }

    [Fact]
    public async Task FindDecisionExamples_ranks_by_context_overlap_within_same_decision_type()
    {
        var memory = new CorrectionMemory(_path);
        await memory.AppendDecisionAsync(
            "continuation-merge",
            new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "contd", ["sameStyle"] = "true" },
            ["Cash Contributions", "Cash Contributions (cont'd)"], "merge", "user");
        await memory.AppendDecisionAsync(
            "continuation-merge",
            new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "none", ["sameStyle"] = "false" },
            ["Key Trust Fund Activity (tr.8)", "Key Trust Fund Activity (tr.9)"], "keep-separate", "user");
        await memory.AppendDecisionAsync(
            "heading-role", // loại quyết định khác — không được trộn vào kết quả continuation-merge
            new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "contd" },
            ["irrelevant"], "heading", "user");

        var query = new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "contd", ["sameStyle"] = "true" };
        var examples = memory.FindDecisionExamples("continuation-merge", query, limit: 2);

        // Cả hai ca "continuation-merge" đều overlap > 0 (chung mode=financial), nhưng ca khớp cả
        // marker lẫn sameStyle phải xếp trước — đó là điểm của "xếp theo overlap", không phải lọc nhị phân.
        Assert.Equal(2, examples.Count);
        Assert.Equal("merge", examples[0].Verdict);
        Assert.Equal("keep-separate", examples[1].Verdict);
    }

    [Fact]
    public async Task RevokeDecision_appends_event_without_rewriting_and_excludes_from_retrieval()
    {
        var memory = new CorrectionMemory(_path);
        var ctx = new Dictionary<string, string> { ["mode"] = "financial", ["marker"] = "contd" };
        var created = await memory.AppendDecisionAsync(
            "continuation-merge", ctx, ["A", "A (cont'd)"], "merge", "user");
        Assert.NotNull(created);

        var revoked = await memory.RevokeDecisionAsync(created!.Id, "user");
        Assert.NotNull(revoked);
        Assert.Equal("revoked", revoked!.Status);
        Assert.Equal(2, File.ReadLines(memory.DecisionsPathOnDisk).Count()); // append-only: 2 dòng, không sửa dòng cũ
        Assert.Empty(memory.FindDecisionExamples("continuation-merge", ctx));

        var again = await memory.RevokeDecisionAsync(created.Id, "user");
        Assert.Null(again); // đã revoked rồi, không ghi thêm event trùng
    }

    private static ReviewBundle Bundle(params ReviewRow[] rows) => new()
    {
        SourceFile = "test.docx",
        Rows = rows,
    };
}
