using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Sources.Docx.Reader;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

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
    public const string FactsVersion = "DOCX_AGENT_LINE_FACTS_V3";
    /// <summary>What the facts of a paragraph hold, as the facts tool tells the model.</summary>
    public const string FactsDescription = "for every paragraph in the range: style (id, name, outlineLevel, sizePt, bold, italic, underline, allCaps, alignment), numbering (id, level, label, format), "
        + "text (chars, boldCharRatio, lineBreaks and, when the paragraph breaks lines, lineBreakOffsets; when its formatting changes inside the paragraph, formatRunCount and formatChangeOffsets; "
        + "offsets are UTF-16 code units of the paragraph's text, in which a line break is one space; the pieces between line breaks and the exact formatRuns are in get_source_span), "
        + "place (inTable, inTextBox, and tableDepth, tableRow, tableCell inside a table), internalLinks (the bookmark names of its internal hyperlinks), "
        + "styleInDocument (rank: the place of its style among the distinct styles of the whole document, 1 being the largest, bold before regular at the same size; linesWithThisStyleInDocument), "
        + "neighbours (whether the paragraphs before and after share its style), sizeVsDocumentMedianPt (its font size minus the median font size of the document) "
        + "and repetition (otherOccurrences: how many other paragraphs have the same text under the stated normalization, and sampleAliases: the first two; get_repeated_occurrences and compare_source_locations read them)";
    /// <summary>Paragraphs decided per segment, and the paragraphs shown on each side as context.</summary>
    public const int Unit = 48, Overlap = 20;
    public const int MaxStylesShown = 15, StyleExamples = 3, StyleExampleChars = 80;
    /// <summary>At most this many pieces and format runs per paragraph, each piece's text clipped to <see cref="PieceChars"/> (its offsets stay exact).</summary>
    public const int MaxPieces = 6, MaxFormatRuns = 8, PieceChars = 120;
    /// <summary>The compact facts of a paragraph show at most this many line-break and format-change offsets (the full record has them all).</summary>
    public const int MaxCompactOffsets = 8, MaxFullOffsets = 64;
    /// <summary>A segment is cut by the size of the compact facts of its paragraphs (each as the tool writes it, repetition summary included): at most
    /// <see cref="Unit"/> paragraphs and at most this many bytes of them, never fewer than one paragraph. What the facts tool sends adds the envelope of its
    /// results (about 1.7 KB each), so the core facts of a window arrive in about 32 KB of tool messages, the target that leaves the rest of the unit's
    /// 64 KB for detail.</summary>
    public const int CoreFactsBytes = 28000;

    DocxAgentSource(string sourceSha, string universeSha, string storeSha, IReadOnlyList<DocxAgentLine> paragraphs,
        IReadOnlyDictionary<string, JsonObject> facts, JsonObject styleTable)
    {
        SourceSha256 = sourceSha; SourceAliasUniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Paragraphs = paragraphs;
        Lines = paragraphs.Select(p => new AgentSourceLine(p.Alias, p.Ordinal, null, p.Text, null, null)).ToArray();
        Facts = facts; StyleTable = styleTable;
        Cores = CoresOf(CompactSizes(paragraphs, facts));
    }

    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    /// <summary>The paragraphs with their OOXML identity and parsed paragraph, in reading order.</summary>
    public IReadOnlyList<DocxAgentLine> Paragraphs { get; }
    /// <summary>The paragraphs as the agent's conversation reads them: alias, order and text; a paragraph has no page, box or typography.</summary>
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    public IAgentFormat Flavor => DocxFormat.Instance;
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

    /// <summary>Reads the DOCX with the production reader. Null with a failure code when it is not a DOCX package, when its pages are images
    /// with next to no OOXML text (SCANNED_IMAGE_ONLY: its content needs OCR, and reading it would decide on a signature block), or when it has no text.</summary>
    public static DocxAgentSource? Load(string docxPath, ExtractionOptions? options, out string failure)
    {
        SourceDocument document;
        DocxSourceIntegrity integrity;
        try
        {
            integrity = DocxSourceIntegrity.Read(docxPath);
            if (integrity.Kind == DocxSourceKind.ScannedImageOnly) { failure = "SCANNED_IMAGE_ONLY"; return null; }
            document = new OpenXmlDocumentSource(options).Read(docxPath);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or FileFormatException)
        { failure = "NOT_A_DOCX_PACKAGE"; return null; }
        var source = From(document, out failure);
        if (source is not null) source.Integrity = integrity;
        return source;
    }

    /// <summary>What the package carries its content as (native text, or native text with page images), when the source was loaded from a file.</summary>
    public DocxSourceIntegrity? Integrity { get; internal set; }

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

    /// <summary>The bytes of each paragraph's entry in a facts result: its compact facts, its alias, and the repetition summary when its text occurs
    /// elsewhere (counted from the same normalization the tool uses).</summary>
    static int[] CompactSizes(IReadOnlyList<DocxAgentLine> paragraphs, IReadOnlyDictionary<string, JsonObject> facts)
    {
        var keys = paragraphs.Select(p => Evidence.RepeatKey(p.Text)).ToArray();
        var counts = keys.Where(k => k.Length > 0).GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Enumerable.Range(0, paragraphs.Count).Select(i =>
        {
            var size = DocxLineFacts.CompactBytes(facts[paragraphs[i].Alias], paragraphs[i].Alias);
            if (keys[i].Length > 0 && counts[keys[i]] - 1 is var others and > 0)
                size += DocxLineFacts.RepetitionBytes(others, paragraphs[i].Alias.Length);
            return size;
        }).ToArray();
    }

    /// <summary>
    /// A partition of the document into windows of consecutive paragraphs: each takes paragraphs while it holds fewer than <see cref="Unit"/> and the
    /// compact facts of its paragraphs fit <see cref="CoreFactsBytes"/> (a paragraph that alone exceeds it is a window of its own). An ordinary document
    /// is cut into windows of <see cref="Unit"/>, as before; only a window of unusually large paragraphs is cut shorter. Every paragraph is in exactly
    /// one window, and the context shown with a window crosses its edges, so no heading is lost at a boundary.
    /// </summary>
    internal static IReadOnlyList<(int Start, int End)> CoresOf(IReadOnlyList<int> compactBytes)
    {
        var cores = new List<(int, int)>();
        int start = 0, bytes = 0;
        for (var i = 0; i < compactBytes.Count; i++)
        {
            var size = compactBytes[i];
            if (i > start && (i - start >= Unit || bytes + size > CoreFactsBytes)) { cores.Add((start, i)); start = i; bytes = 0; }
            bytes += size;
        }
        if (compactBytes.Count > start) cores.Add((start, compactBytes.Count));
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

    /// <summary>
    /// The facts of a paragraph as get_line_facts shows them: the full facts with the text detail cut down to what tells the model whether to ask for
    /// more. The line breaks keep their offsets (at most <see cref="DocxAgentSource.MaxCompactOffsets"/>), the formatting keeps its number of runs and
    /// the offsets where it changes; the pieces between the breaks and the exact format runs stay in the full record that get_source_span returns.
    /// Nothing is dropped by what a paragraph says, only by how much of the same measurement is shown.
    /// </summary>
    internal static JsonObject Compact(JsonObject full)
    {
        var o = (JsonObject)full.DeepClone();
        o.Remove("alias");
        var text = (JsonObject)o["text"]!;
        if (text["lineBreakOffsets"] is JsonArray breaks && breaks.Count > DocxAgentSource.MaxCompactOffsets)
        {
            var kept = breaks.Take(DocxAgentSource.MaxCompactOffsets).Select(b => b!.DeepClone()).ToArray();
            text["lineBreakOffsets"] = new JsonArray(kept);
            text["lineBreakOffsetsNotShown"] = breaks.Count - kept.Length + (text["lineBreakOffsetsNotShown"]?.GetValue<int>() ?? 0);
        }
        text.Remove("pieces");
        if (text["formatRuns"] is JsonArray runs)
        {
            var total = runs.Count + (text["formatRunsNotShown"]?.GetValue<int>() ?? 0);
            var changes = runs.Skip(1).Take(DocxAgentSource.MaxCompactOffsets).Select(r => (JsonNode)r!["start"]!.GetValue<int>()).ToArray();
            text.Remove("formatRuns");
            text.Remove("formatRunsNotShown");
            text["formatRunCount"] = total;
            text["formatChangeOffsets"] = new JsonArray(changes);
        }
        return o;
    }

    /// <summary>The size in bytes of a paragraph's entry in a facts result without its repetition summary: the compact facts, the alias key and a comma.</summary>
    internal static int CompactBytes(JsonObject full, string alias) => Compact(full).ToJsonString().Length + alias.Length + 12;

    /// <summary>The size of the repetition summary of a paragraph whose text occurs in <paramref name="others"/> other paragraphs.</summary>
    internal static int RepetitionBytes(int others, int aliasLength) =>
        ("\"repetition\":{\"otherOccurrences\":" + others + ",\"sampleAliases\":[]},").Length + Math.Min(2, others) * (aliasLength + 3);

    /// <summary>The text of a paragraph as measured: its length, its bold share and its line breaks; where it breaks lines, the offsets of the
    /// breaks and the pieces between them; where its run formatting changes, the stretches of the same formatting. Offsets only, never a label:
    /// which part is a heading is for the model to judge.</summary>
    internal static JsonObject TextFacts(SourceParagraph p)
    {
        var o = new JsonObject { ["chars"] = p.Text.Length, ["boldCharRatio"] = BoldCharRatio(p), ["lineBreaks"] = p.LineBreakOffsets.Count };
        if (p.LineBreakOffsets.Count > 0)
        {
            var breaks = p.LineBreakOffsets.Where(b => b >= 0 && b < p.Text.Length).Order().ToArray();
            o["lineBreakOffsets"] = new JsonArray(breaks.Take(DocxAgentSource.MaxFullOffsets).Select(b => (JsonNode)b).ToArray());
            if (breaks.Length > DocxAgentSource.MaxFullOffsets) o["lineBreakOffsetsNotShown"] = breaks.Length - DocxAgentSource.MaxFullOffsets;
            var pieces = new JsonArray();
            var start = 0;
            foreach (var end in breaks.Append(p.Text.Length))
            {
                if (pieces.Count == DocxAgentSource.MaxPieces) break;
                if (end > start) pieces.Add(Piece(p.Text, start, end));
                start = end + 1;   // the break itself is one space in the text
            }
            o["pieces"] = pieces;
        }
        var runs = FormatRuns(p);
        if (runs.Count > 1)
        {
            o["formatRuns"] = new JsonArray(runs.Take(DocxAgentSource.MaxFormatRuns).Select(r => (JsonNode)new JsonObject
            {
                ["start"] = r.Start, ["end"] = r.End, ["bold"] = r.Bold, ["italic"] = r.Italic, ["underline"] = r.Underline,
            }).ToArray());
            if (runs.Count > DocxAgentSource.MaxFormatRuns) o["formatRunsNotShown"] = runs.Count - DocxAgentSource.MaxFormatRuns;
        }
        return o;
    }

    static JsonObject Piece(string text, int start, int end)
    {
        var piece = new JsonObject { ["start"] = start, ["end"] = end };
        if (end - start <= DocxAgentSource.PieceChars) piece["text"] = text[start..end];
        else { piece["text"] = text.Substring(start, DocxAgentSource.PieceChars); piece["textTruncated"] = true; }
        return piece;
    }

    /// <summary>The run spans merged into stretches of the same bold, italic and underline, over characters that are not white space at either
    /// end of a stretch; a gap between spans (a line break, a tab) joins the stretch around it when both sides agree.</summary>
    static List<(int Start, int End, bool Bold, bool Italic, bool Underline)> FormatRuns(SourceParagraph p)
    {
        var result = new List<(int Start, int End, bool Bold, bool Italic, bool Underline)>();
        foreach (var span in p.TextSpans.Where(t => t.End > t.Start).OrderBy(t => t.Start))
        {
            int start = Math.Max(0, span.Start), end = Math.Min(p.Text.Length, span.End);
            while (start < end && char.IsWhiteSpace(p.Text[start])) start++;
            while (end > start && char.IsWhiteSpace(p.Text[end - 1])) end--;
            if (end <= start) continue;
            if (result.Count > 0 && result[^1] is var last && (last.Bold, last.Italic, last.Underline) == (span.Bold, span.Italic, span.Underline)
                && (start <= last.End || p.Text[last.End..start].All(char.IsWhiteSpace)))
                result[^1] = (last.Start, Math.Max(last.End, end), last.Bold, last.Italic, last.Underline);
            else result.Add((start, end, span.Bold, span.Italic, span.Underline));
        }
        return result;
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
            node["text"] = TextFacts(p);
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
