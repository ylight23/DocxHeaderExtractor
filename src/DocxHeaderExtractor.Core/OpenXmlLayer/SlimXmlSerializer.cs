using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Một dòng trong XML tinh gọn.</summary>
public sealed record XmlLine(string Text, int? ParagraphIndex, bool IsCandidate);

/// <summary>
/// Sinh XML tinh gọn để nạp vào LLM. Nguyên tắc: mỗi đoạn ứng viên là một dòng,
/// các đoạn thân bài liên tiếp gom thành &lt;n c="k"/&gt;.
/// Thuộc tính chỉ xuất hiện khi có giá trị ⇒ giảm mạnh số token so với document.xml gốc.
/// </summary>
public static class SlimXmlSerializer
{
    /// <summary>XML rút gọn nhưng giữ TẤT CẢ các đoạn – dùng để debug/kiểm tra bộ lọc.</summary>
    public static string ToFullXml(SlimDocument doc, ExtractionOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("<doc file=\"").Append(Escape(doc.FileName)).Append("\" n=\"")
          .Append(doc.Paragraphs.Count).Append("\">\n");

        foreach (var p in doc.Paragraphs)
        {
            if (p.Role == ParagraphRole.Empty) continue;
            sb.Append(Element(p, options.MaxTextLength, includeScore: true)).Append('\n');
        }

        sb.Append("</doc>");
        return sb.ToString();
    }

    /// <summary>Danh sách dòng đã tinh gọn cho LLM (ứng viên + đoạn gom + ngữ cảnh).</summary>
    public static IReadOnlyList<XmlLine> BuildLines(SlimDocument doc, ExtractionOptions options) =>
        BuildLines(doc, options, reviewIndexes: null);

    /// <summary>
    /// Sinh dòng cho LLM. Khi <paramref name="reviewIndexes"/> có giá trị, mọi paragraph vẫn được
    /// giữ nguyên làm ngữ cảnh; chỉ các index trong tập đó bị grammar yêu cầu trả lời. Nhờ vậy
    /// pipeline có thể review cả tài liệu mà không để ngưỡng heuristic làm mất heading lạ.
    /// </summary>
    public static IReadOnlyList<XmlLine> BuildLines(
        SlimDocument doc,
        ExtractionOptions options,
        IReadOnlySet<int>? reviewIndexes)
    {
        var lines = new List<XmlLine>();
        int normalRun = 0;

        void FlushNormal()
        {
            if (normalRun == 0) return;
            if (options.CollapseNormalRuns)
                lines.Add(new XmlLine($"<n c=\"{normalRun}\"/>", null, false));
            normalRun = 0;
        }

        var paragraphs = doc.Paragraphs;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];

            if (p.Role == ParagraphRole.Empty) continue;

            var review = reviewIndexes?.Contains(p.Index) ?? p.IsCandidate;
            var preserveEveryParagraph = reviewIndexes is not null;

            if (!p.IsCandidate && !preserveEveryParagraph)
            {
                normalRun++;
                continue;
            }

            FlushNormal();
            lines.Add(new XmlLine(Element(p, options.MaxTextLength, includeScore: false), p.Index, review));

            if (options.IncludeFollowingContext && !preserveEveryParagraph)
            {
                var next = paragraphs.Skip(i + 1)
                    .FirstOrDefault(x => x.Role != ParagraphRole.Empty);
                if (next is not null && !next.IsCandidate && next.Text.Length > 0)
                {
                    var snippet = Truncate(next.Text, options.ContextTextLength);
                    lines.Add(new XmlLine($"  <ctx>{Escape(snippet)}</ctx>", null, false));
                }
            }
        }

        FlushNormal();
        return lines;
    }

    /// <summary>
    /// Bọc một khối để gửi cho mô hình. KHÔNG kèm tên file: chuỗi này là đầu vào của mô hình,
    /// mà tên file không mang thông tin gì về cấu trúc tài liệu — để nó vào thì đổi tên file
    /// là đổi prompt, và cùng một nội dung lại cho ra kết quả khác (đã đo được).
    /// </summary>
    public static string WrapChunk(IEnumerable<XmlLine> lines, int chunkNo, int chunkTotal)
    {
        var sb = new StringBuilder();
        sb.Append("<doc part=\"").Append(chunkNo).Append('/').Append(chunkTotal).Append("\">\n");
        foreach (var l in lines) sb.Append(l.Text).Append('\n');
        sb.Append("</doc>");
        return sb.ToString();
    }

    private static string Element(SlimParagraph p, int maxText, bool includeScore)
    {
        var sb = new StringBuilder(96);
        sb.Append("<p i=\"").Append(p.Index).Append('"');
        if (!string.IsNullOrEmpty(p.StableId)) sb.Append(" sid=\"").Append(Escape(p.StableId)).Append('"');

        if (!string.IsNullOrEmpty(p.StyleId)) sb.Append(" s=\"").Append(Escape(p.StyleId)).Append('"');
        if (p.OutlineLevel is { } ol) sb.Append(" out=\"").Append(ol).Append('"');
        if (p.GuessedLevel is { } gl) sb.Append(" lvl=\"").Append(gl).Append('"');
        if (p.Bold) sb.Append(" b=\"1\"");
        var boldRanges = p.TextSpans.Where(x => x.Bold).Select(x => $"{x.Start}-{x.End}").ToList();
        if (boldRanges.Count > 0 && p.TextSpans.Any(x => !x.Bold))
            sb.Append(" br=\"").Append(string.Join(',', boldRanges)).Append('"');
        if (p.VerifiedHeadingEnd is { } headingEnd && p.VerifiedBodyStart is { } bodyStart)
            sb.Append(" hs=\"0-").Append(headingEnd).Append("\" bs=\"").Append(bodyStart).Append("-")
              .Append(p.Text.Length).Append('"');
        if (p.AllCaps) sb.Append(" caps=\"1\"");
        if (p.Italic) sb.Append(" it=\"1\"");
        if (p.Underline) sb.Append(" u=\"1\"");
        if (p.FontSizePt is { } fs) sb.Append(" sz=\"").Append(Fmt(fs)).Append('"');
        if (!string.IsNullOrEmpty(p.Alignment) && p.Alignment != "left")
            sb.Append(" al=\"").Append(Escape(p.Alignment)).Append('"');
        if (p.NumberingId is { } nid) sb.Append(" num=\"").Append(nid)
            .Append('.').Append(p.NumberingLevel ?? 0).Append('"');
        if (!string.IsNullOrEmpty(p.NumberLabel)) sb.Append(" nlab=\"").Append(Escape(p.NumberLabel)).Append('"');
        if (p.KeepNext) sb.Append(" kn=\"1\"");
        if (p.PageBreakBefore) sb.Append(" pb=\"1\"");
        if (p.TableDepth > 0) sb.Append(" tbl=\"").Append(p.TableDepth).Append('"');
        if (includeScore) sb.Append(" role=\"").Append(p.Role).Append("\" sc=\"").Append(Fmt(p.Score)).Append('"');

        sb.Append('>').Append(Escape(Truncate(p.Text, maxText))).Append("</p>");
        return sb.ToString();
    }

    private static string Fmt(double v) =>
        v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Escape(string s)
    {
        if (s.AsSpan().IndexOfAny('<', '>', '&') < 0 && !s.Contains('"')) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
