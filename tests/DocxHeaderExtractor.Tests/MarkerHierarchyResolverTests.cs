using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class MarkerHierarchyResolverTests
{
    [Fact]
    public void Resolver_builds_roman_decimal_dotted_alpha_tree_and_overrides_wrong_model_parent()
    {
        var resolved = MarkerHierarchyResolver.Resolve([
            Proposal("I. Section", ProposedRole.HeadingTopic, null),
            Proposal("3. Topic", ProposedRole.HeadingTopic, "wrong"),
            Proposal("3.2. Detail", ProposedRole.HeadingTopic, "wrong"),
            Proposal("a. Item", ProposedRole.ListItemTopic, "wrong"),
        ], new HeadingPolicy(IncludeListItemTopic: true));

        Assert.Equal([1, 2, 3, 4], resolved.Select(x => x.Level));
        Assert.Null(resolved[0].ParentId);
        Assert.Equal(resolved[0].Id, resolved[1].ParentId);
        Assert.Equal(resolved[1].Id, resolved[2].ParentId);
        Assert.Equal(resolved[2].Id, resolved[3].ParentId);
        Assert.Equal("overridden", resolved[2].Validation.ParentResolution);
    }

    [Fact]
    public void Resolver_leaves_markerless_heading_parent_unresolved()
    {
        var resolved = MarkerHierarchyResolver.Resolve([
            Proposal("I. Declared section", ProposedRole.HeadingTopic, null),
            Proposal("Visual-only title", ProposedRole.HeadingTopic, "I. Declared section"),
        ]);

        var markerless = resolved[1];
        Assert.Null(markerless.ParentId);
        Assert.False(markerless.Validation.MarkerSequenceValid);
        Assert.False(markerless.Validation.HierarchyValid);
        Assert.False(markerless.Validation.ParentValid);
        Assert.Equal("unresolved", markerless.Validation.ParentResolution);
    }

    private static ProposalValidationResult Proposal(string text, ProposedRole role, string? parent)
    {
        var source = SourceFactsBuilder.FromParagraph(new SlimParagraph { Index = text.GetHashCode(), StableId = text, Text = text });
        var proposal = new ModelProposal
        {
            SourceId = source.SourceId,
            Role = role,
            HeadingSpan = new SourceTextSpan(0, text.Length),
            ProposedParentId = parent,
        };
        return ModelProposalValidator.Validate(source, proposal, new HeadingPolicy(IncludeListItemTopic: true));
    }
}
