namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Toàn bộ tài liệu sau bước lọc OpenXML.
/// </summary>
public sealed class SlimDocument
{
    public required string FileName { get; init; }

    /// <summary>Đường dẫn file thực sự được đọc (có thể là bản .docx convert từ .doc).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Tất cả các đoạn theo thứ tự tài liệu.</summary>
    public required IReadOnlyList<SlimParagraph> Paragraphs { get; init; }

    public double? DefaultFontSizePt { get; init; }

    /// <summary>
    /// Style Word của tài liệu này có đáng tin không, và cho việc gì. Xem
    /// <c>StyleTrustAudit</c>. Null khi chưa chấm (đường dựng SlimDocument trong test).
    /// </summary>
    public StyleTrust? StyleTrust { get; init; }

    /// <summary>Nội dung w:hdr / w:ftr (header–footer của trang), nếu được yêu cầu.</summary>
    public IReadOnlyList<string> PageHeaders { get; init; } = [];
    public IReadOnlyList<string> PageFooters { get; init; } = [];

    public IEnumerable<SlimParagraph> Candidates => Paragraphs.Where(p => p.IsCandidate);

    public SlimParagraph? ByIndex(int index) =>
        _lookup.TryGetValue(index, out var p) ? p : null;

    private Dictionary<int, SlimParagraph> _lookup = new();

    public SlimDocument Build()
    {
        _lookup = Paragraphs.ToDictionary(p => p.Index);
        return this;
    }
}
