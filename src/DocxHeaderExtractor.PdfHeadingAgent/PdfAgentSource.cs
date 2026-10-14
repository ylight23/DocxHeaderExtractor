using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.PdfHeadingAgent;

/// <summary>One parser line as the agent sees it: alias, reading order, page, text, and the parser's bbox and typography (observations).</summary>
internal sealed record AgentSourceLine(string Alias, int Ordinal, int? Page, string Text, JsonElement? Bbox, JsonElement? Typography);

/// <summary>
/// The PDF source of one agent run: the parser's lines in reading order, the identity hashes every tool result carries, the parser layout block
/// of each line, the segments (cores) the document is cut into and the layout facts (V3) of every line. Ported from the qualified V6 universe
/// (P7F1V6Universe, P7F1QEvidenceTools.FromStore, PdfSourceEvidenceStore, P7F1QLayoutPacking, layout segmentation LAYOUT_PAGE_BLOCK_48_V1); the
/// parity tests hold the two to the same bytes.
/// </summary>
internal sealed class AgentSource
{
    public const int Unit = 48, Overlap = 20;

    AgentSource(string sourceSha, string universeSha, string storeSha, IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks)
    {
        SourceSha256 = sourceSha; SourceAliasUniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Lines = lines; Blocks = blocks;
        Cores = CoresOf(lines, blocks);
        LayoutFacts = AgentLayoutFacts.Compute(lines, blocks);
    }

    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    /// <summary>The parser layout block of each line, parallel to <see cref="Lines"/>.</summary>
    public IReadOnlyList<string> Blocks { get; }
    /// <summary>The decided range of each segment, [Start, End) into <see cref="Lines"/>, covering every line once.</summary>
    public IReadOnlyList<(int Start, int End)> Cores { get; }
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> LayoutFacts { get; }

    public static string SegmentId(int n) => $"SEG-{n:000}";

    /// <summary>The targets of one segment and the lines shown with them (20 lines of context each side), as line indices.</summary>
    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        var (core, end) = Cores[segment];
        var from = Math.Max(0, core - Overlap);
        return (Enumerable.Range(core, end - core).Select(i => Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(Lines.Count, end + Overlap) - from).ToArray());
    }

    /// <summary>Parses the PDF with the production parser. Null with a failure code when the file has no text layer or the parse is not stable.</summary>
    public static AgentSource? Load(string pdfPath, out string failure)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(pdfPath);
        var source = parsed.Snapshot;
        if (source.Atoms.Count == 0) { failure = "EMPTY_UNIVERSE"; return null; }
        var (storeSha, rows) = EvidenceStore.Build(source, parsed.Details);
        if (!string.Equals(source.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdfPath))), StringComparison.Ordinal))
        { failure = "SOURCE_CHANGED_DURING_PARSE"; return null; }
        var lines = rows.OrderBy(l => l.Ordinal).ThenBy(l => l.Alias, StringComparer.Ordinal).ToArray();
        if (lines.Select(l => l.Alias).Distinct(StringComparer.Ordinal).Count() != lines.Length) { failure = "DUPLICATE_ALIAS"; return null; }
        var blockByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in source.Evidence) blockByAlias.TryAdd(e.SourceAlias, parsed.Details.LayoutBlockByAtom.GetValueOrDefault(e.SourceId) ?? "NO_BLOCK:" + e.SourceId);
        var blocks = lines.Select(l => blockByAlias.GetValueOrDefault(l.Alias) ?? "NO_BLOCK:" + l.Alias).ToArray();
        failure = "OK";
        return new AgentSource(source.SourceSha256, source.SourceAliasUniverseHash, storeSha, lines, blocks);
    }

    /// <summary>LAYOUT_PAGE_BLOCK_48_V1: whole pages when they fit in 48 lines, else parser layout blocks of the page, else 48-line slices;
    /// consecutive units are packed while the core stays within 48 lines. Only page numbers and parser block ids decide.</summary>
    static IReadOnlyList<(int Start, int End)> CoresOf(IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks)
    {
        int PageOf(int i) => lines[i].Page ?? 0;
        var units = new List<(int Start, int End)>();
        for (var s = 0; s < lines.Count;)
        {
            var e = s; while (e < lines.Count && PageOf(e) == PageOf(s)) e++;
            if (e - s <= Unit) { units.Add((s, e)); s = e; continue; }
            for (var b = s; b < e;)
            {
                var be = b; while (be < e && blocks[be] == blocks[b]) be++;
                for (var k = b; k < be; k += Unit) units.Add((k, Math.Min(be, k + Unit)));
                b = be;
            }
            s = e;
        }
        var cores = new List<(int, int)>();
        for (var u = 0; u < units.Count;)
        {
            var start = units[u].Start; var end = units[u].End; u++;
            while (u < units.Count && units[u].End - start <= Unit) { end = units[u].End; u++; }
            cores.Add((start, end));
        }
        var next = 0;
        foreach (var (a, b) in cores)
        {
            if (a != next || b <= a || b - a > Unit) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
            next = b;
        }
        if (next != lines.Count) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
        return cores;
    }
}

