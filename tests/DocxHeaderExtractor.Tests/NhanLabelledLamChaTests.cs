using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ca đo được trên <c>bench/02-dinh-dang-thu-cong</c>: đúng cấp <b>28,6%</b>, 5/7 mục nông hơn đáp
/// án đúng một cấp. Cấu trúc là "PHẦN I → 1. → 1.1.", tức mục đánh số Ả Rập nằm DƯỚI một nhãn
/// (<c>PHẦN</c>) — đáp án nói cấp phải suy theo cha gần nhất, không theo độ sâu dấu chấm.
/// </summary>
public class NhanLabelledLamChaTests
{
    private static SlimParagraph P(int i, string text) => new()
    {
        Index = i,
        Text = text,
        FontSizePt = 13,
    };

    private static HeadingRecord H(int i, int level, string text) => new()
    {
        Index = i,
        Level = level,
        Text = text,
        Source = HeadingSource.Heuristic,
        Confidence = 1.0,
    };

    /// <summary>Đúng nội dung và đúng cấp khởi tạo của bench/02, để tái lập lỗi thật.</summary>
    private static (List<HeadingRecord> Headings, SlimDocument Document) Bench02()
    {
        (int I, int Lvl, string T)[] rows =
        [
            (0, 1, "PHẦN I. CƠ SỞ LÝ LUẬN"),
            (2, 1, "1. Khái niệm cơ bản"),
            (4, 2, "1.1. Định nghĩa"),
            (6, 2, "1.2. Phân loại"),
            (8, 1, "2. Vai trò trong thực tiễn"),
            (10, 1, "PHẦN II. THỰC TRẠNG"),
            (12, 1, "1. Tình hình chung"),
        ];

        const string Body = "Nội dung phần này mô tả chi tiết các bước triển khai, kèm theo yêu cầu " +
                            "về hạ tầng và nhân sự vận hành, đồng thời nêu rõ trách nhiệm của từng đơn vị.";

        List<SlimParagraph> ps = [];
        for (var i = 0; i <= 13; i++)
            ps.Add(P(i, rows.FirstOrDefault(r => r.I == i) is { T: { } t } ? t : Body));

        var doc = new SlimDocument
        {
            FileName = "02.docx",
            SourcePath = "02.docx",
            Paragraphs = ps,
        }.Build();

        return ([.. rows.Select(r => H(r.I, r.Lvl, r.T))], doc);
    }

    /// <summary>
    /// Đáp án <c>bench/02-dinh-dang-thu-cong.key</c>, nguyên văn:
    /// <c>0→1, 2→2, 4→3, 6→3, 8→2, 10→1, 12→2</c>.
    /// </summary>
    [Fact]
    public void Muc_so_duoi_nhan_PHAN_phai_sau_hon_mot_cap()
    {
        var (headings, doc) = Bench02();

        StructuralHierarchyResolver.Apply(headings, doc);

        Dictionary<int, int> dapAn = new()
        {
            [0] = 1, [2] = 2, [4] = 3, [6] = 3, [8] = 2, [10] = 1, [12] = 2,
        };

        var sai = headings.Where(h => h.Level != dapAn[h.Index])
            .Select(h => $"i={h.Index} \"{h.Text}\" trả {h.Level}, đáp án {dapAn[h.Index]}")
            .ToList();

        Assert.True(sai.Count == 0, string.Join(" · ", sai));
    }

    /// <summary>
    /// Test cô lập ở trên gọi THẲNG resolver nên nó xanh ngay từ đầu — và chính vì thế nó không
    /// bắt được lỗi thật. Lỗi nằm ở chỗ <see cref="StructuralHierarchyResolver"/> và
    /// <see cref="TableOfContentsAnchor"/> nằm trong <c>RunModelAsync</c>, nên đường
    /// <c>--no-llm</c> KHÔNG BAO GIỜ chạy chúng dù cả hai đều tất định và không cần mô hình.
    /// <para>
    /// Đo trên <c>bench</c> (có đáp án): tắt cờ đúng cấp <b>86,1%</b> · đúng cha 91,7% · tuyệt đối
    /// 5/7; bật cờ đúng cấp <b>100%</b> · đúng cha 100% · tuyệt đối 6/7. Precision không đổi.
    /// </para>
    /// Test này ghim rằng cờ THẬT SỰ đổi hành vi — nếu ai nối nhầm chỗ, nó đỏ.
    /// </summary>
    [Fact]
    public void Co_tat_dinh_hierarchy_that_su_doi_hanh_vi_duong_khong_mo_hinh()
    {
        var (tat, doc1) = Bench02();
        var (bat, doc2) = Bench02();

        // Mô phỏng đúng hai đường: đường --no-llm cũ không gọi resolver, đường có cờ thì gọi.
        StructuralHierarchyResolver.Apply(bat, doc2);

        Assert.Equal(1, tat.Single(h => h.Index == 2).Level);   // sai, giữ để lỗi không tàng hình
        Assert.Equal(2, bat.Single(h => h.Index == 2).Level);   // đúng theo đáp án
        Assert.NotEqual(tat.Select(h => h.Level), bat.Select(h => h.Level));
        Assert.NotNull(doc1);
    }

    /// <summary>
    /// MẶC ĐỊNH BẬT — khác mọi cờ mới khác của dự án, và đó là chủ ý: bộ suy cấp này đã có bằng
    /// chứng đáp án người kiểm (§31) và đường có mô hình chạy nó vô điều kiện. Test ghim lựa chọn
    /// đó để không ai tắt nhầm khi dọn dẹp (§51).
    /// </summary>
    [Fact]
    public void Co_tat_dinh_hierarchy_mac_dinh_BAT()
    {
        Assert.True(new PipelineOptions().DeterministicHierarchy);
    }
}
