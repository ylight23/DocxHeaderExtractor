using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF prompt's fact passages (UseLine, FactsToolLine) name only fields that the request or a get_line_facts result really carries,
/// and the pagination they describe is the one the tool does. Checked against a real PDF source, not against a copy of the text.
/// </summary>
public sealed class PromptContractTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private static readonly Regex CamelField = new(@"\b[a-z]+(?:[A-Z][A-Za-z]*)+\b", RegexOptions.CultureInvariant);

    private static readonly Lazy<AgentSource> Source = new(() =>
        AgentSource.Load(TestRepository.Path(Pdf), out var failure) ?? throw new InvalidOperationException(failure));

    [Fact]
    public void The_prompt_does_not_refer_to_observations_the_request_does_not_carry()
    {
        var prompt = AgentProtocol.SystemPrompt;

        Assert.DoesNotContain("observations.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutFacts", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("carries no observations", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_field_named_in_the_fact_passages_is_carried_by_the_request_or_by_a_tool_result()
    {
        var source = Source.Value;
        var styles = new AgentTools(source).DocumentStyles();
        var (decide, shown) = source.Scope(0);
        var request = JsonNode.Parse(AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(0), decide,
            shown.Select(i => source.Lines[i]).ToArray(), styles, targetFacts: source.Flavor.PayloadFacts(source, decide)))!.AsObject();

        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in request.Select(p => p.Key)) carried.Add(key);

        // repeatedText is present only on a fact whose text occurs elsewhere, so read a whole range, page by page.
        var last = Math.Min(AgentTools.MaxFactRange - 1, source.Lines.Count - 1);
        for (int offset = 0; ; )
        {
            var page = JsonNode.Parse(FactsResult(source, 0, last, offset))!;
            CollectPropertyNames(page, carried);
            var next = page["result"]!["nextOffset"];
            if (next is null) break;
            offset = next.GetValue<int>();
        }

        // evidenceRefs is a key of the reply the prompt defines, not of a tool result.
        Assert.Contains("\"evidenceRefs\":", AgentProtocol.SystemPrompt, StringComparison.Ordinal);
        carried.Add("evidenceRefs");

        var named = CamelField.Matches(AgentProtocol.UseLine + " " + AgentProtocol.FactsToolLine)
            .Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Contains("repeatedText", named);
        Assert.Contains("hasMore", named);
        Assert.Contains("nextOffset", named);
        Assert.Equal(Array.Empty<string>(), named.Where(n => !carried.Contains(n)).ToArray());
    }

    [Fact]
    public void The_pagination_the_prompt_describes_is_the_one_the_tool_returns()
    {
        var source = Source.Value;
        var last = Math.Min(99, source.Lines.Count - 1);

        var first = JsonNode.Parse(FactsResult(source, 0, last, offset: 0))!["result"]!.AsObject();
        var facts = first["facts"]!.AsArray();
        var nextOffset = first["nextOffset"]!.GetValue<int?>();

        // A range of more than the 24 lines one call covers comes back cut, and says where it stopped.
        Assert.True(first["hasMore"]!.GetValue<bool>());
        Assert.Equal(facts.Count, nextOffset);
        Assert.True(facts.Count <= AgentTools.MaxFactLines);

        // Calling again at nextOffset reads the next line, as the prompt says.
        var second = JsonNode.Parse(FactsResult(source, 0, last, offset: nextOffset!.Value))!["result"]!.AsObject();
        Assert.Equal(source.Lines[nextOffset.Value].Alias, second["facts"]!.AsArray()[0]!["alias"]!.GetValue<string>());
    }

    [Fact]
    public void A_complete_result_says_hasMore_false()
    {
        var source = Source.Value;
        var result = JsonNode.Parse(FactsResult(source, 0, 3, offset: 0))!["result"]!.AsObject();

        Assert.False(result["hasMore"]!.GetValue<bool>());
        Assert.Null(result["nextOffset"]);
        Assert.Equal(4, result["facts"]!.AsArray().Count);
    }

    private static string FactsResult(AgentSource source, int firstIndex, int lastIndex, int offset)
    {
        var args = JsonSerializer.Serialize(new { first = source.Lines[firstIndex].Alias, last = source.Lines[lastIndex].Alias, offset });
        var tools = new AgentTools(source);
        return Encoding.UTF8.GetString(tools.Execute("E1.01", AgentTools.LineFacts, args).Content);
    }

    private static void CollectPropertyNames(JsonNode? node, ISet<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    into.Add(key);
                    CollectPropertyNames(child, into);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr) CollectPropertyNames(child, into);
                break;
        }
    }
}
