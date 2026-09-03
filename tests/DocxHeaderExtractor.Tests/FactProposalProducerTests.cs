using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class FactProposalProducerTests
{
    [Fact]
    public void Model_request_is_closed_and_contains_source_coordinates()
    {
        var request = Request(Schema("test.period", Required("start"), Required("end")));
        var modelRequest = FactProposalModelRequestBuilder.Build(request);
        var json = JsonSerializer.Serialize(modelRequest);

        Assert.Contains("\"contextChunkId\":\"chunk-1\"", json);
        Assert.Contains("\"key\":\"test.period\"", json);
        Assert.Contains("\"sourceId\":\"p1\"", json);
        Assert.Contains("\"text\":\"prefix start=alpha; end=omega suffix\"", json);
        Assert.DoesNotContain("\"value\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_model_parser_accepts_valid_multifield_multisource_nonzero_spans()
    {
        var request = Request(
            Schema("test.period", Required("start"), Required("end")),
            [
                new DocumentSourceUnit("p2", 1, "end=omega", Anchor("p2", 1), new StructuralSpan(0, 9)),
            ]);
        var modelRequest = FactProposalModelRequestBuilder.Build(request);
        var json = Response(modelRequest, FieldJson("start", "p1", 7, 12), FieldJson("end", "p2", 4, 9));

        var proposals = FactProposalModelResponseParser.Parse(json, modelRequest);

        var proposal = Assert.Single(proposals);
        Assert.Equal(new StructuralSpan(7, 12), proposal.Fields[0].Span);
        Assert.Equal("p2", proposal.Fields[1].SourceId);
        Assert.DoesNotContain("Value", JsonSerializer.Serialize(proposal), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"proposals\":[{\"proposalId\":\"m1\",\"contextChunkId\":\"chunk-1\",\"schemaKey\":\"test.value\",\"fields\":[{\"fieldName\":\"value\",\"sourceId\":\"p1\",\"span\":{\"start\":0,\"end\":4}}],\"value\":\"fake\"}]}")]
    [InlineData("{\"proposals\":[{\"proposalId\":\"m1\",\"contextChunkId\":\"chunk-1\",\"schemaKey\":\"test.value\",\"fields\":[{\"fieldName\":\"value\",\"sourceId\":\"p1\",\"span\":{\"start\":0,\"end\":4}}],\"unexpected\":true}]}")]
    [InlineData("{\"proposals\":[{\"proposalId\":\"m1\",\"contextChunkId\":\"chunk-1\",\"schemaKey\":\"test.value\",\"fields\":[{\"fieldName\":\"value\",\"sourceId\":\"p1\"}]}]}")]
    [InlineData("{not-json")]
    public void Strict_model_parser_rejects_malformed_or_authority_looking_wire(string json)
    {
        var request = FactProposalModelRequestBuilder.Build(Request(Schema("test.value", Required("value"))));

        Assert.Throws<FactProposalModelResponseException>(() =>
            FactProposalModelResponseParser.Parse(json, request));
    }

    [Fact]
    public async Task Model_producer_returns_untrusted_proposal_with_provenance()
    {
        var request = Request(Schema("test.value", Required("value")));
        var producer = new FactProposalModelProducer("fake-model", new FixedModel(
            _ => Response(FactProposalModelRequestBuilder.Build(request), FieldJson("value", "p1", 7, 12))));

        var result = await producer.ProduceAsync(request);

        var produced = Assert.Single(result.Proposals);
        Assert.Empty(result.Failures);
        Assert.Equal("fake-model", produced.Provenance.ProducerId);
        Assert.Equal("model", produced.Provenance.ProducerKind);
        Assert.Equal(request.RequestId, produced.Provenance.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(produced.Provenance.ResponseHash));
    }

    [Fact]
    public async Task Rule_producer_proposal_reaches_authority_and_materializes_source_value()
    {
        var request = Request(Schema("test.value", Required("value")));
        var producer = new RuleFactProposalProducer(new FixedRule("generic-test-rule", _ =>
            [new FactProposal("rule-1", "chunk-1", "test.value", [Field("value", "p1", 7, 12)])]));

        var result = await producer.ProduceAsync(request);
        var authority = Authority().Evaluate(Extraction(), result.Proposals.Select(item => item.Proposal));

        Assert.Empty(result.Failures);
        Assert.Equal("start", Assert.Single(authority.ValidatedFacts).Fields[0].Value);
    }

    [Fact]
    public async Task Unknown_source_and_invalid_span_are_preserved_as_proposals_then_rejected_by_authority()
    {
        var request = Request(Schema("test.value", Required("value")));
        var producer = new RuleFactProposalProducer(new FixedRule("bad-evidence", _ =>
            [
                new FactProposal("unknown", "chunk-1", "test.value", [Field("value", "missing", 0, 1)]),
                new FactProposal("bad-span", "chunk-1", "test.value", [Field("value", "p1", 0, 999)]),
            ]));

        var result = await producer.ProduceAsync(request);
        var authority = Authority().Evaluate(Extraction(), result.Proposals.Select(item => item.Proposal));

        Assert.Equal(2, result.Proposals.Count);
        Assert.Equal(2, authority.Rejections.Count);
        Assert.Contains(authority.Rejections, item => item.Reason == "fact-source-not-grounded");
        Assert.Contains(authority.Rejections, item => item.Reason == "fact-span-invalid");
    }

    [Fact]
    public async Task Semantic_rejection_wins_even_when_producer_confidence_is_one()
    {
        var request = Request(Schema("test.value", Required("value")));
        var producer = new RuleFactProposalProducer(new FixedRule("wrong-semantic", _ =>
            [new FactProposal("wrong", "chunk-1", "test.value", [Field("value", "p1", 7, 12)], 1.0)]));

        var result = await producer.ProduceAsync(request);
        var authority = new FactAuthorityRuntime(
            new InMemoryFactSchemaRegistry([request.Schema]),
            new RejectingSemanticAuthority())
            .Evaluate(Extraction(), result.Proposals.Select(item => item.Proposal));

        Assert.Empty(authority.ValidatedFacts);
        Assert.Equal("semantic-test-rejected", Assert.Single(authority.Rejections).Reason);
    }

    [Fact]
    public async Task Composite_deduplicates_exact_proposals_but_keeps_distinct_source_occurrences()
    {
        var schema = Schema("test.value", new FactFieldSchema("value", true, true));
        var request = Request(schema, [
            new DocumentSourceUnit("p2", 1, "start", Anchor("p2", 1), new StructuralSpan(0, 5)),
        ]);
        var first = new RuleFactProposalProducer(new FixedRule("rule-a", _ =>
            [new FactProposal("a", "chunk-1", "test.value", [Field("value", "p1", 7, 12)])]));
        var duplicate = new RuleFactProposalProducer(new FixedRule("rule-b", _ =>
            [new FactProposal("b", "chunk-1", "test.value", [Field("value", "p1", 7, 12)])]));
        var distinct = new RuleFactProposalProducer(new FixedRule("rule-c", _ =>
            [new FactProposal("c", "chunk-1", "test.value", [Field("value", "p2", 0, 5)])]));

        var result = await new CompositeFactProposalProducer([first, duplicate, distinct]).ProduceAsync(request);

        Assert.Equal(2, result.Proposals.Count);
        Assert.Contains(result.Proposals, item => item.Proposal.Fields[0].SourceId == "p1");
        Assert.Contains(result.Proposals, item => item.Proposal.Fields[0].SourceId == "p2");
    }

    [Fact]
    public async Task Composite_isolates_failure_and_preserves_other_proposals()
    {
        var request = Request(Schema("test.value", Required("value")));
        var good = new RuleFactProposalProducer(new FixedRule("good", _ =>
            [new FactProposal("good", "chunk-1", "test.value", [Field("value", "p1", 7, 12)])]));
        var bad = new ThrowingProducer();
        var secondGood = new RuleFactProposalProducer(new FixedRule("good-2", _ =>
            [new FactProposal("good-2", "chunk-1", "test.value", [Field("value", "p1", 0, 6)])]));

        var result = await new CompositeFactProposalProducer([good, bad, secondGood]).ProduceAsync(request);

        Assert.Equal(2, result.Proposals.Count);
        Assert.Single(result.Failures);
        Assert.Equal("ThrowingProducer", result.Failures[0].ProducerId);
    }

    [Fact]
    public async Task Replay_model_uses_stable_request_identity_without_provider_calls()
    {
        var schema = Schema("test.value", Required("value"));
        var request = Request(schema);
        var modelRequest = FactProposalModelRequestBuilder.Build(request);
        var response = Response(modelRequest, FieldJson("value", "p1", 7, 12));
        var replay = new ReplayFactProposalModel(new Dictionary<string, string>
        {
            [request.RequestId] = response,
        });

        var first = await replay.CompleteAsync(modelRequest);
        var second = await replay.CompleteAsync(FactProposalModelRequestBuilder.Build(Request(schema)));

        Assert.Equal(response, first);
        Assert.Equal(response, second);
        Assert.Equal(0, replay.ProviderCalls);
    }

    [Fact]
    public async Task Production_runtime_joins_contexts_proposals_authority_and_failures()
    {
        var schema = Schema("test.value", Required("value"));
        var request = Request(schema);
        var good = new RuleFactProposalProducer(new FixedRule("good", _ =>
            [new FactProposal("good", "chunk-1", "test.value", [Field("value", "p1", 7, 12)])]));
        var malformed = new FactProposalModelProducer("malformed-model", new FixedModel(_ => "{bad-json"));
        var runtime = new FactProposalProductionRuntime(
            new CompositeFactProposalProducer([good, malformed]),
            Authority());

        var result = await runtime.EvaluateAsync(Extraction(), [schema]);

        Assert.Single(result.ProducedProposals);
        Assert.Single(result.AuthorityResult.ValidatedFacts);
        Assert.Empty(result.AuthorityResult.Rejections);
        Assert.Single(result.ProducerFailures);
    }

    private static FactProposalProductionRequest Request(
        FactSchemaDefinition schema,
        IReadOnlyList<DocumentSourceUnit>? extraSources = null)
    {
        var extraction = Extraction(extraSources);
        var sourceUnits = extraction.SourceCatalog.Units
            .Select(unit => new FactSourceExcerpt(unit.SourceId, unit.SourceOrdinal, unit.Text))
            .ToArray();
        var context = new FactExtractionContext(
            "doc-1", "chunk-1", "section-1", extraction.Chunks[0].Text,
            ["heading-1"], [], [], sourceUnits.Select(unit => unit.SourceId).ToArray(),
            ["heading-1"], sourceUnits);
        return new FactProposalProductionRequest(context, schema);
    }

    private static DocumentExtractionResult Extraction(IReadOnlyList<DocumentSourceUnit>? extraSources = null)
    {
        const string text = "prefix start=alpha; end=omega suffix";
        var source = new DocumentSourceUnit("p1", 0, text, Anchor("p1", 0), new StructuralSpan(0, text.Length));
        var sources = new[] { source }.Concat(extraSources ?? []).ToArray();
        var catalog = new DocumentSourceCatalog(sources);
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
        var section = new StructuralSection("section-1", "heading-1", null, ["heading-1"], sources.Select(item => item.SourceId).ToArray(), ["heading-1"]);
        var chunk = new DocumentChunk("chunk-1", section.Id, section.SourceIds, ["heading-1"], string.Join('\n', sources.Select(item => item.Text)), 4);
        return new DocumentExtractionResult(
            new DocumentIdentity("doc-1", "test.docx", "docx", "test.docx"),
            catalog, structure, [section], [chunk], new DocumentExtractionProvenance("test", "parser", 0));
    }

    private static FactAuthorityRuntime Authority() => new(
        new InMemoryFactSchemaRegistry([Schema("test.value", Required("value"), new FactFieldSchema("start", false, false), new FactFieldSchema("end", false, false))]),
        new AcceptingSemanticAuthority());

    private static string Response(FactProposalModelRequest request, params string[] fields) =>
        $"{{\"proposals\":[{{\"proposalId\":\"model-1\",\"contextChunkId\":\"{request.ContextChunkId}\",\"schemaKey\":\"{request.Schema.Key}\",\"fields\":[{string.Join(',', fields)}],\"confidence\":0.91}}]}}";

    private static string FieldJson(string name, string sourceId, int start, int end) =>
        $"{{\"fieldName\":\"{name}\",\"sourceId\":\"{sourceId}\",\"span\":{{\"start\":{start},\"end\":{end}}}}}";

    private static ProposedFactField Field(string name, string sourceId, int start, int end) =>
        new(name, sourceId, new StructuralSpan(start, end));

    private static FactFieldSchema Required(string name) => new(name, true, false);

    private static FactSchemaDefinition Schema(string key, params FactFieldSchema[] fields) => new(key, fields);

    private static SourceAnchor Anchor(string id, int index) =>
        new() { SourceType = "docx", ParagraphId = id, ParagraphIndex = index };

    private sealed class FixedModel(Func<FactProposalModelRequest, string> response) : IFactProposalModel
    {
        public Task<string> CompleteAsync(FactProposalModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(response(request));
    }

    private sealed class FixedRule(string ruleId, Func<FactProposalProductionRequest, IReadOnlyList<FactProposal>> proposals) : IFactProposalRule
    {
        public string RuleId => ruleId;

        public IReadOnlyList<FactProposal> Propose(FactProposalProductionRequest request) => proposals(request);
    }

    private sealed class ThrowingProducer : IFactProposalProducer
    {
        public Task<FactProposalProductionResult> ProduceAsync(FactProposalProductionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("deterministic-test-failure");
    }

    private sealed class AcceptingSemanticAuthority : IFactSemanticAuthority
    {
        public FactSemanticDecision Validate(FactSemanticContext context) =>
            new(true, "deterministic-test-authority", null);
    }

    private sealed class RejectingSemanticAuthority : IFactSemanticAuthority
    {
        public FactSemanticDecision Validate(FactSemanticContext context) =>
            new(false, "deterministic-test-authority", "semantic-test-rejected");
    }
}
