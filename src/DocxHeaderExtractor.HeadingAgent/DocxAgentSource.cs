using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>One paragraph of a DOCX as the agent sees it: the alias the route's source snapshot gives it, its OOXML identity, reading order, text
/// and the parser's paragraph (style, numbering, formatting).</summary>
internal sealed record DocxAgentLine(string Alias, string SourceId, int Ordinal, string Text, SourceParagraph Paragraph);

/// <summary>
/// The DOCX source of one agent run, the counterpart of <see cref="AgentSource"/> for a PDF: the paragraphs in reading order under the aliases of
/// the route's source snapshot (so that a selector's aliases agree with the shared binder), the identity hashes, the segments the document is cut
/// into and the facts of every paragraph. DOCX has no pages, so a segment is a fixed window of paragraphs.
/// <para>
/// Every fact is an observation read from the OOXML by the existing reader (style, outline level, numbering, run formatting, hyperlinks) or from
/// the paragraph's address in the XML (inside a table, a table cell, a text box), or a comparison with the rest of the document (how often its
/// style is used, whether the neighbours share it). No fact names a kind of paragraph, and none reads the text for a word.
/// </para>
/// </summary>
internal sealed class DocxAgentSource : IAgentSource
{
    public const string Version = "DOCX_AGENT_SOURCE_V1";
    public const string StylesVersion = "DOCX_AGENT_DOCUMENT_STYLES_V1";
    public const string FactsVersion = "DOCX_AGENT_LINE_FACTS_V1";
    /// <summary>What the facts of a paragraph hold, as the facts tool tells the model.</summary>
    public const string FactsDescription = "for every paragraph in the range: style (id, name, outlineLevel, sizePt, bold, italic, underline, allCaps, alignment), numbering (id, level, label, format), "
        + "text (chars, boldCharRatio, lineBreaks), place (inTable, inTextBox, and tableDepth, tableRow, tableCell inside a table), internalLinks (the bookmark names of its internal hyperlinks), "
        + "styleInDocument (rank: the place of its style among the distinct styles of the whole document, 1 being the largest, bold before regular at the same size; linesWithThisStyleInDocument), "
        + "neighbours (whether the paragraphs before and after share its style) and sizeVsDocumentMedianPt (its font size minus the median font size of the document)";
    /// <summary>Paragraphs decided per segment, and the paragraphs shown on each side as context.</summary>
    public const int Unit = 48, Overlap = 20;
    public const int MaxStylesShown = 15, StyleExamples = 3, StyleExampleChars = 80;

    DocxAgentSource(string sourceSha, string universeSha, string storeSha, IReadOnlyList<DocxAgentLine> paragraphs,
        IReadOnlyDictionary<string, JsonObject> facts, JsonObject styleTable)
    {
        SourceSha256 = sourceSha; SourceAliasUniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Paragraphs = paragraphs;
        Lines = paragraphs.Select(p => new AgentSourceLine(p.Alias, p.Ordinal, null, p.Text, null, null)).ToArray();
        Cores = CoresOf(paragraphs.Count);
        Facts = facts; StyleTable = styleTable;
    }

    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    /// <summary>The paragraphs with their OOXML identity and parsed paragraph, in reading order.</summary>
    public IReadOnlyList<DocxAgentLine> Paragraphs { get; }
    /// <summary>The paragraphs as the agent's conversation reads them: alias, order and text; a paragraph has no page, box or typography.</summary>
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    public IAgentFlavor Flavor => DocxAgentFlavor.Instance;
    /// <summary>The decided range of each segment, [Start, End) into <see cref="Lines"/>, covering every paragraph once.</summary>
    public IReadOnlyList<(int Start, int End)> Cores { get; }
    public IReadOnlyDictionary<string, JsonObject> Facts { get; }
    /// <summary>The document style table: the distinct styles of the whole document, with how many paragraphs use each.</summary>
    public JsonObject StyleTable { get; }

