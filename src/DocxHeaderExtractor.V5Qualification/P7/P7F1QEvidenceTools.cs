using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>One parser-owned source line as the read-only tools see it. Geometry and typography are
/// observations copied from <see cref="PdfSourceEvidenceStore"/>; nothing here is a heading label.</summary>
internal sealed record F1QSourceLine(string Alias, int Ordinal, int? Page, string Text, JsonElement? Bbox, JsonElement? Typography);

internal sealed record F1QToolResult(string EvidenceId, string Name, string RawArguments, string Status,
    string? ErrorCode, byte[] Content, string ContentSha256, IReadOnlyList<string> ReturnedAliases);

/// <summary>
/// Frozen, read-only evidence tools for the P7-F1Q experiment. Arguments are strictly typed and bounded;
/// targets must be an issued O# of the current request or a source alias of the same document universe.
/// Results are deterministic JSON with provenance. Tools never return semantic labels or Gold.
/// </summary>
internal sealed class P7F1QEvidenceTools
{
    public const string Version = "P7_F1Q_READ_ONLY_EVIDENCE_TOOLS_V1";
    public const string RepeatNormalization = "LOWER_INVARIANT_DOT_LEADERS_REMOVED_TRAILING_PAGE_NUMBER_REMOVED_DIGITS_MASKED_WHITESPACE_COLLAPSED_V1";
    public const int MaxNeighbors = 6, MaxGeometryLines = 40, MaxSpanTargets = 8, MaxRepeated = 20, ResultByteCap = 16384, MaxTextChars = 160;
    public static readonly string[] Names = ["get_occurrence_context", "get_page_geometry", "get_source_span", "get_repeated_occurrences"];

