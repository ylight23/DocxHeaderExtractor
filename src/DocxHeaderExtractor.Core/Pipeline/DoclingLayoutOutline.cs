using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Adapter cho JSON sidecar do Docling xuất ra. Đây không phải runtime Python bridge: production
/// chỉ đọc JSON đã có, rồi dùng luật .NET để lọc/align về paragraph DOCX. Nếu Docling giúp nhận
/// ra layout đúng ở sandbox, phần ổn định sẽ được port dần thành strategy .NET hẹp hơn.
/// </summary>
public static class DoclingLayoutOutline
{
    public const string Basis = "docling_layout_sidecar";

    private static readonly Regex NumberedMarkerRx = new(
        @"^\s*(?<n>\d{1,3}(?:\.\d{1,3}){0,6}|[A-Z]\.\d{1,3}|[A-Z])[\.)]?\s+\S",
        RegexOptions.Compiled);
    private static readonly Regex RomanPartRx = new(
        @"^\s*(?:part|chapter|section|appendix)\s+(?:[ivxlcdm]+|\d+|[A-Z])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BadLabelRx = new(
        @"(?:page_header|page_footer|footnote|caption|table|picture|figure|formula|code|reference|list_item|text)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GoodLabelRx = new(
        @"(?:title|document_title|section_header|heading|header)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static PdfTextbookOutlineResult TryBuild(
        string originalInputPath,
        SlimDocument slim,
        DocumentModeReport mode,
        string? explicitJsonPath = null)
    {
        var jsonPath = FindSidecar(originalInputPath, explicitJsonPath);
        if (jsonPath is null)
            return PdfTextbookOutlineResult.NotApplicable("no-docling-json");

        IReadOnlyList<DoclingBlock> blocks;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            blocks = ExtractBlocks(doc.RootElement)
                .Where(IsHeadingBlock)
                .Select(NormalizeBlock)
                .Where(b => LooksLikeHeadingText(b.Text))
                .GroupBy(b => (Canon(b.Text), b.Page, b.Label))
                .Select(g => g.First())
                .OrderBy(b => b.Page ?? int.MaxValue)
                .ThenByDescending(b => b.TopY ?? double.MinValue)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PdfTextbookOutlineResult.NotApplicable("docling-json-read-failed");
        }

        if (blocks.Count < 3)
            return PdfTextbookOutlineResult.NotApplicable($"too-few-docling-heading-blocks:{blocks.Count}");

        var aligned = AlignToDocx(blocks, slim);
        var minimum = Math.Max(3, (int)Math.Ceiling(blocks.Count * 0.60));
        if (aligned.Count < minimum)
            return PdfTextbookOutlineResult.NotApplicable($"low-docx-alignment:{aligned.Count}/{blocks.Count}");

