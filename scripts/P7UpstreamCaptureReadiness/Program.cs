using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

// Read-only inventory and capacity planning. No transport, credentials, Gold or PDF reparse.
if (args.Length != 4) throw new ArgumentException("Usage: P7UpstreamCaptureReadiness <frozen-F1-dir> <full-source-pack> <existing-diagnostic-capture-root> <new-public-receipt>");
if (File.Exists(args[3])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var bytes = Pinned(Path.Combine(args[0], "request-manifest.v1.json"), "2a5e4491270fd7ec92f536bfa8ac1e9812c8440f9fbb3e0a58a99d06e2965ae1");
using var manifest = JsonDocument.Parse(bytes); var m = manifest.RootElement;
var calls = m.GetProperty("requests").EnumerateArray().ToArray();
foreach (var call in calls) _ = Pinned(Path.Combine(args[0], call.GetProperty("providerBodyFile").GetString()!), call.GetProperty("providerBodySha256").GetString()!);
using var preflight = JsonDocument.Parse(Pinned("artifacts/web-pdf-semantic-diagnostic/controlled-replay.preflight.v1.json", "263b2fb10228bfbf8b4be493644b240b4f578d10484450d70c792e5f7710d066"));
using var f1 = JsonDocument.Parse(Pinned("artifacts/web-pdf-semantic-diagnostic/controlled-replay.f1.execution.v1.json", "0002facaedfc94ac2ca52b22430962b9f49d5cd656742fa4bb33024e69937fe0"));
using var g2a = JsonDocument.Parse(Pinned("artifacts/web-pdf-semantic-diagnostic/controlled-replay.g2a.execution.v1.json", "d8f79129e19aedf6f3d4ebd785c448ac188ae70814beac89440498a4685c43e6"));
var match = calls.Single(c => c.GetProperty("arm").GetString() == "CONTROL" && c.GetProperty("sourceSha256").GetString() == f1.RootElement.GetProperty("referenceSourceSha256").GetString());
var document = match.GetProperty("document").GetString()!;
var doc = m.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("document").GetString() == document);
Need(doc.GetProperty("sourceUniverseSha256").GetString() == preflight.RootElement.GetProperty("sourceAliasUniverseSha256").GetString(), "LEGACY_UNIVERSE_DRIFT");
using var snapshot = JsonDocument.Parse(Pinned(Path.Combine(args[1], document, "snapshot.json"), doc.GetProperty("snapshotSha256").GetString()!));
var atoms = snapshot.RootElement.GetProperty("atoms").Deserialize<SemanticSourceAtom[]>()!.ToDictionary(a => a.Alias);
using var map = JsonDocument.Parse(Pinned(Path.Combine(args[0], document, match.GetProperty("pack").GetString()!.Split(':')[^1] + ".issued-universe.json"), match.GetProperty("issuedUniverseSha256").GetString()!));
var issued = map.RootElement.EnumerateArray().Select(row => new V5IssuedOccurrenceV1(row.GetProperty("occurrence").GetString()!, atoms[row.GetProperty("alias").GetString()!])).ToArray();
var fc = Legacy("20261008-f1-01", f1.RootElement);
using var parsedF1 = JsonDocument.Parse(fc.Response);
var ledger = OccurrenceFunctionProtocolV1.Parse(parsedF1.RootElement, fc.Response.Length, 49152, issued);
Need(ledger.Decisions.Count == issued.Length && ledger.Decisions.Count == f1.RootElement.GetProperty("returnedLedgerCardinality").GetInt32(), "LEGACY_F1_NOT_TOTAL");
foreach (var decision in ledger.Decisions) {
    var row = f1.RootElement.GetProperty("decisions").EnumerateArray().Single(r => r.GetProperty("occurrence").GetString() == decision.OccurrenceId);
    var atom = issued.Single(i => i.Id == decision.OccurrenceId).Atom;
    Need(row.GetProperty("alias").GetString() == atom.Alias && row.GetProperty("ordinal").GetInt32() == atom.Ordinal &&
        row.GetProperty("function").GetString() == parsedF1.RootElement.GetProperty("decisions").EnumerateArray().Single(r => r.GetProperty("occurrence").GetString() == decision.OccurrenceId).GetProperty("function").GetString(), "LEGACY_PARSED_F1_DRIFT");
}
var idByAlias = issued.ToDictionary(o => o.Atom.Alias, o => o.Id);
var primaries = ledger.Decisions.Where(d => d.Function == OccurrenceFunction.EstablishesStructure).Select(d => d.OccurrenceId).ToArray();
var user = HeadingAnchorProtocolV1.ComposeUserMessage(issued.Select(o => o.Atom).ToArray(), idByAlias,
    primaries.Select(id => (id, issued.Single(o => o.Id == id).Atom)).ToArray());
