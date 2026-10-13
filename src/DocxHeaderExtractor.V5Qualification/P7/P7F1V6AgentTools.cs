using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_V6_AGENT_TOOLS_V1 (Issue #9 D4.11, offline). The registry and dispatcher of the read-only evidence tools Qwen may call through
/// native OpenRouter tool_calls. Every tool reads the parser's record of the pinned source (text, page, reading order, bbox, typography) from
/// the same <see cref="P7F1QEvidenceTools"/> the universe was built from; nothing is computed from Gold, from earlier replies or from heading
/// vocabulary, and no result labels, ranks or filters a line. The F1Q primitives are reused unchanged (layout, page statistics, spatial
/// relation, repeat normalisation); search uses the V6 read-tool normalisation. Descriptions are neutral: they say what a tool returns, never
/// which kind of line it is meant to find.
/// C# validates every argument (strict keys, types, ranges, aliases of this document), calls the one handler the name selects, and returns an
/// exact JSON body of at most <see cref="MaxResultBytes"/> bytes. A result that would not fit is cut by whole items and says so
/// (truncated, returned, available, nextOffset); it is never silently shortened. Every call, rejected ones included, is recorded in the
/// ledger, with the aliases the result showed: that set is what Qwen observed through tools.
/// </summary>
internal sealed class P7F1V6AgentTools
{
    public const string Version = "P7_F1_V6_AGENT_TOOLS_V1";
    public const int MaxResultBytes = 16384, MaxArgumentBytes = 2048, MaxAliasChars = 64, MaxNeighbours = 6, MaxPageLines = 40,
        MaxSpanAliases = 8, MaxRepeated = 20, MaxCompareWindow = 3, MaxSearchLimit = 50, MaxQueryChars = 200, MinQueryChars = 2,
        MaxListTextChars = 300, MaxSpanTextChars = 4000, MaxFontSizes = 16;

    public const string DocumentOverview = "get_document_overview", SourceContext = "get_source_context", PageLayout = "get_page_layout",
        SourceSpan = "get_source_span", SearchText = "search_document_text", Repeated = "get_repeated_occurrences", Compare = "compare_source_locations";

    public static readonly string[] Names = [DocumentOverview, SourceContext, PageLayout, SourceSpan, SearchText, Repeated, Compare];

    /// <summary>Where each tool comes from. Issue #5 names its four tools; the others are the F1Q V2 comparison, the V6 D1 search and a new
    /// overview. get_page_image is not registered: no image of a page exists in the V6 evidence store, and image input on the pinned route
    /// has not been verified.</summary>
    public static readonly IReadOnlyDictionary<string, string> Lineage = new Dictionary<string, string>
    {
        [DocumentOverview] = "NEW (D4.11): per-page counts and font-size histogram from P7F1QEvidenceTools.PageStats",
        [SourceContext] = "Issue #5 get_occurrence_context (F1Q tool set V2 context: layout, relation to target, same-baseline peers, page statistics)",
        [PageLayout] = "Issue #5 get_page_geometry (F1Q get_page_geometry: page lines with bbox and font)",
        [SourceSpan] = "Issue #5 get_source_span (F1Q get_source_span: full parser record per alias)",
        [SearchText] = "V6 D1 search_document_text (P7F1V6Tools.Normalize for the normalized mode)",
        [Repeated] = "Issue #5 get_repeated_occurrences (F1Q RepeatKey normalisation; F1Q description not reused: it named the kinds of line to look for)",
        [Compare] = "F1Q V2 compare_occurrences (same sides and deltas; F1Q description not reused: it named the kinds of line to compare)",
    };

    public const string PageImageStatus = "NOT_REGISTERED: the V6 evidence store holds no page image and image input on the pinned route is not verified";

    /// <summary>One call as the ledger keeps it. RawArgumentsSha256 is over the argument string exactly as Qwen sent it.</summary>
    /// <param name="CacheOf">The evidence id of the earlier call whose result this call repeated, or null (D4.11f).</param>
    public sealed record CallRecord(string EvidenceId, string Tool, string RawArgumentsSha256, string Status, string? ErrorCode,
        string ContentSha256, int ContentBytes, bool Truncated, IReadOnlyList<string> ReturnedAliases, string? CacheOf = null);

    /// <summary>Content is the exact body that goes back to Qwen as the tool message.</summary>
    public sealed record Result(string EvidenceId, string Tool, string Status, string? ErrorCode, byte[] Content, string ContentSha256,
        bool Truncated, IReadOnlyList<string> ReturnedAliases);

    static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly P7F1QEvidenceTools source;
    readonly IReadOnlyList<F1QSourceLine> lines;
    readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
    readonly ILookup<string, int> repeatIndex;
    readonly List<CallRecord> ledger = new();
    readonly HashSet<string> observed = new(StringComparer.Ordinal);
    // Result cache (D4.11f). The tools read a fixed source, so the same call gives the same result. Only successful calls are kept, keyed by
    // the tool and its arguments in canonical form (keys sorted); a repeat returns the stored result, byte for byte but for the evidence id,
    // and says so. The cache lives as long as this registry, i.e. one unit's conversation; nothing is shared between units or arms.
    sealed record Cached(string EvidenceId, JsonNode? Arguments, JsonNode? Result, bool Truncated, string[] Returned);
    readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    public int CacheHits => ledger.Count(c => c.CacheOf is not null);
    public const int ResultMargin = 192;   // room, inside the result cap, for a longer evidence id and the cache note of a repeated call

    static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        _ => e.GetRawText(),
    };

    /// <summary>Codes of a call refused before dispatch. Every one is answered under the call's own id.</summary>
    public static readonly string[] RejectionCodes = ["PER_TURN_CALL_CAP_EXCEEDED", "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED"];

    /// <summary>The name a result and the ledger carry: a registered name, or null. A name Qwen made up is never echoed back, so a result's
    /// size does not depend on it (D4.11b).</summary>
    static string? Registered(string? name) => name is not null && Names.Contains(name, StringComparer.Ordinal) ? name : null;

    public P7F1V6AgentTools(P7F1QEvidenceTools source, IReadOnlyDictionary<string, JsonObject>? layoutFacts = null)
    {
        this.source = source;
        this.layoutFacts = layoutFacts;
        lines = source.AllLines;
        for (int i = 0; i < lines.Count; i++) index[lines[i].Alias] = i;
        repeatIndex = Enumerable.Range(0, lines.Count).ToLookup(i => P7F1QEvidenceTools.RepeatKey(lines[i].Text), StringComparer.Ordinal);
    }

    public IReadOnlyList<CallRecord> Ledger => ledger;
    /// <summary>Every alias a successful tool result showed, in any part of the result.</summary>
    public IReadOnlySet<string> ObservedAliases => observed;
    public int DocumentLines => lines.Count;
    public int PositionOf(string alias) => index[alias];

    // ---------------- declarations ----------------

    public static JsonArray Definitions() => new(
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

    static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["additionalProperties"] = false,
            },
        },
    };

    static JsonObject AliasArg(string description) => new() { ["type"] = "string", ["maxLength"] = MaxAliasChars, ["description"] = description + " An alias from the request or from a tool result." };
    static JsonObject Int(int min, int max, string description) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["description"] = description };

    // ---------------- dispatch ----------------

    sealed class ArgumentError(string code) : Exception(code) { public string Code { get; } = code; }

    /// <summary>Validates the arguments, runs the one handler the name selects and records the call. Never throws for anything Qwen sends.</summary>
    public Result Execute(string evidenceId, string? name, string? rawArguments)
    {
        var raw = rawArguments ?? "";
        var returned = new List<string>();
        JsonNode? arguments = null, result = null;
        string status = "OK"; string? error = null; var truncated = false; string? cacheOf = null; string? key = null;
        try
        {
            if (name is null || !Names.Contains(name, StringComparer.Ordinal)) throw new ArgumentError("UNKNOWN_TOOL");
            using var doc = ParseArguments(raw);
            var args = doc.RootElement;
            key = name + "\n" + Canonical(args);
            if (cache.TryGetValue(key, out var hit))
            {
                (arguments, result, truncated, cacheOf) = (hit.Arguments, hit.Result, hit.Truncated, hit.EvidenceId);
                returned.AddRange(hit.Returned);
            }
            else
                (arguments, result, truncated) = name switch
                {
                    DocumentOverview => Overview(args),
                    SourceContext => Context(args, returned),
                    PageLayout => Page(args, returned),
                    SourceSpan => Span(args, returned),
                    SearchText => Search(args, returned),
                    Repeated => Repeat(args, returned),
                    _ => CompareTwo(args, returned),
                };
        }
        catch (ArgumentError e) { status = name is null || !Names.Contains(name, StringComparer.Ordinal) ? "UNKNOWN_TOOL" : "INVALID_ARGUMENT"; error = e.Code; result = null; returned.Clear(); truncated = false; cacheOf = null; key = null; }
        var content = Body(evidenceId, Registered(name), status, error, arguments, result, cacheOf);
        if (content.Length > MaxResultBytes) throw new InvalidOperationException("V6_TOOL_RESULT_CAP_EXCEEDED");   // the handlers fit before this point
        var aliases = returned.Distinct(StringComparer.Ordinal).ToArray();
        if (status == "OK") foreach (var a in aliases) observed.Add(a);
        if (status == "OK" && key is not null && cacheOf is null) cache[key] = new Cached(evidenceId, arguments, result, truncated, aliases);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        ledger.Add(new CallRecord(evidenceId, Registered(name) ?? "UNREGISTERED", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), status, error,
            sha, content.Length, truncated, aliases, cacheOf));
        return new Result(evidenceId, Registered(name) ?? "UNREGISTERED", status, error, content, sha, truncated, aliases);
    }

    /// <summary>A call the loop refuses before dispatch (over the per-turn cap). It is answered and recorded like any other call.</summary>
    public Result Reject(string evidenceId, string? name, string? rawArguments, string code)
    {
        if (!RejectionCodes.Contains(code, StringComparer.Ordinal)) throw new ArgumentException("REJECTION_CODE_UNKNOWN", nameof(code));
        var content = Body(evidenceId, Registered(name), "REJECTED", code, null, null);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        ledger.Add(new CallRecord(evidenceId, Registered(name) ?? "UNREGISTERED", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawArguments ?? ""))),
            "REJECTED", code, sha, content.Length, false, Array.Empty<string>()));
        return new Result(evidenceId, Registered(name) ?? "UNREGISTERED", "REJECTED", code, content, sha, false, Array.Empty<string>());
    }

    /// <summary>The exact size of the largest rejection this call could receive. Used to reserve room before any call of a turn runs.</summary>
    public int RejectionBytes(string evidenceId, string? name) => RejectionCodes.Max(c => Body(evidenceId, Registered(name), "REJECTED", c, null, null).Length);

    byte[] Body(string evidenceId, string? name, string status, string? error, JsonNode? arguments, JsonNode? result, string? cacheOf = null)
    {
        var body = new JsonObject
        {
            ["evidenceId"] = evidenceId, ["tool"] = name, ["version"] = Version, ["status"] = status, ["errorCode"] = error,
            ["arguments"] = arguments?.DeepClone(), ["result"] = result?.DeepClone(),
            ["provenance"] = new JsonObject
            {
                ["sourceSha256"] = source.SourceSha256, ["sourceAliasUniverseSha256"] = source.SourceAliasUniverseSha256,
                ["evidenceStoreSha256"] = source.EvidenceStoreSha256, ["readOnly"] = true, ["semanticLabels"] = "NONE",
            },
        };
        // Only a repeated call carries this note, so every other result keeps its bytes. The result above is the stored one.
        if (cacheOf is not null) body["cache"] = new JsonObject { ["hit"] = true, ["sameResultAs"] = cacheOf };
        return Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
    }

    /// <summary>The largest prefix of the items whose exact body fits the cap. build(k) makes the result with the first k items.</summary>
    (JsonNode Result, int Taken) Fit(string tool, JsonNode? arguments, int available, Func<int, JsonObject> build)
    {
        for (int k = available; k >= 0; k--)
        {
            var r = build(k);
            if (Body("E0.00", tool, "OK", null, arguments, r).Length + ResultMargin <= MaxResultBytes) return (r, k);   // the margin leaves room for a longer evidence id and the cache note
        }
        throw new InvalidOperationException("V6_TOOL_EMPTY_RESULT_OVER_CAP");
    }

    static JsonObject Truncation(int returned, int available, int? nextOffset) => new()
    {
        ["truncated"] = returned < available, ["returned"] = returned, ["available"] = available, ["nextOffset"] = returned < available ? nextOffset : null,
    };

    // ---------------- handlers ----------------

    (JsonNode?, JsonNode?, bool) Overview(JsonElement args)
    {
        Keys(args);
        var pages = lines.Where(l => l.Page is not null).Select(l => l.Page!.Value).Distinct().Order().ToArray();
        var sizes = lines.Select(l => P7F1QEvidenceTools.FontSizeOf(l.Typography)).Where(s => s is not null).Select(s => Math.Round(s!.Value, 1, MidpointRounding.AwayFromZero))
            .GroupBy(s => s).Select(g => (Size: g.Key, Lines: g.Count())).OrderByDescending(g => g.Lines).ThenByDescending(g => g.Size).ToArray();
        var shownSizes = sizes.Take(MaxFontSizes).OrderByDescending(s => s.Size).ToArray();
        var (result, taken) = Fit(DocumentOverview, new JsonObject(), pages.Length, k => new JsonObject
        {
            ["lineCount"] = lines.Count, ["pageCount"] = pages.Length, ["unplacedLines"] = lines.Count(l => l.Page is null),
            ["fontSizes"] = new JsonArray(shownSizes.Select(s => (JsonNode)new JsonObject { ["size"] = s.Size, ["lines"] = s.Lines }).ToArray()),
            ["fontSizesShown"] = shownSizes.Length, ["fontSizesInDocument"] = sizes.Length,
            ["pages"] = new JsonArray(pages.Take(k).Select(p => Node(source.PageStats(p))).ToArray()),
            ["pagesTruncation"] = Truncation(k, pages.Length, k),
            ["basis"] = P7F1QEvidenceTools.LayoutBasis,
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
            .Where(i => i != at && lines[i].Page == self.Page && Bottom(lines[i]) is { } b && Bottom(self) is { } sb && Math.Abs(b - sb) <= P7F1QEvidenceTools.SameBaselineTolerance)
            .Take(8).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["before"] = before, ["after"] = after };
        var (result, taken) = Fit(SourceContext, arguments, peers.Length, k => new JsonObject
        {
            ["alias"] = alias, ["page"] = self.Page, ["pageStats"] = self.Page is { } p ? Node(source.PageStats(p)) : null,
            ["lines"] = new JsonArray(window.Select(i => (JsonNode)LineNode(i, MaxListTextChars, relativeTo: at)).ToArray()),
            ["sameBaselinePeers"] = new JsonArray(peers.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars)).ToArray()),
            ["peersTruncation"] = Truncation(k, peers.Length, null),
            ["basis"] = P7F1QEvidenceTools.LayoutBasis,
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
            ["page"] = page, ["pageLineCount"] = onPage.Length, ["pageStats"] = Node(source.PageStats(page)), ["offset"] = offset,
            ["lines"] = new JsonArray(slice.Take(k).Select(i => (JsonNode)LineNode(i, MaxListTextChars)).ToArray()),
            ["hasMore"] = offset + k < onPage.Length, ["nextOffset"] = offset + k < onPage.Length ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
            ["basis"] = P7F1QEvidenceTools.LayoutBasis,
        });
        returned.AddRange(slice.Take(taken).Select(i => lines[i].Alias));
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
            ["basis"] = P7F1QEvidenceTools.LayoutBasis,
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
        var needle = mode == "normalized" ? P7F1V6Tools.Normalize(query) : query;
        if (needle.Length == 0) throw new ArgumentError("QUERY_TOO_SHORT");
        var matches = Enumerable.Range(0, lines.Count)
            .Where(i => (mode == "normalized" ? P7F1V6Tools.Normalize(lines[i].Text) : lines[i].Text).Contains(needle, StringComparison.Ordinal)).ToArray();
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
        var key = P7F1QEvidenceTools.RepeatKey(self.Text);
        // An empty key (a line of digits, dots or spaces only) matches every such line; none is reported as a repeat.
        var matches = key.Length == 0 ? Array.Empty<int>() : repeatIndex[key].Where(i => i != at).ToArray();
        var slice = matches.Take(max).ToArray();
        var arguments = new JsonObject { ["alias"] = alias, ["maxResults"] = max };
        var (result, taken) = Fit(Repeated, arguments, slice.Length, k => new JsonObject
        {
            ["alias"] = alias, ["page"] = self.Page, ["text"] = Clip(self.Text, MaxListTextChars), ["normalization"] = P7F1QEvidenceTools.RepeatNormalization,
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
                ["alias"] = alias, ["page"] = self.Page, ["readingOrder"] = at, ["pageStats"] = self.Page is { } p ? Node(source.PageStats(p)) : null,
                ["normalizedText"] = P7F1QEvidenceTools.RepeatKey(self.Text),
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
                ["sameNormalizedText"] = P7F1QEvidenceTools.RepeatKey(a.Text) == P7F1QEvidenceTools.RepeatKey(b.Text), ["exactTextMatch"] = a.Text == b.Text,
                ["fontSizeDelta"] = D(P7F1QEvidenceTools.FontSizeOf(a.Typography), P7F1QEvidenceTools.FontSizeOf(b.Typography)),
                ["boldRatioDelta"] = D(P7F1QEvidenceTools.Bold(a.Typography), P7F1QEvidenceTools.Bold(b.Typography)),
                ["leftEdgeDelta"] = D(P7F1QEvidenceTools.LeftEdgeOf(a.Bbox), P7F1QEvidenceTools.LeftEdgeOf(b.Bbox)),
            },
            ["basis"] = P7F1QEvidenceTools.LayoutBasis,
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

    /// <summary>D4.12: the layout facts of the lines a request shows, in the order shown. Needs the facts the universe computed.</summary>
    public JsonObject LayoutFactsOf(IReadOnlyList<string> shownAliases)
    {
        if (layoutFacts is null) throw new InvalidOperationException("LAYOUT_FACTS_NOT_PROVIDED");
        return new JsonObject
        {
            ["readBy"] = "the program, from the parser's geometry: " + P7F1V6LayoutFacts.Description,
            ["version"] = P7F1V6LayoutFacts.Version,
            ["lines"] = new JsonArray(shownAliases.Select(a => (JsonNode)layoutFacts[a].DeepClone()).ToArray()),
        };
    }

    public PrefetchResult Prefetch(IReadOnlyList<string> targets)
    {
        var listed = new List<(int At, int[] Others)>();
        var without = 0;
        foreach (var a in targets)
        {
            var at = index[a];
            var key = P7F1QEvidenceTools.RepeatKey(lines[at].Text);
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
                n["layout"] = Node(P7F1QEvidenceTools.CompactLayout(o.Bbox, o.Typography));
                n["delta"] = new JsonObject
                {
                    ["fontSize"] = D(P7F1QEvidenceTools.FontSizeOf(t.Typography), P7F1QEvidenceTools.FontSizeOf(o.Typography)),
                    ["boldRatio"] = D(P7F1QEvidenceTools.Bold(t.Typography), P7F1QEvidenceTools.Bold(o.Typography)),
                    ["leftEdge"] = D(P7F1QEvidenceTools.LeftEdgeOf(t.Bbox), P7F1QEvidenceTools.LeftEdgeOf(o.Bbox)),
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
                ["normalization"] = P7F1QEvidenceTools.RepeatNormalization,
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
        o["layout"] = Node(P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography));
        if (relativeTo is { } t && t != i) o["relationToLine"] = Node(source.RelationOf(lines[t], l));
        return o;
    }

    static double? Bottom(F1QSourceLine l) =>
        l.Bbox is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("bottom", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static JsonNode? Node(object? o) => o is null ? null : JsonSerializer.SerializeToNode(o, Wire);
    static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    // ---------------- argument checks ----------------

    static JsonDocument ParseArguments(string raw)
    {
        // A tool without parameters may be called with an empty argument string; it means {}.
        if (string.IsNullOrWhiteSpace(raw)) return JsonDocument.Parse("{}");
        if (Encoding.UTF8.GetByteCount(raw) > MaxArgumentBytes) throw new ArgumentError("ARGUMENTS_TOO_LARGE");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { throw new ArgumentError("ARGUMENTS_NOT_JSON"); }
        if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw new ArgumentError("ARGUMENTS_NOT_OBJECT"); }
        return doc;
    }

    static void Keys(JsonElement args, params string[] required)
    {
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count() || names.Any(n => !required.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentError("ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE");
        if (required.Any(r => !names.Contains(r, StringComparer.Ordinal))) throw new ArgumentError("ARGUMENT_REQUIRED_MISSING");
    }

    static string Str(JsonElement args, string key) =>
        args.GetProperty(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new ArgumentError("ARGUMENT_TYPE_INVALID");

    string Alias(JsonElement args, string key)
    {
        var s = Str(args, key);
        if (s.Length > MaxAliasChars || !index.ContainsKey(s)) throw new ArgumentError("ALIAS_NOT_IN_DOCUMENT");
        return s;
    }

    static int Range(JsonElement args, string key, int min, int max)
    {
        var v = args.GetProperty(key);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) throw new ArgumentError("ARGUMENT_TYPE_INVALID");
        if (n < min || n > max) throw new ArgumentError("ARGUMENT_OUT_OF_RANGE");
        return n;
    }
}
