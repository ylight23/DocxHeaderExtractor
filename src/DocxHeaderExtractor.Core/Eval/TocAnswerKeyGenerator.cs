using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

public enum TocKeyStatus
{
    /// <summary>Tỉ lệ khớp đạt ngưỡng — ghi ra .key được.</summary>
    Accepted,

    /// <summary>Tài liệu không đủ mục lục do Word sinh để tin (dưới <see cref="TocAnswerKeyGenerator.MinimumTocEntries"/>).</summary>
    InsufficientTocEntries,

    /// <summary>Có mục lục nhưng khớp với thân bài dưới ngưỡng — có thể mục lục lỗi thời, hoặc
    /// tiêu đề nằm lọt giữa đoạn (xem TODO.md mục 10, cờ --split-merged) nên không khớp NGUYÊN đoạn.</summary>
    BelowMatchThreshold,
}

/// <summary>Một cặp (đoạn thân bài, cấp) suy được từ một mục lục — chưa qua người duyệt.</summary>
public sealed record TocKeyEntry(string StableId, int Level, string BodyText, string TocText);

public sealed record TocKeyResult(
    string FileName,
    TocKeyStatus Status,
    int TocEntryCount,
    int AmbiguousTocDepthCount,
    int AmbiguousBodyMatchCount,
    double MatchRatio,
    IReadOnlyList<TocKeyEntry> Matches,
    IReadOnlyList<string> UnmatchedTocText,
    IReadOnlyList<string> AmbiguousTocText)
{
    public bool Accepted => Status == TocKeyStatus.Accepted;
    public int MatchedCount => Matches.Count;
}

/// <summary>
/// Sinh đáp án ỨNG VIÊN (chưa qua người duyệt) từ mục lục do Word tự sinh, để mở rộng bench —
/// KHÔNG thay thế cho gán nhãn tay trong <c>keys/</c>.
/// <para>
/// Khớp mục lục với TOÀN BỘ đoạn thân bài (<see cref="SlimDocument.Paragraphs"/>), không chỉ những
/// đoạn pipeline đã xếp là ứng viên heading. Nếu khớp với đầu ra của chính pipeline đang nghi ngờ thì
/// không đo được gì có ý nghĩa độc lập — đúng cái bẫy TODO.md gọi là "không suy về ĐẦU VÀO từ ĐẦU RA
/// của chính pipeline đang nghi ngờ" (§46.5).
/// </para>
/// <para>
/// Dùng chung logic chuẩn hoá/suy độ sâu với <see cref="TableOfContentsAnchor"/> — một nguồn duy
/// nhất, không nhân đôi luật đã kiểm chứng.
/// </para>
/// <para>
/// TOC có thể LỖI THỜI: tác giả sửa tiêu đề mà không refresh mục lục. Đây là lý do bắt buộc có
/// ngưỡng khớp thay vì tin tuyệt đối, và vì sao kết quả ở đây được đánh dấu <c>toc_derived</c>,
/// tách khỏi đáp án người kiểm.
/// </para>
/// </summary>
public static class TocAnswerKeyGenerator
{
    /// <summary>Dưới ngưỡng này, mục lục quá ít để tin tỉ lệ khớp có ý nghĩa thống kê.</summary>
    public const int MinimumTocEntries = 5;

    public const double DefaultMatchThreshold = 0.80;

