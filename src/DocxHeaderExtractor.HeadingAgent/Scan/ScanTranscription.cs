using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>The vision model's blind transcription of one OCR line, and whether it passed the program's checks. Only an ACCEPTED one is a
/// reading of the line (<see cref="ScanAgentSource.VlmTranscribedReading"/>); a rejected one is kept as evidence with its reason.</summary>
public sealed record LineTranscription(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("status")] string Status);

/// <summary>The transcriptions of a document's lines with the audit of the run that made them (or read them back from the store).</summary>
public sealed record TranscriptionSet(string Version, string Model, string PromptVersion, double Scale, IReadOnlyList<LineTranscription> Lines,
    IReadOnlyList<string> Failures, int ProviderCalls, double ProviderCostUsd)
{
    public const string CurrentVersion = "SCAN_VLM_TRANSCRIPTION_V1";
    public int Accepted => Lines.Count(l => l.Status == ScanTranscription.Accepted);
}

/// <summary>
/// Blind transcription of OCR lines by the vision model (owner's decision 2026-10-16, after the measurement on 023: 120/120 heading lines exact
/// from crops enlarged 2x against 77/120 for the result text and at most 84/120 for any choice between the OCR views). The model sees only the
/// crop, never an OCR reading, so it cannot be drawn to one. Measured failures: it may copy the line above or below that the crop caught (at
/// 1x, twice in 120 heading lines, with a line break), and where OCR made a line of specks over a heading, the crop of that line shows the
/// heading, which the model transcribes again. So a transcription is a reading only when it passes the checks of <see cref="Guard"/> against
/// the primary OCR reading of the same box and of the lines next to it. The program edits no reading; it records which one is shown.
/// </summary>
public static class ScanTranscription
{
    public const string PromptVersion = "SCAN_VLM_TRANSCRIBE_V1";
    public const string Accepted = "ACCEPTED", RejectedMultiline = "REJECTED_MULTILINE", RejectedLonger = "REJECTED_LONGER_THAN_OCR",
        RejectedNeighbour = "REJECTED_NEIGHBOUR_TEXT", RejectedEmpty = "REJECTED_EMPTY", NotTranscribed = "NOT_TRANSCRIBED";
    public const int MaxTextChars = 400, LongerByChars = 10, MinNeighbourChars = 3;
    public const double LongerRatio = 2.0, NeighbourMargin = 0.1;

