using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Vision;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualTextRecoveryTests
{
    [Fact]
    public void BuildsVisualSourceFactForLargeTextLayerGap()
    {
        var lines = new[]
        {
            Line(1, 700, "Paragraph above"),
            Line(1, 640, "Paragraph below"),
        };

        var regions = PdfVisualTextRecovery.BuildRegionsForAudit(lines,
            new Dictionary<int, PdfPageBounds> { [1] = new(612, 792) });

        var region = Assert.Single(regions);
        Assert.Equal("v-gap-1-1", region.SourceId);
        Assert.Equal("document_body", region.StructuralScope);
        Assert.Contains("text_layer_gap", region.ObservedEvidence);
        Assert.Equal(0, region.Left);
        Assert.Equal(612, region.Right);
        Assert.True(region.TopY > region.BottomY);
        Assert.Equal(0, region.ContextLinesAbove);
        Assert.Equal(0, region.ContextLinesBelow);
    }

    [Fact]
    public void DoesNotCreateVisualFactForOrdinaryLineSpacing()
    {
        var lines = new[]
        {
            Line(1, 700, "Paragraph above"),
            Line(1, 686, "Paragraph below"),
        };

        var regions = PdfVisualTextRecovery.BuildRegionsForAudit(lines,
            new Dictionary<int, PdfPageBounds> { [1] = new(612, 792) });

        Assert.Empty(regions);
    }

    [Fact]
    public void VisualRegionIncludesThreeLinesOfSemanticNeighborhood()
    {
        var lines = new[]
        {
            Line(1, 760, "Above three"),
            Line(1, 740, "Above two"),
            Line(1, 720, "Above one"),
            Line(1, 650, "Below one"),
            Line(1, 630, "Below two"),
            Line(1, 610, "Below three"),
            Line(1, 590, "Below four"),
        };

        var region = Assert.Single(PdfVisualTextRecovery.BuildRegionsForAudit(lines,
            new Dictionary<int, PdfPageBounds> { [1] = new(612, 792) }));

        Assert.Equal(2, region.ContextLinesAbove);
        Assert.Equal(3, region.ContextLinesBelow);
        Assert.Contains("visual_neighborhood", region.ObservedEvidence);
        Assert.True(region.TopY > 760);
        Assert.True(region.BottomY < 590);
    }

    [Fact]
    public void MarkerSpanLossCreatesGenericBodyBandsWithoutInventingMissingText()
    {
        var lines = new[]
        {
            new PdfLine(1, 300, 14, "Article 10. Previous", 1, "", 0, 72, 400, "serif", "black"),
            new PdfLine(2, 500, 14, "Article 12. Next", 1, "", 0, 72, 400, "serif", "black"),
        };
        var pages = new Dictionary<int, PdfPageBounds> { [1] = new(612, 792), [2] = new(612, 792) };

        var regions = PdfVisualTextRecovery.BuildRegionsForAudit(lines, pages);

        Assert.Equal(6, regions.Count(region => region.SourceId.StartsWith("v-marker-gap-article-10-12", StringComparison.Ordinal)));
        Assert.All(regions.Where(region => region.SourceId.StartsWith("v-marker-gap-article-10-12", StringComparison.Ordinal)),
            region => Assert.Contains("marker_sequence_gap", region.ObservedEvidence));
    }

    [Fact]
    public void MarkerLineLocatorRetainsSpacedMarkerAsVisualSourceFact()
    {
        var lines = new[] { new PdfLine(1, 400, 14, "Article 1 1. Split marker", .9, "", 0, 72, 400, "serif", "black") };
        var regions = PdfVisualTextRecovery.BuildRegionsForAudit(lines,
            new Dictionary<int, PdfPageBounds> { [1] = new(612, 792) });

        var region = Assert.Single(regions, item => item.SourceId.StartsWith("v-marker-line-", StringComparison.Ordinal));
        Assert.Contains("labelled_marker_line", region.ObservedEvidence);
    }

    [Fact]
    public void RejectsPlaceholderVisualResponseAsUncertainEvidence()
    {
        var response = PdfVisualTextRecovery.ParseForAudit("v-gap-1-1",
            "{\"id\":\"v-gap-1-1\",\"role\":\"heading_topic\",\"confidence\":0,\"observed_text\":\"\",\"evidence\":\"visible detail\"}");

        Assert.Equal(PdfBlockRole.HeadingTopic, response.Role);
        Assert.Equal(0, response.Confidence);
        Assert.Empty(response.ObservedText);
        Assert.False(PdfVisualTextRecovery.IsUsableForRecovery(response));
    }

    [Fact]
    public void CanonicalMapFoldsComposedAndDecomposedAccentsWithoutChangingSourceIdentity()
    {
        var composed = PdfVisualTextRecovery.CanonicalForAudit("Chương IV Hoạt động");
        var decomposed = PdfVisualTextRecovery.CanonicalForAudit("Chuo\u031Bng IV Hoa\u0323t đo\u0323ng");

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void RepeatedHeaderProposalIsRejectedEvenWhenItsTextIsVisible()
    {
        var headers = new[] { PdfVisualTextRecovery.CanonicalForAudit("CÔNG BÁO/Số 775 + 776/Ngày 14-7-2018").Where(c => !char.IsDigit(c)).Aggregate("", (s, c) => s + c) };

        Assert.True(PdfVisualTextRecovery.IsRepeatedHeaderArtifactForAudit(
            "CÔNG BÁO/Số 775 + 776/Ngày 14-7-2018", headers));
        Assert.False(PdfVisualTextRecovery.IsRepeatedHeaderArtifactForAudit("Chương IV Hoạt động", headers));
    }

    [Fact]
    public void VisualSourceValidatorRejectsSubordinateBodyItemsButKeepsLegalChapter()
    {
        Assert.False(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit(
            "c) Do not provide telecommunications services"));
        Assert.True(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit(
            "Chapter IV Network Security Operations"));
        Assert.True(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit(
            "Điều 11. Thẩm định an ninh mạng đối với hệ thống thông tin quan trọng về an ninh quốc gia"));
        Assert.True(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit(
            "Điều 25. Bảo vệ an ninh mạng đối với cơ sở hạ tầng không gian mạng quốc gia, cổng kết nối mạng quốc tế"));
    }

    [Fact]
    public void VisualSourceValidatorAcceptsBareArabicMarkerOnlyWithLegalContextAndMarkerLineEvidence()
    {
        Assert.False(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit("25. A numbered body item", "legal"));
        Assert.True(PdfVisualTextRecovery.IsVisualMappedSourceEligibleForAudit("25. A legal article title", "legal",
            ["labelled_marker_line", "full_width_visual_crop"]));
    }

    [Fact]
    public void MarkerSpanReconstructionExpandsOnlyAContiguousSourceLabel()
    {
        const string original = "Preamble. Article 25. Protection of a national network";
        var start = original.IndexOf("25.", StringComparison.Ordinal);
        var reconstructed = PdfVisualTextRecovery.ReconstructMarkerSpanForAudit(original, start, original.Length, "legal");

        Assert.Equal("Article 25. Protection of a national network", reconstructed);
        Assert.Equal("25. Protection of a national network",
            PdfVisualTextRecovery.ReconstructMarkerSpanForAudit(original, start, original.Length, "document_body"));
    }

    [Fact]
    public void RepresentationAuditMarksUncoveredTextLossAtRegionGeneration()
    {
        var document = new SlimDocument
        {
            FileName = "t.docx", SourcePath = "t.docx",
            Paragraphs =
            [
                Paragraph(1, "Article 10. Previous"),
                Paragraph(2, "Article 11. Missing visual title"),
                Paragraph(3, "Article 12. Next"),
            ],
        }.Build();
        var lines = new[] { Line(4, 700, "Article 10. Previous"), Line(4, 500, "Article 12. Next") };
        var regions = new[] { new PdfVisualRegionAudit(0, "v-gap-4-1", 4, 0, 650, 612, 730, 0, 0, ["text_layer_gap"]) };
        var report = PdfVisualRepresentationAudit.EvaluateForAudit(document,
            AnswerKey.Parse("1 1 # Article 10. Previous\n2 1 # Article 11. Missing visual title\n3 1 # Article 12. Next"), lines, regions);

        var missing = Assert.Single(report.Entries);
        Assert.True(missing.VisualRepresentable);
        Assert.Equal("not-lost-before-visual-model", missing.FirstLoss);
        Assert.Equal("not-measured-without-ocr-or-vlm", missing.PixelPresence);
    }

    [Fact]
    public void OfflineVisualEvaluationReportsPerGoldFirstLoss()
    {
        const string title = "Article 11. Missing visual title";
        var coverage = new PdfVisualGoldCoverage(title, 2, false, "not-measured-without-ocr-or-vlm", [4], null, null,
            1, [new PdfVisualRegionCoverage("v-gap-4-1", 4, 0, 400, 612, 700, true, ["text_layer_gap"])], true,
            "not-lost-before-visual-model");
        var trace = new PdfVisualRecoveryTrace("v-gap-4-1", 4, "HeadingTopic", .9, title,
            "visible heading", "visual-ocr-canonical-map", title, "p2", 0, title.Length);

        var result = PdfVisualInferenceEvaluator.Evaluate([title], [trace], [coverage]);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.Recovered);
        Assert.Equal(1, entry.ObservedTextMatches);
        Assert.Equal(1, entry.SourceValidatorAccepted);
        Assert.Null(entry.FirstLoss);
    }

    [Fact]
    public void OfflineVisualEvaluationDoesNotCountProducerExcludedTraceAsProcessed()
    {
        const string title = "Article 25. Visual title";
        var coverage = new PdfVisualGoldCoverage(title, 2, false, "not-measured-without-ocr-or-vlm", [4], null, null,
            1, [new PdfVisualRegionCoverage("v-gap-4-1", 4, 0, 400, 612, 700, true, ["text_layer_gap"])], true,
            "not-lost-before-visual-model");
        var trace = new PdfVisualRecoveryTrace("v-gap-4-1", 4, "Uncertain", 0, "", "",
            "visual-producer-excluded");

        var entry = Assert.Single(PdfVisualInferenceEvaluator.Evaluate([title], [trace], [coverage]).Entries);
        Assert.Equal(0, entry.RegionsProcessed);
        Assert.Equal("visual-region-generation", entry.FirstLoss);
    }

    [Fact]
    public void CrossProducerEvaluatorDedupesCanonicalIdentityButKeepsRoleConflictVisible()
    {
        var traces = new[]
        {
            new PdfVisualRecoveryTrace("v-gap-2-1", 2, "HeadingTopic", .8, "Article 25", "", "visual-ocr-canonical-map", "Article 25", "p50", 3, 13),
            new PdfVisualRecoveryTrace("v-marker-line-2-4", 2, "HeadingTopic", .8, "Article 25", "", "visual-ocr-canonical-map", "Article 25", "p50", 3, 13),
            new PdfVisualRecoveryTrace("v-marker-line-2-5", 2, "BodySentence", .8, "Article 25", "", "visual-source-validator-rejected", "Article 25", "p50", 3, 13),
        };

        var report = PdfVisualCrossProducerEvaluator.Evaluate(traces);
        Assert.Equal(1, report.CrossProducer.CanonicalOverlap);
        Assert.Equal(1, report.CrossProducer.DuplicatesCollapsed);
        Assert.Equal(1, report.CrossProducer.RoleConflicts);
        Assert.Equal(0, report.CrossProducer.OverlapRejectedBeforeValidation);
        Assert.Equal(1, report.VisualProducerStats.Single(item => item.Producer == "marker-line").CanonicalUnique);
    }

    private static PdfLine Line(int page, double y, string text) =>
        new(page, y, 12, text, .1, "", 0, 72, 400, "serif", "black");

    private static SlimParagraph Paragraph(int index, string text) => new()
    {
        Index = index, Text = text, StableId = $"p{index}",
    };
}
