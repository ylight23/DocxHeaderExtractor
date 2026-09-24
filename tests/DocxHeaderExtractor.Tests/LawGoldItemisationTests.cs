using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The three law documents' headings, read off their structure: the law's title, every chapter
/// (its number line and its name line), every section, every article. Each list is built by one rule
/// and must come to the approved total exactly - SRC-003 231, DOC-0092 145 (the Công báo issue holds
/// articles 1-117), DOC-0264 159 (with the Order's title and the Law's title). The qwen3.7-flash runs
/// in eval/a99-closed-loop/scaleup-real-harness-v1 are the cross-check. Write to the authored Gold
/// once with A99_LAW_ITEMISE=1 (refuses a document already itemised).
/// </summary>
public sealed partial class LawGoldItemisationTests
{
    private const string Src003 = "todo10_8/heading_corpus_95_word/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.docx";
    private const string Doc0264 = "todo10_8/heading_corpus_95_word/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.docx";
    private const string Doc0092 = "todo10_8/heading_corpus_100/01_phap_quy/007_Luat_Nha_o_27-2023-QH15.pdf";

    [GeneratedRegex(@"^(Chương|Chapter) [IVXLC]+$")] private static partial Regex ChapterNumber();
    [GeneratedRegex(@"^(Mục|Section) \d+$")] private static partial Regex SectionNumber();
    [GeneratedRegex(@"^(Mục|Section) \d+\.? \S")] private static partial Regex SectionWithName();
    [GeneratedRegex(@"^(Điều|Article) (\d+)\.\s+\S")] private static partial Regex Article();

    /// <summary>A heading as the run of consecutive paragraphs (DOCX) or atoms (PDF) it occupies.</summary>
    internal sealed record Heading(int Start, int Count, string Text, string Kind);

    [Fact]
    public void Src003_is_title_ten_chapters_two_sections_and_articles_1_to_218()
    {
        var headings = DocxHeadings(Src003, titles: [["LUẬT", "DOANH NGHIỆP"]]);
        AssertShape(headings, total: 231, chapters: 10, sections: 2, articles: 218, titles: 1);
    }

    [Fact]
    public void Doc0264_is_two_titles_ten_chapters_twelve_sections_and_articles_1_to_135()
    {
        var headings = DocxHeadings(Doc0264, titles: [["Order", "On the promulgation of law"], ["LAW ON SECURITIES"]]);
        AssertShape(headings, total: 159, chapters: 10, sections: 12, articles: 135, titles: 2);
    }

    [Fact]
    public void Doc0092_is_title_seven_chapters_twenty_sections_and_articles_1_to_117()
    {
        var headings = PdfHeadings(TestRepository.Path(Doc0092), titles: [["LUẬT", "NHÀ Ở"]]);
        AssertShape(headings, total: 145, chapters: 7, sections: 20, articles: 117, titles: 1);
    }

    [Fact]
    public void Dump_for_review()
    {
        var target = Environment.GetEnvironmentVariable("A99_LAW_DUMP");
        if (target is null) return;
        var lines = new List<string>();
        foreach (var (id, headings) in new[]
        {
            ("SRC-003", DocxHeadings(Src003, [["LUẬT", "DOANH NGHIỆP"]])),
            ("DOC-0264", DocxHeadings(Doc0264, [["Order", "On the promulgation of law"], ["LAW ON SECURITIES"]])),
            ("DOC-0092", PdfHeadings(TestRepository.Path(Doc0092), [["LUẬT", "NHÀ Ở"]])),
        })
            lines.AddRange(headings.Select((h, i) => $"{id}\t{i + 1}\t{h.Kind}\t{h.Count}\t{h.Text}"));
        File.WriteAllLines(target, lines);
    }

    private static void AssertShape(IReadOnlyList<Heading> headings, int total, int chapters, int sections, int articles, int titles)
    {
        Assert.Equal(titles, headings.Count(h => h.Kind == "title"));
        Assert.Equal(chapters, headings.Count(h => h.Kind == "chapter"));
        Assert.Equal(sections, headings.Count(h => h.Kind == "section"));
        var numbers = headings.Where(h => h.Kind == "article").Select(h => int.Parse(Article().Match(h.Text).Groups[2].Value)).ToArray();
        Assert.Equal(Enumerable.Range(1, articles), numbers);
        Assert.Equal(total, headings.Count);
    }

