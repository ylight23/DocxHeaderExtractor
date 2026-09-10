using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Minimal model-facing semantic contract: the model chooses a source occurrence,
/// quotes its heading text verbatim, and assigns a role. Numeric coordinates are deliberately
/// absent; the harness remains the only authority that turns text into a source span.</summary>
public static class SemanticTextExactBindingContract
{
    public const string ProtocolVersion = "a99-semantic-text-exact-binding-v1";

    public static readonly string System = $"""
You identify every structurally real document heading or structural label in the supplied source.
A single source occurrence may contain zero, one, or many independent headings. Decide semantic
existence and role yourself from the complete source. Formatting, numbering, and layout are
evidence, not rules. Return only headings you discover in the supplied source.

For every heading, return:
- source: the supplied short source alias, copied exactly
- text: the complete heading text copied verbatim from that source occurrence
- role: one allowed semantic role

The text field is a quotation used for exact deterministic binding. Do not normalize spelling,
punctuation, whitespace, numbering, or case. Do not generate a title. Do not return character
offsets, source IDs, hierarchy, confidence, explanations, or chain-of-thought. Do not return
text that is not an exact substring of the referenced source occurrence. Do not use Gold.

If identical text occurs more than once in the same source occurrence, either provide the 1-based
occurrence ordinal or provide exact leftExactContext/rightExactContext strings. Never guess an
ambiguous occurrence. Return each semantic heading at most once.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static string BuildUser(string packetJson, string route) => $"TASK={ProtocolVersion}\nroute={route}\n{packetJson}";

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        source = new { type = "string", minLength = 1 },
                        text = new { type = "string", minLength = 1 },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "source", "text", "role" },
                },
            },
        },
        required = new[] { "headings" },
    };

    public static SemanticTextResponse Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("semantic-text-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("semantic-text-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        var root = doc.RootElement;
        if (!root.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("semantic-text-response-headings-missing");
        var result = new List<SemanticTextHeading>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()) ||
                !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(text.GetString()) ||
                !item.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || !CeilingSemanticRole.IsAllowed(role.GetString()))
                throw new FormatException("semantic-text-response-heading-schema-invalid");
            int? occurrence = null;
            if (item.TryGetProperty("occurrence", out var occurrenceValue))
            {
                if (!occurrenceValue.TryGetInt32(out var ordinal) || ordinal < 1) throw new FormatException("semantic-text-response-occurrence-invalid");
                occurrence = ordinal;
            }
            var left = item.TryGetProperty("leftExactContext", out var leftValue) && leftValue.ValueKind == JsonValueKind.String ? leftValue.GetString() : null;
            var right = item.TryGetProperty("rightExactContext", out var rightValue) && rightValue.ValueKind == JsonValueKind.String ? rightValue.GetString() : null;
            result.Add(new SemanticTextHeading(source.GetString()!, text.GetString()!, role.GetString()!, occurrence, left, right));
        }
        return new SemanticTextResponse(result);
    }
}

public sealed record SemanticTextResponse(IReadOnlyList<SemanticTextHeading> Headings);

public sealed record SemanticTextHeading(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);

public sealed record SemanticTextSourceAlias(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("rawText")] string RawText);

public enum SemanticTextBindingStatus
{
    SOURCE_ALIAS_RESOLVED,
    EXACT_TEXT_FOUND,
    BOUND,
    TEXT_NOT_FOUND,
    AMBIGUOUS_EXACT_TEXT,
    INVALID_SOURCE_ALIAS,
    EMPTY_TEXT,
    DUPLICATE_PROPOSAL,
}

public sealed record SemanticTextBindingObservation(
    int RawOrdinal,
    SemanticTextHeading Heading,
    SemanticTextBindingStatus Status,
    int ExactTextOccurrenceCount,
    string? Alias,
    string? SourceId,
    int? Start,
    int? End,
    string? FailureReason);

public sealed record SemanticTextBoundHeading(string Alias, string SourceId, int SourceOrdinal, string Text, string Role, int Start, int End);

/// <summary>Exact-only binder. It never normalizes, fuzzily searches, or creates a semantic
/// proposal; it only resolves text already returned by the model inside the selected occurrence.</summary>
public static class SemanticTextExactBinder
{
    public static IReadOnlyList<SemanticTextBoundHeading> Bind(
        IReadOnlyList<SemanticTextHeading> headings,
        IReadOnlyList<SemanticTextSourceAlias> aliases,
        out IReadOnlyList<SemanticTextBindingObservation> observations)
    {
        var byAlias = aliases.ToDictionary(x => x.Alias, StringComparer.Ordinal);
        var result = new List<SemanticTextBoundHeading>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var audit = new List<SemanticTextBindingObservation>();
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            if (string.IsNullOrEmpty(heading.Text))
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.EMPTY_TEXT, 0, null, null, null, null, "EMPTY_TEXT"));
                continue;
            }
            if (!byAlias.TryGetValue(heading.Source, out var alias))
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.INVALID_SOURCE_ALIAS, 0, null, null, null, null, "INVALID_SOURCE_ALIAS"));
                continue;
            }
            var positions = FindExact(alias.RawText, heading.Text);
            if (positions.Count == 0)
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.TEXT_NOT_FOUND, 0, alias.Alias, alias.SourceId, null, null, "TEXT_NOT_FOUND"));
                continue;
            }
            if (positions.Count > 1)
            {
                var selected = SelectDuplicate(alias.RawText, heading, positions);
                if (selected is null)
                {
                    audit.Add(new(i, heading, SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, positions.Count, alias.Alias, alias.SourceId, null, null, "AMBIGUOUS_EXACT_TEXT"));
                    continue;
                }
                positions = [selected.Value];
            }
            var start = positions[0];
            var end = start + heading.Text.Length;
            var key = $"{alias.SourceId}:{start}:{end}";
            if (!seen.Add(key))
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.DUPLICATE_PROPOSAL, positions.Count, alias.Alias, alias.SourceId, start, end, "DUPLICATE_PROPOSAL"));
                continue;
            }
            audit.Add(new(i, heading, SemanticTextBindingStatus.BOUND, positions.Count, alias.Alias, alias.SourceId, start, end, null));
            result.Add(new(alias.Alias, alias.SourceId, alias.SourceOrdinal, heading.Text, heading.Role, start, end));
        }
        observations = audit;
        return result;
    }

    private static List<int> FindExact(string source, string text)
    {
        var result = new List<int>();
        var offset = 0;
        while (offset <= source.Length - text.Length)
        {
            var index = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (index < 0) break;
            result.Add(index);
            offset = index + Math.Max(1, text.Length);
        }
        return result;
    }

    private static int? SelectDuplicate(string source, SemanticTextHeading heading, IReadOnlyList<int> positions)
    {
        if (heading.Occurrence is { } ordinal) return ordinal <= positions.Count ? positions[ordinal - 1] : null;
        if (heading.LeftExactContext is null && heading.RightExactContext is null) return null;
        for (var i = 0; i < positions.Count; i++)
        {
            var start = positions[i];
            var leftOk = heading.LeftExactContext is null || (start >= heading.LeftExactContext.Length && source.Substring(start - heading.LeftExactContext.Length, heading.LeftExactContext.Length) == heading.LeftExactContext);
            var end = start + heading.Text.Length;
            var rightOk = heading.RightExactContext is null || (end + heading.RightExactContext.Length <= source.Length && source.Substring(end, heading.RightExactContext.Length) == heading.RightExactContext);
            if (leftOk && rightOk) return start;
        }
        return null;
    }
}
