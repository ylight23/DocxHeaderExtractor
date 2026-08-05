using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

public class PromptTests
{
    /// <summary>
    /// Prompt ghép từ hằng Rules + ExampleInput. Ghép hụt thì mô hình mất luật phân loại mà
    /// chương trình vẫn chạy bình thường — hỏng theo kiểu chỉ lộ ra sau 12 phút suy luận.
    /// </summary>
    [Fact]
    public void System_prompt_contains_rules_example_and_output_schema()
    {
        var s = HeaderPrompt.System;

        Assert.Contains("source=table_cell là bằng chứng yếu", s);      // luật, không hardcode bảng
        Assert.Contains("PHỤ LỤC B – BIỂU MẪU", s);                     // ví dụ one-shot
        Assert.Contains("""{"h":[{"i":0,"r":"h","l":1}""", s);           // lược đồ đầu ra
        Assert.DoesNotContain("{Rules}", s);                            // chỗ nội suy đã thay
        Assert.DoesNotContain("{ExampleInput}", s);
    }
}

public class ChunkerTests
{
    private static IReadOnlyList<XmlLine> MakeLines(int candidates)
    {
        var lines = new List<XmlLine>();
        for (int i = 0; i < candidates; i++)
        {
            lines.Add(new XmlLine($"<p i=\"{i}\" s=\"Heading1\">Tiêu đề số {i} khá dài để chiếm chỗ</p>", i, true));
            lines.Add(new XmlLine("<n c=\"3\"/>", null, false));
        }
        return lines;
    }

    [Fact]
    public void Splits_when_budget_exceeded_and_keeps_every_candidate()
    {
        var lines = MakeLines(40);
        var chunks = SlimXmlChunker.Split(lines, maxTokensPerChunk: 100, overlapCandidates: 2);

        Assert.True(chunks.Count > 1);

        var covered = chunks.SelectMany(c => c.CandidateIndexes).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, 40), covered);
    }

    [Fact]
    public void Consecutive_chunks_overlap_by_requested_candidate_count()
    {
        var chunks = SlimXmlChunker.Split(MakeLines(40), maxTokensPerChunk: 120, overlapCandidates: 2);

        for (int i = 1; i < chunks.Count; i++)
        {
            var shared = chunks[i - 1].CandidateIndexes.Intersect(chunks[i].CandidateIndexes).ToList();
            Assert.NotEmpty(shared);
        }
    }

    [Fact]
    public void Single_small_document_yields_one_chunk()
    {
        var chunks = SlimXmlChunker.Split(MakeLines(3), maxTokensPerChunk: 4000);
        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].CandidateIndexes.Count);
    }

    [Fact]
    public void Candidate_cap_bounds_chunk_size_even_with_huge_token_budget()
    {
        var chunks = SlimXmlChunker.Split(MakeLines(100), maxTokensPerChunk: 100_000,
            overlapCandidates: 0, maxCandidatesPerChunk: 10);

        Assert.All(chunks, c => Assert.True(c.CandidateIndexes.Count <= 10));
        Assert.Equal(Enumerable.Range(0, 100), chunks.SelectMany(c => c.CandidateIndexes).Distinct().Order());
    }
}

public class EnumeratedGrammarTests
{
    [Fact]
    public void Prompt_distinguishes_front_matter_and_document_title_from_heading_tree()
    {
        Assert.Contains("document_title", HeaderPrompt.System);
        Assert.Contains("Chỉ dùng r=h khi đoạn mở một phần nội dung", HeaderPrompt.System);
        // Phân biệt bằng QUAN HỆ (cụm nhãn liền nhau, không có nội dung triển khai bên dưới),
        // không bằng cách gọi tên loại văn bản.
        Assert.Contains("không dòng nào có phần", HeaderPrompt.System);
        foreach (var hardcoded in DomainHardcodedPhrases)
            Assert.DoesNotContain(hardcoded, HeaderPrompt.System);
    }

