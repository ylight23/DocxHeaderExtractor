using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

public sealed class ScanTextMeasurementsTests
{
    static AgentSourceLine L(int i, string text, int page = 1) => new($"P{page:000}-L{i + 1:000}", i, page, text, null, null);

    [Fact]
    public void A_numbered_line_has_its_literal_token_punctuation_and_preceding_named_number_not_a_parent_claim()
    {
        AgentSourceLine[] lines = [L(0, "Điều 17. Hồ sơ"), L(1, "2. Trường hợp chuyển đổi công ty,"), L(2, "được thực hiện như sau.")];
        var facts = ScanTextMeasurements.Build(lines, _ => "vlm-transcribed")[lines[1].Alias];
        Assert.Equal("TEXT_MEASURED", facts["origin"]!.GetValue<string>());
        Assert.Equal("vlm-transcribed", facts["readingViewId"]!.GetValue<string>());
        Assert.Equal("2.", facts["leadingToken"]!["text"]!.GetValue<string>());
        Assert.Equal("decimal", facts["leadingToken"]!["form"]!.GetValue<string>());
        Assert.Equal(",", facts["endingPunctuation"]!.GetValue<string>());
        Assert.Equal("đ", facts["nextLine"]!["firstLetter"]!["text"]!.GetValue<string>());
        Assert.Equal("lower", facts["nextLine"]!["firstLetter"]!["case"]!.GetValue<string>());
        var preceding = facts["nearestPrecedingNamedNumber"]!;
        Assert.Equal(lines[0].Alias, preceding["alias"]!.GetValue<string>());
        Assert.Equal("Điều #.", preceding["numberedPrefix"]!["key"]!.GetValue<string>());
        Assert.Equal(17, preceding["numberedPrefix"]!["value"]!.GetValue<int>());
        Assert.Equal(0, preceding["linesBetween"]!.GetValue<int>());
        Assert.Null(facts["parent"]);
        Assert.Null(facts["isHeading"]);
        Assert.Null(facts["isBody"]);
        Assert.Null(facts["sentenceComplete"]);
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
        Assert.Null(facts["previousLine"]);
        Assert.Null(facts["nextLine"]);
        Assert.Null(facts["nearestPrecedingNamedNumber"]);
    }

    [Theory]
    [InlineData("Article 17: Scope", "Article #:", 17)]
    [InlineData("Banana 17. Any words", "Banana #.", 17)]
    [InlineData("Chương IV GENERAL", "Chương #", 4)]
    [InlineData("Điều 17 của văn bản khác", "Điều #", 17)]
    public void A_preceding_label_is_found_by_its_form_even_for_arbitrary_words_or_a_cross_reference(string text, string key, int value)
    {
        AgentSourceLine[] lines = [L(0, text), L(1, "85", 2), L(2, "a) hồ sơ", 2)];
        var facts = ScanTextMeasurements.Build(lines, _ => "ocr-original")[lines[2].Alias];
        var preceding = facts["nearestPrecedingNamedNumber"]!;
        Assert.Equal(lines[0].Alias, preceding["alias"]!.GetValue<string>());
        Assert.Equal(1, preceding["page"]!.GetValue<int>());
        Assert.Equal(key, preceding["numberedPrefix"]!["key"]!.GetValue<string>());
        Assert.Equal(value, preceding["numberedPrefix"]!["value"]!.GetValue<int>());
        Assert.Equal(1, preceding["linesBetween"]!.GetValue<int>());
        Assert.Equal(lines[1].Alias, facts["previousLine"]!["alias"]!.GetValue<string>());
    }

    [Fact]
    public void Punctuation_and_case_are_reported_without_guessing_whether_a_sentence_continues()
    {
        AgentSourceLine[] lines = [L(0, "Hồ sơ,\"  "), L(1, "Được thực hiện."), L(2, "123")];
        var facts = ScanTextMeasurements.Build(lines, _ => "ocr-original");
        Assert.Equal(",\"", facts[lines[0].Alias]["endingPunctuation"]!.GetValue<string>());
        Assert.Equal("upper", facts[lines[0].Alias]["nextLine"]!["firstLetter"]!["case"]!.GetValue<string>());
        Assert.Equal(",\"", facts[lines[1].Alias]["previousLine"]!["endingPunctuation"]!.GetValue<string>());
        Assert.Null(facts[lines[1].Alias]["nextLine"]!["firstLetter"]);
        Assert.Equal("", facts[lines[2].Alias]["endingPunctuation"]!.GetValue<string>());
    }

    [Fact]
    public void Long_form_leaders_are_bounded_and_the_count_discloses_what_was_omitted()
    {
        var facts = ScanTextMeasurements.Build([L(0, "Họ tên " + new string('.', 2000))], _ => "ocr-original").Values.Single();
        Assert.Equal("....", facts["endingPunctuation"]!.GetValue<string>());
        Assert.Equal(2000, facts["endingPunctuationTotalChars"]!.GetValue<int>());
    }

    [Fact]
    public void A_label_is_always_strictly_preceding_and_does_not_use_a_future_label()
    {
        AgentSourceLine[] lines = [L(0, "1. Hồ sơ"), L(1, "Điều 18. Đăng ký"), L(2, "1. Thông tin"), L(3, "Điều 19. Xử lý")];
        var facts = ScanTextMeasurements.Build(lines, _ => "ocr-original");
        Assert.Null(facts[lines[0].Alias]["nearestPrecedingNamedNumber"]);
        Assert.Null(facts[lines[1].Alias]["nearestPrecedingNamedNumber"]);
        Assert.Equal(lines[1].Alias, facts[lines[2].Alias]["nearestPrecedingNamedNumber"]!["alias"]!.GetValue<string>());
        Assert.Equal(lines[1].Alias, facts[lines[3].Alias]["nearestPrecedingNamedNumber"]!["alias"]!.GetValue<string>());
    }

    [Fact]
    public void Adding_measurements_keeps_the_scan_system_prompt_and_user_instruction_byte_for_byte()
    {
        Assert.Equal("af5aed029378ec9a2485c0988f1c3b5699a185e563771a97c154c98167342b8a", AgentProtocol.Sha(ScanAgentProtocol.SystemPrompt));
        // The instruction now also says every fact is a measurement to weigh, layout is not function, and reproduced samples or forms are content (2026-10-20).
        Assert.Equal("af68381203672a2304bdd1a4b73afd3ed7553e8f0b65016b97752e20b0103e1e", AgentProtocol.Sha(ScanAgentProtocol.Instruction));
    }
}
