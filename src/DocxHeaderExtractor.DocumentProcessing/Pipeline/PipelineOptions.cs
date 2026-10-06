using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();

    /// <summary>Kích thước chunk đầu ra — thuộc pipeline, không thuộc provider/runtime.</summary>
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>Bỏ qua LLM, chỉ dùng luật (nhanh, để đối chiếu).</summary>
    public bool DisableLlm { get; set; }

    /// <summary>
    /// Optional fail-closed gate for an explicitly frozen PDF provider experiment. Null preserves
    /// ordinary non-experiment runtime behavior; an experiment must supply its manifest-bound gate.
    /// </summary>

    /// <summary>
    /// Explicit experiment-only replay capture. Null keeps ordinary extraction free of artifact
    /// writes; when supplied, persistence is fail-closed unless the request is optional.
    /// </summary>
    public SemanticAuthorityReplayCaptureRequest? ReplayCapture { get; set; }

    /// <summary>Ghi XML tinh gọn từ canonical model ra file để debug/đối chiếu source.</summary>
    public string? DumpXmlPath { get; set; }

    /// <summary>In nguyên văn request/response của mô hình (debug).</summary>
    public bool ShowRawOutput { get; set; }

    public Action<string>? Log { get; set; }

}