    public const string SystemPrompt = "Each image is one line cropped from a page of a scanned document. Transcribe exactly what each image shows, letter for letter: every diacritic, capital letter, digit and punctuation mark as printed. Do not correct spelling, do not complete words cut at the edge, do not add or remove anything, and never use what you know or expect a document of this kind to say. Write a character you cannot read as ?. Ignore fragments of the lines above and below that are cut by the edge of the image.\n"
        + "Answer exactly one JSON object and nothing else: {\"protocolVersion\":\"" + PromptVersion + "\",\"lines\":[{\"id\":\"<id>\",\"text\":\"<transcription>\"}]}, with one entry for every image, using the id given just before it.";

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>NFC, runs of white space as one space, trimmed.</summary>
    public static string Norm(string s) => string.Join(' ', s.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The checks of a transcription against the primary OCR reading of its box and the readings of the lines next to it, all compared as their
    /// letters and digits without diacritics (<see cref="Skeleton"/>), so that dots and dashes the OCR dropped from a form's leaders do not
    /// count: a line break means the model read more than one line (<see cref="RejectedMultiline"/>); a text closer to a neighbour, or to a
    /// neighbour joined to the line, than to the line itself is a neighbour's text (<see cref="RejectedNeighbour"/>: the line above caught by the
    /// margin, or a line of specks over a heading); a text <see cref="LongerByChars"/> letters and <see cref="LongerRatio"/> times longer than the
    /// OCR is refused as well (<see cref="RejectedLonger"/>). A shorter text is accepted: OCR often reads a short heading as noise ("Mục 1" read
    /// "¬ MueL n ca"). Whatever is refused, the OCR reading stays the one shown.
    /// </summary>
    public static string Guard(string? transcription, string ocr, IEnumerable<string>? neighbours = null)
    {
        if (transcription is null) return NotTranscribed;
        if (transcription.Contains('\n') || transcription.Contains('\r')) return RejectedMultiline;
        if (Norm(transcription).Length == 0) return RejectedEmpty;
        var t = Skeleton(transcription);
        var own = Skeleton(ocr);
        var self = Similarity(t, own);
        foreach (var n in (neighbours ?? []).Select(Skeleton).Where(n => n.Length >= MinNeighbourChars))
            if (Similarity(t, n) > self + NeighbourMargin || Similarity(t, n + own) > self + NeighbourMargin || Similarity(t, own + n) > self + NeighbourMargin)
                return RejectedNeighbour;
        return t.Length >= own.Length + LongerByChars && t.Length >= own.Length * LongerRatio ? RejectedLonger : Accepted;
    }

    /// <summary>The letters and digits of a text, lower case, without diacritics (đ as d).</summary>
    public static string Skeleton(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) b.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        }
        return b.ToString();
    }

    /// <summary>One minus the edit distance over the longer length: 1 for the same text, 0 for nothing in common.</summary>
    public static double Similarity(string a, string b) => a.Length + b.Length == 0 ? 1 : 1 - (double)ScanTextEvaluation.Distance(a, b) / Math.Max(a.Length, b.Length);

    /// <summary>The lines whose text a masked crop of <paramref name="line"/> can still show: those whose box overlaps its own (the mask keeps
    /// the line's own box whole). A line of specks OCR made over a heading is one; the line above, merely close, is painted out.</summary>
    public static IEnumerable<OcrLine> Neighbours(OcrPage page, OcrLine line) =>
        page.Lines.Where(l => l.Id != line.Id
            && Math.Min(l.Box.Left + l.Box.Width, line.Box.Left + line.Box.Width) > Math.Max(l.Box.Left, line.Box.Left)
            && Math.Min(l.Box.Top + l.Box.Height, line.Box.Top + line.Box.Height) > Math.Max(l.Box.Top, line.Box.Top));

    /// <summary>
    /// Sends the crops in batches of at most <paramref name="maxPerRequest"/> that stay under the transport's byte cap, each crop after its id,
    /// and returns the text the model gave for each (null where none came back). A batch is stored under <paramref name="cacheDir"/> by what
    /// its crops are made of (<paramref name="keys"/>: image hash, box, scale; not the bytes, which carry ImageMagick's time stamp), and read back.
    /// The budget is made by <paramref name="budgetFor"/> once the batches are cut, from their number: how many crops fit a request depends on
    /// their bytes (on 023, about five grey 2x crops of body lines), so it cannot be planned from the line count.
    /// </summary>
    internal static (string?[] Texts, AgentBudget Budget) Transcribe(IReadOnlyList<string?> crops, IReadOnlyList<string> ids, IReadOnlyList<string> keys,
        string cacheDir, string apiKey, Func<int, AgentBudget> budgetFor, Func<HttpMessageHandler> factory, List<string> failures, string label,
        int maxPerRequest, int parallelism, string reasoning = ReasoningOn)
    {
        var batches = new List<List<int>>();
        var current = new List<int>();
        var content = new JsonArray();
        foreach (var k in Enumerable.Range(0, crops.Count).Where(k => crops[k] is not null))
        {
            var part = new JsonNode[] { Text("id " + ids[k]), Image(crops[k]!) };
            if (current.Count > 0 && (current.Count >= maxPerRequest
                || Request(content.Select(c => c!.DeepClone()).Concat(part.Select(p => p.DeepClone())), reasoning).Bytes > AgentBudget.MaxRequestBytes))
            {
                batches.Add(current);
                current = [];
                content = [];
            }
            foreach (var p in part) content.Add(p);
            current.Add(k);
        }
        if (current.Count > 0) batches.Add(current);
        var budget = budgetFor(batches.Count);

        var result = new string?[crops.Count];
        var problems = new string?[batches.Count];
        // A batch whose reply stays unreadable after the second ask is split in two, down to single lines: one crop can make the model answer
        // with no text (CONTENT_NOT_TEXT, once in 218 batches on 023), and the others then still get read.
        string? Batch(List<int> take, string name)
        {
            var key = TesseractOcr.Sha256(string.Join("|", AgentProtocol.ChatModel, PromptVersion, SystemPrompt, string.Join(",", take.Select(k => ids[k] + "=" + keys[k])))
                + (reasoning == ReasoningOn ? "" : "|reasoning=" + reasoning));
            var file = Path.Combine(cacheDir, key[..24] + ".json");
            var asked = take.Select(k => ids[k]).ToArray();
            var reply = File.Exists(file) ? File.ReadAllText(file, Utf8) : null;
            Dictionary<string, string>? parsed = null;
            string? problem = null;
            var splittable = false;
            if (reply is not null) parsed = Parse(reply, asked, out problem);
            else
            {
                // A transport failure that may pass, a reply that is not text, or one that is not the expected object (measured on 023: two
                // PROTOCOL_VERSION_INVALID and one CONTENT_NOT_TEXT in 218 batches) is asked once more, the same request.
                var request = Request(take.SelectMany(k => new[] { Text("id " + ids[k]), Image(crops[k]!) }), reasoning);
                int attempt = 0, transportRetries = 0, replyRetries = 0;
                while (parsed is null)
                {
                    if (budget.Admit(request) is { } refusal) return $"{name}:BUDGET:{refusal}";
                    try
                    {
                        using var handler = factory();
                        using var model = new AgentModel(handler, apiKey, budget);
                        reply = model.Turn(name, ++attempt, request).Content ?? "";
                    }
                    catch (ReplyFailure f) when (transportRetries < AgentUnit.TransportRetries && AgentUnit.RetriableTransport.Contains(f.Code, StringComparer.Ordinal)) { transportRetries++; continue; }
                    catch (ReplyFailure f) when (replyRetries < 1 && f.Code.StartsWith("CONTENT:", StringComparison.Ordinal)) { replyRetries++; continue; }
                    catch (ReplyFailure f) { problem = "CALL:" + f.Code; splittable = f.Code.StartsWith("CONTENT:", StringComparison.Ordinal); break; }
                    if (AgentProtocol.TryUnwrapFence(reply, out var inner)) reply = inner;
                    parsed = Parse(reply, asked, out problem);
                    if (parsed is null && replyRetries++ >= 1) { splittable = true; break; }
                }
            }
            if (parsed is null)
            {
                if (!splittable || take.Count < 2) return $"{name}:{problem}";
                var half = take.Count / 2;
                var first = Batch(take[..half], name + "a");
                var second = Batch(take[half..], name + "b");
                return first is null && second is null ? null : string.Join(";", new[] { first, second }.Where(x => x is not null));
            }
            foreach (var k in take) result[k] = parsed.GetValueOrDefault(ids[k]);
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(cacheDir);
                var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, reply, Utf8);
                try { File.Move(temp, file); } catch (IOException) when (File.Exists(file)) { File.Delete(temp); }
            }
            return null;
        }
        Parallel.For(0, batches.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, b => problems[b] = Batch(batches[b], $"{label}-B{b + 1:000}"));
        failures.AddRange(problems.Where(p => p is not null)!);
        return (result, budget);
    }

    /// <summary>The transcriptions by id; null when the reply is not the expected object. An id not asked, a repeated id or an overlong text is
    /// left out (that line then has no transcription).</summary>
    internal static Dictionary<string, string>? Parse(string reply, IReadOnlyList<string> ids, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("protocolVersion", out var v) || v.ValueKind != JsonValueKind.String || v.GetString() != PromptVersion)
            { problem = "PROTOCOL_VERSION_INVALID"; return null; }
            if (!root.TryGetProperty("lines", out var ls) || ls.ValueKind != JsonValueKind.Array) { problem = "LINES_MISSING"; return null; }
            var asked = ids.ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in ls.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var key = id.GetString()!;
                if (!asked.Contains(key) || result.ContainsKey(key) || t.GetString()!.Length > MaxTextChars) continue;
                result[key] = t.GetString()!;
            }
            return result;
        }
        catch (JsonException) { problem = "REPLY_NOT_JSON"; return null; }
    }

    static JsonNode Text(string s) => new JsonObject { ["type"] = "text", ["text"] = s };

    static JsonNode Image(string path) => new JsonObject
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) },
    };

    /// <summary>One POST body for the audited transport: the pinned model, the transcription prompt, the crops, json_object, the locked provider
    /// block, streaming and usage, as the agent's own requests.</summary>
    /// <summary>The model's reasoning: on (the measured setting), off, or a number: its token budget.</summary>
    public const string ReasoningOn = "on", ReasoningOff = "off";

    static JsonObject ReasoningBlock(string reasoning) => reasoning switch
    {
        ReasoningOn => new JsonObject { ["enabled"] = true },
        ReasoningOff => new JsonObject { ["enabled"] = false },
        _ => new JsonObject { ["max_tokens"] = int.Parse(reasoning, System.Globalization.CultureInfo.InvariantCulture) },
    };

    internal static PreparedRequest Request(IEnumerable<JsonNode> content, string reasoning = ReasoningOn)
    {
        var body = new JsonObject
        {
            ["model"] = AgentProtocol.ChatModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(content.ToArray()) }),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["temperature"] = 0,
            ["max_tokens"] = AgentProtocol.MaxTokens,
            ["reasoning"] = ReasoningBlock(reasoning),
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { AgentProtocol.ProviderTag }, ignore = AgentProtocol.Ignore, allow_fallbacks = false, require_parameters = true }, Relaxed),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(Relaxed);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>What a crop is made of, for the store key: the page image, the box, the margin, the scale.</summary>
    internal static string CropKey(string imageSha256, PixelBox box, int pad, double scale, bool bilevel = false, IReadOnlyList<PixelBox>? mask = null) =>
        string.Join(":", imageSha256, box.Left, box.Top, box.Width, box.Height, pad, scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
        + (bilevel ? ":bilevel=" + TesseractOcr.BilevelThreshold : "")
        + (mask is null ? "" : ":mask=" + string.Join(";", mask.Select(m => $"{m.Left},{m.Top},{m.Width},{m.Height}")));
}

