using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B2 gold for 056 (OpenStax Business Law I Essentials). This is a source-only review aid: the
/// rendered Contents pages supply a checklist of 14 chapter and 32 numbered-section labels, then the
/// raw body source is inspected for the distinct occurrence that introduces prose. The same titles
/// also appear in the Contents and every chapter's local "Chapter Outline", so neither title equality
/// nor a pre-existing DOCX key can identify the reviewed occurrence on its own.
/// <para>
/// No production textbook route, candidate, rank, scope, domain role, or model response is read here.
/// This does not attempt to review unnumbered subheadings such as "Negotiation Style" at scale: they
/// have no complete source checklist and would turn a benchmark review into a second extractor.
/// </para>
/// </summary>
public sealed class PdfDocument056GoldBuilderProbe
{
    private const string DocxRelativePath = @"todo10_8\heading_corpus_95_word\04_giao_trinh\056_OpenStax_Business_Law_I_Essentials.docx";
    private const int TocStart = 129;
    private const int TocEnd = 223;

    // Source-review tie breaks for the two labels that occur once in a local Chapter Outline and once
    // as the body section that introduces prose. These are occurrence indexes, not a production rule.
    private static readonly IReadOnlyDictionary<string, int> ReviewedAmbiguousBodyLines =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["056/section/10.2"] = 4105,
            ["056/section/12.2"] = 4683,
        };

    [Fact]
    public void Report()
    {
        var auditOutput = Environment.GetEnvironmentVariable("BENCH_056_GOLD_AUDIT");
        var goldOutput = Environment.GetEnvironmentVariable("BENCH_056_GOLD_JSON");
        if (string.IsNullOrWhiteSpace(auditOutput) || string.IsNullOrWhiteSpace(goldOutput)) return;

        var docxPath = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), DocxRelativePath);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;
        var entries = ExtractTocChecklist(lines);
        var occurrences = new List<PdfReviewedOccurrence>();
        var audit = new StringBuilder();
        void Line(string value) => audit.AppendLine(value);

        Line($"TOC checklist: chapters={entries.Count(e => e.Kind == "chapter")} sections={entries.Count(e => e.Kind == "section")}");
        foreach (var entry in entries)
        {
            var matches = entry.Kind == "chapter"
                ? FindChapterBodyOccurrences(lines, entry)
                : FindSectionBodyOccurrences(lines, entry);
            if (matches.Count == 1)
            {
                Emit(occurrences, lines, entry, matches[0]);
                continue;
            }

            if (ReviewedAmbiguousBodyLines.TryGetValue(entry.StableId, out var reviewedIndex) &&
                matches.Contains(reviewedIndex))
            {
                Emit(occurrences, lines, entry, reviewedIndex);
                Line($"REVIEWED BODY [{entry.StableId}] index={reviewedIndex}; sibling is chapter-outline only.");
                continue;
            }

            Line($"REVIEW REQUIRED [{entry.StableId}] {entry.Marker} {entry.Title}: bodyCandidates={matches.Count}");
            foreach (var candidate in matches)
                Line($"    index={candidate} page={lines[candidate].Page} text={lines[candidate].Text}");
        }

        Line($"TOTAL gold occurrences={occurrences.Count}");
        Line("Excluded by review scope: Contents, chapter-outline repetitions, running headers, figures, assessment questions, endnotes, and unnumbered subheadings without a complete source checklist.");

        File.WriteAllText(auditOutput, audit.ToString());
        var bridge = new PdfReviewedOccurrenceBridge(
            "056_OpenStax_Business_Law_I_Essentials.docx",
            Sha256(docxPath),
            "not_recorded_bridge_does_not_enforce_pdf_sha_at_load",
            "not_recorded_no_answer_key_source",
            ExtractionFingerprint(lines),
            occurrences.OrderBy(o => o.Lines[0].Index).ToArray());
        File.WriteAllText(goldOutput, JsonSerializer.Serialize(bridge, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>Fixed B2 audit: frozen occurrence identity must still bind to the raw source facts.</summary>
    [Fact]
    public void FrozenBridgeIsOccurrenceSafeAndSourceGrounded()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docxPath = Path.Combine(root, DocxRelativePath);
        var bridgePath = Path.Combine(root, "keys", "occurrence-bridge",
            "056_OpenStax_Business_Law_I_Essentials.occurrence-bridge.json");
        Assert.True(File.Exists(bridgePath), "056 B2 bridge must be frozen before it contributes to census.");

        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath).Lines;
        var bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(bridgePath));
        Assert.Equal(Sha256(docxPath), bridge.DocxSha256);
        Assert.Equal(ExtractionFingerprint(lines), bridge.PdfLineExtractionFingerprint);
        Assert.Equal(46, bridge.Occurrences.Count);
        Assert.Equal(bridge.Occurrences.Count, bridge.Occurrences.Select(o => o.GoldStableId).Distinct().Count());

        foreach (var occurrence in bridge.Occurrences)
        foreach (var required in occurrence.RequiredLines)
        {
            Assert.InRange(required.Index, 0, lines.Count - 1);
            Assert.Equal(PdfCandidateProvenance.LineId(lines[required.Index]), required.LineId);
            Assert.Equal(lines[required.Index].Text, required.Text);
        }
    }

    private static List<TocEntry> ExtractTocChecklist(IReadOnlyList<PdfLine> lines)
    {
        var result = new List<TocEntry>();
        for (var i = TocStart; i < TocEnd; i++)
        {
            var text = NormalizeTocLine(lines[i].Text);
            if (TryParseSection(text, out var sectionMarker, out var sectionTitle))
            {
                result.Add(new TocEntry("section", sectionMarker, sectionTitle));
                continue;
            }
            if (TryParseChapter(text, out var chapterMarker, out var chapterTitle))
                result.Add(new TocEntry("chapter", chapterMarker, chapterTitle));
        }

        return result;
    }

    private static List<int> FindSectionBodyOccurrences(IReadOnlyList<PdfLine> lines, TocEntry entry)
    {
        var target = PdfTextUtilities.CanonicalForMatch($"{entry.Marker} {entry.Title}");
        return lines.Select((line, index) => (line, index))
            .Where(x => x.index >= TocEnd && PdfTextUtilities.CanonicalForMatch(x.line.Text) == target)
            .Where(x => StartsBodyRegion(lines, x.index))
            .Select(x => x.index)
            .ToList();
    }

    private static List<int> FindChapterBodyOccurrences(IReadOnlyList<PdfLine> lines, TocEntry entry)
    {
        var title = PdfTextUtilities.CanonicalForMatch(entry.Title);
        return Enumerable.Range(TocEnd, lines.Count - TocEnd - 1)
            .Where(index => CanonicalDigits(lines[index].Text) == entry.Marker)
            .Where(index => PdfTextUtilities.CanonicalForMatch(lines[index + 1].Text) == title)
            .ToList();
    }

    private static bool StartsBodyRegion(IReadOnlyList<PdfLine> lines, int index)
    {
        // Real numbered sections can introduce a figure or an unnumbered subheading before their
        // first prose paragraph. A Chapter Outline instead reaches another numbered section first.
        // This is a source-context distinction used only to review the TOC checklist, never a
        // production predicate.
        for (var i = index + 1; i < Math.Min(lines.Count, index + 12); i++)
        {
            var text = lines[i].Text.Trim();
            if (!text.Any(char.IsLetter)) continue;
            if (LooksLikeProse(text)) return true;
            if (LooksLikeNumberedSection(text)) return false;
        }
        return false;
    }

    private static bool LooksLikeProse(string text) => text.Length >= 80 &&
        !text.StartsWith("Figure", StringComparison.OrdinalIgnoreCase) &&
        !text.StartsWith("Chapter Outline", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNumberedSection(string text) =>
        Regex.IsMatch(NormalizeTocLine(text), @"^\d+\.\d+\s");

    private static bool TryParseSection(string text, out string marker, out string title)
    {
        var match = Regex.Match(text, @"^(\d+\.\d+)\s+(.+?)\s+(\d+)$");
        marker = match.Groups[1].Value;
        title = match.Groups[2].Value;
        return match.Success;
    }

    private static bool TryParseChapter(string text, out string marker, out string title)
    {
        var match = Regex.Match(text, @"^(\d+)\s+(.+?)\s+(\d+)$");
        marker = match.Groups[1].Value;
        title = match.Groups[2].Value;
        return match.Success;
    }

    private static string NormalizeTocLine(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"(?<=\d)\s+(?=\d)", "");
        return Regex.Replace(normalized, @"(?<=\d)\s*(?=\.)", "");
    }

    private static string CanonicalDigits(string text) => Regex.Replace(text.Trim(), @"(?<=\d)\s+(?=\d)", "");

    private static void Emit(List<PdfReviewedOccurrence> occurrences, IReadOnlyList<PdfLine> lines, TocEntry entry, int index)
    {
        var indexes = entry.Kind == "chapter" ? new[] { index, index + 1 } : new[] { index };
        var sourceLines = indexes.Select(i => new PdfReviewedOccurrenceLine(i, PdfCandidateProvenance.LineId(lines[i]), lines[i].Text)).ToArray();
        occurrences.Add(new PdfReviewedOccurrence(
            entry.StableId, string.Join(" ", sourceLines.Select(l => l.Text)), lines[index].Page,
            sourceLines, "reviewed", "toc-checklist-body-context", 1));
    }

    private static string ExtractionFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record TocEntry(string Kind, string Marker, string Title)
    {
        public string StableId => $"056/{Kind}/{Marker}";
    }
}
