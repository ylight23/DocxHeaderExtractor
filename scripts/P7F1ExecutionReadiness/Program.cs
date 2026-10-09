using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Read frozen files only; no transport construction or credentials. Output is a plan, not authorization.
if (args.Length != 2) throw new ArgumentException("Usage: <frozen-F1-directory> <new-execution-manifest.json>");
if (File.Exists(args[1])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var manifestBytes = Read(Path.Combine(args[0], "request-manifest.v1.json"), P7F1ExecutionReadiness.FrozenRequestManifestSha256);
using var manifest = JsonDocument.Parse(manifestBytes); var r = manifest.RootElement;
var offlineBytes = Read("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.offline-closure-qualification.v1.json", P7F1ExecutionReadiness.OfflineQualificationSha256);
using var offline = JsonDocument.Parse(offlineBytes);
Need(offline.RootElement.GetProperty("verdict").GetProperty("d23OfflineFreeze").GetString() == "PASS", "OFFLINE_GATE_NOT_PASS");
var endpoint = Read("artifacts/web-pdf-semantic-diagnostic/p7.openrouter-endpoint-metadata.20261009.v1.json", P7F1ExecutionReadiness.EndpointSnapshotSha256);
using var metadata = JsonDocument.Parse(endpoint);
var pinnedProvider = metadata.RootElement.GetProperty("data").GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("tag").GetString() == "alibaba");
var plan = P7F1ExecutionReadiness.Prepare(manifestBytes);
foreach (var call in plan.Calls) {
    var path = SafePath(args[0], call.ProviderBodyFile);
    var body = Read(path, call.Identity.ProviderBodySha256);
    P7F1ExecutionReadiness.ValidateBody(call, body);
    Read(path.Replace(".provider-body.json", ".system.txt", StringComparison.Ordinal), call.Identity.SystemPromptSha256);
    Read(path.Replace(".provider-body.json", ".user.json", StringComparison.Ordinal), call.Identity.UserMessageSha256);
    var issued = Read(SafePath(args[0], call.Identity.Document + "/" + call.Identity.Pack.Split(':')[^1] + ".issued-universe.json"), call.Identity.IssuedUniverseSha256);
    using var ids = JsonDocument.Parse(issued);
    Need(ids.RootElement.GetArrayLength() == call.InScopeOwned + call.OutsideScopeOwned, "ISSUED_CARDINALITY_DRIFT");
}
foreach (var file in r.GetProperty("generationSourceFiles").EnumerateArray()) Read(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!);
foreach (var document in r.GetProperty("documents").EnumerateArray())
    Read(SafePath(args[0], document.GetProperty("document").GetString()! + "/source-evidence-store.json"), document.GetProperty("storeSha256").GetString()!);
Read(Path.Combine(args[0], "downstream-generation-recipe.v1.json"), r.GetProperty("downstreamRecipeSha256").GetString()!);
Read("scripts/P7UpstreamCaptureReadiness/capture-receipt.schema.v1.json", "f042b0fc9252235a841dabbb30f1fc5634efa27c2429b56b7fd459e38985b553");
var sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1ExecutionReadiness.cs",
    "scripts/P7F1ExecutionReadiness/Program.cs", "scripts/P7F1ExecutionReadiness/P7F1ExecutionReadiness.csproj",
    "scripts/P7F1ExecutionReadiness/README.md", "scripts/P7F1ExecutionReadiness/attempt-receipt.schema.v1.json" }
    .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }).ToArray();
