using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

if (args.Length != 7) throw new ArgumentException("Usage: P7ApprovedPilot <source-review.zip> <proposal.zip> <original-source-pack> <user-approval.json> <new-private-dir> <new-public-receipt> <approved-v1-dir>");
if (Directory.Exists(args[4]) || File.Exists(args[5])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var sourceZipHash = HashFile(args[0]); var proposalZipHash = HashFile(args[1]);
Require(sourceZipHash == "e130cc7cd36a8e49cd20f09a013229e893d18c9dda32372a65bdebbdf37048dc" &&
    proposalZipHash == "3934f028b59b82a735d00c2ffee8f9087fda90295c225269808db9c657328b45", "INPUT_ZIP_DRIFT");
using var sourceZip = ZipFile.OpenRead(args[0]); using var proposalZip = ZipFile.OpenRead(args[1]);
var approvalBytes = File.ReadAllBytes(args[3]);
using var approvalJson = JsonDocument.Parse(approvalBytes); var ar = approvalJson.RootElement;
var approval = JsonSerializer.Deserialize<PilotUserApproval>(approvalBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
Require(Hash(approvalBytes) == "b30bf8cb5c7ba3324c2abaecbec02ed5965fda1c4f9e3de8ef382532ca8f9b7c", "ORIGINAL_APPROVAL_DRIFT");
var oldManifestBytes = ReadPinned(Path.Combine(args[6], "gold-manifest.v1.json"),
    "0a70ee0e34c10700a04390da556a2fa6b77eea463304c719d12be4354e2b9bc7");
using var oldManifest = JsonDocument.Parse(oldManifestBytes);
var amendmentBytes = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.gold-representation-amendment.v1.json");
var policyBytes = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.evaluation-policy.v1.json");
using var amendmentJson = JsonDocument.Parse(amendmentBytes); using var policyJson = JsonDocument.Parse(policyBytes);
Require(amendmentJson.RootElement.GetProperty("originalApprovalSha256").GetString() == Hash(approvalBytes) &&
    amendmentJson.RootElement.GetProperty("approvedLabelsAndScopeUnchanged").GetBoolean() &&
    !amendmentJson.RootElement.GetProperty("roleTaxonomyRequired").GetBoolean() &&
    policyJson.RootElement.GetProperty("approvalSha256").GetString() == Hash(approvalBytes), "POLICY_AMENDMENT_MISMATCH");
var proposalBytes = Entry(proposalZip, "p7-independent-source-review-proposal.v1.json");
Require(Hash(proposalBytes) == approval.ProposalSha256 && ar.GetProperty("sourceZipSha256").GetString() == sourceZipHash &&
    ar.GetProperty("proposalZipSha256").GetString() == proposalZipHash, "APPROVAL_INPUT_MISMATCH");
using var proposal = JsonDocument.Parse(proposalBytes);
var scopeBytes = Entry(sourceZip, "evaluation-scope-manifest.json");
Require(Hash(scopeBytes) == ar.GetProperty("scopeManifestSha256").GetString() &&
    Hash(scopeBytes) == proposal.RootElement.GetProperty("evaluationScopeManifestSha256").GetString() &&
    sourceZipHash == proposal.RootElement.GetProperty("sourceZipSha256").GetString(), "SCOPE_DRIFT");
using var scopeJson = JsonDocument.Parse(scopeBytes);
var integrityBytes = Entry(sourceZip, "bundle-integrity.json");
Require(Hash(integrityBytes) == "8c7ba3e10fdee42e87db68c8a48ca0345a8e44a4d876b7f27f4b649af65cf4ca", "SOURCE_INTEGRITY_DRIFT");
using var integrity = JsonDocument.Parse(integrityBytes);
foreach (var file in integrity.RootElement.GetProperty("files").EnumerateArray())
    Require(Hash(Entry(sourceZip, file.GetProperty("path").GetString()!)) == file.GetProperty("sha256").GetString(), "SOURCE_ENTRY_DRIFT");

var approvalCounts = new[] { ("approvedDocuments", 5), ("approvedPages", 6), ("approvedOccurrences", 213),
    ("approvedHeadingUnits", 15), ("approvedHeadingParts", 20) };
foreach (var (key, value) in approvalCounts) Require(ar.GetProperty(key).GetInt32() == value, "APPROVAL_CARDINALITY_DRIFT");
var generated = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
var documents = new List<object>(); var dryRuns = new List<object>(); var golds = new List<PilotApprovedDocument>();
var scenarioSummaries = new List<object>();
var crossingsExercised = 0;
var summary = new SortedDictionary<string, int>(StringComparer.Ordinal);
var scorerSourceFiles = new[] { "P7SourceOnlyReview.cs", "P7EvaluationUniverse.cs", "P7PilotScorer.cs", "P7ApprovedPilotGold.cs" }
    .Select(name => "src/DocxHeaderExtractor.V5Qualification/P7/" + name)
    .Concat(["scripts/P7ApprovedPilot/Program.cs", "scripts/P7ApprovedPilot/P7ApprovedPilot.csproj"])
    .Select(path => new { path, sha256 = HashFile(path) }).ToArray();
var scorerPolicy = Bytes(new { version = "P7_APPROVED_PILOT_SCORER_POLICY_V1", scorerVersion = P7PilotScorer.Version,
    evaluationVersion = P7EvaluationUniverse.Version, offsetConvention = P7ApprovedPilotGold.OffsetConvention,
    membership = "UNIQUE_OCCURRENCE_SET_TP_FP_FN_NOT_DIRECT_F1_OR_G2A_STAGE_SCORE",
    boundary = "ORDERED_PARTS_AND_UTF16_SPANS_EXACT_AT_TRUE_ANCHORS_WITH_REVIEWED_IMMEDIATE_EXIT",
    crossing = P7EvaluationUniverse.CrossingPolicy, unknownBoundary = "NOT_EVALUABLE_DOES_NOT_EXEMPT_KNOWN_MEMBERSHIP_ERRORS",
    unknownMembership = "OUT_OF_EVALUATION_SCOPE_NEVER_NEGATIVE", duplicateAndOverlap = "OBSERVE_WITHOUT_PRUNING",
    firstOutsideRole = "RECORDED_ONLY_NO_FROZEN_GOLD_ROLE_AUTHORITY", evaluationPolicySha256 = Hash(policyBytes),
    reviewerInterpretationsScored = false, sourceFiles = scorerSourceFiles });
generated.Add("scorer-manifest.v1.json", scorerPolicy);
// No pilot inference request set exists yet. Hash this declaration, not invented request bodies.
var requestManifest = Bytes(new { version = "P7_PILOT_REQUEST_FREEZE_STATUS_V1", status = "NOT_FROZEN",
    requests = Array.Empty<object>(), reason = "PILOT_REQUEST_UNIVERSE_AND_TREATMENT_HASHES_REQUIRE_SEPARATE_PREFLIGHT",
    productionFrozenRequestsModified = false, providerCalls = 0, providerExecution = "LOCKED" });
generated.Add("request-manifest.not-frozen.v1.json", requestManifest);
generated.Add("user-approval.v1.json", approvalBytes);
generated.Add("representation-amendment.v1.json", amendmentBytes);
generated.Add("evaluation-policy.v1.json", policyBytes);
var interpretationRoleNotes = new HashSet<string>(StringComparer.Ordinal);
var scoringParityChecks = 0;

foreach (var d in scopeJson.RootElement.GetProperty("documents").EnumerateArray())
{
    var document = d.GetProperty("document").GetString()!; var folder = d.GetProperty("folder").GetString()!;
    var originalSnapshot = ReadPinned(Path.Combine(args[2], document, "snapshot.json"), d.GetProperty("snapshotSha256").GetString()!);
    Require(HashFile(Path.Combine(args[2], document, "source.pdf")) == d.GetProperty("sourceSha256").GetString(), "ORIGINAL_PDF_DRIFT");
    Require(Hash(Entry(sourceZip, folder + "/source.pdf")) == d.GetProperty("sourceSha256").GetString(), "REVIEW_PDF_DRIFT");
    using var snapshot = JsonDocument.Parse(originalSnapshot);
    var atoms = snapshot.RootElement.GetProperty("atoms").EnumerateArray().Select(a => new PilotSourceAtom(
        a.GetProperty("alias").GetString()!, a.GetProperty("sourceId").GetString()!, a.GetProperty("ordinal").GetInt32(),
        a.GetProperty("page").GetInt32(), a.GetProperty("text").GetString()!)).ToArray();
    using var reviewSource = JsonDocument.Parse(Entry(sourceZip, folder + "/all-occurrences.json"));
    var reviewAtoms = reviewSource.RootElement.GetProperty("atoms").EnumerateArray().Select(a => new PilotSourceAtom(
        a.GetProperty("alias").GetString()!, a.GetProperty("sourceId").GetString()!, a.GetProperty("ordinal").GetInt32(),
        a.GetProperty("page").GetInt32(), a.GetProperty("text").GetString()!)).ToArray();
    Require(atoms.SequenceEqual(reviewAtoms), "REVIEW_SOURCE_ATOM_DRIFT");
    var scope = new EvaluationScope(P7EvaluationUniverse.Version, d.GetProperty("sourceSha256").GetString()!,
        d.GetProperty("sourceUniverseSha256").GetString()!, d.GetProperty("snapshotSha256").GetString()!,
        d.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32()).ToArray(), P7EvaluationUniverse.CrossingPolicy);
    var gold = P7ApprovedPilotGold.Build(proposal.RootElement, Hash(proposalBytes), approval, document, scope, atoms);
    var priorDoc = oldManifest.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("document").GetString() == document);
    var oldGoldBytes = ReadPinned(Path.Combine(args[6], priorDoc.GetProperty("goldFile").GetString()!), priorDoc.GetProperty("goldSha256").GetString()!);
    using var oldGoldJson = JsonDocument.Parse(oldGoldBytes);
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    // V1's extra unitRole fields are deliberately ignored by these scoring-owned DTOs.
    var oldGold = new PilotApprovedDocument(document, oldGoldJson.RootElement.GetProperty("scope").Deserialize<EvaluationScope>(opts)!,
        oldGoldJson.RootElement.GetProperty("annotations").Deserialize<EvaluationAnnotation[]>(opts)!,
        oldGoldJson.RootElement.GetProperty("reviewedRows").Deserialize<PilotApprovedRow[]>(opts)!,
        oldGoldJson.RootElement.GetProperty("units").Deserialize<PilotGoldUnit[]>(opts)!, []);
    var scoringBytes = P7ApprovedPilotGold.ScoringProjection(gold);
    Require(scoringBytes.SequenceEqual(P7ApprovedPilotGold.ScoringProjection(oldGold)), "V1_V2_SCORING_PROJECTION_DRIFT");
    golds.Add(gold);
    foreach (var row in gold.ReviewedRows) summary[row.SemanticFunction] = summary.GetValueOrDefault(row.SemanticFunction) + 1;
    var aliasMap = atoms.ToDictionary(a => a.Alias);
    foreach (var note in gold.ReviewerInterpretations.Where(n => n.ProposalRoleNote is not null)) interpretationRoleNotes.Add(note.ProposalRoleNote!);
    var sidecar = Bytes(new { version = "P7_REVIEWER_INTERPRETATION_SIDECAR_V1", document,
        proposalSha256 = Hash(proposalBytes), approvalSha256 = Hash(approvalBytes), evaluationPolicySha256 = Hash(policyBytes),
        scored = false, parserVerified = false, semanticTruthCertified = false, roleTaxonomyRequired = false,
        provenance = "ASSISTANT_SOURCE_REVIEW_NOTES_USER_APPROVED_TASK_DECISIONS_NOT_PHYSICAL_FACTS",
        notes = gold.ReviewerInterpretations, adjudicationNotes = gold.ReviewedRows.Where(r => r.IsDistinctAnchor ||
            approval.Resolutions.Any(p => p.Document == document && p.Alias == r.Alias)).Select(r => new {
                r.Alias, interpretation = AdjudicationNote(document, r.Alias), alternativeInterpretation = AlternativeNote(document, r.Alias),
                provenance = "ASSISTANT_PROPOSED_USER_APPROVED", taskPolicySha256 = Hash(policyBytes), scored = false }) });
    var sidecarFile = folder.Split('/')[^1] + ".reviewer-interpretations.v1.json";
    generated.Add(sidecarFile, sidecar);
    var goldBytes = Bytes(new { version = P7ApprovedPilotGold.Version, status = "USER_APPROVED_PILOT_GOLD",
        provenance = "ASSISTANT_PROPOSAL_USER_SOURCE_REVIEW_AND_FINAL_APPROVAL", approvalSha256 = Hash(approvalBytes),
        proposalSha256 = Hash(proposalBytes), document, scope, offsetConvention = P7ApprovedPilotGold.OffsetConvention,
        evaluationPolicySha256 = Hash(policyBytes), representationAmendmentSha256 = Hash(amendmentBytes),
        reviewedRows = gold.ReviewedRows, annotations = gold.Annotations, units = gold.Units,
        sourceParts = gold.Units.SelectMany(u => u.Parts.Select(p => new { unitAnchor = u.Anchor, p.Alias, p.Start, p.Length,
            aliasMap[p.Alias].SourceId, aliasMap[p.Alias].Ordinal, aliasMap[p.Alias].Text })),
        fullDocumentAdjudicated = false, assistantProposalPreserved = true, providerVerified = false,
        reviewerRoleScoring = "NOT_DEFINED_NOT_SCORED" });
    var goldFile = folder.Split('/')[^1] + ".gold-adjudication.v2.json";
    generated.Add(goldFile, goldBytes);
    documents.Add(new { document, scope.SourceSha256, scope.UniverseSha256, scope.SnapshotSha256, scope.Pages,
        goldFile, goldSha256 = Hash(goldBytes), previousGoldSha256 = Hash(oldGoldBytes), scoringProjectionSha256 = Hash(scoringBytes),
        v1V2ScoringProjectionByteIdentical = true, sidecarFile, sidecarSha256 = Hash(sidecar),
        reviewedRows = gold.ReviewedRows.Count, units = gold.Units.Count,
        parts = gold.Units.Sum(u => u.Parts.Count), outsideUnscored = gold.Annotations.Count(a => a.Status == "OUT_OF_EVALUATION_SCOPE") });
    var universe = atoms.Select(a => new ReviewOccurrence(a.Alias, a.Page, a.Text.Length)).ToArray();
    PilotScore Score(IReadOnlyList<PilotPrediction> predictions)
    {
        var current = P7PilotScorer.Score(scope, universe, gold.Annotations, gold.Units, predictions);
        var previous = P7PilotScorer.Score(oldGold.Scope, universe, oldGold.Annotations, oldGold.Units, predictions);
        Require(Bytes(current).SequenceEqual(Bytes(previous)), "V1_V2_SCORER_OUTCOME_DRIFT"); scoringParityChecks++;
        return current;
    }
    var oracle = gold.Units.Select((u, i) => new PilotPrediction("synthetic-oracle-" + i, u.Anchor, u.Parts)).ToArray();
    var perfect = Score(oracle); var empty = Score([]);
    Require(perfect.EvaluableMembership && perfect.Exact == gold.Units.Count && perfect.NotEvaluable == 0 &&
        perfect.MembershipTP == gold.ReviewedRows.Count(r => r.HeadingMembership) && perfect.MembershipFP == 0 && perfect.MembershipFN == 0,
        "ORACLE_DRY_RUN_FAILED");
    Require(empty.MembershipFN == perfect.MembershipTP && empty.MembershipFP == 0, "MISSING_PREDICTIONS_DRY_RUN_FAILED");
    var nonmember = gold.ReviewedRows.First(r => !r.HeadingMembership);
    var fp = Score([.. oracle, new("synthetic-false-positive", nonmember.Alias, [new(nonmember.Alias, 0, aliasMap[nonmember.Alias].Text.Length)])]);
    Require(fp.MembershipFP == 1 && fp.NotEvaluable == 1 && fp.Boundaries.Last().Reason == "NON_HEADING_ANCHOR_MEMBERSHIP_DIAGNOSTIC", "FP_DRY_RUN_FAILED");
    var underUnit = gold.Units.FirstOrDefault(u => u.Parts.Count > 1);
    var under = underUnit is null ? null : Score([new("synthetic-under", underUnit.Anchor, underUnit.Parts.Take(1).ToArray())]);
    if (under is not null) Require(under.Boundaries[0].Outcome == "UNDEREXTENT" && under.MembershipFN > 0, "UNDEREXTENT_DRY_RUN_FAILED");
    var overUnit = gold.Units.First(u => !gold.ReviewedRows.Single(r => r.Alias == u.ReviewedFirstOutside).HeadingMembership);
    var exit = aliasMap[overUnit.ReviewedFirstOutside!];
    var over = Score([new("synthetic-over", overUnit.Anchor, [.. overUnit.Parts, new(exit.Alias, 0, exit.Text.Length)])]);
    Require(over.MembershipFP == 1 && over.Boundaries[0].Outcome == "OVEREXTENT", "OVEREXTENT_DRY_RUN_FAILED");
    var lastInside = atoms.Select((a, i) => (a, i)).Last(x => scope.Pages.Contains(x.a.Page));
    PilotScore? crossing = null;
    if (lastInside.i + 1 < atoms.Length)
    {
        var outside = atoms[lastInside.i + 1];
        var start = gold.Units[0].Anchor;
        var fullParts = atoms.SkipWhile(a => a.Alias != start).TakeWhile(a => a.Ordinal <= outside.Ordinal)
            .Select(a => new ReviewedPart(a.Alias, 0, a.Text.Length)).ToArray();
        var prediction = new PilotPrediction("synthetic-crossing", start, fullParts);
        crossing = Score([prediction]);
        Require(crossing.NotEvaluable == 1 && crossing.MembershipFP > 0 &&
            crossing.Boundaries[0].FullPrediction.SequenceEqual(fullParts) && crossing.Wrong == 0, "CROSSING_SCOPE_DRY_RUN_FAILED");
        crossingsExercised++;
    }
    var overlap = Score([oracle[0], oracle[0] with { Id = "synthetic-duplicate" }]);
    Require(overlap.DuplicateAnchors == 1 && overlap.OverlappingPairs == 1 && overlap.Boundaries.Count == 2, "OVERLAP_DRY_RUN_FAILED");
    foreach (var (scenario, result) in new (string, PilotScore?)[] { ("ORACLE", perfect), ("NO_PREDICTIONS", empty),
        ("FALSE_POSITIVE", fp), ("UNDEREXTENT", under), ("OVEREXTENT", over), ("CROSSING_SCOPE", crossing), ("DUPLICATE_OVERLAP", overlap) })
        scenarioSummaries.Add(new { document, scenario, exercised = result is not null, tp = result?.MembershipTP,
            fp = result?.MembershipFP, fn = result?.MembershipFN, exact = result?.Exact, wrong = result?.Wrong,
            notEvaluable = result?.NotEvaluable, contractInvalid = result?.ContractInvalid,
            notEvaluableReasons = result?.Boundaries.Where(b => b.Outcome == "NOT_EVALUABLE").Select(b => b.Reason).ToArray() });
    dryRuns.Add(new { document, oracle = perfect, noPredictions = empty, falsePositive = fp, underextent = under,
        overextent = over, crossingScope = crossing, crossingAvailability = crossing is null ? "NO_OUTSIDE_OCCURRENCE_IN_DOCUMENT" : "EXERCISED",
        duplicateOverlap = overlap, directF1LedgerChecks = gold.ReviewedRows.Count,
        directAnchorPositiveChecks = gold.ReviewedRows.Count(r => r.IsDistinctAnchor), metricsAreSynthetic = true });
}
Require(golds.Count == 5 && golds.Sum(g => g.Scope.Pages.Count) == 6 && golds.Sum(g => g.ReviewedRows.Count) == 213 &&
    golds.Sum(g => g.Units.Count) == 15 && golds.Sum(g => g.Units.Sum(u => u.Parts.Count)) == 20 &&
    summary.GetValueOrDefault("ESTABLISHES_STRUCTURE") == 20 && summary.GetValueOrDefault("REPRESENTS_STRUCTURE") == 22 &&
    summary.GetValueOrDefault("OTHER") == 171, "GOLD_CARDINALITY_DRIFT");
