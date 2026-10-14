using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 Gate T0 (offline): the usage audit reads simulated responses only. No call is made.</summary>
public sealed class P7F1V6UsageD32Tests
{
    const string Dir = "artifacts/web-pdf-semantic-diagnostic/p7.f1.v6.d3.2-usage-fixtures";

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(TestRepository.Path(Dir), "*.json").OrderBy(x => x, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Theory] [MemberData(nameof(Fixtures))]
    public void Each_simulated_response_gets_its_expected_audit(string file)
    {
        var fx = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Dir + "/" + file))).RootElement;
        var requestBytes = fx.GetProperty("requestBytes").GetInt32();
        var maxTokens = fx.GetProperty("maxTokens").GetInt32();
        var expected = fx.GetProperty("expectCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var audit = P7F1V6Usage.Check(fx.GetProperty("response").GetRawText(), requestBytes, maxTokens);
        if (expected.Length == 1 && expected[0] == "OK")
            Assert.Equal(new[] { "OK" }, audit.Codes);
        else
            foreach (var code in expected) Assert.Contains(code, audit.Codes);
    }

    [Fact] public void A_response_that_is_not_json_is_reported_not_guessed()
    {
        var audit = P7F1V6Usage.Check("not json", 1000, 32768);
        Assert.Equal(new[] { "RESPONSE_NOT_JSON" }, audit.Codes);
        Assert.Null(audit.ComputedCostUsd);
        Assert.Null(audit.ProviderCostUsd);
    }

    [Fact] public void An_absent_reasoning_field_stays_null_and_is_not_assumed_to_be_zero()
    {
        var fx = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Dir + "/reasoning_details_absent.json"))).RootElement;
        var audit = P7F1V6Usage.Check(fx.GetProperty("response").GetRawText(), 20000, 32768);
        Assert.Null(audit.ReasoningTokens);
        Assert.Null(audit.VisibleTokens);
    }

    [Fact] public void The_cost_of_a_request_uses_the_tier_of_its_prompt_tokens()
    {
        Assert.Equal(2000 * 0.00000003 + 300 * 0.00000013, P7F1V6Usage.Cost(2000, 300)!.Value, 12);
        Assert.Equal(40000 * 0.0000001 + 300 * 0.0000004, P7F1V6Usage.Cost(40000, 300)!.Value, 12);
        Assert.Equal(300000 * 0.0000002 + 300 * 0.0000008, P7F1V6Usage.Cost(300000, 300)!.Value, 12);
    }
}
