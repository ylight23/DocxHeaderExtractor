using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class FactSchemaPackRuntimeTests
{
    [Fact]
    public async Task Three_schemas_share_one_extraction_and_return_validated_facts_only()
    {
        var extraction = Extraction();
        var producer = new FixedProducer(request => request.Schema.Key switch
        {
            "test.amount" => Proposal(request, "value", "p1", 31, 34),
            "test.entity" => Proposal(request, "name", "p1", 0, 5),
            "test.period" => Proposal(request, "start", "p1", 6, 11),
            _ => throw new InvalidOperationException("unexpected-schema"),
        });
        var runtime = new DocumentFactExtractionRuntime(producer, Registry(
            Pack("test.amount", "value", new MarkerAuthority("amount")),
            Pack("test.entity", "name", new MarkerAuthority("entity")),
            Pack("test.period", "start", new MarkerAuthority("period"))));

        var result = await runtime.ExtractFactsAsync(
            extraction,
            new DocumentFactExtractionRequest(["test.period", "test.amount", "test.entity", "test.period"]));

        Assert.Equal(["test.amount", "test.entity", "test.period"], result.SchemaResults.Select(item => item.SchemaKey));
        Assert.Equal(3, result.Facts.Count);
        Assert.Equal(3, result.Audit.ProducedProposals.Count);
        Assert.All(result.Facts, fact => Assert.All(fact.Fields, field =>
            Assert.Equal(extraction.SourceCatalog.Units.Single(unit => unit.SourceId == field.Source.SourceId).Text[
                field.Source.Span.Start..field.Source.Span.End], field.Value)));
        Assert.Equal(3, producer.RequestCount);
    }

    [Fact]
    public void Schema_authorities_are_routed_and_cannot_validate_another_schema()
    {
        var authority = new SchemaRoutedFactSemanticAuthority(Registry(
            Pack("test.period", "start", new MarkerAuthority("period")),
            Pack("test.amount", "value", new MarkerAuthority("amount"))));
        var context = new FactSemanticContext(
            new FactProposal("p", "chunk-1", "test.period", []),
            Schema("test.amount", "value"),
            []);

        var decision = authority.Validate(context);

        Assert.True(decision.Accepted);
        Assert.Equal("authority-amount", decision.Basis);
        Assert.False(new MarkerAuthority("period").Validate(context).Accepted);
    }

    [Fact]
    public async Task Unknown_schema_is_rejected_before_producer_call()
    {
        var producer = new FixedProducer(_ => throw new InvalidOperationException("producer-called"));
        var runtime = new DocumentFactExtractionRuntime(producer, Registry(
            Pack("test.period", "start", new MarkerAuthority("period"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExtractFactsAsync(
            Extraction(), new DocumentFactExtractionRequest(["test.missing"])));

        Assert.Equal("fact-schema-pack-missing:test.missing", exception.Message);
        Assert.Equal(0, producer.RequestCount);
    }

    [Fact]
    public async Task One_rejected_schema_does_not_remove_other_schema_facts()
    {
        var producer = new FixedProducer(request => request.Schema.Key == "test.period"
            ? Proposal(request, "start", "p1", 6, 11)
            : Proposal(request, "wrong", "p1", 0, 5));
        var runtime = new DocumentFactExtractionRuntime(producer, Registry(
            Pack("test.period", "start", new MarkerAuthority("period")),
            Pack("test.amount", "value", new MarkerAuthority("amount"))));

        var result = await runtime.ExtractFactsAsync(
            Extraction(), new DocumentFactExtractionRequest(["test.amount", "test.period"]));

        Assert.Single(result.Facts);
        Assert.Equal("test.period", result.Facts[0].SchemaKey);
        Assert.Contains(result.SchemaResults.Single(item => item.SchemaKey == "test.amount").Rejections,
            rejection => rejection.Reason == "field-not-supported");
    }

    [Fact]
    public async Task Producer_failure_is_scoped_and_other_schema_is_preserved()
    {
        var producer = new FixedProducer(request => request.Schema.Key == "test.amount"
            ? throw new InvalidOperationException("amount-down")
            : Proposal(request, "start", "p1", 6, 11));
        var runtime = new DocumentFactExtractionRuntime(producer, Registry(
            Pack("test.amount", "value", new MarkerAuthority("amount")),
            Pack("test.period", "start", new MarkerAuthority("period"))));

        var result = await runtime.ExtractFactsAsync(
            Extraction(), new DocumentFactExtractionRequest(["test.amount", "test.period"]));

        Assert.Single(result.Facts);
        Assert.Equal("test.period", result.Facts[0].SchemaKey);
        Assert.Single(result.SchemaResults.Single(item => item.SchemaKey == "test.amount").ProducerFailures);
    }

    [Fact]
    public void Empty_or_duplicate_request_keys_are_handled_deterministically()
    {
        Assert.Throws<ArgumentException>(() => new DocumentFactExtractionRequest([]));
        Assert.Throws<ArgumentException>(() => new DocumentFactExtractionRequest(["test.period", " "]));
        var request = new DocumentFactExtractionRequest([" test.period ", "test.amount", "test.period"]);
        Assert.Equal(["test.amount", "test.period"], request.SchemaKeys);
    }

    private static InMemoryFactSchemaPackRegistry Registry(params IFactSchemaPack[] packs) =>
        new(packs);

    private static FactSchemaPack Pack(string key, string field, IFactSemanticAuthority authority) =>
        new(key, "v1", Schema(key, field), authority);

    private static FactSchemaDefinition Schema(string key, string field) =>
        new(key, [new FactFieldSchema(field, true, false)]);

    private static FactProposal Proposal(
        FactProposalProductionRequest request, string field, string sourceId, int start, int end) =>
        new("proposal-" + request.Schema.Key, request.Context.ChunkId!, request.Schema.Key,
            [new ProposedFactField(field, sourceId, new StructuralSpan(start, end))]);

    private static DocumentExtractionResult Extraction()
    {
        const string text = "start=alpha; end=omega; amount=100";
        var source = new DocumentSourceUnit("p1", 0, text, new SourceAnchor
        {
            SourceType = "docx", ParagraphId = "p1", ParagraphIndex = 0,
        }, new StructuralSpan(0, text.Length));
        var element = new ValidatedStructuralElement
        {
            Id = "heading-1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference("p1", 0, new StructuralSpan(0, text.Length))],
            Text = text,
            Level = 1,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "accepted", 1, "test"),
        };
        var structure = ValidatedStructure.FromElements([element], []);
        var section = new StructuralSection("section-1", "heading-1", null,
            ["heading-1"], ["p1"], ["heading-1"]);
        var chunk = new DocumentChunk("chunk-1", section.Id, ["p1"], ["heading-1"], text, 6);
        return new DocumentExtractionResult(
            new DocumentIdentity("doc-1", "test.docx", "docx", "test.docx"),
            new DocumentSourceCatalog([source]), structure, [section], [chunk],
            new DocumentExtractionProvenance("test", "parser", 0));
    }

    private sealed class FixedProducer(
        Func<FactProposalProductionRequest, FactProposal> proposal) : IFactProposalProducer
    {
        public int RequestCount { get; private set; }

        public Task<FactProposalProductionResult> ProduceAsync(
            FactProposalProductionRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(new FactProposalProductionResult([new ProducedFactProposal(
                proposal(request), new FactProposalProvenance("test", "rule", request.RequestId))], []));
        }
    }

    private sealed class MarkerAuthority(string expected) : IFactSemanticAuthority
    {
        public FactSemanticDecision Validate(FactSemanticContext context) =>
            new(context.Schema.Key.EndsWith(expected, StringComparison.Ordinal),
                "authority-" + expected,
                context.Schema.Key.EndsWith(expected, StringComparison.Ordinal) ? null : "wrong-authority");
    }
}
