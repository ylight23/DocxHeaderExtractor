using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline process-isolation proof for the per-attempt timeout policy.</summary>
public static class ProviderTimeoutPolicyV2Runner
{
    public static async Task<int> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "artifacts", "execution-integrity", "provider-timeout-policy-v2");
        Directory.CreateDirectory(output);
        var perAttempt = TimeSpan.FromSeconds(1);
        var oldDocumentBudget = TimeSpan.FromMilliseconds(300);

        var sequenceRoot = Path.Combine(output, "multi-healthy");
        var sequence = new List<ProviderHardTimeoutIntegrity.WatchdogResult>();
        for (var i = 1; i <= 3; i++)
        {
            sequence.Add(await ProviderHardTimeoutIntegrity.RunAttemptAsync(
                "healthy-delay", Path.Combine(sequenceRoot, $"attempt-{i:D2}"), perAttempt, cancellationToken));
        }

        var lateRoot = Path.Combine(output, "late-call");
        var lateStartedAt = DateTimeOffset.UtcNow;
        await Task.Delay(oldDocumentBudget + TimeSpan.FromMilliseconds(50), cancellationToken);
        var late = await ProviderHardTimeoutIntegrity.RunAttemptAsync("healthy-delay", lateRoot, perAttempt, cancellationToken);

        var hang = await ProviderHardTimeoutIntegrity.RunAttemptAsync(
            "hang", Path.Combine(output, "real-hang"), perAttempt, cancellationToken);

        var sequenceComplete = sequence.All(x => x.Status == "COMPLETE" && x.TerminationMode == "NONE");
        var cumulativeExceededOldBudget = sequence.Count == 3 &&
            (sequence[^1].CompletedAt - sequence[0].StartedAt) > oldDocumentBudget;
        var lateCallReceivedFullAttempt = late.Status == "COMPLETE" &&
            late.TerminationMode == "NONE" && (late.CompletedAt - late.StartedAt) < perAttempt;
        var hangIsolated = hang.Status == "ATTEMPT_TIMEOUT" && hang.TerminationMode == "PROCESS_TREE_KILL";
        var passed = sequenceComplete && cumulativeExceededOldBudget && lateCallReceivedFullAttempt && hangIsolated;
        var productionSemanticHash = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var summary = new
        {
            schemaVersion = "provider-timeout-policy-v2-summary-v1",
            status = passed ? "PER_ATTEMPT_TIMEOUT_POLICY_PROVEN" : "FAILED",
            providerCalls = 0,
            goldReads = 0,
            modelCalls = 0,
            productionSemanticHash,
            expectedProductionSemanticHash = "5f0eb27dfa44068fc60c69e0dcf8a05b14a061ed7526610e4592d68c268fe5e6",
            policy = new
            {
                perAttemptHardTimeoutSeconds = ProviderTimeoutPolicyV2.DefaultPerAttemptHardTimeoutSeconds,
                documentSafetyCeilingSeconds = ProviderTimeoutPolicyV2.DefaultDocumentSafetyCeilingSeconds,
                fixedDocumentTimeoutAuthorityRemoved = true,
            },
            scaledProof = new
            {
                simulatedPerAttemptTimeoutSeconds = perAttempt.TotalSeconds,
                simulatedOldDocumentBudgetMs = oldDocumentBudget.TotalMilliseconds,
                multiHealthyCalls = sequence,
                multiHealthyCallsComplete = sequenceComplete,
                cumulativeExceededOldDocumentBudget = cumulativeExceededOldBudget,
                lateCallStartedAt = lateStartedAt,
                lateCall = late,
                lateCallReceivedFullAttemptWindow = lateCallReceivedFullAttempt,
                realHang = hang,
                realHangKilledAsAttemptTimeout = hangIsolated,
            },
        };
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# Provider timeout policy v2\n\nStatus: {(passed ? "PER_ATTEMPT_TIMEOUT_POLICY_PROVEN" : "FAILED")}\n\n- Provider calls: 0\n- Gold reads: 0\n- Production semantic hash: `{productionSemanticHash}`\n- Multi-call cumulative-budget test: `{sequenceComplete && cumulativeExceededOldBudget}`\n- Late-call full-window test: `{lateCallReceivedFullAttempt}`\n- Real-hang attempt isolation test: `{hangIsolated}`\n\nThe proof uses scaled durations for fast deterministic execution; production policy remains 300 seconds per physical attempt with a separate document safety ceiling.\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "provider-timeout-policy-v2-manifest-v1",
            status = passed ? "PER_ATTEMPT_TIMEOUT_POLICY_PROVEN" : "FAILED",
            providerCalls = 0,
            goldReads = 0,
            modelCalls = 0,
            artifacts = Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new { path = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() })
                .Where(item => item.path != "manifest.json")
                .ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return passed ? 0 : 2;
    }
}
