using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The read-only evidence tools of the DOCX agent. Every tool reads the OOXML record of the document (<see cref="DocxAgentSource"/>): the
/// paragraphs, their facts and the style table. They return observations only: no result labels, ranks or filters a paragraph. The machinery
/// (strict arguments, capped exact bodies, the ledger, the cache, rejections) is <see cref="AgentToolHost"/>'s; the tool names and shapes follow
/// the PDF tools where the meaning is the same.
/// </summary>
internal sealed class DocxAgentTools : AgentToolHost
{
    public const string Version = "DOCX_AGENT_TOOLS_V1";
    public const string SourceContext = "get_source_context", SourceSpan = "get_source_span", SearchText = "search_document_text",
        Repeated = "get_repeated_occurrences", Compare = "compare_source_locations";
    /// <summary>The facts tool: the facts of a range of lines and the other occurrences of the same text. The agent's first reply must call it.</summary>
    public const string LineFacts = "get_line_facts";
    public static readonly string[] Names = [SourceContext, SourceSpan, SearchText, Repeated, Compare];

    public const int MaxNeighbours = 6, MaxSpanAliases = 8, MaxRepeated = 20, MaxCompareWindow = 3, MaxSearchLimit = 50, MaxQueryChars = 200, MinQueryChars = 2,
        MaxListTextChars = 200, MaxCompareTextChars = 160, MaxSpanTextChars = 4000, MaxFactLines = 24, MaxFactRange = 400,
        PrefetchPerTarget = 2, PrefetchTextChars = 100;

    readonly DocxAgentSource docx;
    readonly ILookup<string, int> repeatIndex;

    public DocxAgentTools(DocxAgentSource source) : base(source)
    {
        docx = source;
        repeatIndex = Enumerable.Range(0, lines.Count).ToLookup(i => Evidence.RepeatKey(lines[i].Text), StringComparer.Ordinal);
    }

    protected override string ToolsVersion => Version;
    protected override string FactsToolName => LineFacts;
    protected override bool IsRegistered(string? name) => name is not null && (Names.Contains(name, StringComparer.Ordinal) || name == LineFacts);
    public override IReadOnlyList<string> ReservedNames => Names;
    public override JsonObject DocumentStyles() => (JsonObject)docx.StyleTable.DeepClone();

    protected override (JsonNode?, JsonNode?, bool) Dispatch(string name, JsonElement args, List<string> returned) => name switch
    {
        SourceContext => Context(args, returned),
        SourceSpan => Span(args, returned),
        SearchText => Search(args, returned),
        Repeated => Repeat(args, returned),
        LineFacts => FactsOfRange(args, returned),
        _ => CompareTwo(args, returned),
    };

    // ---------------- declarations ----------------

    /// <summary>The six declarations, get_line_facts last.</summary>
    public static JsonArray Definitions()
    {
        var all = BaseDefinitions();
        all.Add(Tool(LineFacts, "Read facts the program measured for the paragraphs from one alias to another in reading order (at most " + MaxFactLines + " per call; use offset to continue where a result was cut). For each paragraph: its style (id, name, outline level, size, bold, italic, underline, all capitals, alignment), its numbering (list, level, rendered label), its text (characters, share of bold characters, line breaks), where it stands (in a table with depth, row and cell, or in a text box), the internal links it carries, the rank and use of its style in the document, whether its neighbours share its style, its size against the document median, and, when its text also occurs on other paragraphs, the first other occurrences (place in reading order, style, whether they are in a table or carry links, the size difference, the paragraph that follows each). Measurements only: no label and no decision.",
            new JsonObject
            {
                ["first"] = AliasArg("The first paragraph of the range."),
                ["last"] = AliasArg("The last paragraph of the range, not before the first and at most " + MaxFactRange + " paragraphs after it."),
                ["offset"] = Int(0, 100000, "How many paragraphs of the range to skip."),
            }, "first", "last", "offset"));
        return all;
    }

