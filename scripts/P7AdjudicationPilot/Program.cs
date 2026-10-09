using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

if (args.Length != 4) throw new ArgumentException("Usage: P7AdjudicationPilot <source-pack> <shape-pack> <new-private-dir> <new-public-receipt>");
if (Directory.Exists(args[2]) || File.Exists(args[3])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var sourceBytes = File.ReadAllBytes(Path.Combine(args[0], "source-manifest.json"));
var shapeBytes = File.ReadAllBytes(Path.Combine(args[1], "screening-receipt.json"));
if (Hash(sourceBytes) != "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30" ||
    Hash(shapeBytes) != "d3dda9a69cf07dc8d214ed86767f5a643a059a42e9834b824af4d79867dc3207") throw new InvalidOperationException("SOURCE_DRIFT");
using var source = JsonDocument.Parse(sourceBytes); using var shape = JsonDocument.Parse(shapeBytes);
// Fixed source-form selection from the prior source-only shape audit. No response/capture input exists.
var selection = new[] {
    new Pick("03c86fc3", [10], "MULTILINE_NUMBERED_HEADING_REPEATED_FURNITURE"),
    new Pick("08f459b8", [52], "RULED_TABLE_WITH_PENDING_SPANNING_HEADING_CANDIDATE"),
    new Pick("2b1a4e73", [1], "TITLE_DATE_FRONT_MATTER_AND_PROSE"),
    new Pick("2663227a", [2], "TOC_CROSS_REFERENCE_REPEATED_FURNITURE"),
    new Pick("f427233d", [1, 2], "ADMINISTRATIVE_TITLE_SUBTITLE_AND_THREE_COLUMN_TABLE_DEVELOPMENT_EXPOSED") };
Directory.CreateDirectory(args[2]);
var selectionBytes = Bytes(new { version = "P7_SOURCE_ONLY_PILOT_SELECTION_V1", selection,
    basis = "WHOLE_PAGES_FROM_PREVIOUS_SOURCE_SHAPE_OBSERVATIONS_NOT_PREDICTIONS",
    sourceManifestSha256 = Hash(sourceBytes), scopeApproval = "PENDING_USER_REVIEW", claimsHeldOut = false });
// Write the selection before reading any page content. No provider responses are consulted.
Write(Path.Combine(args[2], "selection.json"), selectionBytes);
var summaries = new List<object>(); var goldDrafts = new List<object>(); var links = new StringBuilder();
foreach (var pick in selection)
{
    var doc = source.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("sha256").GetString()!.StartsWith(pick.Prefix, StringComparison.Ordinal));
    var id = doc.GetProperty("document").GetString()!;
    var snapshotBytes = File.ReadAllBytes(Path.Combine(args[0], id, "snapshot.json"));
    if (Hash(snapshotBytes) != doc.GetProperty("snapshotSha256").GetString() || HashFile(Path.Combine(args[0], id, "source.pdf")) != doc.GetProperty("sha256").GetString())
        throw new InvalidOperationException("SNAPSHOT_DRIFT");
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var atoms = snapshot.RootElement.GetProperty("atoms").EnumerateArray().ToArray();
    var universe = atoms.Select(a => new ReviewOccurrence(a.GetProperty("alias").GetString()!, a.GetProperty("page").GetInt32(), a.GetProperty("text").GetString()!.Length)).ToArray();
    var scope = new EvaluationScope(P7EvaluationUniverse.Version, doc.GetProperty("sha256").GetString()!, doc.GetProperty("sourceUniverseSha256").GetString()!,
        doc.GetProperty("snapshotSha256").GetString()!, pick.Pages, P7EvaluationUniverse.CrossingPolicy);
    var draft = Bytes(new { version = "P7_GOLD_ADJUDICATION_DRAFT_V1", scope, annotations = P7EvaluationUniverse.Draft(scope, universe),
        units = Array.Empty<PilotGoldUnit>(), status = "PENDING_INDEPENDENT_SOURCE_REVIEW_AND_USER_APPROVAL", reviewer = (string?)null,
        modelOutputsConsulted = false, approval = (string?)null, titleSubtitlePolicySha256 = source.RootElement.GetProperty("policySha256").GetString() });
    Write(Path.Combine(args[2], id + ".gold-adjudication.draft.json"), draft);
    goldDrafts.Add(new { document = id, draftSha256 = Hash(draft), status = "PENDING", approvedUnits = 0 });
    var images = shape.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == id).GetProperty("images").EnumerateArray().ToArray();
    var pages = new List<object>();
    foreach (var page in pick.Pages)
    {
        var image = images.Single(i => i.GetProperty("page").GetInt32() == page);
        var imagePath = Path.Combine(args[1], id, image.GetProperty("file").GetString()!);
        if (HashFile(imagePath) != image.GetProperty("imageSha256").GetString()) throw new InvalidOperationException("RENDER_DRIFT");
        var pageAtoms = atoms.Where(a => a.GetProperty("page").GetInt32() == page).ToArray();
        var native = snapshot.RootElement.GetProperty("nativeLines").EnumerateArray().Where(l => pageAtoms.Any(a => a.GetProperty("sourceId").GetString() == l.GetProperty("sourceId").GetString())).ToArray();
        var pageBytes = Bytes(new { document = id, page, sourceSha256 = scope.SourceSha256, scope.SnapshotSha256,
            atoms = pageAtoms, nativeLines = native, imageSha256 = HashFile(imagePath), membershipStatus = "PENDING_ALL_OCCURRENCES_NO_DEFAULT_NEGATIVES" });
        var name = id + ".page-" + page + ".source-only.json"; Write(Path.Combine(args[2], name), pageBytes);
        pages.Add(new { page, occurrences = pageAtoms.Length, pageSourceSha256 = Hash(pageBytes), imageSha256 = HashFile(imagePath) });
        links.Append($"<h2>{WebUtility.HtmlEncode(doc.GetProperty("sourceKey").GetString())} — page {page}</h2><p>{WebUtility.HtmlEncode(pick.Reason)}</p><a href='{new Uri(Path.Combine(args[0], id, "source.pdf")).AbsoluteUri}#page={page}'>Original PDF</a> | <a href='{name}'>All page source IDs, text and glyph geometry</a> | <a href='{id}.gold-adjudication.draft.json'>Unlabelled review draft</a><p><img style='max-width:100%' src='{new Uri(imagePath).AbsoluteUri}'></p>");
    }
    summaries.Add(new { document = id, sourceSha256 = scope.SourceSha256, sourceUniverseSha256 = scope.UniverseSha256, snapshotSha256 = scope.SnapshotSha256,
        pick.Reason, pages, fullDocumentReview = false, priorExposure = doc.GetProperty("priorExposure").GetString() });
}
var sourceManifest = Bytes(new { version = "P7_LIMITED_PILOT_SOURCE_MANIFEST_V1", selectionSha256 = Hash(selectionBytes), sourceManifestSha256 = Hash(sourceBytes),
    scope = "FULL_SELECTED_PAGES_NO_MODEL_CANDIDATE_FILTER", documents = summaries, providerCalls = 0, modelResponsesRead = false, claimsHeldOut = false });