/// <summary>
/// The transcription preflight of a scanned document: every line of the primary OCR view, cropped from its page image with a margin, the
/// other lines the margin reaches painted out, enlarged <see cref="Options.Scale"/> times in black and white, transcribed blind by the vision model, and checked (<see cref="ScanTranscription.Guard"/>). Runs
/// before the agent conversation, through the audited transport; batches are stored and read back, so a second run sends nothing.
/// </summary>
public sealed class ScanTranscriptionPreflight(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null)
{
    public const int CropPad = 8;

    /// <param name="CeilingUsd">The cost ceiling of the run; null for the worst case of the batches cut (each call reserves the worst cost of a
    /// request at the byte cap, about 0.026 USD; measured on 023: 0.0167 USD for 119 transcription and 34 vision calls).</param>
    /// <param name="Bilevel">Black and white, pixels repeated (measured on 023 with <paramref name="Mask"/>: 120/120 heading lines, as grey 2x,
    /// in a file 6.6 times smaller).</param>
    /// <param name="Mask">The other lines' boxes that reach into the crop are painted white first (without: the line above copied twice in 120).</param>
    public sealed record Options(int MaxPages = 300, double Scale = 2.0, int MaxLinesPerRequest = 10, int Parallelism = 24, double? CeilingUsd = null,
        bool Bilevel = true, bool Mask = true);

