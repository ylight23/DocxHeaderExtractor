using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using SemanticSourceAtom = DocxHeaderExtractor.Core.Models.SemanticSourceAtom;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free structure of the first 31-pack V5 measurement cohort. Deliberately does not
/// compare against the committed cohort hashes: those are bound to the one pinned extraction
/// environment (SRC-089's fonts are not embedded), and the runner's execute mode is the gate that
/// enforces them. These tests hold in every environment. Never reads Gold, never calls a provider.
/// </summary>
public sealed class V5Cohort31Tests
{
    private static readonly Lazy<V5CohortPreflight> Cohort = new(() =>
        V5CohortPreflightBuilder.Build(TestRepository.Root(), DocumentStructureTaskContract.Create()));

    [Fact]
    public void Cohort_is_exactly_seven_plus_twenty_four_packs_in_a_stable_order()
    {
        var rows = Cohort.Value.Rows;
        Assert.Equal(V5CohortGate.CohortRequestCount, rows.Count);
        Assert.Equal(7, rows.Count(row => row.DocumentId == "SRC-089"));
        Assert.Equal(24, rows.Count(row => row.DocumentId == "SRC-095"));
        Assert.Equal(Enumerable.Range(1, 31), rows.Select(row => row.Ordinal));
        Assert.Equal(rows.Count, rows.Select(row => (row.DocumentId, row.PackId)).Distinct().Count());
        Assert.All(rows, row => Assert.StartsWith("RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_", row.PackId));
    }

    [Fact]
    public void Every_source_alias_is_owned_exactly_once_and_halo_only_overlaps_visibility()
    {
        foreach (var document in Cohort.Value.Documents)
        {
            var proof = document.Ownership;
            Assert.Equal(document.AtomCount, proof.TotalAliases);
            Assert.Equal(proof.TotalAliases, proof.OwnedExactlyOnce);
            Assert.Equal(0, proof.UnownedAliases);
            Assert.Equal(0, proof.MultiplyOwnedAliases);
            Assert.Equal(0, proof.OwnedOutsideUniverse);
            Assert.True(proof.OwnedSubsetOfVisibleInEveryPack);
            Assert.True(proof.OwnershipConserved);

            var rows = Cohort.Value.Rows.Where(row => row.DocumentId == document.DocumentId).ToArray();
            var owners = rows.SelectMany(row => row.OwnedAliases.Select(alias => (alias, row.PackId)))
                .ToDictionary(item => item.alias, item => item.PackId, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var owned = row.OwnedAliases.ToHashSet(StringComparer.Ordinal);
                var contextOnly = row.VisibleAliases.Where(alias => !owned.Contains(alias)).ToArray();
                Assert.Equal(row.ContextOnlyCount, contextOnly.Length);
                // A halo alias is visible here but always owned by a different pack.
                Assert.All(contextOnly, alias => Assert.NotEqual(row.PackId, owners[alias]));
            }
        }
    }

    [Fact]
    public void Ownership_proof_detects_an_unowned_and_a_doubly_owned_alias()
    {
        var atoms = Enumerable.Range(0, 3).Select(i => new SemanticSourceAtom($"A{i}", $"S{i}", i, 1, i, 0, "t")).ToArray();
        V5PackedSourceRequest Pack(string id, params string[] owned) =>
            new(id, owned, owned, new V5ComposedSemanticRequest("c", "p", "h", "s", "r", 1));

        var gap = V5CohortPreflightBuilder.ProveOwnership(atoms, [Pack("P1", "A0"), Pack("P2", "A1")]);
        Assert.Equal(1, gap.UnownedAliases);
        Assert.False(gap.OwnershipConserved);

        var twice = V5CohortPreflightBuilder.ProveOwnership(atoms, [Pack("P1", "A0", "A1"), Pack("P2", "A1", "A2")]);
        Assert.Equal(1, twice.MultiplyOwnedAliases);
        Assert.False(twice.OwnershipConserved);
    }

