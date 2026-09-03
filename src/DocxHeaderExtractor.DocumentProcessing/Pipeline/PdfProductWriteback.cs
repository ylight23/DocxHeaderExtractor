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

        // The output declares the exact revision it was derived from. Applying it to any other
        // document would be using an artifact outside the authority it states about itself, so the
        // whole operation is refused here rather than left to the per-heading checks: those verify
        // that a particular anchor still holds, which is not the same as verifying that this is the
        // document the anchors were taken from. Checked before the copy, so a refused writeback
        // leaves nothing behind at all.
        var actualSourceSha = Sha256(source);
        if (!string.Equals(actualSourceSha, output.SourceDocumentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Product output thuộc về bản tài liệu khác; writeback bị từ chối. " +
                $"expected={output.SourceDocumentSha256} actual={actualSourceSha}");

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.Copy(source, target, options.Overwrite);

        var skipped = new List<OutlineWritebackSkip>();
        var applied = new List<PdfProductHeading>();
        var splits = new List<OutlineWriteback.PendingSplit>();

        // Read from the SOURCE, not the copy about to be opened for writing: the two are still
        // byte-identical here, and reading the target in parallel would contend with the write handle.
        var sourceDocument = new OpenXmlDocumentSource(extraction).Read(source);
        var mappings = WritebackMappingSet.FromSourceDocument(sourceDocument);
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
                    if (mappings.Values.FirstOrDefault(mapping =>
                            mapping.Locator.ParagraphIndex == heading.ParagraphIndex) is not { } sourceMapping ||
                        SpanText(sourceMapping.Locator.SourceText, heading.Span) != heading.Text)
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
                    while (bodyStart < sourceMapping.Locator.SourceText.Length && sourceMapping.Locator.SourceText[bodyStart] == ' ') bodyStart++;

                    if (bodyStart < sourceMapping.Locator.SourceText.Length)
                    {
                        if (OutlineWriteback.TrySplitPoint(sourceMapping, walked.Element, bodyStart)
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

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string? Skip(PdfProductHeading heading, int paragraphCount)
    {
        if (heading.ParagraphIndex < 0 || heading.ParagraphIndex >= paragraphCount) return "index_out_of_range";

        // A level M9.1 could not resolve is a fact about the evidence, not a gap this layer fills:
        // w:outlineLvl needs an integer, and there is no honest one to write here.
        if (heading.Level is null) return "level_unresolved";
        if (heading.Level is < 1 or > 9) return "invalid_level";
        return null;
    }

    private static string? SpanText(string? text, DocxTextSpan span)
    {
        if (text is null) return null;
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
        var written = new OpenXmlDocumentSource(extraction).Read(target);
        foreach (var heading in applied)
        {
            var shift = splitIndexes.Count(i => i < heading.ParagraphIndex);
            var at = heading.ParagraphIndex + shift;

            var paragraph = written.Paragraphs.FirstOrDefault(item => item.SourceOrdinal == at)
                            ?? throw new InvalidOperationException(
                                $"Sau khi ghi, đoạn {heading.ParagraphIndex} không còn tồn tại trong bản đích.");

            if (shift == 0 && heading.StableId is { Length: > 0 } stableId && paragraph.SourceId != stableId)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.ParagraphIndex} đổi địa chỉ XML " +
                    $"({paragraph.SourceId} ≠ {stableId}).");

            // A split paragraph keeps only the heading slice; an unsplit one still holds the exact
            // grounded text the anchor pointed at.
            if (paragraph.Text != heading.Text)
                throw new InvalidOperationException(
                    $"Sau khi ghi, nội dung đoạn {heading.ParagraphIndex} không còn khớp anchor.");

            if (paragraph.Style.OutlineLevel != heading.Level - 1)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.ParagraphIndex} có outline level {paragraph.Style.OutlineLevel}, " +
                    $"khác cấp {heading.Level} đã chốt.");
        }
    }
}
