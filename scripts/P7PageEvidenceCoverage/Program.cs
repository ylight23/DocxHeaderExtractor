using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Offline only: no inference transport, credentials, model responses, Gold, or authority changes.
if (args.Length != 5) throw new ArgumentException("Usage: <repo> <pdf> <private-control-root> <private-B-request-root> <new-sanitized-output>");
var repo = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[4]);
if (File.Exists(output)) throw new InvalidOperationException("coverage-output-exists");
static void Require(bool valid, string code) { if (!valid) throw new InvalidOperationException(code); }
var manifestBytes = File.ReadAllBytes(Path.Combine(repo, "artifacts/web-pdf-semantic-diagnostic/p7.interpretation-preflight.v2.json"));
Require(SpatialCanonical.Hash(manifestBytes) == "348312e37a9e92295e0928ef13376c6b2fd266a54bc2dfea4a7d62aa89b1d76e", "coverage-manifest-hash");
using var manifest = JsonDocument.Parse(manifestBytes);
var parsed = PdfSourceAdapter.BuildWithDetails(Path.GetFullPath(args[1]));
var source = parsed.Snapshot;
Require(source.SourceSha256 == manifest.RootElement.GetProperty("sourceSha256").GetString(), "coverage-source-hash");
var store = PdfSourceEvidenceStore.Build(source, parsed.Details);
Require(store.StoreSha256 == manifest.RootElement.GetProperty("evidenceStoreSha256").GetString(), "coverage-store-hash");
var observations = PdfSpatialEvidenceLedgerBuilder.Build(source, parsed.Details, []).Observations.ToDictionary(x => x.Alias);
var owned = source.Atoms.OrderBy(x => x.Ordinal).ThenBy(x => x.Alias, StringComparer.Ordinal).ToArray();
var universe = owned.Select((atom, index) => new CoverageSource("O" + (index + 1), atom.Alias, atom.Page,
    atom.Text, observations[atom.Alias].Bounds is not null)).ToArray();
var requests = new List<object>();
foreach (var call in manifest.RootElement.GetProperty("plannedRequests").EnumerateArray())
{
    var name = call.GetProperty("name").GetString()!;
    var controlDirectory = name == "F1" ? "20261008-f1-01" : name == "G2A" ? "20261008-g2a-01" : "20261008-h2c-01/" + name[4..];
    var controlBytes = File.ReadAllBytes(Path.Combine(args[2], controlDirectory, "provider-body.json"));
    var bBytes = File.ReadAllBytes(Path.Combine(args[3], name + ".provider-body.json"));
    Require(SpatialCanonical.Hash(controlBytes) == call.GetProperty("controlProviderBodySha256").GetString(), "coverage-control-body-hash");
    Require(SpatialCanonical.Hash(bBytes) == call.GetProperty("providerBodySha256").GetString(), "coverage-b-body-hash");
    using var controlBody = JsonDocument.Parse(controlBytes);
    using var bBody = JsonDocument.Parse(bBytes);
    var controlText = controlBody.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    var bText = bBody.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    Require(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(bText)) == call.GetProperty("userMessageSha256").GetString(), "coverage-b-user-hash");
    using var control = JsonDocument.Parse(controlText);
    using var b = JsonDocument.Parse(bText);
    Require(JsonElement.DeepEquals(control.RootElement, b.RootElement.GetProperty("stageInput")), "coverage-control-stage-input-drift");
    // Check all projected raw fields against the parser-owned store, not only the presence of keys.
    var entries = store.Entries.ToDictionary(x => x.SourceAlias);
    foreach (var row in b.RootElement.GetProperty("sourceEvidence").EnumerateArray())
    {
        var entry = row.GetProperty("source");
        Require(entries.TryGetValue(entry.GetProperty("sourceAlias").GetString()!, out var expected) &&
            JsonElement.DeepEquals(entry, SpatialCanonical.Element(expected)), "coverage-projected-store-drift");
    }
    requests.Add(new { name, controlBodySha256 = SpatialCanonical.Hash(controlBytes), bBodySha256 = SpatialCanonical.Hash(bBytes),
        controlSuppliedFieldNames = FieldNames(control.RootElement).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        control = PageEvidenceCoverageAudit.Measure(control.RootElement, universe, false),
        interpretationB = PageEvidenceCoverageAudit.Measure(b.RootElement, universe, true) });
}
static IEnumerable<string> FieldNames(JsonElement node)
{
    if (node.ValueKind == JsonValueKind.Array)
        foreach (var child in node.EnumerateArray()) foreach (var key in FieldNames(child)) yield return key;
    if (node.ValueKind == JsonValueKind.Object)
        foreach (var property in node.EnumerateObject())
        {
            yield return property.Name;
            foreach (var key in FieldNames(property.Value)) yield return key;
        }
}
var lines = parsed.Details.Blocks.SelectMany(x => x.Lines).ToArray();
var fields = store.Entries.SelectMany(x => x.Fields).GroupBy(x => x.Name).OrderBy(x => x.Key)
    .Select(group => new { field = group.Key, observed = group.Count(x => x.Availability == "OBSERVED"), notAvailable = group.Count(x => x.Availability != "OBSERVED") }).ToArray();
var artifact = new {
    schemaVersion = "p7-page-evidence-coverage-audit-1", status = "SINGLE_DOCUMENT_ENGINEERING_AUDIT_NOT_SEMANTIC_QUALIFICATION",
    sourceSha256 = source.SourceSha256, sourceAliasUniverseSha256 = source.SourceAliasUniverseHash,
    preflightSha256 = SpatialCanonical.Hash(manifestBytes), evidenceStoreSha256 = store.StoreSha256,
    parser = new { occurrences = universe.Length, nativeBounds = universe.Count(x => x.BoundsAvailable),
        linesWithGlyphProvenance = lines.Count(x => x.Projection.HasGlyphProvenance), glyphSpanMapEntries = lines.Sum(x => x.Projection.SpanMap.Count),
        sourcePages = universe.Select(x => x.Page).Distinct().Order().ToArray(),
        pageDimensionsRetained = false, pageImagesRetained = false, pageVectorGraphicsRetained = false,
        parserLayoutBlockMapRetained = true },
    store = new { occurrences = store.Entries.Count, fields, pageGroupsProjected = false, parserLayoutBlockIdsProjected = false },
    interpretation = new {
        pairCountsMeaning = "RAW_BOUNDS_AVAILABLE_FOR_COMPARISON_NOT_ASSERTED_ROW_COLUMN_OR_HEADING_RELATIONS",
        pageCoverageMeaning = "CANONICAL_OCCURRENCES_ONLY_NOT_COMPLETE_VISUAL_PDF_PAGE",
        crossPageMeaning = "VISIBLE_TEXT_OR_BOUNDS_ON_MULTIPLE_PAGES_NOT_CROSS_PAGE_SEMANTIC_RELATION",
        fullStoreHashDoesNotImplyFullRequestVisibility = true,
        controlAnonymousContextsNotCountedAsBoundIdentities = true },
    requests, gates = new { providerCalls = 0, goldRead = false, goldMutation = false, runtimeChanged = false,
        frozenRequestsChanged = false, bSpatialImplemented = false, productionPromotion = "LOCKED" }
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
using (var stream = new FileStream(output, FileMode.CreateNew)) { stream.Write(bytes); stream.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { output, artifactSha256 = SpatialCanonical.Hash(bytes), requestPairs = requests.Count }));
