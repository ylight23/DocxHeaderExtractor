using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

// Offline preparation only. No transport, credentials, HTTP client, execution or authorization flag.
if (args.Length != 5) throw new ArgumentException("Usage: P7InterpretationPreflight <repo> <reference-pdf> <private-control-root> <new-private-request-directory> <new-sanitized-manifest>");
var repo = Path.GetFullPath(args[0]);
var pdf = Path.GetFullPath(args[1]);
var privateRoot = Path.GetFullPath(args[2]);
var requestDirectory = Path.GetFullPath(args[3]);
var manifestPath = Path.GetFullPath(args[4]);
if (Directory.Exists(requestDirectory) || File.Exists(manifestPath)) throw new InvalidOperationException("interpretation-preflight-output-exists");
const string SourceHash = "f427233dcdcd8fc9724c4133c6ef5082bc5105474d4f074442b2c63f76922318";
static void Require(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }
static string HashText(string value) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(value));
static JsonDocument Pinned(string path, string hash)
{
    var bytes = File.ReadAllBytes(path);
    Require(SpatialCanonical.Hash(bytes) == hash, "interpretation-control-file-hash-mismatch:" + Path.GetFileName(path));
    return JsonDocument.Parse(bytes);
}
var receipts = Path.Combine(repo, "artifacts/web-pdf-semantic-diagnostic");
using var f1Receipt = Pinned(Path.Combine(receipts, "controlled-replay.f1.execution.v1.json"), "0002facaedfc94ac2ca52b22430962b9f49d5cd656742fa4bb33024e69937fe0");
using var anchorReceipt = Pinned(Path.Combine(receipts, "controlled-replay.g2a.execution.v1.json"), "d8f79129e19aedf6f3d4ebd785c448ac188ae70814beac89440498a4685c43e6");
using var extentReceipt = Pinned(Path.Combine(receipts, "controlled-replay.h2c.execution.v1.json"), "cd38b46ec43bfea83311366d87e81e717a37335b863f1459ec6d4b00965c4b32");
Require(SpatialCanonical.Hash(File.ReadAllBytes(pdf)) == SourceHash, "interpretation-reference-pdf-mismatch");
var parsed = PdfSourceAdapter.BuildWithDetails(pdf);
var source = parsed.Snapshot;
var owned = source.Atoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
Require(owned.Length == 79 && source.SourceAliasUniverseHash == "a7960a793b2c42c1f87c543423fefa07c368e865da2bf25180c713878d6267cb", "interpretation-reference-universe-mismatch");
var controls = new List<(InterpretationRequest Request, JsonElement Receipt, string Directory)>();
var f1 = PdfInterpretationProtocol.Compose(InterpretationStage.F1, source, parsed.Details, owned,
    correspondences: PdfReadOnlyCorrespondenceBuilder.Build(owned, source.Atoms));
