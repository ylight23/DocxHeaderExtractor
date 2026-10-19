using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>Qualification-only framing probes; outputs have no production authority.</summary>
internal static class AdjudicationBiasCanaries
{
    const string BoundaryProtocol = "QUALIFICATION_BOUNDARY_LABEL_SWAP_V1";
    const string HierarchyProtocol = "QUALIFICATION_SINGLE_CHILD_HIERARCHY_V1";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record BoundaryArm(string Case, string LabelOrder, string PhysicalChoice, string[] EvidenceRefs,
        int ResponseBytes, string? FinishReason, string RequestSha256, string? Error);
    internal sealed record ParentDecision(string ChildAlias, string ParentAlias, string[] EvidenceRefs);
    internal sealed record ParentArm(string ChildAlias, string? ParentAlias, string[] EvidenceRefs,
        string? FinishReason, int ResponseBytes, string RequestSha256, string? Error);

    static string BlindCaseId(SourceComparativeCanaries.Case item) => item.Id switch
    {
        "P026-OSCILLATION" => "CASE-01",
        "V17-CONTROL" => "CASE-02",
        "V24-CONTROL" => "CASE-03",
        _ => throw new InvalidOperationException("UNDECLARED_BOUNDARY_CASE")
    };

    internal static JsonObject BoundaryPacket(ScanAgentSource source, SourceComparativeCanaries.Case item, bool swap)
    {
        var a = swap ? item.PartitionB : item.PartitionA;
        var b = swap ? item.PartitionA : item.PartitionB;
        return JsonSerializer.SerializeToNode(new
        {
            protocol = BoundaryProtocol,
            qualificationOnly = true,
            sourceSha256 = source.SourceSha256,
            caseId = BlindCaseId(item),
            sourceEvidence = item.ContextAliases.Select(alias => SourceComparativeCanaries.SourceLine(source, alias)),
            alternatives = new { A = a, B = b }
        }, Json)!.AsObject();
    }

