using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Tests;

/// <summary>How a quote the model wrote is bound to the source text. The cases are the errors seen in the monthly Qwen run (2026-11-03).</summary>
public sealed class GatherQuoteBindTests
{
    static GatherUnit Unit(string id, string text, string path = "", bool heading = false, int? headingEnd = null) =>
        new(id, id.Split('|')[0], id, "body", false, heading, text, path, "pipeline", true, null, null, false, null, null, null, null, id)
        { HeadingEnd = headingEnd };

    static ProposedFact Fact(params FactPart[] parts) => new(parts);

    static FactPart Quote(GatherUnit unit, string role, string quote) => new(unit.Id, 0, 0, role, quote);

    [Fact]
    public void A_label_that_is_the_heading_path_is_dropped_and_the_value_is_kept()
    {
        var unit = Unit("S|body[1]/p[20]", "Tiếp tục triển khai dự án Quỹ Pastb tại 3 xã.", "KẾ HOẠCH TRỌNG TÂM HOẠT ĐỘNG THÁNG 10/2026 > Quỹ Pastb:");

        var result = GatherCheck.Check([unit], [Fact(
            Quote(unit, "label", "KẾ HOẠCH TRỌNG TÂM HOẠT ĐỘNG THÁNG 10/2026 > Quỹ Pastb:"),
            Quote(unit, "value", "Tiếp tục triển khai dự án Quỹ Pastb tại 3 xã."))]);

        var fact = Assert.Single(result.Kept);
        Assert.Equal("Tiếp tục triển khai dự án Quỹ Pastb tại 3 xã.", fact.Render());
        Assert.Contains(fact.Flags, f => f.StartsWith("LABEL_FROM_HEADING_CONTEXT:"));
    }