    /// <summary>The targets of one segment and the paragraphs shown with them (<see cref="Overlap"/> on each side), as line indices.</summary>
    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        var (core, end) = Cores[segment];
        var from = Math.Max(0, core - Overlap);
        return (Enumerable.Range(core, end - core).Select(i => Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(Lines.Count, end + Overlap) - from).ToArray());
    }

    /// <summary>Reads the DOCX with the production reader. Null with a failure code when it has no text or is not a DOCX package.</summary>
    public static DocxAgentSource? Load(string docxPath, ExtractionOptions? options, out string failure)
    {
        SourceDocument document;
        try { document = new OpenXmlDocumentSource(options).Read(docxPath); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or FileFormatException)
        { failure = "NOT_A_DOCX_PACKAGE"; return null; }
        return From(document, out failure);
    }

    /// <summary>Builds the source from an already read document, under the aliases of the route's source snapshot.</summary>
    public static DocxAgentSource? From(SourceDocument document, out string failure)
    {
        var built = DocxSourceAdapter.Build(document);
        var snapshot = built.Snapshot;
        var occurrences = snapshot.Occurrences.OrderBy(o => o.Ordinal).ThenBy(o => o.Alias, StringComparer.Ordinal).ToArray();
        if (occurrences.Length == 0) { failure = "EMPTY_UNIVERSE"; return null; }
        if (occurrences.Select(o => o.Alias).Distinct(StringComparer.Ordinal).Count() != occurrences.Length) { failure = "DUPLICATE_ALIAS"; return null; }
        var lines = new List<DocxAgentLine>(occurrences.Length);
        foreach (var o in occurrences)
        {
            if (!built.Contexts.TryGetValue(o.Id, out var context)) { failure = "PARAGRAPH_NOT_IN_SOURCE"; return null; }
            lines.Add(new DocxAgentLine(o.Alias, o.Id, o.Ordinal, o.Text, context.Source));
        }
        failure = "OK";
        var styles = DocxLineFacts.Styles(lines);
        var facts = DocxLineFacts.Compute(lines, styles);
        return new DocxAgentSource(snapshot.SourceSha256, snapshot.SourceAliasUniverseHash,
            StoreSha(snapshot.SourceSha256, snapshot.SourceAliasUniverseHash, lines, facts), lines, facts, DocxLineFacts.DocumentStyles(lines, styles));
    }

    /// <summary>The evidence store hash: over the version, the identity hashes and, per paragraph, its alias, the hash of its OOXML address, its
    /// reading order, its text and its facts.</summary>
    static string StoreSha(string sourceSha, string universeSha, IReadOnlyList<DocxAgentLine> lines, IReadOnlyDictionary<string, JsonObject> facts)
    {
        var entries = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
        {
            ["alias"] = l.Alias, ["sourceIdSha256"] = Hash(Encoding.UTF8.GetBytes(l.SourceId)), ["ordinal"] = l.Ordinal, ["text"] = l.Text,
            ["facts"] = JsonNode.Parse(facts[l.Alias].ToJsonString()),
        }).ToArray());
        var store = new JsonObject
        {
            ["protocolVersion"] = Version, ["factsVersion"] = FactsVersion, ["sourceSha256"] = sourceSha, ["sourceAliasUniverseSha256"] = universeSha, ["entries"] = entries,
        };
        return Hash(Encoding.UTF8.GetBytes(store.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Fixed windows of <see cref="Unit"/> paragraphs (the last one shorter): a partition of the document.</summary>
    static IReadOnlyList<(int Start, int End)> CoresOf(int count)
    {
        var cores = new List<(int, int)>();
        for (var s = 0; s < count; s += Unit) cores.Add((s, Math.Min(count, s + Unit)));
        return cores;
    }
}

/// <summary>
/// The facts of every paragraph of a DOCX and its document style table. Observations only: what the OOXML says about the paragraph, where it
/// stands in the XML, and how it compares with the paragraphs around it and with the whole document.
/// </summary>
internal static class DocxLineFacts
{
    /// <summary>A style as the document table counts it: style name (else id), size rounded to half a point, weight. Not the all-caps flag: the
    /// reader also sets it from the text of a paragraph, so it says something about that paragraph, not about its style.</summary>
    public readonly record struct Style(string Name, double? SizePt, bool Bold);

    static Style StyleOf(SourceParagraph p) => new(
        p.Style.StyleName ?? p.Style.StyleId ?? "(none)",
        p.Style.FontSizePt is { } s ? Math.Round(s * 2, MidpointRounding.AwayFromZero) / 2 : null,
        p.Style.Bold);

    /// <summary>The distinct styles, largest size first (no size last), bold before regular, then name; rank 1 is the first.</summary>
    public static IReadOnlyList<(Style Style, int Rank, int[] Lines)> Styles(IReadOnlyList<DocxAgentLine> lines) =>
        Enumerable.Range(0, lines.Count).GroupBy(i => StyleOf(lines[i].Paragraph))
            .OrderByDescending(g => g.Key.SizePt ?? double.MinValue).ThenByDescending(g => g.Key.Bold)
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .Select((g, k) => (g.Key, k + 1, g.ToArray())).ToList();

    public static JsonObject DocumentStyles(IReadOnlyList<DocxAgentLine> lines, IReadOnlyList<(Style Style, int Rank, int[] Lines)> styles)
    {
        static string Clip(string t) => t.Length <= DocxAgentSource.StyleExampleChars ? t : t[..DocxAgentSource.StyleExampleChars] + "…";
        return new JsonObject
        {
            ["readBy"] = "the program, from the OOXML of every paragraph of the whole document: each distinct style (style name, font size rounded to half a point, bold), largest size first, bold before regular at the same size, with how many paragraphs use it and the first paragraphs in reading order set in it",
            ["version"] = DocxAgentSource.StylesVersion,
            ["documentLines"] = lines.Count,
            ["stylesTotal"] = styles.Count,
            ["styles"] = new JsonArray(styles.Take(DocxAgentSource.MaxStylesShown).Select(s =>
            {
                var o = new JsonObject { ["rank"] = s.Rank, ["styleName"] = s.Style.Name };
                if (s.Style.SizePt is { } size) o["sizePt"] = size;
                o["bold"] = s.Style.Bold; o["lines"] = s.Lines.Length;
                o["firstLines"] = new JsonArray(s.Lines.Take(DocxAgentSource.StyleExamples).Select(i => (JsonNode)new JsonObject { ["alias"] = lines[i].Alias, ["text"] = Clip(lines[i].Text) }).ToArray());
                return (JsonNode)o;
            }).ToArray()),
            ["stylesNotShown"] = Math.Max(0, styles.Count - DocxAgentSource.MaxStylesShown),
        };
    }

    static double R(double x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    /// <summary>Where the paragraph stands in the XML, read from its address (the walker's path, for example body[1]/tbl[2]/tr[1]/tc[3]/p[1]):
    /// the table nesting depth, the innermost table's row and cell, and whether it is inside a text box.</summary>
    internal static (int TableDepth, int? Row, int? Cell, bool InTextBox) Place(string sourceId)
    {
        int depth = 0; int? row = null, cell = null; var box = false;
        foreach (var part in sourceId.Split('/'))
        {
            var open = part.IndexOf('[');
            if (open < 0 || !part.EndsWith(']') || !int.TryParse(part[(open + 1)..^1], out var n)) continue;
            switch (part[..open])
            {
                case "tbl": depth++; row = null; cell = null; break;
                case "tr": row = n; break;
                case "tc": cell = n; break;
                case "txbxContent": box = true; break;
            }
        }
        return (depth, row, cell, box);
    }

    /// <summary>The share of the paragraph's characters set in bold (from its run spans; the paragraph style's weight when it has none).</summary>
    static double BoldCharRatio(SourceParagraph p)
    {
        if (p.Text.Length == 0) return 0;
        if (p.TextSpans.Count == 0) return p.Style.Bold ? 1 : 0;
        var bold = p.TextSpans.Where(s => s.Bold).Sum(s => Math.Max(0, Math.Min(s.End, p.Text.Length) - Math.Max(0, s.Start)));
        return Math.Round(Math.Min(1.0, (double)bold / p.Text.Length), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The facts of every paragraph, by alias.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Compute(IReadOnlyList<DocxAgentLine> lines, IReadOnlyList<(Style Style, int Rank, int[] Lines)> styles)
    {
        var rank = new Dictionary<int, (int Rank, int Lines)>();
        foreach (var s in styles) foreach (var i in s.Lines) rank[i] = (s.Rank, s.Lines.Length);
        var sizes = lines.Select(l => l.Paragraph.Style.FontSizePt).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
        double? median = sizes.Length == 0 ? null : sizes.Length % 2 == 1 ? sizes[sizes.Length / 2] : (sizes[sizes.Length / 2 - 1] + sizes[sizes.Length / 2]) / 2;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var p = lines[i].Paragraph;
            var style = new JsonObject();
            if (p.Style.StyleId is not null) style["id"] = p.Style.StyleId;
            if (p.Style.StyleName is not null) style["name"] = p.Style.StyleName;
            if (p.Style.OutlineLevel is { } outline) style["outlineLevel"] = outline;
            if (p.Style.FontSizePt is { } size) style["sizePt"] = R(size);
            style["bold"] = p.Style.Bold; style["italic"] = p.Style.Italic; style["underline"] = p.Style.Underline; style["allCaps"] = p.Style.AllCaps;
            if (p.Style.Alignment is not null) style["alignment"] = p.Style.Alignment;
            var node = new JsonObject { ["alias"] = lines[i].Alias, ["style"] = style };
            if (p.Numbering.NumberingId is not null || p.Numbering.NumberLabel is not null)
            {
                var numbering = new JsonObject();
                if (p.Numbering.NumberingId is { } id) numbering["id"] = id;
                if (p.Numbering.NumberingLevel is { } level) numbering["level"] = level;
                if (p.Numbering.NumberLabel is not null) numbering["label"] = p.Numbering.NumberLabel;
                if (p.Numbering.NumberingFormat is not null) numbering["format"] = p.Numbering.NumberingFormat;
                node["numbering"] = numbering;
            }
            node["text"] = new JsonObject { ["chars"] = p.Text.Length, ["boldCharRatio"] = BoldCharRatio(p), ["lineBreaks"] = p.LineBreakOffsets.Count };
            var (depth, row, cell, box) = Place(lines[i].SourceId);
            var place = new JsonObject { ["inTable"] = depth > 0, ["inTextBox"] = box };
            if (depth > 0) { place["tableDepth"] = depth; if (row is { } r) place["tableRow"] = r; if (cell is { } c) place["tableCell"] = c; }
            node["place"] = place;
            if (p.HyperlinkAnchors.Count > 0) node["internalLinks"] = new JsonArray(p.HyperlinkAnchors.Take(3).Select(a => (JsonNode)a).ToArray());
            if (rank.TryGetValue(i, out var sr)) node["styleInDocument"] = new JsonObject { ["rank"] = sr.Rank, ["linesWithThisStyleInDocument"] = sr.Lines };
            var around = new JsonObject();
            if (i > 0) around["sameStyleAsPrevious"] = StyleOf(lines[i - 1].Paragraph) == StyleOf(p);
            if (i + 1 < lines.Count) around["sameStyleAsNext"] = StyleOf(lines[i + 1].Paragraph) == StyleOf(p);
            if (around.Count > 0) node["neighbours"] = around;
            if (p.Style.FontSizePt is { } fs && median is { } med) node["sizeVsDocumentMedianPt"] = R(fs - med);
            result[lines[i].Alias] = node;
        }
        return result;
    }
}
