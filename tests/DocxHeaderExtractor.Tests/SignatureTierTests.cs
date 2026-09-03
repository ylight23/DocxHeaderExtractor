using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Cấp cho tài liệu đánh số GÕ TAY. Đường dẫn số Ả Rập ("3.1" → [3,1]) chỉ nối được các mục cùng
/// hệ ký hiệu, nên nó không biết "PHẦN I" nằm trên "1." — hai tầng thuộc hai hệ khác nhau. Kết quả
/// đo được trên bench trước khi sửa: "1." bị neo ở cấp 1 rồi "1.1." thành cấp 2, trong khi đáp án
/// là 2 và 3.
/// <para>
/// Luật thay thế dựa trên bất biến sẵn có: cùng chữ ký (Kind:Depth) thì cùng cấp, và thứ tự xuất
/// hiện lần đầu của các chữ ký là thứ tự lồng nhau. Không nhắc tới "PHẦN" hay "Chương".
/// </para>
/// </summary>
public sealed class SignatureTierTests
{
    private static (List<HeadingRecord> Headings, DocxHeaderExtractor.DocumentProcessing.Policy.DocxPolicyState Document) Build(
        params string[] texts)
    {
        var headings = new List<HeadingRecord>();
        for (var i = 0; i < texts.Length; i++)
        {
            // Cấp khởi đầu cố tình sai hết về 1: bài kiểm là bộ sắp cấp có tự dựng lại được không.
            headings.Add(new HeadingRecord
            {
                Index = i,
                StableId = $"body[1]/p[{i + 1}]",
                Text = texts[i],
                Level = 1,
                Source = HeadingSource.Model,
            });
        }
        var doc = NativePolicyStateFactory.Create(texts.Select((text, index) =>
            (index, text, (int?)null, (int?)null)));
        return (headings, doc);
    }

    [Fact]
    public void La_Ma_bao_ngoai_A_Rap_thi_thanh_ba_tang()
    {
        var (headings, doc) = Build(
            "PHẦN I. CƠ SỞ LÝ LUẬN",
            "1. Khái niệm cơ bản",
            "1.1. Định nghĩa",
            "1.2. Phân loại",
            "2. Vai trò trong thực tiễn",
            "PHẦN II. THỰC TRẠNG",
            "1. Tình hình chung");

        StructuralHierarchyResolver.Apply(headings, doc);

        Assert.Equal([1, 2, 3, 3, 2, 1, 2], headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    [Fact]
    public void Cung_chu_ky_thi_cung_cap_du_o_xa_nhau()
    {
        var (headings, doc) = Build(
            "I. Mở đầu",
            "1. Bối cảnh",
            "II. Nội dung",
            "1. Phương pháp",
            "III. Kết luận");

        StructuralHierarchyResolver.Apply(headings, doc);

        var levels = headings.OrderBy(h => h.Index).Select(h => h.Level).ToList();
        Assert.Equal(levels[0], levels[2]);
        Assert.Equal(levels[0], levels[4]);
        Assert.Equal(levels[1], levels[3]);
        Assert.True(levels[1] > levels[0], "mục Ả Rập phải nằm dưới mục La Mã");
    }

    [Fact]
    public void Khong_ghi_de_cap_ma_style_heading_built_in_da_khai()
    {
        // Đo được trên bench: tài liệu dùng toàn style Heading chuẩn bị tầng chữ ký ghi đè 5 cấp,
        // kéo độ chính xác cấp từ 100% xuống 87,2%. Cấu trúc đứng trên suy luận — kể cả suy luận
        // từ chính đánh số. Thêm lý do: "Chương 1." không phân tích được nên chữ ký gặp đầu tiên
        // là Arabic:2 của "1.1.", và nó bị xếp nhầm thành tầng ngoài cùng.
        var (headings, doc) = Build(
            "Chương 1. Quy định chung",
            "1.1. Phạm vi điều chỉnh",
            "1.2.1. Cơ quan quản lý");

        foreach (var p in doc.Paragraphs) p.HasBuiltInHeadingStyle = true;
        var levels = new[] { 1, 2, 3 };
        for (var i = 0; i < headings.Count; i++) headings[i].Level = levels[i];

        StructuralHierarchyResolver.Apply(headings, doc);

        Assert.Equal(levels.Select(l => (int?)l), headings.OrderBy(h => h.Index).Select(h => h.Level));
    }

    [Fact]
    public void Mot_he_ky_hieu_duy_nhat_thi_khong_suy_gi_ca()
    {
        // Chỉ một chữ ký thì không có quan hệ lồng nhau nào để suy; đừng bịa ra tầng.
        var (headings, doc) = Build("1. Một", "2. Hai", "3. Ba");

        StructuralHierarchyResolver.Apply(headings, doc);

        Assert.All(headings, h => Assert.Equal(1, h.Level));
    }
}
