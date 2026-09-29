using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A reveal pin is written once, before a held-out reveal, and is history from then on. Re-verifying it must
/// check what has to stay true for that document - its Gold, its registry entry, the source, the engine, the
/// contract, the scorer, the harness, the proposals, the computed checkpoint - and must not fail because the
/// corpus moved on: the registry file as a whole changes whenever a later document's Gold is added.
/// <para>
/// So: with no pin on disk, or with A99_FREEZE_UPDATE=1, the pin is written as <see cref="FreezeArtifact"/>
/// writes any artifact. With a pin on disk it is compared field by field, except the named historical-only
/// fields, which record the corpus state at the reveal and are never rewritten.
/// </para>
/// </summary>
internal static class RevealPin
{
    internal static void Verify(string directory, string name, object pin, params string[] historicalOnly)
    {
        var path = TestRepository.Path($"{directory}/{name}");
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("A99_FREEZE_UPDATE") == "1")
        {
            FreezeArtifact.AssertJson(directory, name, pin);
            return;
        }

        var expected = JsonNode.Parse(JsonSerializer.Serialize(pin, FreezeArtifact.Json))!;
        var committed = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var field in historicalOnly)
        {
            Remove(expected, field);
            Remove(committed, field);
        }
        var changed = new List<string>();
        Diff(expected, committed, "", changed);
        Assert.True(changed.Count == 0,
            $"{name}: pinned facts changed since the reveal: {string.Join(", ", changed)}");
    }

    private static void Diff(JsonNode? a, JsonNode? b, string path, List<string> changed)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
                Diff(oa[key], ob[key], path.Length == 0 ? key : $"{path}.{key}", changed);
            return;
        }
        if (!JsonNode.DeepEquals(a, b)) changed.Add(path);
    }

    private static void Remove(JsonNode root, string dottedPath)
    {
        var parts = dottedPath.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
            node = node?[part];
        (node as JsonObject)?.Remove(parts[^1]);
    }
}
