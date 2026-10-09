using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PDFtoImage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

// Only the pinned source pack is read. No stage predictions, interpretations or existing Gold.
if (args.Length != 3) throw new ArgumentException("Usage: P7ShapeAudit <frozen-source-pack> <new-private-audit-dir> <new-public-receipt>");
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This pinned rendering audit runs on Windows.");
var sourcePack = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) || File.Exists(args[2])) throw new InvalidOperationException("new-output-required");
var input = File.ReadAllBytes(Path.Combine(sourcePack, "source-manifest.json"));
if (Hash(input) != "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30") throw new InvalidOperationException("source-manifest-drift");
using var manifest = JsonDocument.Parse(input);
Directory.CreateDirectory(output);
var rows = new List<object>(); var index = new StringBuilder();
var shapes = new[] { "MULTILINE_TITLE_OR_HEADING", "PARALLEL_COLUMNS", "RULED_TABLE", "BORDERLESS_TABLE", "DOCUMENT_HEADING_INSIDE_TABLE",
    "TITLE_SUBTITLE", "CROSS_PAGE_HEADING", "REPEATED_HEADER_FOOTER", "TOC_CROSS_REFERENCE", "ROTATED_OR_UNUSUAL_LAYOUT" };
var selection = new { version = "P7_SHAPE_SCREENING_POLICY_V1", sourceManifestSha256 = Hash(input),
    policy = "SCAN_ALL_PAGES_NO_PREDICTIONS_RENDER_FIRST_TWO_LAST_AND_MAXIMUM_PAGES_PER_NEUTRAL_CUE",
    dpi = 120, visualReview = "SEPARATE_MANUAL_RECORD_REQUIRED", cuesAreShapes = false,
    noShapeMatch = "NO_CUE_NOT_PROOF_OF_ABSENCE", allGoldStates = "PENDING" };
