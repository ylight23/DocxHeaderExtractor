using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralProposalValidatorOwnershipTests
{
    [Theory]
    [InlineData("identity", "source-occurrence-not-grounded")]
    [InlineData("missing-facts", "source-facts-missing")]
    [InlineData("span", "invalid-proposed-sources")]
    [InlineData("unknown-source", "invalid-proposed-sources")]
    [InlineData("empty-selection", "invalid-proposed-sources")]
    [InlineData("duplicate-observed", "invalid-proposed-sources")]
    [InlineData("type", "unsupported-structural-type")]
    [InlineData("role", "incompatible-structural-role")]
    [InlineData("level-low", "invalid-structural-level")]
    [InlineData("level-high", "invalid-structural-level")]
    [InlineData("parent", "structural-parent-not-grounded")]
    public void Validation_and_materialization_retain_rejection_gates(string mode, string expected)
    {
        var source = Source();
        var proposal = Proposal();
        switch (mode)
        {
            case "identity": proposal = proposal with { SourceOccurrenceId = "other" }; break;
            case "missing-facts": source = source with { ObservedSourceFacts = [] }; break;
            case "span": proposal = proposal with { ProposedSources = [new("p0", new(-1, 4))] }; break;
            case "unknown-source": proposal = proposal with { ProposedSources = [new("unknown", new(0, 4))] }; break;
            case "empty-selection": proposal = proposal with { ProposedSources = [] }; break;
            case "duplicate-observed":
                source = source with { ObservedSourceFacts = [source.ObservedSourceFacts[0], source.ObservedSourceFacts[0]] };
                proposal = proposal with { ProposedSources = null };
                break;
            case "type": proposal = proposal with { Type = (StructuralElementType)99 }; break;
            case "role": proposal = proposal with { Role = (ProposedRole)99 }; break;
            case "level-low": proposal = proposal with { ProposedLevel = 0 }; break;
            case "level-high": proposal = proposal with { ProposedLevel = 10 }; break;
            case "parent": proposal = proposal with { ProposedParentId = "missing" }; break;
        }
        var known = new HashSet<string> { "root" };
        var validation = StructuralProposalValidator.Validate(source, proposal, known);
        Assert.False(validation.Accepted);
        Assert.Equal(expected, validation.RejectionReason);
        Assert.Null(StructuralProposalValidator.Materialize(source, proposal, "heading", Decision(), known));
    }

    [Fact]
    public void Multipart_materialization_preserves_source_order_ordinals_text_and_caller_decision()
    {
        var source = Source() with
        {
            ObservedSourceFacts = [Facts("p0", "Alpha", 42), Facts("p1", "Bravo", null)],
        };
        var proposal = Proposal() with
        {
            ProposedSources = [new("p1", new(0, 5)), new("p0", new(0, 5))],
            ProposedLevel = 2, ProposedParentId = "root",
        };
        var decision = Decision();
        var element = StructuralProposalValidator.Materialize(source, proposal, "heading", decision,
            new HashSet<string> { "root" })!;
        Assert.Equal("Bravo Alpha", element.Text);
        Assert.Equal(new[] { "p1", "p0" }, element.Sources.Select(item => item.SourceId));
        Assert.Equal(new[] { 0, 42 }, element.Sources.Select(item => item.SourceOrdinal));
        Assert.Equal(2, element.Level);
        Assert.Same(decision, element.Decision);
        Assert.True(element.Validation.Accepted);
        Assert.Null(element.ParentId); // Only subsequent graph admission projects parentage.
        Assert.Equal("root", proposal.ProposedParentId);
    }

    [Fact]
    public void Implicit_observed_selection_and_optional_parent_premise_remain_unchanged()
    {
        var proposal = Proposal() with { ProposedSources = null, ProposedParentId = "not-checked-without-known-set" };
        var source = Source();
        var element = StructuralProposalValidator.Materialize(source, proposal, "heading", Decision())!;
        Assert.Equal("Alpha", element.Text);
        Assert.Equal(source.ObservedSources, element.Sources);
        Assert.True(element.Validation.ParentValid);
        Assert.Null(element.ParentId);
    }

    [Fact]
    public void Validator_is_Core_owned_and_old_processing_owner_cannot_reappear()
    {
        var core = typeof(ValidatedStructure).Assembly;
        Assert.Equal(core, typeof(StructuralProposalValidator).Assembly);
        Assert.Equal("DocxHeaderExtractor.Core.Semantics.Validation", typeof(StructuralProposalValidator).Namespace);
        Assert.False(File.Exists(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Materialization/StructuralProposalValidator.cs")));
        foreach (var method in typeof(StructuralProposalValidator).GetMethods()
                     .Where(method => method.DeclaringType == typeof(StructuralProposalValidator)))
        {
            Assert.Equal(core, method.ReturnType.Assembly);
            foreach (var parameter in method.GetParameters().Where(parameter => !parameter.ParameterType.IsGenericType))
                Assert.True(parameter.ParameterType.Assembly == core || parameter.ParameterType == typeof(string));
        }
    }

    private static StructuralProposal Proposal() => new()
    {
        SourceOccurrenceId = "o", Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic,
        ProposedSources = [new("p0", new(0, 5))],
    };

    private static StructuralSourceOccurrence Source() => new()
    {
        SourceOccurrenceId = "o", ObservedSourceFacts = [Facts("p0", "Alpha", 0)],
    };

    private static SourceFacts Facts(string id, string text, int? ordinal) => new()
    {
        SourceId = id, RawText = text, RawSpan = new(0, text.Length),
        Source = new SourceAnchor { SourceType = "test", ParagraphIndex = ordinal },
    };

    private static StructuralDecision Decision() => new("caller-origin", "caller-status", "caller-basis");
}