/// <summary>
/// The parser-owned evidence store (P7_RAW_SOURCE_EVIDENCE_STORE_V1): one entry per atom with its text, page, bbox, typography and reading
/// order, and the store's SHA-256 over its canonical bytes. Only the observations of the qualified spatial ledger are computed; its relation
/// facts never entered the store.
/// </summary>
internal static class EvidenceStore
{
    public const string Version = "P7_RAW_SOURCE_EVIDENCE_STORE_V1";
    public const string GeometryBasis = "PDF_LINE_GLYPH_UNION_BOUNDS_V1_PDF_POINTS_BOTTOM_LEFT";

    static readonly JsonSerializerOptions Canonical = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Canonical);
    static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Canonical);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed record Bounds(double Left, double Right, double Bottom, double Top);
    sealed record Observation(string Alias, int Ordinal, int? Page, Bounds? Bounds, string? MissingReason, string SourceIdSha256);
    sealed record Field(string Name, string Availability, JsonElement? Value, string Basis, string? MissingReason, string ValueSha256);
    sealed record Entry(string SourceAlias, string SourceIdSha256, int Ordinal, int SpanStart, int SpanEnd, IReadOnlyList<Field> Fields);

    public static (string StoreSha256, IReadOnlyList<AgentSourceLine> Lines) Build(DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (source.SourceKind != "pdf" || !IsHash(source.SourceSha256) || !IsHash(source.SourceAliasUniverseHash))
            throw new InvalidOperationException("spatial-source-identity-required");
        var atoms = source.Atoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        if (atoms.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count() != atoms.Length ||
            atoms.Select(atom => atom.SourceId).Distinct(StringComparer.Ordinal).Count() != atoms.Length)
            throw new InvalidOperationException("spatial-duplicate-source-identity");
        var blocks = details.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var entries = new List<Entry>();
        var lines = new List<AgentSourceLine>();
        foreach (var atom in atoms)
        {
            Bounds? bounds = null;
            string? missing = null;
            PdfLine? parserLine = null;
            if (!blocks.TryGetValue(atom.SourceId, out var block)) missing = "PARSER_LINE_NOT_AVAILABLE";
            else
            {
                if (block.Lines.Count != 1) throw new InvalidOperationException("spatial-source-not-single-parser-segment");
                var line = block.Lines[0];
                parserLine = line;
                if (PdfLineIdentity.Of(line) != atom.SourceId || line.Page != atom.Page || line.Projection.VerbatimText != atom.Text)
                    throw new InvalidOperationException("spatial-parser-source-mismatch");
                if (line.Top is null || line.Bottom is null) missing = "GLYPH_VERTICAL_BOUNDS_NOT_AVAILABLE";
                else if (!double.IsFinite(line.Left) || !double.IsFinite(line.Right) ||
                    !double.IsFinite(line.Top.Value) || !double.IsFinite(line.Bottom.Value) ||
                    line.Right <= line.Left || line.Top.Value <= line.Bottom.Value)
                    missing = "INVALID_OR_DEGENERATE_GLYPH_BOUNDS";
                else bounds = new(line.Left == 0 ? 0 : line.Left, line.Right == 0 ? 0 : line.Right,
                    line.Bottom.Value == 0 ? 0 : line.Bottom.Value, line.Top.Value == 0 ? 0 : line.Top.Value);
            }
            var observation = new Observation(atom.Alias, atom.Ordinal, atom.Page > 0 ? atom.Page : null, bounds, missing, Hash(Encoding.UTF8.GetBytes(atom.SourceId)));
            JsonElement? typography = parserLine is null ? null
                : Element(new { parserLine.FontSize, parserLine.FontName, parserLine.BoldRatio, parserLine.ItalicRatio, richTypography = parserLine.Typography });

            Field Make(string name, JsonElement? value, string basis, string? missingReason = null)
            {
                var availability = value is null ? "NOT_AVAILABLE" : "OBSERVED";
                var reason = value is null ? missingReason ?? "MEASUREMENT_NOT_AVAILABLE" : null;
                var hash = Hash(Bytes(new
                { source.SourceSha256, source.SourceAliasUniverseHash, sourceAlias = observation.Alias, name, value, availability, basis, reason }));
                return new(name, availability, value, basis, reason, hash);
            }
            var textValue = Element(atom.Text);
            JsonElement? pageValue = observation.Page is null ? null : Element(observation.Page);
            JsonElement? bboxValue = observation.Bounds is null ? null : Element(observation.Bounds);
            entries.Add(new Entry(observation.Alias, observation.SourceIdSha256, observation.Ordinal, 0, atom.Text.Length, new[]
            {
                Make("text", textValue, "CANONICAL_SOURCE_OCCURRENCE_TEXT_V1"),
                Make("page", pageValue, "PARSER_PAGE_NUMBER_V1", "PAGE_NOT_AVAILABLE"),
                Make("bbox", bboxValue, GeometryBasis, observation.MissingReason),
                Make("typography", typography, "PARSER_LINE_TYPOGRAPHY_SUMMARIES_V1", "PARSER_LINE_NOT_AVAILABLE"),
                Make("readingOrder", Element(observation.Ordinal), "SOURCE_OCCURRENCE_ORDINAL_V1_NOT_VISUAL_ROW_IDENTITY"),
                Make("glyphs", null, "NO_GLYPH_STORE_PROJECTION_V1", "GLYPH_RECORDS_NOT_PROJECTED"),
            }));
            lines.Add(new AgentSourceLine(atom.Alias, atom.Ordinal, observation.Page, atom.Text, bboxValue, typography));
        }
        var storeSha = Hash(Bytes(new { ProtocolVersion = Version, SourceSha256 = source.SourceSha256, SourceAliasUniverseSha256 = source.SourceAliasUniverseHash, Entries = entries }));
        return (storeSha, lines);
    }

    static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

