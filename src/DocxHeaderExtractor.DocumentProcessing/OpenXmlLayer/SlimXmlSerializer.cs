using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>Human-readable XML dump of the native DOCX policy state.</summary>
public static class SlimXmlSerializer
{
    public static string ToFullXml(DocxPolicyState state, ExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sb = new StringBuilder();
        sb.Append("<doc file=\"").Append(Escape(state.Source.FileName)).Append("\" n=\"")
            .Append(state.Paragraphs.Count).Append("\" mode=\"")
            .Append(state.Mode?.Mode.ToString() ?? "Unknown").Append("\">\n");
        foreach (var paragraph in state.Paragraphs)
        {
            if (paragraph.Role == ParagraphRole.Empty) continue;
            sb.Append(Element(paragraph, options.MaxTextLength)).Append('\n');
        }
        return sb.Append("</doc>").ToString();
    }

    private static string Element(DocxPolicyParagraph paragraph, int maxText)
    {
        var sb = new StringBuilder(96);
        sb.Append("<p i=\"").Append(paragraph.Index).Append('"');
        if (!string.IsNullOrEmpty(paragraph.StableId)) sb.Append(" sid=\"").Append(Escape(paragraph.StableId)).Append('"');
        if (!string.IsNullOrEmpty(paragraph.StyleId)) sb.Append(" s=\"").Append(Escape(paragraph.StyleId)).Append('"');
        if (paragraph.OutlineLevel is { } outline) sb.Append(" out=\"").Append(outline).Append('"');
        if (paragraph.GuessedLevel is { } guessed) sb.Append(" lvl=\"").Append(guessed).Append('"');
        if (paragraph.InContentControl) sb.Append(" sdt=\"1\"");
        if (paragraph.Bold) sb.Append(" b=\"1\"");
        if (paragraph.AllCaps) sb.Append(" caps=\"1\"");
        if (paragraph.Italic) sb.Append(" it=\"1\"");
        if (paragraph.Underline) sb.Append(" u=\"1\"");
        if (paragraph.FontSizePt is { } size) sb.Append(" sz=\"").Append(size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
        if (!string.IsNullOrEmpty(paragraph.Alignment) && paragraph.Alignment != "left")
            sb.Append(" al=\"").Append(Escape(paragraph.Alignment)).Append('"');
        if (paragraph.NumberingId is { } numberingId)
            sb.Append(" num=\"").Append(numberingId).Append('.').Append(paragraph.NumberingLevel ?? 0).Append('"');
        if (!string.IsNullOrEmpty(paragraph.NumberLabel)) sb.Append(" nlab=\"").Append(Escape(paragraph.NumberLabel)).Append('"');
        if (paragraph.KeepNext) sb.Append(" kn=\"1\"");
        if (paragraph.PageBreakBefore) sb.Append(" pb=\"1\"");
        if (paragraph.TableDepth > 0) sb.Append(" tbl=\"").Append(paragraph.TableDepth).Append('"');
        sb.Append(" role=\"").Append(paragraph.Role).Append("\" sc=\"")
            .Append(paragraph.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
        return sb.Append('>').Append(Escape(Truncate(paragraph.Text, maxText))).Append("</p>").ToString();
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny('<', '>', '&') < 0 && !text.Contains('"')) return text;
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
