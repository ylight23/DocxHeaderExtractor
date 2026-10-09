using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Only public metadata GET. No credential access, inference POST, or execution mode exists here.
if (args.Length == 2 && args[0] == "snapshot") {
    Need(!Directory.Exists(args[1]), "NEW_SNAPSHOT_DIRECTORY_REQUIRED");
    var started = DateTimeOffset.UtcNow;
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    using var response = await client.GetAsync(P7FinancialQualification.MetadataEndpoint);
    Need(response.IsSuccessStatusCode, "PUBLIC_METADATA_GET_FAILED");
    var bytes = await response.Content.ReadAsByteArrayAsync(); var received = DateTimeOffset.UtcNow;
    var binding = P7FinancialQualification.Bind(bytes);
    Directory.CreateDirectory(args[1]);
    Write(Path.Combine(args[1], "endpoint-metadata.json"), bytes);
    Write(Path.Combine(args[1], "snapshot-receipt.json"), SpatialCanonical.Bytes(new {
        version = "P7_PUBLIC_METADATA_SNAPSHOT_V1", uri = P7FinancialQualification.MetadataEndpoint,
        method = "GET", credentialsUsed = false, sourceDataSent = false, inferenceCalls = 0,
        startedAt = started, receivedAt = received, httpStatus = (int)response.StatusCode,
        representation = "EXACT_HTTP_CONTENT_BYTES_NOT_COMPRESSED_WIRE", metadataSha256 = binding.MetadataSha256,
        bindingSha256 = binding.BindingSha256 }));
    Console.WriteLine(JsonSerializer.Serialize(new { status = "PUBLIC_METADATA_FROZEN", binding.MetadataSha256, binding.BindingSha256 }));
} else if (args.Length == 4 && args[0] == "qualify") {
    Need(!File.Exists(args[3]), "NEW_MANIFEST_REQUIRED");
    var metadata = File.ReadAllBytes(Path.Combine(args[1], "endpoint-metadata.json"));
    var receipt = File.ReadAllBytes(Path.Combine(args[1], "snapshot-receipt.json"));
    var binding = P7FinancialQualification.Bind(metadata);
    using var receiptDoc = JsonDocument.Parse(receipt); var r = receiptDoc.RootElement;
    Need(r.GetProperty("metadataSha256").GetString() == binding.MetadataSha256 &&
        r.GetProperty("bindingSha256").GetString() == binding.BindingSha256 &&
        r.GetProperty("uri").GetString() == P7FinancialQualification.MetadataEndpoint &&
        r.GetProperty("method").GetString() == "GET" && r.GetProperty("httpStatus").GetInt32() == 200 &&
        !r.GetProperty("credentialsUsed").GetBoolean() && !r.GetProperty("sourceDataSent").GetBoolean() &&
        r.GetProperty("inferenceCalls").GetInt32() == 0, "SNAPSHOT_RECEIPT_DRIFT");
    var execution = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.f1-execution-readiness.v1.json");
    Need(SpatialCanonical.Hash(execution) == P7FinancialQualification.ExecutionManifestSha256, "EXECUTION_PLAN_DRIFT");
    var plan = P7F1ExecutionReadiness.Prepare(File.ReadAllBytes(Path.Combine(args[2], "request-manifest.v1.json")));
    foreach (var call in plan.Calls) {
        var root = Path.GetFullPath(args[2]) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, call.ProviderBodyFile));
        Need(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "PATH_OUTSIDE_FREEZE");
        P7F1ExecutionReadiness.ValidateBody(call, File.ReadAllBytes(path));
    }
    var sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7FinancialQualification.cs",
        "scripts/P7FinancialQualification/Program.cs", "scripts/P7FinancialQualification/P7FinancialQualification.csproj",
        "scripts/P7FinancialQualification/README.md", "scripts/P7FinancialQualification/financial-receipt.schema.v1.json" }
        .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }).ToArray();
    var output = SpatialCanonical.Bytes(new {
        version = P7FinancialQualification.Version, status = "OFFLINE_POLICY_QUALIFIED_ACTUAL_EXECUTION_LOCKED",
        executionManifestSha256 = P7FinancialQualification.ExecutionManifestSha256,
        frozenRequestManifestSha256 = plan.RequestManifestSha256,
        metadataReceiptSha256 = SpatialCanonical.Hash(receipt), metadataReceivedAt = r.GetProperty("receivedAt").GetDateTimeOffset(),
        binding, exposureEstimate = P7FinancialQualification.Estimate(binding),
        budget = new { currency = "USD", candidateTotal = 1m, approvedTotal = (decimal?)null,
            candidateIsApproved = false, perCallBillableUpperBound = (decimal?)null,
            fourteenCallsGuaranteedWithinCandidate = false, keyLimitVerified = false,
            keyCreatedOrInspected = false, hardProviderCostGuarantee = false, actualCostUsd = (decimal?)null,
            actualUsage = (object?)null, unknownExposureIsZero = false },
        preSendPolicy = new { callCap = 14, concurrency = 1, timeoutSeconds = 300, retry = 0, repair = false, fallback = false,
            metadataMaximumAgeSeconds = 300, recheckBeforeEveryStart = true, bindingDrift = "HALT_BEFORE_SEND",
            requireApprovedSpend = true, requireDedicatedKeyReceipt = true, requireVerifiedBillingBound = true,
            reserveBeforeSend = true, unknownCharge = "RETAIN_EXPOSURE_HALT_REMAINING", capExceeded = "HALT_RECORD_ACTUAL_UNCLAMPED",
            realTransportIntegration = "DEFERRED_LOCKED_NOT_IMPLEMENTED_BY_THIS_OFFLINE_SIMULATOR" },
        endpointClaims = new { maxTokensAdvertisedSupported = true, requestedMaxTokens = 32768,
            empiricallyHonored = "NOT_MEASURED", hiddenWeightsPinned = false, exactTokenizerMapping = "UNVERIFIED",
            reasoningNotDoubleCounted = true, allPricingTiersIncluded = true, billingSemanticsVerified = false,
            noCacheDisableClaim = true, requestBytesChanged = false, additionalMaxPriceField = false },
        usageReceipt = new { schema = "scripts/P7FinancialQualification/financial-receipt.schema.v1.json",
            costSource = "usage.cost_ACCOUNT_CHARGE_NOT_cost_details.upstream_inference_cost",
            reasoningUsageOptional = true, requiredForContinuation = new[] { "prompt_tokens", "completion_tokens", "cost", "immutable_observation_hash" },
            missingRequired = "HALT_NOT_ZERO_NOT_FREE_NOT_RETRY", calibrationDone = false },
        pending = new[] { "EXPLICIT_14_CALL_AUTHORIZATION", "APPROVED_USD_CAP", "DEDICATED_KEY_LIMIT_RECEIPT",
            "VERIFIED_REQUEST_BILLING_EXPOSURE_BOUND", "LIVE_RUNNER_TRANSPORT_GATE_BINDING" },
        documentation = new[] { "https://openrouter.ai/docs/guides/routing/provider-selection",
            "https://openrouter.ai/docs/cookbook/administration/usage-accounting",
            "https://openrouter.ai/docs/api_reference/limits", "https://openrouter.ai/docs/guides/best-practices/reasoning-tokens",
            "https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key" },
        generationSourceFiles = sources, providerCalls = 0, authorizedCalls = 0, goldMutation = "NONE", productionChanged = false,
        semanticAccuracyClaim = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" });
    Write(args[3], output); Console.WriteLine(JsonSerializer.Serialize(new { manifestSha256 = SpatialCanonical.Hash(output), providerCalls = 0 }));
} else throw new ArgumentException("Usage: snapshot <new-directory> | qualify <snapshot-directory> <frozen-F1-directory> <new-manifest.json>");
static void Need(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
static void Write(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew); stream.Write(bytes); stream.Flush(true); }
