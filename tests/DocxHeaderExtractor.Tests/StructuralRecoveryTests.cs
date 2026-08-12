using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
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
            P(10, "1. Phân xưởng cơ khí"),
            P(11, "2. Phân xưởng lắp ráp"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 2, "1. Phân xưởng cơ khí") };

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

    // ──────────────────────────── TODO mục 7: nhãn + số (PHỤ LỤC 1, PHỤ LỤC 2…) ────────────────────────────

    /// <summary>
    /// Ca thật (TODO mục 7): "PHỤ LỤC 1"/"PHỤ LỤC 2" có role=Normal nên chưa từng tới được mô hình
    /// — đường cứu qua HasStructuralEvidence (cứu đoạn ĐÃ từng bị gắn nhãn document_title) không
    /// chạm tới. Bản NHÃN+SỐ+HẾT cần cờ AllowBareLabelledNumbers (--bare-labels).
    /// </summary>
    [Fact]
    public void Cuu_phu_luc_2_khi_phu_luc_1_da_duoc_nhan()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(1294, "PHỤ LỤC 1"),
            P(1300, "Nội dung phụ lục thứ nhất."),
            P(1335, "PHỤ LỤC 2"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [1294] = H(1294, 1, "PHỤ LỤC 1") };
        var options = new ExtractionOptions { AllowBareLabelledNumbers = true };

        var found = StructuralRecovery.Find(reviewed, accepted, options);

        var one = Assert.Single(found);
        Assert.Equal(1335, one.Paragraph.Index);
        Assert.Equal(1, one.Level);            // cùng cấp với PHỤ LỤC 1
        Assert.Contains("PHỤ LỤC 1", one.Reason);
    }

    /// <summary>Không bật cờ thì "PHỤ LỤC 1"/"PHỤ LỤC 2" không đọc được thành đánh số — giữ hành vi cũ.</summary>
    [Fact]
    public void Khong_cuu_nhan_so_tran_khi_chua_bat_co()
    {
        var reviewed = new List<SlimParagraph> { P(1294, "PHỤ LỤC 1"), P(1335, "PHỤ LỤC 2") };
        var accepted = new Dictionary<int, HeadingRecord> { [1294] = H(1294, 1, "PHỤ LỤC 1") };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted, new ExtractionOptions()));
        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));   // options mặc định null cũng vậy
    }

    /// <summary>Dây chuyền: PHỤ LỤC 2 được cứu mở đường cho PHỤ LỤC 3, giống ca Ả Rập nhiều cấp.</summary>
    [Fact]
    public void Cuu_day_chuyen_phu_luc_2_roi_den_3()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "PHỤ LỤC 1"),
            P(20, "PHỤ LỤC 2"),
            P(30, "PHỤ LỤC 3"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 1, "PHỤ LỤC 1") };
        var options = new ExtractionOptions { AllowBareLabelledNumbers = true };

        var found = StructuralRecovery.Find(reviewed, accepted, options);

        Assert.Equal([20, 30], found.Select(f => f.Paragraph.Index));
        Assert.All(found, f => Assert.Equal(1, f.Level));
    }

    /// <summary>Khác nhãn thì không phải anh em — "CHƯƠNG 2" không cứu vào chuỗi "PHỤ LỤC".</summary>
    [Fact]
    public void Khong_cuu_khi_khac_nhan()
    {
        var reviewed = new List<SlimParagraph> { P(10, "PHỤ LỤC 1"), P(20, "CHƯƠNG 2") };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 1, "PHỤ LỤC 1") };
        var options = new ExtractionOptions { AllowBareLabelledNumbers = true };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted, options));
    }

    /// <summary>Dạng CÓ tiêu đề sau nhãn+số ("Chương 1. Mở đầu") không cần cờ --bare-labels —
    /// LabelledRx của NumberingAudit đọc được vô điều kiện, khác BareLabelledRx.</summary>
    [Fact]
    public void Cuu_nhan_so_co_tieu_de_khong_can_co_bare_labels()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "Chương 1. Mở đầu"),
            P(11, "Nội dung mở đầu."),
            P(20, "Chương 2. Nội dung"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 1, "Chương 1. Mở đầu") };

        var found = StructuralRecovery.Find(reviewed, accepted);   // KHÔNG bật AllowBareLabelledNumbers

        var one = Assert.Single(found);
        Assert.Equal(20, one.Paragraph.Index);
    }

    // ──────────── §55.7: nhãn + số KHÔNG dấu ngắt, và chốt in-hoa chặn chú thích ────────────

    /// <summary>
    /// <c>Chương II QUY ĐỊNH CHUNG</c> — dạng Nghị định 30/2020 bị bản chuyển PDF dán liền, không
    /// còn dấu chấm. Sau §55.2 nó đọc được thành token <c>Labelled</c>, nên đường cứu này phải
    /// nhận nó y như <c>PHỤ LỤC 1</c>/<c>PHỤ LỤC 2</c>.
    /// </summary>
    [Fact]
    public void Cuu_chuong_khong_dau_ngat_khi_chuong_truoc_da_duoc_nhan()
    {
        var reviewed = new List<SlimParagraph>
        {
            P(10, "Chương I QUY ĐỊNH CHUNG"),
            P(14, "Nội dung chương thứ nhất trình bày phạm vi điều chỉnh của văn bản này."),
            P(20, "Chương II QUYỀN VÀ NGHĨA VỤ"),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 1, "Chương I QUY ĐỊNH CHUNG") };

        var found = StructuralRecovery.Find(reviewed, accepted);

        var one = Assert.Single(found);
        Assert.Equal(20, one.Paragraph.Index);
        Assert.Equal(1, one.Level);
    }

    /// <summary>
    /// <b>Đây là test cho lỗ hổng mà bench KHÔNG đo được (§55.8).</b> Chú thích hình/bảng có đúng
    /// hình dạng "nhãn + số + chữ": <c>Bảng 3 Thống kê</c>. Nới <c>LabelledRx</c> ở §55.2 đã làm
    /// chúng parse thành <c>Labelled</c>, mà đường cứu này nhận MỌI token <c>Labelled</c> — nên
    /// chú thích sẽ thành đề mục. <c>StructuralRecovery.Find</c> nằm trong <c>RunModelAsync</c>
    /// nên <c>bench --no-llm</c> vẫn xanh 6/7 trong khi lỗi đã mở.
    /// <para>
    /// Chốt in-hoa ở §55.7 là thứ chặn nó. Test này đỏ nếu ai gỡ chốt đó ra.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Bảng 1 Thống kê ban đầu", "Bảng 2 Thống kê sau điều chỉnh")]
    [InlineData("Hình 1 Sơ đồ tổng thể", "Hình 2 Sơ đồ chi tiết của hệ thống")]
    [InlineData("Table 1 Initial Results", "Table 2 Summary Of Results")]
    public void Khong_cuu_chu_thich_hinh_bang(string first, string next)
    {
        // Số phải LIỀN KỀ, nếu không thì luật cứu-anh-em không chạy và test vô hiệu bất kể chốt.
        var reviewed = new List<SlimParagraph>
        {
            P(10, first),
            P(14, "Nội dung thân bài trình bày chi tiết các bước thực hiện của quy trình này."),
            P(20, next),
        };
        var accepted = new Dictionary<int, HeadingRecord> { [10] = H(10, 1, first) };

        Assert.Empty(StructuralRecovery.Find(reviewed, accepted));
    }
}
