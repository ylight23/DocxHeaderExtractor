using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// Projects the PDF parser's existing visual blocks into target facts.  This is geometry-only
/// evidence: it never calls a block a heading and never changes the line aliases that selection
/// binds to.  A target still has exactly one membership decision for its own alias.
/// </summary>
internal static class PdfLayoutBlockEvidence
{
    public const string Version = "PDF_LAYOUT_BLOCK_EVIDENCE_V1";

    public static void Attach(JsonArray facts, AgentSource source)
    {
        var lines = source.Lines;
        var groups = new List<int[]>();
        for (var start = 0; start < lines.Count;)
        {
            var end = start + 1;
            while (end < lines.Count && source.Blocks[end] == source.Blocks[start]) end++;
            groups.Add(Enumerable.Range(start, end - start).ToArray());
            start = end;
        }
        var byAlias = groups.SelectMany((members, index) => members.Select(member => (lines[member].Alias, index)))
            .ToDictionary(x => x.Alias, x => x.index, StringComparer.Ordinal);
        JsonObject Boundary(int index) => new()
        {
            ["firstAlias"] = lines[groups[index][0]].Alias,
            ["lastAlias"] = lines[groups[index][^1]].Alias,
            ["page"] = lines[groups[index][0]].Page,
            ["lineCount"] = groups[index].Length
        };
        foreach (var fact in facts.OfType<JsonObject>())
        {
            var alias = fact["alias"]?.GetValue<string>() ?? throw new InvalidOperationException("BLOCK_EVIDENCE_ALIAS_MISSING");
            var index = byAlias[alias];
            var members = groups[index];
            fact["layoutBlockEvidence"] = new JsonObject
            {
                ["version"] = Version,
                ["origin"] = "pdf-parser-geometry",
                ["semanticLabels"] = "NONE",
                ["memberAliases"] = new JsonArray(members.Select(i => (JsonNode)JsonValue.Create(lines[i].Alias)!).ToArray()),
                ["firstAlias"] = lines[members[0]].Alias,
                ["lastAlias"] = lines[members[^1]].Alias,
                ["page"] = lines[members[0]].Page,
                ["lineCount"] = members.Length,
                ["previousBlock"] = index == 0 ? null : Boundary(index - 1),
                ["nextBlock"] = index + 1 == groups.Count ? null : Boundary(index + 1)
            };
        }
    }
}
