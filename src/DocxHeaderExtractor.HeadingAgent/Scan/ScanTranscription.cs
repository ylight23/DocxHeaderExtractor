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
/// crop, never an OCR reading, so it cannot be drawn to one. Measured failure: it may copy a fragment of the line above that the crop's margin
/// caught (twice in 120 at 1x, with a line break). So a transcription is a reading only when it passes two checks against the primary OCR
/// reading of the same box: no line break, and not much longer (<see cref="Guard"/>). The program edits no reading; it records which one is shown.
/// </summary>
public static class ScanTranscription
{
    public const string PromptVersion = "SCAN_VLM_TRANSCRIBE_V1";
    public const string Accepted = "ACCEPTED", RejectedMultiline = "REJECTED_MULTILINE", RejectedLonger = "REJECTED_LONGER_THAN_OCR",
        RejectedEmpty = "REJECTED_EMPTY", NotTranscribed = "NOT_TRANSCRIBED";
    public const int MaxTextChars = 400, LongerByChars = 6;
    public const double LongerRatio = 1.5;

    public const string SystemPrompt = "Each image is one line cropped from a page of a scanned document. Transcribe exactly what each image shows, letter for letter: every diacritic, capital letter, digit and punctuation mark as printed. Do not correct spelling, do not complete words cut at the edge, do not add or remove anything, and never use what you know or expect a document of this kind to say. Write a character you cannot read as ?. Ignore fragments of the lines above and below that are cut by the edge of the image.\n"
        + "Answer exactly one JSON object and nothing else: {\"protocolVersion\":\"" + PromptVersion + "\",\"lines\":[{\"id\":\"<id>\",\"text\":\"<transcription>\"}]}, with one entry for every image, using the id given just before it.";

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>NFC, runs of white space as one space, trimmed.</summary>
    public static string Norm(string s) => string.Join(' ', s.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The check of a transcription against the primary OCR reading of the same box: a line break means the model read more than one
    /// line; a text at least <see cref="LongerRatio"/> times or <see cref="LongerByChars"/> characters longer than the OCR reading is likely to
    /// hold a neighbour's words. Either way the OCR reading stays the one shown.</summary>
    public static string Guard(string? transcription, string ocr)
    {
        if (transcription is null) return NotTranscribed;
        if (transcription.Contains('\n') || transcription.Contains('\r')) return RejectedMultiline;
        var t = Norm(transcription);
        if (t.Length == 0) return RejectedEmpty;
        var o = Norm(ocr).Length;
        return t.Length >= o + LongerByChars || t.Length >= o * LongerRatio && t.Length - o >= 2 ? RejectedLonger : Accepted;
    }

    /// <summary>
    /// Sends the crops in batches of at most <paramref name="maxPerRequest"/> that stay under the transport's byte cap, each crop after its id,
    /// and returns the text the model gave for each (null where none came back). A batch is stored under <paramref name="cacheDir"/> by what
    /// its crops are made of (<paramref name="keys"/>: image hash, box, scale; not the bytes, which carry ImageMagick's time stamp), and read back.
    /// The budget is made by <paramref name="budgetFor"/> once the batches are cut, from their number: how many crops fit a request depends on
    /// their bytes (on 023, about five grey 2x crops of body lines), so it cannot be planned from the line count.
    /// </summary>
    internal static (string?[] Texts, AgentBudget Budget) Transcribe(IReadOnlyList<string?> crops, IReadOnlyList<string> ids, IReadOnlyList<string> keys,
        string cacheDir, string apiKey, Func<int, AgentBudget> budgetFor, Func<HttpMessageHandler> factory, List<string> failures, string label,
        int maxPerRequest, int parallelism)
    {
        var batches = new List<List<int>>();
        var current = new List<int>();
        var content = new JsonArray();
        foreach (var k in Enumerable.Range(0, crops.Count).Where(k => crops[k] is not null))
        {
            var part = new JsonNode[] { Text("id " + ids[k]), Image(crops[k]!) };
            if (current.Count > 0 && (current.Count >= maxPerRequest
                || Request(content.Select(c => c!.DeepClone()).Concat(part.Select(p => p.DeepClone()))).Bytes > AgentBudget.MaxRequestBytes))
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
        Parallel.For(0, batches.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, b =>
        {
            var take = batches[b];
            var name = $"{label}-B{b + 1:000}";
            var key = TesseractOcr.Sha256(string.Join("|", AgentProtocol.ChatModel, PromptVersion, SystemPrompt, string.Join(",", take.Select(k => ids[k] + "=" + keys[k]))));
            var file = Path.Combine(cacheDir, key[..24] + ".json");
            var reply = File.Exists(file) ? File.ReadAllText(file, Utf8) : null;
            if (reply is null)
            {
                var request = Request(take.SelectMany(k => new[] { Text("id " + ids[k]), Image(crops[k]!) }));
                for (var attempt = 0; reply is null; attempt++)
                {
                    if (budget.Admit(request) is { } refusal) { problems[b] = $"{name}:BUDGET:{refusal}"; return; }
                    try
                    {
                        using var handler = factory();
                        using var model = new AgentModel(handler, apiKey, budget);
                        reply = model.Turn(name, 1 + attempt, request).Content ?? "";
                    }
                    catch (ReplyFailure f) when (attempt < AgentUnit.TransportRetries && AgentUnit.RetriableTransport.Contains(f.Code, StringComparer.Ordinal)) { }
                    catch (ReplyFailure f) { problems[b] = $"{name}:CALL:{f.Code}"; return; }
                }
                if (AgentProtocol.TryUnwrapFence(reply, out var inner)) reply = inner;
            }
            var parsed = Parse(reply, take.Select(k => ids[k]).ToArray(), out var problem);
            if (parsed is null) { problems[b] = $"{name}:{problem}"; return; }
            foreach (var k in take) result[k] = parsed.GetValueOrDefault(ids[k]);
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(cacheDir);
                var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, reply, Utf8);
                try { File.Move(temp, file); } catch (IOException) when (File.Exists(file)) { File.Delete(temp); }
            }
        });
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
    internal static PreparedRequest Request(IEnumerable<JsonNode> content)
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
            ["reasoning"] = new JsonObject { ["enabled"] = true },
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { AgentProtocol.ProviderTag }, ignore = AgentProtocol.Ignore, allow_fallbacks = false, require_parameters = true }, Relaxed),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(Relaxed);
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>What a crop is made of, for the store key: the page image, the box, the margin, the scale.</summary>
    internal static string CropKey(string imageSha256, PixelBox box, int pad, double scale) =>
        string.Join(":", imageSha256, box.Left, box.Top, box.Width, box.Height, pad, scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// The transcription preflight of a scanned document: every line of the primary OCR view, cropped from its page image with a margin and
/// enlarged <see cref="Options.Scale"/> times, transcribed blind by the vision model, and checked (<see cref="ScanTranscription.Guard"/>). Runs
/// before the agent conversation, through the audited transport; batches are stored and read back, so a second run sends nothing.
/// </summary>
public sealed class ScanTranscriptionPreflight(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null)
{
    public const int CropPad = 8;

    /// <param name="CeilingUsd">The cost ceiling of the run; null for the worst case of the batches cut (each call reserves the worst cost of a
    /// request at the byte cap, about 0.026 USD; measured on 023: 0.0167 USD for 119 transcription and 34 vision calls).</param>
    public sealed record Options(int MaxPages = 300, double Scale = 2.0, int MaxLinesPerRequest = 10, int Parallelism = 4, double? CeilingUsd = null);

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
                keys[k] = ScanTranscription.CropKey(page.ImageSha256, line.Box, CropPad, options.Scale);
                var image = ScanOcrPreflight.PageImagePath(evidenceEntry, page.Page);
                var file = Path.Combine(work, $"{k:00000}.png");
                crops[k] = image is not null && TesseractOcr.CropScaled(magick, image, file, line.Box, CropPad, options.Scale) ? file : null;
            });
            failures.AddRange(Enumerable.Range(0, lines.Length).Where(k => crops[k] is null).Select(k => $"{lines[k].Line.Id}:CROP_FAILED"));
            AgentBudget BudgetFor(int batches) => new(batches * (1 + AgentUnit.TransportRetries), options.CeilingUsd ?? batches * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes));
            var (texts, budget) = ScanTranscription.Transcribe(crops, lines.Select(l => l.Line.Id).ToArray(), keys, Path.Combine(storeRoot, "vlm-transcribe"), apiKey, BudgetFor,
                handlerFactory ?? (() => AgentModel.CreateHandler()), failures, "TRANSCRIBE", options.MaxLinesPerRequest, options.Parallelism);
            var result = lines.Select((l, k) => new LineTranscription(l.Line.Id, texts[k], ScanTranscription.Guard(texts[k], l.Line.Text))).ToArray();
            return new TranscriptionSet(TranscriptionSet.CurrentVersion, AgentProtocol.ChatModel, ScanTranscription.PromptVersion, options.Scale, result,
                failures, budget.Calls, budget.ActualUsd);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
