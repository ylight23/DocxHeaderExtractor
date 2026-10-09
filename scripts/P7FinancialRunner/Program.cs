using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Deliberately no live/execution/key mode. Uses 14 actual frozen bodies with a sealed, network-free fake.
if (args.Length != 4 || args[0] != "dry-run") throw new ArgumentException("Usage: dry-run <frozen-F1-directory> <new-private-journal-directory> <new-public-receipt.json>");
if (Directory.Exists(args[2]) || File.Exists(args[3])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var execution = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.f1-execution-readiness.v1.json");
var financial = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-qualification.v2.json");
var frozen = File.ReadAllBytes(Path.Combine(args[1], "request-manifest.v1.json"));
var metadata = File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-endpoint-snapshot.v1/endpoint-metadata.json");
var plan = P7F1FinancialRunner.ValidateFrozenPackage(execution, financial, frozen, metadata);
var binding = P7FinancialQualification.Bind(metadata);
var bodies = plan.Calls.ToDictionary(c => c.CallHandle, c => {
    var root = Path.GetFullPath(args[1]) + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(Path.Combine(root, c.ProviderBodyFile));
    P7FinancialQualification.Need(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "BODY_PATH_OUTSIDE_FREEZE");
    return File.ReadAllBytes(path);
});
// Time and monetary receipts below are EXPLICIT synthetic fixtures; historical metadata is not a fresh live observation.
var fixtureNow = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
var proof = SpatialCanonical.Hash(SpatialCanonical.Bytes(new { origin = "FIXTURE_ONLY_NOT_AUTHORIZATION_NOT_BILLING_PROOF" }));
var approval = new P7RunnerApproval(P7FinancialQualification.ExecutionManifestSha256, P7F1FinancialRunner.FinancialManifestSha256,
    proof, true, 1m, true, 1m, 1m, proof, plan.Calls.Select(c => new P7RunnerBound(c.CallHandle, c.Identity.ProviderBodySha256,
        binding.BindingSha256, 0.05m, proof, "FIXTURE_ONLY_ALL_CHARGES_BOUND")).ToArray(), true);
var transport = new P7F1DryRunTransport();
var runner = new P7F1FinancialRunner(plan, bodies, binding, transport, new P7F1FileJournal(args[2], plan, true),
    _ => Task.FromResult(new P7RunnerMetadata(metadata, fixtureNow)), () => fixtureNow, true);
var result = await runner.RunAsync(approval);
P7FinancialQualification.Need(result.Status == "RAW_ATTEMPTS_COMPLETED" && result.TransportInvocations == 14 &&
    transport.BodyHashes.SequenceEqual(plan.Calls.Select(c => c.Identity.ProviderBodySha256)), "DRY_RUN_NOT_COMPLETE_OR_BODY_DRIFT");
var sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1FinancialRunner.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1RunnerAdapters.cs",
    "scripts/P7FinancialRunner/Program.cs", "scripts/P7FinancialRunner/P7FinancialRunner.csproj", "scripts/P7FinancialRunner/README.md" }
    .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) });
var receipt = SpatialCanonical.Bytes(new { version = P7F1FinancialRunner.Version, origin = "SYNTHETIC_FINANCIAL_DRY_RUN_WITH_FROZEN_REQUEST_BYTES",
    executionManifestSha256 = P7FinancialQualification.ExecutionManifestSha256, financialManifestSha256 = P7F1FinancialRunner.FinancialManifestSha256,
    requestManifestSha256 = plan.RequestManifestSha256, result, frozenBodyHashesSentToFake = transport.BodyHashes,
    fixtureOnly = true, fixtureBudgetIsApprovedSpend = false, fixtureMetadataTimestampIsLiveObservation = false,
    fakeTransportInvocations = 14, providerCalls = 0, authorizedProviderCalls = 0, credentialsRead = false, networkRequests = 0,
    nativeUsageMeasurement = "NOT_MEASURED", semanticAccuracy = "NOT_EVALUATED", goldRead = false, goldMutation = "NONE",
    productionChanged = false, frozenRequestsChanged = false, providerExecution = "LOCKED", productionPromotion = "LOCKED",
    generationSourceFiles = sources });
using (var file = new FileStream(args[3], FileMode.CreateNew)) { file.Write(receipt); file.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { status = result.Status, fakeCalls = 14, providerCalls = 0, receiptSha256 = SpatialCanonical.Hash(receipt) }));