Write(Path.Combine(output, "screening-policy.json"), Bytes(selection));
foreach (var doc in manifest.RootElement.GetProperty("documents").EnumerateArray())
{
    var id = doc.GetProperty("document").GetString()!;
    if (!Regex.IsMatch(id, "^PDF-[a-f0-9]{64}$")) throw new InvalidOperationException("invalid-document-id");
    var frozen = Path.Combine(sourcePack, id); var snapshotBytes = File.ReadAllBytes(Path.Combine(frozen, "snapshot.json"));
    var pdfPath = Path.Combine(frozen, "source.pdf");
    if (Hash(snapshotBytes) != doc.GetProperty("snapshotSha256").GetString() || HashFile(pdfPath) != doc.GetProperty("sha256").GetString())
        throw new InvalidOperationException("source-snapshot-drift:" + id);
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var lines = snapshot.RootElement.GetProperty("nativeLines").EnumerateArray().ToDictionary(x => x.GetProperty("sourceId").GetString()!, x => x.GetProperty("lines")[0]);
    var atoms = snapshot.RootElement.GetProperty("atoms").EnumerateArray().Select(x =>
    {
        var sourceId = x.GetProperty("sourceId").GetString()!; var l = lines[sourceId];
        return new Atom(x.GetProperty("alias").GetString()!, sourceId, x.GetProperty("ordinal").GetInt32(),
            x.GetProperty("page").GetInt32(), x.GetProperty("text").GetString()!,
            Number(l, "left"), Number(l, "right"), Number(l, "bottom"), Number(l, "top"), Number(l, "fontSize"), Number(l, "boldRatio"));
    }).OrderBy(a => a.Ordinal).ToArray();
    using var pdf = PdfDocument.Open(pdfPath);
    var observations = new List<PageStats>(); var cases = new List<Cue>();
    var margins = new Dictionary<string, List<Atom>>(StringComparer.Ordinal);
    foreach (var p in pdf.GetPages())
    {
        var aa = atoms.Where(a => a.Page == p.Number).ToArray();
        var parallel = new List<string[]>(); var nearby = new List<string[]>();
        for (var i = 0; i < aa.Length; i++)
        {
            var a = aa[i]; if (a.Bottom is null || a.Top is null || a.Left is null || a.Right is null) continue;
            for (var j = i + 1; j < aa.Length; j++)
            {
                var b = aa[j]; if (b.Bottom is null || b.Top is null || b.Left is null || b.Right is null) continue;
                if (Math.Min(a.Top.Value, b.Top.Value) > Math.Max(a.Bottom.Value, b.Bottom.Value) &&
                    Math.Max(a.Left.Value, b.Left.Value) - Math.Min(a.Right.Value, b.Right.Value) > 12)
                    parallel.Add([a.Alias, b.Alias]);
            }
            if (i + 1 < aa.Length && aa[i + 1] is var next && next.Top is { } nt && next.Bottom is { } nb &&
                a.Bottom.Value - nt is > 0 and < 36 && a.Bold is >= .5 && next.Bold is >= .5 &&
                a.FontSize is { } af && next.FontSize is { } nf && Math.Abs(af - nf) < 3)
                nearby.Add([a.Alias, next.Alias]);
            if (a.Bottom < (double)p.Height * .1 || a.Top > (double)p.Height * .9)
            {
                var key = Regex.Replace(a.Text.Trim(), "[0-9]+", "#");
                if (!margins.TryGetValue(key, out var group)) margins[key] = group = [];
                group.Add(a);
            }
        }
        var rulings = new List<object>();
        foreach (var path in p.Paths.Where(x => x.IsStroked))
        {
            var rect = path.GetBoundingRectangle();
            if (rect is null) continue;
            var r = rect.Value; var w = (double)r.Width; var h = (double)r.Height;
            if (w > 50 && h < 2 || h > 20 && w < 2) rulings.Add(new { left = (double)r.Left, right = (double)r.Right, bottom = (double)r.Bottom, top = (double)r.Top });
        }
        var rotated = p.Letters.Where(l => l.TextOrientation != TextOrientation.Horizontal).ToArray();
        var toc = aa.Where(a => Regex.IsMatch(a.Text, @"\b(contents|annex|appendix|section|article)\b|mục\s+lục|xem\s+(điều|mục)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(a => a.Alias).ToArray();
        if (parallel.Count > 0) cases.Add(new("PARALLEL_X_REGIONS_WITH_OVERLAPPING_Y", p.Number, parallel.SelectMany(x => x).Distinct().ToArray(), parallel.Count));
        if (nearby.Count > 0) cases.Add(new("ADJACENT_BOLD_LINES_SIMILAR_FONT", p.Number, nearby.SelectMany(x => x).Distinct().ToArray(), nearby.Count));
        if (rulings.Count > 0) cases.Add(new("STROKED_LONG_THIN_PATHS", p.Number, aa.Select(a => a.Alias).ToArray(), rulings.Count));
        if (toc.Length > 0) cases.Add(new("LITERAL_TOC_OR_REFERENCE_WORDS", p.Number, toc, toc.Length));
        if (rotated.Length > 0) cases.Add(new("NON_HORIZONTAL_GLYPHS", p.Number, aa.Select(a => a.Alias).ToArray(), rotated.Length));
        observations.Add(new(p.Number, aa.Length, parallel.Count, nearby.Count, rulings.Count, rotated.Length, toc.Length));
        // Full page raw vector/orientation observations remain private. They do not label tables/headings.
        Write(Path.Combine(output, id + $".page-{p.Number:D4}.physical.json"), Bytes(new { document = id, page = p.Number,
            width = (double)p.Width, height = (double)p.Height, rulings,
            rotatedGlyphs = rotated.Select(l => new { l.Value, orientation = l.TextOrientation.ToString(),
                bbox = new { left = (double)l.BoundingBox.Left, right = (double)l.BoundingBox.Right, bottom = (double)l.BoundingBox.Bottom, top = (double)l.BoundingBox.Top } }) }));
    }
    foreach (var group in margins.Values.Where(g => g.Select(a => a.Page).Distinct().Count() >= 3))
        foreach (var page in group.GroupBy(a => a.Page)) cases.Add(new("REPEATED_MARGIN_TEXT_DIGITS_NORMALIZED", page.Key, page.Select(a => a.Alias).ToArray(), group.Select(a => a.Page).Distinct().Count()));
    var selectedPages = new[] { 1, Math.Min(2, pdf.NumberOfPages), pdf.NumberOfPages }.Concat(cases.GroupBy(c => c.Kind)
        .Select(g => g.OrderByDescending(c => c.Count).ThenBy(c => c.Page).First().Page)).Distinct().Order().ToArray();
    var images = new List<object>(); var html = new StringBuilder("<!doctype html><meta charset=utf-8><style>body{font:16px system-ui;margin:2em}img{max-width:100%;width:950px}table{border-collapse:collapse}td{border:1px solid #aaa;padding:.4em}</style><h1>Source-only shape screening</h1><p>Neutral cues are candidates, not verified shapes, membership or Gold. Source render and coordinates must be reviewed together.</p>");
    var folder = Path.Combine(output, id); Directory.CreateDirectory(folder);
    foreach (var page in selectedPages)
    {
        var path = Path.Combine(folder, $"page-{page:D4}.png");
        using (var stream = File.OpenRead(pdfPath)) Conversion.SavePng(path, stream, page: page - 1, options: new RenderOptions(Dpi: 120));
        images.Add(new { page, file = $"page-{page:D4}.png", imageSha256 = HashFile(path) });
        html.Append($"<h2>Page {page}</h2><img src='page-{page:D4}.png'><table><tr><th>Source identity</th><th>Text</th><th>PDF bbox</th></tr>");
        foreach (var a in atoms.Where(a => a.Page == page)) html.Append($"<tr><td>{WebUtility.HtmlEncode(a.Alias)} / {WebUtility.HtmlEncode(a.SourceId)}</td><td>{WebUtility.HtmlEncode(a.Text)}</td><td>{a.Left}, {a.Bottom}, {a.Right}, {a.Top}</td></tr>");
        html.Append("</table>");
    }
    Write(Path.Combine(folder, "review.html"), Encoding.UTF8.GetBytes(html.ToString()));
    Write(Path.Combine(folder, "cue-cases.json"), Bytes(new { sourceSha256 = doc.GetProperty("sha256").GetString(), snapshotSha256 = doc.GetProperty("snapshotSha256").GetString(),
        atoms, cases, images, pages = observations, semanticStatus = "NOT_ADJUDICATED", visualReviewStatus = "PENDING" }));
    index.Append($"<li><a href='{id}/review.html'>{WebUtility.HtmlEncode(Path.GetFileName(doc.GetProperty("sourceKey").GetString()))}</a> — {pdf.NumberOfPages} pages screened / {selectedPages.Length} rendered</li>");
    rows.Add(new { document = id, sourceSha256 = doc.GetProperty("sha256").GetString(), sourceUniverseSha256 = doc.GetProperty("sourceUniverseSha256").GetString(),
        snapshotSha256 = doc.GetProperty("snapshotSha256").GetString(), pagesScreened = pdf.NumberOfPages, occurrencesScreened = atoms.Length,
        renderedPages = selectedPages, images, neutralCueInventory = cases.GroupBy(c => c.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { cue = g.Key, pagesWithCue = g.Select(c => c.Page).Distinct().Count(), sourceCases = g.Count() }).ToArray(),
        shapes = shapes.Select(shape => new { shape, coverage = "NOT_COVERED_BY_CONFIRMED_REVIEW", corpusAbsenceProven = false, visualReview = "PENDING", confirmedCases = 0 }).ToArray() });
    Console.WriteLine(JsonSerializer.Serialize(new { document = id, pagesScreened = pdf.NumberOfPages, renderedPages = selectedPages }));
}
Write(Path.Combine(output, "index.html"), Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><h1>P7-D2.3 shape review</h1><p>All 647 pages screened by neutral cues; rendered samples require source-only review. This is not Gold or semantic accuracy evidence.</p><ul>" + index + "</ul>"));
var receipt = new { version = "P7_SHAPE_SCREENING_RECEIPT_V1", status = "ALL_PAGE_PHYSICAL_SCREENING_COMPLETE_VISUAL_SHAPE_REVIEW_PENDING",
    sourceManifestSha256 = Hash(input), screeningPolicySha256 = Hash(Bytes(selection)), renderer = "PDFtoImage_5.4.0_PDFium_120_DPI",
    documents = rows, shapeCountsAreDocumentStratified = true, missingCueDoesNotProveAbsence = true,
    goldMutation = "NONE", modelResponsesRead = false, providerCalls = 0, productionChanged = false, frozenRequestsChanged = false,
    realCohortFreeze = "NOT_FROZEN", providerExecution = "LOCKED", productionPromotion = "LOCKED" };
Write(Path.Combine(output, "screening-receipt.json"), Bytes(receipt)); Write(args[2], Bytes(receipt));
static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
static byte[] Bytes<T>(T x) => JsonSerializer.SerializeToUtf8Bytes(x, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
record Atom(string Alias, string SourceId, int Ordinal, int Page, string Text, double? Left, double? Right, double? Bottom, double? Top, double? FontSize, double? Bold);
record Cue(string Kind, int Page, string[] Aliases, int Count);
record PageStats(int Page, int Occurrences, int ParallelBandPairs, int NearbyBoldPairs, int LongThinStrokes, int NonHorizontalGlyphs, int TocReferenceWords);