var expectedG2 = new OpenRouterQwen37InferenceRequestComposer().Build(HeadingAnchorProtocolV1.SystemPrompt, user, 32768);
var gc = Legacy("20261008-g2a-01", g2a.RootElement);
Need(g2a.RootElement.GetProperty("upstreamF1ReceiptSha256").GetString() == "0002facaedfc94ac2ca52b22430962b9f49d5cd656742fa4bb33024e69937fe0" &&
    g2a.RootElement.GetProperty("upstreamF1ResponseSha256").GetString() == SpatialCanonical.Hash(fc.Response), "LEGACY_G2A_PARENT_DRIFT");
var has = HeadingAnchorProtocolV1.Parse(Encoding.UTF8.GetString(gc.Response), primaries);
using var gLedger = JsonDocument.Parse(gc.Response);
foreach (var decision in gLedger.RootElement.GetProperty("decisions").EnumerateArray()) {
    var id = decision.GetProperty("primary").GetString()!;
    var row = g2a.RootElement.GetProperty("decisions").EnumerateArray().Single(r => r.GetProperty("primary").GetString() == id);
    Need(row.GetProperty("anchor").GetString() == decision.GetProperty("anchor").GetString() && row.GetProperty("alias").GetString() == issued.Single(o => o.Id == id).Atom.Alias, "LEGACY_PARSED_G2A_DRIFT");
}
var f1Exact = match.GetProperty("providerBodySha256").GetString() == SpatialCanonical.Hash(fc.Body) &&
    match.GetProperty("systemPromptSha256").GetString() == f1.RootElement.GetProperty("systemPromptSha256").GetString() &&
    match.GetProperty("userMessageSha256").GetString() == f1.RootElement.GetProperty("userMessageSha256").GetString();
var g2Exact = gc.Body.SequenceEqual(expectedG2);
var rootBindings = calls.Select(c => new {
    document = c.GetProperty("document").GetString(), pack = c.GetProperty("pack").GetString(), stage = "F1",
    arm = c.GetProperty("arm").GetString() == "CONTROL" ? "Control" : "B", mode = "SharedF1Root", sourceSha256 = c.GetProperty("sourceSha256").GetString(),
    sourceUniverseSha256 = c.GetProperty("sourceUniverseSha256").GetString(), snapshotSha256 = c.GetProperty("snapshotSha256").GetString(),
    packSha256 = c.GetProperty("packSha256").GetString(), issuedUniverseSha256 = c.GetProperty("issuedUniverseSha256").GetString(),
    providerBodySha256 = c.GetProperty("providerBodySha256").GetString(), systemPromptSha256 = c.GetProperty("systemPromptSha256").GetString(),
    userMessageSha256 = c.GetProperty("userMessageSha256").GetString(), parentCaptureSha256 = Array.Empty<string>(),
    requiredOrigin = "PROVIDER_RAW", rawStatus = "NOT_CAPTURED_FOR_THIS_EXPERIMENT", authorization = "NOT_GRANTED" }).ToArray();
var sourceFiles = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7UpstreamCaptureReadiness.cs",
    "scripts/P7UpstreamCaptureReadiness/Program.cs", "scripts/P7UpstreamCaptureReadiness/P7UpstreamCaptureReadiness.csproj",
    "scripts/P7UpstreamCaptureReadiness/capture-receipt.schema.v1.json" }.Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }).ToArray();