        return new PdfTextbookOutlineResult(
            aligned,
            $"json={Path.GetFileName(jsonPath)}, aligned={aligned.Count}/{blocks.Count}, mode={mode.Mode}");
    }

    internal static string? FindSidecar(string inputPath, string? explicitJsonPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitJsonPath))
            return File.Exists(explicitJsonPath) ? Path.GetFullPath(explicitJsonPath) : null;

        var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Directory.GetCurrentDirectory();
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        foreach (var candidate in new[]
        {
            Path.Combine(dir, stem + ".docling.json"),
            Path.Combine(dir, stem + ".json"),
            Path.Combine(Directory.GetCurrentDirectory(), ".verify-build", "docling", stem + ".json"),
            Path.Combine(Directory.GetCurrentDirectory(), ".verify-build", "docling", stem + ".docling.json"),
        })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<DoclingBlock> ExtractBlocks(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var text = StringProp(element, "text") ??
                       StringProp(element, "orig") ??
                       StringProp(element, "name");
            var label = StringProp(element, "label") ??
                        StringProp(element, "type") ??
                        StringProp(element, "role");
            if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(label))
                yield return new DoclingBlock(text, label, Page(element), TopY(element));

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    foreach (var block in ExtractBlocks(prop.Value))
                        yield return block;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var block in ExtractBlocks(item))
                    yield return block;
            }
        }
    }

    private static bool IsHeadingBlock(DoclingBlock block)
    {
        if (BadLabelRx.IsMatch(block.Label)) return false;
        return GoodLabelRx.IsMatch(block.Label);
    }

    private static DoclingBlock NormalizeBlock(DoclingBlock block) =>
        block with { Text = NormalizeSpace(block.Text), Label = block.Label.Trim().ToLowerInvariant() };

    private static List<HeadingRecord> AlignToDocx(IReadOnlyList<DoclingBlock> blocks, SlimDocument slim)
    {
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new DocxParagraphCanon(p, BuildCanon(p.Text)))
            .ToList();
        var result = new List<HeadingRecord>();
        var seen = new HashSet<(int Index, int Start, string Canon)>();
        var cursor = 0;

        foreach (var block in blocks)
        {
            var (needleCanon, _) = BuildCanon(block.Text);
            if (needleCanon.Length < 3) continue;

            var match = FindCanonSubstring(paragraphs, needleCanon, cursor);
            if (match is null && cursor > 0)
                match = FindCanonSubstring(paragraphs, needleCanon, 0);
            if (match is null) continue;

            var clean = CleanTitle(match.Value.Text);
            if (!seen.Add((match.Value.Paragraph.Index, match.Value.Start, Canon(clean)))) continue;

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = LevelFor(block, clean),
                Text = clean,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = "docling:" + block.Label,
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.90,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Value.Paragraph.Index;
        }

        return result.OrderBy(h => h.Index).ThenBy(h => h.HeadingSpan?.Start ?? 0).ToList();
    }

    private static MatchResult? FindCanonSubstring(
        IReadOnlyList<DocxParagraphCanon> paragraphs,
        string needleCanon,
        int minIndex)
    {
        foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
        {
            var at = p.Canon.Text.IndexOf(needleCanon, StringComparison.Ordinal);
            if (at < 0) continue;
            var start = p.Canon.SourceOffsets[at];
            var end = p.Canon.SourceOffsets[at + needleCanon.Length - 1] + 1;
            return new MatchResult(p.Paragraph, p.Paragraph.Text[start..end], start, end);
        }

        return null;
    }

    private static int LevelFor(DoclingBlock block, string text)
    {
        if (RomanPartRx.IsMatch(text)) return 1;
        var marker = NumberedMarkerRx.Match(text);
        if (marker.Success)
        {
            var n = marker.Groups["n"].Value;
            if (Regex.IsMatch(n, @"^\d"))
                return Math.Clamp(n.Count(c => c == '.') + 1, 1, 9);
            if (Regex.IsMatch(n, @"^[A-Z]\.\d"))
                return 2;
        }

        return block.Label.Contains("title", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static bool LooksLikeHeadingText(string text)
    {
        if (text.Length is < 3 or > 180) return false;
        if (!text.Any(char.IsLetter)) return false;
        if (text.Count(c => c == '.') >= 4 && !NumberedMarkerRx.IsMatch(text)) return false;
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+").Count;
        return words <= 22 || NumberedMarkerRx.IsMatch(text) || RomanPartRx.IsMatch(text);
    }

    private static string CleanTitle(string text) =>
        NormalizeSpace(text.Replace('–', '-').Replace('—', '-')).Trim(' ', '.', '\t');

    private static string NormalizeSpace(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    private static string Canon(string text) => NonAlphaNumRx.Replace(text.ToLowerInvariant(), "");

    private static (string Text, List<int> SourceOffsets) BuildCanon(string text)
    {
        var chars = new List<char>(text.Length);
        var offsets = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            chars.Add(char.ToLowerInvariant(c));
            offsets.Add(i);
        }

        return (new string(chars.ToArray()), offsets);
    }

    private static string? StringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? Page(JsonElement element)
    {
        if (IntProp(element, "page_no") is { } pageNo) return pageNo;
        if (IntProp(element, "page") is { } page) return page;
        if (!element.TryGetProperty("prov", out var prov)) return null;
        if (prov.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prov.EnumerateArray())
                if (IntProp(item, "page_no") is { } p) return p;
        }
        else if (prov.ValueKind == JsonValueKind.Object && IntProp(prov, "page_no") is { } p)
        {
            return p;
        }

        return null;
    }

    private static double? TopY(JsonElement element)
    {
        if (DoubleProp(element, "top") is { } top) return top;
        if (element.TryGetProperty("bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Object)
            return DoubleProp(bbox, "t") ?? DoubleProp(bbox, "top") ?? DoubleProp(bbox, "y1");
        if (!element.TryGetProperty("prov", out var prov)) return null;
        if (prov.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prov.EnumerateArray())
                if (TopY(item) is { } y) return y;
        }
        else if (prov.ValueKind == JsonValueKind.Object)
        {
            return TopY(prov);
        }

        return null;
    }

    private static int? IntProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var i) => i,
            _ => null,
        };
    }

    private static double? DoubleProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(prop.GetString(), out var d) => d,
            _ => null,
        };
    }

    private sealed record DoclingBlock(string Text, string Label, int? Page, double? TopY);
    private sealed record DocxParagraphCanon(SlimParagraph Paragraph, (string Text, List<int> SourceOffsets) Canon);
    private readonly record struct MatchResult(SlimParagraph Paragraph, string Text, int Start, int End);
}
