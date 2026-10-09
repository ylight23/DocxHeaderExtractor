using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Context candidates only. No runnable transport, Gold, responses, or production contract mutation.
if (args.Length != 6) throw new ArgumentException("Usage: <repo> <pdf> <private-control-root> <private-B-root> <new-private-context-directory> <new-public-manifest>");
static void Require(bool valid, string code) { if (!valid) throw new InvalidOperationException(code); }
var directory = Path.GetFullPath(args[4]); var output = Path.GetFullPath(args[5]);
Require(!Directory.Exists(directory) && !File.Exists(output), "page-context-output-exists");
var manifestBytes = File.ReadAllBytes(Path.Combine(args[0], "artifacts/web-pdf-semantic-diagnostic/p7.interpretation-preflight.v2.json"));
Require(SpatialCanonical.Hash(manifestBytes) == "348312e37a9e92295e0928ef13376c6b2fd266a54bc2dfea4a7d62aa89b1d76e", "page-context-preflight-hash");
using var manifest = JsonDocument.Parse(manifestBytes);
var parsed = PdfSourceAdapter.BuildWithDetails(args[1]);
Require(parsed.Snapshot.SourceSha256 == manifest.RootElement.GetProperty("sourceSha256").GetString(), "page-context-source-hash");
var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
Require(store.StoreSha256 == manifest.RootElement.GetProperty("evidenceStoreSha256").GetString(), "page-context-store-hash");
var policies = new[] { new PageContextPolicy(PageContextScope.LocalVerticalWindow),
    new PageContextPolicy(PageContextScope.SubjectPages),
    new PageContextPolicy(PageContextScope.SubjectAndAdjacentPages, AdjacentPageRadius: 1) };
