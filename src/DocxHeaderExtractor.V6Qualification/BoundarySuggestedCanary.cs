using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>Qualification proposal only; even an independently accepted proposal emits no production headings.</summary>
internal static class BoundarySuggestedCanary
{
    internal const string Version = "QUALIFICATION_BOUNDARY_SUGGESTION_V1";
    internal sealed record Result(string Status, string SeedPacketSha256, string[] Aliases, string[][] InputGroups,
        string[][]? SuggestedGroups, bool? Changed, string? IndependentVerdict, bool QualifiedAccept,
        string? Failure);

    internal static Result Run(IAgentSource source, JsonObject seed, AgentUnit.Model model, AgentBudget budget,
        CancellationToken ct, Action<string, string>? retainPrivate = null)
    {
        var packet = seed["packet"]!.AsObject();
        var target = packet["candidates"]!.AsArray().Single()!.AsObject();
        var groups = target["proposedGroups"]!.Deserialize<string[][]>()!;
        var candidate = new AgentGroupVerification.Candidate(target["run"]!.GetValue<string>(), groups);
        var aliases = candidate.Aliases;
        var extents = seed["extents"]!.Deserialize<Dictionary<string, TextOffsetSpan>>()!;
        var readings = seed["readings"]!.Deserialize<Dictionary<string, string>>()!;
        var original = seed["Verdicts"]!.Deserialize<AgentGroupVerification.Verdict[]>()!.Single();
        if (original.Run != candidate.Id || original.Decision != "REGROUP" ||
            !original.Membership.Select(m => m.Alias).SequenceEqual(aliases) || original.Membership.Any(m => m.Status != "HEADING"))
            throw new InvalidOperationException("INVALID_FROZEN_REGROUP_SEED");
        var seedSha = Sha(packet.ToJsonString());
        Result Done(string status, string[][]? suggested = null, bool? changed = null, string? independent = null, string? failure = null) =>
            new(status, seedSha, aliases, groups, suggested, changed, independent, independent == "ACCEPT", failure);
        // A fresh proposer receives the frozen complaint, not a reconstructed or claimed continuation of the old conversation.
        var proposalPacket = (JsonObject)packet.DeepClone();
        proposalPacket["stage"] = "QUALIFICATION_BOUNDARY_SUGGESTION";
        proposalPacket.Remove("evidenceTools"); proposalPacket.Remove("scanCropTool");
        proposalPacket["frozenComplaint"] = JsonSerializer.SerializeToNode(original);
        var prompt = AgentGroupVerification.Prompt + "\nQUALIFICATION OVERRIDE: no tools. Return protocolVersion " + Version +
            ". Keep the verdict schema, and add suggestedGroups to the single verdict. For REGROUP, suggestedGroups is an ordered partition of every candidate alias exactly once, with contiguous nonempty groups of at most 8 lines. Express the structural correction you propose. You may return the same partition if that is your actual conclusion; never invent changes to satisfy this experiment. For other decisions use suggestedGroups:null. Suggestions have no authority and require fresh independent verification.";
        var request = AgentGrouping.BuildRequest(proposalPacket.ToJsonString(), [], prompt);
        if (budget.Admit(request) is { } refusal) return Done("UNRESOLVED", failure: refusal);
        ct.ThrowIfCancellationRequested();
        var turn = model("C-PROPOSER", 1, request);
        retainPrivate?.Invoke("proposal", JsonSerializer.Serialize(new { request, turn }));
        ct.ThrowIfCancellationRequested();
        if (turn.FinishReason != "stop" || turn.ToolCalls.Count != 0 || turn.Content is null)
            return Done("UNRESOLVED", failure: "PROPOSAL_INCOMPLETE_OR_TOOL_CALL");
        string[][] suggested;
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(turn.Content) > 131072) throw new FormatException();
            var root = JsonNode.Parse(turn.Content)!.AsObject();
            if (root.Count != 2 || root["protocolVersion"]?.GetValue<string>() != Version || root["verdicts"]!.AsArray().Count != 1)
                throw new FormatException();
            var verdict = root["verdicts"]![0]!.DeepClone().AsObject();
            var suggestion = verdict["suggestedGroups"]?.DeepClone();
            if (!verdict.Remove("suggestedGroups")) throw new FormatException();
            var observed = new HashSet<string>(aliases, StringComparer.Ordinal);
            foreach (var line in packet["context"]!.AsArray()) observed.Add(line!["alias"]!.GetValue<string>());
            if (verdict["run"]?.GetValue<string>() != candidate.Id) throw new FormatException();
            var diagnostics = new List<AgentGroupVerification.FieldDiagnostic>();
            var parsed = AgentGroupVerification.ParseVerdict(verdict, candidate, observed, diagnostics, _ => { });
            if (diagnostics.Count != 0) throw new FormatException();
            if (parsed.Decision != "REGROUP")
            {
                if (suggestion is not null) throw new FormatException();
                return Done("PROPOSER_" + parsed.Decision);
            }
            suggested = suggestion!.Deserialize<string[][]>()!;
            var positions = source.Lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
            var run = new AgentGrouping.Run("R1", aliases.Select(a => positions[a]).ToArray());
            if (!run.Lines.SequenceEqual(Enumerable.Range(run.Lines[0], run.Lines.Length))) throw new FormatException();
            var envelope = JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = new[] { new { run = "R1", headings = suggested } } });
            var division = AgentGrouping.Parse(envelope, [run], source, out var failure, out _);
            if (failure is not null || division?.ContainsKey("R1") != true) throw new FormatException();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // No raw exception text, source text, or model reason enters the safe result.
            return Done("UNRESOLVED", failure: "SUGGESTION_PROTOCOL_INVALID");
        }
        var changed = !groups.Select(g => string.Join("\n", g)).SequenceEqual(suggested.Select(g => string.Join("\n", g)));
        if (!changed) return Done("SAME_REJECTED_PARTITION", suggested, false);
        ct.ThrowIfCancellationRequested();
        var check = AgentGroupVerification.Ask(source, [new("V1", suggested)], extents, readings, model, budget, ct, 0,
            source is ScanAgentSource ? ScanAgentProtocol.Instruction : Instruction, "C-INDEPENDENT", 1);
        retainPrivate?.Invoke("independent", JsonSerializer.Serialize(check));
        var decision = check.Verdicts.SingleOrDefault()?.Decision;
        return Done(decision == "ACCEPT" ? "QUALIFIED_ACCEPT" : "UNRESOLVED", suggested, true, decision, check.Failure);
    }
}
