using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Where the written result is, and the sections that could not be written, each with its reason (heading not found, next heading not found, or a place inside a table that is not known).</summary>
public sealed record GatherWriteResult(string OutputPath, IReadOnlyList<string> SkippedHeadings);

/// <summary>
/// Writes a gathering result into a copy of the target DOCX. Each kept fact becomes a tracked insertion (w:ins) placed after the heading's direct content and before its next heading, so the
/// reader can accept or reject it in Word. Conflicts, an incomplete batch and a section with no kept fact are written as tracked notes too: a reader must
/// never see an empty section as a complete one. The source file is never modified.
/// </summary>
public static class GatherWriteback
{
    public const string Author = "DHX gather";

    /// <param name="targetOutlineCoverageVerified">Caller has independently checked that the target outline covers the section boundaries.
    /// Valid heading spans alone do not prove that intervening headings were found. Defaults to false.</param>
    /// <param name="verifiedTargetSha256">SHA-256 of the exact target snapshot for which coverage was verified. This binds the assertion to the copied file.</param>
    public static GatherWriteResult Apply(string targetPath, string outputPath, IReadOnlyList<GatherSection> sections, DateTime when,
        bool targetOutlineCoverageVerified = false, string? verifiedTargetSha256 = null)
        => ApplyCore(targetPath, outputPath, sections, when, targetOutlineCoverageVerified, verifiedTargetSha256, null);

    /// <summary>Creates a clearly marked, provisional copy for display only. This never grants outline approval.</summary>
    public static GatherWriteResult CreateDemo(string targetPath, string outputPath, IReadOnlyList<GatherSection> sections,
        IReadOnlyList<GatherUncertain> uncertain, DateTime when) =>
        ApplyCore(targetPath, outputPath, sections, when, false, null, uncertain);

    static GatherWriteResult ApplyCore(string targetPath, string outputPath, IReadOnlyList<GatherSection> sections, DateTime when,
        bool targetOutlineCoverageVerified, string? verifiedTargetSha256, IReadOnlyList<GatherUncertain>? demoUncertain)
    {
        var demo = demoUncertain is not null;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GATHER_OUTPUT_IS_SOURCE: the result must go to a new file.", nameof(outputPath));
        File.Copy(targetPath, outputPath, overwrite: false);
        string copiedHash;
        using (var copiedStream = File.OpenRead(outputPath))
            copiedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(copiedStream));
        var coverageBoundToFile = targetOutlineCoverageVerified && !string.IsNullOrWhiteSpace(verifiedTargetSha256)
            && string.Equals(copiedHash, verifiedTargetSha256, StringComparison.OrdinalIgnoreCase);

