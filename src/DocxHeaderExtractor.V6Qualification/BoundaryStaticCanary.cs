using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.HeadingAgent;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>Qualification only. No emitted headings, binding, placement, tools for regroup, or production entry point.</summary>
internal static class BoundaryStaticCanary
{
    internal sealed record Arm(int Pair, string Name, int Order, string FeedbackSha256, string StaticEvidenceSha256, string BasePacketSha256,
        string Result, bool? Changed, IReadOnlyList<IReadOnlyList<string>> OutputGroups, string? IndependentVerdict,
        IReadOnlyList<string> Failures, IReadOnlyList<GroupingVerifierEvidenceObservation> IndependentEvidence);
    internal sealed record Result(string CaseId, string Status, string SourceSha256, string AliasUniverseSha256,
        string EvidenceStoreSha256, string? SeedPacketSha256, string? FeedbackSha256,
        IReadOnlyList<string> Aliases, IReadOnlyList<IReadOnlyList<string>> InputGroups, IReadOnlyList<Arm> Arms);

    internal static Result Run(string caseId, IAgentSource source, string[][] groups,
        IReadOnlyDictionary<string, TextOffsetSpan> extents, IReadOnlyDictionary<string, string> readings,
        AgentUnit.Model model, AgentBudget budget, int pairs, CancellationToken ct, Action<string, string>? retainPrivate = null)
    {
        if (pairs is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(pairs));
        var candidate = new AgentGroupVerification.Candidate("V1", groups.Select(g => g.ToArray()).ToArray());
        PreparedRequest? seedRequest = null;
        AgentUnit.Model seedModel = (unit, turn, request) => { seedRequest ??= request; return model(unit, turn, request); };
        var seed = AgentGroupVerification.Ask(source, [candidate], extents, readings, seedModel, budget, ct, 0,
            InstructionFor(source), caseId + "-C0", 0);
        var packet = seedRequest is null ? null : Packet(seedRequest);
        retainPrivate?.Invoke(caseId + "-seed", JsonSerializer.Serialize(new { packet, seed.Verdicts, seed.Evidence, extents, readings }));
        var verdict = seed.Verdicts.SingleOrDefault(v => v.Run == candidate.Id);
        Result Skipped(string status) => new(caseId, status, source.SourceSha256, source.SourceAliasUniverseSha256,
            source.EvidenceStoreSha256, packet is null ? null : Sha(packet.ToJsonString()), null, candidate.Aliases, candidate.Groups, []);
        if (verdict?.Decision != "REGROUP" || verdict.Membership.Any(m => m.Status != "HEADING"))
            return Skipped(verdict is null ? "NO_VALID_SEED" : "SEED_" + verdict.Decision);
        // Restrict this experiment to static evidence; a tool-informed seed would confound that comparison.
        if (seed.VerifierEvidence?.Requests.Count > 0) return Skipped("SEED_REQUESTED_DYNAMIC_EVIDENCE");
        var feedback = JsonSerializer.SerializeToNode(AgentGroupVerification.BoundaryFeedback(candidate, verdict, 1))!.AsObject();
        var feedbackSha = Sha(feedback.ToJsonString());
        var target = packet!["candidates"]![0]!.AsObject();
        var staticEvidence = new JsonObject
        {
            ["targetFacts"] = target["targetFacts"]!.DeepClone(), ["lines"] = target["lines"]!.DeepClone(),
            ["context"] = packet["context"]!.DeepClone(), ["documentStyles"] = packet["documentStyles"]!.DeepClone()
        };
        var staticSha = Sha(staticEvidence.ToJsonString());
        retainPrivate?.Invoke(caseId + "-frozen", JsonSerializer.Serialize(new { feedback, staticEvidence,
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256 }));
        var arms = new List<Arm>();
        string? sharedBasePacketSha = null;
        for (var pair = 1; pair <= pairs; pair++)
        {
            // Counterbalance order; every pair reuses the SAME frozen C0 verdict, not a resampled seed.
            var order = pair % 2 == 1 ? new[] { "A", "B" } : new[] { "B", "A" };
            for (var index = 0; index < order.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var name = order[index];
                var unit = caseId + "-PAIR-" + pair + "-" + name;
                var armFeedback = (JsonObject)feedback.DeepClone();
                if (name == "B") armFeedback["verifierStaticEvidence"] = staticEvidence.DeepClone();
                string? basePacketSha = null;
                AgentUnit.Model repairModel = (id, turn, request) =>
                {
                    var basePacket = Packet(request);
                    basePacket["verificationFeedback"]!.AsObject().Remove("verifierStaticEvidence");
                    basePacketSha ??= Sha(basePacket.ToJsonString());
                    sharedBasePacketSha ??= basePacketSha;
                    if (basePacketSha != sharedBasePacketSha) throw new InvalidOperationException("PAIR_BASE_PACKET_CHANGED");
                    return model(id, turn, request);
                };
                var repair = AgentGrouping.Group(source, candidate.Aliases.ToHashSet(StringComparer.Ordinal), repairModel, budget,
                    1, ct, 0, InstructionFor(source), unit, armFeedback);
                ct.ThrowIfCancellationRequested();
                var observation = AgentGroupVerification.ObserveBoundaryRepair(unit, caseId + "-C0", candidate, 0, repair);
                AgentGroupVerification.Check? independent = null;
                if (observation.Result == "CHANGED")
                    independent = AgentGroupVerification.Ask(source,
                        [new("V1", repair.Groups.Select(g => g.ToArray()).ToArray())], extents, readings, model, budget, ct, 0,
                        InstructionFor(source), unit + "-INDEPENDENT", 1);
                retainPrivate?.Invoke(unit, JsonSerializer.Serialize(new { repair.Evidence, independent }));
                arms.Add(new(pair, name, index + 1, feedbackSha, staticSha, basePacketSha ?? "not-posted", observation.Result, observation.Changed,
                    observation.OutputGroups, independent?.Verdicts.SingleOrDefault()?.Decision,
                    repair.Failures.Concat(independent?.Failure is { } error ? [error] : []).ToArray(),
                    independent?.VerifierEvidence is { } ev ? [ev] : []));
            }
        }
        if (arms.Where(a => a.BasePacketSha256 != "not-posted").Select(a => a.BasePacketSha256).Distinct().Count() > 1)
            throw new InvalidOperationException("PAIR_BASE_PACKET_CHANGED");
        return new(caseId, "PAIRED", source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            Sha(packet.ToJsonString()), feedbackSha, candidate.Aliases, candidate.Groups, arms);
    }

    internal static JsonObject Packet(PreparedRequest request) =>
        JsonNode.Parse(ContentText(request.Body))!.AsObject();

    /// <summary>The packet text of the second message: a plain string, or the first block of the cache-marked array the group requests send.</summary>
    static string ContentText(string body)
    {
        var content = JsonNode.Parse(body)!["messages"]![1]!["content"]!;
        return content is JsonArray blocks ? blocks[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
    }

    static string InstructionFor(IAgentSource source) => source is DocxHeaderExtractor.HeadingAgent.Scan.ScanAgentSource
        ? DocxHeaderExtractor.HeadingAgent.Scan.ScanAgentProtocol.Instruction : Instruction;
}
