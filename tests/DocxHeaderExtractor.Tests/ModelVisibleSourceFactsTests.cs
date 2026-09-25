using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1, model-visible contract: HARNESS MAY REPORT OBSERVABLE SOURCE
/// FACTS; IT MUST NOT PRE-INTERPRET THEIR SEMANTIC MEANING.
/// <para>
/// The scope label ("running_page_artifact", "table_of_contents", "table", "appendix", ...) and the
/// contents flag answer, in the harness's words, the question the model is asked, so the V2 request
/// carries neither. It carries where the occurrence physically sits instead - page, page band, on how
/// many pages its text recurs, the bookmarks it links to - and the tests below hold both halves: every
/// key the model sees is on an allowlist, and nothing the removed labels encoded is lost, because each
/// occurrence they marked is still distinguishable from the raw facts that replaced them.
/// </para>
/// </summary>
public sealed partial class ModelVisibleSourceFactsTests
{
    private const string Doc0252Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Src029Pdf = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";
    private const string Ibrd = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf";
    private const string Docx = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx";

    private static readonly string[] DocxOwnedKeys = ["alias", "text", "owned", "tableDepth", "location", "style", "numbering", "markers"];
    private static readonly string[] PdfOwnedKeys = ["alias", "block", "text", "owned", "location", "style", "numbering", "markers"];
    private static readonly string[] DocxLocationKeys = ["hyperlinkAnchors"];
    private static readonly string[] PdfLocationKeys =
        ["page", "pageBand", "sameNormalizedTextPageCount", "sameNormalizedTextFirstPage", "sameNormalizedTextLastPage"];
    private static readonly string[] DocxStyleKeys = ["StyleId", "StyleName", "OutlineLevel", "Bold"];
    private static readonly string[] PdfStyleKeys = ["Bold", "Italic", "RelativeFontSize", "LineCount"];
    private static readonly string[] MarginKeys = ["alias", "block", "text", "owned"];

