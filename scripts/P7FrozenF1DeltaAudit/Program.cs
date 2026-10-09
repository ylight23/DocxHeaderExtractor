using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Read-only frozen inputs; new sanitized audit output only. No regeneration or provider transport.
if (args.Length != 2) throw new ArgumentException("Usage: P7FrozenF1DeltaAudit <frozen-private-F1-directory> <new-public-receipt>");
if (File.Exists(args[1])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var manifestBytes = Read(Path.Combine(args[0], "request-manifest.v1.json"), "2a5e4491270fd7ec92f536bfa8ac1e9812c8440f9fbb3e0a58a99d06e2965ae1");
using var manifest = JsonDocument.Parse(manifestBytes); var r = manifest.RootElement;
var rows = r.GetProperty("requests").EnumerateArray().ToArray();
var documents = r.GetProperty("documents").EnumerateArray().ToDictionary(d => d.GetProperty("document").GetString()!);
var checks = new List<object>(); var count = 0;
foreach (var group in rows.GroupBy(c => c.GetProperty("document").GetString() + "|" + c.GetProperty("pack").GetString()))
{
    var control = group.Single(c => c.GetProperty("arm").GetString() == "CONTROL");
    var b = group.Single(c => c.GetProperty("arm").GetString() == "B");
    var id = control.GetProperty("document").GetString()!; var pack = control.GetProperty("pack").GetString()!;
    var cb = Read(Path.Combine(args[0], control.GetProperty("providerBodyFile").GetString()!), control.GetProperty("providerBodySha256").GetString()!);
    var bb = Read(Path.Combine(args[0], b.GetProperty("providerBodyFile").GetString()!), b.GetProperty("providerBodySha256").GetString()!);
    using var cBody = JsonDocument.Parse(cb); using var bBody = JsonDocument.Parse(bb);
    foreach (var call in new[] { (control, cBody), (b, bBody) })
        if (HashText(call.Item2.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!) != call.Item1.GetProperty("systemPromptSha256").GetString() ||
            HashText(call.Item2.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!) != call.Item1.GetProperty("userMessageSha256").GetString()) throw new InvalidOperationException("MESSAGE_HASH_DRIFT");
    var store = Read(Path.Combine(args[0], id, "source-evidence-store.json"), documents[id].GetProperty("storeSha256").GetString()!);
    var map = Read(Path.Combine(args[0], id, pack.Split(':')[^1] + ".issued-universe.json"), control.GetProperty("issuedUniverseSha256").GetString()!);
    if (control.GetProperty("issuedUniverseSha256").GetString() != b.GetProperty("issuedUniverseSha256").GetString()) throw new InvalidOperationException("PAIR_ISSUED_HASH_DRIFT");
    var result = P7F1TreatmentDeltaAudit.Validate(cb, bb, store, map, b.GetProperty("systemPromptSha256").GetString()!);
    count += result.IssuedOccurrences;
    checks.Add(new { document = id, pack, result });
}
if (checks.Count != 7 || count != 643 || rows.Length != 14) throw new InvalidOperationException("FROZEN_POPULATION_DRIFT");
var files = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1TreatmentDeltaAudit.cs", "scripts/P7FrozenF1DeltaAudit/Program.cs", "scripts/P7FrozenF1DeltaAudit/P7FrozenF1DeltaAudit.csproj" }
    .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }).ToArray();
var output = SpatialCanonical.Bytes(new { version = P7F1TreatmentDeltaAudit.Version, status = "REGISTERED_COMPOSITE_B_DELTA_PASS",
    requestManifestSha256 = SpatialCanonical.Hash(manifestBytes), pairCount = checks.Count, requestCount = rows.Length,
    issuedOccurrences = count, rows = checks, auditSourceFiles = files,
    allowedDelta = new[] { "RAW_PARSER_SOURCE_EVIDENCE", "VERSIONED_USER_ENVELOPE", "REFERENCES_AND_OPTIONAL_COPIED_ASSERTIONS", "SHORT_INTERPRETATION_RECORD", "REGISTERED_OUTPUT_INSTRUCTIONS_AND_SCHEMA", "QUALIFICATION_RESPONSE_LIMITS" },
    noUnregisteredCarrierOrStageInputOrUniverseDelta = true,
    controlResponseByteCap = r.GetProperty("controlResponseUtf8Cap").GetInt32(), treatmentResponseByteCap = r.GetProperty("treatmentResponseUtf8Cap").GetInt32(),
    completionTokenCeiling = r.GetProperty("completionTokenCeiling").GetInt32(),
    semanticTruthOrModelAccuracyCertified = false, referenceOnlyEffectClaim = false, geometryOnlyEffectClaim = false,
    pendingGates = new[] { "UPSTREAM_CONTROL_RAW_CAPTURE", "DOWNSTREAM_ACTUAL_REQUEST_BYTE_FREEZE", "TOKEN_BUDGET_MEASUREMENT_OR_SEPARATELY_APPROVED_ENDPOINT_CALIBRATION", "EXPLICIT_PROVIDER_AUTHORIZATION" },
    providerCalls = 0, originalRequestsChanged = false, goldChanged = false, productionChanged = false,
    d23 = "OPEN_DOWNSTREAM_REQUEST_FREEZE_PENDING", exactTokenizerMapping = "BLOCKED", providerUsageMeasurement = "BLOCKED", providerExecution = "LOCKED", productionPromotion = "LOCKED" });
using (var f = new FileStream(args[1], FileMode.CreateNew)) { f.Write(output); f.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", pairs = checks.Count, occurrences = count, receiptSha256 = SpatialCanonical.Hash(output), providerCalls = 0 }));
static byte[] Read(string path, string hash) { var bytes = File.ReadAllBytes(path); if (SpatialCanonical.Hash(bytes) != hash) throw new InvalidOperationException("PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return bytes; }
static string HashText(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
