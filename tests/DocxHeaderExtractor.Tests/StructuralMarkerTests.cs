using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Selection;

namespace DocxHeaderExtractor.Tests;

/// <summary>A group of selected lines may not hide a second structural heading (009, run C: "Chương VIII" with "Điều 51" as one heading).</summary>
public sealed class StructuralMarkerTests
{
    static SemanticSourceAtom Atom(string alias, int ordinal, string text) =>
        new(alias, "src-" + alias, ordinal, 1, ordinal, 0, text);

    [Fact]
    public void A_group_with_a_chapter_and_an_article_becomes_two_headings()
    {
        var atoms = new[]
        {
            Atom("A1", 0, "Chương VIIIĐIỀU KHOẢN THI HÀNH"),
            Atom("A2", 1, "Điều 51. Sửa đổi, bổ sung một số điều"),
            Atom("A3", 2, "1. Sửa đổi, bổ sung mục 119 thuộc Phụ lục IV"),
        };
        var selected = new HashSet<string>(["A1", "A2", "A3"], StringComparer.Ordinal);

        var decisions = SelectionAuthority.FromGroups(atoms, selected, [["A1", "A2", "A3"]], new Dictionary<string, string>());

        Assert.Equal([["A1"], ["A2", "A3"]], decisions.Select(d => d.Parts!.Select(p => p.Alias).ToArray()));
    }

    [Fact]
    public void A_group_with_no_second_marker_is_kept_whole()
    {
        var atoms = new[]
        {
            Atom("B1", 0, "Chương I"),
            Atom("B2", 1, "NHỮNG QUY ĐỊNH CHUNG"),
        };
        var selected = new HashSet<string>(["B1", "B2"], StringComparer.Ordinal);

        var decisions = SelectionAuthority.FromGroups(atoms, selected, [["B1", "B2"]], new Dictionary<string, string>());

        Assert.Equal([["B1", "B2"]], decisions.Select(d => d.Parts!.Select(p => p.Alias).ToArray()));
    }
}