    public static TocKeyResult Generate(SlimDocument document, double matchThreshold = DefaultMatchThreshold)
    {
        // depth: -1 nghĩa là hai mục lục trùng chuẩn hoá nhưng khác cấp -> không phân định được, bỏ.
        var tocDepthByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var tocRawTextByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousDepth = 0;
        foreach (var p in document.Paragraphs)
        {
            if (!p.InTableOfContents) continue;
            // "Danh mục hình ảnh"/"Danh mục bảng biểu" dùng ĐÚNG cơ chế TOC field/hyperlink _Toc
            // như mục lục chương, nhưng liệt kê CHÚ THÍCH hình/bảng, không phải đề mục — đo được
            // trên tài liệu thật: 13/32 mục lục là "Hình 1.1:"/"Bảng 1.2:" và không mục nào trong
            // 33 đề mục thật thuộc dạng này. Loại trước khi tính, không để chúng pha loãng mẫu số.
            if (HeadingHeuristics.CaptionRx.IsMatch(p.Text)) continue;
            // Ưu tiên NumberLabel đã resolve từ numbering.xml ("1.1.1" -> cấp 3) hơn suy từ TEXT
            // của dòng mục lục: heading numPr-driven (numbering do Word vẽ, không phải số gõ tay)
            // không để lại số nào trong TEXT của mục lục, nhưng NumberLabel vẫn đúng — đo được trên
            // tài liệu thật: TableOfContentsAnchor.DepthOf mặc định cả 14 mục về cấp 1 vì rơi vào
            // nhánh "không có ký hiệu số trong text -> cấp 1", trong khi NumberLabel ghi rõ "1.1.1".
            var depth = DepthFromNumberLabel(p.NumberLabel) ?? TableOfContentsAnchor.DepthOf(p.Text);
            if (depth is not { } d) continue;
            var key = TableOfContentsAnchor.Normalize(p.Text);
            if (key.Length < 4) continue;
            if (tocDepthByKey.TryGetValue(key, out var old))
            {
                if (old == d) continue;
                if (old >= 1) ambiguousDepth++;
                tocDepthByKey[key] = -1;
            }
            else
            {
                tocDepthByKey[key] = d;
                tocRawTextByKey[key] = p.Text;
            }
        }

        var usableEntries = tocDepthByKey.Where(kv => kv.Value >= 1).ToList();
        if (usableEntries.Count < MinimumTocEntries)
            return new TocKeyResult(document.FileName, TocKeyStatus.InsufficientTocEntries,
                usableEntries.Count, ambiguousDepth, 0, 0.0, [], [], []);

        // Chỉ mục thân bài: chuẩn hoá -> danh sách đoạn khớp. KHÔNG lọc theo IsCandidate/Role —
        // TOC phải được đối chiếu với nguồn độc lập với phán đoán của pipeline.
        var bodyByKey = new Dictionary<string, List<SlimParagraph>>(StringComparer.Ordinal);
        foreach (var p in document.Paragraphs)
        {
            if (p.InTableOfContents) continue;
            var key = TableOfContentsAnchor.Normalize(p.Text);
            if (key.Length < 4) continue;
            if (!bodyByKey.TryGetValue(key, out var list))
                bodyByKey[key] = list = [];
            list.Add(p);
        }

        var matches = new List<TocKeyEntry>();
        var unmatchedToc = new List<string>();
        var ambiguousToc = new List<string>();
        var ambiguousBody = 0;
        foreach (var (key, depth) in usableEntries)
        {
            if (!bodyByKey.TryGetValue(key, out var candidates))
            {
                unmatchedToc.Add(tocRawTextByKey[key]); // không có trong thân bài
                continue;
            }
            if (candidates.Count > 1)
            {
                ambiguousBody++;
                ambiguousToc.Add(tocRawTextByKey[key]); // nhiều đoạn cùng text -> bỏ
                continue;
            }
            var p = candidates[0];
            matches.Add(new TocKeyEntry(p.StableId, Math.Clamp(depth, 1, 9), p.Text, tocRawTextByKey[key]));
        }

        var ratio = (double)matches.Count / usableEntries.Count;
        var status = ratio >= matchThreshold ? TocKeyStatus.Accepted : TocKeyStatus.BelowMatchThreshold;
        return new TocKeyResult(document.FileName, status, usableEntries.Count, ambiguousDepth,
            ambiguousBody, ratio, matches, unmatchedToc, ambiguousToc);
    }

    /// <summary>Đếm số đoạn ngăn cách bởi dấu chấm trong nhãn numbering đã resolve ("1.1.1." -> 3,
    /// "IV." -> 1). Null nếu đoạn không mang numPr (headings kiểu Chương/từ khoá thường không có).</summary>
    private static int? DepthFromNumberLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var parts = label.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts.Length : null;
    }

    public static string ToAnswerKeyText(this TocKeyResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Đáp án SUY TỪ MỤC LỤC (toc_derived) — ").AppendLine(result.FileName);
        sb.AppendLine("# KHÔNG phải người kiểm — mục lục có thể lỗi thời, xem keys/README.md.");
        sb.Append("# Khớp ").Append(result.MatchedCount).Append('/').Append(result.TocEntryCount)
          .Append(" mục (").Append((result.MatchRatio * 100).ToString("0.0")).AppendLine("%).");
        sb.AppendLine("# @<stable-id> <cấp>   — suy từ mục lục, chưa xác nhận");
        foreach (var m in result.Matches.OrderBy(m => m.StableId, StringComparer.Ordinal))
            sb.Append('@').Append(m.StableId).Append(' ').Append(m.Level).Append("   # ").AppendLine(m.BodyText);
        return sb.ToString();
    }
}
