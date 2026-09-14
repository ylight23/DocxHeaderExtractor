using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV4GB;

internal static class Program
{
    private const string ExpectedHead = "6edb12d07f3d5f468705afca32862440c7fd4077";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution";
    private const string V4GaRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger";
    private const string V4FeRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/execution";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private const string ConfigHash = "8983be53cc38d8a28c8d1467216934d3bab1201673dae86f077f82328b392967";
    private const int ExpectedSample = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        var approved = args.Any(x => string.Equals(x, "--execute-approved", StringComparison.Ordinal));
        try
        {
            if (approved)
                throw new InvalidOperationException("V4G_B_PROVIDER_EXECUTION_REQUIRES_EXPLICIT_OPERATOR_APPROVAL_AND_LIVE_EXECUTOR_PHASE");

            await RunPreflightAsync(root);
            Console.WriteLine("V4G_B_STATUS=AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4G_B_ERROR={ex}");
            return 2;
        }
    }

    private static async Task RunPreflightAsync(string root)
    {
        var head = Git(root, "rev-parse HEAD");
        Require(head == ExpectedHead, $"V4G_B_UNEXPECTED_HEAD:{head}");

        var v4ga = Full(root, V4GaRelative);
        var v4fe = Full(root, V4FeRelative);
        var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output), "V4G_B_EXECUTION_ALREADY_EXISTS_USE_RESUME_PHASE");
        Directory.CreateDirectory(output);

        using var gaManifest = Read(Path.Combine(v4ga, "manifest.json"));
        using var gaRequests = Read(Path.Combine(v4ga, "request-manifest.json"));
        using var gaSample = Read(Path.Combine(v4ga, "challenger-sample-request-manifest.json"));
        using var gaFirewall = Read(Path.Combine(v4ga, "firewall.json"));
        ValidateV4Ga(gaManifest.RootElement, gaRequests.RootElement, gaSample.RootElement, gaFirewall.RootElement);

        var baseline = ReconstructHistoricalBaseline(v4fe);
        var sampleRows = gaSample.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var sampleIds = sampleRows.Select(x => x.GetProperty("candidateId").GetString()!).ToArray();
        var allRows = gaRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var sampleRequestRows = sampleIds.Select(id => allRows[id]).ToArray();

        var now = DateTimeOffset.UtcNow;
        await WriteAsync(Path.Combine(output, "provider-preflight.json"), new
        {
            schemaVersion = "a99-v4g-b-provider-preflight-v1",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            requiredExplicitApproval = true,
            requiredFlag = "--execute-approved",
            expectedHead = ExpectedHead,
            actualHead = head,
            sampleCount = sampleRows.Length,
            model = Model,
            provider = Provider,
            configurationHash = ConfigHash,
            transientRequestRetries = 0,
            goldReadCount = 0,
            modelCalls = 0,
            providerCalls = 0,
            requestBodiesPersisted = false,
            requestIntegrityReady = true,
            historicalBaselineReconstructed = true,
            temporalProviderDriftControlled = false,
            createdUtc = now,
        });

        await WriteAsync(Path.Combine(output, "historical-baseline.json"), baseline);
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-execution-manifest-v1",
            experiment = "IDENTITY_BENCHMARK_V4G_B",
            parent = "V4G-A@6edb12d",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            historicalControlComparison = true,
            temporalProviderDriftControlled = false,
            candidateUniverse = 7_702,
            frozenSampleCount = ExpectedSample,
            scheduledCalls = ExpectedSample,
            actualModelCalls = 0,
            actualProviderCalls = 0,
            sampleCandidateIdsSha256 = Sha256Text(string.Join("\n", sampleIds)),
            v4gManifestSha256 = Sha256File(Path.Combine(v4ga, "manifest.json")),
            v4gRequestManifestSha256 = Sha256File(Path.Combine(v4ga, "request-manifest.json")),
            v4gSampleRequestManifestSha256 = Sha256File(Path.Combine(v4ga, "challenger-sample-request-manifest.json")),
            parserChanged = false,
            validatorChanged = false,
            projectionChanged = false,
            targetGroundingRepresentationChanged = true,
            goldReadCount = 0,
            createdUtc = now,
        });

        await WriteAsync(Path.Combine(output, "attempt-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-attempt-manifest-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            expectedAttemptCount = ExpectedSample,
            actualAttemptCount = 0,
            retries = 0,
            attempts = Array.Empty<object>(),
        });
        await WriteAsync(Path.Combine(output, "raw-response-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-raw-response-manifest-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            frozenBeforeParsing = true,
            responseCount = 0,
            persistedRawBodies = 0,
            responses = Array.Empty<object>(),
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "parsed-results.json"), new
        {
            schemaVersion = "a99-v4g-b-parsed-results-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            parser = "HdsaGlobalIdentityRetrieveVerifyContract",
            parserChanged = false,
            responseCount = 0,
            results = Array.Empty<object>(),
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "target-grounding-results.json"), new
        {
            schemaVersion = "a99-v4g-b-target-grounding-results-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            primaryMetric = "TARGET_MISMATCH",
            scheduledDenominator = ExpectedSample,
            evaluableDenominator = (int?)null,
            targetMismatchScheduled = (int?)null,
            targetMismatchEvaluable = (int?)null,
            targetMismatchScheduledRate = (double?)null,
            targetMismatchEvaluableRate = (double?)null,
            validScheduledRate = (double?)null,
            validEvaluableRate = (double?)null,
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "historical-paired-analysis.json"), new
        {
            schemaVersion = "a99-v4g-b-historical-paired-analysis-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            note = "No V2 response exists; paired analysis is deferred until all 128 V2 responses freeze.",
            pairs = Array.Empty<object>(),
        });
        await WriteAsync(Path.Combine(output, "usage-and-latency.json"), new
        {
            schemaVersion = "a99-v4g-b-usage-and-latency-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            calls = 0,
            inputTokens = Array.Empty<int>(),
            outputTokens = Array.Empty<int>(),
            latencyMs = Array.Empty<long>(),
        });
        await WriteAsync(Path.Combine(output, "cost-report.json"), new
        {
            schemaVersion = "a99-v4g-b-cost-report-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            provider = Provider,
            model = Model,
            actualProviderCalls = 0,
            estimatedCostUsd = 0m,
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v4g-b-firewall-v1",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            goldReadCount = 0,
            ir018ToIr022ReadCount = 0,
            v4ebEvaluationReadCount = 0,
            modelCalls = 0,
            providerCalls = 0,
            candidateUniverseChanged = false,
            evidenceChanged = false,
            projectionChanged = false,
            parserChanged = false,
            validatorChanged = false,
            targetGroundingRepresentationChanged = true,
            all128Scheduled = true,
            historical45UsedForSelection = false,
            noProviderTransport = true,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(baseline, sampleRequestRows.Length), new UTF8Encoding(false));
    }

    private static object ReconstructHistoricalBaseline(string v4fe)
    {
        var executionManifestPath = Path.Combine(v4fe, "execution-manifest.json");
        var parsedPath = Path.Combine(v4fe, "parsed-results.json");
        var attemptsDir = Path.Combine(v4fe, "attempts");
        var rawDir = Path.Combine(v4fe, "raw-responses");
        Require(File.Exists(executionManifestPath) && File.Exists(parsedPath) && Directory.Exists(attemptsDir), "V4G_B_MISSING_V4F_E_ARTIFACTS");
        using var execution = Read(executionManifestPath);
        using var parsed = Read(parsedPath);
        var attempts = Directory.GetFiles(attemptsDir, "*.json").Select(path => Read(path).RootElement.Clone()).ToArray();
        var results = parsed.RootElement.GetProperty("results").EnumerateArray().Select(x => x.Clone()).ToArray();
        Require(attempts.Length == 256 && results.Length == 256, "V4G_B_V4F_E_COUNT");

        var arms = new[] { (Arm: "ARM_A", Name: "OLD"), (Arm: "ARM_B", Name: "PROJECTED_V1") };
        var summaries = arms.Select(arm => BuildArmBaseline(arm.Arm, arm.Name, attempts, results, rawDir)).ToArray();
        Require(summaries.Single(x => x.arm == "OLD").targetMismatch == 2, "V4G_B_OLD_BASELINE_MISMATCH");
        Require(summaries.Single(x => x.arm == "PROJECTED_V1").targetMismatch == 45, "V4G_B_PROJECTED_BASELINE_MISMATCH");
        Require(summaries.Single(x => x.arm == "OLD").valid == 109, "V4G_B_OLD_BASELINE_VALID");
        Require(summaries.Single(x => x.arm == "PROJECTED_V1").valid == 63, "V4G_B_PROJECTED_BASELINE_VALID");
        return new
        {
            schemaVersion = "a99-v4g-b-historical-baseline-v1",
            source = "FROZEN_V4F_E_EXECUTION_ARTIFACTS",
            executionManifestSha256 = Sha256File(executionManifestPath),
            parsedResultsSha256 = Sha256File(parsedPath),
            temporalProviderDriftControlled = false,
            goldReadCount = 0,
            v4ebEvaluationReadCount = 0,
            arms = summaries,
        };
    }

    private static BaselineArm BuildArmBaseline(string arm, string name, JsonElement[] attempts, JsonElement[] results, string rawDir)
    {
        var a = attempts.Where(x => x.GetProperty("arm").GetString() == arm).ToArray();
        var r = results.Where(x => x.GetProperty("arm").GetString() == arm).ToArray();
        var rawPersisted = r.Where(x => RawPersisted(x, rawDir)).ToArray();
        var evaluable = rawPersisted.Where(x => x.GetProperty("status").GetString() is "VALID" or "INVALID_SCHEMA").ToArray();
        var mismatches = evaluable.Count(x => x.GetProperty("parsed").GetProperty("validation").GetProperty("rejectionReason").GetString() == "TARGET_PAIR_MISMATCH");
        var valid = evaluable.Count(x => x.GetProperty("status").GetString() == "VALID");
        var otherInvalid = evaluable.Length - mismatches - valid;
        return new BaselineArm(
            name,
            a.Length,
            a.Count(x => x.TryGetProperty("httpStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 200),
            rawPersisted.Length,
            evaluable.Length,
            mismatches,
            valid,
            otherInvalid,
            a.Count(x => x.GetProperty("status").GetString() == "PROVIDER_ERROR"),
            a.Count(x => x.GetProperty("status").GetString() == "TRANSPORT_ERROR" && x.TryGetProperty("httpStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 200));
    }

    private static bool RawPersisted(JsonElement result, string rawDir)
    {
        var sequence = result.GetProperty("sequence").GetInt32();
        var arm = result.GetProperty("arm").GetString()!;
        var hash = result.GetProperty("rawResponseSha256").GetString();
        if (hash is null) return false;
        var path = Path.Combine(rawDir, $"{sequence:D4}-{arm}.json");
        return File.Exists(path) && Sha256File(path) == hash;
    }

    private static void ValidateV4Ga(JsonElement manifest, JsonElement requests, JsonElement sample, JsonElement firewall)
    {
        Require(manifest.GetProperty("candidateCount").GetInt32() == 7_702, "V4G_B_CANDIDATE_UNIVERSE");
        Require(manifest.GetProperty("frozenSampleCount").GetInt32() == ExpectedSample, "V4G_B_SAMPLE_MANIFEST");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0, "V4G_B_GA_GOLD");
        Require(manifest.GetProperty("providerCalls").GetInt32() == 0, "V4G_B_GA_PROVIDER");
        Require(firewall.GetProperty("goldReadCount").GetInt32() == 0, "V4G_B_GA_FIREWALL");
        Require(requests.GetProperty("requestCount").GetInt32() == 7_702, "V4G_B_REQUEST_UNIVERSE");
        Require(sample.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4G_B_SAMPLE_COUNT");
        var rows = sample.GetProperty("requests").EnumerateArray().ToArray();
        Require(rows.Select(x => x.GetProperty("candidateId").GetString()).Distinct(StringComparer.Ordinal).Count() == ExpectedSample, "V4G_B_SAMPLE_DUPLICATE");
        foreach (var row in rows)
        {
            Require(row.GetProperty("model").GetString() == Model, "V4G_B_MODEL_DRIFT");
            Require(row.GetProperty("provider").GetString() == Provider, "V4G_B_PROVIDER_DRIFT");
            Require(row.GetProperty("configurationHash").GetString() == ConfigHash, "V4G_B_CONFIG_DRIFT");
            Require(row.GetProperty("byteLength").GetInt32() > 0, "V4G_B_REQUEST_LENGTH");
            Require(row.GetProperty("requestSha256").GetString() is { Length: 64 }, "V4G_B_REQUEST_SHA");
        }
    }

    private static string BuildReport(object baseline, int sampleCount)
    {
        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        return string.Join(Environment.NewLine, new[]
        {
            "# A99 V4G-B — frozen target-grounding challenger preflight",
            "",
            "Status: **AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL**",
            "",
            "This phase intentionally stopped before transport. The V4G-B task requires a new explicit operator approval; earlier V4F-E approval is not reused.",
            "",
            "## Frozen execution",
            "",
            $"- Expected HEAD: `{ExpectedHead}`.",
            $"- Target-grounded V2 sample: **{sampleCount}/128**.",
            "- Scheduled provider calls: **128**.",
            "- Actual model/provider calls: **0/0**.",
            "- Retries: **0**.",
            "- Gold reads: **0**.",
            "- Historical comparison: **true**; `temporalProviderDriftControlled=false`.",
            "- The historical OLD and PROJECTED_V1 denominators below were reconstructed from frozen V4F-E attempts, parsed results, and persisted raw bodies before any V2 transport.",
            "",
            "## Historical baseline",
            "",
            "```json",
            json,
            "```",
            "",
            "## Integrity and firewall",
            "",
            "The V4G-A sample IDs and request hashes were checked without persisting request bodies. Future transport must reconstruct each V4G request, verify candidate ID, byte length, SHA256, model, provider, and configuration before the single attempt. A mismatch must fail closed.",
            "",
            "No Gold, IR-018..022, V4E-B labels, OLD outputs, or PROJECTED_V1 outputs were used to select the sample. V4F-E artifacts were read-only inputs and V4G-A artifacts were not modified.",
            "",
            "This is not a semantic validation result. It is an execution-gated target-grounding experiment.",
            ""
        });
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Git(string root, string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit();
        Require(process.ExitCode == 0, "V4G_B_GIT_FAILED:" + process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd().Trim();
    }
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record BaselineArm(string arm, int scheduled, int transportSuccess, int rawAvailable, int validatorEvaluable, int targetMismatch, int valid, int otherInvalid, int providerError, int rawPersistenceFailure);
}