var notAttempted = plan.Calls.Select(c => new P7F1AttemptOutcome(c.CallHandle, false, "NOT_ATTEMPTED", null, null)).ToArray();
var output = SpatialCanonical.Bytes(new {
    version = P7F1ExecutionReadiness.Version, status = plan.Status, plan,
    endpoint = new { uri = "https://openrouter.ai/api/v1/chat/completions", model = "qwen/qwen3.7-flash",
        providerTag = "alibaba", reasoning = "ENABLED_NOT_EFFORT", temperature = 0, responseFormat = "json_object", stream = true,
        metadataSnapshotSha256 = plan.EndpointMetadataSnapshotSha256, advertisedEndpointName = pinnedProvider.GetProperty("name").GetString(),
        observation = "HISTORICAL_PIN_20261009_NOT_FRESH_PRICING_OR_AVAILABILITY_VERIFICATION", hiddenWeightsOrTokenizerRevisionPinned = false },
    transport = new { timeoutSeconds = 300, transientRequestRetries = 0, httpAttemptCap = 14, concurrency = 1,
        repair = false, fallback = false, frozenBodyBytesMustBeSentUnchanged = true,
        defaultRetryPolicyMustNotBeInherited = true, responseCapsApplyAfterRawFreezeNotToSseBytes = true,
        newCacheHeaders = false, cacheDisableClaim = "NOT_ESTABLISHED_BY_FROZEN_CARRIER" },
    budget = new { currency = "USD", approvedSpend = (decimal?)null, approvedInputTokens = (long?)null,
        completionTokenCeilingPerCall = 32768, sumRequestedCompletionCeilings = 458752,
        isTotalBillingTokenCap = false, bytesAreTokenUpperBound = false, actualUsage = (object?)null, actualCost = (decimal?)null,
        exactTokenizerMapping = "UNVERIFIED", status = plan.BudgetStatus,
        prerequisite = "SEPARATE_USD_CAP_AND_CONSERVATIVE_PER_CALL_RESERVATION_OR_EXPLICIT_EXPOSURE_POLICY",
        unknownFailedCallCostIsZero = false, cumulativePostResponseStopGuaranteesHardSpendCap = false },
    evaluation = new { mode = "F1_ISOLATED", metricNamespace = "F1_ISOLATED_COMPOSITE_B",
        documents = 5, pages = 6, reviewedRowsPerArm = 213, outsideScopePerArm = 430, outsideScopeStatus = "UNKNOWN_UNSCORED",
        goldManifestSha256 = r.GetProperty("goldManifestSha256").GetString(), scorerManifestSha256 = r.GetProperty("scorerManifestSha256").GetString(),
        policySha256 = r.GetProperty("policySha256").GetString(),
        goldOpenBoundary = "AFTER_BOTH_ARMS_RAW_AND_PARSED_RECEIPTS_FROZEN_AND_PROVIDER_GATE_CLOSED",
        reporting = new[] { "F1_THREE_FUNCTION_CONFUSION", "ESTABLISHES_MEMBERSHIP_TP_FP_FN", "PAIRED_COMMON_ADJUDICATED_ROWS",
            "CALL_AND_VALID_LEDGER_COVERAGE", "FAILURE_CLASS_COUNTS", "RAW_PROVIDER_USAGE_AND_REPORTED_COST" },
        failuresAreNotOTHER = true, incompleteScoresMustDiscloseCoverage = true, f1DoesNotScoreAnchorOrExtent = true,
        futureCrossingScopeBoundary = "NOT_EVALUABLE_DOES_NOT_EXEMPT_ADJUDICATED_MEMBERSHIP_ERRORS" },
    capture = new { schema = "scripts/P7UpstreamCaptureReadiness/capture-receipt.schema.v1.json",
        attemptSchema = "scripts/P7F1ExecutionReadiness/attempt-receipt.schema.v1.json",
        order = "RESERVE_BOUND_ATTEMPT_CAPTURE_RAW_FREEZE_PARSE_FREEZE_LEDGER_ACCOUNT_CLOSE_PROVIDER_GATE_SCORE",
        requiredFiles = new[] { "provider-body.json", "response.txt", "response.sse", "observation.json", "raw-freeze.json", "parsed-stage-decision.json", "receipt.json", "issued-universe.json" },
        preserveAvailableFailureEvidence = true, retry = 0, historicalAdoption = false,
        templates = plan.Calls.Select(c => new { c.CallHandle, c.Identity, status = "NOT_RESERVED_NOT_CAPTURED", rawHashes = (object?)null,
            executionManifestSha256 = "BIND_FINAL_MANIFEST_HASH_AT_RESERVATION", eligibleForDownstreamExecution = false }).ToArray() },
    failurePolicy = new { perCall = "QUARANTINE_NO_RETRY_REPAIR_FALLBACK_NO_SYNTHETIC_OR_HISTORICAL_SUBSTITUTE",
        globalHalt = new[] { "CANCELLED", "BUDGET_EXHAUSTED", "UNKNOWN_COST_EXPOSURE", "ENDPOINT_OR_REQUEST_DRIFT", "RAW_STORAGE_FAILURE" },
        haltedRemainder = "NOT_ATTEMPTED_NOT_OTHER", incompleteCaptureIsComplete = false },
    initialAccounting = P7F1ExecutionReadiness.Account(plan, notAttempted),
    downstream = new { g2aAuthorizedCalls = 0, h2cAuthorizedCalls = 0, controlled = "DEFERRED_SEPARATE_AUTHORIZATION",
        natural = "DEFERRED_SEPARATE_AUTHORIZATION", generation = "VALID_REAL_UPSTREAM_ONLY_NO_GOLD_OR_SYNTHETIC_SUBSTITUTION", metricPooling = false },
    pendingExecutionGates = new[] { "EXPLICIT_14_PRIMARY_CALL_AUTHORIZATION", "APPROVED_USD_OR_EXPOSURE_CAP",
        "ENDPOINT_AND_COST_RESERVATION_BINDING", "EXECUTION_RUNNER_BOUND_TO_THIS_MANIFEST_AND_CAPTURE_POLICY" },
    inputsAndRequestsChanged = false, goldMutation = "NONE", productionChanged = false, historicalReceiptsChanged = false,
    providerCalls = 0, authorizedProviderCalls = 0, semanticImprovementClaim = false,
    providerExecution = "LOCKED", productionPromotion = "LOCKED", generationSourceFiles = sources });
using (var stream = new FileStream(args[1], FileMode.CreateNew)) { stream.Write(output); stream.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { status = plan.Status, plannedCalls = plan.Calls.Count,
    receiptSha256 = SpatialCanonical.Hash(output), providerCalls = 0, approvedSpend = plan.ApprovedUsdCap }));
static byte[] Read(string path, string expected) { var bytes = File.ReadAllBytes(path); Need(SpatialCanonical.Hash(bytes) == expected, "HASH_DRIFT:" + Path.GetFileName(path)); return bytes; }
static void Need(bool valid, string error) { if (!valid) throw new InvalidOperationException(error); }
static string SafePath(string root, string relative) {
    var directory = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(Path.Combine(directory, relative));
    Need(path.StartsWith(directory, StringComparison.OrdinalIgnoreCase), "PATH_OUTSIDE_FREEZE"); return path;
}
