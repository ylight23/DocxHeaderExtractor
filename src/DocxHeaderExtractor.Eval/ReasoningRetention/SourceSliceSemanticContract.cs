using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public sealed record SourceSliceSemanticHeading(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("sliceIds")] IReadOnlyList<string> SliceIds,
    [property: JsonPropertyName("isHeading")] bool IsHeading,
    [property: JsonPropertyName("semanticRole")] string SemanticRole);

public sealed record SourceSliceSemanticResponse(IReadOnlyList<SourceSliceSemanticHeading> Headings);

/// <summary>Experimental challenger contract. The model selects parser-owned slice identities;
/// it never echoes source text and never supplies numeric coordinates.</summary>
public static class SourceSliceSemanticContract
{
    public const string ProtocolVersion = "a99-source-slice-semantic-v1";

    public static string System => $"""
You identify every structurally real document heading or structural label in the complete source.
Formatting, numbering, and layout are evidence, not rules. The source is represented exactly once
as an ordered list of parser-owned slices. Read the complete source and decide heading existence
and semantic role yourself; slice IDs are addressability, not a candidate list or recall gate.

For every discovered heading return:
- source: the supplied short source alias, copied exactly
- selectionMode: exactly SOURCE_SLICE
- sliceIds: one or more supplied slice IDs, in source order, covering the complete heading
- isHeading: true
- semanticRole: one allowed semantic role

Do not return text, offsets, source IDs, hierarchy, confidence, explanations, or chain-of-thought.
Do not invent slice IDs. Do not omit a heading because its slices are not visually distinctive.
Do not normalize or reconstruct text yourself; the harness owns exact source text and UTF-16 spans.
Return each physical heading occurrence at most once. Do not use Gold.

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
                        selectionMode = new { type = "string", @enum = new[] { "SOURCE_SLICE" } },
                        sliceIds = new { type = "array", minItems = 1, items = new { type = "string", minLength = 1 } },
                        isHeading = new { type = "boolean" },
                        semanticRole = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                    },
                    required = new[] { "source", "selectionMode", "sliceIds", "isHeading", "semanticRole" },
                },
            },
        },
        required = new[] { "headings" },
    };

    public static SourceSliceSemanticResponse Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("source-slice-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("source-slice-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("source-slice-response-headings-missing");
        var result = new List<SourceSliceSemanticHeading>();
        foreach (var item in array.EnumerateArray())
        {
            var forbiddenModelFields = new[] { "text", "verbatimText", "offset", "start", "end", "sourceId", "headingSpan" };
            if (item.ValueKind != JsonValueKind.Object ||
                forbiddenModelFields.Any(name => item.TryGetProperty(name, out _)) ||
                !item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()) ||
                !item.TryGetProperty("selectionMode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "SOURCE_SLICE" ||
                !item.TryGetProperty("sliceIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0 ||
                ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) ||
                !item.TryGetProperty("isHeading", out var isHeading) || isHeading.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("semanticRole", out var role) || role.ValueKind != JsonValueKind.String || !CeilingSemanticRole.IsAllowed(role.GetString()))
                throw new FormatException("source-slice-response-heading-schema-invalid");
            result.Add(new(source.GetString()!, "SOURCE_SLICE", ids.EnumerateArray().Select(id => id.GetString()!).ToArray(), isHeading.GetBoolean(), role.GetString()!));
        }
        return new(result);
    }
}

public enum SourceSliceBindingStatus
{
    Bound,
    NonHeadingIgnored,
    UnknownSource,
    InvalidSliceId,
    SourceMismatch,
    InvalidOrder,
    Overlap,
    DuplicateBinding,
}

public sealed record SourceSliceBindingObservation(
    int Ordinal,
    SourceSliceSemanticHeading Heading,
    SourceSliceBindingStatus Status,
    string? SourceId,
    int? Start,
    int? End,
    string? Reason);

/// <summary>Fail-closed binder for the challenger. It owns reconstruction and coordinates from
/// slice records; no model-provided text or offset participates in binding.</summary>
public static class SourceSliceSemanticBinder
{
    public static IReadOnlyList<CanonicalSemanticProposal> Bind(
        IReadOnlyList<SourceSliceSemanticHeading> headings,
        IReadOnlyDictionary<string, IReadOnlyList<SourceSlice>> slicesByAlias,
        out IReadOnlyList<SourceSliceBindingObservation> observations)
    {
        var result = new List<CanonicalSemanticProposal>();
        var audit = new List<SourceSliceBindingObservation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < headings.Count; ordinal++)
        {
            var heading = headings[ordinal];
            if (!heading.IsHeading)
            {
                audit.Add(new(ordinal, heading, SourceSliceBindingStatus.NonHeadingIgnored, null, null, null, null));
                continue;
            }
            if (!slicesByAlias.TryGetValue(heading.Source, out var available))
            {
                audit.Add(new(ordinal, heading, SourceSliceBindingStatus.UnknownSource, null, null, null, "UNKNOWN_SOURCE_ALIAS"));
                continue;
            }
            var byId = available.ToDictionary(slice => slice.SliceId, StringComparer.Ordinal);
            var selected = new List<SourceSlice>();
            var failed = false;
            foreach (var id in heading.SliceIds)
            {
                if (!byId.TryGetValue(id, out var slice))
                {
                    audit.Add(new(ordinal, heading, SourceSliceBindingStatus.InvalidSliceId, null, null, null, "INVALID_SLICE_ID"));
                    failed = true;
                    break;
                }
                if (slice.SourceAlias != heading.Source)
                {
                    audit.Add(new(ordinal, heading, SourceSliceBindingStatus.SourceMismatch, slice.SourceId, slice.Start, slice.End, "SLICE_SOURCE_MISMATCH"));
                    failed = true;
                    break;
                }
                if (selected.Any(item => item.SliceId == slice.SliceId))
                {
                    audit.Add(new(ordinal, heading, SourceSliceBindingStatus.Overlap, slice.SourceId, slice.Start, slice.End, "DUPLICATE_SLICE_ID"));
                    failed = true;
                    break;
                }
                if (selected.Count > 0 && slice.Start != selected[^1].End)
                {
                    audit.Add(new(ordinal, heading, SourceSliceBindingStatus.InvalidOrder, slice.SourceId, slice.Start, slice.End, "NON_CONTIGUOUS_OR_OUT_OF_ORDER"));
                    failed = true;
                    break;
                }
                selected.Add(slice);
            }
            if (failed || selected.Count == 0) continue;
            var start = selected[0].Start;
            var end = selected[^1].End;
            var key = $"{selected[0].SourceId}:{start}:{end}";
            if (!seen.Add(key))
            {
                audit.Add(new(ordinal, heading, SourceSliceBindingStatus.DuplicateBinding, selected[0].SourceId, start, end, "DUPLICATE_PHYSICAL_SPAN"));
                continue;
            }
            var exactText = string.Concat(selected.Select(slice => slice.Text));
            result.Add(new(heading.Source, true, exactText, SemanticRole: heading.SemanticRole, SelectionMode: CanonicalSemanticSelectionMode.VerbatimText));
            audit.Add(new(ordinal, heading, SourceSliceBindingStatus.Bound, selected[0].SourceId, start, end, null));
        }
        observations = audit;
        return result;
    }
}
