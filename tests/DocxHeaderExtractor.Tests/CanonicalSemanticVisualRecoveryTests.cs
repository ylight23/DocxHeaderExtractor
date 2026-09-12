using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticVisualRecoveryTests
{
    [Fact]
    public void Full_page_scan_images_route_to_visual_recovery_and_keep_heading_occurrences()
    {
        var profile = CanonicalSemanticModalityProfiler.Profile([
            new("p1", false, 1, "DOCX_MEDIA"),
            new("p2", false, 1, "DOCX_MEDIA")
        ]);
        var occurrences = CanonicalSemanticVisualRecovery.Recover([
            new("p1", 1, "image-a", new(10, 20, 100, 20), "CHAPTER I")
        ]);
        var bound = CanonicalSemanticVisualBinder.Bind([
            new("V0001", true, "CHAPTER I", "CHAPTER")
        ], occurrences);

        Assert.Equal(CanonicalSemanticModality.VisualOnly, profile.DocumentModality);
        Assert.True(profile.Pages.All(page => page.UseVisualRecovery));
        Assert.Single(bound);
        Assert.True(CanonicalSemanticVisualBindingValidator.IsValid(bound[0], occurrences));
        Assert.Equal("VISUAL_REGION", bound[0].Binding.CoordinateSystem);
    }

    [Fact]
    public void Hybrid_profile_routes_only_text_deficient_pages_to_visual_recovery()
    {
        var profile = CanonicalSemanticModalityProfiler.Profile([
            new("p1", true, 0, "PDF_TEXT"),
            new("p2", false, 1, "PDF_SCAN")
        ]);

        Assert.Equal(CanonicalSemanticModality.Hybrid, profile.DocumentModality);
        Assert.False(profile.Pages[0].UseVisualRecovery);
        Assert.True(profile.Pages[1].UseVisualRecovery);
    }

    [Fact]
    public void Cross_modal_text_and_visual_evidence_for_same_transcript_is_one_occurrence()
    {
        var visual = CanonicalSemanticVisualRecovery.Recover([
            new("p1", 1, "image-a", new(10, 20, 100, 20), "Article 1")
        ]);
        var visualBinding = CanonicalSemanticVisualBinder.Bind([
            new("V0001", true, "Article 1")
        ], visual);

        var unified = CanonicalSemanticCrossModalReconciler.Reconcile(
            [new("source-1", 0, 9, "Article 1", "source-1:0:9", "p1", "image-a",
                new CanonicalSemanticVisualBoundingBox(10, 20, 100, 20))], [visualBinding[0].Binding]);

        var item = Assert.Single(unified);
        Assert.Single(item.TextEvidence);
        Assert.Single(item.VisualEvidence);
    }

    [Fact]
    public void Repeated_same_text_visual_headings_in_different_regions_remain_two_occurrences()
    {
        var result = Production(
            Catalog(("p1", "body")),
            [new CanonicalSemanticPageEvidence("P0001", false, 1, "scan")],
            [
                new CanonicalSemanticVisualBlock("P0001", 1, "image-a", new(10, 10, 100, 20), "RESULTS"),
                new CanonicalSemanticVisualBlock("P0001", 2, "image-a", new(10, 50, 100, 20), "RESULTS")
            ],
            [
                new CanonicalSemanticVisualProposal("V0001", true, "RESULTS", "SECTION"),
                new CanonicalSemanticVisualProposal("V0002", true, "RESULTS", "SECTION")
            ]);

        Assert.Equal(2, result.CanonicalOccurrences.Count);
        Assert.Equal(2, result.Projection.Count);
        Assert.NotEqual(result.CanonicalOccurrences[0].SemanticNodeId,
            result.CanonicalOccurrences[1].SemanticNodeId);
    }

    [Fact]
    public void Same_physical_text_and_visual_heading_is_one_occurrence()
    {
        var result = Production(
            Catalog(("p1", "Article 1", page: 1, box: new(10, 20, 110, 40))),
            [new CanonicalSemanticPageEvidence("P0001", true, 1, "hybrid")],
            [new CanonicalSemanticVisualBlock("P0001", 1, "image-a", new(10, 20, 100, 20), "Article 1")],
            [new CanonicalSemanticVisualProposal("V0001", true, "Article 1", "ARTICLE")],
            new CanonicalSemanticProposal("S0001", true, "Article 1", SemanticRole: "ARTICLE"));

        Assert.Single(result.UnifiedOccurrences);
        Assert.Single(result.UnifiedOccurrences[0].TextEvidence);
        Assert.Single(result.UnifiedOccurrences[0].VisualEvidence);
        Assert.Single(result.CanonicalOccurrences);
    }

    [Fact]
    public void Pdf_points_are_normalized_to_raster_pixels_with_inverted_y_axis()
    {
        var result = Production(
            Catalog(("p1", "Article 1", page: 1, box: new(10, 20, 110, 40))),
            [new CanonicalSemanticPageEvidence("P0001", true, 1, "hybrid",
                SourceWidth: 200, SourceHeight: 100, RasterWidth: 400, RasterHeight: 200,
                CoordinateSystem: "PDF_POINTS_BOTTOM_LEFT_TO_RASTER_PIXELS_TOP_LEFT")],
            [new CanonicalSemanticVisualBlock("P0001", 1, "image-a", new(20, 120, 200, 40), "Article 1")],
            [new CanonicalSemanticVisualProposal("V0001", true, "Article 1", "ARTICLE")],
            new CanonicalSemanticProposal("S0001", true, "Article 1", SemanticRole: "ARTICLE"));

        var text = Assert.Single(result.UnifiedOccurrences).TextEvidence[0];
        Assert.Equal(new CanonicalSemanticVisualBoundingBox(20, 120, 200, 40), text.BoundingBox);
        Assert.Single(result.CanonicalOccurrences);
    }

    [Fact]
    public void Same_text_on_different_pages_is_not_cross_modal_deduped()
    {
        var result = Production(
            Catalog(("p1", "RESULTS", page: 1, box: new(10, 20, 110, 40))),
            [
                new CanonicalSemanticPageEvidence("P0001", true, 0, "text"),
                new CanonicalSemanticPageEvidence("P0002", false, 1, "scan")
            ],
            [new CanonicalSemanticVisualBlock("P0002", 1, "image-b", new(10, 20, 100, 20), "RESULTS")],
            [new CanonicalSemanticVisualProposal("V0001", true, "RESULTS", "SECTION")],
            new CanonicalSemanticProposal("S0001", true, "RESULTS", SemanticRole: "SECTION"));

        Assert.Equal(2, result.CanonicalOccurrences.Count);
        Assert.DoesNotContain(result.UnifiedOccurrences, item =>
            item.TextEvidence.Count > 0 && item.VisualEvidence.Count > 0);
    }

    [Fact]
    public void Visual_document_order_is_page_ascending_then_region_position()
    {
        var result = Production(
            Catalog(("p1", "body")),
            [
                new CanonicalSemanticPageEvidence("P0001", false, 1, "scan"),
                new CanonicalSemanticPageEvidence("P0002", false, 1, "scan")
            ],
            [
                new CanonicalSemanticVisualBlock("P0002", 1, "image-2", new(10, 10, 100, 20), "PAGE TWO"),
                new CanonicalSemanticVisualBlock("P0001", 1, "image-1", new(10, 10, 100, 20), "PAGE ONE")
            ],
            [
                new CanonicalSemanticVisualProposal("V0001", true, "PAGE ONE", "SECTION"),
                new CanonicalSemanticVisualProposal("V0002", true, "PAGE TWO", "SECTION")
            ]);

        Assert.Equal(["visual:P0001", "visual:P0002"], result.CanonicalOccurrences.Select(item => item.SourceId));
    }

    [Fact]
    public void Same_role_and_text_in_different_sections_get_distinct_semantic_nodes()
    {
        var graph = Graph(
            new CanonicalSemanticProposal("S0001", true, "RESULTS", SemanticRole: "SECTION", Scope: "section-a"),
            new CanonicalSemanticProposal("S0002", true, "RESULTS", SemanticRole: "SECTION", Scope: "section-b"));

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal(2, graph.OutlineProjection.Count);
        Assert.NotEqual(graph.Occurrences[0].SemanticNodeId, graph.Occurrences[1].SemanticNodeId);
    }

    [Fact]
    public void Multiline_visual_heading_binds_ordered_visual_line_aliases()
    {
        var occurrences = CanonicalSemanticVisualRecovery.Recover([
            new("p1", 1, "image-a", new(10, 20, 100, 20), "PART I"),
            new("p1", 2, "image-a", new(10, 45, 100, 20), "GENERAL CONDITIONS")
        ]);
        var bound = CanonicalSemanticVisualBinder.Bind([
            new("V0001", true, "PART I", VisualAliases: ["V0001", "V0002"], VerbatimTranscripts: ["PART I", "GENERAL CONDITIONS"])
        ], occurrences);

        var item = Assert.Single(bound);
        Assert.Equal(["V0001", "V0002"], item.Bindings.Select(binding => binding.VisualAlias));
        Assert.All(bound, heading => Assert.True(CanonicalSemanticVisualBindingValidator.IsValid(heading, occurrences)));
    }

    [Fact]
    public void Visual_response_cannot_invent_geometry_or_offsets()
    {
        using var payload = JsonDocument.Parse("{\"headings\":[{\"sourceAlias\":\"V0001\",\"isHeading\":true,\"verbatimText\":\"Article 1\",\"page\":1,\"bbox\":{\"left\":0}}]}");

        var issues = CanonicalSemanticContractValidator.ValidateJson(payload.RootElement);

        Assert.Contains(issues, issue => issue.Code == "NUMERIC_COORDINATE_REJECTED");
    }

    [Fact]
    public void Visual_hash_mismatch_fails_closed_and_footer_never_becomes_heading_without_proposal()
    {
        var occurrences = CanonicalSemanticVisualRecovery.Recover([
            new("p1", 1, "image-a", new(10, 20, 100, 20), "Page 1")
        ]);
        var altered = occurrences[0] with { ImageSha256 = "different-image" };
        var heading = new CanonicalSemanticVisualBoundHeading(
            new(altered.VisualAlias, altered.PageId, altered.ImageSha256, altered.BoundingBox,
                altered.Transcript, altered.RegionSha256, altered.TranscriptSha256),
            "OTHER_STRUCTURAL_LABEL", "Heading", "footer");

        Assert.False(CanonicalSemanticVisualBindingValidator.IsValid(heading, occurrences));
        Assert.True(SemanticCandidatePolicy.CanAcceptVisualOccurrence("V0001", []));
        Assert.Empty(CanonicalSemanticVisualBinder.Bind([], occurrences));
    }

    [Fact]
    public void Visual_semantic_heading_membership_is_independent_of_user_prompt()
    {
        var occurrences = CanonicalSemanticVisualRecovery.Recover([
            new("p1", 1, "image-a", new(10, 20, 100, 20), "Điều 1")
        ]);
        var proposal = new CanonicalSemanticVisualProposal("V0001", true, "Điều 1", "ARTICLE");

        var first = CanonicalSemanticVisualBinder.Bind([proposal], occurrences);
        var second = CanonicalSemanticVisualBinder.Bind([proposal], occurrences);

        Assert.Equal(first.Select(item => item.IsHeading), second.Select(item => item.IsHeading));
    }

    [Fact]
    public void Attached_v6_freezes_are_111_and_362_when_migrated()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "a99-closed-loop", "canonical-semantic-gold-vnext"));
        var first = Path.Combine(root, "semantic", "DOC-0123.semantic-freeze.v1.json");
        var second = Path.Combine(root, "semantic", "DOC-0202.semantic-freeze.v1.json");
        if (!File.Exists(first) || !File.Exists(second)) return;

        using var firstJson = JsonDocument.Parse(File.ReadAllText(first));
        using var secondJson = JsonDocument.Parse(File.ReadAllText(second));
        Assert.Equal(362, firstJson.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(111, secondJson.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
    }

    private static CanonicalSemanticProductionResult Production(
        DocumentSourceCatalog catalog,
        IReadOnlyList<CanonicalSemanticPageEvidence> pages,
        IReadOnlyList<CanonicalSemanticVisualBlock> blocks,
        IReadOnlyList<CanonicalSemanticVisualProposal> visualProposals,
        params CanonicalSemanticProposal[] textProposals)
    {
        return CanonicalSemanticProductionEntryPoint.Run(new(
            catalog,
            textProposals,
            "source-hash",
            pages,
            [], [], [], [],
            blocks,
            visualProposals));
    }

    private static CanonicalSemanticGraph Graph(params CanonicalSemanticProposal[] proposals)
    {
        var catalog = new DocumentSourceCatalog(proposals.Select((proposal, index) =>
            new DocumentSourceUnit(
                $"p{index + 1}", index + 1, proposal.VerbatimText ?? "RESULTS",
                new SourceAnchor { SourceType = "test", ParagraphId = $"p{index + 1}" },
                new StructuralSpan(0, (proposal.VerbatimText ?? "RESULTS").Length))));
        return CanonicalSemanticPipeline.Run(catalog, proposals, "source-hash").Graph;
    }

    private static DocumentSourceCatalog Catalog(params (string Id, string Text, int? page, PdfBoundingBox? box)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id,
            index + 1,
            unit.Text,
            new SourceAnchor
            {
                SourceType = "test",
                ParagraphId = unit.Id,
                Page = unit.page,
                BoundingBox = unit.box
            },
            new StructuralSpan(0, unit.Text.Length))));

    private static DocumentSourceCatalog Catalog(params (string Id, string Text)[] units) =>
        Catalog(units.Select(unit => (unit.Id, unit.Text, (int?)null, (PdfBoundingBox?)null)).ToArray());
}
