using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// CRITIC_ANCHORS phải là mốc cấu trúc QUANH đoạn đang bị phản biện, và không được chứa chính
/// đoạn đó. Bản đầu lấy 12 heading đầu tài liệu theo index nên đoạn ở cuối tài liệu được phản
/// biện bằng mốc của phần mở đầu, đồng thời anchor lặp lại giả thuyết cũ kèm cấp — mớm đáp án
/// cho đúng prompt được giao nhiệm vụ đi phản bác.
/// </summary>
public sealed class CriticAnchorContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-anchor-{Guid.NewGuid():N}");

    /// <summary>Tài liệu đủ dài để "12 heading đầu" và "12 heading gần nhất" là hai tập khác nhau.</summary>
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
        return new BenchDoc("anchor-locality", "20 đề mục cùng cấp để kiểm tra tính cục bộ của anchor", paragraphs);
    }

    [Fact]
    public async Task Anchor_bam_theo_khoi_va_khong_lap_lai_doan_dang_bi_phan_bien()
    {
        var path = BenchDocumentFactory.Write(Document(), _dir);
        using var classifier = new AnchorCapturingClassifier();
        // Chặn 6 ứng viên/khối cho riêng test này. Anchor được dựng bằng cách LOẠI các mục đang bị
        // hỏi, nên nếu cả tài liệu lọt vào một khối thì không còn mốc nào — đúng thứ xảy ra khi
        // chạy không trần. Ở đây cần nhiều khối mới kiểm được tính cục bộ của anchor.
        var options = new PipelineOptions();
        options.Chunking.MaxCandidatesPerChunk = 6;
        using var pipeline = new HeaderExtractionPipeline(options, classifier);

        await pipeline.RunAsync(path);

        Assert.NotEmpty(classifier.CriticViews);
        var withAnchors = classifier.CriticViews.Where(v => v.View.Contains("CRITIC_ANCHORS")).ToList();
        Assert.NotEmpty(withAnchors);

        foreach (var (view, asked) in withAnchors)
        {
            var anchorIndexes = AnchorIndexes(view);
            Assert.NotEmpty(anchorIndexes);

            // 1. Không mớm lại chính giả thuyết đang bị phản biện.
            Assert.Empty(anchorIndexes.Intersect(asked));

            // 2. Mốc phải nằm quanh khối, không phải luôn luôn là phần đầu tài liệu.
            var centre = (asked.Min() + asked.Max()) / 2;
            var farthest = anchorIndexes.Max(i => Math.Abs(i - centre));
            var everyHeading = classifier.CriticViews.SelectMany(v => v.Asked).Distinct().ToList();
            var farthestPossible = everyHeading.Max(i => Math.Abs(i - centre));
            Assert.True(
                farthest < farthestPossible,
                $"Anchor của khối hỏi [{string.Join(',', asked)}] trải xa như thể lấy cả tài liệu: " +
                $"xa nhất {farthest}, tối đa có thể {farthestPossible}.");
        }
    }

    private static List<int> AnchorIndexes(string view)
    {
        var block = Regex.Match(view, @"CRITIC_ANCHORS.*?END_CRITIC_ANCHORS", RegexOptions.Singleline).Value;
        return [.. Regex.Matches(block, @"""i"":(\d+)").Select(m => int.Parse(m.Groups[1].Value))];
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Nhận mọi ứng viên là heading, xác nhận lại tất cả ở lượt critic, và ghi lại view đã nhận.</summary>
    private sealed class AnchorCapturingClassifier : IHeaderClassifier
    {
        public List<(string View, IReadOnlyList<int> Asked)> CriticViews { get; } = [];

        public string ModelName => "test/anchor";
        public int ContextSize => 8192;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            Task.FromResult(Headings(allowedIndexes));

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default)
        {
            CriticViews.Add((chunkXml, [.. allowedIndexes]));
            return Task.FromResult(Headings(allowedIndexes));
        }

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            Task.FromResult(Headings([.. headings.Select(h => h.Index)]));

        private static ChunkResult Headings(IReadOnlyList<int> indexes) =>
            new([.. indexes.Select(i => new ModelHeading
            {
                Index = i,
                Level = 1,
                Role = SemanticRole.Heading,
            })], "{}", 0, 1, new HashSet<int>());

        public void Dispose() { }
    }
}
