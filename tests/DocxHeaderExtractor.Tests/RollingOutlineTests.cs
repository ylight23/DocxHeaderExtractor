using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Khung outline tăng dần: khối sau phải nhận lại mục lục các khối trước đã dựng. Ba tính chất
/// làm nên tính đúng của nó, và cả ba đều đã từng sai ở một phiên bản nào đó của anchor:
/// <list type="number">
/// <item>Khối đầu không có khung (chưa dựng được gì) — nếu có thì khung đến từ chỗ khác chứ không
/// phải từ kết quả thật.</item>
/// <item>Khung KHÔNG chứa đoạn khối này đang hỏi. Khối chồng lấn nên đoạn cuối khối trước xuất
/// hiện lại ở đầu khối sau; đưa lại cấp cũ của nó là mớm đáp án.</item>
/// <item>Khung giữ bộ xương cấp 1–2 của CẢ tài liệu, không chỉ N mục gần nhất. Mục ở khối 5 cần
/// biết nó đang nằm dưới chương nào — mà chương đó được chốt ở khối 1.</item>
/// </list>
/// </summary>
public sealed class RollingOutlineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-rolling-{Guid.NewGuid():N}");

    /// <summary>
    /// 20 đề mục — đủ để "12 mục gần nhất" và "bộ xương cấp 1–2" là hai tập khác nhau, nên tính
    /// chất số 3 mới kiểm được.
    /// </summary>
    private static BenchDoc Document()
    {
        var paragraphs = new List<BenchPara>();
        for (var section = 1; section <= 20; section++)
        {
            paragraphs.Add(new($"{section}. Phần công tác số {section}", 1, Bold: true, SizePt: 14));
            paragraphs.Add(new(
                $"Nội dung mô tả chi tiết của phần {section}, gồm các số liệu tổng hợp trong kỳ báo " +
                "cáo và phần đánh giá kèm theo của đơn vị thực hiện nhiệm vụ."));
        }
        return new BenchDoc("rolling-outline", "20 đề mục để kiểm tra khung outline tăng dần", paragraphs);
    }

    private async Task<OutlineCapturingClassifier> RunAsync(bool rolling)
    {
        var path = BenchDocumentFactory.Write(Document(), _dir);
        var classifier = new OutlineCapturingClassifier();
        var options = new PipelineOptions();
        options.RollingOutline = rolling;
        // Chặn 4 ứng viên/khối: cần ≥5 khối để khối cuối nằm ngoài cửa sổ "12 mục gần nhất".
        options.Chunking.MaxCandidatesPerChunk = 4;
        using var pipeline = new HeaderExtractionPipeline(options, classifier);
        await pipeline.RunAsync(path);
        return classifier;
    }

    [Fact]
    public async Task Tat_co_thi_khong_khoi_nao_nhan_khung()
    {
        var classifier = await RunAsync(rolling: false);
        Assert.NotEmpty(classifier.Views);
        Assert.DoesNotContain(classifier.Views, v => v.View.Contains("OUTLINE_DA_DUNG"));
    }

    [Fact]
    public async Task Khoi_dau_khong_co_khung_cac_khoi_sau_deu_co()
    {
        var classifier = await RunAsync(rolling: true);
        Assert.True(classifier.Views.Count >= 5, $"Cần ≥5 khối để kiểm, chỉ có {classifier.Views.Count}.");

        Assert.DoesNotContain("OUTLINE_DA_DUNG", classifier.Views[0].View);
        foreach (var (view, asked) in classifier.Views.Skip(1))
            Assert.Contains("OUTLINE_DA_DUNG", view);
    }

    [Fact]
    public async Task Khung_khong_lap_lai_doan_khoi_nay_dang_hoi()
    {
        var classifier = await RunAsync(rolling: true);
        foreach (var (view, asked) in classifier.Views.Skip(1))
        {
            var carried = OutlineIndexes(view);
            Assert.NotEmpty(carried);
            Assert.Empty(carried.Intersect(asked));
        }
    }

    [Fact]
    public async Task Khung_mang_theo_cap_ma_mo_hinh_da_chot_khong_phai_cap_mac_dinh()
    {
        var classifier = await RunAsync(rolling: true);
        // Bộ phân loại kịch bản gán cấp 1 cho đoạn có index chia hết cho 4, cấp 3 cho phần còn lại.
        // Khung phải phản ánh đúng hai giá trị đó — nếu nó bịa ra cấp thì thấy ngay.
        var levels = classifier.Views.Skip(1).SelectMany(v => OutlineLevels(v.View)).Distinct().Order().ToList();
        Assert.Equal([1, 3], levels);
    }

    [Fact]
    public async Task Khung_giu_bo_xuong_cap_1_ca_tai_lieu_khong_chi_muc_gan_nhat()
    {
        var classifier = await RunAsync(rolling: true);
        var last = classifier.Views[^1];
        var carried = OutlineIndexes(last.View);

        var everAsked = classifier.Views.SelectMany(v => v.Asked).Distinct().ToList();
        var prior = everAsked.Where(i => i < last.Asked.Min()).ToList();
        Assert.True(prior.Count > RollingRecentProbe,
            $"Cần nhiều hơn {RollingRecentProbe} mục đứng trước khối cuối để kiểm, chỉ có {prior.Count}.");

        // Mọi mục cấp 1 đứng trước phải còn — kể cả mục ở khối ĐẦU, nằm ngoài cửa sổ "12 gần nhất".
        var missingSkeleton = prior.Where(i => i % 4 == 0 && !carried.Contains(i)).ToList();
        Assert.True(missingSkeleton.Count == 0,
            $"Mất bộ xương cấp 1: [{string.Join(',', missingSkeleton)}] bị bỏ khỏi khung.");

        // Nhưng KHÔNG phải mục cấp sâu nào cũng còn: còn hết thì đây là nối lịch sử, không phải khung.
        Assert.Contains(prior, i => i % 4 != 0 && !carried.Contains(i));
    }

    /// <summary>
    /// Bằng hằng <c>RollingRecentCount</c> trong <c>BuildRollingOutline</c>. Phép kiểm bộ xương chỉ
    /// có nghĩa khi số mục đứng trước NHIỀU HƠN cửa sổ này — nếu không thì "12 mục gần nhất" đã bao
    /// trọn mọi mục và luật giữ bộ xương chẳng phải làm gì.
    /// </summary>
    private const int RollingRecentProbe = 12;

    private static string Block(string view) =>
        Regex.Match(view, @"OUTLINE_DA_DUNG.*?END_OUTLINE_DA_DUNG", RegexOptions.Singleline).Value;

    private static List<int> OutlineIndexes(string view) =>
        [.. Regex.Matches(Block(view), @"""i"":(\d+)").Select(m => int.Parse(m.Groups[1].Value))];

    private static List<int> OutlineLevels(string view) =>
        [.. Regex.Matches(Block(view), @"""level"":(\d+)").Select(m => int.Parse(m.Groups[1].Value))];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Nhận mọi ứng viên là heading và ghi lại view của lượt phân loại. Cấp gán theo CHỈ SỐ ĐOẠN
    /// (index chia hết cho 4 → cấp 1, còn lại → cấp 3), KHÔNG theo vị trí trong khối: khối chồng
    /// lấn nên "đoạn đầu khối" không ổn định — cùng một đoạn nhận cấp 1 ở khối này, cấp 3 ở khối
    /// kia, và bản đầu của test này rơi đúng vào đó nên khung chỉ toàn cấp 1.
    /// </summary>
    private sealed class OutlineCapturingClassifier : IHeaderClassifier
    {
        public List<(string View, IReadOnlyList<int> Asked)> Views { get; } = [];

        public string ModelName => "test/rolling";
        public int ContextSize => 8192;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default)
        {
            Views.Add((chunkXml, [.. allowedIndexes]));
            return Task.FromResult(Headings(allowedIndexes));
        }

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            Task.FromResult(Headings(allowedIndexes));

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            Task.FromResult(Headings([.. headings.Select(h => h.Index)]));

        private static ChunkResult Headings(IReadOnlyList<int> indexes) =>
            new([.. indexes.Select(i => new ModelHeading
            {
                Index = i,
                Level = i % 4 == 0 ? 1 : 3,
                Role = SemanticRole.Heading,
            })], "{}", 0, 1, new HashSet<int>());

        public void Dispose() { }
    }
}
