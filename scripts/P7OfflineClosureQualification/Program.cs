using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DocxHeaderExtractor.V5Qualification.P7;

// No HTTP, provider execution, Gold decisions, reviewer sidecars, or pilot downstream writes.
if (args.Length != 5) throw new ArgumentException("Usage: <frozen-F1-dir> <existing-reproduction-dir> <role-free-Gold-dir> <P7-regression.trx> <new-receipt>");
if (File.Exists(args[4])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
const string prefix = "artifacts/web-pdf-semantic-diagnostic/";
var contractBytes = File.ReadAllBytes("scripts/P7OfflineClosureQualification/acceptance-contract.v2.json");
using var contract = JsonDocument.Parse(contractBytes);
Need(contract.RootElement.GetProperty("version").GetString() == P7OfflineClosureQualification.Version &&
    contract.RootElement.GetProperty("requiredChecks").EnumerateArray().Select(c => c.GetString()!).SequenceEqual(P7OfflineClosureQualification.RequiredChecks), "ACCEPTANCE_CODE_DRIFT");
var pins = new[] {
    ("p7.d2.3.upstream-capture-readiness.v1.json", "6e7ef07e466f81b6e7c98ef949c8fe4820d5baeb194ddae1abfd7fe22a56cea2"),
    ("p7.d2.3.upstream-capture-readiness-verification.v1.json", "87e4abbc727060e6534c22a3d0e87d0f081763119ecefc7127e3bf694d113862"),
    ("p7.d2.3.pilot-f1-request-freeze.v1.json", "2a5e4491270fd7ec92f536bfa8ac1e9812c8440f9fbb3e0a58a99d06e2965ae1"),
    ("p7.d2.3.frozen-f1-treatment-delta-audit.v1.json", "39d12e524364a47e5cfee641e3564bdefa1cac587a98c9075ed72886cc3978f2"),
    ("p7.d2.3.gold-v2-freeze-qualification.v1.json", "e5076f39fb3508b22f965c6c650c9df279802b70756f8c6dc52142b2f41098d9") };
foreach (var (file, hash) in pins) Read(prefix + file, hash);
using var readiness = JsonDocument.Parse(Read(prefix + pins[0].Item1, pins[0].Item2)); var prior = readiness.RootElement;
Need(prior.GetProperty("status").GetString() == contract.RootElement.GetProperty("historicalReadinessStatus").GetString() &&
    prior.GetProperty("d23").GetString() == contract.RootElement.GetProperty("historicalD23").GetString(), "HISTORICAL_STATE_DRIFT");
Need(!prior.GetProperty("reuseInventory").GetProperty("fullCurrentQualificationEligibility").GetBoolean() &&
    !prior.GetProperty("reuseInventory").GetProperty("reusePolicyAmendmentApproved").GetBoolean(), "HISTORICAL_ADOPTION_DRIFT");
using var f1 = JsonDocument.Parse(Read(Path.Combine(args[0], "request-manifest.v1.json"), pins[2].Item2)); var manifest = f1.RootElement;
Read(Path.Combine(args[0], "downstream-generation-recipe.v1.json"), manifest.GetProperty("downstreamRecipeSha256").GetString()!);
foreach (var row in manifest.GetProperty("requests").EnumerateArray()) {
    var path = Path.Combine(args[0], row.GetProperty("providerBodyFile").GetString()!);
    using var body = JsonDocument.Parse(Read(path, row.GetProperty("providerBodySha256").GetString()!));
    var messages = body.RootElement.GetProperty("messages");
    Need(HashText(messages[0].GetProperty("content").GetString()!) == row.GetProperty("systemPromptSha256").GetString() &&
        HashText(messages[1].GetProperty("content").GetString()!) == row.GetProperty("userMessageSha256").GetString(), "F1_MESSAGE_DRIFT");
    Read(path.Replace(".provider-body.json", ".system.txt", StringComparison.Ordinal), row.GetProperty("systemPromptSha256").GetString()!);
    Read(path.Replace(".provider-body.json", ".user.json", StringComparison.Ordinal), row.GetProperty("userMessageSha256").GetString()!);
}
var original = Inventory(args[0]); var reproduction = Inventory(args[1]);
Need(original.Length == 56 && original.SequenceEqual(reproduction), "FROZEN_FILE_SET_OR_BYTE_DRIFT");
foreach (var file in manifest.GetProperty("generationSourceFiles").EnumerateArray()) Read(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!);
foreach (var file in prior.GetProperty("sourceFiles").EnumerateArray()) Read(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!);
using var goldMetadata = JsonDocument.Parse(Read(Path.Combine(args[2], "gold-manifest.v2.json"), manifest.GetProperty("goldManifestSha256").GetString()!));
Read(Path.Combine(args[2], "scorer-manifest.v1.json"), manifest.GetProperty("scorerManifestSha256").GetString()!);
Read(Path.Combine(args[2], "evaluation-policy.v1.json"), manifest.GetProperty("policySha256").GetString()!);
var goldFiles = goldMetadata.RootElement.GetProperty("documents").EnumerateArray().ToArray();
foreach (var row in goldFiles) Read(Path.Combine(args[2], row.GetProperty("goldFile").GetString()!), row.GetProperty("goldSha256").GetString()!);
Need(goldFiles.Length == 5 && manifest.GetProperty("requests").GetArrayLength() == 14 &&
    manifest.GetProperty("inScopeOccurrences").GetInt32() == 213 && manifest.GetProperty("issuedOwnedOccurrences").GetInt32() == 643, "PILOT_SCOPE_DRIFT");

var trxBytes = File.ReadAllBytes(args[3]);
using var trxStream = new MemoryStream(trxBytes);
var trx = XDocument.Load(trxStream);
XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var results = trx.Descendants(ns + "UnitTestResult").ToArray();
Need(results.Length >= 319 && results.All(r => r.Attribute("outcome")?.Value == "Passed"), "P7_REGRESSION_INCOMPLETE_OR_FAILED");
var definitions = trx.Descendants(ns + "UnitTest").ToDictionary(d => d.Attribute("id")!.Value,
    d => (Class: d.Element(ns + "TestMethod")!.Attribute("className")!.Value, Method: d.Element(ns + "TestMethod")!.Attribute("name")!.Value));
var closureTests = results.Where(r => definitions[r.Attribute("testId")!.Value].Class.Contains("P7OfflineClosureQualificationTests", StringComparison.Ordinal)).ToArray();
var expectedMethods = new Dictionary<string, int> {
    ["Valid_fixtures_generate_deterministic_bodies_and_bindings"] = 4,
    ["Invalid_or_missing_upstream_never_selects_an_alternative"] = 6,
    ["Changed_raw_upstream_changes_dependency_even_when_decisions_and_body_match"] = 1,
    ["Changed_F1_decision_changes_downstream_issued_subjects_without_Gold"] = 1,
    ["Same_provider_bytes_in_different_modes_do_not_share_manifest_or_metrics"] = 1,
    ["Generation_has_no_Gold_sidecar_or_fallback_parameter_and_no_file_reads"] = 1,
    ["Offline_acceptance_cannot_pass_incomplete_duplicate_or_unknown_checks"] = 3,
    ["Offline_PASS_keeps_execution_usage_promotion_and_historical_adoption_closed"] = 1,
    ["Acceptance_V2_contract_matches_code_and_preserves_pinned_historical_state"] = 1 };
foreach (var pair in expectedMethods) Need(closureTests.Count(r => definitions[r.Attribute("testId")!.Value].Method == pair.Key) == pair.Value, "CLOSURE_REGRESSION_MISSING:" + pair.Key);
Need(closureTests.Length == expectedMethods.Values.Sum(), "CLOSURE_TEST_SET_DRIFT");
var verdict = P7OfflineClosureQualification.Qualify(P7OfflineClosureQualification.RequiredChecks);
var newSources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7OfflineClosureQualification.cs",
    "tests/DocxHeaderExtractor.Tests/P7OfflineClosureQualificationTests.cs", "scripts/P7OfflineClosureQualification/Program.cs",
    "scripts/P7OfflineClosureQualification/P7OfflineClosureQualification.csproj", "scripts/P7OfflineClosureQualification/README.md" }
    .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }).ToArray();
