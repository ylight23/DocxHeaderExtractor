using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Compact hierarchy inventory item. The model sees only a local index and text/role;
/// the harness keeps the canonical proposalId (documentId+sourceId+span) mapping privately.</summary>
public sealed record CeilingHierarchyItem(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("role")] string Role);

public sealed record CeilingHierarchyEdge(
    [property: JsonPropertyName("child")] int Child,
    [property: JsonPropertyName("parent")] int? Parent);

public sealed record CeilingHierarchyResponse(IReadOnlyList<CeilingHierarchyEdge> Parents);

/// <summary>Maps a local hierarchy inventory index back to the canonical global proposalId used
/// by <see cref="ReasoningGlobalHierarchyPass"/>. Array order preserves document order.</summary>
public sealed record CeilingHierarchyBinding(int LocalIndex, string ProposalId);

public static class CeilingHierarchyPacketBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static (string Json, IReadOnlyList<CeilingHierarchyBinding> Bindings) Build(
        IReadOnlyList<ReasoningProposalInventoryItem> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var items = new List<CeilingHierarchyItem>(inventory.Count);
        var bindings = new List<CeilingHierarchyBinding>(inventory.Count);
        for (var i = 0; i < inventory.Count; i++)
        {
            items.Add(new CeilingHierarchyItem(i, inventory[i].ExactText, inventory[i].SemanticRole));
            bindings.Add(new CeilingHierarchyBinding(i, inventory[i].ProposalId));
        }
        var json = JsonSerializer.Serialize(new { headings = items }, JsonOptions);
        return (json, bindings);
    }
}

/// <summary>Compact hierarchy prompt (v2): local integer ids only, no repeated document/source
/// identity, no level field -- level is derived locally after hard graph validation.</summary>
public static class CeilingHierarchyPrompt
{
    public const string ProtocolVersion = "a99-ceiling-hierarchy-v2";

    public const string System = """
You are a document hierarchy resolver. The input is a compact, ordered inventory of headings
already extracted from one document: {"headings":[{"i":0,"text":"...","role":"CHAPTER"}]}.
Array order is document order. For every heading, identify its immediate structural parent among
the other headings in the inventory, using semantic organization (role, numbering evident in the
text, and document order). A heading with no parent (e.g. the document title, or a top-level part)
has parent null. Return exactly: {"parents":[{"child":0,"parent":null},{"child":1,"parent":0}]}.
"child" and "parent" are inventory indexes ("i" values) only, never text or source identities.
Every heading must appear exactly once as a "child". A heading has exactly one parent or null;
never invent a parent outside the inventory, never make a heading its own parent, never create a
cycle. Do not return a level field; level is derived after validation.
""";

    public static string BuildUser(string packetJson) =>
        $"TASK={ProtocolVersion}\n{packetJson}\n" +
        "Return {\"parents\":[{\"child\":<i>,\"parent\":<i>|null}, ...]} covering every heading exactly once.";

    public static object Schema(int inventoryCount) => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            parents = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        child = new { type = "integer", minimum = 0, maximum = Math.Max(0, inventoryCount - 1) },
                        parent = new { type = new[] { "integer", "null" }, minimum = 0, maximum = Math.Max(0, inventoryCount - 1) },
                    },
                    required = new[] { "child", "parent" },
                },
            },
        },
        required = new[] { "parents" },
    };
}

public static class CeilingHierarchyResponseParser
{
    public static CeilingHierarchyResponse Parse(string raw, int inventoryCount)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("ceiling-hierarchy-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("ceiling-hierarchy-response-json-incomplete");
        using var document = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!document.RootElement.TryGetProperty("parents", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("ceiling-hierarchy-response-parents-missing");

        var edges = new List<CeilingHierarchyEdge>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("child", out var childValue) || !childValue.TryGetInt32(out var child) ||
                child < 0 || child >= inventoryCount ||
                !item.TryGetProperty("parent", out var parentValue) ||
                !(parentValue.ValueKind == JsonValueKind.Null || (parentValue.TryGetInt32(out var parsedParent) && parsedParent >= 0 && parsedParent < inventoryCount)))
                throw new FormatException("ceiling-hierarchy-response-edge-invalid");
            edges.Add(new CeilingHierarchyEdge(child, parentValue.ValueKind == JsonValueKind.Null ? null : parentValue.GetInt32()));
        }
        return new CeilingHierarchyResponse(edges);
    }
}
