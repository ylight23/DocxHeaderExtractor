using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

// Provider-free source-only request generation. Never opens Gold decisions, sidecars or captures.
if (args.Length != 4) throw new ArgumentException("Usage: P7PilotRequestFreeze <full-source-pack> <source-only-pilot-pack> <new-private-request-dir> <new-public-manifest>");
if (Directory.Exists(args[2]) || File.Exists(args[3])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var sourceBytes = Pinned(Path.Combine(args[0], "source-manifest.json"), "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30");
var pilotBytes = Pinned(Path.Combine(args[1], "source-manifest.json"), "23c1a49e7a1015a3c5d19c3651fae08fd4207853d122d652da5834e65a0f55d2");
// Hash-only links to evaluation authority; no annotations are loaded by request generation.
var qualificationBytes = Pinned("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.gold-v2-freeze-qualification.v1.json",
    "e5076f39fb3508b22f965c6c650c9df279802b70756f8c6dc52142b2f41098d9");
using var qualification = JsonDocument.Parse(qualificationBytes);
using var sourceManifest = JsonDocument.Parse(sourceBytes); using var pilotManifest = JsonDocument.Parse(pilotBytes);
var composer = new OpenRouterQwen37InferenceRequestComposer();
var sourceFiles = new[] {
    "src/DocxHeaderExtractor.V5Qualification/P7/P7PilotRequestPreflight.cs",
    "src/DocxHeaderExtractor.V5Qualification/P7/PdfInterpretationProtocol.cs",
    "src/DocxHeaderExtractor.V5Qualification/P7/PdfSourceEvidenceStore.cs",
    "src/DocxHeaderExtractor.V5Qualification/P7/PdfSpatialEvidenceLedgerBuilder.cs",
    "src/DocxHeaderExtractor.DocumentProcessing/Semantics/Canonical/SemanticEvidencePackingPolicy.cs",
    "src/DocxHeaderExtractor.Core/Models/Inference/OccurrenceFunctionProtocolV1.cs",
    "src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority/Protocols/HeadingAnchorProtocolV1.cs",
    "src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority/Protocols/HeadingExtentProtocolV2.cs",
    "src/DocxHeaderExtractor.Infrastructure/AI/OpenRouterQwen37InferenceRequestComposer.cs",
    "src/DocxHeaderExtractor.Infrastructure/AI/QualifiedInference/OpenRouterQwen37JsonObjectCarrierV2_1.cs",
    "scripts/P7PilotRequestFreeze/Program.cs", "scripts/P7PilotRequestFreeze/P7PilotRequestFreeze.csproj"
}.Select(path => new { path, sha256 = HashFile(path) }).ToArray();
var recipeBytes = Bytes(new { version = P7PilotRequestPreflight.Version, policy = P7PilotRequestPreflight.UpstreamPolicy,
    selection = P7PilotRequestPreflight.PackSelection, sourceFiles,
    g2a = "STRICT_TOTAL_CONTROL_F1_LEDGER_ESTABLISHES_ONLY_ALL_ISSUED_OR_EMPTY_NO_CALL",
    h2c = "STRICT_TOTAL_CONTROL_G2A_LEDGER_HAS_ONLY_ALL_ISSUED_ANCHORS_NO_GOLD_SELECTION",
    order = "CAPTURE_RAW_FREEZE_VALIDATE_UPSTREAM_THEN_GENERATE_AND_FREEZE_DOWNSTREAM_BEFORE_SEPARATE_AUTHORIZATION",
    emptyUpstream = "NO_DOWNSTREAM_CALLS_NOT_A_MISSING_LEDGER", invalidUpstream = "FAIL_CLOSED_NO_REPAIR_FALLBACK",
    downstreamFrozenNow = false, providerAuthorization = "NOT_GRANTED", stageExperiment = "STAGE_ISOLATED_NOT_END_TO_END_TREATMENT_CHAIN" });
Directory.CreateDirectory(args[2]); Write(Path.Combine(args[2], "downstream-generation-recipe.v1.json"), recipeBytes);
var docs = new List<object>(); var calls = new List<object>(); var reviewedAtoms = 0; var packsTotal = 0; var ownedTotal = 0;
long controlBytesTotal = 0, treatmentBytesTotal = 0;
foreach (var selected in pilotManifest.RootElement.GetProperty("documents").EnumerateArray())
{
    var id = selected.GetProperty("document").GetString()!;
    var src = sourceManifest.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == id);
    var hash = src.GetProperty("sha256").GetString()!;
    var pages = selected.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("page").GetInt32()).ToArray();
    var snapshotBytes = Pinned(Path.Combine(args[0], id, "snapshot.json"), src.GetProperty("snapshotSha256").GetString()!);
    var storeBytes = Pinned(Path.Combine(args[0], id, "evidence-store.json"), src.GetProperty("storeSha256").GetString()!);
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var pdf = Path.Combine(args[0], id, "source.pdf"); Require(HashFile(pdf) == hash, "SOURCE_PDF_DRIFT");
    var parsed = PdfSourceAdapter.BuildWithDetails(pdf); var source = parsed.Snapshot;
    Require(source.SourceSha256 == hash && source.SourceAliasUniverseHash == src.GetProperty("sourceUniverseSha256").GetString() &&
        source.ModelVisibleEvidenceHash == src.GetProperty("modelEvidenceSha256").GetString(), "REPARSE_UNIVERSE_DRIFT");
    Require(JsonElement.DeepEquals(snapshot.RootElement.GetProperty("atoms"), SpatialCanonical.Element(source.Atoms)) &&
        JsonElement.DeepEquals(snapshot.RootElement.GetProperty("evidence"), SpatialCanonical.Element(source.Evidence)), "REPARSE_SOURCE_OR_EVIDENCE_DRIFT");
    var store = PdfSourceEvidenceStore.Build(source, parsed.Details);
    Require(store.CanonicalBytes().SequenceEqual(storeBytes), "REPARSE_STORE_DRIFT");
    var packs = P7PilotRequestPreflight.SelectPacks(source, parsed.Details, pages);
    var inScope = source.Atoms.Where(a => pages.Contains(a.Page)).Select(a => a.Alias).ToArray();
    var ownedAliases = packs.SelectMany(p => p.Owned).Select(e => e.SourceAlias).ToArray();
    Require(ownedAliases.Distinct().Count() == ownedAliases.Length && inScope.All(ownedAliases.Contains), "SELECTED_PAGE_COVERAGE_MISSING_OR_DUPLICATE");
    var docDirectory = Path.Combine(args[2], id); Directory.CreateDirectory(docDirectory);
    Write(Path.Combine(docDirectory, "source-evidence-store.json"), storeBytes);
    var packRows = new List<object>();
    foreach (var pack in packs)
    {
        var request = P7PilotRequestPreflight.F1(source, parsed.Details, pack);
        P7PilotRequestPreflight.ValidateProjection(request);
        var packShort = pack.PackId.Split(':')[^1];
        var issued = request.Owned.Select(o => new { occurrence = o.Id, alias = o.Atom.Alias, ordinal = o.Atom.Ordinal, page = o.Atom.Page }).ToArray();
        var issuedBytes = Bytes(issued); var packHash = Hash(SpatialCanonical.Bytes(pack));
        Write(Path.Combine(docDirectory, packShort + ".issued-universe.json"), issuedBytes);
        var control = composer.Build(request.ControlSystemPrompt, request.ControlUserMessage, PdfInferenceWireContract.CompletionTokenCeiling);
        var treatment = composer.Build(request.SystemPrompt, request.UserMessage, PdfInferenceWireContract.CompletionTokenCeiling);
        using var controlJson = JsonDocument.Parse(control); using var treatmentJson = JsonDocument.Parse(treatment);
        // The provider carrier must be identical outside its two message contents.
        foreach (var field in controlJson.RootElement.EnumerateObject().Where(p => p.Name != "messages"))
            Require(JsonElement.DeepEquals(field.Value, treatmentJson.RootElement.GetProperty(field.Name)), "TREATMENT_TRANSPORT_DRIFT");
        Require(controlJson.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(treatmentJson.RootElement.EnumerateObject().Select(p => p.Name)), "TREATMENT_CARRIER_KEYS_DRIFT");
        foreach (var arm in new[] { "CONTROL", "B" })
        {
            var body = arm == "CONTROL" ? control : treatment;
            var prompt = arm == "CONTROL" ? request.ControlSystemPrompt : request.SystemPrompt;
            var user = arm == "CONTROL" ? request.ControlUserMessage : request.UserMessage;
            var prefix = packShort + ".F1." + arm;
            Write(Path.Combine(docDirectory, prefix + ".provider-body.json"), body);
            Write(Path.Combine(docDirectory, prefix + ".system.txt"), Encoding.UTF8.GetBytes(prompt));
            Write(Path.Combine(docDirectory, prefix + ".user.json"), Encoding.UTF8.GetBytes(user));
            calls.Add(new { document = id, pack = pack.PackId, stage = "F1", arm, callHandle = id + "|" + prefix,
                sourceSha256 = hash, sourceUniverseSha256 = source.SourceAliasUniverseHash, snapshotSha256 = Hash(snapshotBytes),
                packSha256 = packHash, issuedUniverseSha256 = Hash(issuedBytes), decisionSubjects = request.DecisionSubjects,
                protocolVersion = arm == "CONTROL" ? OccurrenceFunctionProtocolV1.Version : PdfInterpretationProtocol.Version,
                systemPromptSha256 = HashText(prompt), userMessageSha256 = HashText(user), providerBodySha256 = Hash(body),
                providerBodyBytes = body.Length, systemUtf8Bytes = Encoding.UTF8.GetByteCount(prompt), userUtf8Bytes = Encoding.UTF8.GetByteCount(user),
                providerBodyFile = id + "/" + prefix + ".provider-body.json", status = "BYTES_FROZEN_NOT_AUTHORIZED",
                inScopeOwned = request.Owned.Count(o => pages.Contains(o.Atom.Page)), outsideScopeOwned = request.Owned.Count(o => !pages.Contains(o.Atom.Page)) });
        }
        controlBytesTotal += control.Length; treatmentBytesTotal += treatment.Length;
        packRows.Add(new { packId = pack.PackId, packSha256 = packHash, issuedUniverseSha256 = Hash(issuedBytes), issued,
            ownedCount = pack.Owned.Count, haloCount = pack.Visible.Count - pack.Owned.Count,
            g2a = "WAITING_FROZEN_CONTROL_F1_LEDGER", h2c = "WAITING_FROZEN_CONTROL_G2A_LEDGER",
            expectedDownstreamCallCount = (int?)null, downstreamRecipeSha256 = Hash(recipeBytes),
            bodyDeltaUtf8Bytes = treatment.Length - control.Length, treatmentProjectionValidated = true });
    }
    reviewedAtoms += inScope.Length; ownedTotal += ownedAliases.Length; packsTotal += packs.Count;
    docs.Add(new { document = id, sourceSha256 = hash, snapshotSha256 = Hash(snapshotBytes), storeSha256 = store.StoreSha256,
        sourceUniverseSha256 = source.SourceAliasUniverseHash, evaluationPages = pages, inScopeOccurrences = inScope.Length,
        fullSourceOccurrences = source.Atoms.Count, totalIssuedOwnedOccurrences = ownedAliases.Length,
        packs = packRows, outOfScopeAtomsRemainUnscored = true, sourceOnlySelection = true });
    Console.WriteLine(JsonSerializer.Serialize(new { document = id, selectedPacks = packs.Count, inScope = inScope.Length, owned = ownedAliases.Length }));
}
Require(docs.Count == 5 && reviewedAtoms == 213 && calls.Count == packsTotal * 2, "PILOT_SELECTION_DRIFT");
var result = Bytes(new { version = "P7_D23_PILOT_F1_REQUEST_FREEZE_V1", status = "F1_CONTROL_B_BYTES_FROZEN_DOWNSTREAM_WAITING_UPSTREAM",
    sourceManifestSha256 = Hash(sourceBytes), pilotSourceManifestSha256 = Hash(pilotBytes),
    goldScorerQualificationReceiptSha256 = Hash(qualificationBytes),
    goldManifestSha256 = qualification.RootElement.GetProperty("goldManifestSha256").GetString(),
    scorerManifestSha256 = qualification.RootElement.GetProperty("scorerManifestSha256").GetString(),
    policySha256 = qualification.RootElement.GetProperty("policySha256").GetString(),
    downstreamRecipeSha256 = Hash(recipeBytes), generationSourceFiles = sourceFiles,
    arms = new[] { "CONTROL", "B" }, treatmentProtocolVersion = PdfInterpretationProtocol.Version,
    baseline = "CURRENT_PRODUCTION_F1_COMPOSER_AND_PROVIDER_CARRIER_NO_HISTORICAL_RESPONSE_REUSE",
    controlHistoricalCaptureParityClaimed = false, packSelection = P7PilotRequestPreflight.PackSelection,
    stageIsolation = P7PilotRequestPreflight.UpstreamPolicy,
    treatmentDelta = "EXISTING_B_V2_RAW_PARSER_PROJECTION_PLUS_REFERENCES_AND_SHORT_INTERPRETATION_ENVELOPE",
    causalClaim = "COMPOSITE_B_EFFECT_ONLY_NOT_REFERENCE_ONLY_OR_GEOMETRY_ONLY",
    pageContextTreatments = "NOT_INCLUDED_B_LOCAL_PAGE_ADJACENT_NOT_AUTOMATICALLY_AUTHORIZED",
    sourceDocuments = docs.Count, evaluationPages = 6, inScopeOccurrences = reviewedAtoms, issuedOwnedOccurrences = ownedTotal,
    selectedPacks = packsTotal, f1FrozenRequests = calls.Count, g2aFrozenRequests = 0, h2cFrozenRequests = 0,
    downstreamRequestCountKnown = false, documents = docs, requests = calls,
    sizing = new { controlProviderUtf8Bytes = controlBytesTotal, treatmentProviderUtf8Bytes = treatmentBytesTotal,
        deltaUtf8Bytes = treatmentBytesTotal - controlBytesTotal, exactTokens = (int?)null, bytesAreTokenUpperBound = false },
    completionTokenCeiling = PdfInferenceWireContract.CompletionTokenCeiling, controlResponseUtf8Cap = PdfInferenceWireContract.ResponseUtf8ByteCap,
    treatmentResponseUtf8Cap = PdfInterpretationProtocol.ResponseUtf8ByteCap,
    downstreamRecipeFrozen = true, downstreamRequestBytesFrozen = false,
    f1StageReadyForSeparateAuthorizationReview = true, allStagesReadyForAuthorization = false,
    d23 = "OPEN_DOWNSTREAM_REQUEST_FREEZE_PENDING", developmentPilotNotGeneralization = true,
    noGoldDecisionsOrSidecarsLoaded = true, noProviderResponsesLoaded = true, goldOrReviewerFieldsProjected = false,
    goldMutation = "NONE", productionChanged = false, existingFrozenRequestsChanged = false,
    providerCalls = 0, authorizedProviderCalls = 0, retry = 0, repair = false, fallback = false,
    exactTokenizerMapping = "BLOCKED", providerUsageMeasurement = "BLOCKED", providerExecution = "LOCKED", productionPromotion = "LOCKED" });
Write(Path.Combine(args[2], "request-manifest.v1.json"), result); Write(args[3], result);
Console.WriteLine(JsonSerializer.Serialize(new { status = "F1_FROZEN_DOWNSTREAM_PENDING", requests = calls.Count, manifestSha256 = Hash(result), providerCalls = 0 }));
static string Hash(byte[] bytes) => SpatialCanonical.Hash(bytes);
static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)); }
static byte[] Bytes<T>(T value) => SpatialCanonical.Bytes(value);
static byte[] Pinned(string path, string expected) { var bytes = File.ReadAllBytes(path); Require(Hash(bytes) == expected, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return bytes; }
static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
