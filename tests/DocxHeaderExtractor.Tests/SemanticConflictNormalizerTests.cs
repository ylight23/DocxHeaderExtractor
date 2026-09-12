using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticConflictNormalizerTests
{
    [Fact]
    public void One_proposal_passes_through_unchanged()
    {
        var aliases = Aliases(("p1", "Heading"));
        var proposal = Whole("S0001", "SECTION");

        var result = SemanticConflictNormalizer.Normalize([proposal], aliases);

        Assert.Equal([proposal], result.NormalizedProposals);
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.SemanticProposalInputCount);
        Assert.Equal(1, result.SemanticProposalNormalizedCount);
        Assert.Equal(0, result.ExactSemanticDuplicatesCollapsed);
        Assert.Equal(0, result.SemanticConflictProposalCount);
    }

    [Fact]
    public void Identical_duplicate_proposals_collapse_to_one()
    {
        var aliases = Aliases(("p1", "Heading"));
        var proposal = Whole("S0001", "SECTION");

        var result = SemanticConflictNormalizer.Normalize([proposal, proposal], aliases);

        Assert.Single(result.NormalizedProposals);
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.ExactSemanticDuplicatesCollapsed);
    }

    [Fact]
    public void Three_identical_duplicates_collapse_deterministically()
    {
        var aliases = Aliases(("p1", "Heading"));
        var proposal = Whole("S0001", "SECTION");

        var result = SemanticConflictNormalizer.Normalize([proposal, proposal, proposal], aliases);

        Assert.Single(result.NormalizedProposals);
        Assert.Equal(2, result.ExactSemanticDuplicatesCollapsed);
        Assert.Equal(proposal, result.NormalizedProposals[0]);
    }

    [Fact]
    public void Whole_alias_text_echo_drift_is_not_a_semantic_conflict()
    {
        var aliases = Aliases(("p1", "Heading"));
        var first = Whole("S0001", "SECTION") with { VerbatimText = "Heading" };
        var second = Whole("S0001", "SECTION") with { VerbatimText = "Héading" };

        var result = SemanticConflictNormalizer.Normalize([first, second], aliases);

        Assert.Single(result.NormalizedProposals);
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.ExactSemanticDuplicatesCollapsed);
    }

    [Fact]
    public void S0239_article_vs_chapter_conflict_is_withheld()
    {
        var aliases = Aliases(("body[1]/p[250]", "Chương III"));
        var article = Whole("S0001", "ARTICLE");
        var chapter = Whole("S0001", "CHAPTER");

        var result = SemanticConflictNormalizer.Normalize([article, chapter], aliases);

        Assert.Empty(result.NormalizedProposals);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("source:body[1]/p[250]:0:10", conflict.PhysicalSourceIdentity);
        Assert.Equal(["ARTICLE", "CHAPTER"], conflict.Alternatives
            .Select(item => item.SemanticRole).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(2, result.SemanticConflictProposalCount);
    }

    [Fact]
    public void Same_text_in_different_aliases_remains_two_occurrences()
    {
        var aliases = Aliases(("p1", "Heading"), ("p2", "Heading"));
        var first = Whole("S0001", "SECTION");
        var second = Whole("S0002", "SECTION");

        var result = SemanticConflictNormalizer.Normalize([second, first], aliases);

        Assert.Equal(["S0001", "S0002"], result.NormalizedProposals.Select(item => item.SourceAlias));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Scope_and_structural_type_disagreement_is_not_silently_resolved()
    {
        var aliases = Aliases(("p1", "Heading"));
        var primary = Whole("S0001", "SECTION") with { Scope = "primary", StructuralType = "chapter" };
        var continuation = Whole("S0001", "SECTION") with { Scope = "continuation", StructuralType = "chapter" };

        var result = SemanticConflictNormalizer.Normalize([primary, continuation], aliases);

        Assert.Empty(result.NormalizedProposals);
        Assert.Single(result.Conflicts);
    }

    [Fact]
    public void Ordering_and_conflict_alternatives_are_independent_of_input_order()
    {
        var aliases = Aliases(("p1", "One"), ("p2", "Two"));
        var one = Whole("S0001", "SECTION");
        var article = Whole("S0002", "ARTICLE");
        var chapter = Whole("S0002", "CHAPTER");

        var forward = SemanticConflictNormalizer.Normalize([chapter, one, article], aliases);
        var reverse = SemanticConflictNormalizer.Normalize([article, one, chapter], aliases);

        Assert.Equal(JsonSerializer.Serialize(forward.NormalizedProposals),
            JsonSerializer.Serialize(reverse.NormalizedProposals));
        Assert.Equal(JsonSerializer.Serialize(forward.Conflicts),
            JsonSerializer.Serialize(reverse.Conflicts));
    }

    [Fact]
    public void Normalization_is_idempotent_for_conflict_free_results()
    {
        var aliases = Aliases(("p1", "One"), ("p2", "Two"));
        var first = Whole("S0001", "SECTION");
        var second = Whole("S0002", "ARTICLE");

        var once = SemanticConflictNormalizer.Normalize([second, first, first], aliases);
        var twice = SemanticConflictNormalizer.Normalize(once.NormalizedProposals, aliases);

        Assert.Equal(JsonSerializer.Serialize(once.NormalizedProposals),
            JsonSerializer.Serialize(twice.NormalizedProposals));
        Assert.Empty(twice.Conflicts);
        Assert.Equal(once.NormalizedProposals.Count, twice.NormalizedProposals.Count);
        Assert.Equal(0, twice.ExactSemanticDuplicatesCollapsed);
    }

    [Fact]
    public void Production_entry_point_withholds_conflicts_before_the_binder()
    {
        var result = CanonicalSemanticProductionEntryPoint.Run(new(
            Catalog(("p1", "Heading")),
            [Whole("S0001", "ARTICLE"), Whole("S0001", "CHAPTER")],
            "source-hash",
            [new CanonicalSemanticPageEvidence("P0001", true, 0, "docx-text")],
            [], [], [], []));

        Assert.Empty(result.TextPipeline.BoundHeadings);
        Assert.Empty(result.TextPipeline.BindingObservations);
        Assert.Empty(result.NormalizedModelProposals);
        Assert.Single(result.SemanticConflicts);
        Assert.Equal(2, result.SemanticProposalInputCount);
        Assert.Equal(0, result.SemanticProposalNormalizedCount);
        Assert.Equal(1, result.SemanticConflictCount);
        Assert.Equal(2, result.SemanticConflictProposalCount);
        Assert.Contains(result.StageLedger, entry =>
            entry.Stage == "SEMANTIC_CONFLICT_CHECK" &&
            entry.Status == "CONFLICTS_WITHHELD" &&
            entry.FirstLossCode == "SEMANTIC_PROPOSAL_CONFLICT");
    }

    private static CanonicalSemanticProposal Whole(string alias, string role) =>
        new(alias, true, null, SemanticRole: role, SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);

    private static IReadOnlyList<SemanticSourceAlias> Aliases(params (string Id, string Text)[] units) =>
        SemanticSourceAliasCatalog.FromCatalog(Catalog(units));

    private static DocumentSourceCatalog Catalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id, index + 1, unit.Text,
            new SourceAnchor { SourceType = "test", ParagraphId = unit.Id },
            new StructuralSpan(0, unit.Text.Length))));
}
