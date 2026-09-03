using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public class ChunkerOverlapCapTests
{
    private static XmlLine Line(string text, int? candidateIndex = null) =>
        new(text, candidateIndex, candidateIndex is not null);

    /// <summary>
    /// Ứng viên thưa là ca thật: tài liệu 344 đoạn chỉ có 13 ứng viên. Phần đuôi cần lùi lại để
    /// gom đủ 2 ứng viên chồng lấn sẽ dài gần bằng cả khối; nếu không chặn, khối mới sinh ra đã
    /// đầy và mỗi dòng lại thành một khối — đo được 138 khối trước khi có mức trần.
    /// </summary>
    [Fact]
    public void Ung_vien_thua_khong_lam_no_so_khoi()
    {
        var lines = new List<XmlLine>();
        for (var i = 0; i < 300; i++)
        {
            // Cứ 40 dòng mới có một ứng viên — đúng mật độ của tài liệu thật.
            lines.Add(i % 40 == 0 ? Line($"<p i=\"{i}\">tiêu đề {i}</p>", i) : Line($"<p>dòng nội dung {i}</p>"));
        }

        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 200, overlapCandidates: 2,
            maxCandidatesPerChunk: 12, countTokens: t => t.Length / 4 + 1);

        // Tổng chi phí ~300 dòng chia ngân sách 200 token: vài chục khối là hợp lý, hàng trăm thì không.
        Assert.InRange(chunks.Count, 1, 40);
        Assert.All(chunks, c => Assert.NotEmpty(c.CandidateIndexes));
    }

    /// <summary>Mọi ứng viên phải xuất hiện ít nhất một lần, kể cả sau khi cắt bớt chồng lấn.</summary>
    [Fact]
    public void Khong_danh_roi_ung_vien_nao_khi_cat_bot_chong_lan()
    {
        var lines = new List<XmlLine>();
        var expected = new List<int>();
        for (var i = 0; i < 200; i++)
        {
            if (i % 25 == 0) { lines.Add(Line($"<p i=\"{i}\">tiêu đề {i}</p>", i)); expected.Add(i); }
            else lines.Add(Line($"<p>dòng {i}</p>"));
        }

        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 150, overlapCandidates: 2,
            maxCandidatesPerChunk: 12, countTokens: t => t.Length / 4 + 1);

        var covered = chunks.SelectMany(c => c.CandidateIndexes).Distinct().OrderBy(i => i);
        Assert.Equal(expected, covered);
    }

    /// <summary>Một dòng dài hơn cả ngân sách vẫn phải đi tiếp, không được kẹt vòng lặp.</summary>
    [Fact]
    public void Dong_dai_hon_ngan_sach_van_tien_duoc()
    {
        var lines = new List<XmlLine>
        {
            Line(new string('x', 4000), 0),
            Line(new string('y', 4000), 1),
        };

        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 100, overlapCandidates: 2,
            maxCandidatesPerChunk: 12, countTokens: t => t.Length / 4 + 1);

        Assert.Equal([0, 1], chunks.SelectMany(c => c.CandidateIndexes).Distinct().OrderBy(i => i));
    }
}
