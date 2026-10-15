using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The evidence tools of the DOCX agent over the OOXML record: what each returns, that every result fits its cap, that bad arguments are
/// refused under the call's own id, and that the facts tool is what the first answer waits for. No network.
/// </summary>
public sealed class DocxAgentToolsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-docx-tools-" + Guid.NewGuid().ToString("N"));
    private const string RealDocx = "todo10_8/generated-docx/01_phap_quy/001_Bo_luat_Dan_su_91-2015-QH13.docx";
    private static readonly Lazy<DocxAgentSource> Real = new(() => DocxAgentSource.Load(TestRepository.Path(RealDocx), null, out _)!);

    public DocxAgentToolsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>130 paragraphs, a Heading1 "Part n" at 0, 20, 40, ... 120: the headings share one normalized text ("part #").</summary>
    private DocxAgentSource Long() => DocxAgentSource.Load(DocxTestDocument.CreateLong(Path.Combine(dir, "long.docx"), 130), null, out _)!;

    private static string A(DocxAgentSource s, int i) => s.Lines[i].Alias;

    private static JsonElement Body(AgentToolHost.Result r) => JsonDocument.Parse(r.Content).RootElement;

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void Six_tools_are_declared_with_strict_schemas_and_the_facts_tool_last()
    {
        var defs = DocxAgentTools.Definitions();
        var names = defs.Select(d => d!["function"]!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(DocxAgentTools.Names.Append(DocxAgentTools.LineFacts), names);
        Assert.Equal(6, defs.Count);
        Assert.All(defs, d =>
        {
            var parameters = d!["function"]!["parameters"]!;
            Assert.False(parameters["additionalProperties"]!.GetValue<bool>());
            Assert.All(parameters["required"]!.AsArray(), r => Assert.NotNull(parameters["properties"]![r!.GetValue<string>()]));
        });
        Assert.Equal(DocxAgentTools.DefinitionsSha256(), DocxAgentTools.DefinitionsSha256());
        Assert.NotEqual(AgentTools.DefinitionsSha256(), DocxAgentTools.DefinitionsSha256());
    }

    [Fact]
    public void The_facts_tool_gives_the_facts_of_a_range_and_the_other_occurrences_of_each_text()
    {
        var source = Long();
        var tools = new DocxAgentTools(source);

        var r = tools.Execute("E1.01", DocxAgentTools.LineFacts, Args(new { first = A(source, 0), last = A(source, 5), offset = 0 }));

        Assert.Equal("OK", r.Status);
        var result = Body(r).GetProperty("result");
        Assert.Equal(DocxAgentSource.FactsVersion, result.GetProperty("version").GetString());
        var facts = result.GetProperty("facts").EnumerateArray().ToArray();
        Assert.Equal(6, facts.Length);
        Assert.Equal(A(source, 0), facts[0].GetProperty("alias").GetString());
        Assert.Equal("heading 1", facts[0].GetProperty("style").GetProperty("name").GetString());
        Assert.False(facts[1].GetProperty("place").GetProperty("inTable").GetBoolean());
        // "Part 1" shares its normalized text with the six other headings: the first two are shown, each with the paragraph that follows it.
        var repeated = facts[0].GetProperty("repeatedText");
        Assert.Equal(6, repeated.GetProperty("otherOccurrences").GetInt32());
        var shown = repeated.GetProperty("shown").EnumerateArray().ToArray();
        Assert.Equal(DocxAgentTools.PrefetchPerTarget, shown.Length);
        Assert.Equal(A(source, 20), shown[0].GetProperty("alias").GetString());
        Assert.Equal(A(source, 21), shown[0].GetProperty("followingLine").GetProperty("alias").GetString());
        Assert.False(shown[0].GetProperty("exactTextMatch").GetBoolean());
        // Body paragraphs of equal digit count share their normalized text with each other.
        Assert.True(facts[1].GetProperty("repeatedText").GetProperty("otherOccurrences").GetInt32() > 0);
        // What the result showed can be cited.
        Assert.Contains(A(source, 20), tools.ObservedAliases);
        Assert.Contains(A(source, 21), tools.ObservedAliases);
        Assert.True(tools.FactsWereRead);
    }

    [Fact]
    public void A_long_range_is_cut_at_a_whole_paragraph_and_says_where_to_continue()
    {
        var source = Real.Value;
        var tools = new DocxAgentTools(source);

        var first = tools.Execute("E1.01", DocxAgentTools.LineFacts, Args(new { first = A(source, 0), last = A(source, 399), offset = 0 }));

        Assert.Equal("OK", first.Status);
        Assert.True(first.Content.Length <= AgentToolHost.MaxResultBytes);
        var result = Body(first).GetProperty("result");
        var returned = result.GetProperty("facts").GetArrayLength();
        Assert.InRange(returned, 1, DocxAgentTools.MaxFactLines);
        Assert.Equal(400, result.GetProperty("rangeLines").GetInt32());
        Assert.True(result.GetProperty("hasMore").GetBoolean());
        var next = result.GetProperty("nextOffset").GetInt32();
        Assert.Equal(returned, next);

        var second = tools.Execute("E1.02", DocxAgentTools.LineFacts, Args(new { first = A(source, 0), last = A(source, 399), offset = next }));
        Assert.Equal("OK", second.Status);
        Assert.Equal(A(source, next), Body(second).GetProperty("result").GetProperty("facts")[0].GetProperty("alias").GetString());
    }

    [Fact]
    public void Every_tool_result_on_a_real_document_fits_the_result_cap()
    {
        var source = Real.Value;
        var tools = new DocxAgentTools(source);
        var calls = new List<(string Tool, object Args)>
        {
            (DocxAgentTools.LineFacts, new { first = A(source, 200), last = A(source, 223), offset = 0 }),
            (DocxAgentTools.SourceContext, new { alias = A(source, 500), before = 6, after = 6 }),
            (DocxAgentTools.SourceSpan, new { aliases = Enumerable.Range(300, 8).Select(i => A(source, i)).ToArray() }),
            (DocxAgentTools.SearchText, new { query = "điều", mode = "normalized", offset = 0, limit = 50 }),
            (DocxAgentTools.Repeated, new { alias = A(source, 10), maxResults = 20 }),
            (DocxAgentTools.Compare, new { first = A(source, 500), second = A(source, 900), window = 3 }),
        };

        var n = 0;
        foreach (var (tool, args) in calls)
        {
            var r = tools.Execute("E1." + (++n).ToString("00"), tool, Args(args));
            Assert.True(r.Status == "OK", tool + ": " + r.Status + " " + r.ErrorCode);
            Assert.True(r.Content.Length <= AgentToolHost.MaxResultBytes, tool + " " + r.Content.Length);
        }
    }

    [Fact]
    public void Context_search_repeats_and_compare_read_the_neighbourhood_the_text_and_the_style_difference()
    {
        var source = Long();
        var tools = new DocxAgentTools(source);

        var context = Body(tools.Execute("E1.01", DocxAgentTools.SourceContext, Args(new { alias = A(source, 21), before = 1, after = 2 }))).GetProperty("result");
        var lines = context.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(new[] { -1, 0, 1, 2 }, lines.Select(l => l.GetProperty("relative").GetInt32()));
        Assert.Equal(A(source, 20), lines[0].GetProperty("alias").GetString());
        Assert.Equal("heading 1", lines[0].GetProperty("facts").GetProperty("styleName").GetString());

        var search = Body(tools.Execute("E1.02", DocxAgentTools.SearchText, Args(new { query = "Body paragraph 7 ", mode = "literal", offset = 0, limit = 5 }))).GetProperty("result");
        Assert.Equal(1, search.GetProperty("total").GetInt32());
        Assert.Equal(A(source, 7), search.GetProperty("matches")[0].GetProperty("alias").GetString());

        var repeats = Body(tools.Execute("E1.03", DocxAgentTools.Repeated, Args(new { alias = A(source, 40), maxResults = 3 }))).GetProperty("result");
        Assert.Equal(6, repeats.GetProperty("totalMatches").GetInt32());
        Assert.Equal(3, repeats.GetProperty("matches").GetArrayLength());

        var compare = Body(tools.Execute("E1.04", DocxAgentTools.Compare, Args(new { first = A(source, 0), second = A(source, 1), window = 0 }))).GetProperty("result").GetProperty("delta");
        Assert.False(compare.GetProperty("sameStyle").GetBoolean());
        Assert.True(compare.GetProperty("sizeDeltaPt").GetDouble() < 0);
        Assert.Equal(1, compare.GetProperty("readingOrderDistance").GetInt32());
        var twins = Body(tools.Execute("E1.05", DocxAgentTools.Compare, Args(new { first = A(source, 0), second = A(source, 20), window = 0 }))).GetProperty("result").GetProperty("delta");
        Assert.True(twins.GetProperty("sameStyle").GetBoolean());
        Assert.True(twins.GetProperty("sameNormalizedText").GetBoolean());
        Assert.False(twins.GetProperty("exactTextMatch").GetBoolean());
    }

    [Fact]
    public void Bad_calls_are_refused_under_their_own_id_and_a_repeat_is_answered_from_the_cache()
    {
        var source = Long();
        var tools = new DocxAgentTools(source);

        var unknownTool = tools.Execute("E1.01", "get_page_layout", "{}");
        Assert.Equal("UNKNOWN_TOOL", unknownTool.Status);
        Assert.Equal("UNREGISTERED", unknownTool.Tool);   // a name the model made up is never echoed

        Assert.Equal("ALIAS_NOT_IN_DOCUMENT", tools.Execute("E1.02", DocxAgentTools.SourceContext, Args(new { alias = "NOPE", before = 1, after = 1 })).ErrorCode);
        Assert.Equal("ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE", tools.Execute("E1.03", DocxAgentTools.SourceContext, Args(new { alias = A(source, 3), before = 1, after = 1, extra = 1 })).ErrorCode);
        Assert.Equal("ARGUMENT_OUT_OF_RANGE", tools.Execute("E1.04", DocxAgentTools.SourceContext, Args(new { alias = A(source, 3), before = 9, after = 1 })).ErrorCode);
        Assert.Equal("RANGE_INVALID", tools.Execute("E1.05", DocxAgentTools.LineFacts, Args(new { first = A(source, 9), last = A(source, 3), offset = 0 })).ErrorCode);
        Assert.Equal("ARGUMENTS_TOO_LARGE", tools.Execute("E1.06", DocxAgentTools.SearchText, "{\"query\":\"" + new string('x', 3000) + "\"}").ErrorCode);
        Assert.Equal("COMPARE_SAME_LINE", tools.Execute("E1.07", DocxAgentTools.Compare, Args(new { first = A(source, 3), second = A(source, 3), window = 0 })).ErrorCode);
        Assert.False(tools.FactsWereRead);

        var args = Args(new { alias = A(source, 21), before = 1, after = 1 });
        var again = Args(new { after = 1, before = 1, alias = A(source, 21) });   // the same call, keys in another order
        var first = tools.Execute("E2.01", DocxAgentTools.SourceContext, args);
        var repeat = tools.Execute("E3.01", DocxAgentTools.SourceContext, again);
        Assert.Equal("E2.01", Body(repeat).GetProperty("cache").GetProperty("sameResultAs").GetString());
        Assert.Equal(Body(first).GetProperty("result").ToString(), Body(repeat).GetProperty("result").ToString());
        Assert.Equal(1, tools.CacheHits);

        var rejected = tools.Reject("E4.01", DocxAgentTools.Compare, "{}", "PER_TURN_CALL_CAP_EXCEEDED");
        Assert.Equal("REJECTED", rejected.Status);
        Assert.Equal(rejected.Content.Length, tools.RejectionBytes("E4.01", DocxAgentTools.Compare) - ("UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED".Length - "PER_TURN_CALL_CAP_EXCEEDED".Length));
        Assert.Equal(DocxAgentTools.Names, tools.ReservedNames);
    }

    [Fact]
    public void The_style_table_is_the_documents_and_a_copy()
    {
        var source = Long();
        var tools = new DocxAgentTools(source);
        var styles = tools.DocumentStyles();

        Assert.Equal(130, styles["documentLines"]!.GetValue<int>());
        Assert.Equal("heading 1", styles["styles"]![0]!["styleName"]!.GetValue<string>());
        Assert.Equal(7, styles["styles"]![0]!["lines"]!.GetValue<int>());
        // A fresh copy each time: the caller may put it in a request without taking the source's node.
        Assert.NotSame(styles, tools.DocumentStyles());
    }
}
