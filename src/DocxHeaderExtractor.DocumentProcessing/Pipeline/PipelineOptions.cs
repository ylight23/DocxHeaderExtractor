using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Sources.OpenXmlLayer;

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
    /// <summary>The folder where a built PDF source is kept between runs; the file's bytes and this build of the code are its key. Null builds every time.</summary>
    public string? SourceCacheRoot { get; set; } = ".dhx-evidence/source";
    public int? ScanJobs { get; set; }
    public bool ScanVision { get; set; }
    /// <summary>Reuse a scan unit's selection answer when its first request is unchanged (stored under the evidence root); false asks again.</summary>
    public bool ScanReuseSelection { get; set; } = true;

}
