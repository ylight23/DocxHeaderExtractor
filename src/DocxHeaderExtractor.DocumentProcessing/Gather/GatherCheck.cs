using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One piece of a fact. Model proposals carry a verbatim quote; direct callers may use a checked source span. <see cref="Role"/> is label, value or text.
/// </summary>
public sealed record FactPart(string UnitId, int Start, int End, string Role, string? Quote = null);

/// <summary>A fact the model proposed: one or more spans that together say one thing (a label and its value, or one sentence).</summary>
public sealed record ProposedFact(IReadOnlyList<FactPart> Parts);

/// <summary>A piece of a kept fact, with its verbatim text and where it came from.</summary>
public sealed record SourcedPart(string UnitId, string SourceId, string Location, string Role, string Text, int? Start = null, int? End = null);

/// <summary>
/// A kept fact. Its part texts are verbatim from the sources. <see cref="Flags"/> name what a reader must check: a pairing that crosses rows, tables or
/// sources, a merged cell, an uncertain heading path, or a label that is not the row label the table's first cell suggests. A flag does not make the
/// fact wrong or right; it says the fact needs a look. <see cref="Render"/> joins the parts with a separator this code adds: the joined line is not
/// verbatim, the parts are.
/// </summary>
public sealed record GatheredFact(IReadOnlyList<SourcedPart> Parts, IReadOnlyList<string> Flags)
{
    /// <summary>Other exact source occurrences of this same fact; used for citations without repeating its text.</summary>
    public IReadOnlyList<SourcedPart> AlsoFrom { get; init; } = Array.Empty<SourcedPart>();

    public string Render()
    {
        var text = new StringBuilder();
        for (var i = 0; i < Parts.Count; i++)
        {
            if (i > 0)
            {
                var previous = Parts[i - 1];
                if (previous.Role == "label")
                {
                    while (text.Length > 0 && char.IsWhiteSpace(text[^1])) text.Length--;
                    if (text.Length > 0 && text[^1] == ':') text.Length--;
                    while (text.Length > 0 && char.IsWhiteSpace(text[^1])) text.Length--;
                    text.Append(": ");
                }
                else if (previous.Role == "value" && Parts[i].Role == "value") text.Append("; ");
                else text.Append(' ');
            }
            text.Append(Parts[i].Text.Trim());
        }
        return text.ToString();
    }
}

public sealed record FactRefusal(int Index, string Reason, ProposedFact? Proposal = null);

/// <summary>A proposed fact whose text repeats a kept one. Its own sources are kept, so every place the text came from can still be shown.</summary>
public sealed record FactDuplicate(int KeptIndex, int DuplicateIndex, IReadOnlyList<SourcedPart> Parts);

public sealed record GatherResult(IReadOnlyList<GatheredFact> Kept, IReadOnlyList<FactRefusal> Refused, IReadOnlyList<FactDuplicate> Duplicates);

