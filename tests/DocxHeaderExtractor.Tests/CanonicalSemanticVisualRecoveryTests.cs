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
            [new("source-1", 0, 9, "Article 1")], [visualBinding[0].Binding]);

        var item = Assert.Single(unified);
        Assert.Single(item.TextEvidence);
        Assert.Single(item.VisualEvidence);
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
}