    [Fact]
    public void A_label_that_is_one_heading_segment_without_its_colon_is_dropped()
    {
        var unit = Unit("S|body[1]/p[30]", "Đăng 4 bài trên trang Facebook.", "KẾ HOẠCH > TRUYỀN THÔNG:");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "label", "TRUYỀN THÔNG"), Quote(unit, "value", "Đăng 4 bài"))]);

        Assert.Equal("Đăng 4 bài", Assert.Single(result.Kept).Render());
    }

    [Fact]
    public void A_label_that_is_the_last_two_segments_of_the_heading_path_is_dropped()
    {
        var unit = Unit("T8|body[1]/p[87]", "Tiếp tục triển khai hệ thống pastb.vn tại 10 tỉnh.",
            "Các hoạt động chính đã triển khai > KẾ HOẠCH TRỌNG TÂM HOẠT ĐỘNG THÁNG 9/2026 > QUỸ PASTB:");

        var result = GatherCheck.Check([unit], [Fact(
            Quote(unit, "label", "KẾ HOẠCH TRỌNG TÂM HOẠT ĐỘNG THÁNG 9/2026 > QUỸ PASTB:"),
            Quote(unit, "value", "Tiếp tục triển khai hệ thống pastb.vn tại 10 tỉnh."))]);

        Assert.Equal("Tiếp tục triển khai hệ thống pastb.vn tại 10 tỉnh.", Assert.Single(result.Kept).Render());
    }

    [Fact]
    public void A_label_that_skips_a_segment_of_the_heading_path_is_not_heading_context()
    {
        var unit = Unit("S|body[1]/p[87]", "Đăng 4 bài.", "A > B > C");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "label", "A > C"), Quote(unit, "value", "Đăng 4 bài."))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_fact_that_is_only_heading_context_is_refused()
    {
        var unit = Unit("S|body[1]/p[30]", "Đăng 4 bài.", "TRUYỀN THÔNG:");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "label", "TRUYỀN THÔNG:"))]);

        Assert.Equal("ONLY_HEADING_LABEL", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_fact_that_is_only_a_label_is_refused()
    {
        var unit = Unit("S|body[1]/p[30]", "Kinh phí: 20 triệu đồng.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "label", "Kinh phí:"))]);

        Assert.Equal("LABEL_ONLY", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_label_that_is_not_heading_context_and_not_in_the_text_is_still_refused()
    {
        var unit = Unit("S|body[1]/p[30]", "Đăng 4 bài.", "TRUYỀN THÔNG:");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "label", "KẾT QUẢ"), Quote(unit, "value", "Đăng 4 bài"))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_quote_found_once_in_a_nearby_unit_is_moved_to_that_unit_and_noted()
    {
        var label = Unit("S|body[1]/p[38]", "CÔNG TÁC XÃ HỘI:");
        var value = Unit("S|body[1]/p[39]", "Thăm hỏi 12 hộ gia đình.");

        var result = GatherCheck.Check([label, value], [Fact(Quote(value, "label", "CÔNG TÁC XÃ HỘI:"), Quote(value, "value", "Thăm hỏi 12 hộ"))]);

        var fact = Assert.Single(result.Kept);
        Assert.Equal(label.Id, fact.Parts[0].UnitId);
        Assert.Contains("UNIT_ID_CORRECTED: S|body[1]/p[39] -> S|body[1]/p[38]", fact.Flags);
    }

    [Fact]
    public void A_quote_found_in_two_nearby_units_is_not_moved()
    {
        var a = Unit("S|body[1]/p[1]", "Đã hoàn thành.");
        var named = Unit("S|body[1]/p[2]", "Kế hoạch tháng sau.");
        var b = Unit("S|body[1]/p[3]", "Đã hoàn thành.");

        var result = GatherCheck.Check([a, named, b], [Fact(Quote(named, "text", "Đã hoàn thành."))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_quote_in_another_source_is_not_moved_there()
    {
        var named = Unit("A|body[1]/p[2]", "Kế hoạch tháng sau.");
        var other = Unit("B|body[1]/p[2]", "Đã hoàn thành.");

        var result = GatherCheck.Check([named, other], [Fact(Quote(named, "text", "Đã hoàn thành."))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_quote_that_differs_only_in_case_keeps_the_source_text()
    {
        var unit = Unit("S|body[1]/p[5]", "Tổ chức ngày hội Sống khỏe cho 200 người.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "text", "Sống Khỏe cho 200 người"))]);

        var fact = Assert.Single(result.Kept);
        Assert.Equal("Sống khỏe cho 200 người", fact.Render());
        Assert.Contains(fact.Flags, f => f.StartsWith("QUOTE_CASE_DIFFERS:"));
    }

    [Fact]
    public void A_quote_the_model_corrected_by_one_letter_keeps_the_source_text_with_its_typo()
    {
        var unit = Unit("S|body[1]/p[7]", "Mở rộng màng lưới tình nguyện viên tại 5 xã vùng cao.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "text", "Mở rộng mạng lưới tình nguyện viên"))]);

        var fact = Assert.Single(result.Kept);
        Assert.Equal("Mở rộng màng lưới tình nguyện viên", fact.Render());
        Assert.Contains("QUOTE_CORRECTED_BY_MODEL: model wrote \"Mở rộng mạng lưới tình nguyện viên\"", fact.Flags);
    }

    [Fact]
    public void A_near_quote_with_two_equally_close_places_is_refused()
    {
        var unit = Unit("S|body[1]/p[7]", "Hỗ trợ xã Tân An đợt một. Hỗ trợ xã Tân Ân đợt hai.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "text", "Hỗ trợ xã Tân Án"))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void A_quote_too_far_from_the_text_is_refused()
    {
        var unit = Unit("S|body[1]/p[7]", "Mở rộng màng lưới tình nguyện viên.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "text", "Phát triển mạng lưới cộng tác viên"))]);

        Assert.Equal("QUOTE_NOT_FOUND", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void An_exact_quote_found_twice_is_ambiguous()
    {
        var unit = Unit("S|body[1]/p[7]", "Đạt. Đạt.");

        var result = GatherCheck.Check([unit], [Fact(Quote(unit, "text", "Đạt."))]);

        Assert.Equal("QUOTE_AMBIGUOUS", Assert.Single(result.Refused).Reason);
    }

    static string Build(Action<MainDocumentPart> fill)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gather-gap-{Guid.NewGuid():N}.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text("Nội dung.")))));
        fill(main);
        return path;
    }

    static IReadOnlyList<string> GapsOf(string path)
    {
        try { return GatherUnits.Read(path, "S").Gaps.Select(g => g.Reason).ToList(); }
        finally { File.Delete(path); }
    }

    static Drawing ShapeDrawing(OpenXmlElement graphicData)
    {
        var graphic = new OpenXmlUnknownElement("a", "graphic", "http://schemas.openxmlformats.org/drawingml/2006/main");
        graphic.AppendChild(graphicData);
        var inline = new OpenXmlUnknownElement("wp", "inline", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing");
        inline.AppendChild(graphic);
        return new Drawing(inline);
    }

    [Fact]
    public void A_drawing_with_only_shapes_is_listed_but_not_unread()
    {
        var gaps = GapsOf(Build(main => main.Document.Body!.AppendChild(new Paragraph(new Run(ShapeDrawing(
            new OpenXmlUnknownElement("a", "graphicData", "http://schemas.openxmlformats.org/drawingml/2006/main")))))));

        Assert.Contains("drawing_without_content", gaps);
        Assert.DoesNotContain("drawing_or_image", gaps);
        Assert.False(GatherUnits.IsUnread("drawing_without_content"));
    }

    [Fact]
    public void A_drawing_with_a_picture_stays_unread()
    {
        var picture = new OpenXmlUnknownElement("a", "graphicData", "http://schemas.openxmlformats.org/drawingml/2006/main");
        picture.AppendChild(new OpenXmlUnknownElement("a", "blip", "http://schemas.openxmlformats.org/drawingml/2006/main"));
        var gaps = GapsOf(Build(main => main.Document.Body!.AppendChild(new Paragraph(new Run(ShapeDrawing(picture))))));

        Assert.Contains("drawing_or_image", gaps);
        Assert.True(GatherUnits.IsUnread("drawing_or_image"));
    }

    [Fact]
    public void A_footer_with_only_a_page_number_is_listed_but_not_unread_and_a_footer_with_text_stays_unread()
    {
        var number = GapsOf(Build(main => main.AddNewPart<FooterPart>().Footer = new Footer(new Paragraph(new Run(new Text("12"))))));
        var text = GapsOf(Build(main => main.AddNewPart<FooterPart>().Footer = new Footer(new Paragraph(new Run(new Text("Bí mật — trang 12"))))));

        Assert.Contains("footer_page_number_only", number);
        Assert.False(GatherUnits.IsUnread("footer_page_number_only"));
        Assert.Contains("footers", text);
    }
}
