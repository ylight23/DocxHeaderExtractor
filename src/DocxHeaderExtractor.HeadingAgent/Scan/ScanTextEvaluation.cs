using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// Measures how well each reader reads the lines of a scan against a gold transcription (<c>SCAN_LINE_TEXT_GOLD_V1</c>): the two OCR views,
/// the better of the two (an oracle no reader can reach), the vision model transcribing each line crop blind (<see cref="ScanTranscription"/>),
/// without any OCR reading shown, from the black-and-white crop the vision preflight sends, from the same crop enlarged
/// <see cref="EnlargedScale"/> times in grey (smoothed) and in black and white (pixels repeated), and what the scan route shows: the 2x black-and-white masked transcription where it passes the program's checks, else the
/// primary OCR reading. It changes no evidence and decides nothing: it reports exact matches and character error rates.
/// </summary>
public sealed class ScanTextEvaluation(string storeRoot, Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string GoldVersion = "SCAN_LINE_TEXT_GOLD_V1", ReportVersion = "SCAN_TEXT_EVAL_V1", PromptVersion = ScanTranscription.PromptVersion;
    public const int MaxLinesPerRequest = 10;
    public const double MatchIou = 0.5, EnlargedScale = 2.0;
    public const string SystemPrompt = ScanTranscription.SystemPrompt;

    public sealed record GoldLine(int N, int Page, PixelBox Box, string Text);

    public sealed record LineResult(int N, int Page, string Gold, IReadOnlyDictionary<string, string?> Readings);

    public sealed record MethodScore(string Method, int Exact, int Lines, double CharacterErrorRate);

    /// <param name="CropBytes">The bytes of every crop sent, by method: what the byte cap of a request has to hold (the model is billed by pixels,
    /// which are the same at one scale whatever the format).</param>
    public sealed record Report(string Version, string SourceSha256, string Model, string PromptVersion, IReadOnlyList<MethodScore> Scores,
        IReadOnlyList<LineResult> Lines, IReadOnlyList<string> Failures, int ProviderCalls, double ProviderCostUsd, IReadOnlyDictionary<string, long> CropBytes);

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public const string Original = ScanOcrPreflight.OriginalView, Upscaled = "ocr-upscaled", BestView = "oracle-better-ocr-view",
        VlmBilevel = "vlm-blind-crop", VlmEnlarged = "vlm-blind-crop-2x", VlmEnlargedBilevel = "vlm-blind-crop-2x-bilevel",
        VlmEnlargedMasked = "vlm-blind-crop-2x-masked", VlmEnlargedBilevelMasked = "vlm-blind-crop-2x-bilevel-masked", Shown = "shown-by-scan-route";

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

    public Report Run(string docxPath, IReadOnlyList<GoldLine> gold, string apiKey, double? ceilingUsd = null, ScanOcrPreflight.Options? ocrOptions = null)
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
        var calls = 0;
        var cost = 0.0;
        var cropBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var factory = handlerFactory ?? (() => AgentModel.CreateHandler());
        var work = Path.Combine(Path.GetTempPath(), "dhx-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var primary = evidence.Views.First(v => v.Spec.ViewId == Original);
            foreach (var (method, enlarged, bilevel, masked) in new[]
            {
                (VlmBilevel, false, true, false), (VlmEnlarged, true, false, false), (VlmEnlargedBilevel, true, true, false),
                (VlmEnlargedMasked, true, false, true), (VlmEnlargedBilevelMasked, true, true, true),
            })
            {
                var crops = new string?[gold.Count];
                var keys = new string[gold.Count];
                for (var k = 0; k < gold.Count; k++)
                {
                    var image = ScanOcrPreflight.PageImagePath(entry, gold[k].Page);
                    var file = Path.Combine(work, $"{method}-{k:000}.png");
                    // The other lines of the page that the margin reaches, as the primary OCR view has them.
                    var mask = masked && primary.Pages.FirstOrDefault(p => p.Page == gold[k].Page) is { } maskPage
                        ? TesseractOcr.BoxesInCrop(gold[k].Box, ScanVisionPreflight.CropPad, maskPage.Lines.Where(l => l != MatchLine(primary, gold[k])).Select(l => l.Box))
                        : null;
                    var ok = image is not null && (enlarged ? TesseractOcr.CropScaled(magick, image, file, gold[k].Box, ScanVisionPreflight.CropPad, EnlargedScale, bilevel, mask)
                        : TesseractOcr.Crop(magick, image, file, gold[k].Box, ScanVisionPreflight.CropPad));
                    crops[k] = ok ? file : null;
                    var sha = evidence.Views[0].Pages.FirstOrDefault(p => p.Page == gold[k].Page)?.ImageSha256 ?? "";
                    keys[k] = method + ":" + ScanTranscription.CropKey(sha, gold[k].Box, ScanVisionPreflight.CropPad, enlarged ? EnlargedScale : 1) + ":" + TesseractOcr.BilevelThreshold
                        + (mask is null ? "" : ":mask=" + string.Join(";", mask.Select(m => $"{m.Left},{m.Top},{m.Width},{m.Height}")));
                    if (ok) cropBytes[method] = cropBytes.GetValueOrDefault(method) + new FileInfo(file).Length;
                    if (!ok) failures.Add($"{method}:L{gold[k].N:000}:CROP_FAILED");
                }
                var ids = gold.Select(g => "T" + g.N.ToString("000", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var (texts, budget) = ScanTranscription.Transcribe(crops, ids, keys, Path.Combine(storeRoot, "vlm-transcribe"), apiKey,
                    n => new AgentBudget(n * (2 + AgentUnit.TransportRetries), ceilingUsd ?? n * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes)), factory, failures, method, MaxLinesPerRequest, parallelism: 2);
                calls += budget.Calls;
                cost += budget.ActualUsd;
                for (var k = 0; k < gold.Count; k++) readings[k][method] = texts[k];
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        // What the scan route shows: the 2x black-and-white masked transcription when it passes the checks against the primary OCR reading, else that reading.
        for (var k = 0; k < gold.Count; k++)
        {
            var ocrText = readings[k].GetValueOrDefault(Original);
            var t = readings[k].GetValueOrDefault(VlmEnlargedBilevelMasked);
            var primary = evidence.Views.First(v => v.Spec.ViewId == Original);
            var neighbours = primary.Pages.FirstOrDefault(p => p.Page == gold[k].Page) is { } page && MatchLine(primary, gold[k]) is { } own
                ? ScanTranscription.Neighbours(page, own).Select(n => n.Text) : [];
            readings[k][Shown] = ocrText is not null && ScanTranscription.Guard(t, ocrText, neighbours) == ScanTranscription.Accepted ? t : ocrText;
        }
        var lines = gold.Select((g, k) => new LineResult(g.N, g.Page, g.Text, (IReadOnlyDictionary<string, string?>)readings[k])).ToArray();
        var methods = new[] { Original, Upscaled, BestView, VlmBilevel, VlmEnlarged, VlmEnlargedBilevel, VlmEnlargedMasked, VlmEnlargedBilevelMasked, Shown };
        var scores = methods.Select(m => Score(m, lines)).ToArray();
        var report = new Report(ReportVersion, evidence.SourceSha256, AgentProtocol.ChatModel, PromptVersion, scores, lines, failures, calls, cost, cropBytes);
        var dir = Path.Combine(storeRoot, "eval");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, evidence.SourceSha256[..16] + "-scan-text-eval.json"), JsonSerializer.Serialize(report, Json), Utf8);
        return report;
    }

    /// <summary>The reading of the view's line that overlaps the gold box most, when it overlaps enough to be the same line.</summary>
    static string? Match(OcrView view, GoldLine g) => MatchLine(view, g)?.Text;

    static OcrLine? MatchLine(OcrView view, GoldLine g)
    {
        var page = view.Pages.FirstOrDefault(p => p.Page == g.Page);
        var best = page?.Lines.Select(l => (Iou: Iou(l.Box, g.Box), Line: l)).MaxBy(x => x.Iou);
        return best is { } b && b.Iou >= MatchIou ? b.Line : null;
    }

    static double Iou(PixelBox a, PixelBox b)
    {
        var w = Math.Min(a.Left + a.Width, b.Left + b.Width) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top);
        var inter = (double)Math.Max(0, w) * Math.Max(0, h);
        var union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    internal static Dictionary<string, string>? Parse(string reply, IReadOnlyList<string> ids, out string? problem) => ScanTranscription.Parse(reply, ids, out problem);

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
    public static string Norm(string s) => ScanTranscription.Norm(s);

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