var output = SpatialCanonical.Bytes(new {
    version = "P7_D23_OFFLINE_CLOSURE_QUALIFICATION_V1", status = "OFFLINE_PREPARATION_QUALIFIED_UNDER_V2_ACCEPTANCE",
    acceptanceContractSha256 = SpatialCanonical.Hash(contractBytes), verdict,
    historicalInputs = pins.Select(p => new { path = prefix + p.Item1, sha256 = p.Item2 }).ToArray(),
    verifiedChecks = P7OfflineClosureQualification.RequiredChecks,
    regression = new { total = results.Length, passed = results.Length, failed = 0, skipped = 0, closureTests = closureTests.Length,
        methods = expectedMethods, trxSha256 = SpatialCanonical.Hash(trxBytes), fullSuite = "NOT_RUN", ci = "NOT_RUN" },
    immutability = new { f1FilesCompared = original.Length, mismatches = 0,
        generationSourcesChecked = manifest.GetProperty("generationSourceFiles").GetArrayLength(),
        captureSourcesChecked = prior.GetProperty("sourceFiles").GetArrayLength(), goldFilesHashChecked = goldFiles.Length,
        historicalStatesRewritten = false, frozenRebaseline = false },
    pilot = new { documents = 5, pages = 6, goldOccurrences = 213, goldHeadingUnits = 15, goldExtentParts = 20,
        f1Requests = 14, issuedOwned = 643, outsideScope = "430_UNKNOWN_UNSCORED", actualPilotDownstreamBodiesCreated = 0,
        executionFreeze = "NOT_ACHIEVED", actualDownstreamCounts = "UNKNOWN_UNTIL_VALID_UPSTREAM" },
    fixturePurpose = "PROTOCOL_REGRESSION_ONLY_NOT_SEMANTIC_ACCURACY_OR_PRODUCTION_BASELINE",
    noGoldDecisionsOrReviewerSidecarsLoadedForGeneration = true, goldBytesHashCheckedOnly = true,
    oldCaptures = "HISTORICAL_DIAGNOSTIC_NOT_ADOPTED", newSourceFiles = newSources,
    exactTokenizerMapping = "BLOCKED", providerUsageCalibration = "PENDING", tokenBudgetMeasurement = "BLOCKED",
    providerCalls = 0, authorizedProviderCalls = 0, goldMutation = "NONE", runtimeChanged = false,
    providerExecution = "LOCKED", productionPromotion = "LOCKED", semanticAccuracyClaim = false });
using (var stream = new FileStream(args[4], FileMode.CreateNew)) { stream.Write(output); stream.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { status = verdict.D23OfflineFreeze, tests = results.Length, closureTests = closureTests.Length,
    receiptSha256 = SpatialCanonical.Hash(output), providerCalls = 0, execution = verdict.D3ProviderExecution }));

static byte[] Read(string path, string hash) { var bytes = File.ReadAllBytes(path); Need(SpatialCanonical.Hash(bytes) == hash, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return bytes; }
static string HashText(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
static string[] Inventory(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path =>
    Path.GetRelativePath(root, path).Replace('\\', '/') + "|" + SpatialCanonical.Hash(File.ReadAllBytes(path))).Order(StringComparer.Ordinal).ToArray();
static void Need(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
