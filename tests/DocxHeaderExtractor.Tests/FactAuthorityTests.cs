using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class FactAuthorityTests
{
    [Fact]
    public void Exact_source_fact_materializes_value_from_source_slice()
    {
        var proposal = Proposal("test.period", Field("start", "p1", 9, 19), Field("end", "p1", 30, 40));
        var result = Evaluate(proposal, Schema("test.period", Required("start"), Required("end")));

        var fact = Assert.Single(result.ValidatedFacts);
        Assert.Equal(("start", "01/01/2026"), (fact.Fields[0].Name, fact.Fields[0].Value));
        Assert.Equal(("end", "31/12/2026"), (fact.Fields[1].Name, fact.Fields[1].Value));
        Assert.StartsWith("fact:", fact.Id);
        Assert.NotEqual(proposal.ProposalId, fact.Id);
        Assert.NotEqual("p1", fact.Id);
    }

    [Fact]
    public void Nonzero_span_and_multi_source_fields_remain_grounded()
    {
        var proposal = Proposal("test.multi", Field("left", "p1", 9, 19), Field("right", "p2", 0, 4));
        var result = Evaluate(
            proposal,
            Schema("test.multi", Required("left"), Required("right")),
            [new DocumentSourceUnit("p2", 1, "date", Anchor("p2", 1), new StructuralSpan(0, 4))]);

        var fact = Assert.Single(result.ValidatedFacts);
        Assert.Equal("01/01/2026", fact.Fields[0].Value);
        Assert.Equal("date", fact.Fields[1].Value);
        Assert.Equal(new StructuralSpan(9, 19), fact.Fields[0].Source.Span);
    }

    [Theory]
    [InlineData("unknown-schema", "schema-not-supported")]
    [InlineData("unknown-field", "field-not-supported")]
    [InlineData("missing-required", "required-field-missing")]
    [InlineData("duplicate-single", "duplicate-fact-field")]
    [InlineData("unknown-source", "fact-source-not-grounded")]
    [InlineData("out-of-range", "fact-span-invalid")]
    public void Invalid_schema_or_source_proposals_are_rejected(string kind, string expectedReason)
    {
        var schema = kind == "unknown-schema" ? Schema("test.other", Required("value")) : Schema("test.value", Required("value"));
        var proposal = kind switch
        {
            "unknown-schema" => Proposal("missing", Field("value", "p1", 0, 1)),
            "unknown-field" => Proposal("test.value", Field("other", "p1", 0, 1)),
            "missing-required" => Proposal("test.value"),
            "duplicate-single" => Proposal("test.value", Field("value", "p1", 0, 1), Field("value", "p1", 1, 2)),
            "unknown-source" => Proposal("test.value", Field("value", "missing", 0, 1)),
            "out-of-range" => Proposal("test.value", Field("value", "p1", 0, 999)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = Evaluate(proposal, schema);
        Assert.Empty(result.ValidatedFacts);
        Assert.Equal(expectedReason, Assert.Single(result.Rejections).Reason);
    }

    [Fact]
    public void Source_outside_context_is_rejected()
    {
        var proposal = Proposal("test.value", Field("value", "p2", 0, 4));
        var extraction = BuildExtraction([
            new DocumentSourceUnit("p2", 1, "date", Anchor("p2", 1), new StructuralSpan(0, 4)),
        ]) with
        {
            Chunks = [BuildExtraction([
                new DocumentSourceUnit("p2", 1, "date", Anchor("p2", 1), new StructuralSpan(0, 4)),
            ]).Chunks[0] with { SourceIds = ["p1"] }],
        };
        var result = new FactAuthorityRuntime(
            new InMemoryFactSchemaRegistry([Schema("test.value", Required("value"))]),
            new AcceptAllSemanticAuthority())
            .Evaluate(extraction, [proposal]);

        Assert.Equal("fact-source-outside-context", Assert.Single(result.Rejections).Reason);
    }

    [Fact]
    public void Semantic_rejection_wins_even_with_confidence_one()
    {
        var proposal = Proposal("test.expiration", Field("date", "p1", 9, 19), confidence: 1.0);
        var result = Evaluate(proposal, Schema("test.expiration", Required("date")));

        Assert.Empty(result.ValidatedFacts);
        Assert.Equal("fact-semantic-rejected", Assert.Single(result.Rejections).Reason);
    }

    [Fact]
    public void Missing_semantic_policy_is_rejected_without_default_accept()
    {
        var proposal = Proposal("test.value", Field("value", "p1", 0, 4));
        var validator = new FactProposalValidator();
        var result = validator.Validate(proposal, BuildExtraction(), new InMemoryFactSchemaRegistry([Schema("test.value", Required("value"))]), null);

        Assert.Null(result.Fact);
        var rejection = result.Rejection;
        Assert.NotNull(rejection);
        Assert.Equal("fact-semantic-authority-missing", rejection!.Reason);
    }

    [Fact]
    public void Missing_context_and_structural_context_are_rejected()
    {
        var schemas = new InMemoryFactSchemaRegistry([Schema("test.value", Required("value"))]);
        var validator = new FactProposalValidator();
        var missingContext = validator.Validate(
            Proposal("test.value", Field("value", "p1", 0, 4), contextChunkId: "missing"),
            BuildExtraction(), schemas, new AcceptAllSemanticAuthority());
        var missingContextRejection = missingContext.Rejection;
        Assert.NotNull(missingContextRejection);
        Assert.Equal("context-not-grounded", missingContextRejection!.Reason);

        var extraction = BuildExtraction() with
        {
            Chunks = [BuildExtraction().Chunks[0] with { StructuralElementIds = ["missing-element"] }],
        };
        var missingStructure = validator.Validate(
            Proposal("test.value", Field("value", "p1", 0, 4)),
            extraction,
            schemas,
            new AcceptAllSemanticAuthority());
        var missingStructureRejection = missingStructure.Rejection;
        Assert.NotNull(missingStructureRejection);
        Assert.Equal("fact-structural-context-not-grounded", missingStructureRejection!.Reason);
    }

    [Fact]
    public void Runtime_preserves_accepted_facts_and_rejections()
    {
        var runtime = new FactAuthorityRuntime(
            new InMemoryFactSchemaRegistry([Schema("test.value", Required("value"))]),
            new AcceptAllSemanticAuthority());
        var result = runtime.Evaluate(BuildExtraction(),
        [
            Proposal("test.value", Field("value", "p1", 0, 4)),
            Proposal("missing", Field("value", "p1", 0, 4)),
        ]);

        Assert.Single(result.ValidatedFacts);
        Assert.Equal("schema-not-supported", Assert.Single(result.Rejections).Reason);
    }

    private static FactAuthorityResult Evaluate(
        FactProposal proposal,
        FactSchemaDefinition schema,
        IReadOnlyList<DocumentSourceUnit>? extraSources = null)
    {
        var extraction = BuildExtraction(extraSources);
        var runtime = new FactAuthorityRuntime(
            new InMemoryFactSchemaRegistry([schema]),
            proposal.SchemaKey == "test.expiration"
                ? new RejectExpirationSemanticAuthority()
                : new AcceptAllSemanticAuthority());
        return runtime.Evaluate(extraction, [proposal]);
    }

    private static DocumentExtractionResult BuildExtraction(IReadOnlyList<DocumentSourceUnit>? extraSources = null)
    {
        var source = new DocumentSourceUnit(
            "p1",
            0,
            "Ngày ký: 01/01/2026; hết hạn: 31/12/2026",
            Anchor("p1", 0),
            new StructuralSpan(0, "Ngày ký: 01/01/2026; hết hạn: 31/12/2026".Length));
        var sources = new[] { source }.Concat(extraSources ?? []).ToArray();
        var catalog = new DocumentSourceCatalog(sources);
        var element = new ValidatedStructuralElement
        {
            Id = "heading-1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference("p1", 0, new StructuralSpan(0, source.Text.Length))],
            Text = source.Text,
            Level = 1,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "accepted", 1, "parser-facts"),
        };
        var structure = ValidatedStructure.FromElements([element], []);
        var sourceIds = sources.Select(item => item.SourceId).ToArray();
        var chunkText = string.Join('\n', sources.Select(item => item.Text));
        var section = new StructuralSection("section-1", "heading-1", null, ["heading-1"], sourceIds, ["heading-1"]);
        var chunk = new DocumentChunk("chunk-1", section.Id, sourceIds, ["heading-1"], chunkText, 3);
        return new DocumentExtractionResult(
            new DocumentIdentity("doc-1", "test.docx", "docx", "test.docx"),
            catalog,
            structure,
            [section],
            [chunk],
            new DocumentExtractionProvenance("test", "parser", 0));
    }

    private static FactProposal Proposal(
        string schemaKey,
        params ProposedFactField[] fields) => Proposal(schemaKey, fields, "chunk-1", null);

    private static FactProposal Proposal(
        string schemaKey,
        ProposedFactField field,
        double? confidence) => Proposal(schemaKey, new[] { field }, "chunk-1", confidence);

    private static FactProposal Proposal(
        string schemaKey,
        ProposedFactField field,
        string contextChunkId) => Proposal(schemaKey, new[] { field }, contextChunkId, null);

    private static FactProposal Proposal(
        string schemaKey,
        IReadOnlyList<ProposedFactField> fields,
        string contextChunkId,
        double? confidence) =>
        new("proposal-1", contextChunkId, schemaKey, fields, confidence);

    private static ProposedFactField Field(string name, string sourceId, int start, int end) =>
        new(name, sourceId, new StructuralSpan(start, end));

    private static FactFieldSchema Required(string name) => new(name, true, false);

    private static FactSchemaDefinition Schema(string key, params FactFieldSchema[] fields) => new(key, fields);

    private static SourceAnchor Anchor(string paragraphId, int index) =>
        new() { SourceType = "docx", ParagraphId = paragraphId, ParagraphIndex = index };

    private sealed class AcceptAllSemanticAuthority : IFactSemanticAuthority
    {
        public FactSemanticDecision Validate(FactSemanticContext context) =>
            new(true, "deterministic-test-policy", null);
    }

    private sealed class RejectExpirationSemanticAuthority : IFactSemanticAuthority
    {
        public FactSemanticDecision Validate(FactSemanticContext context) =>
            new(false, "deterministic-test-policy", "fact-semantic-rejected");
    }
}
