using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// Measures how well each reader reads the lines of a scan against a gold transcription (<c>SCAN_LINE_TEXT_GOLD_V1</c>): the two OCR views,
/// the better of the two (an oracle no reader can reach), and the vision model transcribing each line crop blind, without any OCR reading
/// shown, from the black-and-white crop the vision preflight sends and from the same crop enlarged <see cref="EnlargedScale"/> times. It changes no evidence and decides nothing: it
/// reports exact matches and character error rates so that the owner can choose where the text of a heading comes from.
/// </summary>
public sealed class ScanTextEvaluation(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string GoldVersion = "SCAN_LINE_TEXT_GOLD_V1", ReportVersion = "SCAN_TEXT_EVAL_V1", PromptVersion = "SCAN_VLM_TRANSCRIBE_V1";
    public const int MaxLinesPerRequest = 10, MaxTextChars = 400;
    public const double MatchIou = 0.5, EnlargedScale = 2.0;

    public const string SystemPrompt = "Each image is one line cropped from a page of a scanned document. Transcribe exactly what each image shows, letter for letter: every diacritic, capital letter, digit and punctuation mark as printed. Do not correct spelling, do not complete words cut at the edge, do not add or remove anything, and never use what you know or expect a document of this kind to say. Write a character you cannot read as ?. Ignore fragments of the lines above and below that are cut by the edge of the image.\n"
        + "Answer exactly one JSON object and nothing else: {\"protocolVersion\":\"" + PromptVersion + "\",\"lines\":[{\"id\":\"<id>\",\"text\":\"<transcription>\"}]}, with one entry for every image, using the id given just before it.";

    public sealed record GoldLine(int N, int Page, PixelBox Box, string Text);

    public sealed record LineResult(int N, int Page, string Gold, IReadOnlyDictionary<string, string?> Readings);

    public sealed record MethodScore(string Method, int Exact, int Lines, double CharacterErrorRate);

    public sealed record Report(string Version, string SourceSha256, string Model, string PromptVersion, IReadOnlyList<MethodScore> Scores,
        IReadOnlyList<LineResult> Lines, IReadOnlyList<string> Failures, int ProviderCalls, double ProviderCostUsd);

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public const string Original = ScanOcrPreflight.OriginalView, Upscaled = "ocr-upscaled", BestView = "oracle-better-ocr-view",
        VlmBilevel = "vlm-blind-crop", VlmEnlarged = "vlm-blind-crop-2x";

    public static IReadOnlyList<GoldLine> ReadGold(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = doc.RootElement;
        if (root.GetProperty("version").GetString() != GoldVersion) throw new InvalidOperationException("GOLD_VERSION_INVALID");
        return root.GetProperty("lines").EnumerateArray().Select(l =>
        {
            var b = l.GetProperty("box");
            return new GoldLine(l.GetProperty("n").GetInt32(), l.GetProperty("page").GetInt32(),
                new PixelBox(b.GetProperty("left").GetInt32(), b.GetProperty("top").GetInt32(), b.GetProperty("width").GetInt32(), b.GetProperty("height").GetInt32()),
                l.GetProperty("text").GetString()!);
        }).ToArray();
    }

    public Report Run(string docxPath, IReadOnlyList<GoldLine> gold, string apiKey, double ceilingUsd = 1.0, ScanOcrPreflight.Options? ocrOptions = null)
    {
        var ocr = new ScanOcrPreflight(Path.Combine(storeRoot, ScanHeadingAgentSelector.OcrFolder));
        var evidence = ocr.Run(docxPath, ocrOptions);
        var entry = ocr.EntryOf(evidence);
        var magick = TesseractOcr.FindImageMagick() ?? throw new InvalidOperationException("IMAGEMAGICK_NOT_FOUND");
        var readings = gold.Select(g => new Dictionary<string, string?>(StringComparer.Ordinal)).ToArray();
        foreach (var view in evidence.Views)
            for (var k = 0; k < gold.Count; k++)
                readings[k][view.Spec.ViewId] = Match(view, gold[k]);
        for (var k = 0; k < gold.Count; k++)
        {
            var options = new[] { Original, Upscaled }.Select(v => readings[k].GetValueOrDefault(v)).Where(r => r is not null).ToArray();
            readings[k][BestView] = options.Length == 0 ? null : options.MinBy(r => Distance(Norm(gold[k].Text), Norm(r!)));
        }

        var failures = new List<string>();
        var budget = new AgentBudget(4 * (gold.Count / MaxLinesPerRequest + 2) * (1 + AgentUnit.TransportRetries), ceilingUsd);
        var factory = handlerFactory ?? (() => AgentModel.CreateHandler());
        var work = Path.Combine(Path.GetTempPath(), "dhx-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var (method, enlarged) in new[] { (VlmBilevel, false), (VlmEnlarged, true) })
            {
                var crops = new string?[gold.Count];
                var keys = new string[gold.Count];
                for (var k = 0; k < gold.Count; k++)
                {
                    var image = ScanOcrPreflight.PageImagePath(entry, gold[k].Page);
                    var file = Path.Combine(work, $"{method}-{k:000}.png");
                    var ok = image is not null && (enlarged ? TesseractOcr.CropScaled(magick, image, file, gold[k].Box, ScanVisionPreflight.CropPad, EnlargedScale)
                        : TesseractOcr.Crop(magick, image, file, gold[k].Box, ScanVisionPreflight.CropPad));
                    crops[k] = ok ? file : null;
                    // What the crop is made of, not its bytes: ImageMagick writes the time into every PNG it makes.
                    var sha = evidence.Views[0].Pages.FirstOrDefault(p => p.Page == gold[k].Page)?.ImageSha256 ?? "";
                    keys[k] = string.Join(":", sha, method, gold[k].Box.Left, gold[k].Box.Top, gold[k].Box.Width, gold[k].Box.Height, ScanVisionPreflight.CropPad, TesseractOcr.BilevelThreshold, EnlargedScale);
                    if (!ok) failures.Add($"{method}:L{gold[k].N:000}:CROP_FAILED");
                }
                var texts = Transcribe(crops, keys, apiKey, budget, factory, failures, method);
                for (var k = 0; k < gold.Count; k++) readings[k][method] = texts[k];
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        var lines = gold.Select((g, k) => new LineResult(g.N, g.Page, g.Text, (IReadOnlyDictionary<string, string?>)readings[k])).ToArray();
        var methods = new[] { Original, Upscaled, BestView, VlmBilevel, VlmEnlarged };
        var scores = methods.Select(m => Score(m, lines)).ToArray();
        var report = new Report(ReportVersion, evidence.SourceSha256, AgentProtocol.ChatModel, PromptVersion, scores, lines, failures, budget.Calls, budget.ActualUsd);
        var dir = Path.Combine(storeRoot, "eval");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, evidence.SourceSha256[..16] + "-scan-text-eval.json"), JsonSerializer.Serialize(report, Json), Utf8);
        return report;
    }

    /// <summary>The reading of the view's line that overlaps the gold box most, when it overlaps enough to be the same line.</summary>
    static string? Match(OcrView view, GoldLine g)
    {
        var page = view.Pages.FirstOrDefault(p => p.Page == g.Page);
        var best = page?.Lines.Select(l => (Iou: Iou(l.Box, g.Box), l.Text)).MaxBy(x => x.Iou);
        return best is { } b && b.Iou >= MatchIou ? b.Text : null;
    }

    static double Iou(PixelBox a, PixelBox b)
    {
        var w = Math.Min(a.Left + a.Width, b.Left + b.Width) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top);
        var inter = (double)Math.Max(0, w) * Math.Max(0, h);
        var union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Blind transcriptions, in batches that stay under the transport's byte cap; a batch is stored once per model, prompt and crops.</summary>
    string?[] Transcribe(string?[] crops, string[] cropKeys, string apiKey, AgentBudget budget, Func<HttpMessageHandler> factory, List<string> failures, string method)
    {
        var result = new string?[crops.Length];
        var pending = Enumerable.Range(0, crops.Length).Where(k => crops[k] is not null).ToList();
        var batch = 0;
        while (pending.Count > 0)
        {
            var take = new List<int>();
            var content = new JsonArray();
            foreach (var k in pending.Take(MaxLinesPerRequest))
            {
                var part = new JsonNode[] { Text("id T" + (k + 1).ToString("000", CultureInfo.InvariantCulture)), Image(crops[k]!) };
                if (take.Count > 0 && Request(content.Select(c => c!.DeepClone()).Concat(part.Select(p => p.DeepClone()))).Bytes > AgentBudget.MaxRequestBytes) break;
                foreach (var p in part) content.Add(p);
                take.Add(k);
            }
            pending.RemoveRange(0, take.Count);
            batch++;
            var request = Request(content.Select(c => c!.DeepClone()));
            var key = TesseractOcr.Sha256(string.Join("|", AgentProtocol.ChatModel, PromptVersion, SystemPrompt,
                string.Join(",", take.Select(k => "T" + (k + 1) + "=" + cropKeys[k]))));
            var file = Path.Combine(storeRoot, "vlm-transcribe", key[..24] + ".json");
            string? reply = File.Exists(file) ? File.ReadAllText(file, Utf8) : null;
            if (reply is null)
            {
                if (budget.Admit(request) is { } refusal) { failures.Add($"{method}:B{batch:00}:BUDGET:{refusal}"); continue; }
                try
                {
                    using var handler = factory();
                    using var model = new AgentModel(handler, apiKey, budget);
                    reply = model.Turn($"EVAL-{method}-B{batch:00}", 1, request).Content ?? "";
                }
                catch (ReplyFailure f) { failures.Add($"{method}:B{batch:00}:CALL:{f.Code}"); continue; }
                if (AgentProtocol.TryUnwrapFence(reply, out var inner)) reply = inner;
            }
            var parsed = Parse(reply, take.Select(k => "T" + (k + 1).ToString("000", CultureInfo.InvariantCulture)).ToArray(), out var problem);
            if (parsed is null) { failures.Add($"{method}:B{batch:00}:{problem}"); continue; }
            foreach (var k in take) result[k] = parsed.GetValueOrDefault("T" + (k + 1).ToString("000", CultureInfo.InvariantCulture));
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, reply, Utf8);
            }
        }
        return result;
    }

    /// <summary>The transcriptions by id; null when the reply is not the expected object. An id not asked, a repeated id or an overlong text is
    /// left out (that line then has no transcription and scores as wrong).</summary>
    internal static Dictionary<string, string>? Parse(string reply, IReadOnlyList<string> ids, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("protocolVersion", out var v) || v.GetString() != PromptVersion)
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

    static PreparedRequest Request(IEnumerable<JsonNode> content)
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
            ["provider"] = JsonSerializer.SerializeToNode(new { only = new[] { AgentProtocol.ProviderTag }, ignore = AgentProtocol.Ignore, allow_fallbacks = false, require_parameters = true }),
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        var text = body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PreparedRequest(text, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static MethodScore Score(string method, IReadOnlyList<LineResult> lines)
    {
        int exact = 0, errors = 0, chars = 0;
        foreach (var l in lines)
        {
            var gold = Norm(l.Gold);
            var read = Norm(l.Readings.GetValueOrDefault(method) ?? "");
            if (gold == read) exact++;
            errors += Distance(gold, read);
            chars += gold.Length;
        }
        return new MethodScore(method, exact, lines.Count, chars == 0 ? 0 : (double)errors / chars);
    }

    /// <summary>NFC, runs of white space as one space, trimmed: the comparison ignores only how spaces were written.</summary>
    public static string Norm(string s) => string.Join(' ', s.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static int Distance(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var cur = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
