using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// The read-only evidence tools over a scanned document. They read the OCR evidence store and the vision observations as they were written;
/// nothing is recognised, corrected or chosen at call time. Every result says where its data comes from (OCR_DERIVED, VLM_OBSERVATION) and
/// none labels a line. The machinery (strict arguments, capped bodies, ledgers, cache) is <see cref="AgentToolHost"/>'s.
/// </summary>
internal sealed class ScanAgentTools : AgentToolHost
{
    public const string Version = "SCAN_AGENT_TOOLS_V1";
    public const string LineFacts = "get_line_facts", SourceContext = "get_source_context", SearchText = "search_document_text",
        TextViews = "get_text_views", Sequence = "get_shape_sequence", Integrity = "get_source_integrity";
    public static readonly string[] Names = [SourceContext, SearchText, TextViews, Sequence, Integrity];
    public const int MaxFactLines = 24, MaxFactRange = 400, MaxNeighbours = 6, MaxViewAliases = 8, MaxSearchLimit = 50, MaxQueryChars = 200, MinQueryChars = 2, MaxTextChars = 200;

    readonly ScanAgentSource scan;

    public ScanAgentTools(ScanAgentSource source) : base(source) => scan = source;

    protected override string ToolsVersion => Version;
    protected override string FactsToolName => LineFacts;
    protected override bool IsRegistered(string? name) => name is not null && (Names.Contains(name, StringComparer.Ordinal) || name == LineFacts);
    public override IReadOnlyList<string> ReservedNames => Names;
    public override JsonObject DocumentStyles() => (JsonObject)scan.StyleTable.DeepClone();

    protected override (JsonNode?, JsonNode?, bool) Dispatch(string name, JsonElement args, List<string> returned) => name switch
    {
        LineFacts => FactsOfRange(args, returned),
        SourceContext => Context(args, returned),
        SearchText => Search(args, returned),
        TextViews => Views(args, returned),
        Sequence => SequenceOf(args, returned),
        _ => IntegrityOf(args, returned),
    };

