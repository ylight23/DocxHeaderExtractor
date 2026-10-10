using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]

// P7-F1Q held-out visual review (Issue #6). Renders every selected page with PDFium (PDFtoImage 5.4.0, the renderer
// already pinned by P7ShapeAudit) and overlays the parser bbox of EVERY occurrence, numbered and coloured by its
// draft label, so each source alias can be checked against the real page. Source facts + draft labels only;
// no provider predictions. Not Gold.
// Optional 4th argument: comma-separated document ids, rendered in that order (default: every draft, by id).
if (args.Length is not (3 or 4)) throw new ArgumentException("<gold-draft-dir> <review-bundle-dir> <new-output-dir> [id,id,...]");
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new InvalidOperationException("OUTPUT_EXISTS");
var draftPaths = args.Length == 4
    ? args[3].Split(',').Select(i => Path.Combine(args[0], i.Trim() + ".gold-draft.json")).ToArray()
    : Directory.GetFiles(args[0], "*.gold-draft.json").Order(StringComparer.Ordinal).ToArray();
foreach (var p in draftPaths) if (!File.Exists(p)) throw new FileNotFoundException("DRAFT_MISSING", p);
Directory.CreateDirectory(output);
const int Dpi = 110;
var draftVersion = "";
var index = new StringBuilder();
index.AppendLine("# P7-F1Q held-out — visual Gold review (DRAFT, not approved)");
index.AppendLine();
index.AppendLine($"Each selected page is rendered at {Dpi} DPI with PDFium. Every parser occurrence is drawn as a numbered box: **green** = drafted ESTABLISHES, **blue** = REPRESENTS, **grey** = OTHER, **orange fill** = EXCLUDED (source-corrupted, never scored); an **orange** outline = REVIEW_FOCUS / DECISION_NEEDED, **purple** = row decided by the user. The number in the box is the row number in the document's table, which lists **all** occurrences (not only non-OTHER rows). Boxes come from parser geometry (PDF points, bottom-left origin) and are evidence of what the parser saw, not labels.");
index.AppendLine();
index.AppendLine("Each document page ends with **OTHER rows to check for a missed heading**: OTHER rows that are bold, or larger than the page's most common body size, and short. They come from glyph fonts only. This is a checking aid, not a proposal: no label was changed because of it.");
index.AppendLine();
index.AppendLine("| doc | source | pages | occurrences | E | R | O | excluded | focus | user-decided | OTHER to check |");
index.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
var hashes = new List<object>();
foreach (var draftPath in draftPaths)
{
    using var draft = JsonDocument.Parse(File.ReadAllBytes(draftPath));
    var d = draft.RootElement; var id = d.GetProperty("id").GetString()!; var key = d.GetProperty("sourceKey").GetString()!;
    draftVersion = d.GetProperty("version").GetString()!;
    var otherToCheck = 0;
    using var review = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], id + ".review.json")));
    var layout = review.RootElement.GetProperty("occurrences").EnumerateArray().ToDictionary(o => o.GetProperty("sourceAlias").GetString()!, o => o.Clone());
    var labels = d.GetProperty("labels").EnumerateArray().Select((l, i) => (No: i + 1, L: l.Clone())).ToArray();
    Directory.CreateDirectory(Path.Combine(output, id));
    var md = new StringBuilder();
    md.AppendLine($"# {id} — `{key}`");
    md.AppendLine();
    md.AppendLine($"Draft `{draftVersion}`, status `{d.GetProperty("status").GetString()}`, draft sha256 `{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(draftPath)))}`. Source sha256 `{d.GetProperty("sourceSha256").GetString()}`. [Back to index](README.md)");
    md.AppendLine();
    md.AppendLine($"Approve or correct with `review-apply` lines: `APPROVE {id} by <reviewer> [because <note>]`, `{id} <alias> -> E|R|O because <reason>`.");
    foreach (var page in labels.Select(x => x.L.GetProperty("page").GetInt32()).Distinct())
    {
        using var stream = File.OpenRead(key);
        using var bitmap = Conversion.ToImage(stream, page: page - 1, options: new RenderOptions(Dpi: Dpi));
        double scale = Dpi / 72.0, pageHeightPt = bitmap.Height / scale;
        using var canvas = new SKCanvas(bitmap);
        using var font = new SKFont(SKTypeface.Default, 9);
        foreach (var (no, l) in labels.Where(x => x.L.GetProperty("page").GetInt32() == page))
        {
            var alias = l.GetProperty("sourceAlias").GetString()!;
            var lay = layout[alias].GetProperty("layout");
            if (lay.ValueKind != JsonValueKind.Object || lay.GetProperty("bbox").ValueKind != JsonValueKind.Array) continue;
            var b = lay.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray(); // left,right,bottom,top
            var rect = new SKRect((float)(b[0] * scale), (float)((pageHeightPt - b[3]) * scale), (float)(b[1] * scale), (float)((pageHeightPt - b[2]) * scale));
            var label = Label(l); var flag = l.GetProperty("reviewFlag").GetString();
            var colour = label == "ESTABLISHES" ? new SKColor(26, 152, 80) : label == "REPRESENTS" ? new SKColor(33, 102, 172) : label == "EXCLUDED" ? new SKColor(230, 120, 0) : new SKColor(150, 150, 150);
            using var fill = new SKPaint { Color = colour.WithAlpha(label == "OTHER" ? (byte)28 : (byte)60), Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { Color = flag == "USER_DECIDED" ? new SKColor(118, 42, 131) : flag != "NONE" ? new SKColor(230, 120, 0) : colour,
                Style = SKPaintStyle.Stroke, StrokeWidth = flag != "NONE" ? 2.2f : 1.0f, IsAntialias = true };
            canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke);
            var tag = no.ToString();
            var w = font.MeasureText(tag);
            using var tagBg = new SKPaint { Color = stroke.Color, Style = SKPaintStyle.Fill };
            using var tagFg = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var tx = Math.Max(0, rect.Left - w - 4);
            canvas.DrawRect(new SKRect(tx, rect.Top, tx + w + 3, rect.Top + 11), tagBg);
            canvas.DrawText(tag, tx + 1.5f, rect.Top + 9, SKTextAlign.Left, font, tagFg);
        }
        canvas.Flush();
        var rel = $"{id}/p{page}.jpg";
        using (var image = SKImage.FromBitmap(bitmap)) using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 82)) using (var file = File.Create(Path.Combine(output, rel))) data.SaveTo(file);
        hashes.Add(new { file = rel, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, rel)))) });
        var stratum = labels.First(x => x.L.GetProperty("page").GetInt32() == page).L.GetProperty("pageStratum").GetString();
        md.AppendLine();
        md.AppendLine($"## Page {page} ({stratum})");
        md.AppendLine();
        md.AppendLine($"![{id} page {page}]({rel})");
        md.AppendLine();
        md.AppendLine("| # | alias | text | font | draft | flag | rationale |");
        md.AppendLine("|---:|---|---|---|---|---|---|");
        var onPage = labels.Where(x => x.L.GetProperty("page").GetInt32() == page).ToArray();
        foreach (var (no, l) in onPage)
        {
            var flag = l.GetProperty("reviewFlag").GetString();
            var decided = flag == "USER_DECIDED" && l.TryGetProperty("decisions", out var hist) && hist.GetArrayLength() > 0 ? $"USER_DECIDED ({hist[hist.GetArrayLength() - 1].GetProperty("source").GetString()})" : flag;
            md.AppendLine($"| {no} | {l.GetProperty("sourceAlias").GetString()} | {Cell(l.GetProperty("text").GetString(), 120)} | {FontText(layout[l.GetProperty("sourceAlias").GetString()!])} | **{Label(l)}** | {(flag == "NONE" ? "" : decided)} | {Cell(l.GetProperty("rationale").GetString(), 140)} |");
        }
        // Typography outliers among OTHER rows: bold, or larger than the page's most common size, and short (<= 12 words).
        var sizes = onPage.Select(x => Font(layout[x.L.GetProperty("sourceAlias").GetString()!]).Size).Where(s => s is not null).Select(s => Math.Round(s!.Value * 2) / 2).ToArray();
        var body = sizes.Length == 0 ? (double?)null : sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var check = onPage.Where(x => Label(x.L) == "OTHER").Where(x =>
        {
            var f = Font(layout[x.L.GetProperty("sourceAlias").GetString()!]); var words = (x.L.GetProperty("text").GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return words is > 0 and <= 12 && (f.Bold || (f.Size is { } s && body is { } b && s >= b + 1.0));
        }).ToArray();
        otherToCheck += check.Length;
        md.AppendLine();
        md.AppendLine($"**OTHER rows to check for a missed heading on page {page}** (body size {(body is { } bs ? bs.ToString("0.#") : "?")}pt; bold or ≥ body+1pt, ≤ 12 words): " +
            (check.Length == 0 ? "none." : string.Join(", ", check.Select(x => $"#{x.No} `{x.L.GetProperty("sourceAlias").GetString()}` {Cell(x.L.GetProperty("text").GetString(), 60)} ({FontText(layout[x.L.GetProperty("sourceAlias").GetString()!])})")) + "."));
    }
    File.WriteAllText(Path.Combine(output, id + ".md"), md.ToString());
    var counts = labels.GroupBy(x => Label(x.L)).ToDictionary(g => g.Key, g => g.Count());
    var pagesText = string.Join(", ", labels.Select(x => x.L.GetProperty("page").GetInt32()).Distinct());
    index.AppendLine($"| [{id}]({id}.md) | `{Path.GetFileName(key)}` | {pagesText} | {labels.Length} | {counts.GetValueOrDefault("ESTABLISHES")} | {counts.GetValueOrDefault("REPRESENTS")} | {counts.GetValueOrDefault("OTHER")} | {counts.GetValueOrDefault("EXCLUDED")} | {labels.Count(x => x.L.GetProperty("reviewFlag").GetString() is "REVIEW_FOCUS" or "DECISION_NEEDED")} | {labels.Count(x => x.L.GetProperty("reviewFlag").GetString() == "USER_DECIDED")} | {otherToCheck} |");
}
File.WriteAllText(Path.Combine(output, "README.md"), index.ToString());
File.WriteAllBytes(Path.Combine(output, "render-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
{
    version = "P7_F1Q_HELDOUT_VISUAL_REVIEW_V2", renderer = "PDFtoImage_5.4.0_PDFium", dpi = Dpi, overlay = "parser bbox per occurrence; colour = draft label",
    drafts = draftVersion, documents = draftPaths.Select(p => Path.GetFileName(p).Split('.')[0]),
    images = hashes, providerCalls = 0, predictionsShown = false, gold = "DRAFT_NOT_APPROVED",
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"VISUAL_REVIEW_WRITTEN images={hashes.Count}");

static string Label(JsonElement l) => l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? "EXCLUDED" : l.GetProperty("draftLabel").GetString()!.Replace("_STRUCTURE", "");
static (double? Size, bool Bold, bool Italic) Font(JsonElement occurrence)
{
    var lay = occurrence.GetProperty("layout");
    if (lay.ValueKind != JsonValueKind.Object || lay.GetProperty("font").ValueKind != JsonValueKind.Object) return (null, false, false);
    var f = lay.GetProperty("font");
    return (f.GetProperty("size").ValueKind == JsonValueKind.Number ? f.GetProperty("size").GetDouble() : null,
        f.GetProperty("boldRatio").ValueKind == JsonValueKind.Number && f.GetProperty("boldRatio").GetDouble() >= 0.5,
        f.GetProperty("italicRatio").ValueKind == JsonValueKind.Number && f.GetProperty("italicRatio").GetDouble() >= 0.5);
}
static string FontText(JsonElement occurrence)
{
    var f = Font(occurrence);
    return f.Size is null && !f.Bold && !f.Italic ? "-" : (f.Size is { } s ? s.ToString("0.#") : "?") + (f.Bold ? " B" : "") + (f.Italic ? " I" : "");
}

static string Cell(string? s, int max)
{
    var t = WebUtility.HtmlEncode((s ?? "").Replace("\n", " ")).Replace("|", "\\|");
    return t.Length > max ? t[..max] + "…" : t;
}
