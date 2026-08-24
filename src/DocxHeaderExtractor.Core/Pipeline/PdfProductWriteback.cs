using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// M9.3b. Writes the outline level a <see cref="PdfProductOutput"/> already decided into a COPY of
/// the source document. It locates every occurrence by <see cref="PdfProductHeading.ParagraphIndex"/>
/// and <see cref="PdfProductHeading.StableId"/> - the canonical <see cref="DocxSourceAnchor"/> - and
/// never by searching for the heading text. It reads no <c>PdfEvidenceAnchor</c>, no
/// <c>HeadingRecord</c>, and infers no hierarchy: a heading whose level M9.1 left unresolved is
/// skipped rather than assigned one here, and a parent is never written - <c>w:outlineLvl</c> encodes
/// depth, not an explicit relation, so there is nothing for this layer to invent.
/// <para>
/// The split mechanics (<see cref="OutlineWriteback.TrySplitPoint"/>,
/// <see cref="OutlineWriteback.SplitParagraph"/>) are shared with the writeback this replaces: they
/// operate on a <c>Paragraph</c> element and a raw offset, not on either data shape, so both routes
/// stay identical at the one place content is actually rearranged.
/// </para>
/// <para>
/// Fail-closed like its predecessor: the source is copied once per call (so replaying the same input
/// against a fresh target is deterministic), the source paragraph text at the anchor's span is
/// re-verified against the stored heading text before any mutation, and a written target is read back
/// and checked against every applied heading before the call returns successfully.
/// </para>
/// </summary>
public static class PdfProductWriteback
{
    public static OutlineWritebackResult Apply(
        string sourceDocxPath,
        string targetPath,
        PdfProductOutput output,
        ExtractionOptions extraction,
        OutlineWritebackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(extraction);
        options ??= new OutlineWritebackOptions();

        var source = Path.GetFullPath(sourceDocxPath);
        var target = Path.GetFullPath(targetPath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Không tìm thấy tài liệu nguồn: {source}", source);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Đích ghi trùng file nguồn; writeback luôn ghi ra bản sao.");
        if (File.Exists(target) && !options.Overwrite)
            throw new InvalidOperationException($"File đích đã tồn tại: {target}");

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.Copy(source, target, options.Overwrite);

        var skipped = new List<OutlineWritebackSkip>();
        var applied = new List<PdfProductHeading>();
        var splits = new List<OutlineWriteback.PendingSplit>();

        // Read from the SOURCE, not the copy about to be opened for writing: the two are still
        // byte-identical here, and reading the target in parallel would contend with the write handle.
        var slim = new DocxSlimExtractor(extraction).Extract(source);
        try
        {
            using (var doc = WordprocessingDocument.Open(target, true))
            {
                var main = doc.MainDocumentPart
                           ?? throw new InvalidOperationException($"File không có MainDocumentPart: {target}");
                var body = main.Document?.Body
                           ?? throw new InvalidOperationException($"File không có body: {target}");

                var paragraphs = ParagraphWalker.Enumerate(body, extraction).ToList();
                var headingStyles = options.ApplyHeadingStyles ? OutlineWriteback.HeadingStyleIds(main) : [];

                foreach (var heading in output.Headings)
                {
                    if (Skip(heading, paragraphs.Count) is { } reason)
                    {
                        skipped.Add(new OutlineWritebackSkip(heading.ParagraphIndex, reason));
                        continue;
                    }

                    var walked = paragraphs[heading.ParagraphIndex];
                    if (heading.StableId is { Length: > 0 } stableId && walked.StableId != stableId)
                    {
                        skipped.Add(new OutlineWritebackSkip(heading.ParagraphIndex, "stable_id_mismatch"));
                        continue;
                    }

                    // The anchor's span must still point at the exact text the projection captured -
                    // the source may have moved on since FinalStructure was materialized, and a stale
                    // anchor writing over the wrong slice is worse than an honest skip.
                    var sourceParagraph = slim.ByIndex(heading.ParagraphIndex);
                    if (SpanText(sourceParagraph, heading.Span) != heading.Text)
                    {
                        skipped.Add(new OutlineWritebackSkip(heading.ParagraphIndex, "anchor_text_mismatched"));
                        continue;
                    }

                    if (heading.Span.Start > 0)
                    {
                        // Text precedes the heading in the same paragraph. Splitting a leading slice
                        // off is a different operation from the trailing-body split below and nothing
                        // here has verified it is safe, so this stays a skip rather than a new mode.
                        skipped.Add(new OutlineWritebackSkip(heading.ParagraphIndex, "leading_text_not_splittable"));
                        continue;
                    }

                    // A single normalized space separates the heading slice from trailing body text
                    // (paragraph text never carries a run of consecutive spaces), so the real split
                    // point is one past the span, not the span's own end.
                    var bodyStart = heading.Span.End;
                    while (bodyStart < sourceParagraph!.Text.Length && sourceParagraph.Text[bodyStart] == ' ') bodyStart++;

                    if (bodyStart < sourceParagraph.Text.Length)
                    {
                        if (OutlineWriteback.TrySplitPoint(sourceParagraph, walked.Element, bodyStart)
                            is not { } runIndex)
                        {
                            skipped.Add(new OutlineWritebackSkip(heading.ParagraphIndex, "inline_body_not_splittable"));
                            continue;
                        }
                        splits.Add(new OutlineWriteback.PendingSplit(heading.ParagraphIndex, walked.Element, runIndex));
                    }

                    var pPr = walked.Element.ParagraphProperties;
                    if (pPr is null)
                    {
                        pPr = new ParagraphProperties();
                        walked.Element.PrependChild(pPr);
                    }

                    pPr.OutlineLevel = new OutlineLevel { Val = heading.Level!.Value - 1 };
                    if (headingStyles.TryGetValue(heading.Level.Value, out var styleId))
                        pPr.ParagraphStyleId = new ParagraphStyleId { Val = styleId };

                    applied.Add(heading);
                }

                // Split AFTER every outlineLvl is placed: inserting a new w:p shifts every later
                // index, so anything keyed on `paragraphs` has to be done first.
                foreach (var split in splits) OutlineWriteback.SplitParagraph(split);

                main.Document!.Save();
            }

            Verify(target, extraction, applied, splits.Select(x => x.Index).ToList());
        }
        catch
        {
            OutlineWriteback.TryDelete(target);
            throw;
        }

        return new OutlineWritebackResult(target, applied.Count, skipped);
    }

