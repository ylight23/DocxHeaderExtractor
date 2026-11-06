using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One readable unit of a source: a paragraph, a bullet, a heading's text, or a table cell's paragraph. <see cref="Id"/> is the source and the walker
/// location, so a proposal can name it and the checker can find it again. <see cref="Text"/> is the raw text as written; spans are offsets into it.
/// <see cref="HeadingPath"/> is the path the unit sits under, and <see cref="PathCertain"/> says whether that path is known; when it is not, the
/// path shown is the last one known. <see cref="RowLabel"/> and <see cref="LabelUnitId"/> name the first cell of the row, and
/// <see cref="ColumnHeader"/> and <see cref="HeaderUnitId"/> the first row of the column. These are a rule, not a fact about the table: a table may
/// put its labels elsewhere, so the checker flags a pairing that does not follow them rather than deciding it.
/// </summary>
public sealed record GatherUnit(
    string Id,
    string SourceId,
    string Location,
    string Container,
    bool IsBullet,
    bool IsHeading,
    string Text,
    string HeadingPath,
    string PathSource,
    bool PathCertain,
    string? TablePath,
    string? RowPath,
    bool CellMerged,
    string? RowLabel,
    string? LabelUnitId,
    string? ColumnHeader,
    string? HeaderUnitId,
    string KeyHash)
{
    /// <summary>
    /// For a heading unit, where the heading's own text ends in <see cref="Text"/>: the rest of the paragraph is content under that heading
    /// ("Opening: The meeting was called…"). Null when the unit is not a heading, or when the boundary could not be verified against the text.
    /// </summary>
    public int? HeadingEnd { get; init; }

    /// <summary>The content written in the heading's own paragraph after the heading, or empty.</summary>
    public string InlineBody => HeadingEnd is { } end && end < Text.Length ? Text[end..].Trim() : "";
}

/// <summary>
/// Something of a source that was not read in full, or a heading the pipeline gave that could not be matched to the file, with the place it is in when
/// that is known. A gap is reported with the result: a missing part must never look like a complete answer.
/// </summary>
public sealed record GatherGap(string SourceId, string Location, string Reason);

/// <summary>The units and gaps of one source document.</summary>
public sealed record GatherSource(IReadOnlyList<GatherUnit> Units, IReadOnlyList<GatherGap> Gaps);

