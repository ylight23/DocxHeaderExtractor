using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Fresh semantic-text contract for the duplicate-occurrence intervention. Semantic
/// discovery remains model-owned; context is only an exact locator when identical text occurs
/// more than once inside one supplied source alias.</summary>
public static class SemanticTextDuplicateDisambiguationContract
{
    public const string ProtocolVersion = "a99-semantic-text-duplicate-disambiguation-v1";
    private const int MaxContextLength = 64;

    public static readonly string System = $"""
You identify every structurally real document heading or structural label in the supplied source.
Decide semantic existence and role yourself from the complete source. Formatting, numbering, and
layout are evidence, not rules. Return only headings discovered in the supplied source.

For every heading, return:
- source: the supplied short source alias, copied exactly
- text: the complete heading text copied verbatim from that source occurrence
- role: one allowed semantic role

The text field is a quotation used for exact deterministic binding. Do not normalize spelling,
punctuation, whitespace, numbering, or case. Do not return offsets, hierarchy, confidence,
explanations, or chain-of-thought. Do not return text that is not an exact substring of the
referenced source occurrence. Do not use Gold.

If the same verbatim text occurs more than once inside the referenced source alias, you MUST add
one or both optional exact locator fields. Copy each locator verbatim from the source immediately
before or after the quoted text, using at most {MaxContextLength} characters. Include enough
non-whitespace source text to distinguish exactly one occurrence. These fields locate an already
discovered heading; they do not decide whether text is a heading. If the anchors do not identify
exactly one occurrence, the harness will leave the proposal unresolved.

For unique text, omit both locator fields. Return each semantic heading at most once.
Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

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
                        leftExactContext = new { type = "string", minLength = 1, maxLength = MaxContextLength },
                        rightExactContext = new { type = "string", minLength = 1, maxLength = MaxContextLength },
                    },
                    required = new[] { "source", "text", "role" },
                },
            },
        },
        required = new[] { "headings" },
    };

    public static string BuildUser(string packetJson, string route) => $"TASK={ProtocolVersion}\nroute={route}\n{packetJson}";

    public static SemanticTextResponse Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("duplicate-disambiguation-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("duplicate-disambiguation-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        var root = doc.RootElement;
        if (!root.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("duplicate-disambiguation-headings-missing");
        var result = new List<SemanticTextHeading>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()) ||
                !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(text.GetString()) ||
                !item.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || !CeilingSemanticRole.IsAllowed(role.GetString()) ||
                item.TryGetProperty("occurrence", out _))
                throw new FormatException("duplicate-disambiguation-heading-schema-invalid");

            var left = Context(item, "leftExactContext");
            var right = Context(item, "rightExactContext");
            result.Add(new SemanticTextHeading(source.GetString()!, text.GetString()!, role.GetString()!, null, left, right));
        }
        return new SemanticTextResponse(result);
    }

    private static string? Context(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()) || value.GetString()!.Length > MaxContextLength)
            throw new FormatException("duplicate-disambiguation-context-invalid");
        return value.GetString();
    }
}

public sealed record SemanticTextDuplicateTrace(
    int RawOrdinal,
    string SourceAlias,
    string Text,
    string Kind,
    int ExactMatchCount,
    int ValidAnchorMatchCount,
    bool DisambiguatorAvailable,
    bool DisambiguatorValid,
    bool ExactOccurrenceResolved,
    bool Bound,
    int? ResolvedStart = null);

/// <summary>Exact context-only binder. It rejects zero or multiple anchor matches and never
/// falls back to first/last/nearest occurrence.</summary>
public static class SemanticTextDuplicateBinder
{
    public static IReadOnlyList<SemanticTextBoundHeading> Bind(
        IReadOnlyList<SemanticTextHeading> headings,
        IReadOnlyList<SemanticTextSourceAlias> aliases,
        out IReadOnlyList<SemanticTextBindingObservation> observations,
        out IReadOnlyList<SemanticTextDuplicateTrace> traces)
    {
        var byAlias = aliases.ToDictionary(x => x.Alias, StringComparer.Ordinal);
        var result = new List<SemanticTextBoundHeading>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var audit = new List<SemanticTextBindingObservation>();
        var trace = new List<SemanticTextDuplicateTrace>();
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            if (!byAlias.TryGetValue(heading.Source, out var alias))
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.INVALID_SOURCE_ALIAS, 0, null, null, null, null, "INVALID_SOURCE_ALIAS"));
                trace.Add(new(i, heading.Source, heading.Text, "TEXT_NOT_FOUND", 0, 0, false, false, false, false));
                continue;
            }

            var positions = FindExact(alias.RawText, heading.Text);
            if (positions.Count == 0)
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.TEXT_NOT_FOUND, 0, alias.Alias, alias.SourceId, null, null, "TEXT_NOT_FOUND"));
                trace.Add(new(i, alias.Alias, heading.Text, "TEXT_NOT_FOUND", 0, 0, false, false, false, false));
                continue;
            }

            var available = heading.LeftExactContext is not null || heading.RightExactContext is not null;
            var matching = positions.Where(position => MatchesContext(alias.RawText, heading, position)).ToArray();
            var resolved = positions.Count == 1 ? positions[0] : matching.Length == 1 ? matching[0] : (int?)null;
            if (resolved is null)
            {
                var invalid = positions.Count > 1 && available && matching.Length == 0;
                var reason = invalid ? "INVALID_CONTEXT_ANCHOR" : "AMBIGUOUS_EXACT_TEXT";
                audit.Add(new(i, heading, SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, positions.Count, alias.Alias, alias.SourceId, null, null, reason));
                trace.Add(new(i, alias.Alias, heading.Text, invalid ? "INVALID_CONTEXT_ANCHOR" : "AMBIGUOUS_DUPLICATE_TEXT", positions.Count, matching.Length, available, available && matching.Length == 1, false, false));
                continue;
            }

            var start = resolved.Value;
            var end = start + heading.Text.Length;
            var key = $"{alias.SourceId}:{start}:{end}";
            if (!seen.Add(key))
            {
                audit.Add(new(i, heading, SemanticTextBindingStatus.DUPLICATE_PROPOSAL, positions.Count, alias.Alias, alias.SourceId, start, end, "DUPLICATE_PROPOSAL"));
                trace.Add(new(i, alias.Alias, heading.Text, "DUPLICATE_PROPOSAL", positions.Count, matching.Length, available, true, true, false));
                continue;
            }

            audit.Add(new(i, heading, SemanticTextBindingStatus.BOUND, positions.Count, alias.Alias, alias.SourceId, start, end, null));
            trace.Add(new(i, alias.Alias, heading.Text, positions.Count == 1 ? "UNIQUE_TEXT_BIND" : "DUPLICATE_RESOLVED_BY_EXACT_CONTEXT", positions.Count, matching.Length, available, positions.Count == 1 || matching.Length == 1, true, true, start));
            result.Add(new(alias.Alias, alias.SourceId, alias.SourceOrdinal, heading.Text, heading.Role, start, end));
        }
        observations = audit;
        traces = trace;
        return result;
    }

    private static bool MatchesContext(string source, SemanticTextHeading heading, int start)
    {
        if (heading.LeftExactContext is not null && (start < heading.LeftExactContext.Length ||
            !string.Equals(source.Substring(start - heading.LeftExactContext.Length, heading.LeftExactContext.Length), heading.LeftExactContext, StringComparison.Ordinal))) return false;
        var end = start + heading.Text.Length;
        return heading.RightExactContext is null || (end + heading.RightExactContext.Length <= source.Length &&
            string.Equals(source.Substring(end, heading.RightExactContext.Length), heading.RightExactContext, StringComparison.Ordinal));
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
}
