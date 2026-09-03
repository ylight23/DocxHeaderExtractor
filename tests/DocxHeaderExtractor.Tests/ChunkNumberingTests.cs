using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Số hiệu khối đi thẳng vào prompt qua <c>DOCUMENT_VIEW {"part":N,"totalParts":M}</c>. Nếu N lấy
/// ordinal TRƯỚC khi lọc còn M đếm SAU khi lọc thì ta nói với mô hình một điều tự phủ định.
/// Quan sát trên tài liệu thật: lượt xác minh Structure báo "chia thành 8 khối" rồi in "khối 14/8".
/// </summary>
public sealed class ChunkNumberingTests
{
    [Fact]
    public void So_hieu_khoi_lien_tuc_va_khong_vuot_qua_tong_so_khoi()
    {
        // Xen kẽ ứng viên và những quãng dài chỉ có ngữ cảnh: các quãng đó tạo ra khối không còn
        // câu hỏi nào và bị lọc bỏ — đúng điều kiện sinh ra lỗ hổng đánh số.
        var lines = new List<XmlLine>();
        for (var block = 0; block < 6; block++)
        {
            lines.Add(new XmlLine(new string('c', 400), block * 100, true));
            for (var filler = 0; filler < 8; filler++)
                lines.Add(new XmlLine(new string('n', 400), null, false));
        }

        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 300, overlapCandidates: 0);

        Assert.NotEmpty(chunks);
        Assert.Equal(
            Enumerable.Range(1, chunks.Count),
            chunks.Select(c => c.Number));
        Assert.All(chunks, c => Assert.InRange(c.Number, 1, chunks.Count));
    }

    [Fact]
    public void Prompt_khong_bao_gio_noi_phan_N_cua_M_voi_N_lon_hon_M()
    {
        var lines = new List<XmlLine>();
        for (var block = 0; block < 5; block++)
        {
            lines.Add(new XmlLine(new string('c', 400), block * 100, true));
            for (var filler = 0; filler < 8; filler++)
                lines.Add(new XmlLine(new string('n', 400), null, false));
        }

        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 300, overlapCandidates: 0);

        foreach (var chunk in chunks)
        {
            var view = NeutralDocumentViewSerializer.WrapChunk(chunk.Lines, chunk.Number, chunks.Count);
            Assert.Contains($"\"part\":{chunk.Number},\"totalParts\":{chunks.Count}", view);
            Assert.True(chunk.Number <= chunks.Count,
                $"prompt nói phần {chunk.Number} của {chunks.Count}");
        }
    }
}
