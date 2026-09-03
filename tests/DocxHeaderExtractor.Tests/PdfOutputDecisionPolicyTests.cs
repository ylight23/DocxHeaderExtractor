using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

using DocxHeaderExtractor.Eval;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.2 locks. The policy decides emission over materialized facts and may not become a second
/// validator: one decision per heading, nothing added, nothing removed, nothing rewritten, and
/// behaviour identical to the policy it replaces.
/// </summary>
public sealed class PdfOutputDecisionPolicyTests
{
    [Fact]
    public void EmitsExactlyOneDecisionPerHeadingAndLeavesTheStructureIntact()
    {
        var structure = Project(
            (Structure("b1"), "1 Introduction"),
            (Structure("b2") with { StructuralScope = "appendix_table" }, "4 3 Validation"));

        var decisions = PdfOutputDecisionPolicy.Decide(structure);

        Assert.Equal(structure.Headings.Count, decisions.Count);
        Assert.Equal(structure.Headings.Select(heading => heading.Id), decisions.Select(d => d.HeadingId));
    }

    /// <summary>An unknown parent is not a reason to hide a heading the validator accepted.</summary>
    [Fact]
    public void UnresolvedHierarchyStillEmits()
    {
        var structure = Project((Structure("b1"), "Topic without a marker"));

        var decision = Assert.Single(PdfOutputDecisionPolicy.Decide(structure));

        Assert.True(decision.Emit);
        Assert.True(decision.RequiresReview);
        Assert.Contains("hierarchy_unresolved", decision.Reasons);
    }

    [Theory]
    [InlineData("appendix_table")]
    [InlineData("quoted_replacement")]
    [InlineData("embedded_amendment")]
    public void ExcludedScopesAreReportedWithAReason(string scope)
    {
        var structure = Project((Structure("b1") with { StructuralScope = scope }, "4 3 Validation"));

        var decision = Assert.Single(PdfOutputDecisionPolicy.Decide(structure));

        Assert.False(decision.Emit);
        Assert.Contains($"excluded_scope:{scope}", decision.Reasons);
    }

    [Fact]
    public void ExcludedRolesAreReportedWithAReason()
    {
        var structure = Project((Structure("b1") with { DomainRole = PdfDomainRole.TableTitle }, "Table 1"));

        var decision = Assert.Single(PdfOutputDecisionPolicy.Decide(structure));

        Assert.False(decision.Emit);
        Assert.Contains("excluded_role:TableTitle", decision.Reasons);
    }

    /// <summary>
    /// Behavioural parity with the policy this replaces. The two run on the same validated input and
    /// must agree on which headings a document outline shows; a difference here would be a silent
    /// product change smuggled in by a refactor.
    /// </summary>
    [Fact]
    public void AgreesWithTheLegacyPolicyOnTheSameValidatedInput()
    {
        (PdfValidatedStructure Structure, string Text)[] cases =
        [
            (Structure("b1"), "1 Introduction"),
            (Structure("b2") with { StructuralScope = "appendix_table" }, "4 3 Validation"),
            (Structure("b3") with { StructuralScope = "quoted_replacement" }, "quoted clause"),
            (Structure("b4") with { StructuralScope = "embedded_amendment" }, "amended clause"),
            (Structure("b5") with { DomainRole = PdfDomainRole.TableTitle }, "Table 2"),
            (Structure("b6") with { DomainRole = PdfDomainRole.RunningArtifact }, "Standards Track"),
            (Structure("b7") with { DomainRole = PdfDomainRole.OutlineReference }, "Table of Contents"),
            (Structure("b8") with { StructuralScope = "appendix" }, "4 3 1 Sending a Validation Request"),
            (Structure("b9") with { DomainRole = PdfDomainRole.LegalClause }, "Dieu 1 Pham vi"),
        ];

        var structures = cases.Select(item => item.Structure).ToArray();
        var legacy = PdfLegacyValidatedOutputPolicy.ProjectDocumentOutline(
            cases.Select((item, index) => new HeadingRecord
            {
                Index = index,
                Level = 1,
                Text = item.Text,
                SourceId = item.Structure.SourceId,
                HeadingSpan = new TextOffsetSpan(0, item.Text.Length),
            }).ToArray(),
            structures);

        var decisions = PdfOutputDecisionPolicy.Decide(Project(cases));
        var structure = Project(cases);
        var emittedAnchors = decisions.Where(d => d.Emit)
            .Join(structure.Headings, d => d.HeadingId, h => h.Id, (_, h) => h.SourceAnchor!.ParagraphIndex);
        Assert.Equal(
            legacy.Select(heading => heading.Index).OrderBy(index => index),
            emittedAnchors.OrderBy(index => index));
        Assert.All(legacy, heading => Assert.Equal(HeadingDecisionStatus.RequiresReview, heading.DecisionStatus));
    }

    /// <summary>
    /// Anything the product shows must be locatable in the canonical document, because a writeback
    /// acts on that occurrence. A fact without one stays in the structure for review and is not
    /// emitted.
    /// </summary>
    [Fact]
    public void UngroundedFactIsNeverEmitted()
    {
        var structure = PdfFinalStructureProjection.Project("sha", [Structure("b1")],
            [Fact("b1", 0, "4 3 Validation")], []);

        var decision = Assert.Single(PdfOutputDecisionPolicy.Decide(structure));

        Assert.False(decision.Emit);
        Assert.Contains("grounding_unresolved", decision.Reasons);
        Assert.Single(structure.Headings);
    }

    [Fact]
    public void DecisionsAreDeterministic()
    {
        var structure = Project((Structure("b1"), "1 Introduction"), (Structure("b2"), "2 Overview"));

        Assert.Equal(
            PdfOutputDecisionPolicy.Decide(structure).Select(d => (d.HeadingId, d.Emit, string.Join(",", d.Reasons))),
            PdfOutputDecisionPolicy.Decide(structure).Select(d => (d.HeadingId, d.Emit, string.Join(",", d.Reasons))));
    }

    private static PdfFinalStructure Project(params (PdfValidatedStructure Structure, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => item.Structure).ToArray(),
            cases.Select((item, index) => Fact(item.Structure.SourceId, index, item.Text)).ToArray(),
            cases.Select((item, index) => new PdfCanonicalGrounding(item.Structure.SourceId, index,
                $"@body[1]/p[{index}]", new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfValidatedStructure Structure(string id) =>
        new(id, 1, null, "unresolved", "requires_review") { StructuralScope = "document_body" };

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