    [Fact]
    public async Task Every_key_the_model_sees_is_an_allowlisted_source_fact()
    {
        var docx = CaptureDocx(Docx);
        using var legacy = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(Ibrd), legacy, CancellationToken.None);
        using var structured = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(Doc0252Pdf), structured, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts);

        AssertAllowlisted(docx, DocxOwnedKeys, DocxLocationKeys, DocxStyleKeys);
        AssertAllowlisted(legacy.Requests, PdfOwnedKeys, PdfLocationKeys, PdfStyleKeys);
        AssertAllowlisted(structured.Requests, PdfOwnedKeys, PdfLocationKeys, PdfStyleKeys);
    }

    private static void AssertAllowlisted(
        IEnumerable<CapturedRequest> requests, string[] ownedKeys, string[] locationKeys, string[] styleKeys)
    {
        var owned = 0;
        foreach (var request in requests)
        {
            // CanonicalSemanticRequestComposer: the packet JSON, then "\nSCHEMA=" and the schema.
            var schemaAt = request.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
            var start = request.UserMessage.IndexOf('{');
            using var packet = JsonDocument.Parse(request.UserMessage[start..(schemaAt < 0 ? request.UserMessage.Length : schemaAt)]);
            foreach (var item in packet.RootElement.GetProperty("sourceEvidence").EnumerateArray())
            {
                var keys = item.EnumerateObject().Select(p => p.Name).ToArray();
                if (!item.GetProperty("owned").GetBoolean())
                {
                    Assert.All(keys, key => Assert.Contains(key, MarginKeys));
                    continue;
                }

                owned++;
                Assert.All(keys, key => Assert.Contains(key, ownedKeys));
                Assert.DoesNotContain("scope", keys);
                Assert.DoesNotContain("inTableOfContents", keys);
                if (item.TryGetProperty("location", out var location))
                    Assert.All(location.EnumerateObject(), p => Assert.Contains(p.Name, locationKeys));
                Assert.All(item.GetProperty("style").EnumerateObject(), p => Assert.Contains(p.Name, styleKeys));
                Assert.All(item.GetProperty("markers").EnumerateArray(), m => Assert.Matches(MarkerFact(), m.GetString()!));
            }
        }

        Assert.True(owned > 0);
    }

    [Theory]
    [InlineData(Src029Pdf)]
    [InlineData(Doc0252Pdf)]
    [InlineData(Ibrd)]
    public void Nothing_the_pdf_scope_label_encoded_is_lost(string pdf)
    {
        // The label stays internal (packing, review observations); the model gets the measurements.
        // Each occurrence the label marks must still be told apart by them: a running page artifact
        // is a page-number-shaped text or a line at the top or bottom band recurring on several pages;
        // a contents entry is a line whose text ends in a dot leader and a page number.
        var evidence = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Evidence;
        Assert.All(evidence, item => Assert.NotNull(item.LocationFacts));

        var running = evidence.Where(item => item.StructuralScope == "running_page_artifact").ToArray();
        Assert.All(running, item =>
        {
            var location = JsonSerializer.SerializeToElement(item.LocationFacts);
            var band = location.GetProperty("pageBand").GetString();
            var pages = location.GetProperty("sameNormalizedTextPageCount").GetInt32();
            Assert.True(PageNumberShaped().IsMatch(item.ExactSourceText.Trim()) || (band is "TOP" or "BOTTOM" && pages >= 3),
                $"{item.SourceAlias} '{item.ExactSourceText}' band={band} pages={pages}");
        });
        Assert.All(evidence.Where(item => item.StructuralScope == "table_of_contents"),
            item => Assert.Matches(LeaderAndPageNumber(), item.ExactSourceText));
    }

    [Fact]
    public void Nothing_the_docx_contents_flag_encoded_is_lost()
    {
        // Word's own contents entries carry a TOC style or link to a "_Toc" bookmark; a typed contents
        // list is a run of lines ending in page numbers. The model sees the style name, the anchors
        // and the text, so each flagged paragraph is still told apart without the flag.
        foreach (var path in new[] { Docx, "bench/01-style-chuan.docx" })
        {
            var source = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
            Assert.All(source.Paragraphs.Where(p => p.InTableOfContents), p =>
                Assert.True(
                    (p.Style.StyleName ?? p.Style.StyleId ?? "").Replace(" ", "").StartsWith("toc", StringComparison.OrdinalIgnoreCase) ||
                    p.HyperlinkAnchors.Any(anchor => anchor.StartsWith("_Toc", StringComparison.OrdinalIgnoreCase)) ||
                    TrailingPageNumber().IsMatch(p.Text.Trim()),
                    $"{p.SourceId} '{p.Text}'"));
        }
    }

    [Fact]
    public void A_line_recurring_at_the_top_of_every_page_is_measured_not_labelled()
    {
        var lines = Enumerable.Range(1, 5).SelectMany(page => new[]
        {
            new PdfLine(page, 800, 10, "Section IX - Particular Conditions", 0, "", 0, 72, 500, "F", "F"),
            new PdfLine(page, 400, 10, $"Body text on page {page} that says something different.", 0, "", 0, 72, 500, "F", "F"),
            new PdfLine(page, 40, 10, $"{page}", 0, "", 0, 290, 300, "F", "F"),
        }).ToArray();

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var header = annotations.Where(a => a.Line.Text.StartsWith("Section", StringComparison.Ordinal)).ToArray();
        Assert.All(header, a =>
        {
            Assert.Equal("TOP", a.PageBand);
            Assert.Equal(5, a.SameNormalizedTextPageCount);
            Assert.Equal(1, a.SameNormalizedTextFirstPage);
            Assert.Equal(5, a.SameNormalizedTextLastPage);
        });
        Assert.All(annotations.Where(a => a.Line.Y == 400), a => Assert.Equal("BODY", a.PageBand));
        Assert.All(annotations.Where(a => a.Line.Y == 40), a => Assert.Equal("BOTTOM", a.PageBand));
    }

    private static List<CapturedRequest> CaptureDocx(string path)
    {
        var source = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
        var state = DocxPolicyStateBuilder.Build(source, NumberingStyleFeatures.FromSourceDocument(source),
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        using var capture = new RequestCapturingClassifier();
        DocxAuthorityPipeline.RunAsync(state, mode, capture).GetAwaiter().GetResult();
        return capture.Requests.ToList();
    }

    [GeneratedRegex(@"^marker-(family|signature|depth|is-path|components):")] private static partial Regex MarkerFact();
    [GeneratedRegex(@"^(?:page\s*)?\d{1,4}$", RegexOptions.IgnoreCase)] private static partial Regex PageNumberShaped();
    [GeneratedRegex(@"(?:\.{2,}|…)\s*\d{1,4}\s*$")] private static partial Regex LeaderAndPageNumber();
    [GeneratedRegex(@"\d{1,4}$")] private static partial Regex TrailingPageNumber();
}
