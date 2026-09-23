using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Materializes DOC-0205's first canonical occurrence-gold artifact: the 71 already human-approved
/// headings from <c>strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json</c> (PROVENANCE_ONLY,
/// its own <c>approvedHeadingText</c> field mojibake-corrupted - this reuses its clean
/// <c>rawSourceText</c> field instead, never the corrupted one), plus the one heading missing from
/// that set: the document's own title, "NGHỊ ĐỊNH / Quản lý, kết nối và chia sẻ dữ liệu số của cơ
/// quan nhà nước". That title is verified verbatim against the real paragraph text via
/// <see cref="ParagraphWalker"/> + OpenXml's own <c>InnerText</c> - not hand-typed - closing the
/// 71-vs-72 gap the approved semantic total (72) already implied, per the .key file's own stated
/// scope ("Lấy các đoạn &lt;strong&gt; Chương/Mục/Điều", which by design excludes the document's own
/// title block).
/// <para>
/// Character-offset spans are NOT independently recomputed here for the 71 known headings - they are
/// reused verbatim from the already-approved provenance, exactly the same "promotion" pattern
/// <c>DOC-0001.occurrence-gold.v1.json</c> already uses for its own prior-approved data. The new
/// title entry does not carry an independently-verified span (its exact character offset within the
/// single collapsed paragraph could not be safely reverse-engineered - see commit history); this is
/// explicitly a text-only occurrence for that one entry, which
/// <see cref="CanonicalGoldConsolidationTests"/>'s legacy occurrence path does not require a span for.
/// </para>
/// </summary>
public sealed class Doc0205GoldCorrectionDocumentTitleR2Tests
{
    private const string SourceDocx = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SourceSha256 = "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878";
    private const string PriorOccurrenceMaterialization = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json";
    private const int PriorHeadingCount = 71;
    private const int ApprovedHeadingTotal = 72;

    [Fact]
    public void Title_text_is_verified_verbatim_against_the_real_paragraph_before_use()
    {
        var (_, titleText, _) = LocateVerifiedTitle();
        Assert.Equal("NGHỊ ĐỊNH Quản lý, kết nối và chia sẻ dữ liệu số của cơ quan nhà nước", titleText);
    }

