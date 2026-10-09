using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

if (args.Length != 5) throw new ArgumentException("Usage: P7ShapeReviewReceipt <source-pack> <shape-pack> <source-only-review-notes> <new-private-review-dir> <new-public-receipt>");
if (Directory.Exists(args[3]) || File.Exists(args[4])) throw new InvalidOperationException("new-output-required");
using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[0], "source-manifest.json")));
var screenBytes = File.ReadAllBytes(Path.Combine(args[1], "screening-receipt.json"));
if (Hash(File.ReadAllBytes(Path.Combine(args[0], "source-manifest.json"))) != "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30" ||
    Hash(screenBytes) != "d3dda9a69cf07dc8d214ed86767f5a643a059a42e9834b824af4d79867dc3207") throw new InvalidOperationException("frozen-receipt-drift");
using var screen = JsonDocument.Parse(screenBytes);
var notesBytes = File.ReadAllBytes(args[2]); using var notes = JsonDocument.Parse(notesBytes);
if (notes.RootElement.GetProperty("semanticMembershipAdjudicated").GetBoolean() || notes.RootElement.GetProperty("interpretationRecordsConsulted").GetBoolean())
    throw new InvalidOperationException("source-shape-review-not-gold-or-treatment-analysis");
Directory.CreateDirectory(args[3]);
var shapeTypes = new[] { "ADMINISTRATIVE_DOCUMENT", "MULTILINE_TITLE_OR_HEADING", "MULTICOLUMN_TABLE", "TWO_COLUMN_BODY_TEXT", "RULED_TABLE", "BORDERLESS_TABLE", "PARTIALLY_RULED_TABLE",
    "DOCUMENT_HEADING_INSIDE_TABLE", "TITLE_SUBTITLE", "CROSS_PAGE_HEADING", "REPEATED_HEADER_FOOTER", "TOC_CROSS_REFERENCE", "ROTATED_OR_UNUSUAL_LAYOUT" };
