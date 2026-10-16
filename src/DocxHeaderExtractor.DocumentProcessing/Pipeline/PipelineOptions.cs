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

    public Action<string>? Log { get; set; }

    /// <summary>Shared OCR/VLM evidence store for scanned DOCX files.</summary>
    public string ScanEvidenceRoot { get; set; } = ".dhx-evidence";
    public int? ScanJobs { get; set; }
    public bool ScanVision { get; set; }

}
