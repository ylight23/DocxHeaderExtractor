using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLayoutEvidenceOutlineTests
{
    [Fact]
    public void AnalystBudgetCoversEveryPageBeforeTakingSecondBlock()
    {
        var blocks = Enumerable.Range(1, 28)
            .SelectMany(page => new[]
            {
                Block($"p{page}-a", page, 700),
                Block($"p{page}-b", page, 650),
            })
            .ToArray();

        var selection = PdfLayoutEvidenceOutline.SelectAnalystCandidates(blocks, 40);

        Assert.Equal(56, selection.Available);
        Assert.Equal(28, selection.AvailablePages);
        Assert.Equal(40, selection.Selected.Count);
        Assert.Equal(28, selection.SelectedPages);
        Assert.All(Enumerable.Range(1, 28), page =>
            Assert.Contains(selection.Selected, block => block.Page == page));
    }

    [Fact]
    public void AnalystSelectionWithFullBudgetKeepsEveryCandidate()
    {
        var blocks = Enumerable.Range(1, 15).Select(page => Block($"p{page}", page, 700)).ToArray();

        var selection = PdfLayoutEvidenceOutline.SelectAnalystCandidates(blocks, blocks.Length);

        Assert.Equal(blocks.Length, selection.Selected.Count);
        Assert.Equal(blocks.Select(b => b.Id), selection.Selected.Select(b => b.Id));
    }

    [Fact]
    public void RankedSelectionUsesPlanOrderWithoutRemovingTheCandidatePool()
    {
        var blocks = new[] { Block("low", 1, 700), Block("high", 2, 700) };
        var ranked = new[]
        {
            new RankedCandidate("high", 2, "high", 0.9, 0.2, ModelTier.Small, [], [], []),
            new RankedCandidate("low", 1, "low", 0.2, 0.8, ModelTier.Frontier, [], [], []),
        };

        var selection = PdfLayoutEvidenceOutline.SelectRankedCandidates(blocks, ranked, 1);

        Assert.Equal(2, selection.Available);
        Assert.Equal(new[] { "high" }, selection.Selected.Select(block => block.Id));
    }

    [Fact]
    public void VisualSelectionDefersSemanticUncertaintyToSecondTextPass()
    {
        var strong = Block("strong", 1, 700, "Article 1 Scope and purpose");
        var uncertain = Block("uncertain", 1, 680, "Applicability");
        var ranked = new[]
        {
            new RankedCandidate("strong", 1, strong.Text, .94, .20, ModelTier.Deterministic, [], [], []),
            new RankedCandidate("uncertain", 1, uncertain.Text, .42, .80, ModelTier.Frontier, [], [], ["boundary"]),
        };
        var decisions = new[]
        {
            new PdfBlockDecision("strong", PdfBlockRole.HeadingTopic, .95, "semantic"),
            new PdfBlockDecision("uncertain", PdfBlockRole.Uncertain, 0, "ambiguous"),
        };

        var selected = PdfLayoutEvidenceOutline.SelectVisualEvidenceCandidates([strong, uncertain], ranked, decisions);

        Assert.Empty(selected);
    }

    [Fact]
    public void VisualSelectionDoesNotTreatTightHighScoreCompositeAsConflict()
    {
        var composite = Block("composite", 1, 700, "Chapter I General Provisions");
        var ranked = new[]
        {
            new RankedCandidate("composite", 1, composite.Text, .90, .55, ModelTier.Medium,
                ["labelled_numbering_marker", "canonical_marker_title"], [], ["multi_line_boundary"]),
        };
        var decisions = new[] { new PdfBlockDecision("composite", PdfBlockRole.HeadingTopic, .95, "semantic") };

        var selected = PdfLayoutEvidenceOutline.SelectVisualEvidenceCandidates([composite], ranked, decisions);

        Assert.Empty(selected);
    }

    [Fact]
    public void VisualSelectionRequiresEvidenceForMarkerOnlySource()
    {
        var markerOnly = Block("marker", 1, 700, "Article 11");
        var ranked = new[]
        {
            new RankedCandidate("marker", 1, markerOnly.Text, .94, .20, ModelTier.Deterministic, ["labelled_numbering_marker"], [], []),
        };
        var decisions = new[] { new PdfBlockDecision("marker", PdfBlockRole.HeadingTopic, .95, "semantic") };

        var selected = PdfLayoutEvidenceOutline.SelectVisualEvidenceCandidates([markerOnly], ranked, decisions);

        Assert.Equal("marker", Assert.Single(selected).Id);
    }

    [Fact]
    public void BroadCandidatesKeepCandidateStyleWithoutSparseStyleGate()
    {
        var broadStyle = new PdfStyleKey(12, "serif", "black");
        var sparseStyle = new PdfStyleKey(16, "serif", "blue");
        var profile = new PdfStyleClusterProfile(
            new PdfStyleKey(10, "body", "black"), [],
            new HashSet<PdfStyleKey> { broadStyle, sparseStyle },
            new HashSet<PdfStyleKey>(), new HashSet<PdfStyleKey>());
        var blocks = new[]
        {
            Block("b1", 1, 700, "Introduction", broadStyle),
            Block("b2", 1, 680, "This is a deliberately long body sentence whose only purpose is to exceed the prose threshold and end with a full stop.", broadStyle),
            Block("b3", 2, 700, "Chapter Two", sparseStyle),
        };

        var candidates = PdfLayoutEvidenceOutline.BuildBroadCandidates(blocks, profile);

        Assert.Equal(new[] { "b1", "b3" }, candidates.Select(b => b.Id));
    }

    [Fact]
    public void WideAuditCandidatesAllowBlocksOutsideLearnedCandidateStyles()
    {
        var bodyStyle = new PdfStyleKey(10, "Body", "000000");
        var profile = new PdfStyleClusterProfile(
            bodyStyle, [], new HashSet<PdfStyleKey>(), new HashSet<PdfStyleKey>(), new HashSet<PdfStyleKey>());
        var blocks = new[]
        {
            Block("b1", 1, 700, "A long section title that the learned style profile did not retain", bodyStyle),
            Block("b2", 1, 680, "A B C D E", bodyStyle),
        };

        Assert.Empty(PdfLayoutEvidenceOutline.BuildBroadCandidates(blocks, profile));
        Assert.Equal(new[] { "b1" }, PdfLayoutEvidenceOutline.BuildWideAuditCandidates(blocks).Select(b => b.Id));
    }

    [Fact]
    public void AnalystBudgetKeepsPriorityCandidatesBeforeWideFillers()
    {
        var blocks = Enumerable.Range(1, 8)
            .Select(page => Block($"p{page}", page, 700))
            .ToArray();

        var selection = PdfLayoutEvidenceOutline.SelectAnalystCandidates(
            blocks, 4, new HashSet<string> { "p2", "p4", "p6" });

        Assert.Equal(new[] { "p1", "p2", "p4", "p6" }, selection.Selected.Select(b => b.Id));
    }

    [Fact]
    public void AnalystBudgetRanksSupplementBeforeUnrankedFallbackWithoutDiscardingIt()
    {
        var blocks = new[]
        {
            Block("seed", 1, 700),
            Block("fallback", 2, 700),
            Block("ranked-one", 3, 700),
            Block("ranked-two", 4, 700),
        };
        var selection = PdfLayoutEvidenceOutline.SelectAnalystCandidates(
            blocks, 3, new HashSet<string> { "seed" },
            new Dictionary<string, int> { ["ranked-one"] = 100, ["ranked-two"] = 100 });

        Assert.Equal(new[] { "seed", "ranked-one", "ranked-two" }, selection.Selected.Select(b => b.Id));
    }

    [Fact]
    public void SupplementRankFavorsStructuralMarkersOverPlainFragments()
    {
        var marked = Block("marked", 1, 700, "2.1 Scope of work");
        var plain = Block("plain", 1, 680, "Scope of work");

        Assert.True(
            PdfLayoutEvidenceOutline.ScoreSupplementForAnalyst(marked) >
            PdfLayoutEvidenceOutline.ScoreSupplementForAnalyst(plain));
    }

    [Theory]
    [InlineData("Article 1 1 Security assessment", "article:11")]
    [InlineData("Abschnitt IV GENERAL PROVISIONS", "abschnitt:iv")]
    [InlineData("Table 1 1 Financial results", "table:11")]
    public void LooseMarkerAuditNormalizesSeparatedPdfDigits(string text, string expected)
    {
        Assert.Equal(expected, PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(text));
    }

    [Fact]
    public void MarkerSpanReconstructionCutsTitleFromLongConvertedParagraph()
    {
        const string source = "Page noise and prior body. Article 11 Security assessment of important systems 1. The assessment is performed before approval.";

        var span = PdfLayoutEvidenceOutline.FindMarkerHeadingSpanForAudit(source, "Article 1 1 damaged PDF title");

        Assert.NotNull(span);
        Assert.Equal("Article 11 Security assessment of important systems", source[span!.Start..span.End]);
    }


    [Fact]
    public void SupplementKeepsHeadingLikeAtomicLineWhenGroupingHasNoCandidate()
    {
        var line = new PdfLine(1, 700, 12, "Chapter I General provisions", 0.8, "", 0, 72, 300, "serif", "black");
        var annotation = new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate");

        var candidates = PdfLayoutEvidenceOutline.BuildSupplementCandidates([annotation], []);

        var candidate = Assert.Single(candidates);
        Assert.Equal("s-line-1", candidate.Id);
        Assert.Equal("Chapter I General provisions", candidate.Text);
    }

    [Fact]
    public void SupplementReconstructsAdjacentHeadingFragmentsAcrossVisualStyles()
    {
        var first = new PdfLine(1, 700, 12, "Chapter I", 0.8, "", 0, 72, 180, "serif-bold", "black");
        var second = new PdfLine(1, 684, 10, "General provisions", 0.1, "", 0, 92, 300, "serif", "black");
        var annotations = new[]
        {
            new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate"),
        };

        var candidates = PdfLayoutEvidenceOutline.BuildSupplementCandidates(annotations, []);

        Assert.Contains(candidates, c => c.Text == "Chapter I General provisions");
    }

    private static PdfSemanticBlock Block(string id, int page, double y, string? text = null, PdfStyleKey? style = null)
    {
        var chosen = style ?? new PdfStyleKey(14, "serif", "0.00,0.20,0.40");
        var line = new PdfLine(page, y, chosen.FontSizeBucket, text ?? id, 0.8, "", 0, 72, 300, chosen.FontName, chosen.FillColorKey);
        return new PdfSemanticBlock(id, [line], chosen, page, y, y, 72, 300, text ?? id);
    }
}