        var skipped = new List<string>();
        using (var document = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX_BODY_MISSING");
            var ordered = ParagraphWalker.Enumerate(body, new ExtractionOptions()).ToList();
            var byLocation = ordered.ToDictionary(p => p.StableId, p => p.Element, StringComparer.Ordinal);
            var indexOf = ordered.Select((p, i) => (p.StableId, i)).ToDictionary(x => x.StableId, x => x.i, StringComparer.Ordinal);
            var nextId = 1000;
            if (demo)
                body.PrependChild(Tracked("BẢN DEMO CHƯA DUYỆT — vị trí và nội dung cần xác minh; không phải bản tổng hợp chính thức.", when, ref nextId, true));
            var inserted = new Dictionary<OpenXmlElement, OpenXmlElement>(ReferenceEqualityComparer.Instance);
            // A table that ends the document closes it (signature, recipients): what goes "at the end" is written before it, never after it.
            var endTable = GatherDocument.EndTable(body);
            var endOfContent = endTable is null ? ordered.Count
                : ordered.FindIndex(p => p.Element.Ancestors<Table>().Any(t => ReferenceEquals(t, endTable))) is var first and > 0 ? first : ordered.Count;
            foreach (var section in sections)
            {
                if (!coverageBoundToFile && !demo)
                {
                    skipped.Add(section.Heading + " (độ phủ heading của văn bản đích chưa xác minh)");
                    continue;
                }
                if (section.Run.Facts.Any(f => f.Flags.Contains("SCOPE_UNREVIEWED", StringComparer.Ordinal)))
                {
                    skipped.Add(section.Heading + " (nội dung theo heading chưa được review phạm vi)");
                    continue;
                }
                if (section.Run.Gaps.Any(g => g.SourceId == "target" &&
                    (g.Reason == "heading_boundary_unverified" || g.Reason.StartsWith("heading_unmatched:", StringComparison.Ordinal) ||
                     g.Reason.StartsWith("heading_located_by_text:", StringComparison.Ordinal))))
                {
                    skipped.Add(section.Heading + " (ranh giới heading của văn bản đích chưa xác minh)");
                    continue;
                }
                var isNew = section.NewHeadingDepth is not null;
                Paragraph? headingEnd = null;
                if (!isNew)
                {
                    if (section.TargetLocation.Length == 0 || !byLocation.TryGetValue(section.TargetLocation, out var heading) || !HeadingMatches(heading, section.Heading))
                    {
                        skipped.Add(section.Heading + " (không tìm thấy heading đúng vị trí)");
                        continue;
                    }
                    headingEnd = LastParagraphOf(heading, section.Heading, byLocation);
                }
                var end = headingEnd is null ? -1 : ordered.FindIndex(p => ReferenceEquals(p.Element, headingEnd));
                // The result goes before the next heading, so it follows the heading's direct content and precedes its sub-headings. With no later
                // heading it goes at the end. A new heading goes before the boundary its section names: the end of its parent's subtree.
                var boundary = section.NextHeadingLocation is null ? endOfContent
                    : indexOf.TryGetValue(section.NextHeadingLocation, out var found) ? found : -1;
                if (boundary <= 0 || (!isNew && end < 0))
                {
                    skipped.Add(section.Heading + " (không tìm thấy heading tiếp theo)");
                    continue;
                }
                OpenXmlElement lastDirect = isNew || boundary - 1 > end ? ordered[boundary - 1].Element : headingEnd!;
                // Inside a table, "after the table" is only safe when the next heading is outside it. Otherwise the place is not known: report it.
                var anchorBody = BodyLevel(lastDirect, body);
                var boundaryBody = boundary < ordered.Count ? BodyLevel(ordered[boundary].Element, body) : null;
                if ((headingEnd is not null && BodyLevel(headingEnd, body) is Table) || (anchorBody is Table && ReferenceEquals(anchorBody, boundaryBody)))
                {
                    skipped.Add(section.Heading + " (vị trí trong bảng, không xác định an toàn)");
                    continue;
                }
                // Sections written at the same place follow each other in the order given: a heading's own content, then new headings under its parent.
                var anchor = inserted.GetValueOrDefault(anchorBody, anchorBody);
                var lines = isNew ? new[] { (Text: section.Heading, Bold: true) }.Concat(Lines(section, emptyNote: false).Select(l => (Text: l, Bold: false)))
                    : Lines(section).Select(l => (Text: l, Bold: false));
                foreach (var (line, bold) in lines)
                {
                    var paragraph = Tracked(demo ? "[DEMO — cần xác minh] " + line : line, when, ref nextId, bold);
                    anchor.InsertAfterSelf(paragraph);
                    anchor = paragraph;
                }
                inserted[anchorBody] = anchor;
            }
            if (demo)
            {
                var appendix = new List<string>();
                foreach (var item in demoUncertain!)
                {
                    appendix.Add("[CHƯA RÕ — " + item.SourceHeading + "] " + item.Reason);
                    foreach (var fact in item.Facts)
                        appendix.Add("- " + fact.Render() + " (nguồn: " + string.Join("; ", fact.Parts.Concat(fact.AlsoFrom).Select(p => p.SourceId + " " + p.Location).Distinct()) + ")");
                }
                foreach (var section in sections.Where(s => skipped.Any(x => x.StartsWith(s.Heading + " (", StringComparison.Ordinal))))
                {
                    appendix.Add("[CHƯA XÁC ĐỊNH VỊ TRÍ — " + section.Heading + "]");
                    appendix.AddRange(Lines(section, emptyNote: false));
                }
                if (appendix.Count > 0)
                {
                    var properties = body.GetFirstChild<SectionProperties>();
                    var title = Tracked("DEMO — NỘI DUNG CHƯA XÁC ĐỊNH VỊ TRÍ, CẦN XÁC MINH", when, ref nextId, true);
                    if (properties is null) body.AppendChild(title); else body.InsertBefore(title, properties);
                    foreach (var line in appendix)
                    {
                        var paragraph = Tracked(line, when, ref nextId);
                        if (properties is null) body.AppendChild(paragraph); else body.InsertBefore(paragraph, properties);
                    }
                }
            }
            document.MainDocumentPart!.Document!.Save();
        }
        return new GatherWriteResult(outputPath, skipped);
    }

    /// <summary>The element that is a direct child of the body: a paragraph in a table cell is placed after its table, never inside the cell.</summary>
    static OpenXmlElement BodyLevel(OpenXmlElement element, Body body)
    {
        var current = element;
        while (current.Parent is not null && !ReferenceEquals(current.Parent, body)) current = current.Parent;
        return current;
    }

    /// <summary>
    /// The last paragraph of a heading that runs over several paragraphs: the next paragraphs are part of it while their joined text is still the start
    /// of the heading's text. Inserting after the first one would split the heading.
    /// </summary>
    static Paragraph LastParagraphOf(Paragraph first, string text, IReadOnlyDictionary<string, Paragraph> paragraphs)
    {
        var target = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var joined = string.Concat(first.InnerText.Where(c => !char.IsWhiteSpace(c)));
        var last = first;
        var next = NextParagraph(first);
        while (joined.Length < target.Length && next is not null && target.StartsWith(joined, StringComparison.Ordinal))
        {
            var candidate = joined + string.Concat(next.InnerText.Where(c => !char.IsWhiteSpace(c)));
            // The heading ends inside this paragraph (it may carry text after the heading): the insertion goes after it, and the search stops.
            if (candidate.StartsWith(target, StringComparison.Ordinal)) return next;
            if (!target.StartsWith(candidate, StringComparison.Ordinal)) break;
            joined = candidate;
            last = next;
            next = NextParagraph(next);
        }
        return last;
    }

    static Paragraph? NextParagraph(Paragraph paragraph) =>
        paragraph.NextSibling() as Paragraph;

    /// <summary>
    /// The paragraph starts the heading's text, or the heading's text starts the paragraph (a heading with its body in the same paragraph). A heading
    /// split over several paragraphs matches at its first one.
    /// </summary>
    static bool HeadingMatches(Paragraph heading, string text)
    {
        var paragraph = string.Concat(heading.InnerText.Where(c => !char.IsWhiteSpace(c)));
        var target = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return paragraph.Length > 0 && (target.StartsWith(paragraph, StringComparison.Ordinal) || paragraph.StartsWith(target, StringComparison.Ordinal));
    }

    /// <summary>
    /// The lines written under one heading: each kept fact, then each conflict, then a note when the run is incomplete or empty. A new heading that
    /// only holds sub-headings gets no "empty" note (<paramref name="emptyNote"/> false).
    /// </summary>
    static IEnumerable<string> Lines(GatherSection section, bool emptyNote = true)
    {
        foreach (var fact in section.Run.Facts)
        {
            var sources = string.Join("; ", fact.Parts.Concat(fact.AlsoFrom)
                .Select(p => p.SourceId + " " + p.Location).Distinct(StringComparer.Ordinal));
            var flags = fact.Flags.Count == 0 ? "" : " [cần xem: " + string.Join(", ", fact.Flags) + "]";
            yield return "- " + fact.Render() + " (nguồn: " + sources + ")" + flags;
        }
        foreach (var conflict in section.Conflicts)
            yield return "[mâu thuẫn — " + conflict.Label + ": " + string.Join(" | ", conflict.Values) + "; cần xem]";
        if (section.Run.BatchFailures.Count > 0)
            yield return "[chưa xét đủ: " + section.Run.UnreviewedUnitIds.Count + " đơn vị nằm trong batch lỗi; cần chạy lại]";
        if (section.Run.UnreadGaps.Count > 0)
            yield return "[nguồn chưa đọc đủ: " + string.Join("; ", section.Run.UnreadGaps.Select(g => g.SourceId + " " + g.Reason + (g.Location.Length > 0 ? " " + g.Location : ""))) + "; kết quả không bao phủ phần này]";
        if (section.Run.Refused.Count > 0)
            yield return "[có " + section.Run.Refused.Count + " ý đề xuất bị từ chối do không hợp lệ; cần kiểm tra lại]";
        if (emptyNote && section.Run.Complete && section.Run.Facts.Count == 0)
            yield return "[không có ý nào được chọn từ các nguồn]";
    }

    static Paragraph Tracked(string line, DateTime when, ref int nextId, bool bold = false)
    {
        var run = new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve });
        if (bold) run.PrependChild(new RunProperties(new Bold()));
        var inserted = new InsertedRun(run) { Id = (nextId++).ToString(), Author = Author, Date = when };
        // The paragraph mark is marked as inserted too: rejecting the change must remove the whole paragraph, not leave an empty one behind.
        var mark = new ParagraphMarkRunProperties(new Inserted { Id = (nextId++).ToString(), Author = Author, Date = when });
        return new Paragraph(new ParagraphProperties(mark), inserted);
    }
}