    public TranscriptionSet Run(ScanOcrEvidence evidence, string evidenceEntry, string apiKey, Options? options = null)
    {
        options ??= new Options();
        var magick = TesseractOcr.FindImageMagick() ?? throw new InvalidOperationException("IMAGEMAGICK_NOT_FOUND");
        var primary = evidence.Views.First(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        var lines = primary.Pages.Take(options.MaxPages).SelectMany(p => p.Lines.Select(l => (Page: p, Line: l))).ToArray();
        var failures = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "dhx-transcribe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var crops = new string?[lines.Length];
            var keys = new string[lines.Length];
            Parallel.For(0, lines.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) }, k =>
            {
                var (page, line) = lines[k];
                var mask = options.Mask ? TesseractOcr.BoxesInCrop(line.Box, CropPad, page.Lines.Where(l => l.Id != line.Id).Select(l => l.Box)) : null;
                keys[k] = ScanTranscription.CropKey(page.ImageSha256, line.Box, CropPad, options.Scale, options.Bilevel, mask);
                var image = ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page);
                var file = Path.Combine(work, $"{k:00000}.png");
                crops[k] = image is not null && TesseractOcr.CropScaled(magick, image, file, line.Box, CropPad, options.Scale, options.Bilevel, mask) ? file : null;
            });
            failures.AddRange(Enumerable.Range(0, lines.Length).Where(k => crops[k] is null).Select(k => $"{lines[k].Line.Id}:CROP_FAILED"));
            // Room for the second asks and the splits of a batch that stays unreadable (rare): twice the batches' worst cost.
            AgentBudget BudgetFor(int batches) => new(batches * (2 + AgentUnit.TransportRetries) + lines.Length, options.CeilingUsd ?? 2 * batches * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes));
            var (texts, budget) = ScanTranscription.Transcribe(crops, lines.Select(l => l.Line.Id).ToArray(), keys, Path.Combine(storeRoot, "vlm-transcribe"), apiKey, BudgetFor,
                handlerFactory ?? (() => AgentModel.CreateHandler()), failures, "TRANSCRIBE", options.MaxLinesPerRequest, options.Parallelism);
            var result = lines.Select((l, k) => new LineTranscription(l.Line.Id, texts[k],
                ScanTranscription.Guard(texts[k], l.Line.Text, ScanTranscription.Neighbours(l.Page, l.Line).Select(n => n.Text)))).ToArray();
            return new TranscriptionSet(TranscriptionSet.CurrentVersion, AgentProtocol.ChatModel, ScanTranscription.PromptVersion, options.Scale, result,
                failures, budget.Calls, budget.ActualUsd);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