var output = SpatialCanonical.Bytes(new {
    version = P7UpstreamCaptureReadiness.Version, status = "CONDITIONALLY_FROZEN_CAPTURE_AND_GENERATION_CONTRACTS",
    requestManifestSha256 = SpatialCanonical.Hash(bytes), sourceFiles, rootCaptureBindings = rootBindings,
    captureSchema = "scripts/P7UpstreamCaptureReadiness/capture-receipt.schema.v1.json", schemaStageNames = new[] { "F1", "G2A", "H2C" },
    requiredFiles = new[] { "provider-body.json", "response.txt", "response.sse", "observation.json", "raw-freeze.json", "parsed-stage-decision.json", "receipt.json", "issued-universe.json" },
    observationRequirement = "CONTENT_FINISH_REASON_RAW_SSE_RETRY_COUNT_AND_USAGE_IF_REPORTED_ALL_HASH_FROZEN",
    failedAttemptPolicy = "PRESERVE_FAILURE_RECEIPT_AND_AVAILABLE_RAW_BYTES_NEVER_DRIVE_DOWNSTREAM",
    missingPolicy = "MISSING_NOT_EMPTY_NO_DOWNSTREAM", invalidPolicy = "QUARANTINE_NO_RETRY_REPAIR_FALLBACK",
    emptyPolicy = "VALID_TOTAL_ALL_OTHER_OR_ALL_NO_SUPPRESSES_CALLS_NOT_INCOMPLETE_CAPTURE",
    captureOrder = "RESERVE_AUTHORIZED_ATTEMPT_CAPTURE_RAW_FREEZE_PARSE_FREEZE_LEDGER_GENERATE_FREEZE_DOWNSTREAM_SEPARATE_AUTHORIZATION",
    modes = new[] {
        new { mode = "ControlledDownstream", upstream = "FROZEN_CONTROL_F1_AND_CONTROL_G2A_FOR_BOTH_ARMS", metricNamespace = "CONTROLLED_STAGE_ISOLATED", claim = "NOT_END_TO_END" },
        new { mode = "NaturalEndToEnd", upstream = "OWN_ARM_F1_AND_OWN_ARM_G2A", metricNamespace = "NATURAL_END_TO_END", claim = "REQUEST_UNIVERSES_MAY_DIFFER_NO_SAME_UNIVERSE_CAUSAL_CLAIM" } },
    reuseInventory = new {
        searchScope = "TWO_EXISTING_P7_UPSTREAM_DIAGNOSTIC_RECEIPTS_AND_REFERENCED_PRIVATE_RAW_FILES_NOT_ALL_HISTORICAL_CORPORA",
        receiptsChecked = 2, document, universeHashMatches = true,
        f1 = new { exactBodySystemUserMatch = f1Exact, rawHashFilesVerified = 3, totalLedger = ledger.Decisions.Count, parsedSourceMappingVerified = true,
            receiptSha256 = "0002facaedfc94ac2ca52b22430962b9f49d5cd656742fa4bb33024e69937fe0", rawResponseSha256 = SpatialCanonical.Hash(fc.Response) },
        g2a = new { exactReconstructedBodyMatch = g2Exact, rawHashFilesVerified = 3, primaries = primaries.Length, has = has.Count, upstreamLineageVerified = true,
            receiptSha256 = "d8f79129e19aedf6f3d4ebd785c448ac188ae70814beac89440498a4685c43e6", rawResponseSha256 = SpatialCanonical.Hash(gc.Response) },
        status = f1Exact && g2Exact ? "PROTOCOL_COMPATIBLE_REUSE_CANDIDATES_NOT_ADOPTED" : "REQUEST_DRIFT_REJECTED",
        sourceReviewAlreadyOpenInHistoricalCapture = true, experimentFreshnessEquivalent = false,
        originalExperimentEndpointSnapshotPinAvailable = false, reusePolicyAmendmentApproved = false,
        fullCurrentQualificationEligibility = false, remainingRootCallsWithoutCompatibleObservedCapture = 13,
        newCallsAuthorized = 0, historicalPromptTokensObservedForOneMatchingF1 = 5410,
        historicalUsageDoesNotMeasureAllFrozenTreatmentRequests = true },
    sizing = new { f1Bodies = calls.Length, controlProviderUtf8Bytes = m.GetProperty("sizing").GetProperty("controlProviderUtf8Bytes").GetInt64(),
        bProviderUtf8Bytes = m.GetProperty("sizing").GetProperty("treatmentProviderUtf8Bytes").GetInt64(),
        eachCall = calls.Select(c => new { call = c.GetProperty("callHandle").GetString(), bytes = c.GetProperty("providerBodyBytes").GetInt32() }),
        downstreamPayloadBytes = (long?)null, downstreamSizingStatus = "WAITING_REAL_UPSTREAM_NOT_FABRICATED_WITH_GOLD_OR_SYNTHETIC_LEDGER",
        exactTokens = (long?)null, bytesAreTokenUpperBound = false },
    callBounds = new { oneMode = P7UpstreamCaptureReadiness.Bounds(7, 643, false), bothModesSharingOnlyF1 = P7UpstreamCaptureReadiness.Bounds(7, 643, true),
        basis = "ALL_643_ISSUED_OWNED_OCCURRENCES_MAY_BE_ESTABLISHES_AND_HAS_NOT_GOLD_15_UNITS", boundsAreAuthorized = false },
    budget = new { exactInputTokens = (long?)null, monetaryBudget = (decimal?)null, approvedSpend = (decimal?)null,
        status = "NOT_ESTABLISHED_SEPARATE_CALIBRATION_AND_SPEND_AUTHORIZATION_REQUIRED", noInferenceToMeasureUsage = true },
    downstreamActualRequestBytesFrozen = false, downstreamGenerationContracts = "CONDITIONALLY_FROZEN_REAL_CAPTURE_DEPENDENT",
    goldScorerUnchanged = true, existingRequestBodiesChanged = false, productionChanged = false,
    providerCalls = 0, authorizedProviderCalls = 0, retry = 0, repair = false, fallback = false,
    d23 = "OPEN_EXECUTION_DEPENDENT_BODIES_AND_BUDGET_PENDING", exactTokenizerMapping = "BLOCKED", providerUsageMeasurement = "BLOCKED_FOR_PILOT_TREATMENTS",
    providerExecution = "LOCKED", productionPromotion = "LOCKED" });
