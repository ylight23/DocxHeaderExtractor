using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

public sealed class ScanTextMeasurementsTests
{
    static AgentSourceLine L(int i, string text, int page = 1) => new($"P{page:000}-L{i + 1:000}", i, page, text, null, null);

    [Fact]
    public void A_numbered_line_has_its_literal_token_and_its_own_ending_and_no_fact_about_another_line()
    {
        AgentSourceLine[] lines = [L(0, "Điều 17. Hồ sơ"), L(1, "2. Trường hợp chuyển đổi công ty,"), L(2, "được thực hiện như sau.")];
        var facts = ScanTextMeasurements.Build(lines, _ => "vlm-transcribed")[lines[1].Alias];
        Assert.Equal("TEXT_MEASURED", facts["origin"]!.GetValue<string>());
        Assert.Equal("vlm-transcribed", facts["readingViewId"]!.GetValue<string>());
        Assert.Equal("2.", facts["leadingToken"]!["text"]!.GetValue<string>());
        Assert.Equal("decimal", facts["leadingToken"]!["form"]!.GetValue<string>());
        Assert.Equal(",", facts["endingPunctuation"]!.GetValue<string>());
        Assert.Null(facts["previousLine"]);
        Assert.Null(facts["nextLine"]);
        Assert.Null(facts["nearestPrecedingNamedNumber"]);
    }

    [Fact]
    public void A_line_has_the_same_facts_whatever_lines_are_around_it()
    {
        AgentSourceLine[] alone = [L(0, "2. Trường hợp chuyển đổi công ty,")];
        AgentSourceLine[] crowded = [L(0, "Điều 17. Hồ sơ"), L(1, "2. Trường hợp chuyển đổi công ty,"), L(2, "được thực hiện như sau.")];
        var a = ScanTextMeasurements.Build(alone, _ => "vlm-transcribed")[alone[0].Alias];
        var b = ScanTextMeasurements.Build(crowded, _ => "vlm-transcribed")[crowded[1].Alias];
        Assert.True(JsonNode.DeepEquals(a, b));
    }

    [Theory]
    [InlineData("  12. Một dòng", "12.", "decimal", 2, 5)]
    [InlineData("(2) Một dòng", "(2)", "decimal", 0, 3)]
    [InlineData("1.2 Một dòng", "1.2", "decimal", 0, 3)]
    [InlineData("a) Một dòng", "a)", "letters", 0, 2)]
    [InlineData("đ) Một dòng", "đ)", "letters", 0, 2)]
    [InlineData("IV. Một dòng", "IV.", "letters", 0, 3)]
    public void Prefixes_are_literal_UTF16_spans_not_semantic_clause_types(string text, string token, string form, int start, int end)
    {
        var facts = ScanTextMeasurements.Build([L(0, text)], _ => "ocr-original").Values.Single();
        Assert.Equal(token, facts["leadingToken"]!["text"]!.GetValue<string>());
        Assert.Equal(form, facts["leadingToken"]!["form"]!.GetValue<string>());
        Assert.Equal(start, facts["leadingToken"]!["start"]!.GetValue<int>());
        Assert.Equal(end, facts["leadingToken"]!["end"]!.GetValue<int>());
        Assert.Equal(token, text[start..end]);
    }

    [Theory]
    [InlineData("Article 17: Scope", "Article #:", 17)]
    [InlineData("Banana 17. Any words", "Banana #.", 17)]
    [InlineData("Chương IV GENERAL", "Chương #", 4)]
    [InlineData("Điều 17 của văn bản khác", "Điều #", 17)]
    public void A_numbered_prefix_is_read_from_the_line_itself_by_its_form(string text, string key, int value)
    {
        var facts = ScanTextMeasurements.Build([L(0, text)], _ => "ocr-original").Values.Single();
        Assert.Equal(key, facts["numberedPrefix"]!["key"]!.GetValue<string>());
        Assert.Equal(value, facts["numberedPrefix"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void Long_form_leaders_are_bounded_and_the_count_discloses_what_was_omitted()
    {
        var facts = ScanTextMeasurements.Build([L(0, "Họ tên " + new string('.', 2000))], _ => "ocr-original").Values.Single();
        Assert.Equal("....", facts["endingPunctuation"]!.GetValue<string>());
        Assert.Equal(2000, facts["endingPunctuationTotalChars"]!.GetValue<int>());
    }

    [Fact]
    public void Adding_measurements_keeps_the_scan_system_prompt_and_user_instruction_byte_for_byte()
    {
        Assert.Equal("5e166a6325dd6c90dc914ca266b04392b691d1157ab619aeb81735621a751383", AgentProtocol.Sha(ScanAgentProtocol.SystemPrompt));
        // The instruction now also says every fact is a measurement to weigh and none decides on its own; no example, no scope decision (2026-10-20).
        Assert.Equal("fd013d024c87d41eec392ed4cd03d61bb29842dfc56d0000f0050154342d23b3", AgentProtocol.Sha(ScanAgentProtocol.Instruction));
    }
}
