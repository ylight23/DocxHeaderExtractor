using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// No provider/credentials/replay/Gold dependency. Output must be a new file.
if (args.Length != 2) throw new ArgumentException("Usage: P7SpatialEvidence <pdf> <new-ledger-json>");
if (File.Exists(args[1])) throw new InvalidOperationException("spatial-evidence-output-already-exists");
var parsed = PdfSourceAdapter.BuildWithDetails(args[0]);
var ledger = PdfSpatialEvidenceLedgerBuilder.Build(parsed.Snapshot, parsed.Details);
var reversed = PdfSpatialEvidenceLedgerBuilder.Build(parsed.Snapshot with { Atoms = parsed.Snapshot.Atoms.Reverse().ToArray() },
    parsed.Details with { Blocks = parsed.Details.Blocks.Reverse().ToArray() });
if (!ledger.CanonicalBytes().SequenceEqual(reversed.CanonicalBytes())) throw new InvalidOperationException("spatial-permutation-check-failed");
var observed = ledger.Facts.Where(fact => fact.Availability == "OBSERVED").ToArray();
var issued = ledger.Observations.Select(observation => observation.Alias).ToHashSet(StringComparer.Ordinal);
var references = observed.Select(fact => new SpatialEvidenceReference(fact.FactId, fact.Subjects, fact.Value)).ToArray();
var issues = PdfSpatialEvidenceValidator.Validate(parsed.Snapshot, parsed.Details, ledger, issued, references);
if (issues.Count != 0) throw new InvalidOperationException("spatial-evidence-self-verification-failed");
var report = new {
    schemaVersion = "p7-d1-parser-spatial-evidence-audit-1", status = "PROVIDER_FREE_LEDGER_VERIFIED_NOT_SEMANTIC_QUALIFICATION",
    ledger.ProtocolVersion, ledger.SourceSha256, ledger.SourceAliasUniverseSha256, ledger.ModelVisibleEvidenceSha256,
    ledger.LedgerSha256, queryPolicy = PdfSpatialEvidenceLedgerBuilder.QueryPolicy, geometryBasis = PdfSpatialEvidenceLedgerBuilder.GeometryBasis,
    ledger.Observations, ledger.Facts,
    verification = new { permutationByteIdentical = true, observedReferencesVerified = references.Length,
        notAvailable = ledger.Facts.Count - references.Length, issues = issues.Count },
    providerCalls = 0, goldRead = false, goldMutation = "NONE", sourceUniverseMutation = false,
    productionRequestsChanged = false, semanticAuthorityCreated = false, p7D2 = "LOCKED", p7D3 = "LOCKED", productionPromotion = "LOCKED",
    limitations = new[] { "SAME_VISUAL_ROW is positive common y-band intersection, not semantic identity, native row identity, or table classification",
        "Default inventory covers consecutive source windows of 2/3 only; not all spatial pairs or full page topology",
        "Parser glyph geometry may depend on font reconstruction; provenance is not proof of visual/semantic truth",
        "No validity claim for rotated/writing-mode-specific row relationships; no typography/layout-block/semantic feature added" }
};
var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
using (var file = new FileStream(args[1], FileMode.CreateNew)) { file.Write(bytes); file.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { report.status, subjects = ledger.Observations.Count, facts = ledger.Facts.Count,
    observed = references.Length, notAvailable = ledger.Facts.Count - references.Length, ledger.LedgerSha256,
    artifactSha256 = SpatialCanonical.Hash(bytes), providerCalls = 0 }));