    public static JsonArray Definitions()
    {
        var all = new JsonArray(
            Tool(SourceContext, "Read the lines before and after one line in reading order (across pages), each with its page, its text as the primary OCR view reads it, its box and its mean OCR confidence.",
                new JsonObject { ["alias"] = AliasArg("The line to read around."), ["before"] = Int(0, MaxNeighbours, "Lines before it."), ["after"] = Int(0, MaxNeighbours, "Lines after it.") },
                "alias", "before", "after"),
            Tool(SearchText, "Find the lines whose text contains the query in any OCR view, in reading order. mode literal matches exactly; mode normalized ignores case, Vietnamese diacritics and all whitespace. Each match names the views that contain it.",
                new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = MinQueryChars, ["maxLength"] = MaxQueryChars, ["description"] = "Text to find." },
                    ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("literal", "normalized") },
                    ["offset"] = Int(0, 100000, "Matches to skip."), ["limit"] = Int(1, MaxSearchLimit, "Maximum matches to return."),
                }, "query", "mode", "offset", "limit"),
            Tool(TextViews, "Read every reading of up to 8 lines: each OCR view's text with its confidences and its low-confidence words, the stretches where the views differ (UNVERIFIED), and the vision model's choice between them and the reading it proposed, when one was asked (VLM_OBSERVATION). Nothing here says which reading is right.",
                new JsonObject
                {
                    ["aliases"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxViewAliases, ["items"] = AliasArg("A line alias."), ["description"] = "Aliases of the lines to read." },
                }, "aliases"),
            Tool(Sequence, "Read the numbered sequence a line belongs to, from the form of the text only: its key is the words before its first number (at most two), # for the number, and the punctuation right after it. Returns the key and number, how many lines share the key, the neighbours in the sequence, where the numbers skip, repeat or go down, and for every skip the lines between the neighbours that carry exactly a missing number under another form (a misread label). The result says what the numbers do, not what any line is.",
                new JsonObject { ["alias"] = AliasArg("The line whose sequence to read.") }, "alias"),
            Tool(Integrity, "Read how the document carries its content and how its readings were made: its kind (scanned pages), pages, each OCR view with its engine, version, languages and preprocessing, the views that could not be made, and the vision observations (model, pages observed, failures).",
                new JsonObject(), []));
        all.Add(Tool(LineFacts, "Read the facts the program measured for the lines from one alias to another in reading order (at most " + MaxFactLines + " per call; use offset to continue where a result was cut). For each line: origin OCR_DERIVED, page, text size, layout (box in pixels of the page image, page size, left edge against the page's common edge and how many lines share it, margins and centring, gaps above and below, line height against the page median), ocr (mean and lowest confidence, low-confidence words, whether another line overlaps it, how many views cover it and whether they read it differently), typography (from a vision model, marked VLM_OBSERVATION, or not measured), the vision model's reading choice when it was asked, and a repetition summary. Measurements and observations only: no label and no decision.",
            new JsonObject
            {
                ["first"] = AliasArg("The first line of the range."),
                ["last"] = AliasArg("The last line of the range, not before the first and at most " + MaxFactRange + " lines after it."),
                ["offset"] = Int(0, 100000, "How many lines of the range to skip."),
            }, "first", "last", "offset"));
        return all;
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
        var (result, taken) = Fit(LineFacts, arguments, slice.Length, k => new JsonObject
        {
            ["readBy"] = "the program, from the OCR evidence of the page images and the vision observations; boxes are in pixels of the page image",
            ["version"] = ScanAgentSource.FactsVersion,
            ["rangeLines"] = rangeLength, ["offset"] = offset,
            ["facts"] = new JsonArray(slice.Take(k).Select(i => (JsonNode)scan.Facts[lines[i].Alias].DeepClone()).ToArray()),
            ["hasMore"] = offset + k < rangeLength, ["nextOffset"] = offset + k < rangeLength ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).SelectMany(i => (scan.Facts[lines[i].Alias]["repetition"]?["sampleAliases"]?.AsArray().Select(a => a!.GetValue<string>()) ?? []).Prepend(lines[i].Alias)));
        NoteFactsReturned(slice.Take(taken).Select(i => lines[i].Alias), taken < slice.Length || offset + taken < rangeLength);
        return (arguments, result, taken < slice.Length);
    }

    JsonObject Brief(int i)
    {
        var l = scan.ScanLines[i];
        return new JsonObject
        {
            ["alias"] = l.Alias, ["page"] = l.Page, ["readingOrder"] = i, ["text"] = Clip(l.Primary.Text, MaxTextChars),
            ["box"] = new JsonObject { ["left"] = l.Primary.Box.Left, ["top"] = l.Primary.Box.Top, ["width"] = l.Primary.Box.Width, ["height"] = l.Primary.Box.Height },
            ["meanConfidence"] = l.Primary.MeanConfidence,
        };
    }

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
            ["lines"] = new JsonArray(window.Take(k).Select(i => { var b = Brief(i); b["relative"] = i - at; return (JsonNode)b; }).ToArray()),
            ["truncation"] = Truncation(k, window.Length, null),
        });
        returned.AddRange(window.Take(taken).Select(i => lines[i].Alias));
        return (arguments, result, taken < window.Length);
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
        bool Has(string text) => (mode == "normalized" ? Evidence.Normalize(text) : text).Contains(needle, StringComparison.Ordinal);
        var matches = Enumerable.Range(0, lines.Count)
            .Select(i => (i, views: scan.Readings(lines[i].Alias).Where(r => Has(r.Value)).Select(r => r.Key).ToArray()))
            .Where(x => x.views.Length > 0).ToArray();
        var slice = matches.Skip(offset).Take(limit).ToArray();
        var arguments = new JsonObject { ["query"] = query, ["mode"] = mode, ["offset"] = offset, ["limit"] = limit };
        var (result, taken) = Fit(SearchText, arguments, slice.Length, k => new JsonObject
        {
            ["total"] = matches.Length, ["offset"] = offset, ["scannedLines"] = lines.Count,
            ["matches"] = new JsonArray(slice.Take(k).Select(m => { var b = Brief(m.i); b["inViews"] = new JsonArray(m.views.Select(v => (JsonNode)v).ToArray()); return (JsonNode)b; }).ToArray()),
            ["hasMore"] = offset + k < matches.Length, ["nextOffset"] = offset + k < matches.Length ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).Select(m => lines[m.i].Alias));
        return (arguments, result, taken < slice.Length);
    }

    static JsonObject Reading(string viewId, OcrLine line, string origin) => new()
    {
        ["viewId"] = viewId, ["origin"] = origin, ["text"] = line.Text,
        ["meanConfidence"] = line.MeanConfidence, ["minConfidence"] = line.MinConfidence,
        ["lowConfidenceWords"] = new JsonArray(line.Words.Where(w => w.Confidence < ScanAgentSource.LowConfidence).Take(ScanAgentSource.MaxLowConfidenceWords)
            .Select(w => (JsonNode)new JsonObject { ["text"] = w.Text, ["confidence"] = w.Confidence }).ToArray()),
    };

    (JsonNode?, JsonNode?, bool) Views(JsonElement args, List<string> returned)
    {
        Keys(args, "aliases");
        var arr = args.GetProperty("aliases");
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is < 1 or > MaxViewAliases) throw new ArgumentError("ALIASES_INVALID");
        var aliases = new List<string>();
        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.String) throw new ArgumentError("ARGUMENT_TYPE_INVALID");
            var s = a.GetString()!;
            if (s.Length > MaxAliasChars || !index.ContainsKey(s)) throw new ArgumentError("ALIAS_NOT_IN_DOCUMENT");
            if (!aliases.Contains(s, StringComparer.Ordinal)) aliases.Add(s);
        }
        var arguments = new JsonObject { ["aliases"] = new JsonArray(aliases.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()) };
        JsonNode Record(string alias)
        {
            var l = scan.ScanLines[index[alias]];
            var readings = new JsonArray(Reading(ScanOcrPreflight.OriginalView, l.Primary, "OCR_DERIVED"));
            var differences = new JsonArray();
            foreach (var (view, (other, diffs)) in l.OtherViews)
            {
                readings.Add(Reading(view, other, "OCR_DERIVED"));
                foreach (var d in diffs)
                    differences.Add(new JsonObject { ["views"] = new JsonArray(ScanOcrPreflight.OriginalView, view), ["original"] = d.Original, ["alternative"] = d.Alternative, ["verificationStatus"] = "UNVERIFIED" });
            }
            var o = new JsonObject { ["alias"] = alias, ["page"] = l.Page, ["readings"] = readings, ["differences"] = differences };
            if (scan.VisionChoice(alias) is { } c)
            {
                o["vision"] = new JsonObject { ["origin"] = "VLM_OBSERVATION", ["observedBy"] = scan.VisionModel, ["choice"] = c.Choice };
                if (c.ProposedReading is { Length: > 0 } p)
                {
                    readings.Add(new JsonObject { ["viewId"] = ScanAgentSource.VlmProposedReading, ["origin"] = "CORRECTION_CANDIDATE", ["text"] = p, ["proposedBy"] = scan.VisionModel, ["verificationStatus"] = "UNVERIFIED" });
                }
            }
            o["readingViewIds"] = new JsonArray(scan.Readings(alias).Keys.Select(k => (JsonNode)k).ToArray());
            return o;
        }
        var (result, taken) = Fit(TextViews, arguments, aliases.Count, k => new JsonObject
        {
            ["readBy"] = "the program, from the OCR evidence store and the vision observations as they were written; no reading was corrected",
            ["records"] = new JsonArray(aliases.Take(k).Select(Record).ToArray()),
            ["truncation"] = Truncation(k, aliases.Count, null),
        });
        returned.AddRange(aliases.Take(taken));
        return (arguments, result, taken < aliases.Count);
    }

    (JsonNode?, JsonNode?, bool) SequenceOf(JsonElement args, List<string> returned)
    {
        Keys(args, "alias");
        var alias = Alias(args, "alias");
        var arguments = new JsonObject { ["alias"] = alias };
        var described = ShapeSequence.Describe(lines, index[alias]);
        var result = new JsonObject
        {
            ["readBy"] = "the program, from the form of the primary OCR view's text of every line: the words before the first number, the number, the punctuation after it",
            ["version"] = ShapeSequence.Version, ["sequence"] = described,
        };
        if (described is null) result["noKey"] = "no number stands among the first words of this line";
        if (Body("E0.00", Sequence, "OK", null, arguments, result).Length + ResultMargin > MaxResultBytes) throw new InvalidOperationException("SCAN_TOOL_SEQUENCE_OVER_CAP");
        if (described is not null)
            foreach (var node in described.AsObject().Where(kv => kv.Value is JsonArray).SelectMany(kv => kv.Value!.AsArray())
                         .SelectMany(n => n?["missingNumberUnderAnotherKey"] is JsonArray c ? c.Prepend(n) : [n]))
                foreach (var key in new[] { "alias", "afterAlias", "beforeAlias", "fromAlias", "toAlias" })
                    if (node?[key] is JsonValue v) returned.Add(v.GetValue<string>());
        returned.Add(alias);
        return (arguments, result, false);
    }

    (JsonNode?, JsonNode?, bool) IntegrityOf(JsonElement args, List<string> returned)
    {
        Keys(args);
        var ev = scan.Evidence;
        var result = new JsonObject
        {
            ["readBy"] = "the program, from the source integrity check, the OCR evidence store and the vision observations",
            ["sourceKind"] = ev.SourceKind, ["pages"] = ev.PageCount, ["lines"] = lines.Count, ["textOrigin"] = "OCR_DERIVED",
            ["views"] = new JsonArray(ev.Views.Select(v => (JsonNode)new JsonObject
            {
                ["viewId"] = v.Spec.ViewId, ["engine"] = v.Spec.EngineVersion, ["languages"] = v.Spec.Languages,
                ["preprocessing"] = v.Spec.Preprocessing, ["pageSegmentationMode"] = v.Spec.PageSegmentationMode,
            }).ToArray()),
            ["viewsNotMade"] = new JsonArray(ev.Gaps.Select(g => (JsonNode)new JsonObject { ["viewId"] = g.ViewId, ["reason"] = g.Reason }).ToArray()),
            ["vision"] = new JsonObject
            {
                ["model"] = scan.VisionModel, ["pagesObserved"] = scan.Vision.Count(v => v.Failure is null),
                ["pagesFailed"] = scan.Vision.Count(v => v.Failure is not null), ["origin"] = "VLM_OBSERVATION",
            },
            ["typography"] = "a scan carries no font data: typography comes only from vision observations, which are a model's judgement",
        };
        return (new JsonObject(), result, false);
    }
}
