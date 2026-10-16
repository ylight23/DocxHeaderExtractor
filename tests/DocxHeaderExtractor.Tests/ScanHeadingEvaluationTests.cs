using DocxHeaderExtractor.HeadingAgent.Scan;
using G = DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingEvaluation.GoldHeading;
using R = DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingEvaluation.ResultHeading;

namespace DocxHeaderExtractor.Tests;

/// <summary>Scoring a scan result's headings against a gold of the document's headings.</summary>
public sealed class ScanHeadingEvaluationTests
{
    static readonly G[] Gold =
    [
        new(1, 1, null, "LUẬT AN TOÀN THÔNG TIN MẠNG", false),
        new(2, 1, "Chương I", "Chương I NHỮNG QUY ĐỊNH CHUNG", true),
        new(3, 1, "Điều 1", "Điều 1. Phạm vi điều chỉnh", true),
        new(4, 4, "Chương II", "Chương II BẢO ĐẢM AN TOÀN THÔNG TIN MẠNG", true),
        new(5, 4, "Mục 1", "Mục 1 BẢO VỆ THÔNG TIN MẠNG", true),
        new(6, 7, "Mục 2", "Mục 2 BẢO VỆ THÔNG TIN CÁ NHÂN", true),
        new(7, 9, "Điều 19", "Điều 19. Bảo đảm an toàn thông tin cá nhân trên mạng", true),
    ];

    [Fact]
    public void Labels_are_read_with_their_number_and_never_one_for_another()
    {
        Assert.Equal("chương ii", ScanHeadingEvaluation.LabelOf("Chương II BẢO ĐẢM"));
        Assert.Equal("chương i", ScanHeadingEvaluation.LabelOf("Chương I NHỮNG"));
        Assert.Equal("chương v", ScanHeadingEvaluation.LabelOf("ChươngV ĐIỀU KHOẢN"));
        Assert.Equal("điều 1", ScanHeadingEvaluation.LabelOf("Điều 1. Phạm vi"));
        Assert.NotEqual(ScanHeadingEvaluation.LabelOf("Điều 1. Phạm vi"), ScanHeadingEvaluation.LabelOf("Điều 19. Bảo đảm"));
        Assert.Null(ScanHeadingEvaluation.LabelOf("4. Ban Cơ yếu Chính phủ"));
    }

    [Fact]
    public void Recall_counts_required_headings_found_and_precision_counts_selections_that_are_headings()
    {
        R[] result =
        [
            new(1, 1, "Chương I NHỮNG QUY ĐỊNH CHUNG"),
            new(2, 1, "Điều 1. Phạm vi điều chinh"),          // found, text not exact
            new(3, 4, "Chương II"),                            // the label alone: the heading is found
            new(4, 4, "BẢO ĐẢM AN TOÀN THÔNG TIN MẠNG"),       // its title apart: a split part, not a false positive
            new(5, 4, "Mục 1 BẢO VỆ THÔNG TIN MẠNG"),
            new(6, 5, "Mục 1 BẢO VỆ THÔNG TIN MẠNG"),          // the same heading again: a duplicate
            new(7, 26, "4. Ban Cơ yếu Chính phủ giúp Bộ trưởng"), // a numbered clause: not a heading
        ];

        var r = ScanHeadingEvaluation.Score(Gold, result);

        Assert.Equal((6, 4), (r.Required, r.RequiredFound));
        Assert.Equal([6, 7], r.Missed.Select(m => m.N));
        Assert.Equal((7, 5, 1, 2), (r.Selected, r.SelectedMatched, r.SplitParts, r.FalsePositives));
        Assert.Equal(["duplicate", "extra"], r.Extra.Select(e => e.Kind));
        Assert.Equal(2, r.TextExact);   // Chương I and Mục 1; "chinh" and the label alone differ
        Assert.Equal(0, r.AcceptableFound);
    }

    [Fact]
    public void The_title_block_is_matched_by_its_letters_and_counts_as_acceptable()
    {
        var r = ScanHeadingEvaluation.Score(Gold, [new R(1, 1, "LUẬT AN TOÀN THÔNG TIN MANG")]);
        Assert.Equal((1, 0, 1.0), (r.AcceptableFound, r.FalsePositives, r.Precision));
    }

    [Fact]
    public void A_two_line_title_selected_by_its_first_line_is_found_with_its_text_not_exact()
    {
        G[] gold = [new(1, 43, null, "6. Đăng ký thay đổi thành viên trong trường hợp thành viên sử dụng phần vốn góp để trả nợ", false)];
        var first = ScanHeadingEvaluation.Score(gold, [new R(1, 43, "6. Đăng ký thay đổi thành viên trong trường hợp thành viên sử dụng")]);
        Assert.Equal((1, 0, 0), (first.AcceptableFound, first.FalsePositives, first.TextExact));
        var number = ScanHeadingEvaluation.Score(gold, [new R(1, 43, "6.")]);   // the number alone is not the heading
        Assert.Equal((0, 1), (number.AcceptableFound, number.FalsePositives));
    }

    [Fact]
    public void The_gold_of_022_has_every_chapter_and_article()
    {
        var gold = ScanHeadingEvaluation.ReadGold(TestRepository.Path("eval/scan-text/022_headings.v1.json"));
        Assert.Equal(Enumerable.Range(1, 101).Select(i => "điều " + i), gold.Where(g => g.Label?.StartsWith("Điều", StringComparison.Ordinal) == true).Select(g => ScanHeadingEvaluation.LabelOf(g.Label!)));
        Assert.Equal(9, gold.Count(g => g.Label?.StartsWith("Chương", StringComparison.Ordinal) == true));
        Assert.Equal(111, gold.Count(g => g.Required));   // as the user-approved a99 gold DOC-0202: title, Chương I-IX, Điều 1-101
        Assert.All(gold, g => Assert.True(g.Required));
        Assert.DoesNotContain(gold, g => g.Text.StartsWith("Chương VIII,", StringComparison.Ordinal));
    }

    [Fact]
    public void The_gold_of_011_has_every_chapter_section_and_article()
    {
        var gold = ScanHeadingEvaluation.ReadGold(TestRepository.Path("eval/scan-text/011_headings.v1.json"));
        Assert.Equal(54, gold.Count(g => g.Label?.StartsWith("Điều", StringComparison.Ordinal) == true));
        Assert.Equal(8, gold.Count(g => g.Label?.StartsWith("Chương", StringComparison.Ordinal) == true));
        Assert.Equal(6, gold.Count(g => g.Label?.StartsWith("Mục", StringComparison.Ordinal) == true));
        Assert.Equal(Enumerable.Range(1, 54).Select(i => "điều " + i), gold.Where(g => g.Label?.StartsWith("Điều", StringComparison.Ordinal) == true).Select(g => ScanHeadingEvaluation.LabelOf(g.Label!)));
    }

    [Theory]
    [InlineData("{\"Headings\":[{\"Index\":1,\"Parts\":[{\"Page\":4}],\"Text\":\"Chương II\"}]}")]
    [InlineData("{\"headings\":[{\"index\":1,\"scanSources\":[{\"Page\":4}],\"text\":\"Chương II\"}]}")]
    [InlineData("{\"headings\":[{\"index\":1,\"scanSources\":[{\"page\":4}],\"text\":\"Chương II\"}]}")]
    public void Legacy_diagnostic_and_production_results_have_the_same_heading_inventory(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var result = Assert.Single(ScanHeadingEvaluation.ReadResult(path));
            Assert.Equal(new R(1, 4, "Chương II"), result);
        }
        finally { File.Delete(path); }
    }
}
