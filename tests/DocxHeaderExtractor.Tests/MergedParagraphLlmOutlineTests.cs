using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class MergedParagraphLlmOutlineTests
{
    [Fact]
    public async Task Chi_giu_segment_duoc_model_xac_nhan_la_heading_va_cat_dung_ranh_gioi()
    {
        var doc = new SlimDocument
        {
            FileName = "legal.docx",
            SourcePath = "legal.docx",
            Paragraphs =
            [
                P(0,
                    "Điều 1. Phạm vi điều chỉnh Luật này quy định về hoạt động bảo vệ an ninh quốc gia. " +
                    "Điều 2. Giải thích từ ngữ Trong Luật này, các từ ngữ dưới đây được hiểu như sau:"),
            ],
        }.Build();

        // Model: cả hai segment đều HEADING (đúng), rồi LlmBoundaryCutter cắt bằng few-shot đã đo.
        using var classifier = new ScriptedClassifier(
            classify: _ => "HEADING",
            cut: seg => seg.StartsWith("Điều 1.", StringComparison.Ordinal)
                ? "Điều 1. Phạm vi điều chỉnh"
                : "Điều 2. Giải thích từ ngữ");

        var headings = await MergedParagraphLlmOutline.BuildAsync(
            doc, DocumentMode.VietnameseLegal, classifier);

        Assert.Equal(2, headings.Count);
        Assert.Contains(headings, h => h.Text == "Điều 1. Phạm vi điều chỉnh");
        Assert.Contains(headings, h => h.Text == "Điều 2. Giải thích từ ngữ");
        Assert.All(headings, h => Assert.Equal("merged_paragraph_llm_segment", h.ConfidenceBasis));
        // Grounding: mỗi heading phải khớp đúng OriginalText[Start..End].
        Assert.All(headings, h =>
            Assert.Equal(h.Text, h.OriginalText![h.HeadingSpan!.Start..h.HeadingSpan.End]));
    }

    [Fact]
    public async Task Bo_qua_segment_model_noi_la_NOISE()
    {
        var doc = new SlimDocument
        {
            FileName = "legal.docx",
            SourcePath = "legal.docx",
            Paragraphs =
            [
                P(0,
                    "Điều 1. Phạm vi điều chỉnh Luật này quy định về hoạt động. " +
                    "1. Trường hợp thứ nhất được áp dụng khi có yêu cầu bằng văn bản từ cơ quan có thẩm quyền."),
            ],
        }.Build();

        // Model: chỉ segment đầu là HEADING, segment thứ hai (mục con đánh số) là NOISE.
        using var classifier = new ScriptedClassifier(
            classify: seg => seg.StartsWith("Điều 1.", StringComparison.Ordinal) ? "HEADING" : "NOISE",
            cut: _ => "Điều 1. Phạm vi điều chỉnh");

        var headings = await MergedParagraphLlmOutline.BuildAsync(
            doc, DocumentMode.VietnameseLegal, classifier);

        Assert.Single(headings);
        Assert.Equal("Điều 1. Phạm vi điều chỉnh", headings[0].Text);
    }

    [Fact]
    public async Task Bo_qua_doan_trong_muc_luc_du_model_se_noi_gi()
    {
        // Đo ở handoff §114: trang mục lục có độ dài segment trung vị 12-36 ký tự — nhiều mốc liên
        // tiếp gần như không có thân bài. Đoạn dưới đây dựng đúng hình dạng đó.
        var doc = new SlimDocument
        {
            FileName = "rfc.docx",
            SourcePath = "rfc.docx",
            Paragraphs =
            [
                P(0, "1. Introduction 2. Syntax 3. Notation 4. Overview 5. Storage 6. Caching 7. Security"),
            ],
        }.Build();

        // Model luôn trả HEADING nếu được hỏi — test khoá ở chỗ nó KHÔNG được hỏi, vì lọc trang mục
        // lục phải chặn trước khi gọi model.
        using var classifier = new ScriptedClassifier(classify: _ => "HEADING", cut: seg => seg);

        var headings = await MergedParagraphLlmOutline.BuildAsync(
            doc, DocumentMode.VietnameseLegal, classifier);

        Assert.Empty(headings);
        Assert.False(classifier.WasCalled);
    }

    [Fact]
    public async Task Tra_ve_rong_khi_domain_chua_co_bang_do()
    {
        var doc = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs =
            [
                P(0,
                    "1. Mục thứ nhất nội dung dài để không bị coi là mục lục dày đặc theo ngưỡng đo được. " +
                    "2. Mục thứ hai nội dung dài để không bị coi là mục lục dày đặc theo ngưỡng đo được."),
            ],
        }.Build();
        using var classifier = new ScriptedClassifier(classify: _ => "HEADING", cut: seg => seg);

        var headings = await MergedParagraphLlmOutline.BuildAsync(
            doc, DocumentMode.VietnameseAdministrative, classifier);

        Assert.Empty(headings);
        Assert.False(classifier.WasCalled);
    }

    private static SlimParagraph P(int index, string text) => new()
    {
        Index = index,
        StableId = $"body[1]/p[{index + 1}]",
        Text = text,
    };

    private sealed class ScriptedClassifier(Func<string, string> classify, Func<string, string> cut)
        : IHeaderClassifier
    {
        public bool WasCalled { get; private set; }

        public string ModelName => "test/merged-paragraph";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            WasCalled = true;
            // Phân biệt hai lượt gọi (classify vs cut) bằng chính nội dung system prompt: classify
            // dùng prompt HEADING/NOISE cố định, cut dùng prompt domain (Vietnamese, "Điều N.").
            var isClassify = systemPrompt.Contains("HEADING or NOISE", StringComparison.Ordinal);
            var fragment = ExtractFragment(userMessage);
            var response = isClassify ? classify(fragment) : cut(fragment);
            return Task.FromResult(response);
        }

        private static string ExtractFragment(string userMessage)
        {
            var lines = userMessage.Split('\n');
            return lines.Length >= 2 ? lines[1] : userMessage;
        }

        public void Dispose() { }
    }
}
