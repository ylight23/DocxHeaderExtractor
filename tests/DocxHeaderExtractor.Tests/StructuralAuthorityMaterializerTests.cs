using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

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
