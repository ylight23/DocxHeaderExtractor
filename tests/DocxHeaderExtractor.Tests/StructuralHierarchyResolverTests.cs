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

    /// <summary>
    /// Nhánh chữ ký đã có chốt "cấu trúc đã khai cấp thì không suy lại" (SignatureTierTests), nhưng
    /// nhánh đường dẫn số Ả Rập — ngay bên cạnh trong cùng file — thì từng không có. Ở đây danh sách
    /// đa cấp khai "2." là cấp 3, còn quan hệ cha–con "2." dưới "1." lại suy ra cấp 2. Tuyên bố của
    /// tài liệu phải thắng suy luận; nếu không thì hai bộ suy luận cấu trúc nói hai điều khác nhau
    /// về cùng một đoạn, tuỳ vào cái nào chạy sau.
    /// </summary>
    [Fact]
    public void Nhanh_duong_dan_so_khong_ghi_de_cap_ma_danh_sach_da_cap_da_khai()
    {
        // "2." là anh em của "1.", nên suy luận anh-em muốn kéo nó về cấp 2. Nhưng danh sách đa cấp
        // đã khai nó là cấp 3. Đoạn thứ ba chứng minh nó VẪN nằm trong tập đường dẫn: "2.1." tìm cha
        // "2." và lấy cấp 3 + 1 = 4. Cấm ghi cấp của chính nó, không phải gỡ nó khỏi cây.
        var document = Doc((0, "1. Mục một"), (2, "2. Mục hai"), (4, "2.1. Mục con"));
        document.Paragraphs.Single(p => p.Index == 2).NumberingStyleLevel = 3;
        var headings = Headings((0, 2), (2, 3), (4, 1));

        StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(3, headings.Single(h => h.Index == 2).Level);
        Assert.Equal(4, headings.Single(h => h.Index == 4).Level);
    }

    /// <summary>
    /// Word đánh số qua w:numPr thì con số không nằm trong Text, chỉ có ở NumberLabel — đúng dạng
    /// đánh số bài bản nhất mà commit 13ac456 nói đã "gom về một điểm" qua NumberingAudit.ParseParagraph.
    /// PathOf lại bỏ sót: nó ghép <c>label ?? text</c> (chỉ lấy MỘT trong hai), nên khi có label lại
    /// truyền NHÃN TRƠ ("3.1.") cho ParseArabicPath — không có tên mục theo sau nên HasTitleRemainder
    /// loại, path ra null, và quan hệ cha–con không đọc được cho đúng nhóm tài liệu này.
    /// </summary>
    [Fact]
    public void Dotted_number_via_word_numbering_label_is_still_child_of_its_parent()
    {
        var document = Doc((0, "Cha"), (2, "Con"));
        document = NativePolicyStateFactory.Create([
            (0, "Cha", (int?)null, (int?)null, "3."),
            (2, "Con", (int?)null, (int?)null, "3.1."),
        ]);
        var headings = Headings((0, 2), (2, 1));

        var fixes = StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(1, fixes);
        Assert.Equal(3, headings.Single(h => h.Index == 2).Level);
    }

    /// <summary>Cùng chốt đó cho style Heading built-in trên chính đoạn.</summary>
    [Fact]
    public void Nhanh_duong_dan_so_khong_ghi_de_cap_ma_style_built_in_da_khai()
    {
        var document = Doc((0, "1. Mục một"), (2, "2. Mục hai"));
        document.Paragraphs.Single(p => p.Index == 2).HasBuiltInHeadingStyle = true;
        var headings = Headings((0, 2), (2, 3));

        var fixes = StructuralHierarchyResolver.Apply(headings, document);

        Assert.Equal(0, fixes);
        Assert.Equal(3, headings.Single(h => h.Index == 2).Level);
    }

    private static DocxHeaderExtractor.Core.Application.Policy.DocxPolicyState Doc(
        params (int Index, string Text)[] items) =>
        NativePolicyStateFactory.Create(items.Select(x => (x.Index, x.Text, (int?)null, (int?)null)));

    private static List<HeadingRecord> Headings(params (int Index, int Level)[] items) =>
        items.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" }).ToList();
}
