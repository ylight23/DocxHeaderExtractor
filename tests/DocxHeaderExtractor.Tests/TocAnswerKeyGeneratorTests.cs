using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// <see cref="TocAnswerKeyGenerator"/> phải khớp mục lục với TOÀN BỘ thân bài — không chỉ những
/// đoạn pipeline heuristic đã xếp là ứng viên — vì mục đích của nó là kiểm chứng ĐỘC LẬP với chính
/// pipeline đang bị nghi ngờ (TODO.md §46.5: "không suy về ĐẦU VÀO từ ĐẦU RA của pipeline đang nghi ngờ").
/// </summary>
public sealed class TocAnswerKeyGeneratorTests : IDisposable
{
    private readonly List<string> _files = [];

    private string Write(params Paragraph[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-tocgen-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs));
        main.Document.Save();
        return path;
    }

    /// <summary>Như <see cref="Write"/> nhưng kèm numbering.xml hai cấp, để test NumberLabel.</summary>
    private string WriteWithNumbering(params Paragraph[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-tocgen-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs));

        var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = new Numbering(
            new AbstractNum(
                new Level { LevelIndex = 0, NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal }, LevelText = new LevelText { Val = "%1." } },
                new Level { LevelIndex = 1, NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal }, LevelText = new LevelText { Val = "%1.%2." } })
            { AbstractNumberId = 7 },
            new NumberingInstance(new AbstractNumId { Val = 7 }) { NumberID = 1 });
        numberingPart.Numbering.Save();
        main.Document.Save();
        return path;
    }

    /// <summary>Dòng mục lục mang numPr (numbering do Word VẼ, không có số nào trong TEXT) — mô
    /// phỏng đúng ca đo được trên tài liệu thật: TOC của heading numPr-driven không có ký tự số.</summary>
    private static Paragraph TocEntryNumbered(string text, int ilvl) => new(
        new ParagraphProperties(
            new NumberingProperties(new NumberingLevelReference { Val = ilvl }, new NumberingId { Val = 1 })),
        new Hyperlink(new Run(new Text(text))) { Anchor = "_Toc" + Math.Abs(text.GetHashCode()) });

    public void Dispose()
    {
        foreach (var f in _files) if (File.Exists(f)) File.Delete(f);
    }

    private static Paragraph Plain(string text) => new(new Run(new Text(text)));

    private static Paragraph TocEntry(string text) => new(
        new Hyperlink(new Run(new Text(text))) { Anchor = "_Toc" + Math.Abs(text.GetHashCode()) });

    /// <summary>Năm mục lục, đủ khớp cả năm với thân bài -> phải Accept, đúng cấp từ độ sâu số.</summary>
    [Fact]
    public void Nam_muc_khop_du_100_phan_tram_thi_accept_va_gan_dung_cap()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("1.2. Mục tiêu nghiên cứu"),
            Plain("Mục tiêu được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            Plain("2.1. Phương pháp thực hiện"),
            Plain("Phương pháp được mô tả chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc);

        Assert.Equal(TocKeyStatus.Accepted, result.Status);
        Assert.Equal(5, result.MatchedCount);
        Assert.Equal(1.0, result.MatchRatio, precision: 3);

        var byText = result.Matches.ToDictionary(m => m.BodyText, m => m.Level);
        Assert.Equal(1, byText["Chương 1. Mở đầu"]);
        Assert.Equal(2, byText["1.1. Lý do chọn đề tài"]);
        Assert.Equal(2, byText["1.2. Mục tiêu nghiên cứu"]);
        Assert.Equal(1, byText["Chương 2. Nội dung"]);
        Assert.Equal(2, byText["2.1. Phương pháp thực hiện"]);
    }

    [Fact]
    public void Duoi_nam_muc_luc_thi_khong_du_tin_gan_insufficient()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc);

        Assert.Equal(TocKeyStatus.InsufficientTocEntries, result.Status);
        Assert.Empty(result.Matches);
    }

    /// <summary>Tác giả sửa tiêu đề mà không refresh mục lục — TOC lỗi thời, không tìm thấy trong thân bài.</summary>
    [Fact]
    public void Muc_luc_loi_thoi_khong_tim_thay_trong_than_bai_thi_duoi_nguong()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            // Tiêu đề thân bài đã bị tác giả sửa, mục lục vẫn ghi bản cũ "Mục tiêu nghiên cứu".
            Plain("1.2. Mục tiêu và phạm vi nghiên cứu (bản sửa)"),
            Plain("Mục tiêu được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            Plain("2.1. Phương pháp thực hiện"),
            Plain("Phương pháp được mô tả chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc, matchThreshold: 0.90);

        Assert.Equal(TocKeyStatus.BelowMatchThreshold, result.Status);
        Assert.Equal(4, result.MatchedCount); // 4/5 = 0.80, dưới ngưỡng 0.90 dùng ở đây
    }

    /// <summary>Cùng một tiêu đề lặp lại nhiều nơi trong thân bài (tài liệu mẫu/hợp đồng khung) —
    /// không rõ đoạn nào là đúng, phải BỎ chứ không đoán đại một đoạn.</summary>
    [Fact]
    public void Tieu_de_lap_lai_nhieu_lan_trong_than_bai_thi_bo_khong_doan()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("Phạm vi áp dụng\t9"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("1.2. Mục tiêu nghiên cứu"),
            Plain("Mục tiêu được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            // "Phạm vi áp dụng" xuất hiện lặp lại y hệt ở hai chỗ khác nhau trong thân bài.
            Plain("Phạm vi áp dụng"),
            Plain("Phần này áp dụng cho nhóm đối tượng thứ nhất được mô tả trong tài liệu này."),
            Plain("Phạm vi áp dụng"),
            Plain("Phần này áp dụng cho nhóm đối tượng thứ hai được mô tả trong tài liệu này."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc, matchThreshold: 0.5);

        Assert.DoesNotContain(result.Matches, m => m.BodyText == "Phạm vi áp dụng");
        Assert.Equal(1, result.AmbiguousBodyMatchCount);
        Assert.Equal(4, result.MatchedCount); // 5 mục dùng được - 1 mơ hồ = 4 khớp
    }

    /// <summary>
    /// Điểm mấu chốt: khớp với THÂN BÀI trực tiếp, không lọc qua Role/IsCandidate của pipeline
    /// heuristic — nếu không thì công cụ này chỉ đo lại chính cái nó cần kiểm chứng độc lập.
    /// </summary>
    [Fact]
    public void Khop_voi_doan_khong_duoc_pipeline_xep_la_ung_vien_van_duoc_tinh()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            // Toàn bộ đoạn thân bên dưới KHÔNG có style, không đậm, không hoa — pipeline heuristic
            // hiện tại rất có thể không xếp chúng là ứng viên, nhưng công cụ này vẫn phải khớp được.
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("1.2. Mục tiêu nghiên cứu"),
            Plain("Mục tiêu được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            Plain("2.1. Phương pháp thực hiện"),
            Plain("Phương pháp được mô tả chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var matchedParagraphs = doc.Paragraphs.Where(p => p.Text == "Chương 1. Mở đầu").ToList();
        Assert.Single(matchedParagraphs);
        // Bằng chứng độc lập: đoạn này không nhất thiết được heuristic xếp candidate, nhưng vẫn khớp.

        var result = TocAnswerKeyGenerator.Generate(doc);
        Assert.Equal(TocKeyStatus.Accepted, result.Status);
        Assert.Contains(result.Matches, m => m.BodyText == "Chương 1. Mở đầu" && m.StableId == matchedParagraphs[0].StableId);
    }

    [Fact]
    public void ToAnswerKeyText_sinh_dung_dinh_dang_stable_id_va_danh_dau_toc_derived()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("1.2. Mục tiêu nghiên cứu"),
            Plain("Mục tiêu được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            Plain("2.1. Phương pháp thực hiện"),
            Plain("Phương pháp được mô tả chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc) with { FileName = "vidu.docx" };
        var text = result.ToAnswerKeyText();

        Assert.Contains("toc_derived", text);
        Assert.Contains("vidu.docx", text);
        Assert.Contains("KHÔNG phải người kiểm", text);
        // AnswerKey.Parse phải đọc lại được chính văn bản mình vừa sinh ra (round-trip).
        var parsed = DocxHeaderExtractor.Core.Eval.AnswerKey.Parse(text);
        Assert.True(parsed.HasStableIds);
        Assert.Equal(5, parsed.StableIds.Count);
    }

    [Fact]
    public void ToAnswerKeyText_partial_danh_dau_partial_toc_va_van_parse_duoc()
    {
        var path = Write(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntry("1.1. Lý do chọn đề tài\t2"),
            TocEntry("1.2. Mục tiêu nghiên cứu\t3"),
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            Plain("Chương 1. Mở đầu"),
            Plain("1.1. Lý do chọn đề tài"),
            Plain("1.2. Mục tiêu nghiên cứu"),
            Plain("Chương 2. Nội dung"),
            Plain("2.1. Phương pháp thực hiện"));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc) with { FileName = "partial.docx" };
        var text = result.ToAnswerKeyText(partial: true);

        Assert.Contains("partial_toc", text);
        Assert.Contains("KHÔNG phải outline đầy đủ", text);
        var parsed = DocxHeaderExtractor.Core.Eval.AnswerKey.Parse(text);
        Assert.True(parsed.HasStableIds);
        Assert.Equal(5, parsed.StableIds.Count);
    }

    /// <summary>
    /// ĐO ĐƯỢC trên tài liệu thật (báo cáo thực tập MBBank): mục lục của heading numPr-driven
    /// (numbering do Word vẽ, không gõ tay) không có ký tự số nào trong TEXT — "Giới thiệu chung về
    /// Ngân hàng..." chứ không phải "1.1 Giới thiệu chung...". TableOfContentsAnchor.DepthOf chỉ đọc
    /// TEXT nên trả cấp 1 cho toàn bộ 14 mục loại này (sai). NumberLabel đã resolve từ numbering.xml
    /// ("1.1.1") vẫn đúng cấp dù TEXT không có số — phải ưu tiên nó trước khi rơi về DepthOf(text).
    /// </summary>
    [Fact]
    public void Muc_luc_cua_heading_numPr_driven_khong_co_so_trong_text_van_suy_dung_cap_tu_NumberLabel()
    {
        var path = WriteWithNumbering(
            Plain("MỤC LỤC"),
            TocEntry("Chương 1. Mở đầu\t1"),
            TocEntryNumbered("Giới thiệu chung", 1),           // "1.1." nhưng TEXT không có số
            TocEntryNumbered("Lý do chọn đề tài", 1),           // "1.2." nhưng TEXT không có số
            TocEntry("Chương 2. Nội dung\t5"),
            TocEntry("2.1. Phương pháp thực hiện\t8"),
            Plain("Chương 1. Mở đầu"),
            Plain("Chương này trình bày bối cảnh nghiên cứu và các mục tiêu chính của đề tài."),
            Plain("Giới thiệu chung"),
            Plain("Phần giới thiệu được trình bày chi tiết trong mục này của tài liệu nghiên cứu."),
            Plain("Lý do chọn đề tài"),
            Plain("Lý do được trình bày chi tiết trong phần này của tài liệu nghiên cứu."),
            Plain("Chương 2. Nội dung"),
            Plain("Nội dung chính của chương được trình bày đầy đủ trong các mục con dưới đây."),
            Plain("2.1. Phương pháp thực hiện"),
            Plain("Phương pháp được mô tả chi tiết trong phần này của tài liệu nghiên cứu."));

        var doc = new AuthorityEvaluationSourceReader(new ExtractionOptions()).Read(path).Document;
        var result = TocAnswerKeyGenerator.Generate(doc);

        Assert.Equal(TocKeyStatus.Accepted, result.Status);
        var byText = result.Matches.ToDictionary(m => m.BodyText, m => m.Level);
        Assert.Equal(1, byText["Chương 1. Mở đầu"]);
        Assert.Equal(2, byText["Giới thiệu chung"]);   // KHÔNG phải 1 — đây là điểm bản vá này sửa
        Assert.Equal(2, byText["Lý do chọn đề tài"]);
        Assert.Equal(1, byText["Chương 2. Nội dung"]);
        Assert.Equal(2, byText["2.1. Phương pháp thực hiện"]);
    }
}
