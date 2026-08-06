using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class EvidenceConfidenceCalibratorTests
{
    [Fact]
    public void Structure_reaches_95_only_when_all_five_checks_pass()
    {
        var doc = Document();
        var headings = Headings(modelConfirmed: true);

        EvidenceConfidenceCalibrator.Apply(headings, doc);

        Assert.All(headings.Where(x => x.Source == HeadingSource.Structure), h =>
        {
            Assert.Equal(0.95, h.Confidence);
            Assert.False(h.Disputed);
            Assert.Equal("verified_by_multiple_checks", h.Evidence!.Status);
        });
    }

    [Fact]
    public void Missing_model_confirmation_stays_below_95_and_requires_review()
    {
        var headings = Headings(modelConfirmed: false);

        EvidenceConfidenceCalibrator.Apply(headings, Document());

        Assert.All(headings.Where(x => x.Source == HeadingSource.Structure), h =>
        {
            Assert.Equal(0.85, h.Confidence);
            Assert.True(h.Disputed);
            Assert.Equal("requires_review", h.Evidence!.Status);
        });
    }

    [Fact]
    public void Numbering_audit_conflict_blocks_verified_status()
    {
        var headings = Headings(modelConfirmed: true);

        EvidenceConfidenceCalibrator.Apply(headings, Document(), new HashSet<int> { 27 });

        Assert.Equal(0.85, headings.Single(x => x.Index == 27).Confidence);
        Assert.True(headings.Single(x => x.Index == 27).Disputed);
        Assert.False(headings.Single(x => x.Index == 27).Evidence!.TreeValid);
        Assert.Equal(0.95, headings.Single(x => x.Index == 30).Confidence);
    }

    /// <summary>
    /// Word đánh số qua danh sách nhiều cấp thì con số KHÔNG có trong text của run — nó chỉ tồn tại
    /// ở <see cref="SlimParagraph.NumberLabel"/> do NumberingResolver tính. Đọc trần text làm ba
    /// kiểm tra numbering/sibling/formatting cùng trượt, và đúng nhóm tài liệu đánh số bài bản nhất
    /// lại bị đẩy xuống "cần duyệt". Ở đây text hoàn toàn không có chữ số nào.
    /// </summary>
    [Fact]
    public void Danh_so_tu_dong_cua_Word_van_duoc_tinh_la_co_numbering()
    {
        var ps = new[]
        {
            Numbered(11, "1.", "Phạm vi điều chỉnh"),
            Numbered(13, "2.", "Đối tượng áp dụng"),
        };
        var doc = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = ps }.Build();
        List<HeadingRecord> headings =
        [
            new() { Index = 11, Level = 1, Text = "Phạm vi điều chỉnh", Source = HeadingSource.Structure, ModelConfirmed = true },
            new() { Index = 13, Level = 1, Text = "Đối tượng áp dụng", Source = HeadingSource.Structure, ModelConfirmed = true },
        ];

        EvidenceConfidenceCalibrator.Apply(headings, doc);

        Assert.All(headings, h =>
        {
            Assert.True(h.Evidence!.NumberingValid);
            Assert.True(h.Evidence.SiblingSequenceValid);
            Assert.Equal(0.95, h.Confidence);
        });
    }

    [Theory]
    [InlineData(3, 0.80)]
    [InlineData(4, 0.85)]
    [InlineData(5, 0.95)]
    public void Evidence_tiers_expose_80_85_and_reserve_95_for_all_checks(int passed, double expected)
    {
        Assert.Equal(expected, EvidenceConfidenceCalibrator.ConfidenceForChecks(passed));
    }

    private static SlimDocument Document()
    {
        var ps = new[]
        {
            P(25, "I. VÙNG TRỜI", true),
            P(27, "1. Mục Alpha", true),
            P(30, "2. Máy bay quân sự Ta", true),
        };
        return new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = ps }.Build();
    }

    private static List<HeadingRecord> Headings(bool modelConfirmed) =>
    [
        new() { Index = 25, Level = 1, Text = "I. VÙNG TRỜI", Source = HeadingSource.Model, ModelConfirmed = true },
        new() { Index = 27, Level = 2, Text = "1. Mục Alpha", Source = HeadingSource.Structure, ModelConfirmed = modelConfirmed },
        new() { Index = 30, Level = 2, Text = "2. Máy bay quân sự Ta", Source = HeadingSource.Structure, ModelConfirmed = modelConfirmed },
    ];

    private static SlimParagraph P(int index, string text, bool bold) => new()
    {
        Index = index, StableId = $"p[{index}]", Text = text, Bold = bold, FontSizePt = 13, Alignment = "left",
    };

    private static SlimParagraph Numbered(int index, string label, string text) => new()
    {
        Index = index, StableId = $"p[{index}]", Text = text, Bold = true, FontSizePt = 13, Alignment = "left",
        NumberingId = 4, NumberingLevel = 0, NumberLabel = label,
    };
}
