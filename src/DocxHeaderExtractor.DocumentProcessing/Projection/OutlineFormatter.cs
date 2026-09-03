using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

public enum OutlineFormat { Json, Markdown, Text, Xml, Csv }

public static class OutlineFormatter
{
    private static readonly Regex ContinuationMarker = new(
        @"\s*(?:\((?:cont(?:inued)?|cont['’]?d)\)|(?:cont(?:inued)?|cont['’]?d))\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    private static string ToJson(DocumentOutline outline)
    {
        var root = JsonSerializer.SerializeToNode(outline, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Không serialize được outline JSON.");
        var report = NavigationCollapseReport(outline.Headings);
        root["navigationCollapsedCount"] = report.CollapsedCount;
        root["navigationCollapsedFromIndexes"] = JsonSerializer.SerializeToNode(report.Groups, JsonOptions);
        return root.ToJsonString(JsonOptions);
    }

    private static string ToMarkdown(DocumentOutline o)
    {
        var sb = new StringBuilder();
        sb.Append("# Cấu trúc: ").AppendLine(o.File);
        sb.AppendLine();
        foreach (var h in NavigationCollapseReport(o.Headings).Headings)
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
            // Không nói "hai lượt" ở đây: từ khi có hậu kiểm đánh số, một đoạn bị đánh dấu vì
            // hai lượt lệch nhau HOẶC vì cấp của nó lệch khỏi các mục cùng dạng đánh số.
            sb.Append("> ").Append(o.DisputedCount)
              .AppendLine(" đoạn đáng ngờ (đánh dấu CẦN-XEM-LẠI) — cần trọng tài xác nhận.");
        }
        return sb.ToString();
    }

    private static string ToText(DocumentOutline o)
    {
        var sb = new StringBuilder();
        foreach (var h in NavigationCollapseReport(o.Headings).Headings)
            sb.Append(new string(' ', Math.Max(0, ((h.Level ?? 1) - 1) * 4))).AppendLine(h.Text);
        return sb.ToString();
    }

    /// <summary>
    /// User-facing navigation view: keep source headings intact for JSON/eval, but collapse repeated
    /// page-title continuations in text/markdown output. The collapse is structural, not lexical:
    /// only siblings under the same parent and same level are merged.
    /// </summary>
    public static IReadOnlyList<HeadingRecord> NavigationHeadings(IReadOnlyList<HeadingRecord> headings) =>
        NavigationCollapseReport(headings).Headings;

    public static NavigationCollapseReport NavigationCollapseReport(IReadOnlyList<HeadingRecord> headings)
    {
        var result = new List<HeadingRecord>();
        var parentKeys = new string?[10];
        var seenSiblingKeys = new Dictionary<(string Parent, int Level, string Canon), CollapseAccumulator>();

        foreach (var h in headings)
        {
            // Unresolved level collapses/groups as top-level for this display-only navigation view;
            // it never rewrites HeadingRecord.Level itself, so nothing product-authoritative is guessed.
            var level = Math.Clamp(h.Level ?? 1, 1, 9);
            var parent = level == 1 ? "" : parentKeys[level - 1] ?? "";
            var text = h.Text ?? string.Empty;
            var canon = CanonicalNavigationTitle(text);
            var key = (parent, level, canon);
            if (!string.IsNullOrEmpty(canon) && seenSiblingKeys.TryGetValue(key, out var existing))
            {
                existing.Collapsed.Add(new CollapsedHeadingRef(h.Index, h.Level, text));
                continue;
            }

            var display = ContinuationMarker.Replace(text.Trim(), "").Trim();
            var displayHeading = CloneForDisplay(h, string.IsNullOrWhiteSpace(display) ? text : display);
            result.Add(displayHeading);
            if (!string.IsNullOrEmpty(canon))
                seenSiblingKeys[key] = new CollapseAccumulator(
                    displayHeading.Index,
                    displayHeading.Level,
                    displayHeading.Text,
                    []);

            parentKeys[level] = $"{parent}/{level}:{canon}";
            for (var i = level + 1; i < parentKeys.Length; i++)
                parentKeys[i] = null;
        }

        var groups = seenSiblingKeys.Values
            .Where(g => g.Collapsed.Count > 0)
            .Select(g => new NavigationCollapseGroup(g.KeptIndex, g.KeptLevel, g.KeptText, g.Collapsed))
            .ToList();
        return new NavigationCollapseReport(result, groups);
    }

    private static HeadingRecord CloneForDisplay(HeadingRecord h, string text) => new()
    {
        Index = h.Index,
        StableId = h.StableId,
        Level = h.Level,
        Text = text,
        OriginalText = h.OriginalText,
        HeadingSpan = h.HeadingSpan,
        InlineBody = h.InlineBody,
        InlineBodySpan = h.InlineBodySpan,
        BoundarySource = h.BoundarySource,
        StyleId = h.StyleId,
        Source = h.Source,
        Confidence = h.Confidence,
        ModelConfirmed = h.ModelConfirmed,
        CriticConfirmed = h.CriticConfirmed,
        DecisionStatus = h.DecisionStatus,
        ConfidenceBasis = h.ConfidenceBasis,
        AcceptanceSignature = h.AcceptanceSignature,
        CalibrationSamples = h.CalibrationSamples,
        Evidence = h.Evidence,
        Disputed = h.Disputed,
    };

    private static string CanonicalNavigationTitle(string text)
    {
        var withoutContinuation = ContinuationMarker.Replace((text ?? string.Empty).Trim(), "");
        return new string(withoutContinuation
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
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

    private sealed record CollapseAccumulator(
        int KeptIndex,
        int? KeptLevel,
        string KeptText,
        List<CollapsedHeadingRef> Collapsed);
}

public sealed record NavigationCollapseReport(
    IReadOnlyList<HeadingRecord> Headings,
    IReadOnlyList<NavigationCollapseGroup> Groups)
{
    public int CollapsedCount => Groups.Sum(g => g.Collapsed.Count);
}

public sealed record NavigationCollapseGroup(
    [property: JsonPropertyName("keptIndex")] int KeptIndex,
    [property: JsonPropertyName("keptLevel")] int? KeptLevel,
    [property: JsonPropertyName("keptText")] string KeptText,
    [property: JsonPropertyName("collapsed")] IReadOnlyList<CollapsedHeadingRef> Collapsed);

public sealed record CollapsedHeadingRef(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("text")] string Text);