    [Fact]
    public void Materialize_canonical_occurrence_gold_with_the_title_added()
    {
        var priorPath = TestRepository.Path(PriorOccurrenceMaterialization);
        var priorText = File.ReadAllText(priorPath);
        using var prior = JsonDocument.Parse(priorText);
        var priorRoot = prior.RootElement;
        Assert.Equal(SourceSha256, priorRoot.GetProperty("sourceSha256").GetString());
        var bindings = priorRoot.GetProperty("bindings").EnumerateArray().ToArray();
        Assert.Equal(PriorHeadingCount, bindings.Length);
        Assert.All(bindings, b => Assert.True(b.GetProperty("exactRawSubstringVerified").GetBoolean()));

        var (precedesFirstKnown, titleText, titleRole) = LocateVerifiedTitle();
        Assert.True(precedesFirstKnown);

        // Reuse the 71 prior-approved entries verbatim (their own clean rawSourceText, role, level,
        // and span) - not re-derived, per the promotion pattern DOC-0001.occurrence-gold.v1.json
        // already established for its own prior-approved data.
        var priorEntries = bindings.Select(b => new
        {
            sourceId = b.GetProperty("sourceId").GetString(),
            exactText = b.GetProperty("rawSourceText").GetString(),
            role = b.GetProperty("semanticRole").GetString(),
            level = b.GetProperty("level").GetInt32(),
            utf16Span = new
            {
                start = b.GetProperty("headingSpan").GetProperty("start").GetInt32(),
                end = b.GetProperty("headingSpan").GetProperty("end").GetInt32(),
            },
        }).ToArray();

        var allEntries = new object[]
        {
            new
            {
                sourceId = "body[1]/p[4]",
                exactText = titleText,
                role = titleRole,
                level = 1,
                utf16Span = (object?)null,
                spanIndependentlyVerified = false,
                spanNote = "Text verified verbatim against the real paragraph via ParagraphWalker + OpenXml InnerText; its exact character offset within the single collapsed paragraph was not independently reverse-engineered and is intentionally omitted rather than guessed.",
            },
        }.Concat(priorEntries.Select(e => (object)new
        {
            e.sourceId,
            e.exactText,
            e.role,
            e.level,
            utf16Span = (object?)e.utf16Span,
            spanIndependentlyVerified = true,
            spanNote = (string?)null,
        })).ToArray();

        Assert.Equal(ApprovedHeadingTotal, allEntries.Length);

        var headings = Enumerable.Range(1, allEntries.Length)
            .Select(i => new
            {
                sourceAlias = $"S{i:0000}",
                selectionMode = "WHOLE_ALIAS",
                semanticRole = (string?)null,
                verbatimText = (string?)null,
                occurrence = (string?)null,
                leftExactContext = (string?)null,
                rightExactContext = (string?)null,
                parentSourceAlias = (string?)null,
            }).ToArray();

        var boundOccurrences = allEntries.Select((entry, i) => new
        {
            headingOrdinal = i,
            sourceAlias = $"S{i + 1:0000}",
            sourceId = ((dynamic)entry).sourceId,
            exactText = ((dynamic)entry).exactText,
            semanticRole = ((dynamic)entry).role,
            level = ((dynamic)entry).level,
            utf16Span = ((dynamic)entry).utf16Span,
            spanIndependentlyVerified = ((dynamic)entry).spanIndependentlyVerified,
            spanNote = ((dynamic)entry).spanNote,
        }).ToArray();

        FreezeArtifact.AssertJson(
            "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence",
            "DOC-0205.occurrence-gold.v1.json",
            new
            {
                artifactKind = "a99_pdf_occurrence_gold",
                schemaVersion = "a99-pdf-occurrence-gold-v1",
                documentId = "DOC-0205",
                sourcePath = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx",
                sourceSha256 = SourceSha256,
                semanticHeadingTotal = ApprovedHeadingTotal,
                semanticHeadingTotalAuthority = new
                {
                    authority = "USER_APPROVED_SEMANTIC_TOTAL",
                    decidedAt = "2026-09-23",
                    reviewer = "USER",
                },
                occurrenceAuthority = new
                {
                    authority = "PRIOR_USER_APPROVED_OCCURRENCE_MATERIALIZATION_PLUS_ONE_HUMAN_APPROVED_ADDITION",
                    priorMaterialization = PriorOccurrenceMaterialization,
                    priorMaterializationHeadingCount = PriorHeadingCount,
                    addedHeading = new
                    {
                        text = titleText,
                        role = titleRole,
                        reason = "The .key file this document's 71-heading answer key derives from (keys/legal-human/025_ND_47-2020_Chia_se_du_lieu_so.key) states its own scope as 'Lấy các đoạn <strong> Chương/Mục/Điều', which by design excludes the document's own title block. This is the one heading the approved semantic total (72) already implied but no prior occurrence artifact materialized.",
                        verificationMethod = "ParagraphWalker.Enumerate + DocumentFormat.OpenXml.Wordprocessing.Paragraph.InnerText against the real source docx - not hand-typed.",
                        spanIndependentlyVerified = false,
                    },
                    decidedAt = "2026-09-23",
                    reviewer = "USER_APPROVED_PROVENANCE_PLUS_HUMAN_APPROVED_ADDITION",
                },
                finalAuthority = "PROMOTED_FROM_PRIOR_USER_APPROVAL_PLUS_HUMAN_APPROVED_ADDITION",
                capabilities = new
                {
                    semanticEvaluable = true,
                    occurrenceEvaluable = true,
                    hierarchyEvaluable = false,
                },
                providerCalls = 0,
                modelCalls = 0,
                headings,
                boundOccurrences,
                promotionProvenance = new
                {
                    priorOccurrenceMaterialization = PriorOccurrenceMaterialization,
                    priorOccurrenceMaterializationSha256 = CanonicalArtifactHash.OfText(priorText),
                    approvalAuthority = "USER_APPROVAL_IN_CONVERSATION",
                },
            });
    }

    private static (bool PrecedesFirstKnown, string TitleText, string Role) LocateVerifiedTitle()
    {
        var path = TestRepository.Path(SourceDocx);
        Assert.Equal(SourceSha256, CanonicalArtifactHash.OfBytes(path));

        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var paragraphs = ParagraphWalker.Enumerate(body, new ExtractionOptions()).ToArray();
        var target = Assert.Single(paragraphs, p => p.StableId == "body[1]/p[4]");
        var text = target.Element.InnerText;

        const string titleLine1 = "NGHỊ ĐỊNH";
        const string titleLine2 = "Quản lý, kết nối và chia sẻ dữ liệu số của cơ quan nhà nước";
        var line1Start = text.IndexOf(titleLine1, StringComparison.Ordinal);
        Assert.True(line1Start >= 0, "title line 1 not found verbatim in the real paragraph text");
        var line2Start = text.IndexOf(titleLine2, line1Start, StringComparison.Ordinal);
        Assert.True(line2Start >= 0, "title line 2 not found verbatim in the real paragraph text after line 1");

        var chuongIStart = text.IndexOf("Chương I", line2Start, StringComparison.Ordinal);
        Assert.True(chuongIStart > line2Start, "the title must precede the first Chương I heading");

        return (true, $"{titleLine1} {titleLine2}", "DocumentTitle");
    }
}