    internal static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<F1QSourceLine> lines;
    private readonly Dictionary<string, int> indexByAlias;
    private readonly IReadOnlyDictionary<string, string> aliasByOccurrence;
    private readonly Dictionary<string, string> occurrenceByAlias;
    private readonly ILookup<string, int> repeatIndex;
    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }

    public P7F1QEvidenceTools(string sourceSha256, string universeSha256, string storeSha256,
        IReadOnlyList<F1QSourceLine> documentLines, IReadOnlyDictionary<string, string> issuedAliasByOccurrence)
    {
        lines = documentLines.OrderBy(l => l.Ordinal).ThenBy(l => l.Alias, StringComparer.Ordinal).ToArray();
        indexByAlias = new(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
            if (!indexByAlias.TryAdd(lines[i].Alias, i)) throw new InvalidOperationException("F1Q_TOOL_ALIAS_DUPLICATE");
        if (issuedAliasByOccurrence.Count == 0 || issuedAliasByOccurrence.Values.Any(a => !indexByAlias.ContainsKey(a)) ||
            issuedAliasByOccurrence.Values.Distinct(StringComparer.Ordinal).Count() != issuedAliasByOccurrence.Count)
            throw new InvalidOperationException("F1Q_TOOL_ISSUED_UNIVERSE_INVALID");
        aliasByOccurrence = issuedAliasByOccurrence;
        occurrenceByAlias = issuedAliasByOccurrence.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        repeatIndex = Enumerable.Range(0, lines.Count).ToLookup(i => RepeatKey(lines[i].Text), StringComparer.Ordinal);
        SourceSha256 = sourceSha256; SourceAliasUniverseSha256 = universeSha256; EvidenceStoreSha256 = storeSha256;
    }

    public static P7F1QEvidenceTools FromStore(PdfSourceEvidenceStore store, IReadOnlyDictionary<string, string> issuedAliasByOccurrence)
    {
        var rows = store.Entries.Select(e =>
        {
            var fields = e.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            JsonElement? Value(string name) => fields.TryGetValue(name, out var f) && f.Availability == "OBSERVED" ? f.Value : null;
            var text = Value("text")?.GetString() ?? throw new InvalidOperationException("F1Q_TOOL_TEXT_MISSING");
            var page = Value("page") is { } p ? p.GetInt32() : (int?)null;
            return new F1QSourceLine(e.SourceAlias, e.Ordinal, page, text, Value("bbox"), Value("typography"));
        }).ToArray();
        return new(store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256, rows, issuedAliasByOccurrence);
    }

    public IReadOnlyCollection<string> DocumentAliases => indexByAlias.Keys;

    /// <summary>OpenAI-compatible tool declarations. Frozen into every tool-arm request body.</summary>
    public static JsonArray Definitions() => new(
        Tool(Names[0], "Read the neighbouring parser source lines before and after one target in document reading order (text and page only).",
            new JsonObject
            {
                ["target"] = Target(),
                ["before"] = Int(0, MaxNeighbors, "Lines before the target."),
                ["after"] = Int(0, MaxNeighbors, "Lines after the target."),
            }, "target", "before", "after"),
        Tool(Names[1], "Read observed line geometry (PDF points, bottom-left origin) and font typography summaries for lines on one page, in reading order. Observations only; not heading labels.",
            new JsonObject
            {
                ["page"] = Int(1, 100000, "1-based page number."),
                ["offset"] = Int(0, 100000, "Number of page lines to skip in reading order."),
                ["maxLines"] = Int(1, MaxGeometryLines, "Maximum lines to return."),
            }, "page", "offset", "maxLines"),
        Tool(Names[2], "Read the full parser evidence record (text, page, bbox, typography, reading order) for up to 8 targets.",
            new JsonObject
            {
                ["targets"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxSpanTargets,
                    ["items"] = Target(), ["description"] = "Issued occurrence ids (O#) or source aliases.",
                },
            }, "targets"),
        Tool(Names[3], "Find other lines in the same document whose text repeats the target after a fixed normalization (case, dot leaders, trailing page number, digits masked). Useful for running headers/footers or contents entries vs. body headings.",
            new JsonObject
            {
                ["target"] = Target(),
                ["maxResults"] = Int(1, MaxRepeated, "Maximum matches to return."),
            }, "target", "maxResults"));

    public F1QToolResult Execute(string evidenceId, string name, string rawArguments)
    {
        string status = "OK"; string? error = null; object? result = null; object? arguments = null;
        var returned = new List<string>();
        try
        {
            using var parsed = ParseArguments(rawArguments);
            var args = parsed.RootElement;
            switch (name)
            {
                case "get_occurrence_context":
                {
                    Keys(args, "target", "before", "after");
                    var target = ResolveTarget(Str(args, "target"));
                    int before = Range(args, "before", 0, MaxNeighbors), after = Range(args, "after", 0, MaxNeighbors);
                    var at = indexByAlias[target];
                    var window = Enumerable.Range(Math.Max(0, at - before), Math.Min(lines.Count, at + after + 1) - Math.Max(0, at - before))
                        .Select(i => { returned.Add(lines[i].Alias); return new
                        {
                            sourceAlias = lines[i].Alias, occurrence = occurrenceByAlias.GetValueOrDefault(lines[i].Alias),
                            relative = i - at, page = lines[i].Page, text = Clip(lines[i].Text),
                        }; }).ToArray();
                    arguments = new { target, before, after };
                    result = new { targetAlias = target, lines = window, basis = "SOURCE_OCCURRENCE_READING_ORDER_V1_NOT_VISUAL_ROW_IDENTITY" };
                    break;
                }
                case "get_page_geometry":
                {
                    Keys(args, "page", "offset", "maxLines");
                    int page = Range(args, "page", 1, 100000), offset = Range(args, "offset", 0, 100000), max = Range(args, "maxLines", 1, MaxGeometryLines);
                    var onPage = lines.Where(l => l.Page == page).ToArray();
                    if (onPage.Length == 0) throw new F1QToolArgumentException("PAGE_NOT_IN_DOCUMENT");
                    var boxes = onPage.Select(l => Box(l.Bbox)).Where(b => b is not null).Select(b => b!.Value).ToArray();
                    var sizes = onPage.Select(l => FontSize(l.Typography)).Where(s => s is not null).Select(s => s!.Value).Order().ToArray();
                    var selected = onPage.Skip(offset).Take(max).ToArray();
                    var rows = selected.Select(l =>
                    {
                        var b = Box(l.Bbox);
                        return new
                        {
                            sourceAlias = l.Alias, occurrence = occurrenceByAlias.GetValueOrDefault(l.Alias), text = Clip(l.Text),
                            bbox = b is null ? null : new { left = R(b.Value.Left), right = R(b.Value.Right), bottom = R(b.Value.Bottom), top = R(b.Value.Top) },
                            font = Font(l.Typography),
                        };
                    }).ToList();
                    arguments = new { page, offset, maxLines = max };
                    object Build() => new
                    {
                        page, pageLineCount = onPage.Length, offset, returnedLines = rows.Count,
                        textExtent = boxes.Length == 0 ? null : new
                        {
                            minLeft = R(boxes.Min(b => b.Left)), maxRight = R(boxes.Max(b => b.Right)),
                            minBottom = R(boxes.Min(b => b.Bottom)), maxTop = R(boxes.Max(b => b.Top)),
                        },
                        medianFontSize = sizes.Length == 0 ? (double?)null : R(sizes[sizes.Length / 2]),
                        lines = rows,
                        basis = "PDF_LINE_GLYPH_UNION_BOUNDS_V1_PDF_POINTS_BOTTOM_LEFT_AND_PARSER_LINE_TYPOGRAPHY_V1_OBSERVATIONS_NOT_LABELS",
                    };
                    result = Build();
                    // Deterministic truncation from the end if the byte cap would be exceeded.
                    while (rows.Count > 1 && Serialize(evidenceId, name, status, error, arguments, result).Length > ResultByteCap)
                    { rows.RemoveAt(rows.Count - 1); result = Build(); }
                    returned.AddRange(rows.Select(r => r.sourceAlias));
                    break;
                }
                case "get_source_span":
                {
                    Keys(args, "targets");
                    var raw = args.GetProperty("targets");
                    if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() is < 1 or > MaxSpanTargets)
                        throw new F1QToolArgumentException("TARGETS_INVALID");
                    var targets = raw.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? ResolveTarget(t.GetString()!) : throw new F1QToolArgumentException("TARGET_NOT_STRING"))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    var records = targets.Select(alias =>
                    {
                        var l = lines[indexByAlias[alias]]; returned.Add(alias); var b = Box(l.Bbox);
                        return new
                        {
                            sourceAlias = alias, occurrence = occurrenceByAlias.GetValueOrDefault(alias), readingOrder = l.Ordinal,
                            page = l.Page, text = l.Text,
                            bbox = b is null ? null : new { left = R(b.Value.Left), right = R(b.Value.Right), bottom = R(b.Value.Bottom), top = R(b.Value.Top) },
                            typography = l.Typography,
                        };
                    }).ToArray();
                    arguments = new { targets };
                    result = new { records, basis = "P7_RAW_SOURCE_EVIDENCE_STORE_V1_FIELDS" };
                    break;
                }
                case "get_repeated_occurrences":
                {
                    Keys(args, "target", "maxResults");
                    var target = ResolveTarget(Str(args, "target"));
                    var max = Range(args, "maxResults", 1, MaxRepeated);
                    var self = lines[indexByAlias[target]];
                    var key = RepeatKey(self.Text);
                    var matches = repeatIndex[key].Where(i => lines[i].Alias != target).ToArray();
                    var shown = matches.Take(max).Select(i =>
                    {
                        returned.Add(lines[i].Alias);
                        return new { sourceAlias = lines[i].Alias, occurrence = occurrenceByAlias.GetValueOrDefault(lines[i].Alias),
                            page = lines[i].Page, text = Clip(lines[i].Text), exactTextMatch = lines[i].Text == self.Text };
                    }).ToArray();
                    returned.Add(target);
                    arguments = new { target, maxResults = max };
                    result = new
                    {
                        targetAlias = target, targetPage = self.Page, normalization = RepeatNormalization,
                        normalizedKeyEmpty = key.Length == 0, totalMatches = matches.Length,
                        distinctPagesWithMatches = matches.Select(i => lines[i].Page).Distinct().Count(), matches = shown,
                    };
                    break;
                }
                default: throw new F1QToolArgumentException("UNKNOWN_TOOL");
            }
        }
        catch (F1QToolArgumentException ex) { status = "INVALID_ARGUMENT"; error = ex.Code; result = null; returned.Clear(); }
        var content = Serialize(evidenceId, name, status, error, arguments, result);
        if (content.Length > ResultByteCap) throw new InvalidOperationException("F1Q_TOOL_RESULT_CAP_EXCEEDED");
        return new(evidenceId, name, rawArguments, status, error, content, SpatialCanonical.Hash(content),
            returned.Distinct(StringComparer.Ordinal).ToArray());
    }

    private byte[] Serialize(string evidenceId, string name, string status, string? error, object? arguments, object? result) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            evidenceId, tool = name, version = Version, status, error, arguments, result,
            provenance = new
            {
                sourceSha256 = SourceSha256, sourceAliasUniverseSha256 = SourceAliasUniverseSha256,
                evidenceStoreSha256 = EvidenceStoreSha256, readOnly = true, semanticLabels = "NONE",
            },
        }, WireJson);

    public static string RepeatKey(string text)
    {
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"(?:\s*[.·…]){2,}", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\s+\d+$", "");
        s = Regex.Replace(s, @"\d", "#");
        return s.Trim();
    }

    private string ResolveTarget(string value)
    {
        if (aliasByOccurrence.TryGetValue(value, out var alias)) return alias;
        if (indexByAlias.ContainsKey(value)) return value;
        throw new F1QToolArgumentException("TARGET_NOT_ISSUED_OR_NOT_IN_DOCUMENT");
    }

    private static JsonDocument ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || Encoding.UTF8.GetByteCount(raw) > 2048) throw new F1QToolArgumentException("ARGUMENTS_EMPTY_OR_TOO_LARGE");
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw new F1QToolArgumentException("ARGUMENTS_NOT_OBJECT"); }
            return doc;
        }
        catch (JsonException) { throw new F1QToolArgumentException("ARGUMENTS_NOT_JSON"); }
    }

    private static void Keys(JsonElement args, params string[] required)
    {
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count() || names.Any(n => !required.Contains(n, StringComparer.Ordinal)))
            throw new F1QToolArgumentException("ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE");
        if (required.Any(r => !names.Contains(r, StringComparer.Ordinal))) throw new F1QToolArgumentException("ARGUMENT_REQUIRED_MISSING");
    }

    private static string Str(JsonElement args, string key) =>
        args.GetProperty(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new F1QToolArgumentException("ARGUMENT_TYPE_INVALID");

    private static int Range(JsonElement args, string key, int min, int max)
    {
        var v = args.GetProperty(key);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) throw new F1QToolArgumentException("ARGUMENT_TYPE_INVALID");
        if (n < min || n > max) throw new F1QToolArgumentException("ARGUMENT_OUT_OF_RANGE");
        return n;
    }

    private static string Clip(string text) => text.Length <= MaxTextChars ? text : text[..MaxTextChars] + "…";
    private static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    private static (double Left, double Right, double Bottom, double Top)? Box(JsonElement? box) =>
        box is { ValueKind: JsonValueKind.Object } b
            ? (b.GetProperty("left").GetDouble(), b.GetProperty("right").GetDouble(), b.GetProperty("bottom").GetDouble(), b.GetProperty("top").GetDouble())
            : null;

    private static double? FontSize(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("fontSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;

    private static object? Font(JsonElement? t)
    {
        if (t is not { ValueKind: JsonValueKind.Object } v) return null;
        double? N(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? R(x.GetDouble()) : null;
        return new
        {
            size = N("fontSize"), name = v.TryGetProperty("fontName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            boldRatio = N("boldRatio"), italicRatio = N("italicRatio"),
        };
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
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
    private static JsonObject Target() => new() { ["type"] = "string", ["description"] = "An issued occurrence id such as O7, or a source alias such as L0012:S0 seen in supplied evidence." };
    private static JsonObject Int(int min, int max, string description) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["description"] = description };
}

internal sealed class F1QToolArgumentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
