using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PLACE named different parents for consecutive headings of one rendered kind (style, bold, marker kind, list level).
/// The pair is held for review; the model is not asked again and C# never picks either parent. Synthetic rows and scripted replies, not report text or provider output.
/// </summary>
public sealed class ParentConsistencyTests
{
    const string Plan = "P001-L001", Social = "P001-L002", First = "P001-L003", Second = "P001-L004";

    static string Style(string label, int level = 0, bool bold = true, string name = "List Paragraph") =>
        $"{{\"name\":\"{name}\",\"bold\":{(bold ? "true" : "false")},\"numberLabel\":\"{label}\",\"numberLevel\":{level}}}";

    // A plan, a section under it, and two items of one bullet list under that section, as in the month-9 report shape.
    static (CanonicalSemanticBoundHeading[] Bound, HeadingPlacementEvidence Evidence) Fixture(
        IReadOnlyDictionary<string, string> styles, params string[] extraAliases)
    {
        var aliases = new[] { Plan, Social, First }.Concat(extraAliases).Append(Second).ToArray();
        var lines = aliases.Select((alias, i) => new SemanticSourceAtom(alias, "id:" + alias, i, 1, i, 0, "Dòng " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var states = lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"));
        return (bound, new HeadingPlacementEvidence("{}", styles) { Source = new(lines, states, new string('a', 64)) });
    }

    static async Task<ParentResolver.PlacementResult> Place(CanonicalSemanticBoundHeading[] bound, HeadingPlacementEvidence evidence,
        Dictionary<string, string> parents, List<JsonNode>? packets = null)
    {
        var script = new PlacementScript(p => { packets?.Add(p.DeepClone()); return HierarchyTestProtocol.Reply(p.ToJsonString(), parents); });
        return await ParentResolver.PlaceAsync(bound, script.Placer, PlacementScript.Access, evidence, CancellationToken.None);
    }

    static string? Parent(ParentResolver.PlacementResult result, string alias) =>
        HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).Single(h => h.SourceId == "id:" + alias).ParentSourceId;

    [Fact]
    public async Task Two_items_of_one_bullet_list_placed_under_different_parents_are_both_held_for_review()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("●"), [Second] = Style("●") });
        // The model put the first item under the section and the second item under the plan; nobody asks it again.
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Plan });

        var conflict = Assert.Single(result.Observation.ParentConsistencyConflicts);
        Assert.Equal([First, Second], conflict.Aliases);
        Assert.Equal([Social, Plan], conflict.ProposedParents);
        // Neither parent is applied, and neither is swapped for the other.
        Assert.Null(Parent(result, First));
        Assert.Null(Parent(result, Second));
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h => h.SourceId);
        Assert.Equal(HierarchyResolver.Unresolved, hierarchy["id:" + First].Resolution);
        Assert.Equal(HierarchyResolver.Unresolved, hierarchy["id:" + Second].Resolution);
        // The audit keeps what the model proposed and why it was not used.
        var decisions = result.Observation.Decisions.ToDictionary(d => d.Alias);
        Assert.Equal(Social, decisions[First].ProposedParentAlias);
        Assert.Equal(Plan, decisions[Second].ProposedParentAlias);
        Assert.Equal(AdjacentHeadingParentConsistency.ResolutionBasis, decisions[Second].ResolutionBasis);
        // Headings outside the pair keep their parents.
        Assert.Equal("id:" + Plan, Parent(result, Social));
    }

    [Fact]
    public async Task Numbered_items_with_different_numbers_are_one_kind()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("1."), [Second] = Style("2.") });
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Plan });
        Assert.Single(result.Observation.ParentConsistencyConflicts);
    }

    [Fact]
    public async Task Items_of_one_kind_under_one_parent_are_placed_as_proposed()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("●"), [Second] = Style("●") });
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Social });
        Assert.Empty(result.Observation.ParentConsistencyConflicts);
        Assert.Equal("id:" + Social, Parent(result, First));
        Assert.Equal("id:" + Social, Parent(result, Second));
    }

    [Theory]
    [InlineData("●", 0, true, "List Paragraph", "-", 0, true, "List Paragraph")]   // a different marker
    [InlineData("●", 0, true, "List Paragraph", "●", 1, true, "List Paragraph")]   // a different list level
    [InlineData("●", 0, true, "List Paragraph", "●", 0, false, "List Paragraph")]  // one bold, one not
    [InlineData("●", 0, true, "List Paragraph", "●", 0, true, "Normal")]           // a different paragraph style
    public async Task Headings_of_different_kinds_may_have_different_parents(string label1, int level1, bool bold1, string style1,
        string label2, int level2, bool bold2, string style2)
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string>
            { [First] = Style(label1, level1, bold1, style1), [Second] = Style(label2, level2, bold2, style2) });
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Plan });
        Assert.Empty(result.Observation.ParentConsistencyConflicts);
        Assert.Equal("id:" + Plan, Parent(result, Second));
    }

    [Fact]
    public async Task A_selected_heading_between_them_ends_the_pair()
    {
        const string Between = "P001-L009";
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("●"), [Second] = Style("●") }, Between);
        var result = await Place(bound, evidence,
            new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Between] = Plan, [Second] = Between });
        Assert.Empty(result.Observation.ParentConsistencyConflicts);
    }

    [Fact]
    public async Task Without_measured_list_facts_nothing_is_flagged()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = "{\"name\":\"Normal\",\"bold\":true}", [Second] = "{\"name\":\"Normal\",\"bold\":true}" });
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Plan });
        Assert.Empty(result.Observation.ParentConsistencyConflicts);
    }

    // Month-9 shape: "I. CÁC HOẠT ĐỘNG…" then "A. Truyền thông:" — same style, bold and level, a real parent and child.
    [Fact]
    public async Task A_roman_section_and_the_lettered_item_under_it_are_different_kinds()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("I."), [Second] = Style("A.") });
        evidence = evidence with { NumberFormatByAlias = new Dictionary<string, string> { [First] = "upperRoman", [Second] = "upperLetter" } };
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = "ROOT", [Second] = First });
        Assert.Empty(result.Observation.ParentConsistencyConflicts);
    }

    [Fact]
    public async Task Lettered_items_of_one_list_are_one_kind()
    {
        var (bound, evidence) = Fixture(new Dictionary<string, string> { [First] = Style("A."), [Second] = Style("B.") });
        evidence = evidence with { NumberFormatByAlias = new Dictionary<string, string> { [First] = "upperLetter", [Second] = "upperLetter" } };
        var result = await Place(bound, evidence, new() { [Plan] = "ROOT", [Social] = Plan, [First] = Social, [Second] = Plan });
        Assert.Single(result.Observation.ParentConsistencyConflicts);
    }

    [Theory]
    [InlineData("I.", null, "A.", null, false)]                    // without the format a letter is not read as a counter
    [InlineData("1.", null, "2.", null, true)]
    [InlineData("I.", "upperRoman", "A.", "upperLetter", false)]
    [InlineData("I.", "upperRoman", "II.", "upperRoman", true)]
    [InlineData("", "bullet", "", "bullet", true)]
    [InlineData("", "bullet", "-", "bullet", false)]
    public void Marker_kinds(string label1, string? format1, string label2, string? format2, bool same) =>
        Assert.Equal(same, AdjacentHeadingParentConsistency.MarkerKind(System.Text.RegularExpressions.Regex.Unescape(label1), format1)
            == AdjacentHeadingParentConsistency.MarkerKind(System.Text.RegularExpressions.Regex.Unescape(label2), format2));

    [Theory]
    [InlineData("1.", "#.")]
    [InlineData("12.3", "#.#")]
    [InlineData("a)", "a)")]
    [InlineData("II.", "A.")]
    [InlineData("●", "●")]
    [InlineData(" - ", "-")]
    public void A_marker_kind_ignores_its_counter_value(string label, string kind) =>
        Assert.Equal(kind, AdjacentHeadingParentConsistency.LabelKind(label));
}
