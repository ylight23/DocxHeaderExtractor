using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Successor model contract for the ALL TRUE HEADING OCCURRENCES target. Heading truth
/// is an explicit model decision and is never inferred from the semantic role.</summary>
public static class SemanticTextTrueHeadingContract
{
    public const string ProtocolVersion = "a99-semantic-true-heading-v2";

    public static readonly string System = $"""
You identify semantic structural items in the supplied source and independently decide whether each
item is a TRUE document heading. headingDecision is the authority for heading status.

TRUE_HEADING means a real heading occurrence in the document structure.
NOT_TRUE_HEADING means a structural-looking item that is not itself a true heading, including as
applicable TOC entries, captions, table labels, ordinary list items, form labels, structural labels,
or other non-heading items.

Formatting, numbering, typography and layout are evidence, never deterministic rules. Use complete
document context. Do not use Gold. A semantic role does not imply headingDecision.

For every discovered structural item, return:
- source: the supplied short source alias, copied exactly
- text: the complete item text copied verbatim from that source occurrence
- headingDecision: TRUE_HEADING or NOT_TRUE_HEADING
- role: one allowed semantic role

The text field is used for exact deterministic binding. Do not normalize spelling, punctuation,
whitespace, numbering, or case. Do not generate titles. Do not return character offsets, source IDs,
hierarchy, confidence, explanations, or chain-of-thought. Do not return text that is not an exact
substring of the referenced source occurrence.

If identical text occurs more than once in one source occurrence, provide the 1-based occurrence
ordinal or exact leftExactContext/rightExactContext strings. Never guess an ambiguous occurrence.
Return each semantic item at most once.

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
                        headingDecision = new { type = "string", @enum = new[] { "TRUE_HEADING", "NOT_TRUE_HEADING" } },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "source", "text", "headingDecision", "role" },
                },
            },
        },
        required = new[] { "headings" },
    };

    public static SemanticTextTrueHeadingResponse Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("true-heading-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("true-heading-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("true-heading-response-headings-missing");
        var result = new List<SemanticTextTrueHeadingItem>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()) ||
                !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(text.GetString()) ||
                !item.TryGetProperty("headingDecision", out var decision) || decision.ValueKind != JsonValueKind.String || !IsDecision(decision.GetString()) ||
                !item.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || !CeilingSemanticRole.IsAllowed(role.GetString()))
                throw new FormatException("true-heading-response-item-schema-invalid");
            int? occurrence = null;
            if (item.TryGetProperty("occurrence", out var ordinalValue) && ordinalValue.ValueKind != JsonValueKind.Null)
            {
                if (!ordinalValue.TryGetInt32(out var ordinal) || ordinal < 1) throw new FormatException("true-heading-response-occurrence-invalid");
                occurrence = ordinal;
            }
            var left = item.TryGetProperty("leftExactContext", out var leftValue) && leftValue.ValueKind == JsonValueKind.String ? leftValue.GetString() : null;
            var right = item.TryGetProperty("rightExactContext", out var rightValue) && rightValue.ValueKind == JsonValueKind.String ? rightValue.GetString() : null;
            result.Add(new(source.GetString()!, text.GetString()!, decision.GetString()!, role.GetString()!, occurrence, left, right));
        }
        return new(result);
    }

    public static bool IsDecision(string? value) => value is "TRUE_HEADING" or "NOT_TRUE_HEADING";
}

public sealed record SemanticTextTrueHeadingResponse(IReadOnlyList<SemanticTextTrueHeadingItem> Headings);

public sealed record SemanticTextTrueHeadingItem(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("headingDecision")] string HeadingDecision,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);
