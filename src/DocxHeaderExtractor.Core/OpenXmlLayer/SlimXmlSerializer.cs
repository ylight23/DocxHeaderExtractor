using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Một dòng trong XML tinh gọn.</summary>
public sealed record XmlLine(string Text, int? ParagraphIndex, bool IsCandidate);

/// <summary>
/// XML tinh gọn để CON NGƯỜI đọc: lệnh <c>xml</c> của CLI và file <c>--dump-xml</c>.
/// <para>
/// Đây KHÔNG còn là đầu vào của mô hình. View gửi cho LLM là
/// <see cref="NeutralDocumentViewSerializer"/> — định dạng BLOCK/metadata JSON, cố ý không dùng
/// cú pháp thẻ có thể gợi sẵn đáp án. Hai bản dựng dòng song song từng cùng tồn tại ở đây; bản
/// XML không còn người gọi nào trong <c>src/</c> nên đã bỏ, chỉ giữ lại phần dump toàn văn.
/// </para>
/// </summary>
public static class SlimXmlSerializer
{
    /// <summary>XML rút gọn nhưng giữ TẤT CẢ các đoạn – dùng để debug/kiểm tra bộ lọc.</summary>
    public static string ToFullXml(SlimDocument doc, ExtractionOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("<doc file=\"").Append(Escape(doc.FileName)).Append("\" n=\"")
          .Append(doc.Paragraphs.Count)
          // Chế độ vốn đã được đo ở DocxSlimExtractor nhưng không lộ ra đâu cả, nên không kiểm
          // chứng được trên tập lớn. Đây là chẩn đoán, không phải dữ liệu gửi cho mô hình.
          // Mode có thể null với SlimDocument dựng tay (test, dựng lại từ cache), nên không được
          // truy thẳng — serializer là đường in chẩn đoán, không được làm hỏng lời gọi nào.
          .Append("\" mode=\"").Append(doc.Mode?.Mode.ToString() ?? "Unknown")
          .Append("\">\n");

        foreach (var p in doc.Paragraphs)
        {
            if (p.Role == ParagraphRole.Empty) continue;
            sb.Append(Element(p, options.MaxTextLength, includeScore: true)).Append('\n');
        }

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
        if (p.InContentControl) sb.Append(" sdt=\"1\"");
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
