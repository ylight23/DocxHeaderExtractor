using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]

// P7-F1Q held-out Gold Visual Review Workspace (Issue #6). For each of the 24 documents it writes:
//   <id>.review.html  self-contained offline workspace (embedded page images, SVG bbox overlays, zoom, every
//                     occurrence incl. OTHER, per-occurrence edits with old/new/reason, per-document APPROVE,
//                     JSON + command export; state kept in the browser only);
//   <id>.review.pdf   annotated PDF (rendered pages with numbered, coloured bboxes + full occurrence table).
// Inputs are the reviewer-A DRAFT labels and parser source facts only. No provider predictions, no Gold approval.
if (args.Length != 3) throw new ArgumentException("<gold-draft-dir> <review-bundle-dir> <new-output-dir>");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new InvalidOperationException("OUTPUT_EXISTS");
Directory.CreateDirectory(output);
const int Dpi = 150;
var order = new[] { "044", "049", "087", "032" };
var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var typeface = SKTypeface.FromFamilyName("Arial");
var index = new List<(string Id, string Key, int N, Dictionary<string, int> Counts, int Flagged, int Pages)>();
var manifest = new List<object>();

foreach (var draftPath in Directory.GetFiles(args[0], "*.gold-draft.json"))
{
    var draftBytes = File.ReadAllBytes(draftPath);
    using var draft = JsonDocument.Parse(draftBytes);
    var d = draft.RootElement; var id = d.GetProperty("id").GetString()!; var key = d.GetProperty("sourceKey").GetString()!;
    var sourceSha = d.GetProperty("sourceSha256").GetString()!;
    if (Hex(SHA256.HashData(File.ReadAllBytes(key))) != sourceSha) throw new InvalidOperationException("SOURCE_SHA_DRIFT:" + id);
    using var review = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], id + ".review.json")));
    var layout = review.RootElement.GetProperty("occurrences").EnumerateArray().ToDictionary(o => o.GetProperty("sourceAlias").GetString()!, o => o.Clone());
    var rows = d.GetProperty("labels").EnumerateArray().Select((l, i) =>
    {
        var alias = l.GetProperty("sourceAlias").GetString()!; var lay = layout[alias].GetProperty("layout");
        double[]? bbox = lay.ValueKind == JsonValueKind.Object && lay.GetProperty("bbox").ValueKind == JsonValueKind.Array
            ? lay.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
        string font = "-";
        if (lay.ValueKind == JsonValueKind.Object && lay.GetProperty("font").ValueKind == JsonValueKind.Object)
        {
            var f = lay.GetProperty("font");
            font = (f.GetProperty("size").ValueKind == JsonValueKind.Number ? f.GetProperty("size").GetDouble().ToString("0.#") : "?")
                + (f.GetProperty("boldRatio").ValueKind == JsonValueKind.Number && f.GetProperty("boldRatio").GetDouble() >= 0.5 ? " B" : "")
                + (f.GetProperty("italicRatio").ValueKind == JsonValueKind.Number && f.GetProperty("italicRatio").GetDouble() >= 0.5 ? " I" : "");
        }
        return new Row(i + 1, alias, l.GetProperty("page").GetInt32(), l.GetProperty("pageStratum").GetString()!, l.GetProperty("text").GetString()!,
            bbox, font, l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? "X" : Short(l.GetProperty("draftLabel").GetString()!), l.GetProperty("reviewFlag").GetString()!, l.GetProperty("rationale").GetString()!,
            l.GetProperty("approval").GetString()!);
    }).ToArray();

    // Render pages once (clean, no overlay); overlays are vector (SVG in HTML, drawn in PDF).
    var pages = new List<(int Page, string Stratum, SKBitmap Bitmap, string Jpeg64, double HeightPt, double WidthPt)>();
    foreach (var page in rows.Select(r => r.Page).Distinct())
    {
        using var stream = File.OpenRead(key);
        var bitmap = Conversion.ToImage(stream, page: page - 1, options: new RenderOptions(Dpi: Dpi));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        pages.Add((page, rows.First(r => r.Page == page).Stratum, bitmap, Convert.ToBase64String(data.ToArray()), bitmap.Height * 72.0 / Dpi, bitmap.Width * 72.0 / Dpi));
    }

    // ---------- HTML workspace ----------
    var payload = new
    {
        version = "P7_F1Q_HELDOUT_GOLD_WORKSPACE_V1", id, sourceKey = key, sourceSha256 = sourceSha,
        sourceAliasUniverseSha256 = d.GetProperty("sourceAliasUniverseSha256").GetString(), draftFile = Path.GetFileName(draftPath),
        draftSha256 = Hex(SHA256.HashData(draftBytes)), draftStatus = d.GetProperty("status").GetString(), dpi = Dpi,
        pages = pages.Select(p => new { page = p.Page, stratum = p.Stratum, widthPt = p.WidthPt, heightPt = p.HeightPt, image = "data:image/jpeg;base64," + p.Jpeg64 }),
        rows = rows.Select(r => new { no = r.No, alias = r.Alias, page = r.Page, stratum = r.Stratum, text = r.Text, bbox = r.Bbox, font = r.Font,
            draft = r.Draft == "X" ? null : r.Draft, flag = r.Flag, rationale = r.Rationale, approval = r.Approval }),
    };
    var dataJson = JsonSerializer.Serialize(payload, json).Replace("</", "<\\/");
    File.WriteAllText(Path.Combine(output, id + ".review.html"), Template.Html.Replace("__TITLE__", WebUtility.HtmlEncode($"Gold review {id}")).Replace("__DATA__", dataJson));

    // ---------- Annotated PDF ----------
    var pdfPath = Path.Combine(output, id + ".review.pdf");
    using (var pdfStream = File.Create(pdfPath))
    using (var doc = SKDocument.CreatePdf(pdfStream, new SKDocumentPdfMetadata { Title = $"P7-F1Q held-out Gold review {id} (DRAFT)", Creator = "dhx-f1q-gold-workspace", RasterDpi = Dpi }))
    {
        const float W = 595, H = 842, M = 28;
        using var titleFont = new SKFont(typeface, 13); using var bodyFont = new SKFont(typeface, 8); using var smallFont = new SKFont(typeface, 6.5f);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true }; using var grey = new SKPaint { Color = new SKColor(90, 90, 90), IsAntialias = true };
        // Cover
        var c = doc.BeginPage(W, H); float y = M + 14;
        c.DrawText($"P7-F1Q held-out — Gold review {id} (DRAFT, NOT APPROVED)", M, y, SKTextAlign.Left, titleFont, ink); y += 20;
        foreach (var line in new[] { $"Source: {key}", $"Source SHA-256: {sourceSha}", $"Draft: {Path.GetFileName(draftPath)}  SHA-256 {Hex(SHA256.HashData(draftBytes))}",
            $"Occurrences: {rows.Length}  ESTABLISHES {rows.Count(r => r.Draft == "E")}  REPRESENTS {rows.Count(r => r.Draft == "R")}  OTHER {rows.Count(r => r.Draft == "O")}  EXCLUDED {rows.Count(r => r.Draft == "X")}  flagged {rows.Count(r => r.Flag is "REVIEW_FOCUS" or "DECISION_NEEDED")}",
            $"Pages: {string.Join(", ", pages.Select(p => $"{p.Page} ({p.Stratum})"))}", "",
            "Boxes: parser bbox of every occurrence, numbered as in the table. Fill/outline: green = drafted E, blue = R, grey = O;",
            "orange outline = REVIEW_FOCUS/DECISION_NEEDED; purple = already decided by the user (2026-10-10).",
            "Edit or approve in the companion .review.html file, or send commands:  APPROVE <id>   or   <id> <alias> -> E|R|O  (with a reason).",
            "This file contains parser facts and draft labels only; no model predictions. Not Gold until approved." })
        { c.DrawText(line, M, y, SKTextAlign.Left, bodyFont, line.StartsWith("Boxes") || line.StartsWith("orange") ? grey : ink); y += 12; }
        doc.EndPage();
        foreach (var p in pages)
        {
            var canvas = doc.BeginPage(W, H);
            canvas.DrawText($"{id} — page {p.Page} ({p.Stratum})", M, M, SKTextAlign.Left, bodyFont, ink);
            float scale = (float)Math.Min((W - 2 * M) / p.WidthPt, (H - 2 * M - 10) / p.HeightPt);
            float ox = M, oy = M + 8;
            using (var img = SKImage.FromBitmap(p.Bitmap))
                canvas.DrawImage(img, new SKRect(ox, oy, ox + (float)(p.WidthPt * scale), oy + (float)(p.HeightPt * scale)), new SKSamplingOptions(SKFilterMode.Linear));
            foreach (var r in rows.Where(r => r.Page == p.Page && r.Bbox is not null))
            {
                var b = r.Bbox!;
                var rect = new SKRect(ox + (float)(b[0] * scale), oy + (float)((p.HeightPt - b[3]) * scale), ox + (float)(b[1] * scale), oy + (float)((p.HeightPt - b[2]) * scale));
                var colour = Colour(r.Draft);
                using var fill = new SKPaint { Color = colour.WithAlpha(r.Draft == "O" ? (byte)25 : (byte)55) };
                using var stroke = new SKPaint { Color = Outline(r), Style = SKPaintStyle.Stroke, StrokeWidth = r.Flag == "NONE" ? 0.5f : 1.1f, IsAntialias = true };
                canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke);
                var tag = r.No.ToString(); var w = smallFont.MeasureText(tag);
                using var tagBg = new SKPaint { Color = Outline(r) }; using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var tx = Math.Max(0, rect.Left - w - 2.5f);
                canvas.DrawRect(new SKRect(tx, rect.Top, tx + w + 2, rect.Top + 7), tagBg);
                canvas.DrawText(tag, tx + 1, rect.Top + 5.8f, SKTextAlign.Left, smallFont, white);
            }
            doc.EndPage();
        }
        // Full occurrence table (all rows, including OTHER)
        SKCanvas? t = null; float ty = 0;
        void NewTablePage()
        {
            if (t is not null) doc.EndPage();
            t = doc.BeginPage(W, H); ty = M;
            t.DrawText($"{id} — all {rows.Length} occurrences (draft labels)", M, ty, SKTextAlign.Left, bodyFont, ink); ty += 12;
            t.DrawText("#   page  alias        draft  flag               text  /  rationale", M, ty, SKTextAlign.Left, smallFont, grey); ty += 9;
        }
        NewTablePage();
        foreach (var r in rows)
        {
            var textLines = Wrap(r.Text, smallFont, W - 2 * M - 210); var whyLines = Wrap(r.Rationale, smallFont, W - 2 * M - 210);
            var need = 8f * (textLines.Count + whyLines.Count) + 3;
            if (ty + need > H - M) NewTablePage();
            using var lbl = new SKPaint { Color = Colour(r.Draft) == new SKColor(150, 150, 150) ? new SKColor(90, 90, 90) : Colour(r.Draft), IsAntialias = true };
            t!.DrawText(r.No.ToString(), M, ty + 6, SKTextAlign.Left, smallFont, ink);
            t.DrawText(r.Page.ToString(), M + 22, ty + 6, SKTextAlign.Left, smallFont, ink);
            t.DrawText(r.Alias, M + 44, ty + 6, SKTextAlign.Left, smallFont, ink);
            t.DrawText(r.Draft, M + 100, ty + 6, SKTextAlign.Left, smallFont, lbl);
            t.DrawText(r.Flag == "NONE" ? "" : r.Flag, M + 118, ty + 6, SKTextAlign.Left, smallFont, grey);
            var ly = ty;
            foreach (var l in textLines) { t.DrawText(l, M + 210, ly + 6, SKTextAlign.Left, smallFont, ink); ly += 8; }
            foreach (var l in whyLines) { t.DrawText(l, M + 210, ly + 6, SKTextAlign.Left, smallFont, grey); ly += 8; }
            ty = ly + 3;
        }
        doc.EndPage();
        doc.Close();
    }
    foreach (var p in pages) p.Bitmap.Dispose();
    manifest.Add(new { id, sourceSha256 = sourceSha, draftSha256 = Hex(SHA256.HashData(draftBytes)),
        html = id + ".review.html", htmlSha256 = Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, id + ".review.html")))),
        pdf = id + ".review.pdf", pdfSha256 = Hex(SHA256.HashData(File.ReadAllBytes(pdfPath))), pages = pages.Count, occurrences = rows.Length });
    index.Add((id, key, rows.Length, rows.GroupBy(r => r.Draft).ToDictionary(g => g.Key, g => g.Count()), rows.Count(r => r.Flag != "NONE"), pages.Count));
    Console.WriteLine($"{id}: {pages.Count} pages, {rows.Length} occurrences");
}

