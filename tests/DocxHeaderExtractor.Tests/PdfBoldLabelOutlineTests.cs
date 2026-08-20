using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class PdfBoldLabelOutlineTests
{
    [Fact]
    public void Fortis073DungBoldRunInLabelKhiDocxMatHetDinhDang()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop",
            "073_FORTIS_GC_Minutes_Mar_2026.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "05_bien_ban_hop",
            "073_FORTIS_GC_Minutes_Mar_2026.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfBoldLabelOutline.TryBuild(docx, slim, mode);

        Assert.Equal(7, result.Headings.Count);
        Assert.All(result.Headings, h => Assert.Equal("pdf_bold_label", h.ConfidenceBasis));
        Assert.All(result.Headings, h => Assert.Equal(1, h.Level));
        Assert.Contains(result.Headings, h => h.Text == "Opening:");
        Assert.Contains(result.Headings, h => h.Text == "Present:");
        Assert.Contains(result.Headings, h =>
            h.Text == "Report on Currently Available Resources in the F.O.R.T.I.S. Ukraine FIF.");
        Assert.Contains(result.Headings, h =>
            h.Text == "Funding Request for Eighth Additional Financing to Public Expenditures for " +
                      "Administrative Capacity Endurance (PEACE) in Ukraine Investment Project Financing.");
        Assert.Contains(result.Headings, h => h.Text == "Next Bi-Annual Meeting of the GC.");
        // Span phải khớp CHÍNH XÁC nguyên văn — đây là bất biến OutlineGroundingValidator của harness
        // đòi hỏi; lệch dù chỉ khoảng trắng cũng bị cách ly âm thầm ở lượt sau (đã đo được lỗi này).
        Assert.All(result.Headings, h =>
            Assert.Equal(h.Text, h.OriginalText![h.HeadingSpan!.Start..h.HeadingSpan.End]));
    }

    [Fact]
    public void Khong_kich_hoat_khi_mode_khong_phai_FormatDriven()
    {
        var slim = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs = [],
        };
        var mode = new DocumentModeReport(
            DocumentMode.TypedNumbering, 0, 0, 0, 0, 0, 0, false);

        var result = PdfBoldLabelOutline.TryBuild("x.docx", slim, mode);

        Assert.Empty(result.Headings);
        Assert.StartsWith("mode=", result.Reason);
    }

    [Fact]
    public void IcpTag079MoRongSessionTitleVaDungTaiDanhSachNguoiDu()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop",
            "079_ICP_TAG_Minutes_Apr_2024.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "05_bien_ban_hop",
            "079_ICP_TAG_Minutes_Apr_2024.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfBoldLabelOutline.TryBuild(docx, slim, mode);

        Assert.Equal(18, result.Headings.Count);
        Assert.Contains(result.Headings, h => h.Text == "Session I: ICP 2021 cycle results");
        Assert.Contains(result.Headings, h => h.Text == "ICP 2021 results and forecasts");
        Assert.Contains(result.Headings, h => h.Text == "Session II: Closing");
        Assert.Contains(result.Headings, h => h.Text == "Next steps, any other business, and closing remarks");
        Assert.Contains(result.Headings, h => h.Text == "Annex 2: List of Participants");
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("World Bank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("ICP experts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IcpBoard080KhongKeoRosterSauAnnexParticipants()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop",
            "080_ICP_Governing_Board_Minutes_Feb_2023.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "05_bien_ban_hop",
            "080_ICP_Governing_Board_Minutes_Feb_2023.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfBoldLabelOutline.TryBuild(docx, slim, mode);

        Assert.Equal(13, result.Headings.Count);
        Assert.Contains(result.Headings, h => h.Text == "Annex 1: Meeting Agenda");
        Assert.Contains(result.Headings, h => h.Text == "Annex 2: List of Participants");
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("Finland", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("African Development Bank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Headings, h => h.Text.Contains("World Bank", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fortis075KhongNuotOpeningSauDateVaBoBoldFragmentTrongTrichDan()
    {
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop",
            "075_FORTIS_GC_Minutes_Nov21_2024.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "05_bien_ban_hop",
            "075_FORTIS_GC_Minutes_Nov21_2024.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(docx);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);

        var result = PdfBoldLabelOutline.TryBuild(docx, slim, mode);

        Assert.Equal(12, result.Headings.Count);
        Assert.Contains(result.Headings, h => h.Text == "Date:");
        Assert.Contains(result.Headings, h => h.Text == "Opening:");
        Assert.Contains(result.Headings, h => h.Text == "Item 6: Appointment of the Chair.");
        Assert.DoesNotContain(result.Headings, h => h.Text == "Contributor Cancellation");
    }
}
