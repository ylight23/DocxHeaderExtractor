using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Context observes, never widens the stageDecision universe. No provider execution.</summary>
public sealed class P7PageContextDecisionUniverseTests
{
    private static PdfSourceBuildResult Fixture() => PdfSourceAdapter.BuildWithDetails([
        Line(1, 700, "Prefix"), Line(1, 650, "Anchor"), Line(1, 600, "Successor"),
        Line(2, 700, "Adjacent page")], new string('c', 64));
    private static PdfLine Line(int page, double y, string text) => new(page, y, 12, text, 1, "", 0,
        10, 150, "Times", "", Bottom: y - 10, Top: y);
    private static InterpretationRequest H2(PdfSourceBuildResult source) => PdfInterpretationProtocol.Compose(
        InterpretationStage.H2C, source.Snapshot, source.Details,
        source.Snapshot.Atoms.OrderBy(x => x.Ordinal).Take(3).ToArray(), anchor: "O2");
    private static JsonElement Decision(string[] members, string end, string? outside, string role = "BODY_CONTENT") =>
        JsonSerializer.SerializeToElement(new { decisions = new[] { new { anchor = "O2", headingMembers = members,
            endOccurrence = end, firstOutsideOccurrence = outside, firstOutsideRole = role } } });
    private static void AssertContextPresent(InterpretationRequest request)
    {
        var context = PdfPageEvidenceContextBuilder.Build(request.EvidenceStore, [request.VisibleAliasByOccurrence["O2"]],
            new(PageContextScope.SubjectAndAdjacentPages, AdjacentPageRadius: 1));
        var observed = context.Pages.SelectMany(x => x.Observations).ToArray();
        Assert.Equal(4, observed.Length); Assert.All(observed, x => Assert.False(x.Selectable));
        Assert.Equal(["O2", "O3"], request.VisibleAliasByOccurrence.Keys);
        Assert.Equal(["O2"], request.DecisionSubjects);
    }

    [Fact]
    public void Valid_issued_extent_is_accepted_even_when_prefix_and_next_page_context_are_present()
    {
        var source = Fixture(); var request = H2(source); AssertContextPresent(request);
        PdfInterpretationProtocol.ValidateStage(Decision(["O2"], "O2", "O3"), request, source.Snapshot);
        PdfInterpretationProtocol.ValidateStage(Decision(["O2", "O3"], "O3", null, "NO_VISIBLE_SUCCESSOR"), request, source.Snapshot);
    }

    [Theory]
    [InlineData("prefix-member")]
    [InlineData("adjacent-member")]
    [InlineData("prefix-end")]
    [InlineData("adjacent-end")]
    [InlineData("prefix-outside")]
    [InlineData("adjacent-outside")]
    public void Observed_context_handles_cannot_be_members_endpoint_or_successor(string attack)
    {
        var source = Fixture(); var request = H2(source); AssertContextPresent(request);
        var payload = attack switch {
            "prefix-member" => Decision(["O1", "O2"], "O2", "O3"),
            "adjacent-member" => Decision(["O2", "O3", "O4"], "O4", null, "NO_VISIBLE_SUCCESSOR"),
            "prefix-end" => Decision(["O2"], "O1", "O3"),
            "adjacent-end" => Decision(["O2", "O3"], "O4", null, "NO_VISIBLE_SUCCESSOR"),
            "prefix-outside" => Decision(["O2"], "O2", "O1"),
            _ => Decision(["O2", "O3"], "O3", "O4") };
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.ValidateStage(payload, request, source.Snapshot));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("G2A")]
    public void Context_does_not_add_function_or_anchor_decision_subjects(string stage)
    {
        var source = Fixture();
        var request = PdfInterpretationProtocol.Compose(Enum.Parse<InterpretationStage>(stage), source.Snapshot, source.Details,
            source.Snapshot.Atoms.OrderBy(x => x.Ordinal).Take(3).ToArray(), primaryIds: stage == "G2A" ? ["O2"] : null);
        var context = PdfPageEvidenceContextBuilder.Build(request.EvidenceStore, [request.Owned[1].Atom.Alias],
            new(PageContextScope.SubjectAndAdjacentPages, AdjacentPageRadius: 1));
        Assert.Equal(4, context.Pages.Sum(x => x.Observations.Count));
        var valid = stage == "F1" ? JsonSerializer.SerializeToElement(new { decisions = new[] {
            new { occurrence = "O1", function = "OTHER" }, new { occurrence = "O2", function = "OTHER" }, new { occurrence = "O3", function = "OTHER" } } }) :
            JsonSerializer.SerializeToElement(new { decisions = new[] { new { primary = "O2", anchor = "NO_STRUCTURAL_EXTENT" } } });
        PdfInterpretationProtocol.ValidateStage(valid, request, source.Snapshot);
        var invalid = stage == "F1" ? JsonSerializer.SerializeToElement(new { decisions = new[] {
            new { occurrence = "O1", function = "OTHER" }, new { occurrence = "O2", function = "OTHER" }, new { occurrence = "O4", function = "OTHER" } } }) :
            JsonSerializer.SerializeToElement(new { decisions = new[] { new { primary = "O1", anchor = "HAS_STRUCTURAL_EXTENT" } } });
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.ValidateStage(invalid, request, source.Snapshot));
    }
}