    private static string? Skip(PdfProductHeading heading, int paragraphCount)
    {
        if (heading.ParagraphIndex < 0 || heading.ParagraphIndex >= paragraphCount) return "index_out_of_range";

        // A level M9.1 could not resolve is a fact about the evidence, not a gap this layer fills:
        // w:outlineLvl needs an integer, and there is no honest one to write here.
        if (heading.Level is null) return "level_unresolved";
        if (heading.Level is < 1 or > 9) return "invalid_level";
        return null;
    }

    private static string? SpanText(SlimParagraph? paragraph, DocxTextSpan span)
    {
        if (paragraph is null) return null;
        var text = paragraph.Text;
        if (span.Start < 0 || span.End <= span.Start || span.End > text.Length) return null;
        return text[span.Start..span.End];
    }

    /// <param name="splitIndexes">
    /// Original-numbering indexes of paragraphs that were split in two. Every split inserts one
    /// <c>w:p</c>, shifting every later index by one; without this map verification would read the
    /// wrong paragraph for anything after the first split.
    /// </param>
    private static void Verify(
        string target,
        ExtractionOptions extraction,
        IReadOnlyList<PdfProductHeading> applied,
        IReadOnlyCollection<int> splitIndexes)
    {
        var written = new DocxSlimExtractor(extraction).Extract(target);
        foreach (var heading in applied)
        {
            var shift = splitIndexes.Count(i => i < heading.ParagraphIndex);
            var at = heading.ParagraphIndex + shift;

            var paragraph = written.ByIndex(at)
                            ?? throw new InvalidOperationException(
                                $"Sau khi ghi, đoạn {heading.ParagraphIndex} không còn tồn tại trong bản đích.");

            if (shift == 0 && heading.StableId is { Length: > 0 } stableId && paragraph.StableId != stableId)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.ParagraphIndex} đổi địa chỉ XML " +
                    $"({paragraph.StableId} ≠ {stableId}).");

            // A split paragraph keeps only the heading slice; an unsplit one still holds the exact
            // grounded text the anchor pointed at.
            if (paragraph.Text != heading.Text)
                throw new InvalidOperationException(
                    $"Sau khi ghi, nội dung đoạn {heading.ParagraphIndex} không còn khớp anchor.");

            if (paragraph.OutlineLevel != heading.Level - 1)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.ParagraphIndex} có outline level {paragraph.OutlineLevel}, " +
                    $"khác cấp {heading.Level} đã chốt.");
        }
    }
}
