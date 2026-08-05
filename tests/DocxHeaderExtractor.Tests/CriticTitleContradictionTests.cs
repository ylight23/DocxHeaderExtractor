using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Critic được phép bác heading, nhưng không được phép tự mâu thuẫn rồi xoá. Quan sát thực tế:
/// một request critic trả document_title cho cả "II. THỰC LỰC CHIẾN ĐẤU", "1. Phòng không" và
/// "2. Không quân" — ba tiêu đề chính trong một tài liệu là điều không tồn tại, mà cả ba đã bị
/// xoá khỏi cây lẫn khỏi danh sách cần duyệt.
/// </summary>
public sealed class CriticTitleContradictionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-critic-{Guid.NewGuid():N}");

    private static readonly BenchDoc Document = new(
        "critic-title",
        "Dãy đề mục La Mã → số, đúng dạng critic hay gán nhầm là document_title",
    [
        new("BÁO CÁO NGÀY 14/01/2026", Bold: true, Caps: true, Center: true, SizePt: 14),
        new("II. THỰC LỰC CHIẾN ĐẤU", 1, Bold: true, Caps: true, SizePt: 14),
        new("Phần này tổng hợp thực lực theo từng quân chủng, kèm số liệu trực ban và tình trạng " +
            "kỹ thuật của các đơn vị đang làm nhiệm vụ sẵn sàng chiến đấu."),
        new("1. Phòng không", 2, Bold: true, SizePt: 14),
        new("Số liệu trực ban của các đơn vị phòng không được tổng hợp theo ca trực trong ngày " +
            "và đối chiếu với báo cáo của sở chỉ huy."),
        new("2. Không quân", 2, Bold: true, SizePt: 14),
        new("Các trung đoàn không quân duy trì lực lượng trực ban theo kế hoạch, số máy bay bảo " +
            "đảm kỹ thuật giữ nguyên so với ngày trước."),
    ]);

    [Fact]
    public async Task Nhieu_document_title_khong_duoc_xoa_heading_ma_chi_bat_duyet()
    {
        // Critic bác MỌI mục được hỏi bằng cùng một lý do "đây là tiêu đề chính của văn bản".
        var (outline, log) = await RunAsync(asked => asked.Select(i => new ModelHeading
        {
            Index = i,
            Level = 0,
            Role = SemanticRole.DocumentTitle,
        }));

        Assert.Contains(outline.Headings, h => h.Text.Contains("THỰC LỰC CHIẾN ĐẤU"));
        Assert.Contains(outline.Headings, h => h.Text.Contains("Phòng không"));
        Assert.Contains(outline.Headings, h => h.Text.Contains("Không quân"));
        // Giữ lại không có nghĩa là tin: chúng phải hiện ra như mục cần người xem lại.
        Assert.All(
            outline.Headings.Where(h => h.Text.Contains("Phòng không") || h.Text.Contains("Không quân")),
            h => Assert.True(h.Disputed));
        Assert.Contains(log, line => line.Contains("document_title"));
    }

    [Fact]
    public async Task Mot_document_title_duy_nhat_cung_khong_duoc_xoa()
    {
        // Trường hợp phổ biến nhất và cũng là chỗ hỏng nặng nhất: heading mở đầu tài liệu trông
        // giống tiêu đề chính nên critic gán "d" cho đúng một đoạn. Đo trên bộ bench, đoạn 0 mất ở
        // 6/7 tài liệu vì lý do này. "d" khẳng định đoạn CÓ vai trò tiêu đề, khác hẳn "n"/"f" — nên
        // nó không được xoá, chỉ được hạ xuống cần duyệt.
        // Ghi lại đúng đoạn nào bị gọi là tiêu đề chính thay vì đoán theo nội dung: thứ tự ứng viên
        // do tầng lọc quyết định, bám vào nó thì test kiểm nhầm thứ.
        var titled = new List<int>();
        var refused = new List<int>();
        var (outline, log) = await RunAsync(asked => asked.Select((i, order) =>
        {
            (order == 0 ? titled : refused).Add(i);
            return new ModelHeading
            {
                Index = i,
                Level = 0,
                Role = order == 0 ? SemanticRole.DocumentTitle : SemanticRole.NormalText,
            };
        }));

        var kept = outline.Headings.FirstOrDefault(h => h.Index == titled.Single());
        Assert.NotNull(kept);
        Assert.True(kept!.Disputed);
        Assert.Contains(log, line => line.Contains("document_title"));

        // Lời bác thật sự ("n" = văn bản thường) thì vẫn phải loại như cũ.
        Assert.DoesNotContain(outline.Headings, h => refused.Contains(h.Index));
    }

    [Fact]
    public async Task Doan_mang_style_heading_built_in_khong_bi_critic_dung_toi()
    {
        // Style Heading built-in là tuyên bố tường minh của người soạn, và cấp cũng lấy từ đó. Nên
        // câu trả lời của critic cho những đoạn này không dùng được vào việc gì: không đổi được
        // việc chúng có mặt, không đổi được cấp. Chúng bị loại khỏi batch critic — vừa để khỏi trả
        // tiền prefill, vừa để khỏi làm nhiễu câu trả lời cho các mục khác trong cùng batch.
        // Đo trên bench: 01-style-chuan dùng toàn style chuẩn mà critic loại 3 mục — đúng 3 mục thiếu.
        var styled = new BenchDoc("critic-style", "Style Heading chuẩn, critic bác toàn bộ",
        [
            new("Chương 1. Quy định chung", 1, Style: "Heading1"),
            new("Nội dung chi tiết của chương một, gồm phạm vi điều chỉnh và đối tượng áp dụng."),
            new("1.1. Phạm vi điều chỉnh", 2, Style: "Heading2"),
            new("Phần này mô tả phạm vi áp dụng của quy định trong toàn bộ đơn vị."),
        ]);
        var path = BenchDocumentFactory.Write(styled, _dir);
        var log = new List<string>();
        // Critic bác MỌI mục bằng "n" — lời bác dứt khoát nhất có thể.
        using var classifier = new ScriptedClassifier(asked => asked.Select(i => new ModelHeading
        {
            Index = i,
            Level = 0,
            Role = SemanticRole.NormalText,
        }));
        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions { Log = log.Add }, classifier);

        var outline = await pipeline.RunAsync(path);

        // Critic bác sạch, nhưng cả hai vẫn còn — và còn với cấp do file khai, không phải cấp model.
        var chapter = outline.Headings.FirstOrDefault(h => h.Text.Contains("Chương 1"));
        var section = outline.Headings.FirstOrDefault(h => h.Text.Contains("1.1."));
        Assert.NotNull(chapter);
        Assert.NotNull(section);
        Assert.Equal(1, chapter!.Level);
        Assert.Equal(2, section!.Level);
        Assert.Contains(log, line => line.Contains("Bỏ qua critic"));
    }

    private async Task<(Core.Models.DocumentOutline Outline, List<string> Log)> RunAsync(
        Func<IReadOnlyList<int>, IEnumerable<ModelHeading>> critique)
    {
        var path = BenchDocumentFactory.Write(Document, _dir);
        var log = new List<string>();
        using var classifier = new ScriptedClassifier(critique);
        using var pipeline = new HeaderExtractionPipeline(
            new PipelineOptions { Log = log.Add },
            classifier);

        return (await pipeline.RunAsync(path), log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Lượt một nhận mọi ứng viên là heading; lượt critic trả theo kịch bản của từng test.</summary>
    private sealed class ScriptedClassifier(Func<IReadOnlyList<int>, IEnumerable<ModelHeading>> critique)
        : IHeaderClassifier
    {
        public string ModelName => "test/scripted";
        public int ContextSize => 8192;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            Task.FromResult(new ChunkResult(
                [.. allowedIndexes.Select(i => new ModelHeading
                {
                    Index = i,
                    Level = 1,
                    Role = SemanticRole.Heading,
                })],
                "{}", 0, 1, new HashSet<int>()));

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default)
        {
            var decisions = critique(allowedIndexes).ToList();
            var refused = decisions
                .Where(d => d.Level <= 0 && d.Role != SemanticRole.Uncertain)
                .ToList();
            return Task.FromResult(new ChunkResult(
                [.. decisions.Where(d => d.Level > 0)],
                "{}", 0, 1,
                refused.Select(d => d.Index).ToHashSet(),
                refused.ToDictionary(d => d.Index, d => d.Role)));
        }

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            Task.FromResult(new ChunkResult(
                [.. headings.Select(h => new ModelHeading
                {
                    Index = h.Index,
                    Level = h.HintLevel ?? 1,
                    Role = SemanticRole.Heading,
                })],
                "{}", 0, 1, new HashSet<int>()));

        public void Dispose() { }
    }
}
