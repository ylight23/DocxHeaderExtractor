using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>Qualification-only source comparisons. Results never authorize production headings or hierarchy edges.</summary>
internal static class SourceComparativeCanaries
{
    internal const string BoundaryProtocol = "QUALIFICATION_SOURCE_BOUNDARY_COMPARISON_V1";
    internal const string HierarchyProtocol = "QUALIFICATION_SOURCE_HIERARCHY_COMPARISON_V1";
    internal const string ExpectedSourceSha256 = "762b29b5c61411349d08858bba9eb3be3f417f00dc02a4c4179ca8400ec006a8";
    internal const string ExpectedAliasUniverseSha256 = "208b8bc6a4770c9e316dc0d6d87c5d943c3d6248b3a166d11ad9c6b5dee898ac";
    internal const string ExpectedEvidenceStoreSha256 = "ab8120f6022869cd301a0e37069ddc835eafd3d9240f73a0ce112a88dceda52a";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record Case(string Id, string[] ContextAliases, string[][] PartitionA, string[][] PartitionB,
        string[] Trajectory);
    internal sealed record BoundaryDecision(string CaseId, string Decision, string[] EvidenceRefs,
        string? FinishReason, int ResponseBytes, string RequestSha256, string? ProtocolError);
    internal sealed record HierarchyDecision(string ChildAlias, string ParentAlias, string[] EvidenceRefs);

    internal static Case[] BoundaryCases =>
    [
        new("P026-OSCILLATION", PageAliases(26, 1, 12), [["P026-L003", "P026-L004"]], [["P026-L003"], ["P026-L004"]], ["A", "B", "A"]),
        new("V17-CONTROL", PageAliases(8, 13, 24), [["P008-L013", "P008-L014", "P008-L015"], ["P008-L016", "P008-L017"], ["P008-L018"]], [["P008-L013", "P008-L014", "P008-L015"], ["P008-L016", "P008-L017", "P008-L018"]], ["A", "B", "A"]),
        new("V24-CONTROL", PageAliases(11, 1, 12), [["P011-L001", "P011-L002"], ["P011-L003", "P011-L004"]], [["P011-L001", "P011-L002", "P011-L003", "P011-L004"]], ["A", "B", "A"])
    ];

    internal static string[] PageAliases(int page, int first, int last) => Enumerable.Range(first, last - first + 1)
        .Select(line => $"P{page:000}-L{line:000}").ToArray();

    internal static JsonObject BoundaryPacket(ScanAgentSource source, Case item)
    {
        var lines = item.ContextAliases.Select(alias => SourceLine(source, alias)).ToArray();
        if (!item.PartitionA.Concat(item.PartitionB).SelectMany(x => x).All(item.ContextAliases.Contains))
            throw new InvalidOperationException("COMPARISON_SCOPE_MISSING_FROM_CONTEXT");
        return JsonSerializer.SerializeToNode(new
        {
            protocol = BoundaryProtocol,
            qualificationOnly = true,
            sourceSha256 = source.SourceSha256,
            caseId = item.Id,
            sourceEvidence = lines,
            trajectory = item.Trajectory,
            alternatives = new { A = item.PartitionA, B = item.PartitionB },
            instruction = "Independently judge which partition best matches the source structure. Use the supplied source lines and layout facts; do not infer from the A/B labels or trajectory. Return A, B, or INDETERMINATE. Reference only aliases in sourceEvidence. This is a local qualification comparison, not a production authorization or Gold label."
        }, Json)!.AsObject();
    }

    internal static object SourceLine(ScanAgentSource source, string alias)
    {
        var index = source.Index(alias);
        var line = source.ScanLines[index];
        return new
        {
            alias,
            page = line.Page,
            text = source.Text(alias),
            readings = source.Readings(alias),
            layout = new { line.Primary.Box.Left, line.Primary.Box.Top, line.Primary.Box.Width, line.Primary.Box.Height, line.PageWidth, line.PageHeight },
            facts = source.Facts[alias]
        };
    }

