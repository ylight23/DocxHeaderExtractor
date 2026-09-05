using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class RouteOccurrenceTraceTests
{
    [Fact]
    public void Trace_joins_candidate_and_model_request_only_through_explicit_representation()
    {
        var catalog = Catalog("source-1", "Observed heading");
        var structure = Structure("source-1", "element-1", emitted: true);
        var audit = Audit(
            candidateId: "candidate-7",
            representation: new RouteSourceRepresentation(
                "source-1", "pdf-block-7", "PDF_PARSER_BLOCK", "candidate-7", "EXPLICIT_BLOCK_SOURCE_REFERENCE"),
            request: new RouteModelRequestAudit(
                "semantic-role:req-7", "semantic-role", ["candidate-7"], true, true, "COMPLETED"));

        var trace = Assert.Single(RouteOccurrenceTraceBuilder.Build(
            "document-1", "sha256", catalog, structure, new HashSet<string>(["element-1"]), audit,
            documentGroupId: "group-1", routeOwner: "PDF_AUTHORITY_ROUTE"));

        Assert.Equal("group-1", trace.DocumentGroupId);
        Assert.Equal("pdf-block-7", trace.RepresentationId);
        Assert.Equal("candidate-7", trace.CandidateId);
        Assert.True(trace.CandidateConstructed);
        Assert.True(trace.CandidateSelected);
        Assert.Equal(["semantic-role:req-7"], trace.ModelRequestIds);
        Assert.Equal("EXACT_CANDIDATE_ID", trace.ModelRequestMembership);
        Assert.True(trace.ModelProposalPresent);
        Assert.True(trace.FinalIncluded);
        Assert.Null(trace.FinalParent);
    }

    [Fact]
    public void Trace_retains_unknown_when_source_candidate_mapping_is_not_explicit()
    {
        var catalog = Catalog("source-1", "Same-looking heading");
        var structure = Structure("source-1", "element-1", emitted: false);
        var audit = Audit(candidateId: "unrelated-candidate");

        var trace = Assert.Single(RouteOccurrenceTraceBuilder.Build(
            "document-1", "sha256", catalog, structure, new HashSet<string>(), audit,
            routeOwner: "DOCX_AUTHORITY_ROUTE"));

        Assert.Null(trace.RepresentationId);
        Assert.Null(trace.CandidateId);
        Assert.Null(trace.CandidateConstructed);
        Assert.Equal("UNKNOWN", trace.ModelRequestMembership);
        Assert.Equal("DOCX_AUTHORITY_ROUTE", trace.RouteOwner);
        Assert.False(trace.FinalIncluded);
    }

    [Fact]
    public void Trace_does_not_turn_a_document_response_into_per_occurrence_model_exposure()
    {
        var catalog = Catalog("source-1", "Deterministic heading");
        var structure = Structure("source-1", "element-1", emitted: true);
        var audit = Audit(candidateId: "candidate-1") with
        {
            RawAnalystResponses = ["a document-level response with no request membership"],
        };

        var trace = Assert.Single(RouteOccurrenceTraceBuilder.Build(
            "document-1", "sha256", catalog, structure, new HashSet<string>(["element-1"]), audit,
            routeOwner: "DOCX_AUTHORITY_ROUTE"));

        Assert.Empty(trace.ModelRequestIds);
        Assert.Equal("NOT_REQUESTED", trace.ModelRequestMembership);
        Assert.Null(trace.ModelProposalPresent);
    }

    [Fact]
    public void Observability_fields_do_not_change_compatibility_audit_json_shape()
    {
        var json = JsonSerializer.Serialize(Audit(
            candidateId: "candidate-1",
            representation: new RouteSourceRepresentation(
                "source-1", "representation-1", "DOCX_SOURCE_PARAGRAPH", "candidate-1", "PARSER_OWNED_LINEAGE"),
            request: new RouteModelRequestAudit(
                "request-1", "semantic-role", ["candidate-1"], true, false, "STARTED")));

        Assert.DoesNotContain("sourceRepresentations", json, StringComparison.Ordinal);
        Assert.DoesNotContain("modelRequests", json, StringComparison.Ordinal);
        Assert.DoesNotContain("occurrenceTraces", json, StringComparison.Ordinal);
    }

    private static DocumentSourceCatalog Catalog(string sourceId, string text) =>
        new([
            new DocumentSourceUnit(
                sourceId,
                0,
                text,
                new SourceAnchor { SourceType = "pdf", ParagraphId = "stable-1", ParagraphIndex = 0 },
                new StructuralSpan(0, text.Length)),
        ]);

    private static ValidatedStructure Structure(string sourceId, string elementId, bool emitted)
    {
        var text = "Observed heading";
        var element = new ValidatedStructuralElement
        {
            Id = elementId,
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference(sourceId, 0, new StructuralSpan(0, text.Length))],
            Text = text,
            Level = 1,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "accepted", 1, "test"),
        };
        return new ValidatedStructure([element]);
    }

    private static RouteExecutionAudit Audit(
        string candidateId,
        RouteSourceRepresentation? representation = null,
        RouteModelRequestAudit? request = null)
    {
        var candidate = new RouteBlockAudit(candidateId, 1, "Observed heading");
        return new RouteExecutionAudit(
            "test",
            1,
            1,
            1,
            1,
            [candidate],
            [candidate],
            [],
            [new RouteBlockDecisionAudit(candidateId, "HeadingTopic", 1, "test")],
            [candidateId],
            [],
            [candidateId])
        {
            SourceRepresentations = representation is null ? [] : [representation],
            ModelRequests = request is null ? [] : [request],
            CandidateStageTraces = [new PdfCandidateStageTrace(
                candidateId, "document_body", "HeadingTopic", "resolved", "accepted", null)],
        };
    }
}
