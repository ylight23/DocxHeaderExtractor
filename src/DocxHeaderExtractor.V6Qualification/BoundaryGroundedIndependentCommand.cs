using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.HeadingAgent;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

internal static class BoundaryGroundedIndependentCommand
{
    internal sealed record Case(string Id, string SeedSha256, string OldIndependentSha256);
    internal sealed record Manifest(string SourceManifest, string CohortDirectory, string ReportSha256,
        string LockSha256, Case[] Cases);
    internal sealed record Input(string Id, JsonObject Packet, Dictionary<string, TextOffsetSpan> Extents,
        Dictionary<string, string> Readings, string OldRequestSha256);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static Input Prepare(Case item, JsonObject cohortReport, JsonObject cohortLock, JsonObject seed, JsonObject oldCheck)
    {
        var outcome = cohortReport["report"]!["outcomes"]!.AsArray().Single(o => o!["id"]!.GetValue<string>() == item.Id)!;
        var result = outcome["result"]!;
        if (outcome["category"]!.GetValue<string>() != "CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED" ||
            result["independentVerdict"]?.GetValue<string>() != "REGROUP")
            throw new InvalidOperationException("E_REQUIRES_FROZEN_CHANGED_REJECTED_PROPOSAL");
        var eligible = cohortLock["eligibleSeeds"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == item.Id)!;
        if (eligible["privateSeedSha256"]!.GetValue<string>() != item.SeedSha256 ||
            result["seedPacketSha256"]!.GetValue<string>() != eligible["packetSha256"]!.GetValue<string>() ||
            Sha(seed["packet"]!.ToJsonString()) != eligible["packetSha256"]!.GetValue<string>())
            throw new InvalidOperationException("E_FROZEN_SEED_MISMATCH");
        var packet = (JsonObject)seed["packet"]!.DeepClone();
        var target = packet["candidates"]!.AsArray().Single()!.AsObject();
        var oldGroups = target["proposedGroups"]!.Deserialize<string[][]>()!;
        var changed = result["suggestedGroups"]!.Deserialize<string[][]>()!;
        if (BoundaryGroundedCanary.Equal(oldGroups, changed) ||
            !oldGroups.SelectMany(g => g).SequenceEqual(changed.SelectMany(g => g)))
            throw new InvalidOperationException("E_PROPOSAL_NOT_CHANGED_OR_SCOPE_MISMATCH");
        var verdict = oldCheck["Verdicts"]!.Deserialize<AgentGroupVerification.Verdict[]>()!.Single();
        if (verdict.Run != "V1" || verdict.Decision != "REGROUP" ||
            !verdict.Membership.Select(m => m.Alias).SequenceEqual(changed.SelectMany(g => g)) ||
            verdict.Membership.Any(m => m.Status != "HEADING") ||
            oldCheck["VerifierEvidence"]!["Requests"]!.AsArray().Count != 0)
            throw new InvalidOperationException("E_OLD_INDEPENDENT_NOT_STATIC_REGROUP");
        // The original independent Ask used V1 and the same alias scope, source snapshot, reading and extent maps.
        // Only the partition and candidate id differ from the retained C0 packet.
        target["run"] = "V1";
        target["proposedGroups"] = JsonSerializer.SerializeToNode(changed);
        var originalReplies = oldCheck["Evidence"]!.AsArray().Select(e => JsonNode.Parse(e!.GetValue<string>())!)
            .Where(e => e["response"] is not null).ToArray();
        if (originalReplies.Length != 1) throw new InvalidOperationException("E_EXPECTS_SINGLE_ORIGINAL_INDEPENDENT_POST");
        var expected = originalReplies[0]["requestSha256"]!.GetValue<string>();
        var originalRequest = AgentGrouping.BuildRequest(packet.ToJsonString(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            [], AgentGroupVerification.Prompt);
        if (originalRequest.Sha256 != expected) throw new InvalidOperationException("E_ORIGINAL_INDEPENDENT_REQUEST_RECONSTRUCTION_MISMATCH");
        return new(item.Id, packet, seed["extents"]!.Deserialize<Dictionary<string, TextOffsetSpan>>()!,
            seed["readings"]!.Deserialize<Dictionary<string, string>>()!, expected);
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[3] is not ("--live" or "--validate"))
        {
            Console.Error.WriteLine("boundary-grounded-e <predeclared-manifest.json> <new-output-directory> --live|--validate");
            return 2;
        }
        var declaration = File.ReadAllText(args[1]);
        var manifest = JsonSerializer.Deserialize<Manifest>(declaration, Json)!;
        // This canary has exactly the two predeclared changed/rejected branches, never a searched replacement.
        if (!manifest.Cases.Select(c => c.Id).SequenceEqual(new[] { "V17", "V24" })) throw new InvalidOperationException("E_FIXED_CASES_REQUIRED");
        var reportText = File.ReadAllText(Path.Combine(manifest.CohortDirectory, "report.json"));
        var lockText = File.ReadAllText(Path.Combine(manifest.CohortDirectory, "cohort-lock.json"));
        if (Sha(reportText) != manifest.ReportSha256 || Sha(lockText) != manifest.LockSha256)
            throw new InvalidOperationException("E_COHORT_HASH_MISMATCH");
        var report = JsonNode.Parse(reportText)!.AsObject();
        var cohortLock = JsonNode.Parse(lockText)!.AsObject();
        var inputs = manifest.Cases.Select(c =>
        {
            var seedText = File.ReadAllText(Path.Combine(manifest.CohortDirectory, "private", c.Id + "-seed.json"));
            var oldText = File.ReadAllText(Path.Combine(manifest.CohortDirectory, "private", c.Id + "-independent.json"));
            if (Sha(seedText) != c.SeedSha256 || Sha(oldText) != c.OldIndependentSha256)
                throw new InvalidOperationException("E_PRIVATE_INPUT_HASH_MISMATCH");
            return Prepare(c, report, cohortLock, JsonNode.Parse(seedText)!.AsObject(), JsonNode.Parse(oldText)!.AsObject());
        }).ToArray();
        var config = JsonSerializer.Deserialize<BoundaryStaticCanaryCommand.Manifest>(File.ReadAllText(manifest.SourceManifest), Json)!;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        var source = BoundarySuggestedCanaryCommand.LoadSource(config, ct);
        var priorManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(manifest.CohortDirectory, "declaration.json")))!;
        if (source.SourceSha256 != priorManifest["sourceSha256"]!.GetValue<string>() ||
            source.SourceAliasUniverseSha256 != priorManifest["aliasUniverseSha256"]!.GetValue<string>() ||
            source.EvidenceStoreSha256 != priorManifest["evidenceStoreSha256"]!.GetValue<string>())
            throw new InvalidOperationException("E_SOURCE_HASH_MISMATCH");
        foreach (var input in inputs) Console.WriteLine(input.Id + " original independent request SHA matched: " + input.OldRequestSha256);
        if (args[3] == "--validate") return 0;
        var output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        File.WriteAllText(Path.Combine(output, "declaration.json"), declaration);
        // Persist both fully frozen inputs before the first provider call; they contain no proposer reason or old verdict.
        foreach (var input in inputs)
            File.WriteAllText(Path.Combine(output, "private", input.Id + "-input.json"), JsonSerializer.Serialize(input));
        File.WriteAllText(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(new
        {
            manifestSha256 = Sha(declaration), source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            maxPostAdmissions = 6, maxUsd = 1, noSecondSuggestionApplied = true,
            inputs = inputs.Select(input => new { input.Id, input.OldRequestSha256, packetSha256 = Sha(input.Packet.ToJsonString()) })
        }, Json));
        var budget = new AgentBudget(6, 1);
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, key, budget, cancellationToken: ct);
        var outcomes = new List<object>();
        foreach (var input in inputs)
        {
            AgentUnit.Model model = (unit, round, request) =>
            {
                Console.WriteLine(input.Id + "-" + unit + " POST " + round);
                return provider.Turn(input.Id + "-" + unit, round, request);
            };
            BoundaryGroundedCanary.Result? result = null;
            string? failure = null;
            try
            {
                result = BoundaryGroundedCanary.Evaluate(source, input.Packet, input.Extents, input.Readings, model, budget, ct,
                    (name, text) => File.WriteAllText(Path.Combine(output, "private", input.Id + "-" + name + ".json"), text),
                    independentOnly: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failure = "E_RUNTIME_FAILURE"; }
            outcomes.Add(new { input.Id, input.OldRequestSha256, result, failure });
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                qualificationOnly = true, productionAuthorization = false, manifestSha256 = Sha(declaration),
                source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
                providerPosts = budget.Calls, costUsd = budget.ActualUsd, maxProtocolRoundsPerCase = 3,
                noSecondSuggestionApplied = true, outcomes
            }, Json));
        }
        Console.WriteLine("E complete; no second suggestion applied and no production headings emitted");
        return 0;
    }
}
