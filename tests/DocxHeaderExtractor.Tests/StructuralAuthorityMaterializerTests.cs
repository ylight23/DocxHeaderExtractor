using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralAuthorityMaterializerTests
{
    [Fact]
    public void Pdf_materialization_projects_the_same_full_heading_records_as_the_legacy_oracle()
    {
        var final = Project(
            (new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review"), "1 Introduction"),
            (new PdfValidatedStructure("b2", 2, "b1", "resolved", "requires_review"), "1.1 Scope"));
        var decisions = PdfOutputDecisionPolicy.Decide(final);
        var product = PdfProductOutputSerializer.Serialize(final, decisions);
        var materialized = StructuralAuthorityMaterializer.Materialize(final, decisions);

        var oldHeadings = PdfProductOutlineAdapter.ToHeadingRecords(product);
        var newHeadings = HeadingOutlineProjection.Project(
            materialized.Structure, materialized.EmittedElementIds);

        Assert.Equal(JsonSerializer.Serialize(oldHeadings), JsonSerializer.Serialize(newHeadings));
        Assert.Equal(0, materialized.UnjoinedSourceCount);
        Assert.Equal(0, materialized.UnjoinedParentCount);

        var first = materialized.Structure.Elements[0];
        Assert.NotEqual(first.Id, first.Sources[0].SourceId);
        Assert.Equal("@body[1]/p[0]", first.Sources[0].SourceId);
        Assert.Equal(first.Id.Replace("structural:pdf:", "", StringComparison.Ordinal),
            first.ProjectionMetadata!.CompatibilitySourceId);
    }

    [Fact]
    public void Title_subtitle_and_heading_are_all_compatible_outline_elements()
    {
        var source = new SourceReference("source", 0, new StructuralSpan(0, 5));
        var elements = new[]
        {
            Element("title", StructuralElementType.Title, ProposedRole.DocumentTitle, "Title", null),
            Element("subtitle", StructuralElementType.Subtitle, ProposedRole.CoverTitle, "Sub", null),
            Element("heading", StructuralElementType.Heading, ProposedRole.HeadingTopic, "Heading", 1),
        };

        var projected = HeadingOutlineProjection.Project(ValidatedStructure.FromElements(elements));

        Assert.Equal(["Title", "Sub", "Heading"], projected.Select(item => item.Text));
        Assert.Null(projected[0].Level);
        Assert.Null(projected[1].Level);
        Assert.Equal(1, projected[2].Level);

        ValidatedStructuralElement Element(
            string id, StructuralElementType type, ProposedRole role, string text, int? level) => new()
        {
            Id = id,
            Type = type,
            Role = role,
            Sources = [source],
            Text = text,
            Level = level,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("structure", "AutoAcceptedEvidence", 1, "test"),
        };
    }

    [Fact]
    public void Pdf_parser_materialization_uses_the_exact_parser_unit_for_doc0252()
    {
        const string pdfText = "1 G loba l office u pdate";
        const string docxText = "1.Global office update";
        var final = SingleHeading(
            "body[1]/p[12]#0-22",
            new DocxSourceAnchor(11, "body[1]/p[12]", new DocxTextSpan(0, docxText.Length)),
            new PdfEvidenceAnchor(1, "s-line-26", new PdfTextSpan(0, pdfText.Length), pdfText, ["line-26"]),
            docxText);
        var catalog = PdfCatalog("s-line-26", pdfText, 11);

        var materialized = StructuralAuthorityMaterializer.Materialize(
            final,
            [],
            catalog,
            StructuralMaterializationSourceAuthority.PdfParserSource);
        var element = Assert.Single(materialized.Structure.Elements);
        var source = Assert.Single(element.Sources);

        Assert.Equal("s-line-26", source.SourceId);
        Assert.Equal(11, source.SourceOrdinal);
        Assert.Equal(new StructuralSpan(0, pdfText.Length), source.Span);
        Assert.Equal(pdfText, element.Text);
        Assert.Equal(pdfText, catalog.Units.Single().Text);

        var projected = Assert.Single(HeadingOutlineProjection.Project(materialized.Structure));
        Assert.Equal("body[1]/p[12]#0-22", projected.SourceId);
        Assert.Equal(new TextOffsetSpan(0, docxText.Length), projected.HeadingSpan);
        Assert.Equal(docxText, projected.Text);
        Assert.Equal("s-line-26", final.Headings.Single().PdfEvidence!.BlockId);
    }

    [Fact]
    public void Docx_canonical_materialization_keeps_pdf_evidence_as_provenance()
    {
        const string pdfText = "1 G loba l office u pdate";
        const string docxText = "1.Global office update";
        var final = SingleHeading(
            "body[1]/p[12]#0-22",
            new DocxSourceAnchor(11, "body[1]/p[12]", new DocxTextSpan(0, docxText.Length)),
            new PdfEvidenceAnchor(1, "s-line-26", new PdfTextSpan(0, pdfText.Length), pdfText, ["line-26"]),
            docxText);
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit(
                "body[1]/p[12]", 11, docxText,
                new SourceAnchor
                {
                    SourceType = "docx",
                    ParagraphId = "body[1]/p[12]",
                    ParagraphIndex = 11,
                },
                new StructuralSpan(0, docxText.Length)),
        ]);

        var materialized = StructuralAuthorityMaterializer.Materialize(
            final,
            [],
            catalog,
            StructuralMaterializationSourceAuthority.CanonicalDocumentSource);
        var element = Assert.Single(materialized.Structure.Elements);
        var source = Assert.Single(element.Sources);

        Assert.Equal("body[1]/p[12]", source.SourceId);
        Assert.Equal(new StructuralSpan(0, docxText.Length), source.Span);
        Assert.Equal(docxText, element.Text);
        Assert.Equal("s-line-26", final.Headings.Single().PdfEvidence!.BlockId);
        Assert.Equal(new PdfTextSpan(0, pdfText.Length), final.Headings.Single().PdfEvidence!.Span);
    }

    [Fact]
    public void True_pdf_source_materializes_without_a_docx_anchor()
    {
        const string raw = "prefix Figure 3 caption suffix";
        const string title = "Figure 3 caption";
        var final = SingleHeading(
            "pdf-occurrence-1",
            null,
            new PdfEvidenceAnchor(2, "pdf-b", new PdfTextSpan(7, 23), title, ["line-7"]),
            title);
        var catalog = PdfCatalog("pdf-b", raw, 4);

        var materialized = StructuralAuthorityMaterializer.Materialize(
            final,
            [],
            catalog,
            StructuralMaterializationSourceAuthority.PdfParserSource);
        var element = Assert.Single(materialized.Structure.Elements);
        var source = Assert.Single(element.Sources);

        Assert.Equal("pdf-b", source.SourceId);
        Assert.Equal(4, source.SourceOrdinal);
        Assert.Equal(new StructuralSpan(7, 23), source.Span);
        Assert.Equal(title, element.Text);
    }

    [Fact]
    public void Missing_pdf_source_unit_reports_the_validator_boundary_reason()
    {
        const string text = "1 G loba l office u pdate";
        var final = SingleHeading(
            "body[1]/p[12]#0-22",
            new DocxSourceAnchor(11, "body[1]/p[12]", new DocxTextSpan(0, 22)),
            new PdfEvidenceAnchor(1, "s-line-26", new PdfTextSpan(0, text.Length), text, []),
            "1.Global office update");

        var exception = Assert.Throws<StructuralMaterializationException>(() =>
            StructuralAuthorityMaterializer.Materialize(
                final,
                [],
                new DocumentSourceCatalog([]),
                StructuralMaterializationSourceAuthority.PdfParserSource));

        Assert.Equal("pdf-source-not-in-catalog", exception.Reason);
        Assert.Equal("s-line-26", exception.SourceId);
        Assert.False(exception.SourceUnitFound);
    }

    [Fact]
    public void Doc0252_catalog_builder_includes_supplemental_parser_candidate()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop",
            "072_ICP_TAG_Minutes_Mar_2025.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var standardBlocks = PdfSemanticBlockGrouper.Build(snapshot.Annotations);
        var catalog = DocumentSourceCatalogBuilder.FromPdfParserBlocks(
            standardBlocks.Concat(snapshot.CandidateBlocks).ToArray(), snapshot.Lines);
        var candidate = Assert.Single(snapshot.CandidateBlocks.Where(block => block.Id == "s-line-26"));
        var unit = Assert.Single(catalog.Units.Where(source => source.SourceId == candidate.Id));

        Assert.Equal(candidate.Text, unit.Text);
        Assert.Equal(candidate.Lines.Select(PdfCandidateProvenance.LineId), unit.SourceAnchor.RenderLineIds);
        var sourceLineIndexes = candidate.Lines
            .Select(line => snapshot.Lines.Select(PdfCandidateProvenance.LineId).ToList()
                .IndexOf(PdfCandidateProvenance.LineId(line)))
            .Where(index => index >= 0)
            .ToArray();
        Assert.Equal(sourceLineIndexes.Min(), unit.SourceOrdinal);
    }

    private static PdfFinalStructure SingleHeading(
        string id,
        DocxSourceAnchor? sourceAnchor,
        PdfEvidenceAnchor? evidence,
        string text) => new(
        "sha",
        "facts",
        "final",
        new PdfFinalStructureCounters(1, 1, 0, sourceAnchor is null ? 1 : 0, 1, 0, 1),
        [new PdfFinalHeading(
            id,
            sourceAnchor,
            evidence,
            text,
            "Heading",
            "document_body",
            "accepted",
            sourceAnchor is null ? "grounding_unresolved" : "grounded",
            1,
            null,
            "resolved",
            null,
            null,
            "validated",
            text)]);

    private static DocumentSourceCatalog PdfCatalog(string sourceId, string text, int ordinal) =>
        new([
            new DocumentSourceUnit(
                sourceId,
                ordinal,
                text,
                new SourceAnchor
                {
                    SourceType = "pdf",
                    ParagraphIndex = ordinal,
                    RenderBlockId = sourceId,
                },
                new StructuralSpan(0, text.Length)),
        ]);

    private static PdfFinalStructure Project(
        params (PdfValidatedStructure Structure, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => item.Structure).ToArray(),
            cases.Select((item, index) => Fact(item.Structure.SourceId, index, item.Text)).ToArray(),
            cases.Select((item, index) => new PdfCanonicalGrounding(
                item.Structure.SourceId, index, $"@body[1]/p[{index}]",
                new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfHierarchyFactAudit Fact(string id, int order, string text) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            null, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
