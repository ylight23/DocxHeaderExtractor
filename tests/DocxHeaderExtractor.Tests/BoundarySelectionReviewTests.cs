using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundarySelectionReviewTests
{
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
        instruction: PdfMembershipInstructionV3.Instruction, sourceBound: true)!);

    internal static AgentUnitOutcome[] Outcomes(AgentSource s) => Enumerable.Range(0, s.Cores.Count).Select(i => new AgentUnitOutcome(
        AgentSource.SegmentId(i), "COMPLETE", null, s.Scope(i).Decide.ToDictionary(a => a, _ => "NOT_SELECT"),
        1, 0, 0, false, null, null, null, 0, 0, [])).ToArray();

    [Fact]
    public void Adjacent_left_aligned_parent_and_child_are_not_routed_as_a_centered_name_cluster()
    {
        var source = AgentSource.Load(TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf"),
            out _, instruction: PdfMembershipInstructionV9.Instruction, sourceBound: true, preloadPageLayoutEvidence: true)!;
        var targets = source.Scope(12).Decide;
        var outcome = Outcomes(source)[12] with { Decisions = targets.ToDictionary(a => a, a => targets.Take(2).Contains(a) ? "SELECT" : "NOT_SELECT") };
        Assert.Empty(BoundarySelectionReview.NameCandidates(source, outcome));
    }

    [Fact]
    public void Name_review_can_only_remove_opening_candidates_and_cannot_add_or_change_other_membership()
    {
        var source = AgentSource.Load(TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"),
            out _, instruction: PdfMembershipInstructionV9.Instruction, sourceBound: true, preloadPageLayoutEvidence: true)!;
        var outcomes = Outcomes(source);
        var plan = BoundarySelectionReview.Plan(source, outcomes);
        foreach (var i in plan)
        {
            var targets = source.Scope(i).Decide;
            outcomes[i] = outcomes[i] with { Decisions = targets.ToDictionary(a => a,
                a => targets.Take(3).Contains(a) || a == targets[^1] ? "SELECT" : "NOT_SELECT") };
        }
        var snapshot = outcomes.ToArray();
        ModelTurn Model(string _, int turn, PreparedRequest request)
        {
            var body = JsonNode.Parse(request.Body)!;
            Assert.Equal(2, body["messages"]!.AsArray().Count);
            var packet = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal(3, packet["selectionReview"]!["candidateAliases"]!.AsArray().Count);
            Assert.Null(packet["selectionReview"]!["previousDecisions"]);
            var targets = packet["targets"]!.AsArray();
            Assert.Equal(3, targets.Count);
            return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                decisions = targets.Select(t => new { alias = t!["alias"]!.GetValue<string>(), decision = "NOT_SELECT" }) }), [], "stop");
        }
        var result = BoundarySelectionReview.Apply(source, outcomes, Model,
            AgentSelector.BudgetFor(source, PdfMembershipInstructionV9.Instruction, boundaryReview: true), 6,
            PdfMembershipInstructionV9.Instruction, CancellationToken.None);
        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Events.Length);
        foreach (var i in plan)
        {
            var targets = source.Scope(i).Decide;
            Assert.All(targets.Take(3), a => Assert.Equal("NOT_SELECT", outcomes[i].Decisions[a]));
            Assert.Equal("SELECT", outcomes[i].Decisions[targets[^1]]);
            Assert.Equal("NOT_SELECT", outcomes[i].Decisions[targets[4]]);
        }
        Assert.All(Enumerable.Range(0, outcomes.Length).Where(i => !plan.Contains(i)), i => Assert.Same(snapshot[i], outcomes[i]));
    }

    [Fact]
    public void Routing_is_bounded_and_complete_model_decisions_are_applied_only_to_their_own_scope()
    {
        var s = Source.Value; var outcomes = Outcomes(s); var plan = BoundarySelectionReview.Plan(s, outcomes);
        Assert.Equal(2, plan.Length); Assert.Equal(0, plan[0]);
        var snapshots = outcomes.ToArray(); var calls = new ConcurrentBag<string>();
        ModelTurn Model(string unit, int turn, PreparedRequest request)
        {
            calls.Add(unit);
            var p = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.NotNull(p["selectionReview"]);
            var targets = p["targets"]!.AsArray(); var selected = targets[0]!["alias"]!.GetValue<string>();
            return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                decisions = targets.Select(t => t!["alias"]!.GetValue<string>() == selected
                    ? (object)new { alias = selected, decision = "SELECT", sourceText = t["text"]!.GetValue<string>(), evidenceRefs = new[] { selected } }
                    : new { alias = t["alias"]!.GetValue<string>(), decision = "NOT_SELECT" }) }), [], "stop");
        }
        var (events, failures) = BoundarySelectionReview.Apply(s, outcomes, Model,
            AgentSelector.BudgetFor(s, PdfMembershipInstructionV3.Instruction, boundaryReview: true), 6,
            PdfMembershipInstructionV3.Instruction, CancellationToken.None);
        Assert.Equal(2, calls.Count); Assert.Equal(2, events.Length); Assert.Empty(failures);
        foreach (var i in Enumerable.Range(0, outcomes.Length))
        {
            if (!plan.Contains(i)) Assert.Same(snapshots[i], outcomes[i]);
            else
            {
                Assert.Equal(AgentSource.SegmentId(i), outcomes[i].UnitId);
                Assert.Single(outcomes[i].Decisions.Where(d => d.Value == "SELECT"));
                Assert.Equal(s.Scope(i).Decide.ToHashSet(), outcomes[i].Decisions.Keys.ToHashSet());
            }
        }
    }

    [Fact]
    public void Invalid_or_incomplete_review_is_bounded_and_keeps_the_original_decisions()
    {
        var s = Source.Value; var outcomes = Outcomes(s); var snapshots = outcomes.ToArray(); var calls = 0;
        var (events, failures) = BoundarySelectionReview.Apply(s, outcomes, (_, _, _) =>
        { Interlocked.Increment(ref calls); return new("{}", [], "stop"); },
            AgentSelector.BudgetFor(s, PdfMembershipInstructionV3.Instruction, boundaryReview: true), 6,
            PdfMembershipInstructionV3.Instruction, CancellationToken.None);
        Assert.InRange(calls, 2, BoundarySelectionReview.MaxScopes * BoundarySelectionReview.MaxPosts);
        Assert.Equal(2, failures.Length);
        Assert.All(Enumerable.Range(0, outcomes.Length), i => Assert.Same(snapshots[i], outcomes[i]));
        Assert.All(events, e => Assert.False(JsonNode.Parse(e)!["applied"]!.GetValue<bool>()));
    }

    [Fact]
    public void Incomplete_original_scopes_are_not_reconstructed_by_review()
    {
        var s = Source.Value; var outcomes = Outcomes(s);
        for (var i = 0; i < outcomes.Length; i++) outcomes[i] = outcomes[i] with { Status = "INCOMPLETE" };
        var result = BoundarySelectionReview.Apply(s, outcomes, (_, _, _) => throw new Exception("must not call"),
            AgentSelector.BudgetFor(s), 6, PdfMembershipInstructionV3.Instruction, CancellationToken.None);
        Assert.Empty(result.Events); Assert.Empty(result.Failures);
    }

    [Fact]
    public void Review_is_opt_in_and_V3_retains_an_explicit_custom_instruction()
    {
        Assert.Throws<ArgumentException>(() => new PdfSelector(boundaryReview: true));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 3));
        Assert.Equal(PdfMembershipInstructionV3.Instruction, PdfSelector.ResolveInstruction(null, true, 3));
        Assert.Equal(PdfMembershipInstructionV4.Instruction, PdfSelector.ResolveInstruction(null, true, 4));
        Assert.Equal(PdfMembershipInstructionV5.Instruction, PdfSelector.ResolveInstruction(null, true, 5));
        Assert.Equal(PdfMembershipInstructionV6.Instruction, PdfSelector.ResolveInstruction(null, true, 6));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 6));
        Assert.Equal(PdfMembershipInstructionV7.Instruction, PdfSelector.ResolveInstruction(null, true, 7));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 7));
        Assert.Equal(PdfMembershipInstructionV8.Instruction, PdfSelector.ResolveInstruction(null, true, 8));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 8));
        Assert.Equal(PdfMembershipInstructionV9.Instruction, PdfSelector.ResolveInstruction(null, true, 9));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 9));
        Assert.Equal(PdfMembershipInstructionV10.Instruction, PdfSelector.ResolveInstruction(null, true, 10));
        Assert.Equal(PdfMembershipInstructionV12.Instruction, PdfSelector.ResolveInstruction(null, true, 12));
        Assert.Equal(PdfMembershipInstructionV13.Instruction, PdfSelector.ResolveInstruction(null, true, 13));
        Assert.Equal("Custom", PdfSelector.ResolveInstruction("Custom", true, 10));
        Assert.NotEqual(new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 3).SelectorId,
            new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 3, boundaryReview: true).SelectorId);
        Assert.DoesNotContain("L0513", PdfMembershipInstructionV3.Instruction);
    }

    [Fact]
    public void V10_is_pdf_only_opt_in_and_has_no_qualification_document_or_content_type_rule()
    {
        var selector = new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 10);
        Assert.Contains("pdf-preflight-v2:", selector.SelectorId);
        Assert.Equal(14, new DocxHeaderExtractor.DocumentProcessing.Harness.PipelineOptions().PdfMembershipInstructionVersion);
        Assert.DoesNotContain("072", PdfMembershipInstructionV10.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("023", PdfMembershipInstructionV10.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("participant", PdfMembershipInstructionV10.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("meeting", PdfMembershipInstructionV10.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", PdfMembershipInstructionV10.Instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V12_reuses_the_docx_structural_policy_without_changing_the_source_bound_contract()
    {
        var selector = new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 12);
        Assert.Equal(SourceBoundSelection.Version, AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
            instruction: PdfMembershipInstructionV12.Instruction, sourceBound: true)!.Flavor.AnswerVersion);
        Assert.DoesNotContain("participant", PdfMembershipInstructionV12.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("meeting", PdfMembershipInstructionV12.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", PdfMembershipInstructionV12.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":role-bound-", selector.SelectorId, StringComparison.Ordinal);
        Assert.Equal(14, new DocxHeaderExtractor.DocumentProcessing.Harness.PipelineOptions().PdfMembershipInstructionVersion);
    }

    [Fact]
    public void V13_adds_only_parser_layout_block_measurements_to_the_existing_source_bound_request()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
            instruction: PdfMembershipInstructionV13.Instruction, sourceBound: true, includeLayoutBlockEvidence: true)!;
        var request = System.Text.Json.Nodes.JsonNode.Parse(AgentUnit.PrepareFirstRequest(source, 0, PdfMembershipInstructionV13.Instruction).Body)!;
        var packet = System.Text.Json.Nodes.JsonNode.Parse(request["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
        var fact = packet["targetFacts"]!.AsArray()[0]!.AsObject();
        Assert.Equal(SourceBoundSelection.Version, source.Flavor.AnswerVersion);
        Assert.Equal("NONE", fact["layoutBlockEvidence"]!["semanticLabels"]!.GetValue<string>());
        Assert.Equal(fact["alias"]!.GetValue<string>(), fact["layoutBlockEvidence"]!["memberAliases"]!.AsArray()[0]!.GetValue<string>());
        // Source prose may legitimately contain "role". Check protocol fields, not document text.
        bool HasRoleField(JsonNode? node) => node switch
        {
            JsonObject obj => obj.Any(p => p.Key.Equals("role", StringComparison.OrdinalIgnoreCase) || HasRoleField(p.Value)),
            JsonArray array => array.Any(HasRoleField),
            _ => false
        };
        Assert.False(HasRoleField(packet));
    }

    [Fact]
    public void Parser_selection_units_are_contiguous_geometry_regions_and_keep_original_aliases()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _, sourceBound: true, unitSelection: true)!;
        var units = PdfSelectionUnits.Build(source);
        Assert.Equal(source.Lines.Count, units.SelectMany(x => x.Members).Distinct().Count());
        Assert.Equal(source.Lines.Count, units.SelectMany(x => x.Members).Count());
        Assert.All(units, unit =>
        {
            Assert.Equal(unit.Members[0], unit.Members.Min());
            Assert.Equal(unit.Members[^1], unit.Members.Max());
            Assert.All(unit.Members, member => Assert.Equal(source.Lines[unit.Members[0]].Page, source.Lines[member].Page));
            Assert.True(unit.Kind is "paragraph-region" or "table-region");
            Assert.StartsWith("L", unit.FirstAlias(source), StringComparison.Ordinal);
            if (unit.Kind == "paragraph-region" && unit.Geometry["rows"]!.GetValue<int>() > 1)
                Assert.Single(unit.Members.Select(i => source.Blocks[i]).Distinct(StringComparer.Ordinal));
        });
        foreach (var alias in new[] { "L0123:S0", "L0137:S0", "L0150:S0", "L0288:S0" })
        {
            var unit = Assert.Single(units.Where(u => u.Members.Any(i => source.Lines[i].Alias == alias)));
            Assert.Equal("paragraph-region", unit.Kind);
            Assert.Equal([alias], unit.Members.Select(i => source.Lines[i].Alias));
        }
        var wrappedParagraph = Assert.Single(units.Where(u => u.Members.Select(i => source.Lines[i].Alias)
            .Contains("L0127:S0", StringComparer.Ordinal)));
        Assert.Contains("L0128:S0", wrappedParagraph.Members.Select(i => source.Lines[i].Alias));
        Assert.Contains("L0129:S0", wrappedParagraph.Members.Select(i => source.Lines[i].Alias));
        Assert.Contains(units, unit => unit.Members.Select(i => source.Lines[i].Alias).ToHashSet().IsSupersetOf(["L0359:S0", "L0360:S0"]));
        Assert.Contains(units, unit => unit.Kind == "table-region" && unit.Members.Any(i => source.Lines[i].Page == 13));
        Assert.Contains(units, unit => unit.Kind == "table-region" && unit.Members.Any(i => source.Lines[i].Page == 14));
        foreach (var core in source.Cores)
            Assert.Equal(source.Lines.Skip(core.Start).Take(core.End - core.Start).Select(x => x.Alias),
                PdfSelectionUnits.ForCore(source, units, core).SelectMany(x => x.Members).Select(i => source.Lines[i].Alias));
    }

    [Fact]
    public void Pdf_table_regions_stop_when_the_repeated_column_grid_ends()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf"), out _,
            sourceBound: true, unitSelection: true)!;
        var units = PdfSelectionUnits.Build(source);
        var metadata = Assert.Single(units.Where(u => u.Members.Any(i => source.Lines[i].Alias == "L0000:S0")));
        Assert.Equal("table-region", metadata.Kind);
        Assert.Equal(6, metadata.Geometry["rows"]!.GetValue<int>());
        Assert.Contains("L0005:S1", metadata.Members.Select(i => source.Lines[i].Alias));
        Assert.DoesNotContain("L0007:S0", metadata.Members.Select(i => source.Lines[i].Alias));

        var coverTitle = Assert.Single(units.Where(u => u.Members.Any(i => source.Lines[i].Alias == "L0007:S0")));
        Assert.Equal("paragraph-region", coverTitle.Kind);
        Assert.Contains("L0008:S0", coverTitle.Members.Select(i => source.Lines[i].Alias));

        var indexHeading = Assert.Single(units.Where(u => u.Members.Any(i => source.Lines[i].Alias == "L1961:S0")));
        Assert.Equal("paragraph-region", indexHeading.Kind);
        Assert.Equal(1, indexHeading.Geometry["rows"]!.GetValue<int>());
    }

    [Fact]
    public void V14_makes_structural_geometry_units_the_only_selection_targets_but_returns_original_aliases()
    {
        var source = AgentSource.Load(TestRepository.Path(
            "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"), out _,
            instruction: PdfMembershipInstructionV12.Instruction, sourceBound: true, unitSelection: true)!;
        ModelTurn Model(string unitId, int turn, PreparedRequest request)
        {
            var body = JsonNode.Parse(request.Body)!;
            Assert.Equal(PdfUnitSelectionProtocol.SystemPrompt, body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            var packet = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Null(packet["targets"]);
            Assert.Equal(PdfSelectionUnits.Version, packet["unitProtocol"]!.GetValue<string>());
            var units = packet["targetUnits"]!.AsArray();
            Assert.All(units, unit => Assert.NotNull(unit!["geometryKind"]));
            return new(JsonSerializer.Serialize(new
            {
                protocolVersion = PdfUnitSelectionProtocol.Version,
                status = "COMPLETE",
                decisions = units.Select(u => new { unitId = u!["unitId"]!.GetValue<string>(), decision = "NOT_SELECT", selected = Array.Empty<object>(),
                    reason = "Continues surrounding prose and does not introduce a separate content unit." })
            }), [], "stop");
        }
        var result = PdfUnitSelectionTask.Run(source, 0, Model,
            AgentSelector.BudgetFor(source, PdfMembershipInstructionV12.Instruction, unitSelection: true), 0,
            PdfMembershipInstructionV12.Instruction);
        Assert.Equal("COMPLETE", result.Status);
        Assert.All(result.Decisions.Values, value => Assert.Equal("NOT_SELECT", value));
        Assert.NotNull(result.Reasons);
        Assert.All(result.Reasons!.Values, reason => Assert.Equal("Continues surrounding prose and does not introduce a separate content unit.", reason));
        Assert.Equal(source.Scope(0).Decide.OrderBy(x => x), result.Decisions.Keys.OrderBy(x => x));
        var selector = new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 14);
        Assert.Contains(":structural-geometry-selection-units-v2", selector.SelectorId, StringComparison.Ordinal);
        Assert.Contains(":unit-rejection-reason-v1", selector.SelectorId, StringComparison.Ordinal);
        Assert.Equal(14, new DocxHeaderExtractor.DocumentProcessing.Harness.PipelineOptions().PdfMembershipInstructionVersion);
    }

}
