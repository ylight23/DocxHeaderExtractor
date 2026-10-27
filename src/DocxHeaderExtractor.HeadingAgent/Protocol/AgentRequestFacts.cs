using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;

namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Reads the existing facts handlers locally, including their pagination. No provider call and no membership decision.</summary>
internal static class AgentRequestFacts
{
    public static JsonArray Read(IAgentSource source, IReadOnlyList<string> targets)
        => Read(source.Flavor.CreateTools(source), targets);

    internal static JsonArray Read(IAgentTools tools, IReadOnlyList<string> targets)
    {
        var facts = new JsonArray();
        // A page may exceed the facts tool's legal range. Read legal windows locally; never omit facts to make a packet fit.
        foreach (var window in targets.Chunk(DocxHeaderExtractor.HeadingAgent.Tools.AgentTools.MaxFactRange))
        for (var offset = 0; offset < window.Length;)
        {
            var response = tools.Execute($"E0.{facts.Count:00}", "get_line_facts",
                JsonSerializer.Serialize(new { first = window[0], last = window[^1], offset }));
            if (response.Status != "OK") throw new InvalidOperationException("REQUEST_FACTS_READ_FAILED:" + response.Status);
            var result = JsonNode.Parse(response.Content)!["result"]!;
            var page = result["facts"]!.AsArray();
            if (page.Count == 0) throw new InvalidOperationException("REQUEST_FACTS_NO_PROGRESS");
            foreach (var fact in page) facts.Add(fact!.DeepClone());
            offset += page.Count;
            if (!result["hasMore"]!.GetValue<bool>()) break;
            if (result["nextOffset"]!.GetValue<int>() != offset) throw new InvalidOperationException("REQUEST_FACTS_OFFSET_MISMATCH");
        }
        if (!targets.SequenceEqual(facts.Select(f => f!["alias"]!.GetValue<string>()), StringComparer.Ordinal))
            throw new InvalidOperationException("REQUEST_FACTS_TARGET_MISMATCH");
        return facts;
    }
}
