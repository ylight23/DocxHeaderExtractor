using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

public sealed class ReasoningRetentionHarnessTests
{
    [Fact]
    public void Full_context_keeps_non_candidate_source_occurrences()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "Heading", null, (int?)null),
            (1, "body prose that is not a candidate", null, (int?)null),
        ]);

        var pack = ReasoningContextBuilder.Build(state.Source, state);

        Assert.Equal(2, pack.Occurrences.Count);
        Assert.Equal(1d, pack.SourceOccurrenceCoverage);
        Assert.Contains(pack.Occurrences, item => item.RawText.Contains("body prose", StringComparison.Ordinal));
        Assert.False(pack.Occurrences.Single(item => item.SourceOrdinal == 1).CandidateHint.Candidate);
    }

    [Fact]
    public void Candidate_signals_are_hints_and_do_not_filter_context()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "Normal paragraph", null, (int?)null),
        ]);
        var pack = ReasoningContextBuilder.Build(state.Source, state);
        var serialized = string.Join('\n', pack.Segments.Select(item => item.Text));

        Assert.Contains("candidateHint", serialized, StringComparison.Ordinal);
        Assert.Contains("Normal paragraph", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("gold", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_occurrence_has_exactly_once_utf16_ownership_and_overlapping_halo()
    {
        var state = NativePolicyStateFactory.Create([(0, new string('x', 101), null, (int?)null)]);
        var pack = ReasoningContextBuilder.Build(
            state.Source,
            state,
            maxContextCharacters: 20,
            windowCharacters: 20,
            expandOwnedPerOccurrence: false);

        var ranges = pack.Segments
            .OrderBy(segment => segment.OwnedStartCharacter)
            .Select(segment => (OwnedStart: segment.OwnedStartCharacter!.Value, OwnedEnd: segment.OwnedEndCharacter!.Value,
                VisibleStart: segment.VisibleStartCharacter!.Value, VisibleEnd: segment.VisibleEndCharacter!.Value))
            .ToArray();
        Assert.True(ranges.Length > 1);
        Assert.Equal(0, ranges[0].OwnedStart);
        Assert.Equal(101, ranges[^1].OwnedEnd);
        Assert.All(ranges.Zip(ranges.Skip(1)), pair => Assert.Equal(pair.First.OwnedEnd, pair.Second.OwnedStart));
        Assert.Contains(ranges.Zip(ranges.Skip(1)), pair => pair.First.VisibleEnd > pair.Second.VisibleStart);
    }

    [Fact]
    public void Character_ownership_uses_utf16_offsets_for_supplementary_characters()
    {
        var text = "A😀BC";
        var state = NativePolicyStateFactory.Create([(0, text, null, (int?)null)]);
        var pack = ReasoningContextBuilder.Build(state.Source, state, 3, 3, expandOwnedPerOccurrence: false);
        var owned = pack.Segments.Select(segment => (segment.OwnedStartCharacter!.Value, segment.OwnedEndCharacter!.Value)).ToArray();

        Assert.Equal(text.Length, 5);
        Assert.Equal((0, 2), owned[0]);
        Assert.Equal((2, 4), owned[1]);
        Assert.Equal((4, 5), owned[2]);
    }

    [Fact]
    public void Hard_validator_rejects_invalid_span_but_bad_model_level_cannot_delete()
    {
        var issues = ReasoningHardInvariantValidator.Validate(
            Proposal("p[0]", "Heading", 0, 99, 10),
            new Dictionary<string, string> { ["p[0]"] = "Heading" });

        Assert.Contains("source-span-invalid", issues);
        Assert.DoesNotContain("level-out-of-range", issues);
    }

    [Fact]
    public void Hard_validator_does_not_reject_normal_style_nonbold_or_low_candidate_score()
    {
        var proposal = Proposal("p[0]", "Normal heading", 0, 14, 1);
        var issues = ReasoningHardInvariantValidator.Validate(
            proposal,
            new Dictionary<string, string> { ["p[0]"] = "Normal heading" });

        Assert.Empty(issues);
    }

    [Fact]
    public void Materializer_keeps_semantic_proposal_separate_from_content_projection()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "Agenda Items", null, (int?)null),
        ]);
        var proposal = Proposal("p[0]", "Agenda Items", 0, 12, 2, "AGENDA_NAVIGATION_HEADING");

        var (structure, validated) = ReasoningProposalMaterializer.Materialize(
            state.Source,
            state,
            [proposal]);

        Assert.True(Assert.Single(validated).Accepted);
        Assert.Single(structure.Elements);
        Assert.Empty(ReasoningTaskProjection.ProjectContentHeadings(structure));
    }

    [Fact]
    public void Materializer_does_not_first_win_conflicting_same_span_payloads()
    {
        var state = NativePolicyStateFactory.Create([(0, "Heading", null, (int?)null)]);
        var proposals = new[]
        {
            Proposal("p[0]", "Heading", 0, 7, 1, "SECTION"),
            Proposal("p[0]", "Heading", 0, 7, 1, "CONTENT_HEADING"),
        };

        var (structure, validated) = ReasoningProposalMaterializer.Materialize(state.Source, state, proposals);

        Assert.Single(structure.Elements);
        var row = Assert.Single(validated);
        Assert.True(row.Accepted);
        Assert.Equal("ROLE_CONFLICT", row.ConflictStatus);
    }

    [Fact]
    public void Bad_model_level_is_derived_from_validated_parent_graph()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "Parent", null, (int?)null),
            (1, "Child", null, (int?)null),
        ]);
        var proposals = new[]
        {
            Proposal("p[0]", "Parent", 0, 6, 99),
            Proposal("p[1]", "Child", 0, 5, -4) with { ProposedParent = "reasoning:p[0]:0:6" },
        };

        var (structure, validated) = ReasoningProposalMaterializer.Materialize(state.Source, state, proposals);

        Assert.Equal(2, structure.Elements.Count);
        Assert.All(validated, item => Assert.True(item.Accepted));
        Assert.Equal(1, structure.Elements.Single(item => item.Sources.Single().SourceId == "p[0]").Level);
        Assert.Equal(2, structure.Elements.Single(item => item.Sources.Single().SourceId == "p[1]").Level);
    }

    [Fact]
    public void Hard_validator_rejects_self_parent_and_cycle()
    {
        var first = new ValidatedStructuralElement
        {
            Id = "a",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference("p[0]", 0, new StructuralSpan(0, 1))],
            Text = "A",
            ParentId = "b",
            Validation = Validated(),
            Decision = Decision(),
        };
        var second = first with
        {
            Id = "b",
            Sources = [new SourceReference("p[1]", 1, new StructuralSpan(0, 1))],
            Text = "B",
            ParentId = "a",
        };
        var cycle = ValidatedStructure.FromElements([first, second]);
        Assert.Contains("parent-cycle", ReasoningHardInvariantValidator.ValidateStructure(cycle));
    }

    [Fact]
    public async Task Harness_exposes_all_source_ids_to_model_even_when_candidates_are_empty()
    {
        var state = NativePolicyStateFactory.Create([
            (0, "Not a candidate", null, (int?)null),
            (1, "A heading", null, (int?)null),
        ]);
        var model = new FixedReasoningModel(request =>
            request.OwnedOutputScope.CanonicalSourceId == "p[1]"
                ? new ReasoningModelResponse(
                    [new ReasoningModelHeadingProposal
                    {
                        Start = 0,
                        End = 9,
                        SemanticRole = "CONTENT_HEADING",
                        ProposedLevel = 1,
                    }],
                    [])
                : new ReasoningModelResponse([], []));
        var harness = new ReasoningPreservingHeadingHarness(model);

        var observation = await harness.RunAsync(
            state.Source,
            state,
            ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(2, observation.ContextVisible.Count);
        Assert.Contains(observation.ContextVisible, id => id.Contains("p[0]", StringComparison.Ordinal));
        Assert.Single(observation.Validated);
        Assert.Equal(2, model.ProviderCalls);
    }

    [Fact]
    public void Prompt_parser_accepts_rich_roles_without_private_reasoning()
    {
        var raw = """
        {"schemaVersion":"a99-reasoning-bounded-v2","headings":[{"start":0,"end":7,"semanticRole":"CONTENT_HEADING","proposedLevel":1,"proposedParentLocalId":null,"confidence":0.9,"decisionEvidence":[{"evidenceType":"semantic","sourceReference":"context","shortEvidenceCode":"TOPIC_PHRASE"}]}],"decisionEvidence":[]}
        """;

        var response = ReasoningModelResponseParser.Parse(raw);

        Assert.Equal("CONTENT_HEADING", Assert.Single(response.Headings).SemanticRole);
        Assert.DoesNotContain("chain", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_parser_fails_closed_when_required_semantic_role_is_missing()
    {
        Assert.Throws<FormatException>(() => ReasoningModelResponseParser.Parse(
            "{\"headings\":[{\"start\":0,\"end\":1,\"proposedLevel\":1,\"proposedParentLocalId\":null,\"confidence\":0.9,\"decisionEvidence\":[]}]}"));
    }

    [Fact]
    public void Metric_evaluable_cohort_is_derived_from_capability_and_exact_span_flags()
    {
        var root = FindRepositoryRoot();
        var exhaustive = ReasoningGoldArtifactLoader.DiscoverExhaustiveDocuments(root);
        var evaluable = ReasoningGoldArtifactLoader.DiscoverMetricEvaluableDocuments(root);

        Assert.Equal(6, exhaustive.Count);
        Assert.Equal(5, evaluable.Count);
        Assert.DoesNotContain("DOC-0264", evaluable);
    }

    private static ReasoningHeadingProposal Proposal(
        string sourceId,
        string text,
        int start,
        int end,
        int level,
        string role = "CONTENT_HEADING") =>
        new()
        {
            SourceId = sourceId,
            HeadingSpan = new StructuralSpan(start, end),
            Text = text,
            SemanticRole = role,
            ProposedLevel = level,
            Confidence = 0.9,
        };

    private static StructuralValidation Validated() =>
        new(true, true, true, true, 1, true, true, true, null);

    private static StructuralDecision Decision() =>
        new("test", "accepted", 1, "test");

    private sealed class FixedReasoningModel(
        Func<ReasoningModelRequest, ReasoningModelResponse> response) : IReasoningSemanticModel
    {
        public string ModelName => "fixed";
        public string ProviderName => "test";
        public int ContextSize => 80_000;
        public int ProviderCalls { get; private set; }

        public Task<ReasoningModelResponse> CompleteAsync(
            ReasoningModelRequest request,
            CancellationToken ct = default)
        {
            ProviderCalls++;
            return Task.FromResult(response(request));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
