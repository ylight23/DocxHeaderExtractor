using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Tools;
using Xunit.Abstractions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The numbered sequences of a document, from the form of its lines only: the key is the words before the first number, # and the punctuation
/// after it; a sequence reports where its numbers skip, repeat or go down. No line is called a heading.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ShapeSequenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Điều 4. Thẩm quyền", "Điều #.", 4, "decimal")]
    [InlineData("Chương IV QUY ĐỊNH CHUNG", "Chương #", 4, "roman")]
    [InlineData("2. Việc cấp bản sao", "#.", 2, "decimal")]
    [InlineData("Article 17: Scope", "Article #:", 17, "decimal")]
    [InlineData("Mục 2 Hồ sơ", "Mục #", 2, "decimal")]
    [InlineData("IV. NỘI DUNG", "#.", 4, "roman")]
    public void A_line_with_a_number_among_its_first_words_has_a_key_and_a_value(string text, string key, int value, string form)
    {
        Assert.Equal((key, value, form), ShapeSequence.KeyOf(text));
    }

    [Theory]
    [InlineData("I think this is body text")]
    [InlineData("IIII. not a canonical number")]
    [InlineData("The scope of this law covers 12 areas")]
    [InlineData("a) Chứng thực bản sao")]
    [InlineData("")]
    public void A_line_without_a_number_among_its_first_words_has_no_key(string text)
    {
        Assert.Null(ShapeSequence.KeyOf(text));
    }

    static AgentSourceLine L(int i, string text) => new($"S{i:0000}", i, null, text, null, null);

    [Fact]
    public void A_sequence_says_where_its_numbers_skip_repeat_and_go_down()
    {
        string[] texts = ["Điều 1. A", "body", "Điều 2. B", "Điều 3. C", "Điều 4. D", "Điều 5. E", "Điều 6. F", "Điều 8. H", "Điều 8. H again", "Điều 5. see above"];
        var lines = texts.Select((t, i) => L(i, t)).ToArray();

        var s = ShapeSequence.Describe(lines, 7)!;

        Assert.Equal("Điều #.", s["key"]!.GetValue<string>());
        Assert.Equal(9, s["linesWithThisKey"]!.GetValue<int>());
        Assert.Equal(7, s["placeInSequence"]!.GetValue<int>());
        var gap = s["gaps"]!.AsArray().Single()!;
        Assert.Equal((6, 8, 1), (gap["after"]!.GetValue<int>(), gap["before"]!.GetValue<int>(), gap["missingCount"]!.GetValue<int>()));
        Assert.Equal("S0006", gap["afterAlias"]!.GetValue<string>());
        Assert.Equal([5, 8], s["repeatedValues"]!.AsArray().Select(r => r!["value"]!.GetValue<int>()).Order());
        var down = s["decreases"]!.AsArray().Single()!;
        Assert.Equal((8, 5), (down["from"]!.GetValue<int>(), down["to"]!.GetValue<int>()));
        Assert.Contains(s["window"]!.AsArray(), e => e!["alias"]!.GetValue<string>() == "S0007");
        Assert.Null(ShapeSequence.Describe(lines, 1));
    }

    [Theory]
    [InlineData("- Điều 28. Thực hiện công việc", "Điều #.", 28)]
    [InlineData("• 3. Item", "#.", 3)]
    public void A_symbol_in_front_of_a_label_is_not_a_word_of_the_key(string text, string key, int value)
    {
        Assert.Equal((key, value), ShapeSequence.KeyOf(text) is { } k ? (k.Key, k.Value) : default);
    }

    [Fact]
    public void A_gap_lists_the_lines_between_its_neighbours_that_carry_exactly_a_missing_number_under_another_key()
    {
        string[] texts = ["Điều 39. A", "body", "Diéu 40. Nghia vu", "Điều 40 của Luật này (a cross-reference elsewhere)", "Điều 41. B", "Điều 42. C", "Điều 9, far away"];
        var lines = texts.Select((t, i) => L(i, t)).ToArray();

        var gap = ShapeSequence.Describe(lines, 0)!["gaps"]!.AsArray().Single()!;

        Assert.Equal((39, 41), (gap["after"]!.GetValue<int>(), gap["before"]!.GetValue<int>()));
        var candidates = gap["missingNumberUnderAnotherKey"]!.AsArray();
        Assert.Equal(["S0002", "S0003"], candidates.Select(c => c!["alias"]!.GetValue<string>()));
        Assert.Equal(["Diéu #.", "Điều #"], candidates.Select(c => c!["key"]!.GetValue<string>()));
        Assert.All(candidates, c => Assert.Equal(40, c!["value"]!.GetValue<int>()));
    }

    [Fact]
    public void The_docx_tool_returns_the_sequence_as_an_observation_and_the_aliases_it_showed_can_be_cited()
    {
        var path = DocxTestDocument.CreateLong(Path.Combine(Path.GetTempPath(), "dhx-seq-" + Guid.NewGuid().ToString("N") + ".docx"), 60);
        var source = DocxAgentSource.Load(path, null, out _)!;
        var tools = new DocxAgentTools(source);
        var withKey = source.Lines.First(l => ShapeSequence.KeyOf(l.Text) is not null);

        var r = tools.Execute("E1.01", DocxAgentTools.Sequence, JsonSerializer.Serialize(new { alias = withKey.Alias }));

        Assert.Equal("OK", r.Status);
        var result = JsonDocument.Parse(r.Content).RootElement.GetProperty("result");
        Assert.Equal(ShapeSequence.Version, result.GetProperty("version").GetString());
        Assert.Equal(withKey.Alias, result.GetProperty("sequence").GetProperty("alias").GetString());
        Assert.Contains(withKey.Alias, tools.ObservedAliases);
        Assert.Contains("\"semanticLabels\":\"NONE\"", System.Text.Encoding.UTF8.GetString(r.Content), StringComparison.Ordinal);
        Assert.Contains(DocxAgentTools.Definitions(), d => d!["function"]!["name"]!.GetValue<string>() == DocxAgentTools.Sequence);
    }

    /// <summary>A real statute, when the corpus is in the repository: the article sequence and what it reports.</summary>
    [Fact]
    public void The_article_sequence_of_a_real_statute_is_read_and_its_anomalies_reported()
    {
        var path = TestRepository.Path("todo10_8/generated-docx/01_phap_quy/001_Bo_luat_Dan_su_91-2015-QH13.docx");
        if (!File.Exists(path)) return;
        var source = DocxAgentSource.Load(path, null, out _)!;
        var first = source.Lines.Select((l, i) => (l, i)).First(x => ShapeSequence.KeyOf(x.l.Text)?.Key == "Điều #.").i;

        var s = ShapeSequence.Describe(source.Lines, first)!;

        output.WriteLine(s.ToJsonString()[..Math.Min(1500, s.ToJsonString().Length)]);
        Assert.True(s["linesWithThisKey"]!.GetValue<int>() >= 600);
        Assert.Equal(1, s["firstValue"]!.GetValue<int>());
    }
}