/// <summary>The parser observations the tools and facts read (from the qualified P7F1QEvidenceTools): page statistics, the spatial relation of
/// two lines, compact layout, font size, bold ratio, edges, and the repeat normalization. Observations only; no label.</summary>
internal static class Evidence
{
    public const string RepeatNormalization = "LOWER_INVARIANT_DOT_LEADERS_REMOVED_TRAILING_PAGE_NUMBER_REMOVED_DIGITS_MASKED_WHITESPACE_COLLAPSED_V1";
    public const string LayoutBasis = "bbox=[left,right,bottom,top] in PDF points, bottom-left origin; font={size,name,boldRatio,italicRatio}; parser observations, not labels";
    public const double SameBaselineTolerance = 2.0;

    public static object PageStats(IReadOnlyList<AgentSourceLine> lines, int page)
    {
        var onPage = lines.Where(l => l.Page == page).ToArray();
        var boxes = onPage.Select(l => Box(l.Bbox)).Where(b => b is not null).Select(b => b!.Value).ToArray();
        var sizes = onPage.Select(l => FontSizeOf(l.Typography)).Where(s => s is not null).Select(s => s!.Value).Order().ToArray();
        return new
        {
            page, lineCount = onPage.Length,
            textExtent = boxes.Length == 0 ? null : new[] { R(boxes.Min(b => b.Left)), R(boxes.Max(b => b.Right)), R(boxes.Min(b => b.Bottom)), R(boxes.Max(b => b.Top)) },
            medianFontSize = sizes.Length == 0 ? (double?)null : R(sizes[sizes.Length / 2]),
        };
    }

