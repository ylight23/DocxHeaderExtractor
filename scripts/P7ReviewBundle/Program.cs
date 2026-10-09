using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 6) throw new ArgumentException("Usage: P7ReviewBundle <pilot-dir> <source-pack> <shape-pack> <new-bundle-dir> <new-zip> <new-public-receipt>");
if (Directory.Exists(args[3]) || File.Exists(args[4]) || File.Exists(args[5])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var pilotSource = ReadPinned(Path.Combine(args[0], "source-manifest.json"), "23c1a49e7a1015a3c5d19c3651fae08fd4207853d122d652da5834e65a0f55d2");
var originalSource = ReadPinned(Path.Combine(args[1], "source-manifest.json"), "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30");
var originalShapes = ReadPinned(Path.Combine(args[2], "screening-receipt.json"), "d3dda9a69cf07dc8d214ed86767f5a643a059a42e9834b824af4d79867dc3207");
using var pilot = JsonDocument.Parse(pilotSource); using var sources = JsonDocument.Parse(originalSource); using var shapes = JsonDocument.Parse(originalShapes);
Directory.CreateDirectory(args[3]);
var outputRoot = Path.GetFullPath(args[3]);
var files = new List<FileEntry>(); var docs = new List<object>(); var scopeForms = new List<object>(); var worksheet = new List<object>();
var html = new StringBuilder("<!doctype html><html lang='vi'><meta charset='utf-8'><title>P7 source-only pilot review</title><style>body{font-family:system-ui;max-width:1200px;margin:auto;padding:24px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:6px;vertical-align:top}img{max-width:100%}.pending{background:#fff4d4}</style><h1>P7 — source-only adjudication pilot</h1><p class='pending'>Scope chưa duyệt. 213 occurrences đều PENDING; không có prediction, interpretation hay Gold đã phê duyệt. Mở PDF đầy đủ để kiểm tra continuation qua trang. Nguồn ngoài sáu trang chỉ là reference-only, không tự trở thành Gold hoặc decision universe.</p><p><a href='evaluation-scope-manifest.json'>Evaluation scope</a> · <a href='adjudication/worksheet.pending.json'>213-row worksheet</a> · <a href='adjudication/scope-review.pending.json'>Scope &amp; boundary review</a> · <a href='README.md'>Review instructions</a></p>");
Write("source-manifest.original.json", originalSource); Write("pilot-source-manifest.original.json", pilotSource);
CopyPinned("selection.original.json", Path.Combine(args[0], "selection.json"), "52216c9749ccbfcd57e649c8ed0fb2e391af358af3e97f0e508899ca5543888e");
CopyPinned("gold-adjudication-manifest.draft.original.json", Path.Combine(args[0], "gold-adjudication-manifest.draft.json"), "20f5ad1cb3cb7894ec88b66358c13f05332ef78e65aa0613da7cf8c004483bf5");
CopyPinned("scorer-policy-manifest.candidate.original.json", Path.Combine(args[0], "scorer-policy-manifest.candidate.json"), "04add542650d995debf31898adae33f28f7c222513c80788981e3c29ab0c945d");
var n = 0; var contextOccurrences = 0;
foreach (var pdoc in pilot.RootElement.GetProperty("documents").EnumerateArray())
{
    var id = pdoc.GetProperty("document").GetString()!; var folder = $"documents/D{++n:00}";
    var doc = sources.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == id);
    CopyPinned(folder + "/source.pdf", Path.Combine(args[1], id, "source.pdf"), doc.GetProperty("sha256").GetString()!);
    var snapshotBytes = ReadPinned(Path.Combine(args[1], id, "snapshot.json"), doc.GetProperty("snapshotSha256").GetString()!);
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var native = snapshot.RootElement.GetProperty("nativeLines").EnumerateArray().ToDictionary(l => l.GetProperty("sourceId").GetString()!, l => l.GetProperty("lines")[0]);
    var selectedPages = pdoc.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("page").GetInt32()).ToArray();
    var atoms = snapshot.RootElement.GetProperty("atoms").EnumerateArray().Select(a =>
    {
        var sourceId = a.GetProperty("sourceId").GetString()!; var line = native[sourceId];
        return JsonSerializer.SerializeToElement(new { alias = a.GetProperty("alias"), sourceId,
            ordinal = a.GetProperty("ordinal"), page = a.GetProperty("page"), text = a.GetProperty("text"),
            left = line.GetProperty("left"), right = line.GetProperty("right"), bottom = line.GetProperty("bottom"), top = line.GetProperty("top"),
            evaluationStatus = selectedPages.Contains(a.GetProperty("page").GetInt32()) ? "PENDING" : "OUT_OF_EVALUATION_SCOPE",
            referenceOnly = !selectedPages.Contains(a.GetProperty("page").GetInt32()) });
    }).ToArray();
    var atomsByAlias = atoms.ToDictionary(a => a.GetProperty("alias").GetString()!, StringComparer.Ordinal);
    contextOccurrences += atoms.Length;
    Write(folder + "/all-occurrences.json", Bytes(new { document = id, snapshotSha256 = doc.GetProperty("snapshotSha256"),
        sourceUniverseSha256 = doc.GetProperty("sourceUniverseSha256"), coordinatesBasis = "FROZEN_NATIVE_LINE_POINT_BOUNDS_NO_SEMANTIC_PREDICATES",
        coordinatesCompletenessCertified = false, atoms }));
    var draftName = id + ".gold-adjudication.draft.json";
    var draftPath = Path.Combine(args[0], draftName);
    using var draft = JsonDocument.Parse(File.ReadAllBytes(draftPath));
    if (draft.RootElement.GetProperty("annotations").EnumerateArray().Any(a =>
        a.GetProperty("status").GetString() != atomsByAlias[a.GetProperty("alias").GetString()!].GetProperty("evaluationStatus").GetString() ||
        a.GetProperty("semanticFunction").ValueKind != JsonValueKind.Null || a.GetProperty("headingMembership").ValueKind != JsonValueKind.Null || a.GetProperty("extentParts").ValueKind != JsonValueKind.Null))
        throw new InvalidOperationException("UNLABELLED_SOURCE_REVIEW_REQUIRED");
    using var goldManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[0], "gold-adjudication-manifest.draft.json")));
    var draftHash = goldManifest.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == id).GetProperty("draftSha256").GetString()!;
    CopyPinned($"adjudication/D{n:00}.gold-adjudication.draft.json", draftPath, draftHash);
    html.Append($"<h2>D{n:00} — {E(doc.GetProperty("sourceKey").GetString()!)}</h2><p>{E(pdoc.GetProperty("reason").GetString()!)} · <a href='{folder}/source.pdf'>Full original PDF</a> · <a href='{folder}/all-occurrences.json'>All-page source geometry for boundary review</a> · <a href='adjudication/D{n:00}.gold-adjudication.draft.json'>Full-partition PENDING form</a></p>");
    foreach (var page in pdoc.GetProperty("pages").EnumerateArray())
    {
        var number = page.GetProperty("page").GetInt32(); var sourceName = id + ".page-" + number + ".source-only.json";
        var pageTarget = folder + $"/page-{number:0000}.source-only.json";
        CopyPinned(pageTarget, Path.Combine(args[0], sourceName), page.GetProperty("pageSourceSha256").GetString()!);
        var shapeDoc = shapes.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == id);
        var image = shapeDoc.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("page").GetInt32() == number);
        var imageTarget = folder + $"/page-{number:0000}.png";
        CopyPinned(imageTarget, Path.Combine(args[2], id, image.GetProperty("file").GetString()!), page.GetProperty("imageSha256").GetString()!);
        var pageAtoms = atoms.Where(a => a.GetProperty("page").GetInt32() == number).ToArray();
        scopeForms.Add(new { document = id, page = number, occurrences = pageAtoms.Length, scopeDecision = "PENDING",
            predecessorPage = number > 1 ? (int?)(number - 1) : null, successorPage = number < doc.GetProperty("pages").GetInt32() ? (int?)(number + 1) : null,
            sourceBoundaryReview = "PENDING", scopeExpansionNeeded = (bool?)null, proposedAdditionalPages = Array.Empty<int>(), rationale = (string?)null,
            extentUnitsAdjudicated = (int?)null, exactEvaluableUnits = (int?)null, exactEvaluabilityRate = (double?)null,
            evaluationChangesRequireNewManifest = true });
        html.Append($"<h3>Page {number} — {pageAtoms.Length} occurrences</h3><p><a href='{folder}/source.pdf#page={number}'>PDF page</a> · <a href='{pageTarget}'>Frozen page atoms + native glyph geometry</a></p><img src='{imageTarget}' alt='Original PDF page {number}'><table><thead><tr><th>Alias / source ID</th><th>Ordinal</th><th>Text</th><th>Bbox points</th><th>Review</th></tr></thead><tbody>");
        foreach (var a in pageAtoms)
        {
            worksheet.Add(new { document = id, alias = a.GetProperty("alias").GetString(), sourceId = a.GetProperty("sourceId").GetString(), page = number,
                ordinal = a.GetProperty("ordinal").GetInt32(), reviewStatus = "PENDING",
                semanticFunctionStatus = "PENDING", semanticFunction = (string?)null,
                headingMembershipStatus = "PENDING", headingMembership = (bool?)null,
                anchorStatus = "PENDING", isDistinctAnchor = (bool?)null,
                unitRole = (string?)null, unitId = (string?)null, extentStatus = "PENDING", extentParts = Array.Empty<object>(),
                boundaryClosureStatus = "PENDING", reviewedFirstOutside = (string?)null, documentEndAttested = (bool?)null, sourceReviewNote = (string?)null });
            html.Append($"<tr><td>{E(a.GetProperty("alias").GetString()!)}<br>{E(a.GetProperty("sourceId").GetString()!)}</td><td>{a.GetProperty("ordinal")}</td><td>{E(a.GetProperty("text").GetString()!)}</td><td>[{a.GetProperty("left")}, {a.GetProperty("bottom")}, {a.GetProperty("right")}, {a.GetProperty("top")}]</td><td>PENDING</td></tr>");
        }
        html.Append("</tbody></table>");
    }
    docs.Add(new { document = id, folder, sourceSha256 = doc.GetProperty("sha256"), sourceUniverseSha256 = doc.GetProperty("sourceUniverseSha256"),
        snapshotSha256 = doc.GetProperty("snapshotSha256"), pages = selectedPages, documentPages = doc.GetProperty("pages"), priorExposure = doc.GetProperty("priorExposure") });
}
if (n != 5 || worksheet.Count != 213 || scopeForms.Count != 6) throw new InvalidOperationException("PILOT_CARDINALITY_DRIFT");
Write("evaluation-scope-manifest.json", Bytes(new { version = "P7_PORTABLE_PILOT_SCOPE_REVIEW_V1", originalPilotSourceManifestSha256 = Hash(pilotSource),
    evaluationPages = 6, evaluationOccurrences = 213, documents = docs, scopeApproval = "AWAITING_INDEPENDENT_SOURCE_REVIEW", contextOnlySourcesNeverBecomeGold = true,
    expansionPolicy = "SOURCE_ONLY_REVIEW_LAST_UNIT_AND_IMMEDIATE_SUCCESSOR_RECURSIVE_ADJACENT_PAGE_SUPPORT_IF_UNRESOLVED_NEW_MANIFEST_BEFORE_PREDICTIONS",
    automaticScopeExpansion = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" }));
Write("adjudication/scope-review.pending.json", Bytes(new { version = "P7_SCOPE_CLOSURE_REVIEW_FORM_V1", status = "PENDING", rows = scopeForms }));
Write("adjudication/worksheet.pending.json", Bytes(new { version = "P7_INDEPENDENT_AXES_REVIEW_WORKSHEET_V1", status = "PENDING_NOT_GOLD",
    interpretationRecordsConsulted = false, reviewer = (string?)null, rows = worksheet }));
Write("README.md", Encoding.UTF8.GetBytes("""
# P7 portable independent source-review package

Extract ZIP to a fresh directory; open index.html. All links are relative and work offline.
Five original PDFs are byte-identical copies; six PNGs reuse existing frozen renders.
All 213 selected-page occurrences are shown in the HTML tables with source IDs and point bounds.
Per-page JSON retains native glyph data. All-page occurrence geometry is provided for boundary
closure review; those outside six pages are reference-only, NOT semantic negatives or added Gold.

## Independent review

1. Review each whole page, positives AND negatives. Approve/reject scope in scope-review.pending.json.
2. Check the last possible heading on each selected page against the original PDF's next page and
   the all-page source geometry. Review the previous page for possible incoming continuation.
3. If a boundary remains open, record PENDING. Expand review only by source-based unit/first-outside
   closure, never by model prediction. Record requested pages; approve a NEW manifest before scoring.
4. In worksheet.pending.json label semantic function, heading membership, distinct anchor and
   extent independently. The worksheet is a review input, not directly approved scorer Gold.
5. Include title/subtitle as independently adjudicated units. Shared topic alone does not merge units.
   Table placement neither includes nor excludes a heading automatically. Resolve all disagreements
   from original source; source ID, spans and source-review notes must accompany adjudicated units.
6. Exact boundary requires a closed unit + reviewed immediate successor, or an independently attested
   actual document end. Report exact-evaluable / all reviewed true-anchor units after adjudication;
   unknown rate is null, not zero. Do not choose a favorable evaluation denominator after inference.

## Independent metrics

Extent NOT_EVALUABLE never exempts known membership FP/FN. F1 function accuracy, G2A distinct-anchor
accuracy, reconstructed membership and H2-C exact extent are different metrics. The current pilot
scorer scores reconstructed occurrence membership and extent; it is NOT a complete F1/G2A stage scorer.
Review worksheet keeps their Gold axes separate for future frozen stage scoring.
Overlap is always an observation; semantic correctness requires matching independent Gold.

No responses/interpretations, API key, provider calls, approved labels or production changes included.
Reference PDF is known development data; no held-out/generalization claim. Tokenizer/usage BLOCKED;
Gold/scorer/request freeze OPEN; provider and promotion LOCKED. Existing manifests are copied intact.
Original/scorer manifests are historical candidates, not newly approved or rebaselined.

Review forms are deliberately editable. Compare originals with bundle-integrity.json before editing;
keep reviewed copies separately, with reviewer identity, source-only attestation and approval.
"""));
html.Append("</html>"); Write("index.html", Encoding.UTF8.GetBytes(html.ToString()));
// Validate every local HTML target before creating the archive. No host-specific URI may escape.
foreach (Match link in Regex.Matches(html.ToString(), "(?:href|src)='([^']+)'"))
{
    var target = link.Groups[1].Value.Split('#')[0];
    if (target.Contains(':') || Path.IsPathRooted(target) || !File.Exists(Path.Combine(outputRoot, target))) throw new InvalidOperationException("NON_PORTABLE_OR_MISSING_LINK");
}
var integrity = Bytes(new { version = "P7_PORTABLE_REVIEW_BUNDLE_INTEGRITY_V1", sourceDocuments = n, evaluationPages = scopeForms.Count,
    pendingOccurrences = worksheet.Count, contextReferenceOccurrences = contextOccurrences, files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
    sourceOnly = true, approvedGoldUnits = 0, providerCalls = 0 });
Write("bundle-integrity.json", integrity);
using (var zipStream = new FileStream(args[4], FileMode.CreateNew))
using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
    foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
    {
        var entry = archive.CreateEntry(f.Path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var input = File.OpenRead(Path.Combine(outputRoot, f.Path)); using var output = entry.Open(); input.CopyTo(output);
    }
using (var archive = ZipFile.OpenRead(args[4]))
    foreach (var f in files)
    {
        using var input = archive.GetEntry(f.Path)!.Open();
        if (Convert.ToHexStringLower(SHA256.HashData(input)) != f.Sha256) throw new InvalidOperationException("ZIP_INTEGRITY_FAILURE");
    }
var receipt = Bytes(new { version = "P7_PORTABLE_SOURCE_REVIEW_ZIP_RECEIPT_V1", status = "AWAITING_INDEPENDENT_SOURCE_REVIEW",
    zipSha256 = HashFile(args[4]), zipBytes = new FileInfo(args[4]).Length, integrityManifestSha256 = Hash(integrity),
    files = files.Count, originalPdfCopies = 5, selectedPageImages = 6, pendingOccurrences = 213, contextReferenceOccurrences = contextOccurrences,
    relativeHtmlLinksVerified = true, zipEntryHashesVerified = true, exactEvaluabilityRate = (double?)null,
    evaluationScopeAutomaticallyExpanded = false, goldAdjudication = "PENDING", D23 = "OPEN", providerCalls = 0,
    sourcePdfsModified = false, productionChanged = false, frozenRequestsChanged = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" });
using (var f = new FileStream(args[5], FileMode.CreateNew)) f.Write(receipt);
Console.WriteLine(Encoding.UTF8.GetString(receipt));

void Write(string relative, byte[] bytes)
{
    var target = Path.GetFullPath(Path.Combine(outputRoot, relative));
    if (!target.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("OUTPUT_ESCAPE");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    using var f = new FileStream(target, FileMode.CreateNew); f.Write(bytes); f.Flush(true);
    files.Add(new(relative, bytes.LongLength, Hash(bytes)));
}
void CopyPinned(string relative, string input, string hash) => Write(relative, ReadPinned(input, hash));
static byte[] ReadPinned(string path, string expected) { var bytes = File.ReadAllBytes(path); if (Hash(bytes) != expected) throw new InvalidOperationException("PINNED_HASH_MISMATCH: " + Path.GetFileName(path)); return bytes; }
static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static string E(string text) => WebUtility.HtmlEncode(text);
record FileEntry(string Path, long Bytes, string Sha256);
