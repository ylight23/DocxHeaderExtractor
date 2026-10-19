using System.Text.Json.Nodes;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

internal static class GroundedVerifierFixture
{
    internal static string[][] Different(string[][] groups) => groups.Any(g => g.Length > 1)
        ? groups.SelectMany(g => g.Select(a => new[] { a })).ToArray()
        : groups.SelectMany(g => g).Chunk(8).ToArray();
    internal static string Reply(string json, JsonNode packet)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var verdict in root["verdicts"]!.AsArray())
        {
            if (verdict!["decision"]?.GetValue<string>() != "REGROUP") continue;
            var candidate = packet["candidates"]!.AsArray().Single(c => c!["run"]!.GetValue<string>() == verdict["run"]!.GetValue<string>())!;
            var groups = candidate["proposedGroups"]!.Deserialize<string[][]>()!;
            verdict["currentGroups"] = JsonSerializer.SerializeToNode(groups);
            verdict["suggestedGroups"] = JsonSerializer.SerializeToNode(Different(groups));
        }
        return root.ToJsonString();
    }
}
