using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Eval.R18;

namespace DocxHeaderExtractor.Tests;

public sealed class R18DeterministicDiagnosticsTests
{
    [Fact]
    public void Marker_path_component_conflict_is_a_telemetry_failure()
    {
        var source = Source("p1", 0, "1.2 Topic");
        var facts = new PdfHierarchyFactAudit(
            "p1", 0, 1, "document_body", "SemanticOnly", "arabic", 2, true, "1.2", null, null, 2,
            "marker_prefix_parent_candidate", ["marker:arabic"])
        {
            FactId = "fact-p1",
            SourceBlockText = source.Paragraph.Text,
            HeadingSpan = new TextOffsetSpan(0, source.Paragraph.Text.Length),
            HeadingText = source.Paragraph.Text,
            MarkerComponents = [1, 3],
        };
        var execution = Execution(source, Element("structural:p1", "p1", 2, null), [facts]);

        var report = R18DeterministicDiagnostics.Analyze(source.Document, execution, []);

        var marker = Assert.Single(report.Observations.Where(item =>
            item.Diagnostic == R18DiagnosticKind.MarkerSequence));
        Assert.Equal(R18DiagnosticStatus.Fail, marker.Status);
        Assert.Equal("marker-path-components-disagree", marker.Reason);
        Assert.Null(execution.Result.Structure.Elements.Single().ProjectionMetadata);
    }

    [Fact]
    public void Hierarchy_constraint_detects_missing_parent_without_mutating_structure()
    {
        var source = Source("p1", 0, "1.2 Topic");
        var facts = new PdfHierarchyFactAudit(
            "p1", 0, 1, "document_body", "SemanticOnly", "arabic", 2, true, "1.2", null, null, 2,
            "relationship_unresolved", ["marker:arabic"])
        {
            FactId = "fact-p1",
            SourceBlockText = source.Paragraph.Text,
            HeadingSpan = new TextOffsetSpan(0, source.Paragraph.Text.Length),
            HeadingText = source.Paragraph.Text,
            MarkerComponents = [1, 2],
        };
        var element = Element("structural:p1", "p1", 2, null);
        var execution = Execution(source, element, [facts]);

        var report = R18DeterministicDiagnostics.Analyze(source.Document, execution, []);

        var hierarchy = Assert.Single(report.Observations.Where(item =>
            item.Diagnostic == R18DiagnosticKind.HierarchyConstraint));
        Assert.Equal(R18DiagnosticStatus.Fail, hierarchy.Status);
        Assert.Equal("nested-level-missing-parent", hierarchy.Reason);
        Assert.Null(execution.Result.Structure.Elements.Single().ParentId);
    }

    [Fact]
    public void Missing_sibling_evidence_is_not_applicable_not_a_pass()
    {
        var source = Source("p1", 0, "Plain text");
        var execution = Execution(source, Element("structural:p1", "p1", null, null), []);

        var report = R18DeterministicDiagnostics.Analyze(source.Document, execution, []);

        var sibling = Assert.Single(report.Observations.Where(item =>
            item.Diagnostic == R18DiagnosticKind.SiblingConsistency));
        Assert.Equal(R18DiagnosticStatus.NotApplicable, sibling.Status);
        Assert.Equal("sibling-relation-not-observable", sibling.Reason);
    }

    [Fact]
    public void Diagnostic_quality_metrics_are_only_computed_for_comparable_references()
    {
        var source = Source("p1", 0, "1 Topic");
        var fact = new PdfHierarchyFactAudit(
            "p1", 0, 1, "document_body", "SemanticOnly", "arabic", 1, true, "1", null, null, 1,
            "relationship_unresolved", ["marker:arabic"])
        {
            FactId = "fact-p1",
            SourceBlockText = source.Paragraph.Text,
            HeadingSpan = new TextOffsetSpan(0, source.Paragraph.Text.Length),
            HeadingText = source.Paragraph.Text,
            MarkerComponents = [1],
        };
        var execution = Execution(source, Element("structural:p1", "p1", 1, null), [fact]);
        var decision = new R18DecisionObservation
        {
            SourceId = "p1",
            FinalPresent = true,
            FinalLevel = 1,
            FinalLevelStatus = R18ObservationStatus.Observable,
            Reference = new R18ReferenceOutcome
            {
                Authority = R18ReferenceAuthority.HumanKey,
                ExpectedLevel = 1,
            },
        };

        var report = R18DeterministicDiagnostics.Analyze(source.Document, execution, [decision]);

        var marker = Assert.Single(report.Metrics, item => item.Diagnostic == R18DiagnosticKind.MarkerSequence);
        Assert.Equal(1, marker.Applicable);
        Assert.Equal(0, marker.Alerts);
        Assert.Equal(0, marker.TrueErrorAlerts);
        Assert.Equal(0, marker.FalseAlerts);
        Assert.Equal(0, marker.RelevantErrors);
        Assert.Null(marker.Precision);
        Assert.Equal("MEASURED_AGAINST_REFERENCE_BACKED_OBSERVATIONS", report.QualityClaim);
    }

    private static (SourceDocument Document, SourceParagraph Paragraph) Source(string id, int ordinal, string text)
    {
        var paragraph = new SourceParagraph
        {
            SourceId = id,
            SourceOrdinal = ordinal,
            Text = text,
            Style = new SourceStyleFacts(),
            Numbering = new SourceNumberingFacts(),
            Layout = new SourceLayoutFacts(),
        };
        return (new SourceDocument
        {
            DocumentId = "doc-1",
            FileName = "doc.docx",
            SourcePath = "doc.docx",
            SourceKind = "docx",
            Paragraphs = [paragraph],
        }, paragraph);
    }

    private static ValidatedStructuralElement Element(string id, string sourceId, int? level, string? parentId) =>
        new()
        {
            Id = id,
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference(sourceId, 0, new StructuralSpan(0, 1))],
            Text = "Topic",
            Level = level,
            ParentId = parentId,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "validated", 1, "test"),
        };

    private static AuthorityPipelineExecutionResult Execution(
        (SourceDocument Document, SourceParagraph Paragraph) source,
        ValidatedStructuralElement element,
        IReadOnlyList<PdfHierarchyFactAudit> facts)
    {
        var structure = ValidatedStructure.FromElements([element]);
        var outline = new DocumentOutline
        {
            File = source.Document.FileName,
            ParagraphCount = 1,
            CandidateCount = 1,
            Headings = [],
            RouteAudit = new RouteExecutionAudit(
                "test", 1, 1, 0, 0, [], [], [], [], [], [], [])
            {
                HierarchyFacts = facts,
            },
        };
        var result = new DocumentExtractionResult(
            new DocumentIdentity(source.Document.DocumentId, source.Document.FileName, source.Document.SourceKind, source.Document.SourcePath),
            new DocumentSourceCatalog([new DocumentSourceUnit(
                source.Paragraph.SourceId, source.Paragraph.SourceOrdinal, source.Paragraph.Text,
                new SourceAnchor { SourceType = "docx", ParagraphId = source.Paragraph.SourceId, ParagraphIndex = source.Paragraph.SourceOrdinal },
                new StructuralSpan(0, source.Paragraph.Text.Length))]),
            structure,
            [],
            [],
            new DocumentExtractionProvenance("test", "test", 0));
        return new AuthorityPipelineExecutionResult(result, outline);
    }
}