var ordered = index.OrderBy(i => Array.IndexOf(order, i.Id) is var k && k >= 0 ? k : 100).ThenBy(i => i.Id).ToArray();
var md = new StringBuilder();
md.AppendLine("# P7-F1Q held-out — Gold Visual Review Workspace (DRAFT, not approved)");
md.AppendLine();
md.AppendLine("Per document: `<id>.review.html` (self-contained, offline: zoom, every occurrence incl. OTHER, edit with reason, APPROVE, export JSON/commands) and `<id>.review.pdf` (annotated pages + full table). Review order: 044, 049, 087, 032, then the rest. Send back the exported `<id>.gold-review-decisions.json` or the commands (`APPROVE <id>`, `<id> <alias> -> E|R|O because …`). No Gold is approved and the provider stays locked until all 24 documents are approved.");
md.AppendLine();
md.AppendLine("**Privacy.** The repository is public. The `.review.html` / `.review.pdf` files embed 150 DPI page renders, so they are **not committed** (`.gitignore`). They live in the local worktree (this folder) and in the private store `~/.codex/diagnostic-captures/p7-f1q-heldout-private-20261010/gold-workspace.v1/`. `workspace-manifest.json` pins the SHA-256 of every file and of the draft it was built from. Regenerate byte-for-byte with:");
md.AppendLine();
md.AppendLine($"    dotnet run --project scripts/P7F1QGoldWorkspace -c Release -- {Rel(args[0])} {Rel(args[1])} {Rel(output)}");
md.AppendLine();
md.AppendLine("| order | doc | source | pages | occurrences | E | R | O | excluded | flagged | HTML | PDF |");
md.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|");
var n = 0;
foreach (var i in ordered)
    md.AppendLine($"| {++n} | {i.Id} | `{Path.GetFileName(i.Key)}` | {i.Pages} | {i.N} | {i.Counts.GetValueOrDefault("E")} | {i.Counts.GetValueOrDefault("R")} | {i.Counts.GetValueOrDefault("O")} | {i.Counts.GetValueOrDefault("X")} | {i.Flagged} | [{i.Id}.review.html]({i.Id}.review.html) | [{i.Id}.review.pdf]({i.Id}.review.pdf) |");
