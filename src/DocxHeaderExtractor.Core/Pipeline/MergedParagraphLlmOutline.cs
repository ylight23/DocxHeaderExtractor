using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Fallback cho tài liệu PDF→DOCX gộp nhiều "Điều N."/"N.N. Title" vào một đoạn — tầng ứng viên
/// OpenXML/heuristic không thấy được marker vì nó nằm GIỮA đoạn, không phải đầu (đo trên toàn corpus
/// ở handoff.md §112: 13/29 file `01_phap_quy`+`06_dich_song_ngu`, cả 5/5 file `07_system_generated`).
/// <para>
/// Dùng <see cref="ParagraphHeadingSplitter.SegmentsWithOffsets"/> để lấy segment, LỌC trang mục
/// lục/danh sách dày đặc (độ dài segment trung vị thấp — đo được ở §114: trang mục lục 12-36 ký tự,
/// thân bài thật 90-500+, chọn 50 làm biên an toàn), rồi hỏi LLM PHÂN LOẠI từng segment (heading thật
/// hay rác hình dạng-giống-heading: cross-reference, mục con đánh số, số phương trình...) TRƯỚC KHI
/// cắt ranh giới bằng <see cref="LlmBoundaryCutter"/> đã đo 100% ở §111 — hai bước tách biệt, tái
/// dùng cơ chế đã có, không phát minh một prompt gộp mới chưa đo.
/// </para>
/// <para>
/// Vì sao KHÔNG dùng <c>ExtractionOptions.SplitMergedParagraphs</c> (dựng heading trực tiếp, không
/// qua model): đo trên toàn corpus ở §113, cờ đó làm 54/89 file (61%) nổ found vào vùng implausible
/// — regex marker quá lỏng để tin trực tiếp. Lớp này THAY THẾ bước "tin trực tiếp" bằng bước "hỏi
/// model xác nhận từng segment", đúng vai trò tầng ngữ nghĩa đã có trong pipeline cho mọi nguồn
/// <see cref="HeadingSource.Structure"/> khác.
/// </para>
/// <para>
/// CHỈ áp dụng domain đã có bảng cắt ranh giới đo được
/// (<see cref="LlmBoundaryCutter.IsSupported"/>) — không suy diễn số đo sang domain chưa đo.
/// </para>
/// </summary>
public static class MergedParagraphLlmOutline
{
    private const string ClassifySystem =
        "You receive ONE short text fragment extracted from a document. Decide whether it is a REAL " +
        "section/document heading (a title that starts a new topic) or a FALSE POSITIVE that merely " +
        "looks like one because it starts with a number or label — e.g. a cross-reference (\"Section 5\" " +
        "inside a sentence), a list item, an equation/theorem number, a citation, a page header/footer, " +
        "or a sub-clause inside a longer sentence. Answer with EXACTLY one word: HEADING or NOISE. " +
        "No explanation, no punctuation, no other words.";

    /// <summary>
    /// Ngưỡng đo ở handoff.md §114: đoạn trang mục lục/danh sách dày đặc có độ dài segment TRUNG VỊ
    /// 12-36 ký tự (5 đoạn đầu `092_RFC9111`); đoạn thân bài thật trung vị 30-500+ (phần còn lại của
    /// `092` và toàn bộ `010_Luat_An_ninh_mang`). 50 là biên an toàn đo được, không phải hằng số lý
    /// thuyết — có thể lệch vài ca biên, cần đo lại nếu áp dụng cho domain khác.
    /// </summary>
    private const int TocMedianLengthThreshold = 50;

    private const int MinSegmentLength = 8;
    private const int MaxSegmentLength = 2000;

    public static async Task<List<HeadingRecord>> BuildAsync(
        SlimDocument document,
        DocumentMode mode,
        IHeaderClassifier classifier,
        CancellationToken ct = default)
    {
        var result = new List<HeadingRecord>();
        if (!LlmBoundaryCutter.IsSupported(mode)) return result;

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            ct.ThrowIfCancellationRequested();
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;

            var segments = ParagraphHeadingSplitter.SegmentsWithOffsets(p.Text);
            if (segments.Count < 2) continue;
            if (LooksLikeTocOrDenseListing(segments)) continue;

            foreach (var seg in segments)
            {
                if (seg.Text.Length < MinSegmentLength || seg.Text.Length > MaxSegmentLength) continue;
                ct.ThrowIfCancellationRequested();

                bool isHeading;
                try
                {
                    var verdictRaw = await classifier.BoundaryCutAsync(
                        ClassifySystem, $"Fragment:\n{seg.Text}\nAnswer:", ct);
                    isHeading = verdictRaw.Trim().Contains("HEADING", StringComparison.OrdinalIgnoreCase);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Lỗi backend không phải lỗi tất định của luật — bỏ qua segment này, không làm
                    // hỏng cả lượt trích xuất.
                    continue;
                }
                if (!isHeading) continue;

                var cut = await LlmBoundaryCutter.TryCutAsync(classifier, mode, seg.Text, ct);
                if (cut is not { } end || end <= 0 || end >= seg.Text.Length) continue;

                var title = seg.Text[..end];
                var start = seg.Start;
                var absoluteEnd = start + end;
                // Grounding bắt buộc: title phải đúng là substring của OriginalText tại đúng vị trí
                // đã tính — nếu lệch (không nên xảy ra vì start/end đến từ chính p.Text) thì bỏ qua
                // thay vì tin mù, cùng nguyên tắc OutlineGroundingValidator.
                if (start < 0 || absoluteEnd > p.Text.Length || p.Text[start..absoluteEnd] != title) continue;

                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = 1,
                    Text = title,
                    OriginalText = p.Text,
                    HeadingSpan = new TextOffsetSpan(start, absoluteEnd),
                    BoundarySource = "merged-paragraph-llm",
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.7,
                    DecisionStatus = HeadingDecisionStatus.RequiresReview,
                    ConfidenceBasis = "merged_paragraph_llm_segment",
                });
            }
        }
        return result;
    }

    private static bool LooksLikeTocOrDenseListing(
        IReadOnlyList<ParagraphHeadingSplitter.OffsetSegment> segments)
    {
        var lengths = segments.Select(s => s.Text.Length).OrderBy(x => x).ToList();
        var median = lengths[lengths.Count / 2];
        return median < TocMedianLengthThreshold;
    }
}
