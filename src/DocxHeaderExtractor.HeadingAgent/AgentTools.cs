using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The read-only evidence tools the agent may call (ported from the qualified P7F1V6AgentTools, P7_F1_V6_AGENT_TOOLS_V1, with get_line_facts
/// declared and layout facts V3). Every tool reads the parser's record of the document; no result labels, ranks or filters a line. Arguments are
/// validated strictly, every result is an exact JSON body of at most <see cref="MaxResultBytes"/> bytes that says when it was cut, and every call
/// is recorded with the aliases its result showed. The parity tests hold every result to the qualified bytes.
/// </summary>
internal sealed class AgentTools : AgentToolHost
{
    public const string Version = "P7_F1_V6_AGENT_TOOLS_V1";
    public const int MaxNeighbours = 6, MaxPageLines = 40,
        MaxSpanAliases = 8, MaxRepeated = 20, MaxCompareWindow = 3, MaxSearchLimit = 50, MaxQueryChars = 200, MinQueryChars = 2,
        MaxListTextChars = 300, MaxSpanTextChars = 4000, MaxFontSizes = 16;

    public const string DocumentOverview = "get_document_overview", SourceContext = "get_source_context", PageLayout = "get_page_layout",
        SourceSpan = "get_source_span", SearchText = "search_document_text", Repeated = "get_repeated_occurrences", Compare = "compare_source_locations";

    public static readonly string[] Names = [DocumentOverview, SourceContext, PageLayout, SourceSpan, SearchText, Repeated, Compare];

    /// <summary>The eighth tool: the layout facts (V3) and the other occurrences of the same text, for a range of lines. The agent's first reply
    /// must call it.</summary>
    public const string LineFacts = "get_line_facts";
    public const int MaxFactLines = 24, MaxFactRange = 400;

    readonly ILookup<string, int> repeatIndex;

    public AgentTools(AgentSource source) : base(source)
    {
        layoutFacts = source.LayoutFacts;
        repeatIndex = Enumerable.Range(0, lines.Count).ToLookup(i => Evidence.RepeatKey(lines[i].Text), StringComparer.Ordinal);
    }

    protected override string ToolsVersion => Version;
    protected override string FactsToolName => LineFacts;
    protected override bool IsRegistered(string? name) => name is not null && (Names.Contains(name, StringComparer.Ordinal) || name == LineFacts);
    public override IReadOnlyList<string> ReservedNames => Names;

    protected override (JsonNode?, JsonNode?, bool) Dispatch(string name, JsonElement args, List<string> returned) => name switch
    {
        DocumentOverview => Overview(args),
        SourceContext => Context(args, returned),
        PageLayout => Page(args, returned),
        SourceSpan => Span(args, returned),
        SearchText => Search(args, returned),
        Repeated => Repeat(args, returned),
        LineFacts => FactsOfRange(args, returned),
        _ => CompareTwo(args, returned),
    };

    // ---------------- declarations ----------------

    /// <summary>The eight declarations, get_line_facts last.</summary>
    public static JsonArray Definitions()
    {
        var all = BaseDefinitions();
        all.Add(FactsToolDefinition());
        return all;
    }

    static JsonObject FactsToolDefinition() => Tool(LineFacts, "Read facts the program measured for the lines from one alias to another in reading order (at most " + MaxFactLines + " lines per call; use offset to continue where a result was cut). For each line: its page, its layout block (number, lines in the block, its place in it), the separate text segments on its visual line (how many, which one, the gaps in points before and after it), how many other lines of its page start at the same left edge, and, when its text also occurs on other lines, the first other occurrences (page, position in reading order, layout, how their font and left edge differ, the line that follows each). Measurements only: no label and no decision.",
        new JsonObject
        {
            ["first"] = AliasArg("The first line of the range."),
            ["last"] = AliasArg("The last line of the range, not before the first and at most " + MaxFactRange + " lines after it."),
            ["offset"] = Int(0, 100000, "How many lines of the range to skip."),
        }, "first", "last", "offset");

    static JsonArray BaseDefinitions() => new(
        Tool(DocumentOverview, "Summarise the whole document from the parser: number of lines and pages, and for each page its line count, text extent and median font size; plus how many lines use each font size. No line text.",
            new JsonObject()),
        Tool(SourceContext, "Read the lines before and after one line in reading order. Each line has its text, page and layout (bbox in PDF points, font size, font name, bold and italic ratio) and its position relative to the given line (same baseline, vertical offset, horizontal overlap, font-size difference). Also returns the other lines on the same baseline of that page and the page's statistics.",
            new JsonObject
            {
                ["alias"] = AliasArg("The line to read around."),
                ["before"] = Int(0, MaxNeighbours, "Lines before it."),
                ["after"] = Int(0, MaxNeighbours, "Lines after it."),
            }, "alias", "before", "after"),
        Tool(PageLayout, "Read the lines of one page in reading order, each with its text and layout (bbox in PDF points, bottom-left origin; font size, name, bold and italic ratio), and the page's statistics.",
            new JsonObject
            {
                ["page"] = Int(1, 100000, "1-based page number."),
                ["offset"] = Int(0, 100000, "Page lines to skip in reading order."),
                ["limit"] = Int(1, MaxPageLines, "Maximum lines to return."),
            }, "page", "offset", "limit"),
        Tool(SourceSpan, "Read the full parser record of up to 8 lines: complete text, page, reading-order position and layout.",
            new JsonObject
            {
                ["aliases"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxSpanAliases, ["items"] = AliasArg("A line alias."),
                    ["description"] = "Aliases of the lines to read." },
            }, "aliases"),
        Tool(SearchText, "Find the lines of the whole document whose text contains the query, in reading order. mode literal matches exactly; mode normalized ignores case, Vietnamese diacritics and all whitespace. Each match has its alias, page, text and layout.",
            new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = MinQueryChars, ["maxLength"] = MaxQueryChars, ["description"] = "Text to find." },
                ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("literal", "normalized") },
                ["offset"] = Int(0, 100000, "Matches to skip."),
                ["limit"] = Int(1, MaxSearchLimit, "Maximum matches to return."),
            }, "query", "mode", "offset", "limit"),
        Tool(Repeated, "Find the other lines of the document whose text is the same as one line after a fixed normalization (lower case, runs of dots removed, a trailing number removed, digits masked, whitespace collapsed). Each match has its alias, page, text, layout and whether its text is exactly the same.",
            new JsonObject
            {
                ["alias"] = AliasArg("The line whose text to look for."),
                ["maxResults"] = Int(1, MaxRepeated, "Maximum matches to return."),
            }, "alias", "maxResults"),
        Tool(Compare, "Put two lines side by side: each with the lines around it, its layout and its page's statistics, plus the differences between them (page distance, reading-order distance, same normalized text, same exact text, font-size, bold-ratio and left-edge differences).",
            new JsonObject
            {
                ["first"] = AliasArg("First line."),
                ["second"] = AliasArg("Second line."),
                ["window"] = Int(0, MaxCompareWindow, "Lines before and after each line."),
            }, "first", "second", "window"));

    public static string DefinitionsSha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Definitions().ToJsonString(Wire))));

    // ---------------- handlers ----------------

    (JsonNode?, JsonNode?, bool) Overview(JsonElement args)
    {
        Keys(args);
        var pages = lines.Where(l => l.Page is not null).Select(l => l.Page!.Value).Distinct().Order().ToArray();
        var sizes = lines.Select(l => Evidence.FontSizeOf(l.Typography)).Where(s => s is not null).Select(s => Math.Round(s!.Value, 1, MidpointRounding.AwayFromZero))
            .GroupBy(s => s).Select(g => (Size: g.Key, Lines: g.Count())).OrderByDescending(g => g.Lines).ThenByDescending(g => g.Size).ToArray();
        var shownSizes = sizes.Take(MaxFontSizes).OrderByDescending(s => s.Size).ToArray();
        var (result, taken) = Fit(DocumentOverview, new JsonObject(), pages.Length, k => new JsonObject
        {
            ["lineCount"] = lines.Count, ["pageCount"] = pages.Length, ["unplacedLines"] = lines.Count(l => l.Page is null),
            ["fontSizes"] = new JsonArray(shownSizes.Select(s => (JsonNode)new JsonObject { ["size"] = s.Size, ["lines"] = s.Lines }).ToArray()),
            ["fontSizesShown"] = shownSizes.Length, ["fontSizesInDocument"] = sizes.Length,
            ["pages"] = new JsonArray(pages.Take(k).Select(p => Node(Evidence.PageStats(lines, p))).ToArray()),
            ["pagesTruncation"] = Truncation(k, pages.Length, k),
            ["basis"] = Evidence.LayoutBasis,
        });
        return (new JsonObject(), result, taken < pages.Length || shownSizes.Length < sizes.Length);
    }

    (JsonNode?, JsonNode?, bool) Context(JsonElement args, List<string> returned)
    {
        Keys(args, "alias", "before", "after");
        var alias = Alias(args, "alias");
        int before = Range(args, "before", 0, MaxNeighbours), after = Range(args, "after", 0, MaxNeighbours);
        var at = index[alias]; var self = lines[at];
        var window = Enumerable.Range(Math.Max(0, at - before), Math.Min(lines.Count, at + after + 1) - Math.Max(0, at - before)).ToArray();
        var peers = self.Page is null ? Array.Empty<int>() : Enumerable.Range(0, lines.Count)
            .Where(i => i != at && lines[i].Page == self.Page && Bottom(lines[i]) is { } b && Bottom(self) is { } sb && Math.Abs(b - sb) <= Evidence.SameBaselineTolerance)
            .Take(8).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["before"] = before, ["after"] = after };
        var (result, taken) = Fit(SourceContext, arguments, peers.Length, k => new JsonObject
        {
            ["alias"] = alias, ["page"] = self.Page, ["pageStats"] = self.Page is { } p ? Node(Evidence.PageStats(lines, p)) : null,
            ["lines"] = new JsonArray(window.Select(i => (JsonNode)LineNode(i, MaxListTextChars, relativeTo: at)).ToArray()),
            ["sameBaselinePeers"] = new JsonArray(peers.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars)).ToArray()),
            ["peersTruncation"] = Truncation(k, peers.Length, null),
            ["basis"] = Evidence.LayoutBasis,
        });
        returned.AddRange(window.Select(i => lines[i].Alias));
        returned.AddRange(peers.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < peers.Length);
    }

    (JsonNode?, JsonNode?, bool) Page(JsonElement args, List<string> returned)
    {
        Keys(args, "page", "offset", "limit");
        int page = Range(args, "page", 1, 100000), offset = Range(args, "offset", 0, 100000), limit = Range(args, "limit", 1, MaxPageLines);
        var onPage = Enumerable.Range(0, lines.Count).Where(i => lines[i].Page == page).ToArray();
        if (onPage.Length == 0) throw new ArgumentError("PAGE_NOT_IN_DOCUMENT");
        var slice = onPage.Skip(offset).Take(limit).ToArray();
        var arguments = new JsonObject { ["page"] = page, ["offset"] = offset, ["limit"] = limit };
        var (result, taken) = Fit(PageLayout, arguments, slice.Length, k => new JsonObject
        {
            ["page"] = page, ["pageLineCount"] = onPage.Length, ["pageStats"] = Node(Evidence.PageStats(lines, page)), ["offset"] = offset,
            ["lines"] = new JsonArray(slice.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars)).ToArray()),
            ["hasMore"] = offset + k < onPage.Length, ["nextOffset"] = offset + k < onPage.Length ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
            ["basis"] = Evidence.LayoutBasis,
        });
        returned.AddRange(slice.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < slice.Length);
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
        var aliases = slice.Select(i => lines[i].Alias).ToList();
        var repeated = aliases.Count == 0 ? new Dictionary<string, JsonObject>() :
            Prefetch(aliases).Observations["targets"]!.AsArray().ToDictionary(t => t!["alias"]!.GetValue<string>(), t => t!.AsObject(), StringComparer.Ordinal);
        JsonNode Item(int i)
        {
            var o = (JsonObject)layoutFacts![lines[i].Alias].DeepClone();
            o["page"] = lines[i].Page;
            if (repeated.TryGetValue(lines[i].Alias, out var r))
            {
                var copy = (JsonObject)r.DeepClone();
                copy.Remove("alias");
                o["repeatedText"] = copy;
            }
            return o;
        }
        IEnumerable<string> Shown(int i) => repeated.TryGetValue(lines[i].Alias, out var r)
            ? r["shown"]!.AsArray().SelectMany(x => new[] { x!["alias"]!.GetValue<string>() }.Concat(x["followingLine"] is JsonObject f ? new[] { f["alias"]!.GetValue<string>() } : Array.Empty<string>()))
            : Array.Empty<string>();
        var (result, taken) = Fit(LineFacts, arguments, slice.Length, k => new JsonObject
        {
            ["readBy"] = "the program, from the parser's geometry and from the text of the whole document: " + AgentLayoutFacts.DescriptionV3 + "; repeatedText lists the first other occurrences of the line's text under the stated normalization",
            ["version"] = AgentLayoutFacts.VersionV3,
            ["normalization"] = Evidence.RepeatNormalization,
            ["rangeLines"] = rangeLength, ["offset"] = offset,
            ["facts"] = new JsonArray(slice.Take(k).Select(Item).ToArray()),
            ["hasMore"] = offset + k < rangeLength, ["nextOffset"] = offset + k < rangeLength ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).SelectMany(i => Shown(i).Prepend(lines[i].Alias)));
        return (arguments, result, taken < slice.Length);
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
            ["records"] = new JsonArray(aliases.Take(k).Select(a => (JsonNode)LineNode(index[a], MaxSpanTextChars)).ToArray()),
            ["truncation"] = Truncation(k, aliases.Count, null),
            ["basis"] = Evidence.LayoutBasis,
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
        // An empty key (a line of digits, dots or spaces only) matches every such line; none is reported as a repeat.
        var matches = key.Length == 0 ? Array.Empty<int>() : repeatIndex[key].Where(i => i != at).ToArray();
        var slice = matches.Take(max).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["maxResults"] = max };
        var (result, taken) = Fit(Repeated, arguments, slice.Length, k => new JsonObject
        {
            ["alias"] = alias, ["page"] = self.Page, ["text"] = Clip(self.Text, MaxListTextChars), ["normalization"] = Evidence.RepeatNormalization,
            ["normalizedKeyEmpty"] = key.Length == 0, ["totalMatches"] = matches.Length,
            ["distinctPagesWithMatches"] = matches.Select(i => lines[i].Page).Distinct().Count(),
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
            var at = index[alias]; var self = lines[at];
            return new JsonObject
            {
                ["alias"] = alias, ["page"] = self.Page, ["readingOrder"] = at, ["pageStats"] = self.Page is { } p ? Node(Evidence.PageStats(lines, p)) : null,
                ["normalizedText"] = Evidence.RepeatKey(self.Text),
                ["lines"] = new JsonArray(Around(at).Select(i => (JsonNode)LineNode(i, MaxListTextChars, relativeTo: at)).ToArray()),
            };
        }
        var a = lines[index[first]]; var b = lines[index[second]];
        double? D(double? x, double? y) => x is { } xv && y is { } yv ? Math.Round(yv - xv, 1, MidpointRounding.AwayFromZero) : null;
        var arguments = new JsonObject { ["first"] = first, ["second"] = second, ["window"] = window };
        var result = new JsonObject
        {
            ["first"] = Side(first), ["second"] = Side(second),
            ["delta"] = new JsonObject
            {
                ["samePage"] = a.Page == b.Page, ["pageDistance"] = a.Page is { } pa && b.Page is { } pb ? pb - pa : null,
                ["readingOrderDistance"] = index[second] - index[first],
                ["sameNormalizedText"] = Evidence.RepeatKey(a.Text) == Evidence.RepeatKey(b.Text), ["exactTextMatch"] = a.Text == b.Text,
                ["fontSizeDelta"] = D(Evidence.FontSizeOf(a.Typography), Evidence.FontSizeOf(b.Typography)),
                ["boldRatioDelta"] = D(Evidence.Bold(a.Typography), Evidence.Bold(b.Typography)),
                ["leftEdgeDelta"] = D(Evidence.LeftEdgeOf(a.Bbox), Evidence.LeftEdgeOf(b.Bbox)),
            },
            ["basis"] = Evidence.LayoutBasis,
        };
        // Two windows of at most 7 lines of 300 characters each fit the cap; checked anyway.
        if (Body("E0.00", Compare, "OK", null, arguments, result).Length + ResultMargin > MaxResultBytes) throw new InvalidOperationException("V6_TOOL_COMPARE_OVER_CAP");
        returned.AddRange(Around(index[first]).Concat(Around(index[second])).Select(i => lines[i].Alias));
        return (arguments, result, false);
    }

    // ---------------- prefetched observations (D4.11h) ----------------

    public const int PrefetchPerTarget = 2, PrefetchMaxBytes = 48_000, PrefetchTextChars = 100;

    /// <param name="Observations">The object that goes into the request.</param>
    /// <param name="ShownAliases">Every alias the observations show (other occurrences and the line after each).</param>
    /// <param name="Listed">Targets that have another occurrence and are listed.</param>
    /// <param name="WithoutOther">Targets with no other occurrence of the same normalized text.</param>
    /// <param name="Omitted">Targets that have another occurrence but did not fit the byte cap (0 when the list is complete).</param>
    public sealed record PrefetchResult(JsonObject Observations, int Bytes, IReadOnlyList<string> ShownAliases, int Listed, int WithoutOther, int Omitted, int PerTarget);

    /// <summary>
    /// The neutral read that the program runs itself, instead of waiting for the model to ask (D4.11h): for every target whose normalized text
    /// (the repeat normalization of get_repeated_occurrences) also occurs on another line of the document, the first <see cref="PrefetchPerTarget"/>
    /// other occurrences in reading order with their page, reading-order position, layout, how their font and left edge differ from the target's,
    /// and the line that follows each. The only rule is "the text occurs elsewhere"; it says nothing about what any line is. A target with no
    /// other occurrence is only counted. If the list would exceed the byte cap it is first cut to one occurrence per target and then by whole
    /// targets from the end, and the result says so (complete false, omittedTargets).
    /// </summary>
    readonly IReadOnlyDictionary<string, JsonObject>? layoutFacts;

    /// <summary>D4.12l: the style table of the whole document.</summary>
    public override JsonObject DocumentStyles() => AgentLayoutFacts.DocumentStyles(lines);

    public PrefetchResult Prefetch(IReadOnlyList<string> targets)
    {
        var listed = new List<(int At, int[] Others)>();
        var without = 0;
        foreach (var a in targets)
        {
            var at = index[a];
            var key = Evidence.RepeatKey(lines[at].Text);
            var others = key.Length == 0 ? Array.Empty<int>() : repeatIndex[key].Where(i => i != at).ToArray();
            if (others.Length == 0) without++; else listed.Add((at, others));
        }

        JsonObject Build(int perTarget, int take)
        {
            JsonObject Other(int at, int j)
            {
                var o = lines[j];
                var t = lines[at];
                double? D(double? x, double? y) => x is { } xv && y is { } yv ? Math.Round(yv - xv, 1, MidpointRounding.AwayFromZero) : null;
                var n = new JsonObject { ["alias"] = o.Alias, ["page"] = o.Page, ["readingOrder"] = j, ["distanceFromTarget"] = j - at, ["exactTextMatch"] = o.Text == t.Text };
                if (o.Text != t.Text) n["text"] = Clip(o.Text, PrefetchTextChars);
                n["layout"] = Node(Evidence.CompactLayout(o.Bbox, o.Typography));
                n["delta"] = new JsonObject
                {
                    ["fontSize"] = D(Evidence.FontSizeOf(t.Typography), Evidence.FontSizeOf(o.Typography)),
                    ["boldRatio"] = D(Evidence.Bold(t.Typography), Evidence.Bold(o.Typography)),
                    ["leftEdge"] = D(Evidence.LeftEdgeOf(t.Bbox), Evidence.LeftEdgeOf(o.Bbox)),
                };
                if (j + 1 < lines.Count) n["followingLine"] = new JsonObject { ["alias"] = lines[j + 1].Alias, ["page"] = lines[j + 1].Page, ["text"] = Clip(lines[j + 1].Text, PrefetchTextChars) };
                return n;
            }
            var items = listed.Take(take).Select(x => (JsonNode)new JsonObject
            {
                ["alias"] = lines[x.At].Alias, ["otherOccurrences"] = x.Others.Length,
                ["pagesWithOtherOccurrences"] = x.Others.Select(i => lines[i].Page).Distinct().Count(),
                ["shown"] = new JsonArray(x.Others.Take(perTarget).Select(j => (JsonNode)Other(x.At, j)).ToArray()),
            }).ToArray();
            var o = new JsonObject
            {
                ["readBy"] = "the program, not requested by the model: for every target whose text also occurs on another line of the document it ran the get_repeated_occurrences read, and for each occurrence shown it read the line that follows",
                ["normalization"] = Evidence.RepeatNormalization,
                ["shownPerTarget"] = perTarget,
                ["complete"] = take == listed.Count,
                ["targetsWithoutOtherOccurrence"] = without,
                ["targets"] = new JsonArray(items),
            };
            if (take < listed.Count) o["omittedTargets"] = listed.Count - take;
            return o;
        }

        int Size(JsonObject o) => Encoding.UTF8.GetByteCount(o.ToJsonString(Wire));
        var perTarget = PrefetchPerTarget;
        var take = listed.Count;
        var obs = Build(perTarget, take);
        if (Size(obs) > PrefetchMaxBytes) { perTarget = 1; obs = Build(perTarget, take); }
        while (Size(obs) > PrefetchMaxBytes && take > 0) { take--; obs = Build(perTarget, take); }
        var shown = obs["targets"]!.AsArray().SelectMany(t => t!["shown"]!.AsArray().SelectMany(x =>
            new[] { x!["alias"]!.GetValue<string>() }.Concat(x["followingLine"] is JsonObject f ? new[] { f["alias"]!.GetValue<string>() } : Array.Empty<string>())))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new PrefetchResult(obs, Size(obs), shown, take, without, listed.Count - take, perTarget);
    }

    // ---------------- observations ----------------

    JsonObject LineNode(int i, int maxText, int? relativeTo = null)
    {
        var l = lines[i];
        var o = new JsonObject { ["alias"] = l.Alias, ["page"] = l.Page, ["readingOrder"] = i };
        if (relativeTo is { } r) o["relative"] = i - r;
        o["text"] = Clip(l.Text, maxText);
        if (l.Text.Length > maxText) { o["textTruncated"] = true; o["textChars"] = l.Text.Length; }
        o["layout"] = Node(Evidence.CompactLayout(l.Bbox, l.Typography));
        if (relativeTo is { } t && t != i) o["relationToLine"] = Node(Evidence.RelationOf(lines[t], l));
        return o;
    }

    static double? Bottom(AgentSourceLine l) =>
        l.Bbox is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("bottom", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

}