var docs = source.RootElement.GetProperty("documents").EnumerateArray().ToArray();
var cases = new List<Case>(); var docSummaries = new List<object>(); var links = new StringBuilder();
foreach (var doc in docs)
{
    var id = doc.GetProperty("document").GetString()!;
    var snapshotBytes = File.ReadAllBytes(Path.Combine(args[0], id, "snapshot.json"));
    if (Hash(snapshotBytes) != doc.GetProperty("snapshotSha256").GetString() ||
        HashFile(Path.Combine(args[0], id, "source.pdf")) != doc.GetProperty("sha256").GetString()) throw new InvalidOperationException("source-drift");
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var native = snapshot.RootElement.GetProperty("nativeLines").EnumerateArray().ToDictionary(l => l.GetProperty("sourceId").GetString()!, l => l.GetProperty("lines")[0]);
    var atoms = snapshot.RootElement.GetProperty("atoms").EnumerateArray().Select(a =>
    {
        var sourceId = a.GetProperty("sourceId").GetString()!; var line = native[sourceId];
        return JsonSerializer.SerializeToElement(new { alias = a.GetProperty("alias").GetString(), sourceId,
            ordinal = a.GetProperty("ordinal").GetInt32(), page = a.GetProperty("page").GetInt32(), text = a.GetProperty("text").GetString(),
            left = line.GetProperty("left"), right = line.GetProperty("right"), bottom = line.GetProperty("bottom"), top = line.GetProperty("top") });
    }).ToArray();
    var screenDoc = screen.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("document").GetString() == id);
    var occurrenceUniverse = atoms.Select(a => new ReviewOccurrence(a.GetProperty("alias").GetString()!, a.GetProperty("page").GetInt32(), a.GetProperty("text").GetString()!.Length)).ToArray();
    // No evaluation subset is auto-chosen from shape cues. An empty draft plan is deliberately not ready.
    var scope = new EvaluationScope(P7EvaluationUniverse.Version, doc.GetProperty("sha256").GetString()!, doc.GetProperty("sourceUniverseSha256").GetString()!,
        doc.GetProperty("snapshotSha256").GetString()!, [], P7EvaluationUniverse.CrossingPolicy);
    Write(Path.Combine(args[3], id + ".evaluation-scope.draft.json"), Bytes(new { status = "DRAFT_SCOPE_SELECTION_REQUIRED_NOT_FROZEN", scope,
        annotations = P7EvaluationUniverse.Draft(scope, occurrenceUniverse),
        noMetrics = true, outsideScopeIsNotNegative = true, independentReviewApproved = false }));
    foreach (var note in notes.RootElement.GetProperty("cases").EnumerateArray().Where(c => id.StartsWith("PDF-" + c.GetProperty("documentPrefix").GetString(), StringComparison.Ordinal)))
    {
        var page = note.GetProperty("page").GetInt32();
        var images = screenDoc.GetProperty("images").EnumerateArray().Where(x => x.GetProperty("page").GetInt32() == page).ToArray();
        if (images.Length != 1) throw new InvalidOperationException("review-without-rendered-evidence");
        var imageFile = images[0].GetProperty("file").GetString()!;
        var imagePath = Path.Combine(args[1], id, imageFile);
        if (HashFile(imagePath) != images[0].GetProperty("imageSha256").GetString()) throw new InvalidOperationException("image-drift");
        var subjects = atoms.Where(a => a.GetProperty("page").GetInt32() == page).Select(a => new {
            alias = a.GetProperty("alias").GetString(), sourceId = a.GetProperty("sourceId").GetString(),
            ordinal = a.GetProperty("ordinal").GetInt32(), page, a = a.Clone() }).ToArray();
        var caseId = "SHAPE-" + doc.GetProperty("sha256").GetString() + "-P" + page;
        var observedShapes = note.GetProperty("shapes").EnumerateArray().Select(v => v.GetString()!).ToArray();
        var pending = note.TryGetProperty("pendingSemanticShapes", out var ps) ? ps.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
        if (observedShapes.Concat(pending).Any(x => !shapeTypes.Contains(x) && x != "PARALLEL_FRONT_MATTER_REGIONS")) throw new InvalidOperationException("unregistered-shape");
        var record = new { version = "P7_SOURCE_ONLY_SHAPE_CASE_V1", caseId, document = id, sourceSha256 = scope.SourceSha256,
            universeSha256 = scope.UniverseSha256, snapshotSha256 = scope.SnapshotSha256, page,
            sourceRegion = "WHOLE_PAGE_OBSERVATION_NO_SEMANTIC_UNIT_SELECTION", originalPdfReference = id + "/source.pdf#page=" + page,
            imageReference = id + "/" + imageFile, imageSha256 = HashFile(imagePath),
            subjects, observedShapes, pendingSemanticShapes = pending,
            observation = note.GetProperty("observation").GetString(), reviewBasis = "ORIGINAL_PDF_RENDER_PLUS_FROZEN_SOURCE_COORDINATES",
            approvalStatus = "SOURCE_SHAPE_OBSERVATION_NOT_GOLD_PENDING_USER_REVIEW", goldCreated = false };
        var bytes = Bytes(record); Write(Path.Combine(args[3], caseId + ".json"), bytes);
        cases.Add(new(caseId, id, page, observedShapes, pending, subjects.Length, Hash(bytes), HashFile(imagePath)));
        var originalUri = new Uri(Path.Combine(args[0], id, "source.pdf")).AbsoluteUri;
        links.Append($"<li>{WebUtility.HtmlEncode(Path.GetFileName(doc.GetProperty("sourceKey").GetString()))}, page {page}: <a href='{originalUri}#page={page}'>Original PDF</a>; <a href='{new Uri(imagePath).AbsoluteUri}'>Original render</a>; <a href='{caseId}.json'>all source IDs / bboxes</a><p>{WebUtility.HtmlEncode(record.observation)}</p></li>");
    }
    var reviewed = cases.Where(c => c.Document == id).ToArray();
    docSummaries.Add(new { document = id, totalPages = doc.GetProperty("pages").GetInt32(), physicalPagesScreened = doc.GetProperty("pages").GetInt32(),
        visuallyReviewedPages = reviewed.Select(c => c.Page).Distinct().Order().ToArray(), completeVisualDocumentReview = false,
        shapes = shapeTypes.Select(shape => new { shape, observedCases = reviewed.Count(c => c.Shapes.Contains(shape)),
            pendingSemanticCases = reviewed.Count(c => c.Pending.Contains(shape)),
            coverage = reviewed.Any(c => c.Shapes.Contains(shape)) ? "OBSERVED_SOURCE_RENDER_FORM_NOT_GOLD" : "NOT_COVERED_BY_CONFIRMED_EXAMPLE",
            absenceFromWholeDocumentProven = false }).ToArray() });
}
Write(Path.Combine(args[3], "index.html"), Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><h1>P7 source-only shape observations</h1><p>19 pages reviewed visually; all 647 screened physically. No membership, extent Gold or semantic predicate is produced. Missing examples do not prove absence from the remaining pages.</p><ul>" + links + "</ul>"));
var report = new { version = "P7_D23_SOURCE_SHAPE_AUDIT_V1", status = "PHYSICAL_SCREENING_COMPLETE_LIMITED_VISUAL_SHAPE_REVIEW_SOURCE_ONLY",
    sourceManifestSha256 = Hash(File.ReadAllBytes(Path.Combine(args[0], "source-manifest.json"))), screeningReceiptSha256 = Hash(screenBytes), reviewNotesSha256 = Hash(notesBytes),
    documents = docSummaries, cases, shapeSummary = shapeTypes.Select(shape => new { shape, observedDocumentCount = cases.Where(c => c.Shapes.Contains(shape)).Select(c => c.Document).Distinct().Count(),
        observedCaseCount = cases.Count(c => c.Shapes.Contains(shape)), pendingSemanticCaseCount = cases.Count(c => c.Pending.Contains(shape)),
        coverage = cases.Any(c => c.Shapes.Contains(shape)) ? "OBSERVED_SOURCE_RENDER_FORM_NOT_GOLD" : "NOT_COVERED", absenceFromCorpusProven = false }).ToArray(),
    evaluationUniverse = "NOT_FROZEN_NO_PAGES_AUTO_SELECTED", draftStatuses = new[] { "ADJUDICATED", "PENDING", "OUT_OF_EVALUATION_SCOPE" },
    trueBorderlessPolicy = "NO_FULL_GRID_IS_NOT_STRICTLY_UNRULED_PARTIALLY_RULED_REPORTED_SEPARATELY",
    pendingShapeFollowUp = new[] { "CROSS_PAGE_HEADING", "TWO_COLUMN_BODY_TEXT", "BORDERLESS_TABLE", "DOCUMENT_HEADING_INSIDE_TABLE_SEMANTIC_ADJUDICATION" },
    shapeAuditClosure = "OPEN_COVERAGE_GAPS_AND_USER_REVIEW_REMAIN", membershipAdjudicated = false, extentAdjudicated = false,
    providerCalls = 0, modelResponsesRead = false, goldMutation = "NONE", productionChanged = false, frozenRequestsChanged = false,
    realCohortFreeze = "NOT_FROZEN", providerExecution = "LOCKED", productionPromotion = "LOCKED" };
Write(Path.Combine(args[3], "shape-audit.json"), Bytes(report)); Write(args[4], Bytes(report));
Console.WriteLine(JsonSerializer.Serialize(new { report.status, cases = cases.Count, documents = docs.Length, receiptSha256 = Hash(Bytes(report)) }));
static byte[] Bytes<T>(T x) => JsonSerializer.SerializeToUtf8Bytes(x, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
record Case(string CaseId, string Document, int Page, string[] Shapes, string[] Pending, int SourceReferences, string CaseSha256, string ImageSha256);
