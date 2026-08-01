using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Learning;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CorrectionMemoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dhx-memory-{Guid.NewGuid():N}.jsonl");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

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

    private static ReviewBundle Bundle(params ReviewRow[] rows) => new()
    {
        SourceFile = "test.docx",
        Rows = rows,
    };
}
