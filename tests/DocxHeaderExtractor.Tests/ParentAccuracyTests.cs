using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Bài toán con <i>parent finding</i> của HRDoc, thêm bên cạnh "đúng cấp tuyệt đối".
/// <para>
/// Lý do tồn tại, đo được ở §26.2: lớp style-only của khoá luận đúng cấp tuyệt đối 41,2% nhưng
/// đúng cha 100% — cây không sai một cạnh nào, mọi lỗi là lệch ĐỀU một bậc. Chỉ nhìn 41,2% thì kết
/// luận "style vô dụng cho cấp" và ném đi một tín hiệu hoàn hảo, đúng như §17 đã lỡ làm.
/// </para>
/// </summary>
public class ParentAccuracyTests
{
    /// <summary>Cả cây bị đẩy sâu đều một bậc: sai cấp toàn tập, nhưng quan hệ cha–con nguyên vẹn.</summary>
    [Fact]
    public void Lech_deu_mot_bac_thi_sai_cap_toan_bo_nhung_dung_cha_toan_bo()
    {
        var score = Score(
            truth: [(0, 1), (2, 2), (4, 3), (6, 2), (8, 1)],
            got: [(0, 2), (2, 3), (4, 4), (6, 3), (8, 2)]);

        Assert.Equal(0, score.LevelCorrect);
        Assert.Equal(1.0, score.ParentAccuracy);
    }

    /// <summary>Một mục bị gán sai làm ĐỔI cha của chính nó và của mục sau — đúng cái cấp tuyệt đối
    /// che mất khi nó chỉ đếm số ô lệch.</summary>
    [Fact]
    public void Doi_quan_he_cha_con_thi_metric_cay_phat()
    {
        // "4" lẽ ra là con của "2"; gán cấp 1 biến nó thành anh em của "0", và "6" theo đó đổi cha.
        var score = Score(
            truth: [(0, 1), (2, 2), (4, 3), (6, 3)],
            got: [(0, 1), (2, 2), (4, 1), (6, 2)]);

        Assert.True(score.ParentAccuracy < 1.0,
            $"Cây đã đổi hình mà metric cha vẫn 100%: {score.ParentCorrect}/{score.LevelJudged}");
    }

    /// <summary>
    /// Hai mục CÙNG CẤP là anh em, phải cùng cha — không phải mục sau làm con mục trước.
    /// <para>
    /// Mutation test bắt được lỗ này: đổi <c>levelOf(stack[^1]) >= level</c> thành <c>></c> khi đẩy
    /// ngăn xếp thì anh em thành cha–con, mà ba test đầu vẫn xanh cả ba — vì chúng dựng CẢ HAI cây
    /// bằng cùng một hàm nên lỗi triệt tiêu lẫn nhau. Chỉ ca mà đáp án có anh em còn kết quả trả về
    /// thì không mới lộ ra.
    /// </para>
    /// </summary>
    [Fact]
    public void Anh_em_cung_cap_phai_cung_cha_khong_phai_cha_con()
    {
        // Đáp án: "4" là em của "2", cả hai cùng cha "0". Model đẩy "4" xuống cấp 3 ⇒ nó thành CON
        // của "2". Quan hệ đã đổi, metric cha phải phạt.
        var score = Score(
            truth: [(0, 1), (2, 2), (4, 2)],
            got: [(0, 1), (2, 2), (4, 3)]);

        Assert.True(score.ParentAccuracy < 1.0,
            $"Anh em bị biến thành cha–con mà metric vẫn tuyệt đối: {score.ParentCorrect}/{score.LevelJudged}");
    }

    /// <summary>Đúng hoàn toàn thì cả hai thước đo cùng tuyệt đối — chốt chống ăn nhầm.</summary>
    [Fact]
    public void Dung_hoan_toan_thi_ca_hai_thuoc_do_deu_tuyet_doi()
    {
        var score = Score(
            truth: [(0, 1), (2, 2), (4, 3), (6, 2)],
            got: [(0, 1), (2, 2), (4, 3), (6, 2)]);

        Assert.Equal(1.0, score.LevelAccuracy);
        Assert.Equal(1.0, score.ParentAccuracy);
    }

    private static DocScore Score((int Index, int Level)[] truth, (int Index, int Level)[] got)
    {
        var outline = new DocumentOutline
        {
            File = "x.docx",
            Headings = [.. got.Select(x => new HeadingRecord { Index = x.Index, Level = x.Level, Text = "" })],
        };
        var key = AnswerKey.Parse(string.Join('\n', truth.Select(x => $"{x.Index} {x.Level}")));
        return Evaluator.Score("x.docx", outline, [.. truth.Select(x => x.Index)], key);
    }
}