    static JsonArray BaseDefinitions() => new(
        Tool(SourceContext, "Read the paragraphs before and after one paragraph in reading order. Each has its text and a brief of its facts (style name, size, bold, outline level, numbering label, whether it is in a table) and its distance from the given paragraph.",
            new JsonObject
            {
                ["alias"] = AliasArg("The paragraph to read around."),
                ["before"] = Int(0, MaxNeighbours, "Paragraphs before it."),
                ["after"] = Int(0, MaxNeighbours, "Paragraphs after it."),
            }, "alias", "before", "after"),
        Tool(SourceSpan, "Read the full record of up to 8 paragraphs: complete text, reading-order position and all their facts.",
            new JsonObject
            {
                ["aliases"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxSpanAliases, ["items"] = AliasArg("A paragraph alias."),
                    ["description"] = "Aliases of the paragraphs to read." },
            }, "aliases"),
        Tool(SearchText, "Find the paragraphs of the whole document whose text contains the query, in reading order. mode literal matches exactly; mode normalized ignores case, Vietnamese diacritics and all whitespace. Each match has its alias, text and a brief of its facts.",
            new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = MinQueryChars, ["maxLength"] = MaxQueryChars, ["description"] = "Text to find." },
                ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("literal", "normalized") },
                ["offset"] = Int(0, 100000, "Matches to skip."),
                ["limit"] = Int(1, MaxSearchLimit, "Maximum matches to return."),
            }, "query", "mode", "offset", "limit"),
        Tool(Repeated, "Find the other paragraphs of the document whose text is the same as one paragraph after a fixed normalization (lower case, runs of dots removed, a trailing number removed, digits masked, whitespace collapsed). Each match has its alias, text, a brief of its facts and whether its text is exactly the same.",
            new JsonObject
            {
                ["alias"] = AliasArg("The paragraph whose text to look for."),
                ["maxResults"] = Int(1, MaxRepeated, "Maximum matches to return."),
            }, "alias", "maxResults"),
        Tool(Compare, "Put two paragraphs side by side: each with the paragraphs around it and their facts in brief, plus the differences between them (reading-order distance, same normalized text, same exact text, same style, size and bold-share differences, same alignment, same numbering level).",
            new JsonObject
            {
                ["first"] = AliasArg("First paragraph."),
                ["second"] = AliasArg("Second paragraph."),
                ["window"] = Int(0, MaxCompareWindow, "Paragraphs before and after each."),
            }, "first", "second", "window"));

    public static string DefinitionsSha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Definitions().ToJsonString(Wire))));

    // ---------------- handlers ----------------

    (JsonNode?, JsonNode?, bool) Context(JsonElement args, List<string> returned)
    {
        Keys(args, "alias", "before", "after");
        var alias = Alias(args, "alias");
        int before = Range(args, "before", 0, MaxNeighbours), after = Range(args, "after", 0, MaxNeighbours);
        var at = index[alias];
        var from = Math.Max(0, at - before);
        var window = Enumerable.Range(from, Math.Min(lines.Count, at + after + 1) - from).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["before"] = before, ["after"] = after };
        var (result, taken) = Fit(SourceContext, arguments, window.Length, k => new JsonObject
        {
            ["alias"] = alias,
            ["lines"] = new JsonArray(window.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars, relativeTo: at)).ToArray()),
            ["truncation"] = Truncation(k, window.Length, null),
        });
        returned.AddRange(window.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < window.Length);
    }

    (JsonNode?, JsonNode?, bool) Span(JsonElement args, List<string> returned)
    {
        Keys(args, "aliases");
        var arr = args.GetProperty("aliases");
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is < 1 or > MaxSpanAliases) throw new ArgumentError("ALIASES_INVALID");
        var aliases = new List<string>();
        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.String) throw new ArgumentError("ARGUMENT_TYPE_INVALID");
            var s = a.GetString()!;
            if (s.Length > MaxAliasChars || !index.ContainsKey(s)) throw new ArgumentError("ALIAS_NOT_IN_DOCUMENT");
            if (!aliases.Contains(s, StringComparer.Ordinal)) aliases.Add(s);
        }
        var arguments = new JsonObject { ["aliases"] = new JsonArray(aliases.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()) };
        var (result, taken) = Fit(SourceSpan, arguments, aliases.Count, k => new JsonObject
        {
            ["records"] = new JsonArray(aliases.Take(k).Select(a =>
            {
                var i = index[a];
                var o = new JsonObject { ["alias"] = a, ["readingOrder"] = i, ["text"] = Clip(lines[i].Text, MaxSpanTextChars) };
                if (lines[i].Text.Length > MaxSpanTextChars) { o["textTruncated"] = true; o["textChars"] = lines[i].Text.Length; }
                o["facts"] = FactsNode(i);
                return (JsonNode)o;
            }).ToArray()),
            ["truncation"] = Truncation(k, aliases.Count, null),
        });
        returned.AddRange(aliases.Take(taken));
        return (arguments, result, taken < aliases.Count);
    }

    (JsonNode?, JsonNode?, bool) Search(JsonElement args, List<string> returned)
    {
        Keys(args, "query", "mode", "offset", "limit");
        var query = Str(args, "query");
        if (query.Trim().Length < MinQueryChars) throw new ArgumentError("QUERY_TOO_SHORT");
        if (query.Length > MaxQueryChars || query.Any(char.IsControl)) throw new ArgumentError("QUERY_INVALID");
        var mode = Str(args, "mode");
        if (mode is not ("literal" or "normalized")) throw new ArgumentError("MODE_INVALID");
        int offset = Range(args, "offset", 0, 100000), limit = Range(args, "limit", 1, MaxSearchLimit);
        var needle = mode == "normalized" ? Evidence.Normalize(query) : query;
        if (needle.Length == 0) throw new ArgumentError("QUERY_TOO_SHORT");
        var matches = Enumerable.Range(0, lines.Count)
            .Where(i => (mode == "normalized" ? Evidence.Normalize(lines[i].Text) : lines[i].Text).Contains(needle, StringComparison.Ordinal)).ToArray();
        var slice = matches.Skip(offset).Take(limit).ToArray();
        var arguments = new JsonObject { ["query"] = query, ["mode"] = mode, ["offset"] = offset, ["limit"] = limit };
        var (result, taken) = Fit(SearchText, arguments, slice.Length, k => new JsonObject
        {
            ["total"] = matches.Length, ["offset"] = offset, ["scannedLines"] = lines.Count,
            ["matches"] = new JsonArray(slice.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars)).ToArray()),
            ["hasMore"] = offset + k < matches.Length, ["nextOffset"] = offset + k < matches.Length ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < slice.Length);
    }

    (JsonNode?, JsonNode?, bool) Repeat(JsonElement args, List<string> returned)
    {
        Keys(args, "alias", "maxResults");
        var alias = Alias(args, "alias");
        var max = Range(args, "maxResults", 1, MaxRepeated);
        var at = index[alias]; var self = lines[at];
        var key = Evidence.RepeatKey(self.Text);
        // An empty key (a paragraph of digits, dots or spaces only) matches every such paragraph; none is reported as a repeat.
        var matches = key.Length == 0 ? Array.Empty<int>() : repeatIndex[key].Where(i => i != at).ToArray();
        var slice = matches.Take(max).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["maxResults"] = max };
        var (result, taken) = Fit(Repeated, arguments, slice.Length, k => new JsonObject
        {
            ["alias"] = alias, ["text"] = Clip(self.Text, MaxListTextChars), ["normalization"] = Evidence.RepeatNormalization,
            ["normalizedKeyEmpty"] = key.Length == 0, ["totalMatches"] = matches.Length,
            ["matches"] = new JsonArray(slice.Take(k).Select(i =>
            {
                var n = LineNode(i, MaxListTextChars);
                n["exactTextMatch"] = lines[i].Text == self.Text;
                return (JsonNode)n;
            }).ToArray()),
            ["truncation"] = Truncation(k, matches.Length, null),
        });
        returned.Add(alias);
        returned.AddRange(slice.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < matches.Length);
    }

    (JsonNode?, JsonNode?, bool) CompareTwo(JsonElement args, List<string> returned)
    {
        Keys(args, "first", "second", "window");
        var first = Alias(args, "first"); var second = Alias(args, "second");
        if (first == second) throw new ArgumentError("COMPARE_SAME_LINE");
        var window = Range(args, "window", 0, MaxCompareWindow);
        int[] Around(int at) => Enumerable.Range(Math.Max(0, at - window), Math.Min(lines.Count, at + window + 1) - Math.Max(0, at - window)).ToArray();
        JsonObject Side(string alias)
        {
            var at = index[alias];
            return new JsonObject
            {
                ["alias"] = alias, ["readingOrder"] = at, ["normalizedText"] = Evidence.RepeatKey(lines[at].Text),
                ["lines"] = new JsonArray(Around(at).Select(i => (JsonNode)LineNode(i, MaxCompareTextChars, relativeTo: at)).ToArray()),
            };
        }
        int a = index[first], b = index[second];
        JsonObject Style(int i) => docx.Facts[lines[i].Alias]["style"]!.AsObject();
        double? D(string key) => Style(a)[key] is { } x && Style(b)[key] is { } y ? Math.Round(y.GetValue<double>() - x.GetValue<double>(), 1, MidpointRounding.AwayFromZero) : null;
        string? S(int i, string key) => Style(i)[key]?.ToJsonString();
        int? Level(int i) => docx.Facts[lines[i].Alias]["numbering"]?["level"]?.GetValue<int>();
        double Bold(int i) => docx.Facts[lines[i].Alias]["text"]!["boldCharRatio"]!.GetValue<double>();
        var arguments = new JsonObject { ["first"] = first, ["second"] = second, ["window"] = window };
        var result = new JsonObject
        {
            ["first"] = Side(first), ["second"] = Side(second),
            ["delta"] = new JsonObject
            {
                ["readingOrderDistance"] = b - a,
                ["sameNormalizedText"] = Evidence.RepeatKey(lines[a].Text) == Evidence.RepeatKey(lines[b].Text), ["exactTextMatch"] = lines[a].Text == lines[b].Text,
                ["sameStyle"] = S(a, "name") == S(b, "name") && S(a, "sizePt") == S(b, "sizePt") && S(a, "bold") == S(b, "bold"),
                ["sizeDeltaPt"] = D("sizePt"),
                ["boldShareDelta"] = Math.Round(Bold(b) - Bold(a), 2, MidpointRounding.AwayFromZero),
                ["sameAlignment"] = S(a, "alignment") == S(b, "alignment"),
                ["sameNumberingLevel"] = Level(a) == Level(b),
            },
        };
        // Two windows of at most 7 paragraphs of 160 characters each fit the cap; checked anyway.
        if (Body("E0.00", Compare, "OK", null, arguments, result).Length + ResultMargin > MaxResultBytes) throw new InvalidOperationException("DOCX_TOOL_COMPARE_OVER_CAP");
        returned.AddRange(Around(a).Concat(Around(b)).Select(i => lines[i].Alias));
        return (arguments, result, false);
    }

    (JsonNode?, JsonNode?, bool) FactsOfRange(JsonElement args, List<string> returned)
    {
        Keys(args, "first", "last", "offset");
        var first = Alias(args, "first"); var last = Alias(args, "last");
        int from = index[first], to = index[last];
        if (to < from || to - from + 1 > MaxFactRange) throw new ArgumentError("RANGE_INVALID");
        int offset = Range(args, "offset", 0, 100000);
        var rangeLength = to - from + 1;
        var slice = Enumerable.Range(from, rangeLength).Skip(offset).Take(MaxFactLines).ToArray();
        var arguments = new JsonObject { ["first"] = first, ["last"] = last, ["offset"] = offset };
        var repeated = slice.ToDictionary(i => i, RepeatedTextOf);
        JsonNode Item(int i)
        {
            var o = FactsNode(i);
            o["alias"] = lines[i].Alias;
            if (repeated[i] is { } r) o["repeatedText"] = r.DeepClone();
            return o;
        }
        IEnumerable<string> Shown(int i) => repeated[i] is { } r
            ? r["shown"]!.AsArray().SelectMany(x => new[] { x!["alias"]!.GetValue<string>() }.Concat(x["followingLine"] is JsonObject f ? new[] { f["alias"]!.GetValue<string>() } : Array.Empty<string>()))
            : Array.Empty<string>();
        var (result, taken) = Fit(LineFacts, arguments, slice.Length, k => new JsonObject
        {
            ["readBy"] = "the program, from the OOXML of every paragraph and from the text of the whole document: " + DocxAgentSource.FactsDescription + "; repeatedText lists the first other occurrences of the paragraph's text under the stated normalization",
            ["version"] = DocxAgentSource.FactsVersion,
            ["normalization"] = Evidence.RepeatNormalization,
            ["rangeLines"] = rangeLength, ["offset"] = offset,
            ["facts"] = new JsonArray(slice.Take(k).Select(Item).ToArray()),
            ["hasMore"] = offset + k < rangeLength, ["nextOffset"] = offset + k < rangeLength ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).SelectMany(i => Shown(i).Prepend(lines[i].Alias)));
        return (arguments, result, taken < slice.Length);
    }

    // ---------------- observations ----------------

    /// <summary>The facts of one paragraph, as the source computed them (a copy: a node has one parent).</summary>
    JsonObject FactsNode(int i)
    {
        var o = (JsonObject)docx.Facts[lines[i].Alias].DeepClone();
        o.Remove("alias");
        return o;
    }

    /// <summary>A paragraph's facts in a few words, for lists: style name, size, bold, outline level, numbering label, whether it is in a table.</summary>
    JsonObject Brief(int i)
    {
        var f = docx.Facts[lines[i].Alias];
        var style = f["style"]!.AsObject();
        var o = new JsonObject();
        if (style["name"] is { } name) o["styleName"] = name.DeepClone();
        if (style["sizePt"] is { } size) o["sizePt"] = size.DeepClone();
        o["bold"] = style["bold"]!.DeepClone();
        if (style["outlineLevel"] is { } outline) o["outlineLevel"] = outline.DeepClone();
        if (f["numbering"]?["label"] is { } label) o["numberLabel"] = label.DeepClone();
        o["inTable"] = f["place"]!["inTable"]!.DeepClone();
        return o;
    }

    JsonObject LineNode(int i, int maxText, int? relativeTo = null)
    {
        var l = lines[i];
        var o = new JsonObject { ["alias"] = l.Alias, ["readingOrder"] = i };
        if (relativeTo is { } r) o["relative"] = i - r;
        o["text"] = Clip(l.Text, maxText);
        if (l.Text.Length > maxText) { o["textTruncated"] = true; o["textChars"] = l.Text.Length; }
        o["facts"] = Brief(i);
        return o;
    }

    /// <summary>The first other occurrences of a paragraph's text: where they are, their facts in brief, and the paragraph after each. Null when
    /// the text occurs nowhere else. The only rule is "the text occurs elsewhere"; it says nothing about what any paragraph is.</summary>
    JsonObject? RepeatedTextOf(int at)
    {
        var key = Evidence.RepeatKey(lines[at].Text);
        var others = key.Length == 0 ? Array.Empty<int>() : repeatIndex[key].Where(i => i != at).ToArray();
        if (others.Length == 0) return null;
        var target = docx.Facts[lines[at].Alias]["style"]!.AsObject();
        JsonObject Other(int j)
        {
            var o = new JsonObject { ["alias"] = lines[j].Alias, ["readingOrder"] = j, ["distanceFromTarget"] = j - at, ["exactTextMatch"] = lines[j].Text == lines[at].Text };
            if (lines[j].Text != lines[at].Text) o["text"] = Clip(lines[j].Text, PrefetchTextChars);
            o["facts"] = Brief(j);
            var other = docx.Facts[lines[j].Alias]["style"]!.AsObject();
            if (target["sizePt"] is { } x && other["sizePt"] is { } y) o["sizeDeltaPt"] = Math.Round(y.GetValue<double>() - x.GetValue<double>(), 1, MidpointRounding.AwayFromZero);
            if (j + 1 < lines.Count) o["followingLine"] = new JsonObject { ["alias"] = lines[j + 1].Alias, ["text"] = Clip(lines[j + 1].Text, PrefetchTextChars) };
            return o;
        }
        return new JsonObject
        {
            ["otherOccurrences"] = others.Length,
            ["shown"] = new JsonArray(others.Take(PrefetchPerTarget).Select(j => (JsonNode)Other(j)).ToArray()),
        };
    }
}
