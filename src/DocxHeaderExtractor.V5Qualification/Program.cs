namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// The only place a real V5 qualification provider call can originate. Not a test project -
/// <c>dotnet test</c> never discovers or runs an Exe project, so the ordinary suite stays provider-free
/// regardless of environment variables. Every command below has its own frozen manifest, explicit
/// confirmation sentinel and retry policy; without a recognised command this prints usage and exits.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // P6T-A is a two-call, total anchor-role ledger viability canary. It never scores semantics;
        // execution needs its own explicit sentinel and makes no retry, repair, fallback, or Pass-2 call.
        if (args.Contains("--p6ta-total-anchor-role-canary"))
            return await P6TATotalAnchorRoleCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tc-correspondence-evidence-canary"))
            return await P6TCCorrespondenceEvidenceCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6td-explicit-abstention-canary"))
            return await P6TDExplicitAbstentionCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te1-unit-topology-canary"))
            return await P6TE1UnitTopologyCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tf1-function-membership-canary"))
            return await P6TF1FunctionMembershipCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te-doc0256-e-challenge") || args.Contains("--p6te-doc0252-e-challenge") || args.Contains("--p6te-src041-e-challenge"))
            return await P6TEChallengeCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te-src041-h2-challenge"))
            return await P6TEH2ContinuationCanary.RunAsync(LocateRepoRoot(), args);
        // P6T-G2A population executes only the five requests frozen across the complete F1-authority cohort.
        if (args.Contains("--p6tg2a-full-pack-population"))
            return await P6TG2AFullPackPopulationCanary.RunAsync(LocateRepoRoot(), args);
        // P6T-H2 executes only the 31 anchor-scoped edge ledgers frozen after full-pack G2A.
        if (args.Contains("--p6th3-full-population-h2"))
            return await P6TH3FullPopulationH2Canary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th-h2b1-k4-full-population"))
            return await P6TH3FullPopulationH2Canary.RunH2B1Async(LocateRepoRoot(), args);
        // P6T-H2C executes the frozen direct end-pointer requests over the full G2A-HAS population.
        if (args.Contains("--p6th2c-direct-end-pointer-full31"))
            return await P6TH2CEndPointerCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-paired"))
            return await P6TH2CEndPointerCanary.RunCleanPairedAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-v2"))
            return await P6TH2CEndPointerCanary.RunCleanV2OnlyAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-v1"))
            return await P6TH2CEndPointerCanary.RunCleanV1OnlyAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-freeze-response-hashes"))
            return P6TH2CEndPointerCanary.FreezeCaptureHashes(LocateRepoRoot());
        if (args.Contains("--p6th2c-clarified-retry-until-accepted"))
            return await P6TH2CEndPointerCanary.ClarifiedRetryUntilAcceptedAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th3-conflict-adjudication"))
            return await P6TH3ConflictAdjudicationCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tf1-retry-src089"))
            return await P6TF1FunctionMembershipCanary.RetrySrc089Async(LocateRepoRoot(), args);

        Console.Error.WriteLine("Usage: dhx-v5-qualify <P6T command> [--confirm=<command sentinel>]");
        Console.Error.WriteLine("No recognised command. Historical P5/P6A-S and V5 canary commands are retired; see git history.");
        return 2;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            $"No DocxHeaderExtractor.sln above {AppContext.BaseDirectory}");
    }
}