/// <summary>
/// Keeps proposals whose quote has exactly one match or whose direct source span is valid. A fact with any invalid part is refused as a whole, so a partly
/// valid fact never reads as complete. A fact that is valid but whose parts sit in a way the table does not suggest is kept and flagged. Calls no model.
/// </summary>
public static class GatherCheck
{
    static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "label", "value", "text" };

    public static GatherResult Check(IReadOnlyList<GatherUnit> units, IEnumerable<ProposedFact> facts)
    {
        var byId = units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var order = units.Select((u, i) => (u.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var kept = new List<GatheredFact>();
        var refused = new List<FactRefusal>();
        var duplicates = new List<FactDuplicate>();
        var keptByText = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var fact in facts)
        {
            var i = index++;
            if (fact.Parts.Count == 0) { refused.Add(new(i, "NO_PARTS", fact)); continue; }
            var resolved = new List<(FactPart Part, GatherUnit Unit, int Start, int End)>();
            var notes = new List<string>();
            string? failure = null;
            foreach (var part in fact.Parts)
            {
                if (!byId.TryGetValue(part.UnitId, out var unit)) { failure = "UNKNOWN_UNIT"; break; }
                if (!Roles.Contains(part.Role)) { failure = "ROLE_UNKNOWN"; break; }
                if (part.Quote is null)
                {
                    if (part.Start < 0 || part.End <= part.Start || part.End > unit.Text.Length) { failure = "SPAN_OUTSIDE_TEXT"; break; }
                    resolved.Add((part, unit, part.Start, part.End));
                    continue;
                }
                var binding = Bind(part, unit, units, order);
                if (binding.Failure is not null) { failure = binding.Failure; break; }
                if (binding.Note is not null) notes.Add(binding.Note);
                if (binding.Unit is not null) resolved.Add((part, binding.Unit, binding.Start, binding.End));
            }
            if (failure is null && resolved.Count == 0) failure = "ONLY_HEADING_LABEL";
            if (failure is null && resolved.All(r => r.Part.Role == "label")) failure = "LABEL_ONLY";
            if (failure is not null) { refused.Add(new(i, failure, fact)); continue; }
            var parts = resolved.Select(r => new SourcedPart(r.Unit.Id, r.Unit.SourceId, r.Unit.Location, r.Part.Role,
                r.Unit.Text[r.Start..r.End], r.Start, r.End)).ToList();
            var key = string.Join("␟", parts.Select(p => p.Text));
            if (keptByText.TryGetValue(key, out var first)) { duplicates.Add(new(first, i, parts)); continue; }
            keptByText[key] = kept.Count;
            var flags = Flags(resolved.Select(r => r.Part with { UnitId = r.Unit.Id }).ToList(), resolved.Select(r => r.Unit).ToList());
            kept.Add(new GatheredFact(parts, [.. flags, .. notes]));
        }
        return new(kept, refused, duplicates);
    }

    /// <summary>How one quoted part was found in the source: its unit and span, or the part dropped (Unit null), or the reason it was not found.</summary>
    sealed record Binding(GatherUnit? Unit, int Start, int End, string? Note, string? Failure);

    /// <summary>A quote is looked for in the units this many places before and after the unit it names, in the same source.</summary>
    const int NeighbourUnits = 3;

    /// <summary>
    /// Binds a quote to the source, in this order, each step on evidence in the source text and never choosing between two places:
    /// (1) an exact match, once, in the unit named; (2) a label that is the unit's heading path, a segment of it, its own heading text, its row label or
    /// column header: that context is attached to every fact already, so the part is dropped and noted; (3) an exact match, once, in exactly one nearby
    /// unit of the same source: the unit id is corrected and noted; (4) a match that differs only in case, once, in the unit named; (5) a match within a
    /// few character edits, at one place only, in the unit named. In (4) and (5) the source's own text is kept and what the model wrote is noted.
    /// </summary>
    static Binding Bind(FactPart part, GatherUnit unit, IReadOnlyList<GatherUnit> units, Dictionary<string, int> order)
    {
        var quote = part.Quote!;
        if (string.IsNullOrWhiteSpace(quote)) return new(null, -1, -1, null, "QUOTE_MISSING");
        var exact = Matches(unit.Text, quote, StringComparison.Ordinal);
        if (exact.Count == 1) return new(unit, exact[0], exact[0] + quote.Length, null, null);
        if (exact.Count > 1) return new(null, -1, -1, null, "QUOTE_AMBIGUOUS");
        if (part.Role == "label" && IsHeadingContext(quote, unit))
            return new(null, -1, -1, "LABEL_FROM_HEADING_CONTEXT: " + quote, null);
        var at = order[unit.Id];
        var nearby = units.Where(u => u.SourceId == unit.SourceId && u.Id != unit.Id && Math.Abs(order[u.Id] - at) <= NeighbourUnits
            && Matches(u.Text, quote, StringComparison.Ordinal).Count == 1).ToList();
        if (nearby.Count == 1)
        {
            var start = nearby[0].Text.IndexOf(quote, StringComparison.Ordinal);
            return new(nearby[0], start, start + quote.Length, $"UNIT_ID_CORRECTED: {unit.Location} -> {nearby[0].Location}", null);
        }
        var caseless = Matches(unit.Text, quote, StringComparison.OrdinalIgnoreCase);
        if (caseless.Count == 1) return new(unit, caseless[0], caseless[0] + quote.Length, $"QUOTE_CASE_DIFFERS: model wrote \"{quote}\"", null);
        if (Closest(unit.Text, quote) is { } close)
            return new(unit, close.Start, close.End, $"QUOTE_CORRECTED_BY_MODEL: model wrote \"{quote}\"", null);
        return new(null, -1, -1, null, "QUOTE_NOT_FOUND");
    }

    static List<int> Matches(string text, string quote, StringComparison comparison)
    {
        var found = new List<int>();
        for (var at = text.IndexOf(quote, comparison); at >= 0; at = at + 1 < text.Length ? text.IndexOf(quote, at + 1, comparison) : -1)
            found.Add(at);
        return found;
    }

    /// <summary>
    /// Whether a label is context the system already attaches: the unit's heading path or any run of its consecutive segments ("B > C" of
    /// "A > B > C"), its own heading text, its row label or column header.
    /// </summary>
    static bool IsHeadingContext(string quote, GatherUnit unit)
    {
        var key = ContextKey(quote);
        if (key.Length == 0) return false;
        var context = new List<string?> { unit.RowLabel, unit.ColumnHeader };
        var segments = unit.HeadingPath.Split(" > ", StringSplitOptions.RemoveEmptyEntries);
        for (var first = 0; first < segments.Length; first++)
            for (var last = first; last < segments.Length; last++)
                context.Add(string.Join(" > ", segments[first..(last + 1)]));
        if (unit.IsHeading && unit.HeadingEnd is { } end) context.Add(unit.Text[..end]);
        return context.Any(c => c is not null && ContextKey(c) == key);
    }

    static string ContextKey(string text) =>
        new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd(':', '.', ';').ToUpperInvariant();

    /// <summary>
    /// The one place in the text within a few character edits of the quote (case ignored): at most one edit per 30 characters, at most six. Null when
    /// nothing is that close, or when two separate places are equally close.
    /// </summary>
    static (int Start, int End)? Closest(string text, string quote)
    {
        if (text.Length > 4000 || quote.Length < 8) return null;
        var budget = Math.Min(6, Math.Max(1, quote.Length / 30));
        var best = int.MaxValue;
        var places = new List<(int Start, int End)>();
        for (var start = 0; start + quote.Length - budget <= text.Length; start++)
            for (var length = Math.Max(1, quote.Length - budget); length <= quote.Length + budget && start + length <= text.Length; length++)
            {
                var distance = Distance(text.AsSpan(start, length), quote, Math.Min(best, budget));
                if (distance > budget || distance > best) continue;
                if (distance < best) { best = distance; places.Clear(); }
                places.Add((start, start + length));
            }
        if (places.Count == 0) return null;
        // Windows that overlap the first best one are the same place seen with another length; a window apart from it is a second place.
        var first = places[0];
        if (places.Any(p => p.Start >= first.End || p.End <= first.Start)) return null;
        return places.OrderBy(p => Math.Abs(p.End - p.Start - quote.Length)).First();
    }

    /// <summary>Levenshtein distance, case ignored, stopping early once every path exceeds <paramref name="limit"/>.</summary>
    static int Distance(ReadOnlySpan<char> a, string b, int limit)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// What a reader must look at. Table rules are only a suggestion: a label beside a value in the same row is the usual case, but a table may use
    /// a column or a merged cell for its labels, so a different pairing is flagged, not decided.
    /// </summary>
    static IReadOnlyList<string> Flags(IReadOnlyList<FactPart> parts, IReadOnlyList<GatherUnit> picked)
    {
        var flags = new List<string>();
        if (picked.Select(u => u.SourceId).Distinct().Count() > 1) flags.Add("CROSS_SOURCE");
        if (picked.Select(u => u.Id).Distinct(StringComparer.Ordinal).Skip(1).Any()) flags.Add("MULTIPLE_UNITS");
        if (parts.Count(p => p.Role == "value") > 1) flags.Add("MULTIPLE_VALUES");
        var places = picked.Select(u => (u.TablePath, u.RowPath)).Distinct().ToList();
        if (places.Count > 1 || (picked.Any(u => u.TablePath is not null) && picked.Any(u => u.TablePath is null)))
            flags.Add("CROSS_ROW_OR_TABLE");
        if (picked.Any(u => u.CellMerged)) flags.Add("MERGED_CELL");
        if (picked.Any(u => !u.PathCertain)) flags.Add("PATH_UNCERTAIN");
        foreach (var value in picked.Where(u => u.TablePath is not null && u.LabelUnitId is not null))
        {
            var labels = parts.Where(p => p.Role == "label").Select(p => p.UnitId).ToList();
            if (labels.Count > 0 && !labels.Contains(value.LabelUnitId!)) { flags.Add("LABEL_NOT_ROW_LABEL"); break; }
        }
        return flags;
    }

    /// <summary>
    /// The units of one batch as the model reads them. Each unit carries its heading path and whether that path is certain, and for a table cell the
    /// unit ids of its row label and column header, so the model can cite them even when they are not in its text.
    /// </summary>
    public static string Payload(IReadOnlyList<GatherUnit> batch)
    {
        var payload = new JsonObject
        {
            ["units"] = new JsonArray(batch.Select(u => (JsonNode)new JsonObject
            {
                ["unitId"] = u.Id,
                // Content after a heading in the same paragraph sits under that heading, so its path includes it; the heading's own words are named apart.
                ["heading"] = u.InlineBody.Length > 0 ? string.Join(" > ", new[] { u.HeadingPath, u.Text[..u.HeadingEnd!.Value].Trim() }.Where(x => x.Length > 0)) : u.HeadingPath,
                ["headingText"] = u.IsHeading && u.HeadingEnd is { } end ? u.Text[..end].Trim() : null,
                ["headingCertain"] = u.PathCertain,
                ["rowLabel"] = u.RowLabel,
                ["rowLabelUnitId"] = u.LabelUnitId,
                ["columnHeader"] = u.ColumnHeader,
                ["columnHeaderUnitId"] = u.HeaderUnitId,
                ["text"] = u.Text,
            }).ToArray()),
        };
        return payload.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

/// <summary>Splits units into model batches by characters and count. A unit longer than the character limit is a batch of its own.</summary>
public static class GatherBatches
{
    public const int MaxChars = 6000, MaxItems = 40;

    /// <summary>Characters for the JSON keys and the ids of a unit: a fixed allowance, not measured per unit.</summary>
    public const int KeysAllowance = 160;

    /// <summary>
    /// Batches of units. The cost of a unit is what is sent for it: its text and its context (heading, row label, column header), plus the allowance.
    /// The units of one table row stay in the same batch, so a value and its label can both be cited in one reply. A unit whose cost exceeds the limit
    /// is a batch of its own; it is not cut.
    /// </summary>
    public static List<List<GatherUnit>> Plan(IEnumerable<GatherUnit> items)
    {
        var batches = new List<List<GatherUnit>>();
        var current = new List<GatherUnit>();
        var chars = 0;
        foreach (var group in Groups(items.Where(i => !string.IsNullOrWhiteSpace(i.Text))))
        {
            var cost = group.Sum(Cost);
            if (current.Count > 0 && (chars + cost > MaxChars || current.Count + group.Count > MaxItems))
            {
                batches.Add(current);
                current = [];
                chars = 0;
            }
            current.AddRange(group);
            chars += cost;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>What one unit adds to a request: its text and its context, plus the allowance.</summary>
    public static int Cost(GatherUnit unit) =>
        unit.Text.Length + unit.HeadingPath.Length + (unit.RowLabel?.Length ?? 0) + (unit.ColumnHeader?.Length ?? 0) + KeysAllowance;

    /// <summary>Consecutive units of one table row form a group; any other unit is a group of its own.</summary>
    internal static IEnumerable<List<GatherUnit>> Groups(IEnumerable<GatherUnit> items)
    {
        List<GatherUnit>? group = null;
        (string SourceId, string? RowPath)? rowKey = null;
        foreach (var item in items)
        {
            (string SourceId, string? RowPath)? key = item.TablePath is null ? null : (item.SourceId, item.RowPath);
            if (group is not null && key is not null && key == rowKey) { group.Add(item); continue; }
            if (group is not null) yield return group;
            group = [item];
            rowKey = key;
        }
        if (group is not null) yield return group;
    }
}
