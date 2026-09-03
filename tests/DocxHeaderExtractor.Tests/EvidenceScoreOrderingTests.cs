using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Thang điểm để so với cổng precision phải do BẰNG CHỨNG dẫn dắt.
/// <para>
/// Bản cũ đặt <c>CriticConfirmed</c> lên trên mọi bằng chứng. Đo được (§37.2) trên khoá luận với
/// đáp án có nhãn người: nhóm hiển thị 0,93 — nhóm được TỰ NHẬN — đúng 47,6%, còn nhóm 0,85 bị bắt
/// duyệt tay đúng 100%. Ở mức quyết định: tự nhận 16 mục đúng 62,5%, bắt duyệt 111 mục đúng 82,0%.
/// </para>
/// <para>
/// Lý do phản biện là PHẢN chỉ báo: nó chỉ chạy trên khối mà chính pipeline đã đánh dấu không đáng
/// tin, nên "đã qua phản biện" nghĩa là đến từ vùng đáng ngờ.
/// </para>
/// </summary>
public class EvidenceScoreOrderingTests
{
    /// <summary>Đủ 5/5 kiểm tra phải xếp TRÊN mục chỉ có phản biện xác nhận.</summary>
    [Fact]
    public void Bang_chung_day_du_xep_tren_muc_chi_co_phan_bien()
    {
        var full = Score(Heading(HeadingSource.Model, critic: false, Evidence(5)));
        var criticOnly = Score(Heading(HeadingSource.Model, critic: true, Evidence(2)));

        Assert.True(full > criticOnly,
            $"5/5 kiểm tra ({full}) phải hơn 2/5 có phản biện ({criticOnly})");
    }

    /// <summary>Phản biện KHÔNG được tự nâng điểm: cùng bằng chứng thì cùng điểm.</summary>
    [Fact]
    public void Phan_bien_khong_con_nang_diem()
    {
        Assert.Equal(
            Score(Heading(HeadingSource.Model, critic: false, Evidence(3))),
            Score(Heading(HeadingSource.Model, critic: true, Evidence(3))));
    }

    /// <summary>
    /// Mục KHÔNG có evidence không được nhận điểm cao — nhóm này đo được 0/5 đúng, toàn bộ là mục
    /// mang style nhưng chưa qua một kiểm tra cấu trúc nào.
    /// </summary>
    [Fact]
    public void Khong_co_evidence_thi_khong_duoc_diem_cao()
    {
        var none = Score(Heading(HeadingSource.Style, critic: true, evidence: null, confidence: 0.95));

        Assert.True(none <= 0.60, $"mục không có evidence vẫn được {none}");
    }

    /// <summary>Thang điểm phải đơn điệu theo số kiểm tra đã qua.</summary>
    [Fact]
    public void Diem_tang_dan_theo_so_kiem_tra()
    {
        var scores = Enumerable.Range(0, 6)
            .Select(n => Score(Heading(HeadingSource.Model, critic: false, Evidence(n))))
            .ToList();

        Assert.Equal(scores.OrderBy(x => x), scores);
        Assert.True(scores[5] > scores[0]);
    }

    /// <summary>Ứng viên thuần heuristic vẫn giữ trần cũ — hình thức không thay được cấu trúc.</summary>
    [Fact]
    public void Ung_vien_heuristic_van_bi_chan_tran()
    {
        Assert.True(Score(Heading(HeadingSource.Heuristic, critic: true, Evidence(5))) <= 0.75);
    }

    private static double Score(HeadingRecord h)
    {
        var list = new List<HeadingRecord> { h };
        PrecisionAcceptanceGate.Apply(list, profile: null, targetPrecision: 0.93, minimumSamples: 52);
        return h.Confidence;
    }

    private static HeadingEvidence Evidence(int passed)
    {
        var f = new bool[5];
        for (var i = 0; i < passed; i++) f[i] = true;
        return new HeadingEvidence(f[0], f[1], f[2], f[3], f[4], "supporting_checks");
    }

    private static HeadingRecord Heading(
        HeadingSource source, bool critic, HeadingEvidence? evidence, double confidence = 0.8) => new()
    {
        Index = 0,
        Level = 1,
        Text = "MỞ ĐẦU",
        Source = source,
        CriticConfirmed = critic,
        Evidence = evidence,
        Confidence = confidence,
    };
}