    public static object RelationOf(AgentSourceLine target, AgentSourceLine other)
    {
        var a = Box(target.Bbox); var b = Box(other.Bbox);
        if (a is null || b is null || target.Page != other.Page) return new { samePage = target.Page == other.Page };
        double overlap = Math.Max(0, Math.Min(a.Value.Right, b.Value.Right) - Math.Max(a.Value.Left, b.Value.Left));
        double narrower = Math.Max(1e-6, Math.Min(a.Value.Right - a.Value.Left, b.Value.Right - b.Value.Left));
        return new
        {
            samePage = true,
            sameBaseline = Math.Abs(a.Value.Bottom - b.Value.Bottom) <= SameBaselineTolerance,
            verticalOffset = R(b.Value.Bottom - a.Value.Bottom),
            horizontalOverlapRatio = Math.Round(overlap / narrower, 2, MidpointRounding.AwayFromZero),
            fontSizeDelta = FontSizeOf(other.Typography) is { } so && FontSizeOf(target.Typography) is { } st ? R(so - st) : (double?)null,
        };
    }

    /// <summary>The compact layout of one line: rounded box and font facts, or null when the source has neither.</summary>
    public static object? CompactLayout(JsonElement? bbox, JsonElement? typography)
    {
        var b = Box(bbox);
        var f = Font(typography);
        if (b is null && f is null) return null;
        return new { bbox = b is null ? null : new { left = R(b.Value.Left), right = R(b.Value.Right), bottom = R(b.Value.Bottom), top = R(b.Value.Top) }, font = f };
    }

    public static double? FontSizeOf(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("fontSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
    public static double? Bold(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("boldRatio", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
    public static double? LeftEdgeOf(JsonElement? bbox) => Box(bbox)?.Left;
    public static double? RightEdgeOf(JsonElement? bbox) => Box(bbox)?.Right;

    public static string RepeatKey(string text)
    {
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"(?:\s*[.·…]){2,}", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\s+\d+$", "");
        s = Regex.Replace(s, @"\d", "#");
        return s.Trim();
    }

    /// <summary>The search normalization: diacritics and whitespace removed, đ as d, lower case.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return new string(sb.ToString().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    static (double Left, double Right, double Bottom, double Top)? Box(JsonElement? box) =>
        box is { ValueKind: JsonValueKind.Object } b
            ? (b.GetProperty("left").GetDouble(), b.GetProperty("right").GetDouble(), b.GetProperty("bottom").GetDouble(), b.GetProperty("top").GetDouble())
            : null;

    static object? Font(JsonElement? t)
    {
        if (t is not { ValueKind: JsonValueKind.Object } v) return null;
        double? N(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? R(x.GetDouble()) : null;
        return new
        {
            size = N("fontSize"), name = v.TryGetProperty("fontName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            boldRatio = N("boldRatio"), italicRatio = N("italicRatio"),
        };
    }
}
