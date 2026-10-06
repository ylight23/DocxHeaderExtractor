using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free checks that qualification classifies a response with the same partial fail-closed
/// semantics <see cref="DocumentAgentRuntime"/> already implements: a response-fatal problem discards
/// the response, a claim-local refusal discards only that proposal and is preserved, and valid
/// siblings are kept. Never reads Gold, never calls a provider.
/// </summary>
public sealed class V5BindingQualificationTests
{
    private static readonly SemanticSourceAtom[] Atoms = Enumerable.Range(0, 4)
        .Select(i => new SemanticSourceAtom($"A{i}", $"S{i}", i, 1, i, 0, $"Text{i}"))
        .ToArray();

    // A0..A2 owned, A3 is halo: visible as context, never a legal subject for this pack.
    private static readonly ClaimBindingScope Scope = ClaimBindingScope.Create(
        ["A0", "A1", "A2"], ["A0", "A1", "A2", "A3"]);

    private static string Claim(string alias, string value = "v") =>
        $$"""{"subject":{"sourceParts":[{"sourceAlias":"{{alias}}"}]},"predicate":"DESCRIBES","value":"{{value}}","state":"RESOLVED","evidenceNeeds":[]}""";

    private static string Response(params string[] claims) => $$"""{"claims":[{{string.Join(",", claims)}}]}""";

    private static V5PackBindingQualification Qualify(string? raw, string? finishReason = "stop", string? transportError = null) =>
        V5BindingQualifier.Qualify(raw, finishReason, transportError, Contract(), "pack-1", Atoms, Scope).Qualification;

    [Fact]
    public void One_local_refusal_does_not_make_the_response_unusable()
    {
        var q = Qualify(Response(Claim("A0"), Claim("A3")));

        Assert.True(q.ResponseUsable);
        Assert.Null(q.ResponseFatalReason);
        Assert.Equal(V5PackBindingQualification.WirePass, q.WireStatus);
        Assert.Equal(1, q.RefusalCount);
        Assert.True(q.RuntimeProcessedSafely);
    }

    [Fact]
    public void One_ownership_refusal_yields_partial_binding()
    {
        var q = Qualify(Response(Claim("A0"), Claim("A1"), Claim("A3")));

        Assert.Equal(V5PackBindingOutcome.PARTIAL_BINDING, q.Outcome);
        Assert.False(q.BindingComplete);
        Assert.Equal(1, q.OwnershipRefusalCount);
        Assert.Equal(0, q.OtherRefusalCount);
        Assert.Equal(1, q.RefusalTaxonomy["subject-alias-not-owned"]);
        Assert.Equal(2m / 3m, q.BoundFraction);
    }

    [Fact]
    public void Valid_siblings_of_a_refused_proposal_remain_accepted()
    {
        var (q, _, binding) = V5BindingQualifier.Qualify(
            Response(Claim("A0", "x"), Claim("A3"), Claim("A2", "z")), "stop", null, Contract(), "pack-1", Atoms, Scope);

        Assert.Equal(3, q.ProposalCount);
        Assert.Equal(2, q.BoundCount);
        Assert.True(q.HasAcceptedClaims);
        Assert.Equal(["A0", "A2"], binding!.Bound.Select(item => item.Claim.Subject.Parts.Single().Alias));
        Assert.Equal(["x", "z"], binding.Bound.Select(item => item.Claim.Value));
    }

    [Fact]
    public void Zero_refusal_response_yields_binding_complete()
    {
        var q = Qualify(Response(Claim("A0"), Claim("A1"), Claim("A2")));

        Assert.Equal(V5PackBindingOutcome.BINDING_COMPLETE, q.Outcome);
        Assert.True(q.BindingComplete);
        Assert.True(q.RuntimeProcessedSafely);
        Assert.Equal(1m, q.BoundFraction);
    }

    [Fact]
    public void All_refused_usable_response_yields_binding_empty()
    {
        var q = Qualify(Response(Claim("A3"), Claim("NOT-AN-ATOM")));

        Assert.True(q.ResponseUsable);
        Assert.Equal(V5PackBindingOutcome.BINDING_EMPTY, q.Outcome);
        Assert.False(q.HasAcceptedClaims);
        Assert.Equal(2, q.RefusalCount);
        Assert.Equal(1, q.OwnershipRefusalCount);
        Assert.Equal(1, q.OtherRefusalCount);
        // Refusing everything is still safe processing; it is the response that is empty, not the runtime that failed.
        Assert.True(q.RuntimeProcessedSafely);
    }