    /// <summary>
    /// Những cụm từ mô tả riêng văn bản hành chính Việt Nam. Prompt từng liệt kê chúng để chặn
    /// phần đầu công văn thành heading, nhưng liệt kê theo LOẠI văn bản thì chỉ đúng với loại đó
    /// và đo được là bắn quá tay: trên bench, critic loại 3 mục của một tài liệu dùng toàn style
    /// Heading chuẩn — đúng bằng số mục bị thiếu.
    /// </summary>
    private static readonly string[] DomainHardcodedPhrases =
    [
        "nơi nhận", "kính gửi", "dấu mật", "bảo mật/khẩn", "tên cơ quan", "số hiệu", "mã biểu mẫu",
    ];

    [Fact]
    public void Critic_prompt_challenges_weak_model_heading_without_document_phrase_hardcode()
    {
        Assert.Contains("CHỦ ĐỘNG tìm phản ví dụ", HeaderPrompt.CriticSystem);
        // Phép thử là quan hệ phạm vi, không phải danh sách loại văn bản.
        Assert.Contains("MỞ RA phạm vi nội dung", HeaderPrompt.CriticSystem);
        Assert.Contains("từ khóa riêng lẻ", HeaderPrompt.CriticSystem);
        // Và phải có vế KHẲNG ĐỊNH: bản cũ chỉ toàn vế phủ định nên critic thiên về bác bỏ.
        Assert.Contains("thì nó là heading", HeaderPrompt.CriticSystem);
        Assert.DoesNotContain("Đơn vị Alpha", HeaderPrompt.CriticSystem);
        Assert.DoesNotContain("Đơn vị Beta", HeaderPrompt.CriticSystem);
        foreach (var hardcoded in DomainHardcodedPhrases)
            Assert.DoesNotContain(hardcoded, HeaderPrompt.CriticSystem);
    }

    [Fact]
    public void Role_grammar_separates_heading_from_non_heading_roles()
    {
        var gbnf = HeaderPrompt.BuildRoleEnumeratedGbnf([7]);

        Assert.Contains("\\\"r\\\":\\\"h\\\",\\\"l\\\":" , gbnf);
        Assert.Contains("nonheading ::= [dtfscnu]", gbnf);
        Assert.Contains("hlvl ::= [1-9]", gbnf);
    }

    [Fact]
    public void Grammar_pins_every_index_in_order()
    {
        var gbnf = HeaderPrompt.BuildEnumeratedGbnf([0, 7, 42]);

        Assert.Contains("""root ::= "{\"h\":[" it0 "," it1 "," it2 "]}" """.TrimEnd(), gbnf);
        Assert.Contains("""it0 ::= "{\"i\":0,\"l\":" lvl "}" """.TrimEnd(), gbnf);
        Assert.Contains("""it1 ::= "{\"i\":7,\"l\":" lvl "}" """.TrimEnd(), gbnf);
        Assert.Contains("""it2 ::= "{\"i\":42,\"l\":" lvl "}" """.TrimEnd(), gbnf);
        Assert.Contains("lvl ::= [0-9]", gbnf);
    }

    [Fact]
    public void Empty_candidate_list_produces_valid_empty_grammar()
    {
        Assert.Contains("""root ::= "{\"h\":[]}" """.TrimEnd(), HeaderPrompt.BuildEnumeratedGbnf([]));
    }

    [Fact]
    public void Hierarchy_grammar_cannot_emit_zero_level()
    {
        var gbnf = HeaderPrompt.BuildEnumeratedGbnf([3, 9], allowZero: false);
        Assert.Contains("lvl ::= [1-9]", gbnf);
        Assert.DoesNotContain("lvl ::= [0-9]", gbnf);
    }

