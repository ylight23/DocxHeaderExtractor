using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Source-only inputs. No provider, response, interpretation, credentials or Gold reader.
if (args.Length != 4) throw new ArgumentException("Usage: P7SourceReviewFreeze <repo> <reference-pdf> <new-private-pack-dir> <new-public-receipt>");
var repo = Path.GetFullPath(args[0]); var reference = Path.GetFullPath(args[1]);
var pack = Path.GetFullPath(args[2]); var receipt = Path.GetFullPath(args[3]);
if (Directory.Exists(pack) || File.Exists(receipt)) throw new InvalidOperationException("new-output-required");
var poolPath = Path.Combine(repo, "artifacts/web-pdf-semantic-diagnostic/p7.d2.source-pool.v1.json");
var poolBytes = File.ReadAllBytes(poolPath);
var pool = JsonSerializer.Deserialize<Pool>(poolBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
if (pool.Sources.Length != 84 || pool.Sources.GroupBy(s => s.Category).Count() != 8)
    throw new InvalidOperationException("source-pool-drift");
// Preregistered neutral selection: one minimum content SHA per category. No size/page/Gold filtering.
var selected = pool.Sources.GroupBy(s => s.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
    .Select(g => g.OrderBy(s => s.Sha256, StringComparer.Ordinal).ThenBy(s => s.SourceKey, StringComparer.Ordinal).First()).ToArray();
Directory.CreateDirectory(pack);
var selection = new { version = "P7_SOURCE_CANDIDATE_SELECTION_V1", sourcePoolArtifactSha256 = Hash(poolBytes),
    rule = "ONE_MINIMUM_CONTENT_SHA256_PER_CATEGORY_THEN_SOURCE_KEY_INCLUDING_REFERENCE_NO_REPLACEMENT_ON_FAILURE",
    status = "SOURCE_REVIEW_CANDIDATES_NOT_APPROVED_QUALIFICATION_COHORT", selected };
// Written before parsing or review. CreateNew prevents in-place rebaseline.
Write(Path.Combine(pack, "selection.json"), Bytes(selection));
var policy = new { version = "P7_TITLE_SUBTITLE_POLICY_V1", status = "USER_SCOPE_APPROVED_UNIT_ADJUDICATION_OPEN",
    approvalBasis = "USER_REPLY_2026_10_09_INCLUDE_TITLE_SUBTITLE_ADJUDICATE_UNITS_INDEPENDENTLY",
    titleSubtitlePolicy = "INCLUDE_DOCUMENT_TITLES_AND_SUBTITLES",
    grouping = "SOURCE_REVIEWED_EXACT_UNITS_NOT_MODEL_DERIVED",
    multipart = "ALLOWED_FOR_LINES_OF_SAME_UNIT_NOT_SHARED_TOPIC",
    tableScope = "REVIEW_SEMANTIC_DOCUMENT_ROLE_NOT_BLANKET_INCLUDE_OR_EXCLUDE_BY_GEOMETRY",
    unresolved = "NOT_EVALUABLE", occurrenceGoldApproved = false };
var policyBytes = Bytes(policy); var policyHash = Hash(policyBytes);
Write(Path.Combine(pack, "title-subtitle-policy.json"), policyBytes);
var parserFiles = Directory.EnumerateFiles(Path.Combine(repo, "src/DocxHeaderExtractor.DocumentProcessing/Source"), "*.cs", SearchOption.AllDirectories)
    .Concat(Directory.EnumerateFiles(Path.Combine(repo, "src/DocxHeaderExtractor.Core"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
    .OrderBy(p => p, StringComparer.Ordinal).Select(p => new { file = Path.GetRelativePath(repo, p).Replace('\\', '/'), sha256 = HashFile(p) }).ToArray();
var parserIdentity = new { version = "P7_PARSER_IDENTITY_V1", files = parserFiles,
    pdfFacts = PdfSourceFactsVersions.Id(PdfSourceFactsVersions.Current), projection = PdfSourceTextProjection.CurrentVersion,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    dependencyAssemblies = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
        .Select(p => new { file = Path.GetFileName(p), sha256 = HashFile(p) }).ToArray() };
var parserBytes = Bytes(parserIdentity); Write(Path.Combine(pack, "parser-identity.json"), parserBytes);
var rows = new List<object>(); var links = new StringBuilder();
foreach (var item in selected)
{
    var sourcePath = item.SourceKey == "USER_REFERENCE_PDF" ? reference : Path.GetFullPath(Path.Combine(repo, item.SourceKey));
    if (item.SourceKey != "USER_REFERENCE_PDF" && !sourcePath.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("source-path-outside-repo");
    if (HashFile(sourcePath) != item.Sha256 || new FileInfo(sourcePath).Length != item.Bytes)
        throw new InvalidOperationException("source-bytes-drift:" + item.SourceKey);
    var id = "PDF-" + item.Sha256; var folder = Path.Combine(pack, id); Directory.CreateDirectory(folder);
    // A private copy pins the exact bytes actually parsed; source cannot change between review and parse.
    var frozenPdf = Path.Combine(folder, "source.pdf"); File.Copy(sourcePath, frozenPdf, overwrite: false);
    if (HashFile(frozenPdf) != item.Sha256) throw new InvalidOperationException("copy-hash-mismatch");
    var built = PdfSourceAdapter.BuildWithDetails(frozenPdf); var s = built.Snapshot;
    var store = PdfSourceEvidenceStore.Build(s, built.Details);
    var storeBytes = store.CanonicalBytes(); Write(Path.Combine(folder, "evidence-store.json"), storeBytes);
    using var pdf = UglyToad.PdfPig.PdfDocument.Open(frozenPdf);
    var pages = pdf.GetPages().Select(p => new { page = p.Number, width = (double)p.Width, height = (double)p.Height }).ToArray();
    var lines = built.Details.Blocks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new { sourceId = b.Id, lines = b.Lines }).ToArray();
    var snapshot = new { version = "P7_FULL_SOURCE_REVIEW_SNAPSHOT_V1", sourceSha256 = s.SourceSha256,
        universeSha256 = s.SourceAliasUniverseHash, modelEvidenceSha256 = s.ModelVisibleEvidenceHash,
        parserIdentitySha256 = Hash(parserBytes), pages, atoms = s.Atoms, aliases = s.Aliases,
        occurrences = s.Occurrences, evidence = s.Evidence,
        contexts = s.OccurrenceContexts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
        catalog = s.Catalog, nativeLines = lines,
        layoutBlocks = built.Details.LayoutBlockByAtom.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() };
    var snapshotBytes = Bytes(snapshot); var snapshotHash = Hash(snapshotBytes);
    Write(Path.Combine(folder, "snapshot.json"), snapshotBytes);
    var reviewOccurrences = s.Atoms.Select(a => new ReviewOccurrence(a.Alias, a.Page, a.Text.Length)).ToArray();
    Write(Path.Combine(folder, "review.draft.json"), Bytes(P7SourceOnlyReview.Draft(s.SourceSha256, s.SourceAliasUniverseHash,
        snapshotHash, policyHash, reviewOccurrences)));
    var html = new StringBuilder("<!doctype html><meta charset=utf-8><title>Source-only PDF review</title><style>body{font:16px system-ui;margin:2em}td{border:1px solid #aaa;padding:.3em}table{border-collapse:collapse;width:100%}svg{max-width:800px;width:100%;border:1px solid #888}.text{white-space:pre-wrap}</style>");
    html.Append("<h1>Source-only review</h1><p>NOT GOLD. Every label is unadjudicated. Review all pages of the original PDF, including regions not represented by extracted atoms. No predictions or interpretation records are included.</p><a href='source.pdf'>Open hash-pinned original PDF</a>");
    foreach (var page in pages)
    {
        var atoms = s.Atoms.Where(a => a.Page == page.page).OrderBy(a => a.Ordinal).ToArray();
        html.Append($"<h2>Page {page.page}</h2><p>All {atoms.Length} extracted occurrences. Bbox diagram is not a PDF render.</p><svg viewBox='0 0 {N(page.width)} {N(page.height)}'>");
        foreach (var a in atoms)
        {
            var line = built.Details.Blocks.Single(b => b.Id == a.SourceId).Lines.Single();
            if (line.Top is { } top && line.Bottom is { } bottom)
                html.Append($"<rect x='{N(line.Left)}' y='{N(page.height - top)}' width='{N(line.Right - line.Left)}' height='{N(top - bottom)}' fill='none' stroke='#467'/><text x='{N(line.Left)}' y='{N(page.height - top)}' font-size='6'>{WebUtility.HtmlEncode(a.Alias)}</text>");
        }
        html.Append("</svg><table><tr><th>Alias</th><th>Source ID / ordinal</th><th>Whole source text</th><th>Review</th></tr>");
        foreach (var a in atoms) html.Append($"<tr><td>{WebUtility.HtmlEncode(a.Alias)}</td><td>{WebUtility.HtmlEncode(a.SourceId)} / {a.Ordinal}</td><td class='text'>{WebUtility.HtmlEncode(a.Text)}</td><td>UNADJUDICATED</td></tr>");
        html.Append("</table>");
    }
    var htmlBytes = Encoding.UTF8.GetBytes(html.ToString()); Write(Path.Combine(folder, "review.html"), htmlBytes);
    links.Append($"<li><a href='{id}/review.html'>{WebUtility.HtmlEncode(item.FileName)}</a> — {pages.Length} pages / {s.Atoms.Count} occurrences; source-only review pending</li>");
    rows.Add(new { document = id, item.SourceKey, item.Category, item.Sha256, item.Bytes,
        sourceUniverseSha256 = s.SourceAliasUniverseHash, modelEvidenceSha256 = s.ModelVisibleEvidenceHash,
        snapshotSha256 = snapshotHash, storeSha256 = store.StoreSha256, reviewHtmlSha256 = Hash(htmlBytes),
        pages = pages.Length, occurrences = s.Atoms.Count, glyphMapEntries = built.Details.Blocks.Sum(b => b.Lines.Sum(l => l.Projection.SpanMap.Count)),
        priorExposure = item.SourceKey == "USER_REFERENCE_PDF" ? "KNOWN_DEVELOPMENT" : "NOT_SCREENED",
        reviewStatus = "NOT_REVIEWED", shapeCoverage = "NOT_ADJUDICATED", goldStatus = "NOT_FROZEN" });
    Console.WriteLine(JsonSerializer.Serialize(new { document = id, pages = pages.Length, occurrences = s.Atoms.Count, snapshotHash }));
}
Write(Path.Combine(pack, "index.html"), Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><h1>P7 complete-source review candidates</h1><p>Original PDFs and all extracted occurrences; no model predictions. This is not an approved cohort or Gold.</p><ul>" + links + "</ul>"));
var report = new { version = "P7_D23_SOURCE_FREEZE_RECEIPT_V1", status = "SOURCE_CANDIDATE_UNIVERSES_FROZEN_REVIEW_PENDING",
    selectionSha256 = Hash(Bytes(selection)), sourcePoolArtifactSha256 = Hash(poolBytes), parserIdentitySha256 = Hash(parserBytes),
    policySha256 = policyHash, policy, documents = rows, claimsHeldOut = false,
    renderer = "NOT_AVAILABLE_ORIGINAL_PDFS_INCLUDED_BBOX_DIAGRAMS_NOT_RENDERS",
    goldManifest = "NOT_FROZEN_NO_OCCURRENCE_APPROVAL", scorerManifest = "NOT_FROZEN",
    requestManifest = "NOT_FROZEN_FOR_CANDIDATE_COHORT", shapeReview = "OPEN_CATEGORY_COVERAGE_IS_NOT_SHAPE_COVERAGE",
    providerCalls = 0, modelResponsesRead = false, goldRead = false, goldMutation = "NONE",
    runtimeChanged = false, productionRequestsChanged = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" };
var reportBytes = Bytes(report); Write(Path.Combine(pack, "source-manifest.json"), reportBytes); Write(receipt, reportBytes);
Console.WriteLine(JsonSerializer.Serialize(new { status = report.status, documents = rows.Count, receiptSha256 = Hash(reportBytes), providerCalls = 0 }));
static byte[] Bytes<T>(T value) => SpatialCanonical.Bytes(value);
static string Hash(byte[] bytes) => SpatialCanonical.Hash(bytes);
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
static string N(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
record Pool(Source[] Sources);
record Source(string SourceKey, string FileName, long Bytes, string Sha256, string Category);