Write(Path.Combine(args[2], "source-manifest.json"), sourceManifest);
var goldManifest = Bytes(new { version = "P7_PILOT_GOLD_ADJUDICATION_MANIFEST_DRAFT_V1", sourceManifestSha256 = Hash(sourceManifest), documents = goldDrafts,
    status = "NOT_FROZEN_NO_UNIT_APPROVAL", treatmentInterpretationsUsed = false });
Write(Path.Combine(args[2], "gold-adjudication-manifest.draft.json"), goldManifest);
var scorerManifest = Bytes(new { version = P7PilotScorer.Version, status = "SYNTHETIC_QUALIFICATION_CANDIDATE_NOT_REAL_GOLD_FREEZE",
    membership = "OCCURRENCE_SET_TP_FP_FN_ON_COMPLETELY_REVIEWED_PAGES", boundary = "PART_ORDER_SPAN_EXACT_AT_SEPARATE_TRUE_ANCHORS_WITH_REVIEWED_EXIT_OR_ATTESTED_DOCUMENT_END",
    crossing = "RETAIN_UNTRIMMED_EXACT_NOT_EVALUABLE_WHEN_OUTSIDE_TRUTH_UNKNOWN", unknown = "NEVER_OTHER_OR_FALSE",
    invalid = "CONTRACT_INVALID_MEMBERSHIP_METRICS_BLOCKED", duplicateAndOverlap = "OBSERVE_ALL_NO_PRUNING_NO_UNIT_ACCURACY_CLAIM_WHEN_DUPLICATED",
    functionsDoNotOverrideExtent = true, semanticTruthCertified = false, providerCalls = 0 });
Write(Path.Combine(args[2], "scorer-policy-manifest.candidate.json"), scorerManifest);
Write(Path.Combine(args[2], "index.html"), Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><h1>P7 limited source-only adjudication pilot</h1><p>5 documents / 6 whole pages. Review every occurrence, including negatives; title and subtitle are independently adjudicated units. Table membership remains a semantic review question. No predictions, interpretation records, approved Gold or provider calls. Scope selection is frozen as a proposal; user approval and real Gold remain pending.</p>" + links));
var receipt = Bytes(new { version = "P7_D23_ADJUDICATION_PILOT_PREFLIGHT_V1", status = "SOURCE_SELECTION_FIXED_GOLD_PENDING_SCORER_SYNTHETIC_ONLY",
    sourceManifestSha256 = Hash(sourceManifest), selectionSha256 = Hash(selectionBytes), goldDraftManifestSha256 = Hash(goldManifest), scorerCandidateManifestSha256 = Hash(scorerManifest),
    documents = summaries, sourceDocuments = 5, evaluationPages = 6, reviewedMembershipUnits = 0, adjudication = "OPEN_USER_APPROVAL_REQUIRED",
    coverageGaps = new[] { "CROSS_PAGE_HEADING", "TWO_COLUMN_BODY_TEXT", "STRICT_BORDERLESS_TABLE", "WHOLE_DOCUMENT_WITHOUT_TABLE", "TABLE_HEADING_MEMBERSHIP_PENDING" },
    gapsProvenAbsent = false, generalizationQualified = false, claimsHeldOut = false,
    sourceManifest = "SOURCE_IDENTITY_FIXED_SCOPE_SELECTION_PROPOSAL", goldManifest = "NOT_FROZEN", scorerManifest = "CANDIDATE_SYNTHETIC_TESTS_REQUIRED",
    treatmentRequestManifest = "NOT_FROZEN", providerCalls = 0, modelResponsesRead = false, goldMutation = "NONE", productionChanged = false,
    frozenRequestsChanged = false, exactTokenizerMapping = "BLOCKED", tokenBudgetMeasurement = "BLOCKED", providerExecution = "LOCKED", productionPromotion = "LOCKED" });
Write(args[3], receipt); Console.WriteLine(JsonSerializer.Serialize(new { receiptSha256 = Hash(receipt), documents = 5, pages = 6, status = "GOLD_PENDING" }));
static byte[] Bytes<T>(T x) => JsonSerializer.SerializeToUtf8Bytes(x, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
record Pick(string Prefix, int[] Pages, string Reason);