const int EnvelopeByteCap = 1_048_576;
var prepared = new List<(string Name, byte[] Bytes)>();
var rows = new List<object>();
foreach (var call in manifest.RootElement.GetProperty("plannedRequests").EnumerateArray())
{
    var name = call.GetProperty("name").GetString()!;
    var controlDirectory = name == "F1" ? "20261008-f1-01" : name == "G2A" ? "20261008-g2a-01" : "20261008-h2c-01/" + name[4..];
    var controlBytes = File.ReadAllBytes(Path.Combine(args[2], controlDirectory, "provider-body.json"));
    var bBytes = File.ReadAllBytes(Path.Combine(args[3], name + ".provider-body.json"));
    Require(SpatialCanonical.Hash(controlBytes) == call.GetProperty("controlProviderBodySha256").GetString() &&
        SpatialCanonical.Hash(bBytes) == call.GetProperty("providerBodySha256").GetString(), "page-context-frozen-body-hash");
    using var control = JsonDocument.Parse(controlBytes); using var b = JsonDocument.Parse(bBytes);
    var bText = b.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    Require(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(bText)) == call.GetProperty("userMessageSha256").GetString(), "page-context-user-hash");
    using var user = JsonDocument.Parse(bText);
    using var controlUser = JsonDocument.Parse(control.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    Require(JsonElement.DeepEquals(user.RootElement.GetProperty("stageInput"), controlUser.RootElement), "page-context-control-drift");
    var mapping = user.RootElement.GetProperty("sourceEvidence").EnumerateArray()
        .ToDictionary(x => x.GetProperty("occurrence").GetString()!, x => x.GetProperty("source").GetProperty("sourceAlias").GetString()!);
    var subjects = user.RootElement.GetProperty("decisionSubjects").EnumerateArray().Select(x => mapping[x.GetString()!]).ToArray();
    var baselines = mapping.Values.ToHashSet(StringComparer.Ordinal);
    foreach (var policy in policies)
    {
        var context = PdfPageEvidenceContextBuilder.Build(store, subjects, policy);
        var contextBytes = context.CanonicalBytes();
        // A draft envelope is size-audited, not issued as a provider request. Existing B parser
        // does not yet admit page-context references; that separate protocol gate remains closed.
        var envelope = JsonNode.Parse(bText)!.AsObject();
        envelope["contextCandidateVersion"] = "P7_PAGE_CONTEXT_ENVELOPE_DRAFT_V1";
        envelope["pageEvidenceContext"] = JsonNode.Parse(contextBytes);
        var envelopeBytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        Require(envelopeBytes.Length <= EnvelopeByteCap, "page-context-envelope-byte-cap");
        envelope.Remove("contextCandidateVersion"); envelope.Remove("pageEvidenceContext");
        Require(JsonNode.DeepEquals(envelope, JsonNode.Parse(bText)), "page-context-baseline-mutation");
        var visible = context.Pages.SelectMany(x => x.Observations).ToArray();
        var subjectOrdinals = subjects.Select(alias => store.Entries.Single(x => x.SourceAlias == alias).Ordinal).ToArray();
        var label = name + "_" + policy.Scope;
        prepared.Add((label, contextBytes));
        rows.Add(new { name, scope = policy.Scope.ToString(), contextSha256 = context.ContextSha256,
            contextUtf8Bytes = contextBytes.Length, draftUserEnvelopeUtf8Bytes = envelopeBytes.Length,
            draftUserEnvelopeSha256 = SpatialCanonical.Hash(envelopeBytes),
            inputTokens = (int?)null, inputTokensStatus = "NOT_MEASURED_NO_PINNED_MODEL_TOKENIZER_PROVIDER_GATE_BLOCKED",
            unicodeScalars = Encoding.UTF8.GetString(envelopeBytes).EnumerateRunes().Count(),
            contextOccurrences = visible.Length, addedAliases = visible.Where(x => !baselines.Contains(x.SourceAlias)).Select(x => x.SourceAlias).ToArray(),
            contextOnlySelectableCount = visible.Count(x => x.Selectable),
            prefixAvailableForSubjects = subjects.Select((alias, i) => new { alias,
                earlierSamePageOccurrences = context.Pages.SelectMany(x => x.Observations)
                    .Count(x => x.Ordinal < subjectOrdinals[i] && store.Entries.Single(e => e.SourceAlias == x.SourceAlias).Fields.Single(f => f.Name == "page").Value!.Value.GetInt32() ==
                        store.Entries.Single(e => e.SourceAlias == alias).Fields.Single(f => f.Name == "page").Value!.Value.GetInt32()) }).ToArray(),
            pages = context.Pages.Select(x => new { x.Page, x.AvailableSourceOccurrences, x.Coverage,
                aliases = x.Observations.Select(o => o.SourceAlias).ToArray() }).ToArray(),
            missingGeometryAliases = context.MissingGeometryAliases });
    }
}
Directory.CreateDirectory(directory);
foreach (var item in prepared) WriteNew(Path.Combine(directory, item.Name + ".context.json"), item.Bytes);
var result = new {
    schemaVersion = "p7-page-context-provider-free-preflight-1", status = "CONTEXT_BUILDER_ONLY_NOT_PROVIDER_REQUEST_READY",
    preflightSha256 = SpatialCanonical.Hash(manifestBytes), sourceSha256 = store.SourceSha256,
    sourceAliasUniverseSha256 = store.SourceAliasUniverseSha256, evidenceStoreSha256 = store.StoreSha256,
    policy = new { localVerticalRadiusPoints = 72, adjacentPageRadius = 1, maxContextUtf8Bytes = 262_144,
        maxDraftUserEnvelopeUtf8Bytes = EnvelopeByteCap, missingBounds = "NO_GEOMETRIC_INCLUSION_EXCEPT_SUBJECT_REPORTED_NOT_FALSE",
        truncation = "FORBIDDEN_FAIL_CLOSED_ON_BUDGET", glyphProjection = "NONE_PARSER_SPAN_MAP_RETAINED_UNCHANGED" },
    contextCandidates = rows,
    ablationDesign = new {
        currentBAlreadyIncludesInterpretation = true,
        proposedAxes = new[] { "CONTEXT_SCOPE: EXISTING / LOCAL / SUBJECT_PAGES", "RESPONSE: EVIDENCE_ONLY / REFERENCES_AND_INTERPRETATION" },
        warning = "B_PLUS_PAGE_VS_B_PLUS_PAGE_PLUS_INTERPRETATION_IS_NOT_A_NEW_AXIS_B_ALREADY_HAS_INTERPRETATION",
        runnableTreatmentRequestsFrozen = false },
    gates = new { providerCalls = 0, goldRead = false, runtimeChanged = false, sourceUniverseChanged = false,
        controlBodiesChanged = false, existingBRequestsChanged = false, semanticPredicates = false,
        corpusFrozen = false, scoringPolicyFrozen = false, exactTokenizerAvailable = false,
        productionPromotion = "LOCKED" }
};
WriteNew(output, JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { contextCandidates = prepared.Count, output, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(output)) }));
static void WriteNew(string path, byte[] bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = new FileStream(path, FileMode.CreateNew); stream.Write(bytes); stream.Flush(true);
}