    [Fact]
    public void Grammar_output_shape_round_trips_through_the_parser()
    {
        // Chuỗi mà grammar cho phép sinh ra phải parse được thành đúng các mục cấp ≥ 1.
        var hs = ModelJson.Parse("""{"h":[{"i":0,"l":1},{"i":7,"l":0},{"i":42,"l":3}]}""");

        Assert.Equal(2, hs.Count);                       // mục l=0 bị loại
        Assert.Equal([0, 42], hs.Select(h => h.Index));
        Assert.Equal([1, 3], hs.Select(h => h.Level));
    }

    [Fact]
    public void Parser_can_preserve_explicit_model_rejection_when_requested()
    {
        var decisions = ModelJson.Parse("""{"h":[{"i":4,"l":0},{"i":5,"l":2}]}""", includeNonHeadings: true);
        Assert.Equal([0, 2], decisions.Select(x => x.Level));
    }

    [Fact]
    public void Parser_preserves_semantic_non_heading_role()
    {
        var decisions = ModelJson.Parse(
            """{"h":[{"i":3,"r":"d","l":0},{"i":4,"r":"h","l":2}]}""",
            includeNonHeadings: true);

        Assert.Equal(SemanticRole.DocumentTitle, decisions[0].Role);
        Assert.Equal(SemanticRole.Heading, decisions[1].Role);
    }
}

public class FullReviewSerializationTests
{
    [Fact]
    public void Full_review_keeps_normal_paragraphs_as_model_questions()
    {
        var doc = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs =
            [
                new SlimParagraph { Index = 0, Text = "Một đoạn không có định dạng", StyleId = "Normal" },
                new SlimParagraph { Index = 1, Text = "Một heading theo style", StyleId = "Heading1", Role = ParagraphRole.StyledHeading },
            ],
        }.Build();

        var lines = SlimXmlSerializer.BuildLines(doc, new ExtractionOptions(), new HashSet<int> { 0, 1 });

        Assert.Equal([0, 1], lines.Where(x => x.IsCandidate).Select(x => x.ParagraphIndex));
        Assert.DoesNotContain(lines, x => x.Text.StartsWith("<n ", StringComparison.Ordinal));
    }

    [Fact]
    public void Neutral_view_preserves_source_identity_without_markdown_heading_bias()
    {
        var doc = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs =
            [
                new SlimParagraph
                {
                    Index = 7,
                    StableId = "body[1]/tbl[0]/tr[3]/tc[0]/p[0]",
                    Text = "3.2. Ngoài dự báo",
                    StyleId = "Normal",
                    Bold = true,
                    TableDepth = 1,
                    Role = ParagraphRole.HeadingCandidate,
                },
            ],
        }.Build();

        var lines = NeutralDocumentViewSerializer.BuildLines(
            doc, new ExtractionOptions(), new HashSet<int> { 7 });
        var view = NeutralDocumentViewSerializer.WrapChunk(lines, 1, 1);

        Assert.Contains("\"i\":7", view);
        Assert.Contains("\"requested\":true", view);
        Assert.Contains("\"stableId\":\"body[1]/tbl[0]/tr[3]/tc[0]/p[0]\"", view);
        Assert.Contains("\"source\":\"table_cell\"", view);
        Assert.Contains("3.2. Ngoài dự báo", view);
        Assert.DoesNotContain("# 3.2. Ngoài dự báo", view);
        Assert.DoesNotContain("<p ", view);
    }
}

public class ModelJsonTests
{
    [Fact]
    public void Parses_clean_response()
    {
        var hs = ModelJson.Parse("""{"headings":[{"i":0,"level":1},{"i":12,"level":2}]}""");
        Assert.Equal(2, hs.Count);
        Assert.Equal(12, hs[1].Index);
        Assert.Equal(2, hs[1].Level);
    }

    [Fact]
    public void Ignores_prose_around_the_json()
    {
        var hs = ModelJson.Parse("""
            Đây là kết quả:
            {"headings":[{"i":3,"level":1}]}
            Hy vọng giúp ích!
            """);
        Assert.Single(hs);
        Assert.Equal(3, hs[0].Index);
    }