    [Fact]
    public void Two_independent_builds_are_byte_identical()
    {
        var again = V5CohortPreflightBuilder.Build(TestRepository.Root(), DocumentStructureTaskContract.Create());
        Assert.Equal(JsonSerializer.Serialize(Cohort.Value), JsonSerializer.Serialize(again));
        foreach (var (first, second) in Cohort.Value.Rows.Zip(again.Rows))
            Assert.Equal(first.ProviderBody, second.ProviderBody);
    }

    [Fact]
    public void Every_frozen_body_carries_exactly_the_qualified_payload()
    {
        foreach (var row in Cohort.Value.Rows)
        {
            Assert.Equal(row.ProviderRequestHash, V5CohortPreflightBuilder.Sha256(row.ProviderBody));
            Assert.Equal(row.ProviderRequestBytes, row.ProviderBody.Length);
            var body = JsonDocument.Parse(row.ProviderBody).RootElement;
            Assert.Equal("qwen/qwen3.7-flash", body.GetProperty("model").GetString());
            Assert.Equal(0, body.GetProperty("temperature").GetInt32());
            Assert.Equal(row.MaxCompletionTokens, body.GetProperty("max_tokens").GetInt32());
            Assert.Equal("none", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("usage").GetProperty("include").GetBoolean());
            var provider = body.GetProperty("provider");
            Assert.Equal(["alibaba"], provider.GetProperty("order").EnumerateArray().Select(item => item.GetString()));
            Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
            Assert.True(provider.GetProperty("require_parameters").GetBoolean());
            Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
            Assert.False(provider.GetProperty("zdr").GetBoolean());
            Assert.DoesNotContain("Bearer", Encoding.UTF8.GetString(row.ProviderBody), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cohort_gate_admits_exactly_thirty_one_authorized_requests_and_is_separate_from_the_canary_gate()
    {
        V5CohortGate.Authorize(31, providerExecutionAuthorized: true);
        Assert.Throws<InvalidOperationException>(() => V5CohortGate.Authorize(31, providerExecutionAuthorized: false));
        Assert.Throws<InvalidOperationException>(() => V5CohortGate.Authorize(30, providerExecutionAuthorized: true));
        Assert.Throws<InvalidOperationException>(() => V5CohortGate.Authorize(32, providerExecutionAuthorized: true));
        // A canary authorization never stretches to the cohort.
        Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(31, providerExecutionAuthorized: true));
    }

    [Fact]
    public void Cohort_runner_has_one_evaluator_its_own_sentinel_and_no_gold()
    {
        var runner = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.V5Qualification/Cohort31.cs"));
        Assert.Contains("V5BindingQualifier.Qualify(", runner, StringComparison.Ordinal);
        Assert.Contains("V5CohortGate.Authorize(", runner, StringComparison.Ordinal);
        // No second evaluator: binding and decoding only ever happen inside V5BindingQualifier.
        Assert.DoesNotContain("ExactClaimBinderV2_1.Bind(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("SemanticClaimResponseCodecV2_1.Parse(", runner, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "gold-current", "canonical-semantic-gold", "GoldLabel" })
            Assert.DoesNotContain(forbidden, runner, StringComparison.OrdinalIgnoreCase);
        var program = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.V5Qualification/Program.cs"));
        var canarySentinel = program.Split("ConfirmSentinel = \"")[1].Split('"')[0];
        var cohortSentinel = runner.Split("ConfirmSentinel = \"")[1].Split('"')[0];
        Assert.NotEqual(canarySentinel, cohortSentinel);
    }

    [Theory]
    [InlineData("subject-alias-not-owned:L0094:S0", V5BindingQualifier.FamilySubjectAliasNotOwned)]
    [InlineData("object-alias-not-visible:L0001:S0", V5BindingQualifier.FamilyObjectAliasNotVisible)]
    [InlineData("'L0002:S1' is not an atom in this source", V5BindingQualifier.FamilyUnknownAlias)]
    [InlineData("'L0002:S0' does not contain that text", V5BindingQualifier.FamilyExactTextBinding)]
    [InlineData("'L0002:S0' has 1 occurrences of that text, not 2", V5BindingQualifier.FamilyExactTextBinding)]
    [InlineData("'L0002:S0' contains that text 2 times and nothing says which", V5BindingQualifier.FamilyExactTextBinding)]
    [InlineData("'L0002:S0' contains that text but not in the context given", V5BindingQualifier.FamilyExactTextBinding)]
    [InlineData("'L0002:S0' selects the whole atom and must not also quote text", V5BindingQualifier.FamilyExactTextBinding)]
    [InlineData("'L0002:S0' is selected twice at the same place", V5BindingQualifier.FamilySourceOrderOrOverlap)]
    [InlineData("'L0002:S0' selects text that comes before the part above it", V5BindingQualifier.FamilySourceOrderOrOverlap)]
    [InlineData("'L0002:S0' selects text the part above it already covers", V5BindingQualifier.FamilySourceOrderOrOverlap)]
    [InlineData("'L0003:S0' does not come after 'L0004:S0' in the source", V5BindingQualifier.FamilySourceOrderOrOverlap)]
    [InlineData("unknown-existing-claim-id", V5BindingQualifier.FamilyRefinementId)]
    [InlineData("existing-claim-id-mismatch", V5BindingQualifier.FamilyRefinementId)]
    [InlineData("duplicate-harness-claim-id", V5BindingQualifier.FamilyDuplicateDurableClaimId)]
    [InlineData("predicate-missing", V5BindingQualifier.FamilyOtherExactBinderRefusal)]
    [InlineData("something-new", V5BindingQualifier.FamilyOtherExactBinderRefusal)]
    public void Refusal_family_is_keyed_on_fixed_binder_wording_not_on_quoted_aliases(string reason, string family)
    {
        Assert.Equal(family, V5BindingQualifier.RefusalFamily(reason));
        Assert.Contains(family, V5BindingQualifier.RefusalFamilies);
    }

    [Fact]
    public void Refusal_families_match_what_the_real_binder_emits()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("A0", "S0", 0, 1, 0, 0, "Alpha beta"),
            new SemanticSourceAtom("A1", "S1", 1, 1, 1, 0, "Gamma"),
        };
        var scope = ClaimBindingScope.Create(["A0", "A1"], ["A0", "A1"]);
        SemanticClaimProposalV2_1 Proposal(params ProviderSourcePartV2_1[] parts) =>
            new(new ClaimSourceEndpointV2_1(parts), "DESCRIBES", "v", EvidenceNeeds: []);
        var binding = ExactClaimBinderV2_1.Bind("pack", [
            Proposal(new ProviderSourcePartV2_1("NOPE")),
            Proposal(new ProviderSourcePartV2_1("A0", "delta")),
            Proposal(new ProviderSourcePartV2_1("A1"), new ProviderSourcePartV2_1("A0")),
        ], atoms, scope);

        Assert.Equal(
            [V5BindingQualifier.FamilyUnknownAlias, V5BindingQualifier.FamilyExactTextBinding, V5BindingQualifier.FamilySourceOrderOrOverlap],
            binding.Refusals.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => V5BindingQualifier.RefusalFamily(item.Value)));
    }

    [Fact]
    public void Transport_aggregate_uses_nearest_rank_percentiles_and_counts_retries()
    {
        var calls = Enumerable.Range(1, 20).Select(i => new V5CohortCallTransport(
            i, "D", $"P{i}", i != 20, i == 3 ? 2 : 1, i == 20 ? null : i == 19 ? "length" : "stop",
            100, 10, 0, i * 10.0)).ToArray();
        var aggregate = V5CohortTransportAggregate.From(calls, 1234);

        Assert.Equal(20, aggregate.ProviderCalls);
        Assert.Equal(21, aggregate.HttpAttempts);
        Assert.Equal(1, aggregate.TransportRetries);
        Assert.Equal(1, aggregate.TransportFailures);
        Assert.Equal(1, aggregate.FinishReasonLength);
        Assert.Equal(18, aggregate.FinishReasonDistribution["stop"]);
        Assert.Equal(1, aggregate.FinishReasonDistribution["(none)"]);
        Assert.Equal(2000, aggregate.PromptTokens);
        Assert.Equal(100, aggregate.LatencyP50Ms);
        Assert.Equal(190, aggregate.LatencyP95Ms);
        Assert.Equal(1234, aggregate.TotalWallTimeMs);
    }
}
