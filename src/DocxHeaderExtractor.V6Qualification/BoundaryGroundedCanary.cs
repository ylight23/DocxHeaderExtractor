using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>Qualification D: ground boundary verdicts in the input graph; never emits production headings.</summary>
internal static class BoundaryGroundedCanary
{
    internal const string Version = "QUALIFICATION_GROUNDED_REGROUP_V1";
    internal const int MaxProtocolRounds = 3;
    static readonly string[] RequiredFields = ["run", "decision", "reason", "evidenceRefs", "membership", "currentGroups", "suggestedGroups"];
    internal sealed record Round(int Number, string? FinishReason, int ResponseBytes, string? Decision,
        string[][]? CurrentGroups, string[][]? SuggestedGroups, string? ProtocolError, string[] MissingFields)
    {
        public IReadOnlyDictionary<string, string> Membership { get; init; } = new Dictionary<string, string>();
    }
    internal sealed record Result(string Status, string SeedPacketSha256, string[] Aliases, string[][] InputGroups,
        string[][]? SuggestedGroups, string? IndependentVerdict, bool QualifiedAccept, string? Failure,
        IReadOnlyList<Round> Rounds);
    sealed class ConsistencyException(string code) : Exception(code);
    internal static bool Equal(string[][] a, string[][] b) => a.Length == b.Length &&
        a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal));

    internal static Result Run(IAgentSource source, JsonObject seed, AgentUnit.Model model, AgentBudget budget,
        CancellationToken ct, Action<string, string>? retainPrivate = null)
    {
        var packet = seed["packet"]!.AsObject();
        var target = packet["candidates"]!.AsArray().Single()!.AsObject();
        var groups = target["proposedGroups"]!.Deserialize<string[][]>()!;
        var candidate = new AgentGroupVerification.Candidate(target["run"]!.GetValue<string>(), groups);
        var original = seed["Verdicts"]!.Deserialize<AgentGroupVerification.Verdict[]>()!.Single();
        if (original.Run != candidate.Id || original.Decision != "REGROUP" ||
            !original.Membership.Select(m => m.Alias).SequenceEqual(candidate.Aliases) || original.Membership.Any(m => m.Status != "HEADING"))
            throw new InvalidOperationException("INVALID_FROZEN_REGROUP_SEED");
        return Evaluate(source, packet, seed["extents"]!.Deserialize<Dictionary<string, TextOffsetSpan>>()!,
            seed["readings"]!.Deserialize<Dictionary<string, string>>()!, model, budget, ct, retainPrivate, original);
    }

    internal static Result Evaluate(IAgentSource source, JsonObject packet,
        IReadOnlyDictionary<string, TextOffsetSpan> extents, IReadOnlyDictionary<string, string> readings,
        AgentUnit.Model model, AgentBudget budget, CancellationToken ct, Action<string, string>? retainPrivate = null,
        AgentGroupVerification.Verdict? frozenComplaint = null, bool independentOnly = false)
    {
        if (independentOnly && (frozenComplaint is not null || packet.ContainsKey("frozenComplaint")))
            throw new InvalidOperationException("INDEPENDENT_VERIFIER_MUST_NOT_RECEIVE_COMPLAINT");
        var target = packet["candidates"]!.AsArray().Single()!.AsObject();
        var groups = target["proposedGroups"]!.Deserialize<string[][]>()!;
        var candidate = new AgentGroupVerification.Candidate(target["run"]!.GetValue<string>(), groups);
        ValidatePartition(source, candidate.Aliases, groups);
        var observed = candidate.Aliases.ToHashSet(StringComparer.Ordinal);
        foreach (var line in packet["context"]!.AsArray()) observed.Add(line!["alias"]!.GetValue<string>());
        var sha = Sha(packet.ToJsonString());
        var rounds = new List<Round>();
        Result Done(string status, string[][]? suggestion = null, string? independent = null, bool accept = false, string? failure = null) =>
            new(status, sha, candidate.Aliases, groups, suggestion, independent, accept, failure, rounds.ToArray());
        var groundedPacket = (JsonObject)packet.DeepClone();
        groundedPacket["stage"] = independentOnly ? "QUALIFICATION_GROUNDED_INDEPENDENT" : "QUALIFICATION_GROUNDED_REGROUP";
        groundedPacket.Remove("evidenceTools"); groundedPacket.Remove("scanCropTool");
        if (frozenComplaint is not null) groundedPacket["frozenComplaint"] = JsonSerializer.SerializeToNode(frozenComplaint);
        var responseTemplate = new
        {
            protocolVersion = Version,
            verdicts = new[] { new
            {
                run = candidate.Id, decision = "ACCEPT|REGROUP|MEMBERSHIP_DISPUTE|UNRESOLVED",
                reason = "<1 to 1000 characters>", evidenceRefs = new[] { candidate.Aliases[0] },
                membership = candidate.Aliases.Select(a => new { alias = a, status = "HEADING|NOT_HEADING|UNRESOLVED", evidenceRefs = new[] { a } }),
                currentGroups = groups, suggestedGroups = (string[][]?)null
            } }
        };
        // Retain semantic instructions, but publish ONE complete schema rather than append a conflicting schema extension.
        var semanticPrompt = string.Join("\n", FrozenVerifierV2.Prompt.Split('\n').Where(line =>
            !line.StartsWith("Otherwise return exactly", StringComparison.Ordinal) &&
            !line.StartsWith("You may ask for evidence", StringComparison.Ordinal)));
        var prompt = semanticPrompt + "\nQUALIFICATION CONTRACT: no tools. Return protocolVersion " + Version +
            ". Every verdict MUST contain ALL seven fields: run, decision, reason, evidenceRefs, membership, currentGroups, suggestedGroups. Include complete per-alias membership and evidenceRefs even for ACCEPT; currentGroups and suggestedGroups do not replace them. currentGroups must exactly echo proposedGroups, including ordered group boundaries, for EVERY decision. For REGROUP, suggestedGroups must be a DIFFERENT valid ordered partition covering every candidate alias exactly once, in contiguous nonempty groups of at most 8 lines. For other decisions suggestedGroups must be null. A reason cannot replace structural consistency. If current boundaries are correct, ACCEPT; do not invent boundary changes merely to satisfy REGROUP. Otherwise provide a genuinely different valid partition, or report membership dispute/unresolved as appropriate. A suggestion has no authority until a fresh independent verifier accepts it. Protocol consistency repairs stay in this conversation and do not open a semantic boundary cycle.\nComplete response template (choose ONE decision and ONE membership status per alias; replace suggestedGroups:null with a different valid partition only for REGROUP):\n" + JsonSerializer.Serialize(responseTemplate);
        var history = new JsonArray();
        for (int round = 1; round <= MaxProtocolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var request = AgentGrouping.BuildRequest(groundedPacket.ToJsonString(), history, prompt);
            if (budget.Admit(request) is { } refusal) return Done("UNRESOLVED", failure: refusal);
            ct.ThrowIfCancellationRequested();
            var turn = model(independentOnly ? "E-INDEPENDENT" : "D-VERIFIER", round, request);
            retainPrivate?.Invoke("verifier-round-" + round, JsonSerializer.Serialize(new { request, turn }));
            ct.ThrowIfCancellationRequested();
            var bytes = turn.Content is null ? 0 : Encoding.UTF8.GetByteCount(turn.Content);
            if (turn.FinishReason != "stop" || turn.ToolCalls.Count != 0 || turn.Content is null || bytes > 131072)
            {
                rounds.Add(new(round, turn.FinishReason, bytes, null, null, null, "VERIFIER_INCOMPLETE_OR_TOOL_CALL", []));
                return Done("UNRESOLVED", failure: "VERIFIER_INCOMPLETE_OR_TOOL_CALL");
            }
            string? decision = null;
            string[][]? current = null, suggested = null;
            string? error = null;
            string[] missingFields = [];
            IReadOnlyDictionary<string, string> membership = new Dictionary<string, string>();
            try
            {
                var root = JsonNode.Parse(turn.Content)!.AsObject();
                if (root.Count != 2 || root["protocolVersion"]?.GetValue<string>() != Version || root["verdicts"]!.AsArray().Count != 1)
                    throw new FormatException();
                var verdict = root["verdicts"]![0]!.DeepClone().AsObject();
                missingFields = RequiredFields.Where(field => !verdict.ContainsKey(field)).ToArray();
                if (missingFields.Length != 0) throw new FormatException();
                current = verdict["currentGroups"]!.Deserialize<string[][]>()!;
                suggested = verdict["suggestedGroups"]?.Deserialize<string[][]>();
                if (!verdict.Remove("currentGroups") || !verdict.Remove("suggestedGroups")) throw new FormatException();
                if (verdict["run"]?.GetValue<string>() != candidate.Id) throw new FormatException();
                var diagnostics = new List<AgentGroupVerification.FieldDiagnostic>();
                var parsed = AgentGroupVerification.ParseMembershipVerdict(verdict, candidate, observed, diagnostics, _ => { });
                if (diagnostics.Count != 0) throw new FormatException();
                decision = parsed.Decision;
                membership = parsed.Membership.ToDictionary(m => m.Alias, m => m.Status, StringComparer.Ordinal);
                ValidatePartition(source, candidate.Aliases, current);
                if (!Equal(current, groups)) throw new ConsistencyException("BOUNDARY_CURRENT_GROUPS_MISMATCH");
                if (decision == "REGROUP")
                {
                    if (suggested is null) throw new FormatException();
                    ValidatePartition(source, candidate.Aliases, suggested);
                    if (Equal(suggested, groups)) throw new ConsistencyException("BOUNDARY_VERDICT_SELF_CONTRADICTORY");
                }
                else if (suggested is not null) throw new FormatException();
            }
            catch (ConsistencyException e) { error = e.Message; }
            catch (Exception e) when (e is not OperationCanceledException) { error = "GROUNDED_VERDICT_PROTOCOL_INVALID"; }
            // Only validated source-alias partitions can enter safe observations, even for an invalid response.
            rounds.Add(new(round, turn.FinishReason, bytes, decision, Safe(current), Safe(suggested), error, missingFields)
                { Membership = membership });
            if (error is not null)
            {
                if (round == MaxProtocolRounds) return Done("UNRESOLVED", failure: error);
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Content });
                history.Add(new JsonObject { ["role"] = "user", ["content"] = JsonSerializer.Serialize(new
                {
                    stage = "PROTOCOL_CONSISTENCY_REPAIR", error, missingFields, responseTemplate,
                    run = candidate.Id, proposedGroups = groups,
                    requirement = "Echo exactly this current partition. REGROUP requires a different valid partition. If these boundaries are correct, ACCEPT with suggestedGroups:null; otherwise provide a different valid partition or report dispute/unresolved. Do not alter membership to evade a boundary consistency error."
                }) });
                continue;
            }
            if (decision == "ACCEPT") return Done("GROUNDED_ACCEPT_CURRENT", accept: true);
            if (decision != "REGROUP") return Done("UNRESOLVED", failure: "VERDICT_" + decision);
            // E is the final independent check: a different valid suggestion is a grounded rejection,
            // not permission to apply a second repair or start another verifier conversation.
            if (independentOnly) return Done("GROUNDED_REGROUP_REJECT_CURRENT", suggested);
            ct.ThrowIfCancellationRequested();
            var check = AgentGroupVerification.Ask(source, [new("V1", suggested!)], extents, readings, model, budget, ct, 0,
                source is ScanAgentSource ? ScanAgentProtocol.Instruction : Instruction, "D-INDEPENDENT", 1);
            retainPrivate?.Invoke("independent", JsonSerializer.Serialize(check));
            var independent = check.Verdicts.SingleOrDefault()?.Decision;
            return Done(independent == "ACCEPT" ? "QUALIFIED_ACCEPT_CHANGED" : "UNRESOLVED", suggested, independent,
                independent == "ACCEPT", check.Failure);
        }
        throw new InvalidOperationException("UNREACHABLE");

        string[][]? Safe(string[][]? partition)
        {
            if (partition is null) return null;
            try { ValidatePartition(source, candidate.Aliases, partition); return partition; }
            catch { return null; }
        }
    }

    static void ValidatePartition(IAgentSource source, string[] aliases, string[][] partition)
    {
        var positions = source.Lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
        var run = new AgentGrouping.Run("R1", aliases.Select(a => positions[a]).ToArray());
        if (!run.Lines.SequenceEqual(Enumerable.Range(run.Lines[0], run.Lines.Length))) throw new FormatException();
        var envelope = JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
            runs = new[] { new { run = "R1", headings = partition } } });
        var division = AgentGrouping.Parse(envelope, [run], source, out var failure, out _);
        if (failure is not null || division?.ContainsKey("R1") != true) throw new FormatException();
    }
}