var goldManifest = Bytes(new { version = "P7_APPROVED_PILOT_GOLD_MANIFEST_V2", status = "USER_APPROVED_PILOT_GOLD_SERIALIZATION_VALIDATED",
    sourceZipSha256 = sourceZipHash, sourceScopeManifestSha256 = Hash(scopeBytes), proposalZipSha256 = proposalZipHash,
    proposalSha256 = Hash(proposalBytes), approvalSha256 = Hash(approvalBytes), scorerManifestSha256 = Hash(scorerPolicy),
    requestManifestSha256 = Hash(requestManifest), requestManifestStatus = "NOT_FROZEN", documents,
    evaluationPolicySha256 = Hash(policyBytes), representationAmendmentSha256 = Hash(amendmentBytes),
    supersedesRepresentationManifestSha256 = Hash(oldManifestBytes), previousGoldPreserved = true,
    reviewerRolesInGoldDecisionRows = false, roleTaxonomyRequired = false,
    summary, pending = 0, developmentOnly = true, claimsHeldOut = false, full647PageGoldApproved = false,
    providerCalls = 0, productionGoldModified = false, frozenRequestsModified = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" });
generated.Add("gold-manifest.v2.json", goldManifest);
var dryRun = Bytes(new { version = "P7_APPROVED_PILOT_SCORER_DRY_RUN_V1", goldManifestSha256 = Hash(goldManifest),
    scorerManifestSha256 = Hash(scorerPolicy), directMembershipRows = 213, oracleExactBoundaries = 15,
    oracleMembershipTP = 20, oracleMembershipFP = 0, oracleMembershipFN = 0, oracleNotEvaluable = 0,
    syntheticNotModelAccuracy = true, documents = dryRuns, providerCalls = 0 });
generated.Add("scorer-dry-run.v1.json", dryRun);
var outputIntegrity = Bytes(new { version = "P7_APPROVED_PILOT_ARTIFACT_INTEGRITY_V1",
    files = generated.Select(f => new { path = f.Key, bytes = f.Value.Length, sha256 = Hash(f.Value) }) });
generated.Add("artifact-integrity.v1.json", outputIntegrity);
// Validate every artifact before writing anything. CreateNew never overwrites an older checkpoint.
Directory.CreateDirectory(args[4]);
foreach (var f in generated) WriteNew(Path.Combine(args[4], f.Key), f.Value);
var publicReceipt = Bytes(new { version = "P7_D23_USER_APPROVED_GOLD_DRY_RUN_RECEIPT_V3", status = "ROLE_FREE_GOLD_PARITY_AND_SCORER_PASS_REQUEST_FREEZE_PENDING",
    sourceZipSha256 = sourceZipHash, proposalZipSha256 = proposalZipHash, proposalSha256 = Hash(proposalBytes),
    approvalSha256 = Hash(approvalBytes), goldManifestSha256 = Hash(goldManifest), scorerManifestSha256 = Hash(scorerPolicy),
    requestManifestSha256 = Hash(requestManifest), requestManifestStatus = "NOT_FROZEN", dryRunSha256 = Hash(dryRun),
    artifactIntegritySha256 = Hash(outputIntegrity), generatedFiles = generated.Count, documents, summary,
    evaluationPolicySha256 = Hash(policyBytes), representationAmendmentSha256 = Hash(amendmentBytes),
    previousGoldManifestSha256 = Hash(oldManifestBytes), previousGoldPreserved = true,
    roleFreeGoldScoringProjectionParityDocuments = golds.Count, scoringParityChecks,
    reviewerRolesInGoldDecisionRows = false, reviewerInterpretationsScored = false, roleTaxonomyRequired = false,
    sourceReviewRoleNoteValues = interpretationRoleNotes.Count,
    evaluationDocuments = 5, evaluationPages = 6, adjudicatedRows = 213, distinctAnchors = 15, headingParts = 20, pending = 0,
    closedReviewedBoundaries = 15, oracleExact = 15, oracleNotEvaluable = 0, oracleTP = 20, oracleFP = 0, oracleFN = 0,
    oracleMembershipTN = 193, syntheticScenarios = scenarioSummaries,
    syntheticCrossingsNotEvaluable = crossingsExercised, offsetConvention = P7ApprovedPilotGold.OffsetConvention,
    actualModelAccuracyScored = false, provenance = "ASSISTANT_PROPOSAL_USER_REVIEW_AND_APPROVAL_NOT_TWO_INDEPENDENT_REVIEWERS",
    interpretationRecordsUsedForGold = false, providerCalls = 0, existingGoldMutation = "NONE_NEW_PILOT_MANIFEST_ONLY",
    productionChanged = false, frozenRequestsChanged = false, exactTokenizerMapping = "BLOCKED", tokenBudgetMeasurement = "BLOCKED",
    d23 = "OPEN_REQUEST_FREEZE_PENDING", providerExecution = "LOCKED", productionPromotion = "LOCKED" });
WriteNew(args[5], publicReceipt);
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", goldManifestSha256 = Hash(goldManifest), receiptSha256 = Hash(publicReceipt),
    rows = 213, units = 15, oracleExact = 15, providerCalls = 0 }));