using (var f = new FileStream(args[3], FileMode.CreateNew)) { f.Write(output); f.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { status = "CONDITIONALLY_FROZEN", roots = 14, protocolCompatibleLegacyCaptures = f1Exact && g2Exact ? 2 : 0,
    receiptSha256 = SpatialCanonical.Hash(output), providerCalls = 0 }));

(byte[] Body, byte[] Response) Legacy(string folder, JsonElement receipt)
{
    Need(receipt.GetProperty("finishReason").GetString() == "stop" && receipt.GetProperty("parserAccepted").GetBoolean() &&
        receipt.GetProperty("httpAttempts").GetInt32() == 1 && receipt.GetProperty("retryCount").GetInt32() == 0 &&
        !receipt.GetProperty("repair").GetBoolean() && !receipt.GetProperty("fallback").GetBoolean(), "LEGACY_EXECUTION_POLICY_INVALID");
    var dir = Path.Combine(args[2], folder);
    using var freeze = JsonDocument.Parse(Pinned(Path.Combine(dir, "raw-freeze.json"), receipt.GetProperty("rawFreezeSha256").GetString()!));
    Need(freeze.RootElement.GetProperty("status").GetString() == "RAW_FROZEN_BEFORE_PARSE", "LEGACY_FREEZE_MISSING");
    var files = receipt.GetProperty("captureHashes").EnumerateArray().ToArray();
    Need(files.Length == 3 && files.Select(f => f.GetProperty("file").GetString()).Distinct().Count() == 3, "LEGACY_RAW_FILE_SET_INVALID");
    foreach (var item in files) {
        var name = item.GetProperty("file").GetString()!; Need(name is "provider-body.json" or "response.txt" or "response.sse", "LEGACY_FILE_INVALID");
        _ = Pinned(Path.Combine(dir, name), item.GetProperty("sha256").GetString()!);
        var frozenHashes = freeze.RootElement.TryGetProperty("hashes", out var oldHashes) ? oldHashes : freeze.RootElement.GetProperty("captureHashes");
        Need(frozenHashes.EnumerateArray().Any(h => h.GetProperty("file").GetString() == name &&
            h.GetProperty("sha256").GetString() == item.GetProperty("sha256").GetString()), "LEGACY_FREEZE_HASH_DRIFT");
    }
    var body = Pinned(Path.Combine(dir, "provider-body.json"), receipt.GetProperty("providerBodySha256").GetString()!);
    var response = File.ReadAllBytes(Path.Combine(dir, "response.txt")); var sse = File.ReadAllBytes(Path.Combine(dir, "response.sse"));
    using var bj = JsonDocument.Parse(body); var messages = bj.RootElement.GetProperty("messages");
    Need(HashText(messages[0].GetProperty("content").GetString()!) == receipt.GetProperty("systemPromptSha256").GetString() &&
        HashText(messages[1].GetProperty("content").GetString()!) == receipt.GetProperty("userMessageSha256").GetString(), "LEGACY_MESSAGE_HASH_DRIFT");
    var observationBytes = File.ReadAllBytes(Path.Combine(dir, "observation.json")); using var observation = JsonDocument.Parse(observationBytes);
    Need(HashText(observation.RootElement.GetProperty("Content").GetString()!) == SpatialCanonical.Hash(response) &&
        HashText(observation.RootElement.GetProperty("RawSse").GetString()!) == SpatialCanonical.Hash(sse) &&
        observation.RootElement.GetProperty("FinishReason").GetString() == "stop" && observation.RootElement.GetProperty("RetryCount").GetInt32() == 0, "LEGACY_OBSERVATION_DRIFT");
    return (body, response);
}
static string HashText(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
static byte[] Pinned(string path, string hash) { var data = File.ReadAllBytes(path); Need(SpatialCanonical.Hash(data) == hash, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return data; }
static void Need(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