/// <summary>
/// Reads a DOCX into units for gathering. Every paragraph with text is a unit, in reading order: body paragraphs, table cell paragraphs and text box
/// paragraphs. Headings are units too (their text can hold information), and they give the units after them their heading path.
/// <para>
/// Headings come from the pipeline's outline when it is given (<see cref="HeadingRecord"/>, matched by paragraph index and text); a heading whose level
/// is unknown leaves the path uncertain until the next known heading. Without an outline the Heading styles are used and the path is marked as read
/// from styles. Calls no model.
/// </para>
/// </summary>
public static class GatherUnits
{
    static readonly Regex TypedBullet = new(@"^\s*[-–•+*▪]\s+", RegexOptions.Compiled);
    static readonly Regex HeadingStyle = new(@"^Heading([1-9])$", RegexOptions.Compiled);
    static readonly Regex TableRe = new(@"^.*?/tbl\[\d+\]", RegexOptions.Compiled);
    static readonly Regex RowRe = new(@"^.*?/tr\[\d+\]", RegexOptions.Compiled);
    static readonly Regex CellRe = new(@"^.*?/tr\[\d+\]/tc\[\d+\]", RegexOptions.Compiled);
    static readonly Regex CellIdx = new(@"/tc\[(\d+)\]", RegexOptions.Compiled);
    static readonly Regex RowIdx = new(@"/tr\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>Regions the reader does not read in full. Text boxes are read (their paragraphs are units), so they are not listed here.</summary>
    static readonly string[] WholeFileGaps = ["drawing_or_image", "tracked_changes", "tracked_formatting", "footnotes", "endnotes", "comments", "headers", "footers"];

    /// <summary>Gap reasons that mean a part of the source is not in the units: not read in full, or a heading that could not be placed.</summary>
    public static bool IsUnread(string reason) =>
        reason is "merged_cells" or "nested_table" or "heading_boundary_unverified" or "selection_incomplete" || reason.StartsWith("heading_unmatched:", StringComparison.Ordinal) || reason.StartsWith("heading_located_by_text:", StringComparison.Ordinal) || WholeFileGaps.Contains(reason);

    /// <param name="path">The DOCX file.</param>
    /// <param name="sourceId">The name the units of this file carry in their ids.</param>
    /// <param name="outline">The pipeline's headings for this file, or null to use the Heading styles.</param>
    public static GatherSource Read(string path, string sourceId, IReadOnlyList<HeadingRecord>? outline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var version = DocxVersionReader.Read(path);
        var bulletLevels = BulletLevels(path);
        var cellText = CellTexts(version.Paragraphs);
        var cellFirstUnit = CellFirstUnit(version.Paragraphs, sourceId);
        var gaps = new List<GatherGap>();
        var mapped = outline is null ? null : PipelineHeadings(outline, version.Paragraphs, sourceId, gaps);
        var pipeline = mapped?.Matched;
        var continuation = mapped?.Continuation ?? new HashSet<string>(StringComparer.Ordinal);
        var unknownSpans = mapped?.Unknown ?? [];
        var pathSource = pipeline is null ? "style" : "pipeline";
        var headingCount = pipeline?.Count ?? version.Paragraphs.Count(p => p.StyleId is not null && HeadingStyle.IsMatch(p.StyleId));
        var headingPath = new List<(int Level, string Text)>();
        var uncertain = headingCount == 0;
        var units = new List<GatherUnit>();
        var remainingHeading = "";

        foreach (var p in version.Paragraphs)
        {
            if (p.Cell?.Merged == true) gaps.Add(new(sourceId, TableRe.Match(p.Location).Value, "merged_cells"));
            if (p.Cell?.TableDepth > 1) gaps.Add(new(sourceId, TableRe.Match(p.Location).Value, "nested_table"));
            if (p.NormalizedKey.Length == 0) continue;

            var (isHeading, level, headingText) = HeadingOf(p, pipeline);
            // A heading's own path is its ancestors: the open headings of a lower level. Its earlier siblings are not its path.
            var ancestors = level is { } own ? headingPath.Where(h => h.Level < own) : headingPath;
            var pathBefore = string.Join(" > ", ancestors.Select(h => h.Text));
            var inUnknownSpan = unknownSpans.Any(span => p.Ordinal > span.From && p.Ordinal <= span.To);
            var numbered = p.NumId is not null && bulletLevels.Contains((p.NumId, p.Ilvl ?? 0));
            var table = p.Cell is null ? null : TableRe.Match(p.Location).Value;
            var row = p.Cell is null ? null : RowRe.Match(p.Location).Value;
            var column = CellIdx.Match(CellRe.Match(p.Location).Value).Groups[1].Value;
            var firstInRow = p.Cell is not null && column == "1";
            var firstRow = p.Cell is not null && RowIdx.Match(p.Location).Groups[1].Value == "1";

            int? headingEnd = null;
            if (isHeading)
            {
                if (!continuation.Contains(p.Location))
                    remainingHeading = Compact(pipeline is null ? p.NormalizedKey : pipeline[p.Location].Text);
                var span = pipeline is not null && !continuation.Contains(p.Location) ? pipeline[p.Location].HeadingSpan : null;
                // An explicit source span must verify against this paragraph. Never replace an invalid supplied span with a guessed boundary.
                if (span is { } hs)
                {
                    var multiParagraph = pipeline is not null && continuation.Any(loc => ReferenceEquals(pipeline[loc], pipeline[p.Location]));
                    var part = hs.Start == 0 && hs.End > 0 && hs.End <= p.RawText.Length ? Compact(p.RawText[..hs.End]) : "";
                    if (part.Length > 0 && (part == remainingHeading || (multiParagraph && remainingHeading.StartsWith(part, StringComparison.Ordinal))))
                    {
                        headingEnd = hs.End;
                        remainingHeading = remainingHeading[part.Length..];
                    }
                    else gaps.Add(new(sourceId, p.Location, "heading_boundary_unverified"));
                }
                else if (HeadingPrefixEnd(p.RawText, remainingHeading) is { } found)
                {
                    headingEnd = found.End;
                    remainingHeading = remainingHeading[found.Consumed..];
                }
                else gaps.Add(new(sourceId, p.Location, "heading_boundary_unverified"));
            }

            units.Add(new GatherUnit($"{sourceId}|{p.Location}", sourceId, p.Location, p.Container, numbered || TypedBullet.IsMatch(p.RawText),
                isHeading, p.RawText, pathBefore, pathSource, !uncertain && !inUnknownSpan && (!isHeading || headingEnd is not null), table, row, p.Cell?.Merged == true,
                firstInRow || p.Cell is null ? null : cellText.GetValueOrDefault(row + "/tc[1]"),
                firstInRow || p.Cell is null ? null : cellFirstUnit.GetValueOrDefault(row + "/tc[1]"),
                firstRow || p.Cell is null ? null : cellText.GetValueOrDefault(table + "/tr[1]/tc[" + column + "]"),
                firstRow || p.Cell is null ? null : cellFirstUnit.GetValueOrDefault(table + "/tr[1]/tc[" + column + "]"),
                p.KeyHash) { HeadingEnd = headingEnd });

            // The second and later paragraphs of a heading are part of it: they are units, but they do not add a level to the path.
            if (!isHeading || continuation.Contains(p.Location)) continue;
            if (level is null || headingEnd is null) { uncertain = true; continue; }
            while (headingPath.Count > 0 && headingPath[^1].Level >= level) headingPath.RemoveAt(headingPath.Count - 1);
            headingPath.Add((level.Value, headingText));
            uncertain = false;
        }
        gaps.AddRange(WholeFileGapsOf(path, sourceId, version.UnsupportedRegions));
        return new GatherSource(units, gaps);
    }

    /// <summary>Whether a paragraph is a heading, its level (null when not known), and the heading's own text for the path.</summary>
    static (bool IsHeading, int? Level, string Text) HeadingOf(ParagraphFingerprint p, Dictionary<string, HeadingRecord>? pipeline)
    {
        if (pipeline is not null)
            return pipeline.TryGetValue(p.Location, out var record) ? (true, record.Level, Norm(record.Text)) : (false, null, "");
        if (p.StyleId is null || HeadingStyle.Match(p.StyleId) is not { Success: true } match) return (false, null, "");
        return (true, int.Parse(match.Groups[1].Value), p.NormalizedKey);
    }

    /// <summary>
    /// The pipeline's headings keyed by unit location. A heading is first matched by its stable id, when that paragraph's text starts with the
    /// heading's text. When it does not, the heading is searched for by its text, forward from the previous heading, so the order holds. Matching
    /// ignores whitespace, because the reader and the pipeline do not always break words at the same place. A heading found only by text is reported
    /// as a gap ("heading_located_by_text"): the pipeline's id and the reader's location disagree there. A heading found nowhere is a gap too, so it
    /// cannot silently move the path.
    /// </summary>
    /// <summary>The pipeline's headings mapped onto the reader's paragraphs.</summary>
    /// <param name="Matched">Each paragraph of a matched heading (the first and the later ones) to its heading.</param>
    /// <param name="Continuation">The paragraphs of a heading after its first one.</param>
    /// <param name="Unknown">Spans of paragraphs (ordinals, exclusive start, inclusive end) that lie after a heading that could not be matched.</param>
    internal sealed record PipelineOutline(Dictionary<string, HeadingRecord> Matched, HashSet<string> Continuation, List<(int From, int To)> Unknown);

    /// <summary>A heading of at most this many paragraphs is looked for across consecutive paragraphs.</summary>
    const int MaxHeadingParagraphs = 4;

    /// <summary>
    /// Maps the pipeline's headings onto the reader's paragraphs. A heading is first looked for at its stable id, then in the paragraphs after the
    /// previous heading, in order. A heading matches a run of consecutive paragraphs when their joined text is the heading's text (whitespace ignored),
    /// so a heading that the pipeline split over two paragraphs is matched as one; the match is reported as a gap ("heading_spans_paragraphs"). A
    /// heading found only by its text is reported ("heading_located_by_text"). A heading found nowhere is a gap, and the paragraphs after it, up to the
    /// next matched heading, are uncertain: the missing heading may have changed their path.
    /// </summary>
    static PipelineOutline PipelineHeadings(IReadOnlyList<HeadingRecord> outline, IReadOnlyList<ParagraphFingerprint> paragraphs,
        string sourceId, List<GatherGap> gaps)
    {
        var byLocation = paragraphs.Select((p, i) => (p, i)).ToDictionary(x => x.p.Location, x => x.i, StringComparer.Ordinal);
        var matched = new Dictionary<string, HeadingRecord>(StringComparer.Ordinal);
        var continuation = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<(int From, int To)>();
        var cursor = -1;
        int? unknownFrom = null;
        foreach (var heading in outline.OrderBy(h => h.Index))
        {
            var text = Compact(heading.Text);
            var stable = heading.StableId ?? heading.SourceId ?? "";
            (int First, int Last)? covered = null;
            var byId = false;
            if (text.Length > 0 && byLocation.TryGetValue(stable, out var at) && at > cursor)
            {
                covered = Cover(paragraphs, at, text);
                byId = covered is not null;
            }
            for (var i = cursor + 1; covered is null && text.Length > 0 && i < paragraphs.Count; i++)
                covered = Cover(paragraphs, i, text);
            if (covered is not { } span)
            {
                gaps.Add(new(sourceId, stable, $"heading_unmatched:{heading.Index}"));
                unknownFrom ??= cursor;
                continue;
            }
            if (!byId)
            {
                gaps.Add(new(sourceId, paragraphs[span.First].Location, $"heading_located_by_text:{stable}"));
                // A text-only recovery cannot establish source identity, including for following content.
                unknownFrom ??= span.First - 1;
            }
            if (span.Last > span.First) gaps.Add(new(sourceId, paragraphs[span.First].Location, $"heading_spans_paragraphs:{span.Last - span.First + 1}"));
            if (byId && unknownFrom is { } from) { unknown.Add((from, span.First)); unknownFrom = null; }
            matched[paragraphs[span.First].Location] = heading;
            for (var j = span.First + 1; j <= span.Last; j++)
            {
                matched[paragraphs[j].Location] = heading;
                continuation.Add(paragraphs[j].Location);
            }
            cursor = span.Last;
        }
        if (unknownFrom is { } tail) unknown.Add((tail, int.MaxValue));
        return new PipelineOutline(matched, continuation, unknown);
    }

    /// <summary>
    /// The run of paragraphs from <paramref name="start"/> whose joined text is <paramref name="text"/>, or null. The first paragraph must have text; a
    /// run ends at the first paragraph that makes the joined text longer than the heading, or differ from it.
    /// </summary>
    static (int First, int Last)? Cover(IReadOnlyList<ParagraphFingerprint> paragraphs, int start, string text)
    {
        if (Compact(paragraphs[start].NormalizedKey).Length == 0) return null;
        var joined = "";
        for (var j = start; j < paragraphs.Count && j - start < MaxHeadingParagraphs; j++)
        {
            joined += Compact(paragraphs[j].NormalizedKey);
            if (joined.StartsWith(text, StringComparison.Ordinal)) return (start, j);
            if (!text.StartsWith(joined, StringComparison.Ordinal)) return null;
        }
        return null;
    }

    /// <summary>
    /// Where the heading's text (whitespace removed) ends in a paragraph: the offset after its last character, and how many of its characters this
    /// paragraph holds (less than all when the heading goes on in the next paragraph). Null when the paragraph does not start with the heading.
    /// </summary>
    internal static (int End, int Consumed)? HeadingPrefixEnd(string text, string compactHeading)
    {
        var consumed = 0;
        var end = 0;
        for (var i = 0; i < text.Length && consumed < compactHeading.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (text[i] != compactHeading[consumed]) return null;
            consumed++;
            end = i + 1;
        }
        return consumed == 0 ? null : (end, consumed);
    }

    /// <summary>Text without any whitespace, for comparing a heading with a paragraph whose words may be joined differently.</summary>
    static string Compact(string raw) => new(raw.Normalize(System.Text.NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c)).ToArray());

