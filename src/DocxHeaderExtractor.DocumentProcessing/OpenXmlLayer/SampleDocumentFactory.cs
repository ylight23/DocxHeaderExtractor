using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>
/// Sinh .docx mẫu để thử nhanh và để kiểm thử: có heading theo style chuẩn,
/// heading "giả" chỉ định dạng thủ công (đậm/hoa/canh giữa), bảng, và đoạn thân bài.
/// </summary>
public static class SampleDocumentFactory
{
    public static void Create(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        AddStyles(main);

        Heading(body, "Heading1", "Chương 1. Tổng quan hệ thống");
        Normal(body, "Tài liệu này mô tả kiến trúc tổng thể của hệ thống trích xuất cấu trúc văn bản, " +
                     "bao gồm tầng đọc OpenXML, tầng lọc heuristic và tầng suy luận bằng mô hình ngôn ngữ chạy trên CPU.");

        Heading(body, "Heading2", "1.1. Phạm vi");
        Normal(body, "Phạm vi bao gồm các định dạng .docx và .doc. Định dạng .doc nhị phân được chuyển đổi trước khi xử lý.");
        Normal(body, "Ngoài ra, tài liệu đề cập tới cách đo đạc hiệu năng khi chạy suy luận trên máy không có GPU.");

        Heading(body, "Heading2", "1.2. Thuật ngữ");
        Normal(body, "OOXML là định dạng mở của Microsoft Office. GGUF là định dạng lưu trữ mô hình đã lượng tử hoá.");

        // Heading "giả": không dùng style Heading, chỉ in đậm + canh giữa + chữ hoa.
        Fake(body, "PHỤ LỤC A – BẢNG ĐỐI CHIẾU", bold: true, caps: true, center: true, sizePt: 14);
        Normal(body, "Bảng dưới đây đối chiếu tên style trong Word với cấp tiêu đề tương ứng trong kết quả đầu ra.");

        AddTable(body);

        Fake(body, "2.1 Kết quả thử nghiệm", bold: true, caps: false, center: false, sizePt: 13);
        Normal(body, "Trên tập 50 văn bản hành chính, bộ lọc heuristic giữ lại trung bình 6% số đoạn làm ứng viên, " +
                     "giúp giảm khoảng 90% số token phải nạp vào mô hình so với việc gửi toàn bộ nội dung.");

        Heading(body, "Heading1", "Chương 2. Kết luận");
        Normal(body, "Kết hợp OpenXML và mô hình 3B lượng tử hoá cho kết quả ổn định với chi phí phần cứng thấp.");

        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(new FontSize { Val = "22" }))));   // 11pt

        for (int lvl = 1; lvl <= 3; lvl++)
        {
            styles.Append(new Style(
                new StyleName { Val = $"heading {lvl}" },
                new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(
                    new OutlineLevel { Val = lvl - 1 },
                    new KeepNext()),
                new StyleRunProperties(
                    new Bold(),
                    new FontSize { Val = ((16 - lvl) * 2).ToString() }))
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{lvl}",
            });
        }

        styles.Append(new Style(new StyleName { Val = "Normal" })
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true,
        });

        part.Styles = styles;
        part.Styles.Save();
    }

    private static void Heading(Body body, string styleId, string text) =>
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

    private static void Normal(Body body, string text) =>
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

    private static void Fake(Body body, string text, bool bold, bool caps, bool center, double sizePt)
    {
        var rPr = new RunProperties();
        if (bold) rPr.Append(new Bold());
        if (caps) rPr.Append(new Caps());
        rPr.Append(new FontSize { Val = ((int)(sizePt * 2)).ToString() });

        var pPr = new ParagraphProperties(new ParagraphStyleId { Val = "Normal" });
        if (center) pPr.Append(new Justification { Val = JustificationValues.Center });

        body.AppendChild(new Paragraph(pPr,
            new Run(rPr, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AddTable(Body body)
    {
        var table = new Table(
            new TableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        string[][] rows =
        [
            ["Style Word", "Cấp"],
            ["Heading 1", "1"],
            ["Heading 2", "2"],
        ];

        foreach (var row in rows)
        {
            var tr = new TableRow();
            foreach (var cell in row)
                tr.Append(new TableCell(new Paragraph(new Run(new Text(cell)))));
            table.Append(tr);
        }

        body.AppendChild(table);
    }
}