    /// <summary>
    /// Titles are named (they have no number to find them by); the rest by rule: a chapter is its
    /// number paragraph and the name paragraph after it; a section likewise when its number stands
    /// alone, else its one paragraph; an article is its one paragraph.
    /// </summary>
    internal static List<Heading> DocxHeadings(string docx, string[][] titles)
    {
        var texts = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(docx)).Select(p => p.Text.Trim()).ToArray();
        var headings = new List<Heading>();
        var nextTitle = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            if (nextTitle < titles.Length && texts.Skip(i).Take(titles[nextTitle].Length).SequenceEqual(titles[nextTitle]))
            {
                var count = titles[nextTitle++].Length;
                headings.Add(new(i, count, string.Join(" ", texts.Skip(i).Take(count)), "title"));
                i += count - 1;
            }
            else if (ChapterNumber().IsMatch(texts[i]) || SectionNumber().IsMatch(texts[i]))
            {
                headings.Add(new(i, 2, texts[i] + " " + texts[i + 1], ChapterNumber().IsMatch(texts[i]) ? "chapter" : "section"));
                i++;
            }
            else if (SectionWithName().IsMatch(texts[i])) headings.Add(new(i, 1, texts[i], "section"));
            else if (Article().IsMatch(texts[i])) headings.Add(new(i, 1, texts[i], "article"));
        }
        Assert.Equal(titles.Length, nextTitle);
        return headings;
    }

    /// <summary>
    /// The same rule over PDF atoms, where a title can wrap: a chapter, section or article runs on
    /// through the bold lines that follow it, up to the next numbered heading or the first line that
    /// is not bold.
    /// </summary>
    internal static List<Heading> PdfHeadings(string pdf, string[][] titles)
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(pdf).Atoms;
        var bold = BoldBySourceId(pdf);
        var headings = new List<Heading>();
        var nextTitle = 0;
        for (var i = 0; i < atoms.Count; i++)
        {
            var text = atoms[i].Text.Trim();
            if (nextTitle < titles.Length && atoms.Skip(i).Take(titles[nextTitle].Length).Select(a => a.Text.Trim()).SequenceEqual(titles[nextTitle]))
            {
                var count = titles[nextTitle++].Length;
                headings.Add(new(i, count, string.Join(" ", atoms.Skip(i).Take(count).Select(a => a.Text.Trim())), "title"));
                i += count - 1;
                continue;
            }
            var kind = ChapterNumber().IsMatch(text) ? "chapter"
                : SectionNumber().IsMatch(text) || SectionWithName().IsMatch(text) ? "section"
                : Article().IsMatch(text) ? "article"
                : null;
            if (kind is null) continue;
            var end = i + 1;
            while (end < atoms.Count && bold[atoms[end].SourceId] && !StartsHeading(atoms[end].Text.Trim())) end++;
            headings.Add(new(i, end - i, string.Join(" ", atoms.Skip(i).Take(end - i).Select(a => a.Text.Trim())), kind));
            i = end - 1;
        }
        Assert.Equal(titles.Length, nextTitle);
        return headings;
    }

    private static bool StartsHeading(string text) =>
        ChapterNumber().IsMatch(text) || SectionNumber().IsMatch(text) || SectionWithName().IsMatch(text) || Article().IsMatch(text);

    private static Dictionary<string, bool> BoldBySourceId(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3)
            .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().BoldRatio >= 0.5, StringComparer.Ordinal);
    }

    [Fact]
    public void Apply_the_law_itemisations_to_authored_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_LAW_ITEMISE") != "1") return;

        ApplyDocx("SRC-003", Src003, DocxHeadings(Src003, [["LUẬT", "DOANH NGHIỆP"]]), 231);
        ApplyDocx("DOC-0264", Doc0264, DocxHeadings(Doc0264, [["Order", "On the promulgation of law"], ["LAW ON SECURITIES"]]), 159);
        ApplyPdf("DOC-0092", Doc0092, PdfHeadings(TestRepository.Path(Doc0092), [["LUẬT", "NHÀ Ở"]]), 145);
    }

    private static void ApplyDocx(string id, string docx, List<Heading> headings, int total)
    {
        var path = TestRepository.Path(docx);
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(path);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(
                DocumentSourceCatalogBuilder.FromSourceDocument(new OpenXmlDocumentSource().Read(path)))
            .ToDictionary(alias => alias.SourceId, StringComparer.Ordinal);
        var claims = headings.Select((heading, ordinal) =>
        {
            var parts = new JsonArray();
            for (var i = heading.Start; i < heading.Start + heading.Count; i++)
            {
                var alias = aliases[paragraphs[i].StableId];
                var partText = paragraphs[i].Text.Trim();
                var at = alias.Text.IndexOf(partText, StringComparison.Ordinal);
                Assert.True(at >= 0, $"'{partText}' is not in alias {alias.Alias}");
                parts.Add(new JsonObject
                {
                    ["sourceAlias"] = alias.Alias,
                    ["sourceId"] = paragraphs[i].StableId,
                    ["utf16Span"] = new JsonObject { ["start"] = at, ["end"] = at + partText.Length },
                });
            }
            var first = parts[0]!;
            return new JsonObject
            {
                ["headingOrdinal"] = ordinal,
                ["sourceAlias"] = first["sourceAlias"]!.GetValue<string>(),
                ["sourceId"] = first["sourceId"]!.GetValue<string>(),
                ["exactText"] = heading.Text,
                ["selectionMode"] = CanonicalSemanticSelectionMode.WholeAlias,
                ["semanticRole"] = null,
                ["utf16Span"] = heading.Count == 1 ? first["utf16Span"]!.DeepClone() : null,
                ["parts"] = parts,
                ["evidence"] = $"structure: {heading.Kind}; user-approved",
            };
        }).ToList();
        Apply(id, total, "SOURCE_ALIAS", claims);
    }

    private static void ApplyPdf(string id, string pdf, List<Heading> headings, int total)
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms;
        var claims = headings.Select(heading =>
        {
            var parts = atoms.Skip(heading.Start).Take(heading.Count).Select(a => new SemanticSourcePart(a.Alias, "WHOLE_ALIAS")).ToArray();
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
            Assert.True(binding.IsBound, binding.Reason);
            return new JsonObject
            {
                ["approvedWording"] = heading.Text,
                ["sourceParts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                {
                    ["sourceAlias"] = p.SourceAlias,
                    ["selectionMode"] = p.SelectionMode,
                    ["verbatimText"] = null,
                    ["occurrence"] = null,
                    ["leftExactContext"] = null,
                    ["rightExactContext"] = null,
                }).ToArray()),
                ["semanticRole"] = null,
                ["identity"] = binding.Identity,
                ["projectedText"] = string.Join(" ", binding.Parts.Select(p => p.Text)),
                ["evidence"] = $"structure: {heading.Kind}; user-approved",
            };
        }).ToList();
        Apply(id, total, "STRUCTURED_SOURCE_PARTS", claims);
    }

    private static void Apply(string id, int total, string coordinateSystem, List<JsonObject> claims)
    {
        var path = TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json");
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot: refuses a document that is already itemised
        Assert.Equal(total, gold["semanticHeadingTotal"]!.GetValue<int>());
        Assert.Equal(total, claims.Count);

        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = coordinateSystem,
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var provenance = gold["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = $"gold-correction:{id}:itemised-by-structure:2026-09-24",
            ["sha256"] = CanonicalArtifactHash.OfText(before),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        foreach (var run in Directory.GetFiles(TestRepository.Path($"eval/a99-closed-loop/scaleup-real-harness-v1/{id}"), "*.v1.json").Order(StringComparer.Ordinal))
            provenance.Add(new JsonObject
            {
                ["path"] = Path.GetRelativePath(TestRepository.Root(), run).Replace('\\', '/'),
                ["sha256"] = CanonicalArtifactHash.OfTextFile(run),
                ["role"] = "PILOT_EVIDENCE",
            });
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }
}