static byte[] Entry(ZipArchive zip, string name) { using var input = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static byte[] ReadPinned(string path, string hash) { var bytes = File.ReadAllBytes(path); Require(Hash(bytes) == hash, "SOURCE_HASH_DRIFT"); return bytes; }
static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
static void WriteNew(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
static string? AdjudicationNote(string document, string alias) => (document[4..12], alias) switch
{
    ("2b1a4e73", "L0000:S0") => "Tên chương trình dưới logo được xem là nhận diện trong policy heading hiện tại, không phải heading được chấm.",
    ("2b1a4e73", "L0001:S0") => "Title sự kiện được adjudicate như một unit độc lập theo policy bao gồm title/subtitle.",
    ("f427233d", "L0028:S0") => "Nhãn phân phối hành chính không thuộc phạm vi document heading của pilot hiện tại.",
    ("03c86fc3", "L0355:S0") or ("03c86fc3", "L0357:S0") => "Marker và caption được phê duyệt như một multipart unit; phân loại pháp lý không được chấm.",
    _ => null
};
static string? AlternativeNote(string document, string alias) => (document[4..12], alias) switch
{
    ("2b1a4e73", "L0000:S0") => "Có thể xét như supertitle nếu mở một policy title-block khác; Gold OTHER hiện tại không quyết định tác vụ mới.",
    ("f427233d", "L0028:S0") => "Có thể thuộc administrative region nếu tác vụ mới yêu cầu; cần policy và adjudication mới.",
    _ => null
};
