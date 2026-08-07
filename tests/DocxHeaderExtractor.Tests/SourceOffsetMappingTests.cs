using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Chuẩn hoá khoảng trắng là phép MẤT MÁT: mọi chuỗi trắng — kể cả <c>w:tab</c> và <c>w:br</c> —
/// gộp thành một dấu cách. Hai thứ bị mất theo, và cả hai đều cần cho việc khác:
/// <list type="bullet">
/// <item>vị trí <c>w:br</c> — không có nó thì Shift+Enter không phân biệt được với việc Word tự
/// ngắt dòng theo chiều rộng, tức một tiêu đề bị cắt đôi trông y hệt tiêu đề thường;</item>
/// <item>đường về nguồn — không có nó thì mọi thứ tính bằng offset chuẩn hoá đều không ghi ngược
/// lại DOCX được (<c>OutlineWriteback</c> từ chối bằng <c>inline_body_not_splittable</c>).</item>
/// </list>
/// </summary>
public sealed class SourceOffsetMappingTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files) LegacyDocConverter.TryDelete(f);
    }

    /// <summary>Dựng một paragraph từ các run; <c>null</c> trong danh sách nghĩa là một <c>w:br</c>.</summary>
    private string Docx(params string?[] runsAndBreaks)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-src-{Guid.NewGuid():N}.docx");
        _files.Add(path);

        using var wp = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();
        var para = new Paragraph();
        foreach (var piece in runsAndBreaks)
            para.AppendChild(piece is null
                ? new Run(new Break())
                : new Run(new Text(piece) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));
        main.Document = new Document(new Body(para));
        main.Document.Save();
        return path;
    }

    private Core.Models.SlimParagraph First(string path) =>
        new DocxSlimExtractor(new ExtractionOptions()).Extract(path).Paragraphs[0];

    [Fact]
    public void Shift_enter_de_lai_dau_vet_con_Word_tu_ngat_dong_thi_khong()
    {
        var withBreak = First(Docx("2.1.2. Thành", null, "công"));
        var plain = First(Docx("2.1.2. Thành công"));

        // Cùng một chuỗi hiển thị sau chuẩn hoá — đó chính là lý do cần trường riêng.
        Assert.Equal(plain.Text, withBreak.Text);
        Assert.Empty(plain.LineBreakOffsets);
        Assert.Single(withBreak.LineBreakOffsets);
        Assert.Equal("2.1.2. Thành".Length, withBreak.LineBreakOffsets[0]);
    }

    [Fact]
    public void Nhieu_khoang_trang_va_tab_khong_lam_lech_duong_ve_nguon()
    {
        string[] runs = ["2.1.2. Thành   \t  ", "công: 20%"];
        var p = First(Docx(runs));

        Assert.Equal("2.1.2. Thành công: 20%", p.Text);
        AssertSegmentsCoverText(p);
        AssertMapsBackToSource(p, runs);
    }

    /// <summary>
    /// Khoảng trắng bị gộp NGAY GIỮA một run — ca duy nhất phân biệt được ánh xạ đúng với ánh xạ
    /// "cứ cộng dồn": ở đây run không đổi, nên nếu offset thô không được kiểm liền mạch thì mọi ký
    /// tự sau chỗ gộp đều trỏ lệch về nguồn mà vẫn phủ kín text.
    /// </summary>
    [Fact]
    public void Khoang_trang_gop_giua_mot_run_khong_lam_troi_offset_tho()
    {
        string[] runs = ["Phần A     kết luận"];
        var p = First(Docx(runs));

        Assert.Equal("Phần A kết luận", p.Text);
        AssertMapsBackToSource(p, runs);
        // Đúng hai segment: trước và sau chỗ khoảng trắng bị nuốt.
        Assert.Equal(2, p.SourceSegments.Count);
        Assert.Equal("Phần A".Length + 1, p.SourceSegments[1].Start);
        Assert.Equal("Phần A     ".Length, p.SourceSegments[1].RawStart);
    }

    [Fact]
    public void Anh_xa_tra_ve_dung_run_khi_text_trai_qua_nhieu_run()
    {
        var p = First(Docx("Phần A", " – ", "Kết luận"));

        Assert.Equal("Phần A – Kết luận", p.Text);
        // Ký tự đầu thuộc run 0, ký tự cuối thuộc run 2 — nếu ánh xạ gộp hết về một run thì hỏng.
        Assert.Equal(0, SegmentAt(p, 0).RunIndex);
        Assert.Equal(2, SegmentAt(p, p.Text.Length - 1).RunIndex);
    }

    [Fact]
    public void Moi_ky_tu_deu_co_duong_ve_nguon()
    {
        var p = First(Docx("  A", null, "B  \t C  "));

        for (var i = 0; i < p.Text.Length; i++)
            Assert.True(p.SourceSegments.Any(s => i >= s.Start && i < s.End),
                $"ký tự {i} ('{p.Text[i]}') không có segment nào phủ");
    }

    private static Core.Models.SlimSourceSegment SegmentAt(Core.Models.SlimParagraph p, int index) =>
        p.SourceSegments.First(s => index >= s.Start && index < s.End);

    /// <summary>
    /// Phép thử THẬT của ánh xạ: đi ngược từ mỗi ký tự chuẩn hoá về nguồn phải ra đúng ký tự đó —
    /// hoặc ra một ký tự trắng khác, vì <c>\t</c> và <c>w:br</c> đều chuẩn hoá thành dấu cách.
    /// Chỉ kiểm "phủ kín" thì một ánh xạ trỏ lệch hoàn toàn vẫn qua được.
    /// </summary>
    private static void AssertMapsBackToSource(Core.Models.SlimParagraph p, string[] runs)
    {
        foreach (var s in p.SourceSegments)
        {
            var raw = runs[s.RunIndex];
            for (var k = 0; k < s.End - s.Start; k++)
            {
                var got = p.Text[s.Start + k];
                var src = raw[s.RawStart + k];
                Assert.True(got == src || (char.IsWhiteSpace(got) && char.IsWhiteSpace(src)),
                    $"offset {s.Start + k} ('{got}') trỏ về run {s.RunIndex}[{s.RawStart + k}] = '{src}'");
            }
        }
    }

    /// <summary>Segment phải phủ kín, không chồng nhau, và tăng dần theo offset chuẩn hoá.</summary>
    private static void AssertSegmentsCoverText(Core.Models.SlimParagraph p)
    {
        var covered = 0;
        foreach (var s in p.SourceSegments)
        {
            Assert.Equal(covered, s.Start);
            Assert.True(s.End > s.Start);
            covered = s.End;
        }
        Assert.Equal(p.Text.Length, covered);
    }
}
