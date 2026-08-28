using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class RfcTocOccurrenceIndexDiagnosisTests
{
    private static readonly Regex Target = new(
        @"(?<![\w.])1\.\s*Introduction\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Rfc_092_occurrence_index_spaces_are_audited_without_remediation()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "07_system_generated", "092_RFC9111_HTTP_Caching.docx");
        var document = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
        var result = RfcTocDictionaryOutline.Analyze(document);
        Assert.True(result.Accepted);
        Assert.Equal(67, result.Diagnostics.DictionaryEntries);
        Assert.Equal(67, result.Diagnostics.BodyAnchors);
        Assert.Equal(1.0, result.Diagnostics.BodyAnchorRatio);

        var paragraphs = document.Paragraphs.OrderBy(p => p.Index).ToList();
        var eligible = paragraphs.Where(p => !p.Corrupt && !string.IsNullOrWhiteSpace(p.Text)).ToList();
        var bodyOrdinal = 0;
        var tocOrdinal = 0;
        var occurrences = new List<object>();
        foreach (var paragraph in paragraphs)
        {
            var matches = Target.Matches(paragraph.Text);
            foreach (Match match in matches)
            {
                var isToc = paragraph.Index >= 60 && paragraph.Index < 100;
                occurrences.Add(new
                {
                    occurrenceId = $"{paragraph.StableId}#target-{match.Index}",
                    rawText = match.Value,
                    normalizedText = "1. Introduction",
                    sourceParagraphIndex = paragraph.Index,
                    eligibleParagraphOrdinal = eligible.FindIndex(p => p.Index == paragraph.Index) + 1,
                    bodyAnchorOrdinal = isToc ? (int?)null : ++bodyOrdinal,
                    tocOrdinal = isToc ? ++tocOrdinal : (int?)null,
                    tableDepth = paragraph.TableDepth,
                    stableId = paragraph.StableId,
                    sourceLineIds = (string[]?)null,
                    sourceSegments = paragraph.SourceSegments,
                    isTocOccurrence = isToc,
                    isBodyOccurrence = !isToc,
                    selectedAsAnchor = !isToc && paragraph.Index == 100,
                });
            }
        }

        var selected = result.Headings.Single(h => h.Text == "1. Introduction");
        var controlTexts = new[] { "1. Introduction", "5. Field Definitions", "Appendix A Collected ABNF" };
        var sameDocumentControls = controlTexts.Select(text =>
        {
            var heading = result.Headings.Single(h => h.Text == text);
            var source = document.Paragraphs.Single(p => p.Index == heading.Index);
            var tocNeedle = text.StartsWith("Appendix", StringComparison.Ordinal) ? "Collected ABNF" : text.Split(' ', 2)[1];
            var tocSource = paragraphs.FirstOrDefault(p => p.Index >= 60 && p.Index < 100 &&
                p.Text.Contains(tocNeedle, StringComparison.OrdinalIgnoreCase));
            return new
            {
                text,
                runtimeIndex = heading.Index,
                runtimeStableId = heading.StableId,
                runtimeTableDepth = source.TableDepth,
                tocSourceIndex = tocSource?.Index,
                tocStableId = tocSource?.StableId,
                tocTableDepth = tocSource?.TableDepth,
            };
        }).ToArray();
        var report = new
        {
            task = "RFC-3",
            rfc2CandidateGenerationRecovered = true,
            targetText = "1. Introduction",
            expectedIndex = 8,
            actualIndex = selected.Index,
            expectedIndexSemantics = "hard-coded historical test assertion; no producer or source mapping found",
            expectedIndexAuthority = "STALE_EXPECTATION",
            actualIndexSemantics = "raw ParagraphWalker/SlimParagraph source index copied by RfcTocDictionaryOutline",
            actualIndexAuthority = "SOURCE_FACT",
            occurrenceIdentityMatch = selected.StableId == document.Paragraphs.Single(p => p.Index == selected.Index).StableId,
            positionalIndexMatch = selected.Index == 8,
            firstIndexDivergenceStage = "TEST_EXPECTATION",
            firstIndexDivergenceOperation = "RfcTocDictionaryOutlineTests.Dung_toc_dictionary_giu_so_muc_va_khop_nav_092 assertion",
            rootCauseClassification = "STALE_TEST_EXPECTATION",
            productionRemediationJustified = "NO",
            testExpectationReviewJustified = "YES",
            sameDocumentControls,
            occurrences,
            indexSpaces = new object[]
            {
                new { field = "WalkedParagraph.StableId", writer = "ParagraphWalker", meaning = "XML structural identity", zeroBased = (bool?)null, source = "document.xml traversal", stableAcrossFiltering = true, stableAcrossTables = true },
                new { field = "SlimParagraph.Index", writer = "DocxSlimExtractor.BuildParagraph", meaning = "raw document paragraph traversal ordinal", zeroBased = true, source = "ParagraphWalker output", stableAcrossFiltering = true, stableAcrossTables = true },
                new { field = "HeadingRecord.Index", writer = "RfcTocDictionaryOutline", meaning = "copied source paragraph index", zeroBased = true, source = "SlimParagraph.Index", stableAcrossFiltering = true, stableAcrossTables = true },
                new { field = "expected test value", writer = "RfcTocDictionaryOutlineTests line 27", meaning = "unexplained literal 8", zeroBased = (bool?)null, source = "test assertion", stableAcrossFiltering = false, stableAcrossTables = false },
            }
        };

        File.WriteAllText(
            Path.Combine(root, "eval", "verification", "rfc-toc-occurrence-index-diagnosis.v1.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
