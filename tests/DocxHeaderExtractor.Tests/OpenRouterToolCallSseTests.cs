using System.Text;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free tests for the additive tool-call capture in
/// <see cref="OpenRouterHeaderExtractor.SseReassembly"/> - the same internal SSE reassembly
/// <see cref="OpenRouterTests"/> already exercises indirectly for the json_object path, now checked
/// directly for <c>delta.tool_calls[]</c> across multiple simulated chunks. No network call.
/// </summary>
public sealed class OpenRouterToolCallSseTests
{
    [Fact]
    public void Tool_call_fragments_split_across_chunks_are_all_captured_in_order()
    {
        var stream = new OpenRouterHeaderExtractor.SseReassembly();

        Feed(stream, """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"submit_semantic_claims","arguments":""}}]}}]}""");
        Feed(stream, """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"claims\":"}}]}}]}""");
        Feed(stream, """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"[]}"}}]}}]}""");
        Feed(stream, """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");
        Feed(stream, "data: [DONE]", final: true);

        Assert.True(stream.TransportComplete);
        Assert.Equal("tool_calls", stream.FinishReason);
        Assert.Equal(string.Empty, stream.Content);
        Assert.Equal(3, stream.ToolCallFragments.Count);
        Assert.All(stream.ToolCallFragments, f => Assert.Equal(0, f.Index));
        Assert.Equal("call_1", stream.ToolCallFragments[0].Id);
        Assert.Equal("submit_semantic_claims", stream.ToolCallFragments[0].FunctionName);
        var reassembled = DocxHeaderExtractor.Core.V5.V5ToolCallArgumentsReassembler.Reassemble(stream.ToolCallFragments);
        Assert.Equal(1, reassembled.Count);
        Assert.Equal("""{"claims":[]}""", reassembled[0].Arguments);
        Assert.Equal("submit_semantic_claims", reassembled[0].FunctionName);
    }

    [Fact]
    public void A_json_object_style_content_only_stream_never_produces_tool_call_fragments()
    {
        var stream = new OpenRouterHeaderExtractor.SseReassembly();
        Feed(stream, """data: {"choices":[{"delta":{"content":"{\"claims\":[]}"}}]}""");
        Feed(stream, """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""");
        Feed(stream, "data: [DONE]", final: true);

        Assert.True(stream.TransportComplete);
        Assert.Equal("""{"claims":[]}""", stream.Content);
        Assert.Empty(stream.ToolCallFragments);
    }

    private static void Feed(OpenRouterHeaderExtractor.SseReassembly stream, string line, bool final = false)
    {
        var pending = new StringBuilder(line + "\n\n");
        stream.Feed(pending, final);
    }
}
