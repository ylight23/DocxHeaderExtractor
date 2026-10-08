using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();

    /// <summary>Kích thước chunk đầu ra — thuộc pipeline, không thuộc provider/runtime.</summary>
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>Bỏ qua LLM, chỉ dùng luật (nhanh, để đối chiếu).</summary>
    public bool DisableLlm { get; set; }

    /// <summary>In nguyên văn request/response của mô hình (debug).</summary>
    public bool ShowRawOutput { get; set; }

    public Action<string>? Log { get; set; }

}
