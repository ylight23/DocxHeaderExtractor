namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

public sealed class ExtractionOptions
{
    /// <summary>Đi vào cả đoạn nằm trong bảng.</summary>
    public bool IncludeTables { get; set; } = true;

    /// <summary>Đọc thêm w:hdr / w:ftr (header–footer trang in).</summary>
    public bool IncludePageHeadersFooters { get; set; }
}
