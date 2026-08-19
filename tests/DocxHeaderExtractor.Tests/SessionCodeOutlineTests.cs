using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class SessionCodeOutlineTests
{
    [Fact]
    public void Nhan_ma_phien_va_cat_ranh_gioi_bang_cum_nguoi_trinh_bay()
    {
        var doc = new SlimDocument
        {
            FileName = "icp.docx",
            SourcePath = "icp.docx",
            Paragraphs =
            [
                P(0, "D1.00 - Introduction of the Development Economics Data Group Survey Unit " +
                     "Talip Kilic, World Bank, informed participants that the ICP Global Office " +
                     "is now part of the newly established Survey Unit."),
                P(1, "D1.01 - Global updates Marko Rissanen, World Bank, presented global " +
                     "updates on the implementation of the ICP 2024 cycle."),
                P(2, "D2.02 - Reference PPP Mapping Giovanni Tonutti, World Bank, presented an " +
                     "overview of the ICP approach to estimating reference PPPs."),
            ],
        }.Build();
        var mode = new DocumentModeReport(
            DocumentMode.FormatDriven, 3, 0, 0, 0, 0, 0, false);

        var headings = SessionCodeOutline.Build(doc, mode);

        Assert.Equal(3, headings.Count);
        Assert.All(headings, h => Assert.Equal("session_code_marker", h.ConfidenceBasis));
        Assert.Contains(headings, h =>
            h.Index == 0 &&
            h.Text == "D1.00 - Introduction of the Development Economics Data Group Survey Unit");
        Assert.Contains(headings, h =>
            h.Index == 1 && h.Text == "D1.01 - Global updates");
        Assert.Contains(headings, h =>
            h.Index == 2 && h.Text == "D2.02 - Reference PPP Mapping");
    }

    [Fact]
    public void Khong_kich_hoat_duoi_nguong_toi_thieu_hoac_sai_mode()
    {
        var doc = new SlimDocument
        {
            FileName = "icp.docx",
            SourcePath = "icp.docx",
            Paragraphs =
            [
                P(0, "D1.00 - Introduction Talip Kilic, World Bank, informed participants."),
                P(1, "D1.01 - Global updates Marko Rissanen, World Bank, presented global updates."),
            ],
        }.Build();
        var formatDriven = new DocumentModeReport(
            DocumentMode.FormatDriven, 2, 0, 0, 0, 0, 0, false);
        var typedNumbering = new DocumentModeReport(
            DocumentMode.TypedNumbering, 2, 0, 0, 0, 0, 0, false);

        Assert.Empty(SessionCodeOutline.Build(doc, formatDriven)); // chỉ 2 mã, dưới ngưỡng 3
        Assert.Empty(SessionCodeOutline.Build(doc, typedNumbering)); // sai mode
    }

    private static SlimParagraph P(int index, string text) => new()
    {
        Index = index,
        StableId = $"body[1]/p[{index + 1}]",
        Text = text,
    };
}