    [Fact]
    public void Salvages_pairs_from_malformed_json()
    {
        var hs = ModelJson.Parse("""{"headings":[{"i":1,"level":1},{"i":2,"level":"two"}]}""");
        Assert.Contains(hs, h => h.Index == 1);
    }

    [Fact]
    public void Returns_empty_for_garbage()
    {
        Assert.Empty(ModelJson.Parse("không có gì cả"));
        Assert.Empty(ModelJson.Parse(""));
    }

    [Fact]
    public void Braces_inside_strings_do_not_break_extraction()
    {
        var json = ModelJson.ExtractFirstObject("""{"note":"a } b","headings":[]}""");
        Assert.Equal("""{"note":"a } b","headings":[]}""", json);
    }
}

public class ModelProfileTests
{
    [Fact]
    public void Qwen_profile_uses_8k_context_with_5k_document_budget()
    {
        var options = new LlamaOptions { ModelPath = "Qwen2.5-7B-Instruct-Q4_K_M.gguf" };

        options.ApplyRecommendedModelProfile();

        Assert.Equal(8192u, options.ContextSize);
        Assert.Equal(5000, options.ChunkTokenBudget);
    }

    [Fact]
    public void Qwen_profile_preserves_explicit_non_default_budget()
    {
        var options = new LlamaOptions
        {
            ModelPath = "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            ContextSize = 6144,
            ChunkTokenBudget = 3200,
        };

        options.ApplyRecommendedModelProfile();

        Assert.Equal(6144u, options.ContextSize);
        Assert.Equal(3200, options.ChunkTokenBudget);
    }

    [Fact]
    public void Llama_3_2_profile_raises_invalid_4k_context_to_8k()
    {
        var options = new LlamaOptions { ModelPath = "Llama-3.2-3B-Instruct-Q4_K_M.gguf" };

        options.ApplyRecommendedModelProfile();

        Assert.Equal(8192u, options.ContextSize);
        Assert.Equal(2200, options.ChunkTokenBudget);
        Assert.True(LlamaOptions.RequiredContextSize(
            options.ChunkTokenBudget, options.MaxOutputTokens) <= options.ContextSize);
    }

    [Fact]
    public void Unknown_4k_model_keeps_context_and_fits_chunk_budget()
    {
        var options = new LlamaOptions { ModelPath = "unknown-model.gguf" };

        options.ApplyRecommendedModelProfile();

        Assert.Equal(4096u, options.ContextSize);
        Assert.Equal(1796, options.ChunkTokenBudget);
        Assert.Equal(options.ContextSize,
            LlamaOptions.RequiredContextSize(options.ChunkTokenBudget, options.MaxOutputTokens));
    }
}

public class LevelNormalizationTests
{
    private static HeadingRecord H(int index, int level) =>
        new() { Index = index, Level = level, Text = $"h{index}" };

    [Fact]
    public void Collapses_level_jumps()
    {
        var list = new List<HeadingRecord> { H(0, 1), H(1, 3), H(2, 5), H(3, 1) };
        HeaderExtractionPipeline.NormalizeLevels(list);

        Assert.Equal([1, 2, 3, 1], list.Select(h => h.Level));
    }

    [Fact]
    public void Keeps_already_valid_hierarchy()
    {
        var list = new List<HeadingRecord> { H(0, 1), H(1, 2), H(2, 2), H(3, 3), H(4, 1) };
        HeaderExtractionPipeline.NormalizeLevels(list);

        Assert.Equal([1, 2, 2, 3, 1], list.Select(h => h.Level));
    }

    [Fact]
    public void Document_starting_at_deep_level_is_lifted_to_one()
    {
        var list = new List<HeadingRecord> { H(0, 4), H(1, 5) };
        HeaderExtractionPipeline.NormalizeLevels(list);

        Assert.Equal([1, 2], list.Select(h => h.Level));
    }
}
