using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// TODO mục 5: <c>OutlineWriteback</c> từng từ chối bằng <c>inline_body_not_splittable</c> mỗi khi
/// heading chỉ chiếm một phần paragraph. Ánh xạ offset → (run, offset thô) trong
/// <see cref="SlimSourceSegment"/> mở khoá được ca đó — nhưng chỉ ở phạm vi HẸP và fail-closed.
/// <para>
/// Nội dung mẫu là văn bản trung tính; test khoá HÀNH VI TÁCH, không khoá chữ nghĩa (§7.6).
/// </para>
/// </summary>
public sealed class InlineBodyWritebackTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-inline-{Guid.NewGuid():N}")).FullName;

    private const string HeadingText = "Điều 1. Phạm vi áp dụng";
    private const string BodyText = "Quy trình này áp dụng cho toàn bộ đơn vị trực thuộc kể từ ngày ký.";
    private const string SecondHeading = "Điều 2. Trách nhiệm thi hành";

    private string Target => Path.Combine(_dir, "dich.docx");

    /// <summary>Đoạn dính: tiêu đề và thân bài nằm ở HAI run riêng, nên ranh giới rơi đúng đầu run.</summary>
    private string WriteSource(bool separateRuns)
    {
        var path = Path.Combine(_dir, separateRuns ? "tach-duoc.docx" : "tach-khong-duoc.docx");
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();

        var joined = separateRuns
            ? new Paragraph(
                new Run(new Text(HeadingText + " ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new Text(BodyText)))
            // Ba dấu cách bị chuẩn hoá gộp thành một, nên có một segment MỚI bắt đầu đúng ở ranh
            // giới nhưng với RawStart != 0 — ca mà chốt "đầu run" sinh ra để chặn.
            : new Paragraph(new Run(new Text(HeadingText + "   " + BodyText)
                { Space = SpaceProcessingModeValues.Preserve }));

        main.Document = new Document(new Body(
            joined,
            new Paragraph(new Run(new Text("Đoạn thân bài kế tiếp, đủ dài để không bị coi là ứng viên tiêu đề nào cả."))),
            // Đề mục thứ hai đứng SAU chỗ tách: nếu Verify không dịch chỉ số, nó sẽ đi soi nhầm đoạn.
            new Paragraph(new Run(new Text(SecondHeading))),
            new Paragraph(new Run(new Text("Đoạn thân bài cuối, cũng đủ dài để không bị nhận là tiêu đề.")))));
        main.Document.Save();
        return path;
    }

    private static HeadingRecord Split(SlimParagraph p, int level) => new()
    {
        Index = p.Index,
        StableId = p.StableId,
        Level = level,
        Text = HeadingText,
        OriginalText = p.Text,
        InlineBody = BodyText,
        InlineBodySpan = new TextOffsetSpan(HeadingText.Length + 1, p.Text.Length),
        Source = HeadingSource.Model,
        Confidence = 0.9,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
    };

    private static DocumentOutline Outline(SlimDocument slim, params HeadingRecord[] headings) => new()
    {
        File = slim.FileName,
        ParagraphCount = slim.Paragraphs.Count,
        CandidateCount = headings.Length,
        Headings = headings,
    };

    /// <summary>
    /// Round-trip mở/ghi/đọc lại — tiêu chí nghiệm thu TODO đặt ra. Đoạn dính tách làm hai: phần
    /// tiêu đề giữ vị trí và mang <c>outlineLvl</c>, phần thân bài thành đoạn mới ngay sau và KHÔNG
    /// mang outline. Không một ký tự nào biến mất.
    /// </summary>
    [Fact]
    public void Doan_dinh_tach_duoc_khi_ranh_gioi_roi_dau_run()
    {
        var source = WriteSource(separateRuns: true);
        var slim = new DocxSlimExtractor().Extract(source);
        var joined = slim.Paragraphs.First(p => p.Text.StartsWith(HeadingText));

        var second = slim.Paragraphs.First(p => p.Text == SecondHeading);
        var secondRecord = new HeadingRecord
        {
            Index = second.Index, StableId = second.StableId, Level = 2, Text = second.Text,
            Source = HeadingSource.Model, Confidence = 0.9,
            DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
        };

        var result = OutlineWriteback.Apply(
            source, Target, Outline(slim, Split(joined, level: 2), secondRecord), new ExtractionOptions());

        Assert.Equal(2, result.Applied);
        Assert.Empty(result.Skipped);

        var written = new DocxSlimExtractor().Extract(Target);
        var head = written.ByIndex(joined.Index)!;
        var tail = written.ByIndex(joined.Index + 1)!;

        Assert.Equal(HeadingText, head.Text);
        Assert.Equal(1, head.OutlineLevel);                  // cấp 2 → w:outlineLvl = 1
        Assert.Equal(BodyText, tail.Text);
        Assert.Null(tail.OutlineLevel);                      // thân bài không được vào cây điều hướng

        // Đoạn mới CHÈN THÊM, không thay thế: tài liệu dài ra đúng một đoạn.
        Assert.Equal(slim.Paragraphs.Count + 1, written.Paragraphs.Count);

        // Không mất ký tự: ghép hai phần lại đúng bằng text gốc.
        Assert.Equal(joined.Text, $"{head.Text} {tail.Text}");

        // Đề mục phía sau chỗ tách phải dịch đúng một đoạn và vẫn mang cấp đã chốt.
        var shifted = written.ByIndex(second.Index + 1)!;
        Assert.Equal(SecondHeading, shifted.Text);
        Assert.Equal(1, shifted.OutlineLevel);
    }

    /// <summary>
    /// Fail-closed: ranh giới nằm GIỮA một run thì tách sẽ phải cắt đôi text trong run, tức đổi cách
    /// chia run của tài liệu. Từ chối như cũ thay vì làm liều.
    /// </summary>
    [Fact]
    public void Ranh_gioi_giua_run_thi_van_tu_choi()
    {
        var source = WriteSource(separateRuns: false);
        var slim = new DocxSlimExtractor().Extract(source);
        var joined = slim.Paragraphs.First(p => p.Text.StartsWith(HeadingText));

        var result = OutlineWriteback.Apply(
            source, Target, Outline(slim, Split(joined, level: 2)), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("inline_body_not_splittable", Assert.Single(result.Skipped).Reason);

        // Và bản đích giữ nguyên số đoạn — không tách nửa vời.
        Assert.Equal(slim.Paragraphs.Count, new DocxSlimExtractor().Extract(Target).Paragraphs.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