string Raw(JsonElement receipt, string directory)
{
    Require(receipt.GetProperty("finishReason").GetString() == "stop" && receipt.GetProperty("parserAccepted").GetBoolean(), "interpretation-invalid-control-capture");
    using var freeze = Pinned(Path.Combine(directory, "raw-freeze.json"), receipt.GetProperty("rawFreezeSha256").GetString()!);
    Require(freeze.RootElement.GetProperty("status").GetString() == "RAW_FROZEN_BEFORE_PARSE", "interpretation-control-not-frozen");
    var frozenHashes = freeze.RootElement.TryGetProperty("hashes", out var hashes) ? hashes : freeze.RootElement.GetProperty("captureHashes");
    var entries = receipt.GetProperty("captureHashes").EnumerateArray().ToArray();
    Require(entries.Length == 3 && entries.Select(entry => entry.GetProperty("file").GetString()).Distinct().Count() == 3, "interpretation-capture-files-invalid");
    foreach (var item in entries)
    {
        var name = item.GetProperty("file").GetString()!;
        Require(name is "provider-body.json" or "response.txt" or "response.sse", "interpretation-control-file-invalid");
        Require(SpatialCanonical.Hash(File.ReadAllBytes(Path.Combine(directory, name))) == item.GetProperty("sha256").GetString() &&
            frozenHashes.EnumerateArray().Any(value => value.GetProperty("file").GetString() == name && value.GetProperty("sha256").GetString() == item.GetProperty("sha256").GetString()), "interpretation-control-hash-mismatch");
    }
    return File.ReadAllText(Path.Combine(directory, "response.txt"));
}
var f1Directory = Path.Combine(privateRoot, "20261008-f1-01");
var f1Raw = Raw(f1Receipt.RootElement, f1Directory);
using var f1Json = JsonDocument.Parse(f1Raw);
var functions = OccurrenceFunctionProtocolV1.Parse(f1Json.RootElement, Encoding.UTF8.GetByteCount(f1Raw), 49_152, f1.Owned);
var primaries = functions.Decisions.Where(value => value.Function == OccurrenceFunction.EstablishesStructure).Select(value => value.OccurrenceId).ToArray();
var g2a = PdfInterpretationProtocol.Compose(InterpretationStage.G2A, source, parsed.Details, owned, primaryIds: primaries);
var anchorDirectory = Path.Combine(privateRoot, "20261008-g2a-01");
var g2aRaw = Raw(anchorReceipt.RootElement, anchorDirectory);
var has = HeadingAnchorProtocolV1.Parse(g2aRaw, primaries);
controls.Add((f1, f1Receipt.RootElement, f1Directory));
controls.Add((g2a, anchorReceipt.RootElement, anchorDirectory));
var extentDirectory = Path.Combine(privateRoot, "20261008-h2c-01");
using var setFreeze = Pinned(Path.Combine(extentDirectory, "capture-set-freeze.json"), extentReceipt.RootElement.GetProperty("captureSetSha256").GetString()!);
Require(setFreeze.RootElement.GetProperty("status").GetString() == "COMPLETE_RAW_CAPTURE_SET", "interpretation-incomplete-control-set");
var extentRows = extentReceipt.RootElement.GetProperty("rows").EnumerateArray().ToArray();
Require(extentRows.Length == has.Count && extentRows.Select(row => row.GetProperty("anchor").GetString()!).ToHashSet().SetEquals(has), "interpretation-upstream-cohort-mismatch");
foreach (var row in extentRows.OrderBy(row => row.GetProperty("ordinal").GetInt32()))
{
    var id = row.GetProperty("anchor").GetString()!;
    Require(setFreeze.RootElement.GetProperty("rawFreezeHashes").EnumerateArray().Any(item => item.GetProperty("anchor").GetString() == id && item.GetProperty("rawFreezeSha256").GetString() == row.GetProperty("rawFreezeSha256").GetString()), "interpretation-control-set-lineage-mismatch");
    var directory = Path.Combine(extentDirectory, id);
    Raw(row, directory);
    controls.Add((PdfInterpretationProtocol.Compose(InterpretationStage.H2C, source, parsed.Details, owned, anchor: id), row, directory));
}

var composer = new OpenRouterQwen37InferenceRequestComposer();
var prepared = controls.Select(control =>
{
    var request = control.Request;
    using var original = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(control.Directory, "provider-body.json")));
    var body = composer.Build(request.ControlSystemPrompt, request.ControlUserMessage, 32_768);
    Require(SpatialCanonical.Hash(body) == control.Receipt.GetProperty("providerBodySha256").GetString(), "interpretation-control-provider-body-parity-failed");
    Require(HashText(request.ControlUserMessage) == control.Receipt.GetProperty("userMessageSha256").GetString() &&
        HashText(request.ControlSystemPrompt) == control.Receipt.GetProperty("systemPromptSha256").GetString(), "interpretation-control-message-parity-failed");
    var treatment = composer.Build(request.SystemPrompt, request.UserMessage, 32_768);
    var name = PdfInterpretationProtocol.Name(request.Stage).Replace("-", "");
    if (request.Stage == InterpretationStage.H2C) name += "_" + request.DecisionSubjects.Single();
    return new { name, request, treatment, receipt = control.Receipt };
}).ToArray();
Require(prepared.Length == 8, "interpretation-preflight-cohort-size-invalid");

