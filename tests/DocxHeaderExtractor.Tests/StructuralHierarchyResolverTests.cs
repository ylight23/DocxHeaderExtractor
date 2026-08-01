using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

public class StructuralHierarchyResolverTests
{
    [Fact]
    public void Consecutive_siblings_correct_a_drifting_model_level()
    {
        var document = Doc((0, "PHẦN I"), (2, "1. Khái niệm"), (4, "1.1. Định nghĩa"),
            (6, "1.2. Phân loại"), (8, "2. Vai trò"));
        var headings = Headings((0, 1), (2, 2), (4, 3), (6, 3), (8, 4));

        var fixes = StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(1, fixes);
        Assert.Equal(2, headings.Single(h => h.Index == 8).Level);
    }

    [Fact]
    public void Dotted_number_is_child_of_its_numbered_parent()
    {
        var document = Doc((0, "3. Cha"), (2, "3.1. Con"));
        var headings = Headings((0, 2), (2, 1));

        StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(3, headings.Single(h => h.Index == 2).Level);
    }

    [Fact]
    public void Reset_number_does_not_borrow_sibling_level_from_previous_section()
    {
        var document = Doc((0, "1. Mục cũ"), (2, "PHẦN MỚI"), (4, "1. Mục mới"));
        var headings = Headings((0, 2), (2, 1), (4, 2));

        var fixes = StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(0, fixes);
        Assert.Equal(2, headings.Single(h => h.Index == 4).Level);
    }

    private static SlimDocument Doc(params (int Index, string Text)[] items) => new SlimDocument
    {
        FileName = "x.docx", SourcePath = "x.docx",
        Paragraphs = items.Select(x => new SlimParagraph { Index = x.Index, Text = x.Text }).ToList(),
    }.Build();

    private static List<HeadingRecord> Headings(params (int Index, int Level)[] items) =>
        items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" }).ToList();
}