    [Theory]
    [InlineData("{not json", "json:")]
    [InlineData("""{"claims":[],"extra":1}""", "schema:")]
    [InlineData("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A0"}]},"predicate":"NOT_IN_CONTRACT","value":"v","state":"RESOLVED","evidenceNeeds":[]}]}""", "schema:")]
    public void Codec_or_schema_failure_remains_response_fatal(string raw, string reasonPrefix)
    {
        var q = Qualify(raw);

        Assert.False(q.ResponseUsable);
        Assert.Equal(V5PackBindingOutcome.RESPONSE_FATAL, q.Outcome);
        Assert.Equal(nameof(V5PackBindingOutcome.RESPONSE_FATAL), q.WireStatus);
        Assert.StartsWith(reasonPrefix, q.ResponseFatalReason);
        Assert.False(q.RuntimeProcessedSafely);
        Assert.Equal(0, q.BoundCount);
    }

    [Fact]
    public void Finish_reason_length_remains_response_fatal_even_when_the_json_happens_to_parse()
    {
        var q = Qualify(Response(Claim("A0")), finishReason: "length");

        Assert.False(q.ResponseUsable);
        Assert.Equal(V5PackBindingOutcome.RESPONSE_FATAL, q.Outcome);
        Assert.Equal("finish-reason-length", q.ResponseFatalReason);
        Assert.Equal(0, q.BoundCount);

        var transport = Qualify(null, finishReason: null, transportError: "timeout");
        Assert.Equal(V5PackBindingOutcome.RESPONSE_FATAL, transport.Outcome);
        Assert.Equal("transport:timeout", transport.ResponseFatalReason);
    }

    [Fact]
    public void No_refusal_is_silently_repaired()
    {
        var proposals = new[]
        {
            new SemanticClaimProposalV2_1(new([new("A0")]), "DESCRIBES", "v", EvidenceNeeds: []),
            new SemanticClaimProposalV2_1(new([new("A3")]), "DESCRIBES", "v", EvidenceNeeds: []),
        };
        var real = ExactClaimBinderV2_1.Bind("pack-1", proposals, Atoms, Scope);
        var honest = V5BindingQualifier.FromBinding(proposals, real, Scope);
        Assert.Equal(0, honest.UnsafeRepairCount);
        Assert.Equal([new KeyValuePair<string, string>("proposal-2", "subject-alias-not-owned:A3")], honest.Refusals);
        Assert.DoesNotContain(real.Bound, item => item.Claim.Subject.Parts.Any(part => part.Alias == "A3"));

        // A binder that "fixed" the halo subject onto an owned alias instead of refusing it.
        var repairedSubject = real.Bound[0].Claim.Subject;
        var repaired = new ClaimBindingResultV2_1(
            [real.Bound[0], new BoundSemanticClaimV2_1(real.Bound[0].Claim with { ClaimId = "repaired", Subject = repairedSubject }, null)],
            new Dictionary<string, string>());
        var repairedQ = V5BindingQualifier.FromBinding(proposals, repaired, Scope);
        Assert.Equal(1, repairedQ.UnsafeRepairCount);
        Assert.False(repairedQ.RuntimeProcessedSafely);

        // A binder that dropped the refused proposal without recording why.
        var dropped = new ClaimBindingResultV2_1([real.Bound[0]], new Dictionary<string, string>());
        var droppedQ = V5BindingQualifier.FromBinding(proposals, dropped, Scope);
        Assert.False(droppedQ.RuntimeProcessedSafely);

        // A binder that accepted the halo subject as-is from the wrong pack.
        var haloAtom = Atoms[3];
        var wrongPack = new ClaimBindingResultV2_1(
            [real.Bound[0], new BoundSemanticClaimV2_1(real.Bound[0].Claim with
            {
                ClaimId = "wrong-pack",
                Subject = new BoundClaimEndpoint([real.Bound[0].Claim.Subject.Parts[0] with
                {
                    Alias = haloAtom.Alias, SourceId = haloAtom.SourceId, Ordinal = haloAtom.Ordinal, End = haloAtom.Text.Length, Text = haloAtom.Text,
                }]),
            }, null)],
            new Dictionary<string, string>());
        var wrongPackQ = V5BindingQualifier.FromBinding(proposals, wrongPack, Scope);
        Assert.Equal(1, wrongPackQ.OutOfScopeAcceptedCount);
        Assert.False(wrongPackQ.RuntimeProcessedSafely);
    }

    [Fact]
    public void Aggregate_keeps_every_layer_separate()
    {
        var packs = new[]
        {
            Qualify(Response(Claim("A0"), Claim("A1"))),
            Qualify(Response(Claim("A0"), Claim("A3"))),
            Qualify(Response(Claim("A3"))),
            Qualify("{not json"),
            Qualify(Response(Claim("A0")), finishReason: "length"),
        };
        var aggregate = V5QualificationAggregate.From(packs);

        Assert.Equal(5, aggregate.ProviderCalls);
        Assert.Equal(3, aggregate.UsableResponses);
        Assert.Equal(1, aggregate.JsonInvalidResponses);
        Assert.Equal(0, aggregate.SchemaInvalidResponses);
        Assert.Equal(1, aggregate.FinishReasonLength);
        Assert.Equal(5, aggregate.TotalProposals);
        Assert.Equal(3, aggregate.TotalBound);
        Assert.Equal(2, aggregate.TotalRefused);
        Assert.Equal(2, aggregate.OwnershipRefusals);
        Assert.Equal(1, aggregate.PacksBindingComplete);
        Assert.Equal(1, aggregate.PacksPartialBinding);
        Assert.Equal(1, aggregate.PacksBindingEmpty);
        Assert.Equal(3, aggregate.PacksRuntimeProcessedSafely);
        Assert.Equal(0, aggregate.UnsafeRepairs);
    }

    [Fact]
    public void Qualification_has_no_gold_or_provider_dependency()
    {
        // The classifier itself: no Gold path, no transport. (This test file is not scanned - it has
        // to name the forbidden tokens to check for them.)
        var source = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.V5Qualification/LegacyCore/V5BindingQualification.cs"));
        foreach (var forbidden in new[] { "gold-current", "GoldLabel", "canonical-semantic-gold", "OpenRouter", "HttpClient", "OPENROUTER_API_KEY" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("dhx-v5-qualify", typeof(V5BindingQualifier).Assembly.GetName().Name);
    }

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "v5-binding-qualification-test",
        "Source-backed test task.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));
}
