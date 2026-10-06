namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

public sealed class ExtractionOptions
{
    /// <summary>Đi vào cả đoạn nằm trong bảng.</summary>
    public bool IncludeTables { get; set; } = true;
}
