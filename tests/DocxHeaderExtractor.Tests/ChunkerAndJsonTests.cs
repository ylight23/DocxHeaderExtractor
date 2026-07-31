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

        Assert.Contains("tbl=1 (nằm trong bảng) LUÔN l=0", s);          // luật
        Assert.Contains("PHỤ LỤC B – BIỂU MẪU", s);                     // ví dụ one-shot
        Assert.Contains("""{"h":[{"i":0,"l":1}""", s);                   // lược đồ đầu ra
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
    public void Grammar_output_shape_round_trips_through_the_parser()
    {
        // Chuỗi mà grammar cho phép sinh ra phải parse được thành đúng các mục cấp ≥ 1.
        var hs = ModelJson.Parse("""{"h":[{"i":0,"l":1},{"i":7,"l":0},{"i":42,"l":3}]}""");

        Assert.Equal(2, hs.Count);                       // mục l=0 bị loại
        Assert.Equal([0, 42], hs.Select(h => h.Index));
        Assert.Equal([1, 3], hs.Select(h => h.Level));
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
