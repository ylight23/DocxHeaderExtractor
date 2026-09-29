using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

public enum OutlineFormat { Json, Markdown, Text, Xml, Csv }

public static class OutlineFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // giữ nguyên tiếng Việt có dấu
    };

    public static string Format(DocumentOutline outline, OutlineFormat format) => format switch
    {
        OutlineFormat.Json => ToJson(outline),
        OutlineFormat.Markdown => ToMarkdown(outline),
        OutlineFormat.Text => ToText(outline),
        OutlineFormat.Xml => ToXml(outline),
        OutlineFormat.Csv => ToCsv(outline),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string ToJson(DocumentOutline outline) => JsonSerializer.Serialize(outline, JsonOptions);

    private static string ToMarkdown(DocumentOutline o)
    {
        var sb = new StringBuilder();
        sb.Append("# Cấu trúc: ").AppendLine(o.File);
        sb.AppendLine();
        foreach (var h in o.Headings)
        {
            sb.Append(new string(' ', Math.Max(0, ((h.Level ?? 1) - 1) * 2)))
              .Append("- ")
              .Append(h.Text)
              .Append("  <!-- lvl=").Append(h.Level?.ToString() ?? "?")
              .Append(" i=").Append(h.Index)
              .Append(string.IsNullOrEmpty(h.StableId) ? "" : " sid=" + h.StableId)
              .Append(" src=").Append(h.Source)
              .Append(h.Disputed ? " CẦN-XEM-LẠI" : "")
              .AppendLine(" -->");
        }

        if (o.DisputedCount > 0)
        {
            sb.AppendLine();
            sb.Append("> ").Append(o.DisputedCount)
              .AppendLine(" đoạn đáng ngờ (đánh dấu CẦN-XEM-LẠI) — cần trọng tài xác nhận.");
        }
        return sb.ToString();
    }

    private static string ToText(DocumentOutline o)
    {
        var sb = new StringBuilder();
        foreach (var h in o.Headings)
            sb.Append(new string(' ', Math.Max(0, ((h.Level ?? 1) - 1) * 4))).AppendLine(h.Text);
        return sb.ToString();
    }

    private static string ToXml(DocumentOutline o)
    {
        var sb = new StringBuilder();
        sb.Append("<outline file=\"").Append(Esc(o.File)).Append("\" headings=\"")
          .Append(o.Headings.Count).AppendLine("\">");
        foreach (var h in o.Headings)
        {
            sb.Append("  <h level=\"").Append(h.Level?.ToString() ?? "")
               .Append("\" index=\"").Append(h.Index)
               .Append(string.IsNullOrEmpty(h.StableId) ? "" : "\" stableId=\"" + Esc(h.StableId))
              .Append("\" source=\"").Append(h.Source)
              .Append("\">").Append(Esc(h.Text)).AppendLine("</h>");
        }
        sb.AppendLine("</outline>");
        return sb.ToString();
    }

    private static string ToCsv(DocumentOutline o)
    {
        var sb = new StringBuilder();
        sb.AppendLine("index,stableId,level,source,confidence,styleId,text");
        foreach (var h in o.Headings)
        {
            sb.Append(h.Index).Append(',')
              .Append(Csv(h.StableId ?? "")).Append(',')
              .Append(h.Level?.ToString() ?? "").Append(',')
              .Append(h.Source).Append(',')
              .Append(h.Confidence.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(h.StyleId ?? "")).Append(',')
              .AppendLine(Csv(h.Text));
        }
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains('"') || s.Contains(',') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"") + '"'
            : s;

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
