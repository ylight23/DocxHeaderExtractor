using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

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
        TextViews = "get_text_views", Integrity = "get_source_integrity", Crop = "get_scan_crop";
    public static readonly string[] Names = [SourceContext, SearchText, TextViews, Integrity, Crop];
    /// <summary>The most lines one crop holds, all on one page.</summary>
    public const int MaxCropLines = 24;
    public const int MaxFactLines = 24, MaxFactRange = 400, MaxNeighbours = 6, MaxViewAliases = 8, MaxSearchLimit = 50, MaxQueryChars = 200, MinQueryChars = 2, MaxTextChars = 200;

    readonly ScanAgentSource scan;

    public ScanAgentTools(ScanAgentSource source) : base(source) => scan = source;

    protected override string ToolsVersion => Version;
    protected override string FactsToolName => LineFacts;
    // A scan's facts carry no type style; the crop shows the type itself, so it is the evidence a scan run requires (AgentUnit.ScanRequiresFactsTool).
    protected override string RequiredEvidenceTool => Crop;
    protected override bool IsRegistered(string? name) => name is not null && (Names.Contains(name, StringComparer.Ordinal) || name == LineFacts);
    public override IReadOnlyList<string> ReservedNames => Names;
    public override JsonObject DocumentStyles() => (JsonObject)scan.StyleTable.DeepClone();

    protected override (JsonNode?, JsonNode?, bool) Dispatch(string name, JsonElement args, List<string> returned) => name switch
    {
        LineFacts => FactsOfRange(args, returned),
        SourceContext => Context(args, returned),
        SearchText => Search(args, returned),
        TextViews => Views(args, returned),
        Crop => CropOf(args, returned),
        _ => IntegrityOf(args, returned),
    };

    public static JsonArray Definitions()
    {
        var all = new JsonArray(
            Tool(SourceContext, "Read the lines before and after one line in reading order (across pages), each with its page, its text as shown and its box.",
                new JsonObject { ["alias"] = AliasArg("The line to read around."), ["before"] = Int(0, MaxNeighbours, "Lines before it."), ["after"] = Int(0, MaxNeighbours, "Lines after it.") },
                "alias", "before", "after"),
            Tool(SearchText, "Find the lines whose text contains the query in any OCR view, in reading order. mode literal matches exactly; mode normalized ignores case, Vietnamese diacritics and all whitespace. Each match names the views that contain it.",
                new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = MinQueryChars, ["maxLength"] = MaxQueryChars, ["description"] = "Text to find." },
                    ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("literal", "normalized") },
                    ["offset"] = Int(0, 100000, "Matches to skip."), ["limit"] = Int(1, MaxSearchLimit, "Maximum matches to return."),
                }, "query", "mode", "offset", "limit"),
            Tool(TextViews, "Read every reading of up to 8 lines: each OCR view's text with its mean and lowest confidence and every word with its confidence, the stretches where the views differ, the vision transcription with the OCR views that read the same, and the vision model's choice between them and the reading it proposed, when one was asked (VLM_OBSERVATION). Nothing here says which reading is right.",
                new JsonObject
                {
                    ["aliases"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxViewAliases, ["items"] = AliasArg("A line alias."), ["description"] = "Aliases of the lines to read." },
                }, "aliases"),
            Tool(Integrity, "Read how the document carries its content and how its readings were made: its kind (scanned pages), pages, each OCR view with its engine, version, languages and preprocessing, the views that could not be made, and the vision observations (model, pages observed, failures).",
                new JsonObject(), []));
        all.Add(Tool(Crop, "See up to " + MaxCropLines + " lines of one page as an image cut from the page image (black and white, 32 px around them): the type itself, its weight, size and indentation, and the places of the lines against each other.",
            new JsonObject
            {
                ["aliases"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxCropLines, ["items"] = AliasArg("A line alias."), ["description"] = "Lines of one page to see together." },
            }, "aliases"));
        all.Add(Tool(LineFacts, "Read the facts the program measured for the lines from one alias to another in reading order (at most " + MaxFactLines + " per call; use offset to continue where a result was cut). For each line, each fact taken from the line itself: origin OCR_DERIVED, page, text size, layout (box in pixels of the page image, page size, margins), typography measured on the page image from the line's own box (stroke width, stroke per height, ink share, runs; MEASURED_ON_IMAGE, or not measured) and a vision model's observation when one was asked (VLM_OBSERVATION), the reading shown and its leading token, numbered prefix and ending punctuation, and the vision model's reading choice when it was asked. Measurements and observations only: no label and no decision.",
            new JsonObject
            {
                ["first"] = AliasArg("The first line of the range."),
                ["last"] = AliasArg("The last line of the range, not before the first and at most " + MaxFactRange + " lines after it."),
                ["offset"] = Int(0, 100000, "How many lines of the range to skip."),
            }, "first", "last", "offset"));
        return all;
    }

    /// <summary>The crop of the lines named: the result says what was cut, and the image goes to the conversation after the tool results.</summary>
    (JsonNode?, JsonNode?, bool) CropOf(JsonElement args, List<string> returned)
    {
        Keys(args, "aliases");
        var arguments = JsonNode.Parse(args.GetRawText())!.AsObject();
        var result = GroupVerifier.ReadCrop(scan, arguments, out var image);
        if (image is JsonObject picture && result["status"]?.GetValue<string>() == "OK")
        {
            PendingImages.Add(picture);
            returned.AddRange(arguments["aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        }
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
        var (result, taken) = Fit(LineFacts, arguments, slice.Length, k => new JsonObject
        {
            ["readBy"] = "the program, from the OCR evidence of the page images and the vision observations; boxes are in pixels of the page image",
            ["version"] = ScanAgentSource.FactsVersion,
            ["rangeLines"] = rangeLength, ["offset"] = offset,
            ["facts"] = new JsonArray(slice.Take(k).Select(i => (JsonNode)scan.Facts[lines[i].Alias].DeepClone()).ToArray()),
            ["hasMore"] = offset + k < rangeLength, ["nextOffset"] = offset + k < rangeLength ? offset + k : null,
            ["truncation"] = Truncation(k, slice.Length, offset + k),
        });
        returned.AddRange(slice.Take(taken).SelectMany(i => scan.Flavor.FactReferences(scan.Facts[lines[i].Alias]).Prepend(lines[i].Alias)));
        NoteFactsReturned(slice.Take(taken).Select(i => lines[i].Alias), taken < slice.Length || offset + taken < rangeLength);
        return (arguments, result, taken < slice.Length);
    }

    JsonObject Brief(int i)
    {
        var l = scan.ScanLines[i];
        return new JsonObject
        {
            ["alias"] = l.Alias, ["page"] = l.Page, ["readingOrder"] = i, ["text"] = Clip(lines[i].Text, MaxTextChars),
            ["box"] = new JsonObject { ["left"] = l.Primary.Box.Left, ["top"] = l.Primary.Box.Top, ["width"] = l.Primary.Box.Width, ["height"] = l.Primary.Box.Height },
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
        ["words"] = new JsonArray(line.Words.Select(w => (JsonNode)new JsonObject { ["text"] = w.Text, ["confidence"] = w.Confidence }).ToArray()),
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
                    differences.Add(new JsonObject { ["views"] = new JsonArray(ScanOcrPreflight.OriginalView, view), ["original"] = d.Original, ["alternative"] = d.Alternative });
            }
            var o = new JsonObject { ["alias"] = alias, ["page"] = l.Page, ["readings"] = readings, ["differences"] = differences };
            if (scan.Transcription(alias) is { } t)
            {
                var agreeing = scan.OcrViewsAgreeingWithTranscription(alias);
                if (t.Status == ScanTranscription.Accepted)
                    readings.Add(new JsonObject
                    {
                        ["viewId"] = ScanAgentSource.VlmTranscribedReading, ["origin"] = "VLM_TRANSCRIBED", ["text"] = ScanTranscription.Norm(t.Text!),
                        ["transcribedBy"] = scan.Transcriptions!.Model, ["ocrViewsThatReadTheSame"] = new JsonArray(agreeing.Select(v => (JsonNode)v).ToArray()),
                    });
                else
                    o["transcriptionRejected"] = new JsonObject { ["status"] = t.Status, ["text"] = t.Text is null ? null : Clip(t.Text, MaxTextChars), ["origin"] = "VLM_TRANSCRIBED" };
            }
            o["shownReading"] = scan.DefaultReading(alias);
            if (scan.VisionChoice(alias) is { } c)
            {
                o["vision"] = new JsonObject { ["origin"] = "VLM_OBSERVATION", ["observedBy"] = scan.VisionModel, ["choice"] = c.Choice };
                if (c.ProposedReading is { Length: > 0 } p)
                {
                    readings.Add(new JsonObject { ["viewId"] = ScanAgentSource.VlmProposedReading, ["origin"] = "CORRECTION_CANDIDATE", ["text"] = p, ["proposedBy"] = scan.VisionModel });
                }
            }
            o["readingViewIds"] = new JsonArray(scan.Readings(alias).Keys.Select(k => (JsonNode)k).ToArray());
            return o;
        }
        var (result, taken) = Fit(TextViews, arguments, aliases.Count, k => new JsonObject
        {
            ["readBy"] = "the program, from the OCR evidence store, the vision observations and the vision transcriptions as they were written; no reading was edited",
            ["records"] = new JsonArray(aliases.Take(k).Select(Record).ToArray()),
            ["truncation"] = Truncation(k, aliases.Count, null),
        });
        returned.AddRange(aliases.Take(taken));
        return (arguments, result, taken < aliases.Count);
    }

    (JsonNode?, JsonNode?, bool) IntegrityOf(JsonElement args, List<string> returned)
    {
        Keys(args);
        var ev = scan.Evidence;
        var result = new JsonObject
        {
            ["readBy"] = "the program, from the source integrity check, the OCR evidence store, the vision observations and the vision transcriptions",
            ["sourceKind"] = ev.SourceKind, ["pages"] = ev.PageCount, ["lines"] = lines.Count,
            ["textShown"] = new JsonObject
            {
                ["vlmTranscribed"] = lines.Count(l => scan.DefaultReading(l.Alias) == ScanAgentSource.VlmTranscribedReading),
                ["ocrOriginal"] = lines.Count(l => scan.DefaultReading(l.Alias) == ScanOcrPreflight.OriginalView),
            },
            ["transcription"] = scan.Transcriptions is not { } ts ? new JsonObject { ["asked"] = false } : new JsonObject
            {
                ["asked"] = true, ["model"] = ts.Model, ["promptVersion"] = ts.PromptVersion, ["cropScale"] = ts.Scale, ["reasoning"] = ts.Reasoning, ["origin"] = "VLM_TRANSCRIBED",
                ["seenByModel"] = "only the crop of the line; no OCR reading",
                ["statuses"] = new JsonObject(ts.Lines.GroupBy(l => l.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            },
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
            ["typography"] = "a scan carries no font data: its line facts carry pixel measurements marked MEASURED_ON_IMAGE; optional vision observations are a model's judgement",
        };
        if (ev.PageImageBasis is { } basis) result["pageImageBasis"] = basis;
        return (new JsonObject(), result, false);
    }
}