    static string Norm(string raw) => System.Text.RegularExpressions.Regex.Replace(raw.Normalize(System.Text.NormalizationForm.FormC), @"\s+", " ").Trim();

    /// <summary>Text of each table cell, paragraphs joined in order, keyed by the cell's location path.</summary>
    static Dictionary<string, string> CellTexts(IReadOnlyList<ParagraphFingerprint> paragraphs)
    {
        var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in paragraphs)
        {
            var cell = CellRe.Match(p.Location);
            if (!cell.Success || p.NormalizedKey.Length == 0) continue;
            if (!texts.TryGetValue(cell.Value, out var list)) texts[cell.Value] = list = [];
            list.Add(p.NormalizedKey);
        }
        return texts.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value), StringComparer.Ordinal);
    }

    /// <summary>The unit id of the first paragraph with text in each cell, so a value can name the cell that labels it.</summary>
    static Dictionary<string, string> CellFirstUnit(IReadOnlyList<ParagraphFingerprint> paragraphs, string sourceId)
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in paragraphs)
        {
            var cell = CellRe.Match(p.Location);
            if (!cell.Success || p.NormalizedKey.Length == 0 || first.ContainsKey(cell.Value)) continue;
            first[cell.Value] = $"{sourceId}|{p.Location}";
        }
        return first;
    }

    /// <summary>
    /// The whole-file regions the reader did not read, each checked against the file: a drawing region holding no picture, chart, diagram or embedded
    /// object (only shapes and connectors; text in a text box is read as units) is recorded as "drawing_without_content", and headers or footers
    /// whose only text is digits (page numbers) as "header_page_number_only" / "footer_page_number_only". Those are still listed, but they are not
    /// unread content (<see cref="IsUnread"/>). Anything else is kept as the region it was reported as.
    /// </summary>
    static IEnumerable<GatherGap> WholeFileGapsOf(string path, string sourceId, IReadOnlyList<string> regions)
    {
        var listed = regions.Where(r => WholeFileGaps.Contains(r)).ToList();
        if (listed.Count == 0) return [];
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var main = document.MainDocumentPart;
        var result = new List<GatherGap>();
        foreach (var region in listed)
        {
            var reason = region switch
            {
                "drawing_or_image" when main?.Document?.Body is { } body && !DrawingsCarryContent(body) => "drawing_without_content",
                "footers" when main is not null && main.FooterParts.All(part => DigitsOnly(part.Footer)) => "footer_page_number_only",
                "headers" when main is not null && main.HeaderParts.All(part => DigitsOnly(part.Header)) => "header_page_number_only",
                _ => region,
            };
            result.Add(new GatherGap(sourceId, "", reason));
        }
        return result;
    }

    /// <summary>Whether any drawing or VML picture holds content the reader does not read: an image, a chart, a diagram or an embedded object.</summary>
    static bool DrawingsCarryContent(Body body) =>
        body.Descendants().Any(e => e.LocalName is "blip" or "imagedata" or "chart" or "relIds" or "OLEObject" or "oleObj");

    static bool DigitsOnly(DocumentFormat.OpenXml.OpenXmlElement? root) =>
        root is null || string.Concat(root.Descendants<Text>().Select(t => t.Text)).Where(c => !char.IsWhiteSpace(c)).All(char.IsDigit);

    /// <summary>(numId, level) pairs whose numbering format is a bullet. A numId points to an abstract definition; each level of it is checked.</summary>
    internal static HashSet<(string NumId, int Level)> BulletLevels(string path)
    {
        var levels = new HashSet<(string, int)>();
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var numbering = document.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return levels;
        var bulletByAbstract = new Dictionary<int, HashSet<int>>();
        foreach (var abstractNum in numbering.Elements<AbstractNum>())
        {
            var id = abstractNum.AbstractNumberId?.Value;
            if (id is null) continue;
            bulletByAbstract[id.Value] = abstractNum.Elements<Level>()
                .Where(l => l.NumberingFormat?.Val?.Value == NumberFormatValues.Bullet && l.LevelIndex?.Value is not null)
                .Select(l => l.LevelIndex!.Value).ToHashSet();
        }
        foreach (var instance in numbering.Elements<NumberingInstance>())
        {
            var numId = instance.NumberID?.Value;
            var abstractId = instance.AbstractNumId?.Val?.Value;
            if (numId is null || abstractId is null || !bulletByAbstract.TryGetValue(abstractId.Value, out var bullets)) continue;
            foreach (var level in bullets) levels.Add((numId.Value.ToString(), level));
        }
        return levels;
    }
}
