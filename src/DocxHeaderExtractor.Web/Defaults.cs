using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Giá trị mặc định đọc thẳng từ <see cref="LlamaOptions"/> và <see cref="ExtractionOptions"/>,
/// để giao diện không tự chép hằng số rồi lệch khỏi CLI khi Core đổi.
/// </summary>
public sealed record Defaults(
    int ChunkTokens,
    int ChunkCandidates,
    double Threshold,
    bool StructuralOnly)
{
    public static Defaults Current()
    {
        var llama = new LlamaOptions();
        var extraction = new ExtractionOptions();
        return new Defaults(
            ChunkTokens: llama.ChunkTokenBudget,
            ChunkCandidates: llama.MaxCandidatesPerChunk,
            Threshold: extraction.CandidateThreshold,
            // Đo được: bật luật từ ngữ không đổi kết quả trên cả hai bộ test, nhưng luật loại
            // chú thích có thể chém nhầm tiêu đề dạng "Bảng 2 cột dữ liệu" mà không cho gỡ.
            StructuralOnly: true);
    }
}
