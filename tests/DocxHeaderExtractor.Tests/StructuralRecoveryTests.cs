using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public class StructuralRecoveryTests
{
    private static SlimParagraph P(int index, string text, int tableDepth = 0) => new()
    {
        Index = index,
        StableId = $"body[1]/p[{index}]",
        Text = text,
        TableDepth = tableDepth,
    };

    private static HeadingRecord H(int index, int level, string text) => new()
    {
        Index = index,
        Level = level,
        Text = text,
        Source = HeadingSource.Model,
        Confidence = 1.0,
    };

    /// <summary>Ca đo được trên tài liệu thật: 3.1 ngoài bảng được nhận, 3.2 trong bảng bị loại.</summary>
    [Fact]
    public void Cuu_em_ke_tiep_bi_model_loai_du_nam_trong_bang()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(26, "3. MB quân sự nước ngoài"),
            P(27, "3.1. Trong dự báo (tổng số tốp/số chiếc/đêm): 0/0"),
            P(28, "- Cất, hạ cánh tại VN: 0/0"),
            P(30, "3.2. Ngoài dự báo (tổng số tốp/số chiếc/đêm): 02/02/0", tableDepth: 1),
        };
        var accepted = new Dictionary<int, HeadingRecord>
        {
            [26] = H(26, 2, "3. MB quân sự nước ngoài"),
            [27] = H(27, 3, "3.1. Trong dự báo (tổng số tốp/số chiếc/đêm): 0/0"),
        };

        var found = StructuralRecovery.Find(reviewed, accepted);

        var one = Assert.Single(found);
        Assert.Equal(30, one.Paragraph.Index);
        Assert.Equal(3, one.Level);                       // cùng cấp với 3.1
        Assert.Contains("trong bảng", one.Reason);
    }

    [Fact]
    public void Cuu_day_chuyen_32_roi_den_33()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "3.1. Một"),
            P(11, "3.2. Hai", tableDepth: 1),
            P(12, "3.3. Ba", tableDepth: 1),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 3, "3.1. Một") };

        var found = StructuralRecovery.Find(reviewed, accepted);

        Assert.Equal([11, 12], found.Select(f => f.Paragraph.Index));
        Assert.All(found, f => Assert.Equal(3, f.Level));
    }

    /// <summary>Sang mục cha khác thì hết phạm vi: 4.1 không phải em của 3.1.</summary>
    [Fact]
    public void Khong_cuu_khi_khac_tien_to_cha()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "3.1. Một"),
            P(11, "4.1. Thuộc mục khác"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 3, "3.1. Một") };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }

    [Fact]
    public void Khong_cuu_khi_da_co_heading_nong_hon_chen_giua()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "3.1. Một"),
            P(20, "IV. Phần mới"),
            P(30, "3.2. Hai"),
        };
        var accepted = new Dictionary<int, HeadingRecord>
        {
            [10] = H(10, 3, "3.1. Một"),
            [20] = H(20, 1, "IV. Phần mới"),   // nông hơn ⇒ đóng phạm vi mục 3
        };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }

    /// <summary>
    /// Đánh số một cấp không có tiền tố cha để xác định anh em, mà tài liệu hành chính đầy dòng
    /// số liệu mở đầu bằng số — cứu ở đó sẽ đổ vào hàng loạt dòng không phải tiêu đề.
    /// </summary>
    [Fact]
    public void Khong_cuu_danh_so_mot_cap()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "1. Phòng không"),
            P(11, "2. Không quân"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 2, "1. Phòng không") };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }

    [Fact]
    public void Khong_cuu_doan_da_duoc_nhan()
    {
        var reviewed = new List<SlimParagraph> { P(10, "3.1. Một"), P(11, "3.2. Hai") };
        var accepted = new Dictionary<int, HeadingRecord>
        {
            [10] = H(10, 3, "3.1. Một"),
            [11] = H(11, 3, "3.2. Hai"),
        };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }

    /// <summary>Đoạn không nằm trong tập model được hỏi thì cứu vào là vượt quyền.</summary>
    [Fact]
    public void Khong_cuu_doan_ngoai_tap_review()
    {
        var reviewed = new List<SlimParagraph> { P(10, "3.1. Một") };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 3, "3.1. Một") };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }
}
