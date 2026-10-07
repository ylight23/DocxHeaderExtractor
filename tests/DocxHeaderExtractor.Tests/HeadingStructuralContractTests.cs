using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingStructuralContractTests
{
    [Fact]
    public void Production_taxonomy_and_numeric_identities_are_exact()
    {
        Assert.Equal(new[] { "Heading" }, Enum.GetNames<StructuralElementType>());
        Assert.Equal(new[] { "HeadingTopic" }, Enum.GetNames<ProposedRole>());
        Assert.Equal(new[] { "ParentChild" }, Enum.GetNames<StructuralRelationType>());
        Assert.Equal(2, (int)StructuralElementType.Heading);
        Assert.Equal(0, (int)ProposedRole.HeadingTopic);
        Assert.Equal(0, (int)StructuralRelationType.ParentChild);
    }

    [Fact]
    public void Heading_graph_wire_bytes_match_the_pre_cleanup_baseline()
    {
        // Captured provider-free from cb2246b before taxonomy retirement. Never rebaseline.
        var graph = ValidatedStructure.FromElements([
            Heading("root", "p0", 0, "Root", 1, null),
            Heading("child", "p1", 1, "Wrapped heading", 2, "root"),
        ]);
        Assert.Equal("931fc4b2312baf43bf7ccaef1efbd604dd2b8838e72f5ed0146ae2476fff39cd",
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(graph))));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(99)]
    public void Retired_numeric_types_are_rejected_by_proposal_and_graph(int value)
    {
        var proposal = Proposal() with { Type = (StructuralElementType)value };
        var validation = StructuralProposalValidator.Validate(Source(), proposal);
        Assert.False(validation.Accepted);
        Assert.False(validation.TypeValid);
        Assert.Equal("unsupported-structural-type", validation.RejectionReason);
        var element = Heading("root", "p0", 0, "Root", 1, null) with { Type = proposal.Type };
        Assert.Equal("unsupported-structural-type",
            Assert.Throws<InvalidOperationException>(() => new ValidatedStructure([element])).Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(99)]
    public void Retired_numeric_roles_are_rejected_by_proposal_and_graph(int value)
    {
        var proposal = Proposal() with { Role = (ProposedRole)value };
        var validation = StructuralProposalValidator.Validate(Source(), proposal);
        Assert.False(validation.Accepted);
        Assert.False(validation.TypeRoleValid);
        Assert.Equal("incompatible-structural-role", validation.RejectionReason);
        var element = Heading("root", "p0", 0, "Root", 1, null) with { Role = proposal.Role };
        Assert.Equal("incompatible-structural-role",
            Assert.Throws<InvalidOperationException>(() => ValidatedStructure.FromElements([element])).Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(99)]
    public void Retired_numeric_relations_cannot_enter_the_heading_graph(int value)
    {
        var elements = new[] { Heading("root", "p0", 0, "Root", 1, null),
            Heading("child", "p1", 1, "Child", 2, null) };
        Assert.Equal("relation-type-unsupported", Assert.Throws<InvalidOperationException>(() =>
            new ValidatedStructure(elements, [new StructuralRelation("root", "child", (StructuralRelationType)value)])).Message);
    }

    [Theory]
    [InlineData("type", "Caption")]
    [InlineData("type", "Title")]
    [InlineData("role", "Caption")]
    [InlineData("role", "LocalSubheading")]
    public void Retired_string_taxonomy_is_not_silently_remapped(string property, string value)
    {
        var json = JsonSerializer.Serialize(Proposal()).Replace(
            property == "type" ? "\"type\":\"Heading\"" : "\"role\":\"HeadingTopic\"",
            $"\"{property}\":\"{value}\"");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StructuralProposal>(json));
    }

    [Theory]
    [InlineData(-1, 4)] [InlineData(0, 0)] [InlineData(0, 5)]
    public void Heading_source_span_gate_still_refuses_invalid_selections(int start, int end)
    {
        var proposal = Proposal() with
        {
            ProposedSources = [new ProposedSourceReference("p0", new StructuralSpan(start, end))],
        };
        Assert.Equal("invalid-proposed-sources",
            StructuralProposalValidator.Validate(Source(), proposal).RejectionReason);
        Assert.Null(StructuralProposalValidator.Materialize(Source(), proposal, "root",
            new StructuralDecision("model", "RequiresReview", "bound")));
    }

    [Fact]
    public void Heading_source_identity_and_multiple_parent_gates_remain_fail_closed()
    {
        Assert.Equal("source-occurrence-not-grounded",
            StructuralProposalValidator.Validate(Source(), Proposal() with { SourceOccurrenceId = "other" }).RejectionReason);
        var ids = new HashSet<string> { "a", "b", "c" };
        Assert.Equal("multiple-parent-relations", Assert.Throws<InvalidOperationException>(() =>
            StructuralRelationProposalValidator.Materialize(ids, [
                new StructuralRelationProposal("a", "c", StructuralRelationType.ParentChild),
                new StructuralRelationProposal("b", "c", StructuralRelationType.ParentChild),
            ])).Message);
    }

    [Fact]
    public void Contract_files_have_explicit_owners_and_no_generic_monolith()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.Core/Models");
        Assert.False(File.Exists(Path.Combine(root, "StructuralContracts.cs")));
        foreach (var file in new[] { "SourceSelectionContracts.cs", "HeadingStructuralContracts.cs",
                     "HeadingHierarchyContracts.cs", "ValidatedStructure.cs" })
            Assert.True(File.Exists(Path.Combine(root, file)), file);
    }

    private static StructuralSourceOccurrence Source() => new()
    {
        SourceOccurrenceId = "o",
        ObservedSourceFacts = [new SourceFacts
        {
            SourceId = "p0", RawText = "Root", RawSpan = new SourceTextSpan(0, 4),
            Source = new SourceAnchor { SourceType = "test", ParagraphIndex = 0 },
        }],
    };

    private static StructuralProposal Proposal() => new()
    {
        SourceOccurrenceId = "o", Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic,
        ProposedSources = [new ProposedSourceReference("p0", new StructuralSpan(0, 4))],
    };

    private static ValidatedStructuralElement Heading(string id, string source, int ordinal, string text, int level, string? parent) => new()
    {
        Id = id, Type = StructuralElementType.Heading, Role = ProposedRole.HeadingTopic,
        Sources = [new SourceReference(source, ordinal, new StructuralSpan(0, text.Length))],
        Text = text, Level = level, ParentId = parent,
        Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
        Decision = new StructuralDecision("model", "RequiresReview", "bound"),
    };
}
