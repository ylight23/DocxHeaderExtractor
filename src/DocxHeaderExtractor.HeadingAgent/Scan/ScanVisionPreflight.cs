using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// The vision preflight of a scanned document (owner's decisions 2026-10-16: page images may be sent; the vision model is the agent's own
/// qwen/qwen3.7-flash; the agent, not the vision model, decides what a heading is). One request per page, before any agent conversation:
/// <list type="bullet">
/// <item>the page in black and white at <see cref="Options.PageWidth"/> pixels, with every OCR line of the page (id, box in that image, first
/// words): for each, closed answers on its typography (bold, size against its neighbours, alignment, each with "uncertain");</item>
/// <item>for the lines whose evidence is weakest (the OCR views read them differently, a word below 60 confidence, another line overlaps them),
/// a crop at full resolution and the readings of every view: the model chooses one, NONE or UNREADABLE, and may propose a reading.</item>
/// </list>
/// The request goes through the agent's audited transport (Alibaba only, the pinned model, usage and cost audit). The reply is validated: line
/// ids must be the ones asked, answers from the closed sets, a choice one of the views shown; everything else is rejected and recorded. A valid
/// observation is stored once per image, model, prompt version and set of asked lines, and read back after; a failed call is returned, not
/// stored. Nothing here edits a reading: a proposed reading is a correction candidate the agent may take or leave.
/// </summary>
public sealed class ScanVisionPreflight(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string PromptVersion = "SCAN_VLM_PROMPT_V1";
    public const int MaxProposedChars = 400, CropPad = 8, FirstWordsChars = 40;

    /// <summary>The calls sent and their cost in the last <see cref="Run"/> (pages read back from the store cost nothing).</summary>
    public int LastCalls { get; private set; }
    public double LastCostUsd { get; private set; }
    /// <summary>The durations of the last run's calls.</summary>
    public CallStats? LastCallTimes { get; private set; }

    CallTimings timings = new();

    public sealed record Options(int MaxPages = 300, int MaxReadingsPerPage = 10, int MaxLinesPerPage = 80, int PageWidth = 1000, int Parallelism = 32,
        double CeilingUsd = 3.0);

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public const string SystemPrompt = "You look at one page image of a scanned document and answer closed questions about what the image shows. You decide nothing about the document's structure: another reader does that with your answers as evidence.\n"
        + "Part 1, lines: the request lists lines with an id, their box in the page image you see (left, top, width, height in pixels of that image) and the first words of their OCR reading. For every listed line answer appearsBold (yes, no or uncertain: whether its letters look bold compared with the ordinary text of the page), relativeSize (larger, same, smaller or uncertain: its letter height compared with the lines just above and below it) and alignment (left, center, right, justified or uncertain). Answer uncertain whenever the image does not show it clearly.\n"
        + "Part 2, readings: for some lines the request adds a crop of the line at full resolution, then the OCR readings of that line by viewId. Choose the viewId whose text matches the crop exactly, letter for letter, diacritics and punctuation included; NONE when no reading matches; UNREADABLE when the crop cannot be read. With NONE you may add proposedReading: the line exactly as the crop shows it. Read only what the image shows; never use what you know or expect a document of this kind to say.\n"
        + "Answer exactly one JSON object and nothing else: {\"protocolVersion\":\"" + VlmPageObservation.CurrentVersion + "\",\"lines\":[{\"lineId\":\"<id>\",\"appearsBold\":\"yes|no|uncertain\",\"relativeSize\":\"larger|same|smaller|uncertain\",\"alignment\":\"left|center|right|justified|uncertain\"}],\"readings\":[{\"lineId\":\"<id>\",\"choice\":\"<viewId>|NONE|UNREADABLE\",\"proposedReading\":\"<only with NONE, optional>\"}]}. One entry in lines for every listed line and one entry in readings for every crop.";

    /// <summary>Observes every page of an evidence (up to <see cref="Options.MaxPages"/>). Returns one observation per page, the failed ones with
    /// their failure; never throws for a page the model could not answer.</summary>
    public IReadOnlyList<VlmPageObservation> Run(ScanOcrEvidence evidence, string evidenceEntry, string apiKey, Options? options = null, CancellationToken ct = default)
    {
        using var cancellation = TesseractOcr.WithCancellation(ct);
        ct.ThrowIfCancellationRequested();
        options ??= new Options();
        timings = new CallTimings();
        var magick = TesseractOcr.FindImageMagick() ?? throw new InvalidOperationException("IMAGEMAGICK_NOT_FOUND");
        var primary = evidence.Views.First(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        var others = evidence.Views.Where(v => v != primary).ToArray();
        var aligned = others.ToDictionary(v => v.Spec.ViewId, v => OcrViewComparer.Compare(primary, v).ToDictionary(a => a.LineA, StringComparer.Ordinal));
        var otherLines = others.ToDictionary(v => v.Spec.ViewId, v => v.Pages.SelectMany(p => p.Lines).ToDictionary(l => l.Id, StringComparer.Ordinal));
        var pages = primary.Pages.Take(options.MaxPages).ToArray();
        var budget = new AgentBudget(pages.Length * (1 + AgentUnit.TransportRetries), options.CeilingUsd);
        var factory = handlerFactory ?? (() => AgentModel.CreateHandler());
        var result = new VlmPageObservation[pages.Length];
        Parallel.For(0, pages.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct }, k =>
        {
            var page = pages[k];
            var readings = page.Lines.ToDictionary(l => l.Id, l =>
            {
                var r = new Dictionary<string, string>(StringComparer.Ordinal) { [ScanOcrPreflight.OriginalView] = l.Text };
                foreach (var v in others)
                    if (aligned[v.Spec.ViewId].TryGetValue(l.Id, out var a) && a.LineB is { } b) r[v.Spec.ViewId] = otherLines[v.Spec.ViewId][b].Text;
                return r;
            }, StringComparer.Ordinal);
            result[k] = ObservePage(page, readings, ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page), magick, apiKey, budget, factory, options, ct);
        });
        LastCalls = budget.Calls;
        LastCallTimes = timings.Stats();
        LastCostUsd = budget.ActualUsd;
        return result;
    }

    /// <summary>The lines whose evidence is weakest, weakest first: the views read them differently, a word below 60, or another line overlaps them.</summary>
    internal static IReadOnlyList<OcrLine> Weakest(OcrPage page, IReadOnlyDictionary<string, Dictionary<string, string>> readings, int max) =>
        page.Lines.Where(l => readings[l.Id].Values.Distinct(StringComparer.Ordinal).Count() > 1 || l.MinConfidence < ScanAgentSource.LowConfidence || l.OverlapsOtherLine)
            .OrderBy(l => l.MinConfidence).ThenBy(l => l.Id, StringComparer.Ordinal).Take(max).ToArray();

    VlmPageObservation ObservePage(OcrPage page, IReadOnlyDictionary<string, Dictionary<string, string>> readings, string? imagePath, string magick,
        string apiKey, AgentBudget budget, Func<HttpMessageHandler> factory, Options options, CancellationToken ct)
    {
        VlmPageObservation Failed(string code, IReadOnlyList<string>? lines = null, IReadOnlyList<string>? asked = null) =>
            new(VlmPageObservation.CurrentVersion, page.Page, page.ImageSha256, AgentProtocol.ChatModel, PromptVersion, lines ?? [], asked ?? [], [], [], [], code, null);
        if (imagePath is null) return Failed("PAGE_IMAGE_MISSING");

        var work = Path.Combine(Path.GetTempPath(), "dhx-vlm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var pageFile = Path.Combine(work, "page.png");
            if (!TesseractOcr.Downscale(magick, imagePath, pageFile, options.PageWidth)) return Failed("IMAGE_DOWNSCALE_FAILED");
            var scale = page.ImageWidth > 0 ? (double)options.PageWidth / page.ImageWidth : 1.0;
            var listed = page.Lines.Take(options.MaxLinesPerPage).ToArray();
            var content = new List<JsonNode>
            {
                Text(new JsonObject
                {
                    ["page"] = page.Page,
                    ["imageWidth"] = options.PageWidth,
                    ["lines"] = new JsonArray(listed.Select(l => (JsonNode)new JsonObject
                    {
                        ["lineId"] = l.Id,
                        ["box"] = new JsonObject { ["left"] = (int)(l.Box.Left * scale), ["top"] = (int)(l.Box.Top * scale), ["width"] = (int)(l.Box.Width * scale), ["height"] = (int)(l.Box.Height * scale) },
                        ["firstWords"] = l.Text.Length <= FirstWordsChars ? l.Text : l.Text[..FirstWordsChars],
                    }).ToArray()),
                }),
                Image(pageFile),
            };
            var asked = new List<string>();
            foreach (var line in Weakest(page, readings, options.MaxReadingsPerPage))
            {
                var crop = Path.Combine(work, line.Id + ".png");
                if (!TesseractOcr.Crop(magick, imagePath, crop, line.Box, CropPad)) continue;
                JsonNode[] part = [
                    Text(new JsonObject { ["crop"] = line.Id, ["readings"] = new JsonArray(readings[line.Id].Select(r => (JsonNode)new JsonObject { ["viewId"] = r.Key, ["text"] = r.Value }).ToArray()) }),
                    Image(crop)];
                // A crop is added only while the request stays under the byte cap of the audited transport; the ones left out are not asked.
                if (Request(content.Concat(part)).Bytes > AgentBudget.MaxRequestBytes) break;
                content.AddRange(part);
                asked.Add(line.Id);
            }
            var lineIds = listed.Select(l => l.Id).ToArray();
            var key = TesseractOcr.Sha256(string.Join("|", page.ImageSha256, AgentProtocol.ChatModel, PromptVersion, options.PageWidth,
                string.Join(",", lineIds), string.Join(",", asked.Select(a => a + "=" + string.Join("/", readings[a].Select(r => r.Key + ":" + r.Value))))));
            var file = Path.Combine(storeRoot, $"{page.ImageSha256[..16]}-{key[..12]}.json");
            if (File.Exists(file))
                return JsonSerializer.Deserialize<VlmPageObservation>(File.ReadAllText(file, Utf8), Json) ?? throw new InvalidOperationException("VLM_OBSERVATION_UNREADABLE");

            var request = Request(content);
            AgentProtocol.ModelTurn turn;
            using (var handler = factory())
            using (var model = new AgentModel(handler, apiKey, budget, cancellationToken: ct, rateLimitRetries: AgentModel.ProductionRateLimitRetries))
            {
                var refusal = budget.Admit(request);
                if (refusal is not null) return Failed("BUDGET:" + refusal, lineIds, asked);
                try { turn = timings.Time($"VLM-P{page.Page:000}", () => model.Turn($"VLM-P{page.Page:000}", 1, request)); }
                catch (ReplyFailure f) { return Failed("CALL:" + f.Code, lineIds, asked); }
            }
            var reply = turn.Content ?? "";
            if (AgentProtocol.TryUnwrapFence(reply, out var inner)) reply = inner;
            var observation = Validate(page, reply, lineIds, asked, readings);
            if (observation.Failure is not null) return observation;
            Directory.CreateDirectory(storeRoot);
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(observation, Json), Utf8);
            try { File.Move(temp, file); } catch (IOException) when (File.Exists(file)) { File.Delete(temp); }
            return observation;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    static JsonNode Text(JsonObject o) => new JsonObject { ["type"] = "text", ["text"] = o.ToJsonString(Json) };

    static JsonNode Image(string path) => new JsonObject
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) },
    };

    /// <summary>One POST body for the audited transport: the pinned model, the vision system prompt, the page's content, json_object, the locked
    /// provider block, streaming and usage, as the agent's own requests.</summary>
    internal static PreparedRequest Request(IEnumerable<JsonNode> content)
    {
        var body = new JsonObject
        {
            ["model"] = AgentProtocol.ChatModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(content.Select(c => c.DeepClone()).ToArray()) }),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["temperature"] = 0,
            ["max_tokens"] = AgentProtocol.MaxTokens,
            ["reasoning"] = new JsonObject { ["enabled"] = true },
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { AgentProtocol.ProviderTag }, ignore = AgentProtocol.Ignore, allow_fallbacks = false, require_parameters = true }, Json),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(Json);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>The reply checked against what was asked. Entries that fail are rejected one by one and recorded; a reply that is not the
    /// expected object fails the page.</summary>
    internal static VlmPageObservation Validate(OcrPage page, string reply, IReadOnlyList<string> lineIds, IReadOnlyList<string> asked,
        IReadOnlyDictionary<string, Dictionary<string, string>> readings)
    {
        VlmPageObservation Make(IReadOnlyList<VlmLineObservation> lines, IReadOnlyList<VlmReadingChoice> choices, IReadOnlyList<string> rejected, string? failure) =>
            new(VlmPageObservation.CurrentVersion, page.Page, page.ImageSha256, AgentProtocol.ChatModel, PromptVersion, lineIds, asked, lines, choices, rejected, failure, null);
        JsonElement root;
        try { root = JsonDocument.Parse(reply).RootElement.Clone(); }
        catch (JsonException) { return Make([], [], [], "REPLY_NOT_JSON"); }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("protocolVersion", out var pv) || pv.ValueKind != JsonValueKind.String || pv.GetString() != VlmPageObservation.CurrentVersion)
            return Make([], [], [], "PROTOCOL_VERSION_INVALID");
        var rejected = new List<string>();
        var lines = new List<VlmLineObservation>();
        var choices = new List<VlmReadingChoice>();
        var listed = lineIds.ToHashSet(StringComparer.Ordinal);
        var cropped = asked.ToHashSet(StringComparer.Ordinal);
        static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var e in ls.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.Object ? S(e, "lineId") : null;
                string? b = id is null ? null : S(e, "appearsBold"), sz = id is null ? null : S(e, "relativeSize"), al = id is null ? null : S(e, "alignment");
                if (id is null || !listed.Contains(id)) { rejected.Add("line:UNKNOWN_LINE_ID:" + (id ?? "?")); continue; }
                if (lines.Any(x => x.LineId == id)) { rejected.Add("line:DUPLICATE:" + id); continue; }
                if (!VlmPageObservation.BoldValues.Contains(b) || !VlmPageObservation.SizeValues.Contains(sz) || !VlmPageObservation.AlignmentValues.Contains(al))
                { rejected.Add("line:VALUE_NOT_IN_CLOSED_SET:" + id); continue; }
                lines.Add(new VlmLineObservation(id, b!, sz!, al!));
            }
        if (root.TryGetProperty("readings", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var e in rs.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.Object ? S(e, "lineId") : null;
                if (id is null || !cropped.Contains(id)) { rejected.Add("reading:NOT_ASKED:" + (id ?? "?")); continue; }
                if (choices.Any(x => x.LineId == id)) { rejected.Add("reading:DUPLICATE:" + id); continue; }
                var choice = S(e, "choice");
                if (choice is null || !(readings[id].ContainsKey(choice) || choice is VlmPageObservation.NoReading or VlmPageObservation.Unreadable))
                { rejected.Add("reading:CHOICE_NOT_SHOWN:" + id); continue; }
                var proposed = S(e, "proposedReading");
                if (proposed is not null && (choice != VlmPageObservation.NoReading || proposed.Trim().Length == 0 || proposed.Length > MaxProposedChars))
                { rejected.Add("reading:PROPOSAL_NOT_ALLOWED:" + id); proposed = null; }
                choices.Add(new VlmReadingChoice(id, choice, proposed));
            }
        return Make(lines, choices, rejected, null);
    }
}
