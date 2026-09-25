using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// REMOVE_MODEL_VISIBLE_ATTENTION_V1: the harness gives the model source facts and no judgement of its
/// own about whether an occurrence looks like a heading. The legacy candidate heuristics (caption and
/// object-label regexes, data tables, content controls, contents lines) may still run for diagnostics,
/// but nothing they conclude may reach a semantic request: switching them on or off must leave every
/// request byte-identical, every non-empty occurrence must stay owned by some request, and no request
/// may carry an attention or candidate label. Measured on real requests, captured without a provider.
/// </summary>
public sealed partial class ModelVisibleAttentionBoundaryTests
{
    private const string Docx = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx";

    /// <summary>The DOCX sources of the Gold registry, as input data.</summary>
    private static readonly string[] Docxs =
    [
        "bench/01-style-chuan.docx",
        Docx,
        "todo10_8/generated-docx-v2/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx",
        "todo10_8/generated-docx-v2/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx",
        "todo10_8/generated-docx-v2/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx",
        "todo10_8/generated-docx-v2/05_bien_ban_hop/079_ICP_TAG_Minutes_Apr_2024.docx",
        "todo10_8/heading_corpus_95_word/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.docx",
        "todo10_8/heading_corpus_95_word/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.docx",
    ];
    private const string Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf";

    private static ExtractionOptions Heuristics(bool on) => new()
    {
        UseLexicalRules = on,
        SkipDataTables = on,
        SkipContentControls = on,
        SkipCorruptParagraphs = on,
    };

    private static (DocxPolicyState State, List<CapturedRequest> Requests) CaptureDocx(ExtractionOptions options, string path = Docx)
    {
        var source = new OpenXmlDocumentSource().Read(Path.IsPathRooted(path) ? path : TestRepository.Path(path));
        var state = DocxPolicyStateBuilder.Build(source, NumberingStyleFeatures.FromSourceDocument(source),
            new DocumentFeatureDeriver().Derive(source), options);
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        using var capture = new RequestCapturingClassifier();
        DocxAuthorityPipeline.RunAsync(state, mode, capture).GetAwaiter().GetResult();
        return (state, capture.Requests.ToList());
    }

    [Fact]
    public void Heuristic_toggles_cannot_change_what_the_model_sees()
    {
        var live = new List<string>();
        foreach (var path in Docxs.Append(SyntheticDocx()))
        {
            var on = CaptureDocx(Heuristics(true), path);
            var off = CaptureDocx(Heuristics(false), path);
            if (!on.State.Paragraphs.Select(p => p.IsCandidate).SequenceEqual(off.State.Paragraphs.Select(p => p.IsCandidate)))
                live.Add(path);

            Assert.NotEmpty(on.Requests);
            Assert.Equal(on.Requests.Select(r => r.SystemPrompt), off.Requests.Select(r => r.SystemPrompt));
            Assert.Equal(on.Requests.Select(r => r.UserMessage), off.Requests.Select(r => r.UserMessage));
        }

        // The toggles are live upstream on at least one source - otherwise the equality proves nothing.
        Assert.NotEmpty(live);
    }

    [Fact]
    public void Every_non_empty_docx_occurrence_is_owned_by_a_request()
    {
        var (state, requests) = CaptureDocx(Heuristics(true));
        var owned = requests.SelectMany(r => OwnedAliases(r.UserMessage)).ToArray();
        Assert.Equal(owned.Length, owned.Distinct(StringComparer.Ordinal).Count());
        // No top-k, no threshold, no veto: one owned alias per non-empty paragraph.
        Assert.Equal(state.Paragraphs.Count(p => p.Role != ParagraphRole.Empty), owned.Length);
    }

    [Fact]
    public async Task No_request_carries_an_attention_or_candidate_label()
    {
        var (_, docx) = CaptureDocx(Heuristics(true));
        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(Pdf), capture, CancellationToken.None);
        var pdf = capture.Requests.ToList();
        Assert.NotEmpty(pdf);

        foreach (var request in docx.Concat(pdf))
        {
            Assert.DoesNotMatch(Forbidden(), request.UserMessage);
            Assert.DoesNotMatch(Forbidden(), request.SystemPrompt);
        }

        // Legacy occurrence profile (the default): every occurrence is owned by exactly one request.
        var owned = pdf.SelectMany(r => OwnedAliases(r.UserMessage)).ToArray();
        Assert.Equal(owned.Length, owned.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(PdfCanonicalSourceUniverseBuilder.Build(TestRepository.Path(Pdf)).Evidence.Count, owned.Length);

        // Structured atom profile: every atom is owned by exactly one request, and no label either.
        using var structured = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(Pdf), structured, CancellationToken.None,
            profile: DocxHeaderExtractor.Core.Models.PdfSemanticAuthorityProfile.StructuredSourceParts);
        Assert.All(structured.Requests, r => Assert.DoesNotMatch(Forbidden(), r.UserMessage));
        var atoms = structured.Requests.SelectMany(r => OwnedAliases(r.UserMessage)).ToArray();
        Assert.Equal(atoms.Length, atoms.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Pdf)).Atoms.Count, atoms.Length);
    }

    /// <summary>
    /// A source the toggled rules certainly match: a caption-shaped line (CaptionRx) and a paragraph
    /// inside a content control. The corpus DOCX above happen to contain neither.
    /// </summary>
    private static string SyntheticDocx()
    {
        var path = Path.Combine(Path.GetTempPath(), $"attention-boundary-{Guid.NewGuid():N}.docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        static Paragraph P(string text) => new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        main.Document = new Document(new Body(
            P("Introduction"),
            P("The results are summarised below."),
            P("Figure 3: Revenue by year"),
            P("Revenue grew in each year of the period."),
            new SdtBlock(new SdtContentBlock(P("Prepared by the finance team"))),
            P("Conclusion")));
        main.Document.Save();
        return path;
    }

    private static IEnumerable<string> OwnedAliases(string userMessage)
    {
        var list = OwnedList().Match(userMessage);
        Assert.True(list.Success, "a request without ownedSourceAliases");
        return AliasItem().Matches(list.Groups[1].Value).Select(m => m.Groups[1].Value);
    }

    [GeneratedRegex("\"ownedSourceAliases\"\\s*:\\s*\\[([^\\]]*)\\]")] private static partial Regex OwnedList();
    [GeneratedRegex("\"([^\"]+)\"")] private static partial Regex AliasItem();
    [GeneratedRegex("\"attention\"|attention\" flag|policy-(non-)?candidate|candidate-attention|pdf-layout-(non-)?candidate|candidateScore", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
