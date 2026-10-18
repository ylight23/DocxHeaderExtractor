using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

internal static class BoundaryGroundedCohortCommand
{
    internal sealed record Manifest(string SourceManifest, string OriginAudit, string OriginAuditSha256,
        string SourceSha256, string AliasUniverseSha256, string EvidenceStoreSha256, string[][] ExcludedPilotScopes,
        BoundaryGroundedCohort.Proposal[] Proposals);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[3] != "--live")
        {
            Console.Error.WriteLine("boundary-grounded-cohort <predeclared-manifest.json> <new-output-directory> --live");
            return 2;
        }
        var declarationText = File.ReadAllText(args[1]);
        var manifest = JsonSerializer.Deserialize<Manifest>(declarationText, Json)!;
        var manifestSha = Sha(declarationText);
        var originText = File.ReadAllText(manifest.OriginAudit);
        if (Sha(originText) != manifest.OriginAuditSha256) throw new InvalidOperationException("COHORT_ORIGIN_AUDIT_MISMATCH");
        var origin = originText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)
            .Single(node => node["type"]?.GetValue<string>() == "result");
        // Exhaustive rule fixed before outcomes: all original VERIFY C0 scopes with >=2 aliases, excluding named pilots.
        var expected = origin["pipeline"]!["audit"]!["grouping"]!["partitions"]!.AsArray()
            .Where(node => node!["phase"]?.GetValue<string>() == "VERIFY" && node["boundaryRepairCycle"]!.GetValue<int>() == 0)
            .Where(node => node!["candidateAliases"]!.AsArray().Count >= 2)
            .Where(node => !manifest.ExcludedPilotScopes.Any(scope => scope.SequenceEqual(node!["candidateAliases"]!.Deserialize<string[]>()!)))
            .Select(node => new BoundaryGroundedCohort.Proposal(node!["candidateId"]!.GetValue<string>(), node["outputGroups"]!.Deserialize<string[][]>()!)).ToArray();
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(manifest.Proposals)))
            throw new InvalidOperationException("COHORT_MUST_MATCH_EXHAUSTIVE_DECLARATION_RULE");
        var output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        var sourceConfig = JsonSerializer.Deserialize<BoundaryStaticCanaryCommand.Manifest>(File.ReadAllText(manifest.SourceManifest), Json)!;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        var source = BoundarySuggestedCanaryCommand.LoadSource(sourceConfig, ct);
        if (source.SourceSha256 != manifest.SourceSha256 || source.SourceAliasUniverseSha256 != manifest.AliasUniverseSha256 ||
            source.EvidenceStoreSha256 != manifest.EvidenceStoreSha256) throw new InvalidOperationException("COHORT_SOURCE_MISMATCH");
        BoundaryGroundedCohort.ValidateProposals(source, manifest.Proposals);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        File.WriteAllText(Path.Combine(output, "declaration.json"), declarationText);
        var budget = new AgentBudget(400, 3);
        File.WriteAllText(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(new
        {
            manifestSha, qualificationOnly = true, productionAuthorization = false,
            candidateCount = manifest.Proposals.Length, maxPostAdmissions = 400, maxUsd = 3,
            parallelism = 4, sourceRepresentation = "default-reading-full-extent", noSeedReplacement = true,
            seedEligibility = "valid C0 REGROUP, all HEADING, no requested dynamic evidence",
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256
        }, Json));
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, key, budget, cancellationToken: ct);
        AgentUnit.Model model = (unit, round, request) =>
        {
            Console.WriteLine(unit + " POST " + round);
            return provider.Turn(unit, round, request);
        };
        void Private(string name, string text) => File.WriteAllText(Path.Combine(output, "private", name + ".json"), text);
        void Freeze(BoundaryGroundedCohort.Baseline[] baselines)
        {
            var eligible = baselines.Where(b => b.Status == "ELIGIBLE_REGROUP").ToArray();
            File.WriteAllText(Path.Combine(output, "cohort-lock.json"), JsonSerializer.Serialize(new
            {
                manifestSha, baselinePosts = budget.Calls, baselineCostUsd = budget.ActualUsd, baselines,
                eligibleSeeds = eligible.Select(b => new { b.Id, b.PacketSha256,
                    privateSeedSha256 = Sha(File.ReadAllText(Path.Combine(output, "private", b.Id + "-seed.json"))) })
            }, Json));
            Console.WriteLine("COHORT FROZEN: " + eligible.Length + "/" + baselines.Length + " eligible; no replacements");
        }
        void Save(BoundaryGroundedCohort.Report report) => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            manifestSha, qualificationOnly = true, productionAuthorization = false,
            candidateCount = report.Baselines.Length, eligibleCount = report.Baselines.Count(b => b.Status == "ELIGIBLE_REGROUP"),
            completedD = report.Outcomes.Length, providerPosts = budget.Calls, costUsd = budget.ActualUsd,
            categories = BoundaryGroundedCohort.Categories.ToDictionary(category => category, category => report.Outcomes.Count(o => o.Category == category)), report
        }, Json));
        var result = BoundaryGroundedCohort.Run(source, manifest.Proposals, model, budget, 4, ct, Private, Freeze, Save);
        Save(result);
        Console.WriteLine("qualification cohort complete; all candidates and outcomes retained; no production headings emitted");
        return 0;
    }
}