    internal static BoundaryDecision JudgeBoundary(ScanAgentSource source, Case item, AgentUnit.Model model,
        AgentBudget budget, CancellationToken ct, Action<string, string>? retainPrivate = null)
    {
        var packet = BoundaryPacket(source, item);
        var prompt = "You are a fresh, independent source-structure adjudicator. Compare alternatives from the source itself. The trajectory is context for why this comparison was selected; it is not evidence for either partition. Do not repeat a prior model decision. Reply with one JSON object only: {\"protocol\":\"" + BoundaryProtocol + "\",\"caseId\":\"" + item.Id + "\",\"decision\":\"A|B|INDETERMINATE\",\"evidenceRefs\":[\"source alias\"]}. Evidence refs must be aliases present in sourceEvidence. No prose outside JSON.";
        var request = AgentGrouping.BuildRequest(packet.ToJsonString(), [], prompt);
        var requestSha = request.Sha256;
        if (budget.Admit(request) is { } refusal)
            return new(item.Id, "INDETERMINATE", [], null, 0, requestSha, refusal);
        ct.ThrowIfCancellationRequested();
        var turn = model("SOURCE-BOUNDARY-" + item.Id, 1, request);
        retainPrivate?.Invoke(item.Id, JsonSerializer.Serialize(new { request, turn }, Json));
        ct.ThrowIfCancellationRequested();
        var bytes = turn.Content is null ? 0 : Encoding.UTF8.GetByteCount(turn.Content);
        if (turn.FinishReason != "stop" || turn.ToolCalls.Count != 0 || turn.Content is null || bytes > 32768)
            return new(item.Id, "INDETERMINATE", [], turn.FinishReason, bytes, requestSha, "INCOMPLETE_OR_TOOL_CALL");
        try
        {
            var root = JsonNode.Parse(turn.Content)!.AsObject();
            if (root.Count != 4 || root["protocol"]?.GetValue<string>() != BoundaryProtocol || root["caseId"]?.GetValue<string>() != item.Id)
                throw new FormatException();
            var decision = root["decision"]!.GetValue<string>();
            var refs = root["evidenceRefs"]!.Deserialize<string[]>()!;
            if (decision is not ("A" or "B" or "INDETERMINATE") || (decision != "INDETERMINATE" && refs.Length == 0) || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length ||
                refs.Any(alias => !item.ContextAliases.Contains(alias, StringComparer.Ordinal)) ||
                (decision == "INDETERMINATE" && refs.Length != 0)) throw new FormatException();
            return new(item.Id, decision, refs, turn.FinishReason, bytes, requestSha, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(item.Id, "INDETERMINATE", [], turn.FinishReason, bytes, requestSha, "RESPONSE_PROTOCOL_INVALID");
        }
    }

    internal static JsonObject HierarchyPacket(ScanAgentSource source)
    {
        var aliases = PageAliases(8, 13, 27);
        return JsonSerializer.SerializeToNode(new
        {
            protocol = HierarchyProtocol,
            qualificationOnly = true,
            sourceSha256 = source.SourceSha256,
            sourceEvidence = aliases.Select(alias => SourceLine(source, alias)),
            graph = new
            {
                chapter = new { alias = "P008-L013", proposedParent = "ROOT" },
                section = new { alias = "P008-L016", proposedParent = "P008-L013" },
                children = new[]
                {
                    new { alias = "P008-L018", label = "target 1", proposedParent = "P008-L013", candidateParents = new[] { "P008-L016", "P008-L013", "ROOT" } },
                    new { alias = "P008-L024", label = "target 2", proposedParent = "P008-L016", candidateParents = new[] { "P008-L016", "P008-L013", "ROOT" } }
                }
            },
            responseTemplate = new
            {
                protocol = HierarchyProtocol,
                decisions = new[]
                {
                    new { childAlias = "P008-L018", parentAlias = "P008-L016|P008-L013|ROOT|UNRESOLVED", evidenceRefs = new[] { "P008-L016", "P008-L018" } },
                    new { childAlias = "P008-L024", parentAlias = "P008-L016|P008-L013|ROOT|UNRESOLVED", evidenceRefs = new[] { "P008-L016", "P008-L024" } }
                },
                consistencyNote = "SAME_PARENT|DIFFERENT_PARENT"
            },
            instruction = "Independently compare the immediate-parent choices for both target headings using the visible source text, layout facts, neighboring structure, and graph. Choose one listed candidate parent for each child or UNRESOLVED. Do not use numbering/type heuristics alone. Return exactly one JSON object matching responseTemplate, with one decision per target. References must be supplied source aliases; UNRESOLVED may use an empty evidenceRefs array. This is a qualification comparison only; it must not authorize or write any production edge."
        }, Json)!.AsObject();
    }

    internal static (HierarchyDecision[] Decisions, string Consistency, string? Error) ParseHierarchy(string content)
    {
        try
        {
            var root = JsonNode.Parse(content)!.AsObject();
            if (root.Count != 3 || root["protocol"]?.GetValue<string>() != HierarchyProtocol) throw new FormatException();
            var decisions = root["decisions"]!.Deserialize<HierarchyDecision[]>(Json)!;
            if (!decisions.Select(d => d.ChildAlias).Order(StringComparer.Ordinal).SequenceEqual(new[] { "P008-L018", "P008-L024" })) throw new FormatException();
            var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["P008-L018"] = ["P008-L016", "P008-L013", "ROOT", "UNRESOLVED"],
                ["P008-L024"] = ["P008-L016", "P008-L013", "ROOT", "UNRESOLVED"]
            };
            if (decisions.Any(d => !allowed[d.ChildAlias].Contains(d.ParentAlias, StringComparer.Ordinal) ||
                (d.ParentAlias != "UNRESOLVED" && d.EvidenceRefs.Length == 0) ||
                d.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != d.EvidenceRefs.Length ||
                d.EvidenceRefs.Any(a => !PageAliases(8, 13, 27).Contains(a, StringComparer.Ordinal)))) throw new FormatException();
            var consistency = root["consistencyNote"]!.GetValue<string>();
            var same = decisions[0].ParentAlias == decisions[1].ParentAlias;
            if (consistency != (same ? "SAME_PARENT" : "DIFFERENT_PARENT")) throw new FormatException();
            return (decisions, consistency, null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return ([], "UNRESOLVED", "RESPONSE_PROTOCOL_INVALID"); }
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static int Run(string[] args, bool hierarchy)
    {
        var name = hierarchy ? "hierarchy-comparative-canary" : "boundary-oscillation-canary";
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
        if (source.SourceSha256 != ExpectedSourceSha256 || source.SourceAliasUniverseSha256 != ExpectedAliasUniverseSha256 ||
            source.EvidenceStoreSha256 != ExpectedEvidenceStoreSha256)
            throw new InvalidOperationException("PREDECLARED_SOURCE_SNAPSHOT_MISMATCH");
        var output = Path.GetFullPath(args[2]);
        var expectedName = hierarchy ? "hierarchy-comparative-canary" : "boundary-oscillation-canary";
        if (!output.Contains(expectedName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("DEDICATED_OUTPUT_DIRECTORY_REQUIRED");
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        var inputs = hierarchy ? 1 : BoundaryCases.Length;
        if (args[3] == "--validate")
        {
            Console.WriteLine($"{name} validated; source={source.SourceSha256}; aliases={source.SourceAliasUniverseSha256}; evidence={source.EvidenceStoreSha256}; cases={inputs}; no provider calls");
            return 0;
        }
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "declaration.json"), JsonSerializer.Serialize(new
        {
            qualificationOnly = true, productionAuthorization = false, sourceManifestSha256 = Sha(manifestText),
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            cases = hierarchy ? ["P008-L018", "P008-L024"] : BoundaryCases.Select(c => c.Id).ToArray(),
            localSourceReviewIsStrictGold = false, maxPosts = inputs, maxUsd = 0.5
        }, Json));
        var budget = new AgentBudget(inputs, 0.5);
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, key, budget, cancellationToken: ct);
        AgentUnit.Model model = (unit, turn, request) =>
        {
            Console.WriteLine(unit + " POST " + turn);
            return provider.Turn(unit, turn, request);
        };
        if (!hierarchy)
        {
            var outcomes = new List<BoundaryDecision>();
            foreach (var item in BoundaryCases)
            {
                ct.ThrowIfCancellationRequested();
                outcomes.Add(JudgeBoundary(source, item, model, budget, ct,
                    (label, text) => File.WriteAllText(Path.Combine(output, "private", label + ".json"), text)));
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                {
                    qualificationOnly = true, productionAuthorization = false, localSourceReviewIsStrictGold = false,
                    source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
                    providerPosts = budget.Calls, costUsd = budget.ActualUsd, outcomes
                }, Json));
            }
        }
        else
        {
            var packet = HierarchyPacket(source);
            var prompt = "You are a fresh, independent comparative hierarchy adjudicator. Evaluate both child-parent edges jointly from the supplied source and graph. Do not merely inherit the proposals. Reply with the exact required JSON schema in the packet; evidenceRefs must point to supplied source aliases. No prose outside JSON.";
            var request = AgentGrouping.BuildRequest(packet.ToJsonString(), [], prompt);
            if (budget.Admit(request) is { } refusal) throw new InvalidOperationException(refusal);
            ct.ThrowIfCancellationRequested();
            var turn = model("SOURCE-HIERARCHY-COMPARISON", 1, request);
            File.WriteAllText(Path.Combine(output, "private", "hierarchy.json"), JsonSerializer.Serialize(new { request, turn }, Json));
            ct.ThrowIfCancellationRequested();
            var bytes = turn.Content is null ? 0 : Encoding.UTF8.GetByteCount(turn.Content);
            var parsed = turn.FinishReason == "stop" && turn.ToolCalls.Count == 0 && turn.Content is not null && bytes <= 32768
                ? ParseHierarchy(turn.Content) : (Array.Empty<HierarchyDecision>(), "UNRESOLVED", "INCOMPLETE_OR_TOOL_CALL");
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                qualificationOnly = true, productionAuthorization = false, localSourceReviewIsStrictGold = false,
                source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
                providerPosts = budget.Calls, costUsd = budget.ActualUsd, finishReason = turn.FinishReason,
                responseBytes = bytes, requestSha256 = request.Sha256,
                sameParentForBoth = parsed.Item3 is null && parsed.Item2 == "SAME_PARENT",
                decisions = parsed.Item1, consistency = parsed.Item2, protocolError = parsed.Item3
            }, Json));
        }
        Console.WriteLine(name + " complete; qualification only; no production authority emitted");
        return 0;
    }
}