    internal static BoundaryArm JudgeBoundary(ScanAgentSource source, SourceComparativeCanaries.Case item, bool swap,
        AgentUnit.Model model, AgentBudget budget, CancellationToken ct, Action<string, string>? retainPrivate = null)
    {
        var packet = BoundaryPacket(source, item, swap);
        var labelOrder = swap ? "ORIGINAL_B_AS_A" : "ORIGINAL_A_AS_A";
        var blindCaseId = BlindCaseId(item);
        var request = AgentGrouping.BuildRequest(packet.ToJsonString(), [],
            "Independently compare the two proposed heading partitions using only the supplied source text and layout evidence. Return exactly one JSON object: {\"protocol\":\"" + BoundaryProtocol + "\",\"caseId\":\"" + blindCaseId + "\",\"decision\":\"A|B|INDETERMINATE\",\"evidenceRefs\":[\"source alias\"]}. Cite only aliases in sourceEvidence. Do not rely on labels or information outside this packet.");
        if (request.Body.Contains("trajectory", StringComparison.OrdinalIgnoreCase) || request.Body.Contains("oscillation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BOUNDARY_BIAS_PACKET_CONTAINS_HISTORY");
        var sha = request.Sha256;
        if (budget.Admit(request) is { } refusal) return new(item.Id, labelOrder, "INDETERMINATE", [], 0, null, sha, refusal);
        ct.ThrowIfCancellationRequested();
        var turn = model("LABEL-SWAP-" + blindCaseId + (swap ? "-SWAP" : "-BASE"), 1, request);
        retainPrivate?.Invoke(item.Id + (swap ? "-swap" : "-base"), JsonSerializer.Serialize(new { request, turn }, Json));
        ct.ThrowIfCancellationRequested();
        var bytes = turn.Content is null ? 0 : Encoding.UTF8.GetByteCount(turn.Content);
        if (turn.FinishReason != "stop" || turn.ToolCalls.Count != 0 || turn.Content is null || bytes > 32768)
            return new(item.Id, labelOrder, "INDETERMINATE", [], bytes, turn.FinishReason, sha, "INCOMPLETE_OR_TOOL_CALL");
        try
        {
            var root = JsonNode.Parse(turn.Content)!.AsObject();
            if (root.Count != 4 || root["protocol"]?.GetValue<string>() != BoundaryProtocol || root["caseId"]?.GetValue<string>() != blindCaseId)
                throw new FormatException();
            var choice = root["decision"]!.GetValue<string>();
            var refs = root["evidenceRefs"]!.Deserialize<string[]>(Json)!;
            if (choice is not ("A" or "B" or "INDETERMINATE") ||
                (choice != "INDETERMINATE" && refs.Length == 0) ||
                refs.Distinct(StringComparer.Ordinal).Count() != refs.Length ||
                refs.Any(a => !item.ContextAliases.Contains(a, StringComparer.Ordinal)) ||
                (choice == "INDETERMINATE" && refs.Length != 0)) throw new FormatException();
            var physical = choice == "INDETERMINATE" ? choice :
                (choice == "A") != swap ? "ORIGINAL_A" : "ORIGINAL_B";
            return new(item.Id, labelOrder, physical, refs, bytes, turn.FinishReason, sha, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(item.Id, labelOrder, "INDETERMINATE", [], bytes, turn.FinishReason, sha, "RESPONSE_PROTOCOL_INVALID");
        }
    }

    internal static JsonObject HierarchyPacket(ScanAgentSource source, string child)
    {
        if (child is not ("P008-L018" or "P008-L024")) throw new ArgumentOutOfRangeException(nameof(child));
        return JsonSerializer.SerializeToNode(new
        {
            protocol = HierarchyProtocol,
            qualificationOnly = true,
            sourceSha256 = source.SourceSha256,
            sourceEvidence = SourceComparativeCanaries.PageAliases(8, 13, 27).Select(alias => SourceComparativeCanaries.SourceLine(source, alias)),
            target = new { childAlias = child, candidateParents = new[] { "P008-L016", "P008-L013", "ROOT", "UNRESOLVED" } },
            responseTemplate = new { protocol = HierarchyProtocol, childAlias = child,
                parentAlias = "P008-L016|P008-L013|ROOT|UNRESOLVED", evidenceRefs = new[] { "P008-L016", child } }
        }, Json)!.AsObject();
    }

    internal static (ParentDecision? Decision, string? Error) ParseParent(string content, string child)
    {
        try
        {
            var root = JsonNode.Parse(content)!.AsObject();
            if (root.Count != 4 || root["protocol"]?.GetValue<string>() != HierarchyProtocol || root["childAlias"]?.GetValue<string>() != child)
                throw new FormatException();
            var decision = root.Deserialize<ParentDecision>(Json)!;
            if (decision.ChildAlias != child || decision.ParentAlias is not ("P008-L016" or "P008-L013" or "ROOT" or "UNRESOLVED") ||
                (decision.ParentAlias != "UNRESOLVED" && decision.EvidenceRefs.Length == 0) ||
                decision.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != decision.EvidenceRefs.Length ||
                decision.EvidenceRefs.Any(a => !SourceComparativeCanaries.PageAliases(8, 13, 27).Contains(a, StringComparer.Ordinal)))
                throw new FormatException();
            return (decision, null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return (null, "RESPONSE_PROTOCOL_INVALID"); }
    }

    internal static int Run(string[] args, bool hierarchy)
    {
        var name = hierarchy ? "hierarchy-single-child-canary" : "boundary-label-swap-canary";
        if (args.Length != 4 || args[3] is not ("--live" or "--validate"))
        {
            Console.Error.WriteLine(name + " <source-manifest.json> <new-output-directory> --live|--validate");
            return 2;
        }
        var manifestText = File.ReadAllText(args[1]);
        var manifest = JsonSerializer.Deserialize<BoundaryStaticCanaryCommand.Manifest>(manifestText, Json)!;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        var source = BoundarySuggestedCanaryCommand.LoadSource(manifest, ct);
        if (source.SourceSha256 != SourceComparativeCanaries.ExpectedSourceSha256 ||
            source.SourceAliasUniverseSha256 != SourceComparativeCanaries.ExpectedAliasUniverseSha256 ||
            source.EvidenceStoreSha256 != SourceComparativeCanaries.ExpectedEvidenceStoreSha256)
            throw new InvalidOperationException("PREDECLARED_SOURCE_SNAPSHOT_MISMATCH");
        var output = Path.GetFullPath(args[2]);
        if (!output.Contains(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("DEDICATED_OUTPUT_DIRECTORY_REQUIRED");
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        var postLimit = hierarchy ? 2 : SourceComparativeCanaries.BoundaryCases.Length * 2;
        if (args[3] == "--validate")
        {
            Console.WriteLine($"{name} validated; source={source.SourceSha256}; aliases={source.SourceAliasUniverseSha256}; evidence={source.EvidenceStoreSha256}; posts={postLimit}; no provider calls");
            return 0;
        }
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        var budget = new AgentBudget(postLimit, 0.5);
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, key, budget, cancellationToken: ct);
        AgentUnit.Model model = (unit, turn, request) =>
        {
            Console.WriteLine(unit + " POST " + turn);
            return provider.Turn(unit, turn, request);
        };
        var declaration = JsonSerializer.Serialize(new
        {
            qualificationOnly = true, productionAuthorization = false, manifestSha256 = Sha(manifestText),
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            procedure = hierarchy ? "two fresh single-child parent comparisons; no proposal/current parent/consistency prompt" : "paired label swap; no trajectory or repair history",
            localSourceReviewIsStrictGold = false, maxPosts = postLimit, maxUsd = 0.5
        }, Json);
        File.WriteAllText(Path.Combine(output, "declaration.json"), declaration);
        if (!hierarchy)
        {
            var arms = new List<BoundaryArm>();
            var cases = SourceComparativeCanaries.BoundaryCases;
            for (var i = 0; i < cases.Length; i++)
            {
                // Counterbalance which label ordering receives the first fresh call.
                var orders = i % 2 == 0 ? new[] { false, true } : new[] { true, false };
                foreach (var swap in orders)
                {
                    ct.ThrowIfCancellationRequested();
                    arms.Add(JudgeBoundary(source, cases[i], swap, model, budget, ct,
                        (label, text) => File.WriteAllText(Path.Combine(output, "private", label + ".json"), text)));
                    SaveBoundary();
                }
            }
            void SaveBoundary() => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                qualificationOnly = true, productionAuthorization = false, localSourceReviewIsStrictGold = false,
                source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
                providerPosts = budget.Calls, costUsd = budget.ActualUsd, outcomes = arms,
                labelStability = arms.GroupBy(a => a.Case).Select(g => new { caseId = g.Key,
                    samePhysicalPartition = g.Select(a => a.PhysicalChoice).Distinct(StringComparer.Ordinal).Count() == 1 &&
                        g.First().PhysicalChoice is "ORIGINAL_A" or "ORIGINAL_B" })
            }, Json));
        }
        else
        {
            var outcomes = new List<ParentArm>();
            foreach (var child in new[] { "P008-L018", "P008-L024" })
            {
                var packet = HierarchyPacket(source, child);
                var request = AgentGrouping.BuildRequest(packet.ToJsonString(), [],
                    "Make a fresh independent immediate-parent judgment for this one target only, using the source lines and listed candidate parents. Do not infer from another heading or from any prior proposal. Reply with exactly one JSON object matching responseTemplate. Evidence refs must be supplied source aliases.");
                if (request.Body.Contains("proposedParent", StringComparison.OrdinalIgnoreCase) || request.Body.Contains("consistency", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SINGLE_CHILD_PACKET_CONTAINS_COMPARATIVE_FRAMING");
                if (budget.Admit(request) is { } refusal) throw new InvalidOperationException(refusal);
                ct.ThrowIfCancellationRequested();
                var turn = model("SINGLE-CHILD-" + child, 1, request);
                File.WriteAllText(Path.Combine(output, "private", child + ".json"), JsonSerializer.Serialize(new { request, turn }, Json));
                ct.ThrowIfCancellationRequested();
                var bytes = turn.Content is null ? 0 : Encoding.UTF8.GetByteCount(turn.Content);
                var parsed = turn.FinishReason == "stop" && turn.ToolCalls.Count == 0 && turn.Content is not null && bytes <= 32768
                    ? ParseParent(turn.Content, child) : (null, "INCOMPLETE_OR_TOOL_CALL");
                outcomes.Add(new(child, parsed.Item1?.ParentAlias, parsed.Item1?.EvidenceRefs ?? [], turn.FinishReason,
                    bytes, request.Sha256, parsed.Item2));
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                {
                    qualificationOnly = true, productionAuthorization = false, localSourceReviewIsStrictGold = false,
                    source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
                    providerPosts = budget.Calls, costUsd = budget.ActualUsd, outcomes,
                    offlineComparisonOnly = true,
                    sameParent = outcomes.Count == 2 && outcomes[0].Error is null && outcomes[1].Error is null &&
                        outcomes[0].ParentAlias is not null and not "UNRESOLVED" && outcomes[0].ParentAlias == outcomes[1].ParentAlias
                }, Json));
            }
        }
        Console.WriteLine(name + " complete; no production authority emitted");
        return 0;
    }

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