Directory.CreateDirectory(requestDirectory);
void WriteNew(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew); stream.Write(bytes); stream.Flush(true); }
foreach (var call in prepared)
{
    WriteNew(Path.Combine(requestDirectory, call.name + ".provider-body.json"), call.treatment);
    WriteNew(Path.Combine(requestDirectory, call.name + ".system.txt"), Encoding.UTF8.GetBytes(call.request.SystemPrompt));
    WriteNew(Path.Combine(requestDirectory, call.name + ".user.json"), Encoding.UTF8.GetBytes(call.request.UserMessage));
}
WriteNew(Path.Combine(requestDirectory, "source-evidence-store.json"), f1.EvidenceStore.CanonicalBytes());
var report = new
{
    schemaVersion = "p7-b-interpretation-provider-free-preflight-1", protocolVersion = PdfInterpretationProtocol.Version,
    status = "ENGINEERING_PREFLIGHT_FROZEN_NOT_AUTHORIZED_NOT_MULTI_DOCUMENT_QUALIFICATION",
    sourceSha256 = source.SourceSha256, source.SourceAliasUniverseHash, source.ModelVisibleEvidenceHash,
    evidenceStoreSha256 = f1.EvidenceStore.StoreSha256, sourceOccurrences = owned.Length,
    cohortSelection = "FULL_F1_SOURCE_UNIVERSE_THEN_FROZEN_CONTROL_F1_ESTABLISHES_THEN_FROZEN_CONTROL_G2A_HAS_NO_GOLD_FILTER",
    stageIsolation = "G2A_AND_H2C_USE_FROZEN_CONTROL_UPSTREAM_NOT_NEW_TREATMENT_OUTPUTS",
    controlProviderBodyParity = "8_OF_8_BYTE_EQUIVALENT", sourceReviewAlreadyOpen = true,
    rawParserFields = new[] { "text", "page", "bbox", "typography", "readingOrder", "glyphs" },
    glyphAvailability = "NOT_AVAILABLE_NOT_PROJECTED", predefinedSpatialRelationsIssued = false,
    plannedRequests = prepared.Select(call => new
    {
        call.name, stage = PdfInterpretationProtocol.Name(call.request.Stage), decisionSubjects = call.request.DecisionSubjects,
        visibleOccurrenceCount = call.request.VisibleAliasByOccurrence.Count, call.request.SystemPromptSha256, call.request.UserMessageSha256,
        providerBodySha256 = SpatialCanonical.Hash(call.treatment), providerBodyBytes = call.treatment.Length,
        controlProviderBodySha256 = call.receipt.GetProperty("providerBodySha256").GetString(),
        controlResponseSha256 = call.receipt.GetProperty("captureHashes").EnumerateArray().Single(value => value.GetProperty("file").GetString() == "response.txt").GetProperty("sha256").GetString()
    }).ToArray(),
    completionTokenCeiling = 32_768, responseUtf8ByteCap = PdfInterpretationProtocol.ResponseUtf8ByteCap,
    interpretationCharacterCap = PdfInterpretationProtocol.InterpretationCharacterCap,
    treatmentDelta = new[] { "RAW_PARSER_FIELD_PROJECTION", "REFERENCES_AND_OPTIONAL_COPIED_ASSERTIONS", "SHORT_INTERPRETATION_RECORD", "VERSIONED_RESPONSE_ENVELOPE_AND_OUTPUT_INSTRUCTIONS", "QUALIFICATION_RESPONSE_BYTE_CAP" },
    causalClaim = "NONE_B_IS_A_COMPOSITE_TREATMENT_REFERENCE_ONLY_EFFECT_REQUIRES_B0_B1_SEPARATE_EXPERIMENT",
    evaluation = new
    {
        membership = "SOURCE_REVIEW_PER_STAGE_SEPARATE_FROM_EXACT_BOUNDARY", exactBoundary = "REQUIRES_TITLE_SUBTITLE_ADJUDICATION_BEFORE_SCORING",
        crossAnchorOverlap = "OBSERVATION_ONLY_NO_PRUNING", references = "UNKNOWN_MISBOUND_UNAVAILABLE_OR_ASSERTION_MISMATCH_RATE",
        interpretations = "INDEPENDENT_UNSUPPORTED_INTERPRETATION_REVIEW_NOT_VERIFIER_CERTIFICATION",
        costs = "INPUT_OUTPUT_REASONING_TOKENS_LATENCY_CALLS_BYTES", generalization = "MULTI_DOCUMENT_CORPUS_AND_SCORING_AUTHORITY_NOT_YET_FROZEN"
    },
    providerCalls = 0, authorizedProviderCalls = 0, retry = 0, repair = false, fallback = false,
    goldRead = false, goldMutation = "NONE", productionRequestsChanged = false, semanticAuthorityCreated = false,
    providerExecution = "LOCKED", p7D3 = "LOCKED", p7E = "LOCKED", retrievalTreatmentC = "NOT_IMPLEMENTED"
};
WriteNew(manifestPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.status, requests = prepared.Length, controlProviderBodyParity = report.controlProviderBodyParity,
    manifestSha256 = SpatialCanonical.Hash(File.ReadAllBytes(manifestPath)), f1.EvidenceStore.StoreSha256, providerCalls = 0 }));