md.AppendLine();
md.AppendLine($"Totals: {index.Sum(i => i.N)} occurrences on {index.Sum(i => i.Pages)} pages — E {index.Sum(i => i.Counts.GetValueOrDefault("E"))} · R {index.Sum(i => i.Counts.GetValueOrDefault("R"))} · O {index.Sum(i => i.Counts.GetValueOrDefault("O"))} · excluded {index.Sum(i => i.Counts.GetValueOrDefault("X"))}.");
File.WriteAllText(Path.Combine(output, "README.md"), md.ToString());
File.WriteAllBytes(Path.Combine(output, "workspace-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
{
    version = "P7_F1Q_HELDOUT_GOLD_WORKSPACE_MANIFEST_V1", renderer = "PDFtoImage_5.4.0_PDFium", dpi = Dpi, reviewOrder = ordered.Select(i => i.Id),
    documents = manifest, totalOccurrences = index.Sum(i => i.N), totalPages = index.Sum(i => i.Pages), providerCalls = 0, goldApproved = false,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"WORKSPACE_WRITTEN documents={index.Count} pages={index.Sum(i => i.Pages)} occurrences={index.Sum(i => i.N)}");

static string Short(string label) => label switch { "ESTABLISHES_STRUCTURE" => "E", "REPRESENTS_STRUCTURE" => "R", _ => "O" };
static SKColor Colour(string draft) => draft == "E" ? new SKColor(26, 152, 80) : draft == "R" ? new SKColor(33, 102, 172) : draft == "X" ? new SKColor(230, 120, 0) : new SKColor(150, 150, 150);
static SKColor Outline(Row r) => r.Flag == "USER_DECIDED" ? new SKColor(118, 42, 131) : r.Flag != "NONE" ? new SKColor(230, 120, 0) : Colour(r.Draft);
static string Hex(byte[] b) => Convert.ToHexStringLower(b);
static string Rel(string path) => Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).Replace('\\', '/');
static List<string> Wrap(string text, SKFont font, float width)
{
    var lines = new List<string>(); var current = "";
    foreach (var word in text.Replace("\n", " ").Split(' '))
    {
        var candidate = current.Length == 0 ? word : current + " " + word;
        if (font.MeasureText(candidate) <= width) { current = candidate; continue; }
        if (current.Length > 0) lines.Add(current);
        current = word;
        while (font.MeasureText(current) > width && current.Length > 1)
        { var cut = current.Length - 1; while (cut > 1 && font.MeasureText(current[..cut]) > width) cut--; lines.Add(current[..cut]); current = current[cut..]; }
    }
    if (current.Length > 0) lines.Add(current);
    return lines.Count == 0 ? [""] : lines;
}

internal sealed record Row(int No, string Alias, int Page, string Stratum, string Text, double[]? Bbox, string Font, string Draft, string Flag, string Rationale, string Approval);
