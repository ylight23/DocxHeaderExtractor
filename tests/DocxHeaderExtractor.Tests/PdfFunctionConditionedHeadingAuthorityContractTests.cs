using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfFunctionConditionedHeadingAuthorityContractTests
{
    private static readonly string[] Tail = ["A1", "A2"];
    private static readonly IReadOnlyDictionary<string, string> IdByAlias = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["A1"] = "O1",
        ["A2"] = "O2",
    };
    private static readonly IReadOnlyDictionary<string, SemanticSourceAtom> Atoms = new Dictionary<string, SemanticSourceAtom>(StringComparer.Ordinal)
    {
        ["A1"] = new("A1", "S1", 1, 1, 1, 0, "Heading"),
        ["A2"] = new("A2", "S2", 2, 1, 2, 0, "Body"),
    };

    [Fact]
    public void G2A_rejects_extra_root_properties()
    {
        const string raw = """{"decisions":[{"primary":"O1","anchor":"HAS_STRUCTURAL_EXTENT"}],"extra":true}""";

        Assert.Throws<InvalidOperationException>(() =>
            PdfFunctionConditionedHeadingAuthorityAdapter.ParseG2A(raw, ["O1"]));
    }

    [Fact]
    public void G2A_shared_composer_emits_unselectable_owned_neighbors_with_default_escaping()
    {
        var owned = Atoms.Values.OrderBy(value => value.Ordinal).ToArray();
        var user = PdfFunctionConditionedHeadingAuthorityAdapter.ComposeG2AUserMessage(
            owned,
            IdByAlias,
            [("O1", Atoms["A1"])]);

        using var json = JsonDocument.Parse(user);
        var row = json.RootElement.GetProperty("occurrences")[0];
        Assert.Equal("O1", row.GetProperty("primary").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("previous").ValueKind);
        var next = row.GetProperty("next");
        Assert.Equal("O2", next.GetProperty("occurrence").GetString());
        Assert.False(next.GetProperty("selectable").GetBoolean());
    }

    [Theory]
    [InlineData("NO_VISIBLE_SUCCESSOR", "O2")]
    [InlineData("INVALID", "O2")]
    [InlineData("BODY_CONTENT", null)]
    public void Boundary_rejects_invalid_first_outside_role_contract(string role, string? firstOutside)
    {
        var outside = firstOutside is null ? "null" : $"\"{firstOutside}\"";
        var raw = $$"""{"decisions":[{"anchor":"O1","headingMembers":["O1"],"endOccurrence":"O1","firstOutsideOccurrence":{{outside}},"firstOutsideRole":"{{role}}"}]}""";

        Assert.Throws<InvalidOperationException>(() =>
            PdfFunctionConditionedHeadingAuthorityAdapter.BindBoundary(raw, "O1", Tail, IdByAlias, Atoms));
    }

    [Fact]
    public void Boundary_accepts_terminal_only_with_no_visible_successor_role()
    {
        const string raw = """{"decisions":[{"anchor":"O1","headingMembers":["O1","O2"],"endOccurrence":"O2","firstOutsideOccurrence":null,"firstOutsideRole":"NO_VISIBLE_SUCCESSOR"}]}""";

        var decision = PdfFunctionConditionedHeadingAuthorityAdapter.BindBoundary(raw, "O1", Tail, IdByAlias, Atoms);

        Assert.Equal(["S1", "S2"], decision.Parts!.Select(part => part.SourceId).ToArray());
    }
}
