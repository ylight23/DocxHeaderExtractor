using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Selection;

namespace DocxHeaderExtractor.Tests;

public sealed class GroupingAuthorityTests
{
    [Theory]
    [InlineData("Chương IV", "Điều 34. Hợp đồng điện tử")]
    [InlineData("Chapter IV", "Article 34. Electronic contracts")]
    [InlineData("Chương I", "NHỮNG QUY ĐỊNH CHUNG")]
    public void Binding_preserves_the_supplied_partition_without_keyword_overrides(string first, string second)
    {
        var atoms = new[] { new SemanticSourceAtom("A", "src-A", 0, 1, 0, 0, first),
            new SemanticSourceAtom("B", "src-B", 1, 1, 1, 0, second) };
        var selected = new HashSet<string>(["A", "B"], StringComparer.Ordinal);
        var layout = new Dictionary<string, string>();
        var merged = SelectionAuthority.FromGroups(atoms, selected, [["A", "B"]], layout);
        Assert.Equal(["A", "B"], Assert.Single(merged).Parts!.Select(p => p.Alias));
        var split = SelectionAuthority.FromGroups(atoms, selected, [["A"], ["B"]], layout);
        Assert.Equal([["A"], ["B"]], split.Select(d => d.Parts!.Select(p => p.Alias).ToArray()));
    }
}
