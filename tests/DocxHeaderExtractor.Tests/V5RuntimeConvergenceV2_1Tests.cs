using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using SemanticSourceAtom = DocxHeaderExtractor.Core.Models.SemanticSourceAtom;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free runtime/qualification convergence checks. DocumentAgentRuntime and the standalone
/// qualification runner now share the exact same codec (<see cref="SemanticClaimResponseCodecV2_1"/>)
/// and binder (<see cref="ExactClaimBinderV2_1"/>); this replays the three frozen canary responses
/// through that shared path with no network call, and audits every halo-as-subject refusal the real
/// canary produced against every P05 pack, to prove no source occurrence lost its one semantic
/// opportunity. Never reads Gold, never calls a provider.
/// </summary>
public sealed class V5RuntimeConvergenceV2_1Tests
{
    private const string ResultPath = "artifacts/v5-provider-canary-current/canary-result.v1.json";
    private const string ConvergenceRoot = "artifacts/v5-runtime-convergence-v2_1";

    private static readonly (string Id, string Pdf)[] Docs =
    [
        ("SRC-089", SourcePdfCorpus.Src089),
        ("SRC-095", SourcePdfCorpus.Src095),
    ];

    // The latest real canary (recorded at 36702da, captured at 966c16a). Per pack:
    // proposals, bound, refused, outcome - exactly what qualification must now report.
    private static readonly (string DocumentId, string PackId, int Proposals, int Bound, int Refused, V5PackBindingOutcome Outcome)[] ExpectedCanary =
    [
        ("SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", 22, 21, 1, V5PackBindingOutcome.PARTIAL_BINDING),
        ("SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", 43, 43, 0, V5PackBindingOutcome.BINDING_COMPLETE),
        ("SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_004", 109, 107, 2, V5PackBindingOutcome.PARTIAL_BINDING),
    ];

    private static readonly (string DocumentId, string PackId, string Alias)[] ExpectedHaloRefusals =
    [
        ("SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", "L0094:S0"),
        ("SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_004", "L0373:S0"),
        ("SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_004", "L0377:S0"),
    ];

    private sealed record ReplayedPack(
        string DocumentId,
        V5PackedSourceRequest Pack,
        IReadOnlyList<SemanticSourceAtom> Atoms,
        SemanticClaimResponseV2_1 Response,
        ClaimBindingResultV2_1 Binding,
        V5PackBindingQualification Qualification,
        IReadOnlyList<V5PackedSourceRequest> AllPacks);

    /// <summary>Replays every frozen raw response through the shared qualifier. Never mutates the frozen artifact.</summary>
    private static IReadOnlyList<ReplayedPack> ReplayFrozenCanary()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300);
        var built = Docs.ToDictionary(
            doc => doc.Id,
            doc => V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(doc.Pdf), doc.Id, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope).Requests,
            StringComparer.Ordinal);
        var atomsByDoc = Docs.ToDictionary(doc => doc.Id, doc => V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(doc.Pdf)), StringComparer.Ordinal);

        var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath))).RootElement;
        return frozen.GetProperty("results").EnumerateArray().Select(result =>
        {
            var documentId = result.GetProperty("documentId").GetString()!;
            var packId = result.GetProperty("packId").GetString()!;
            var pack = built[documentId].Single(item => item.PackId == packId);
            var scope = ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases);
            var (qualification, response, binding) = V5BindingQualifier.Qualify(
                result.GetProperty("rawResponse").GetString(),
                result.GetProperty("finishReason").GetString(),
                null, contract, packId, atomsByDoc[documentId], scope);
            Assert.True(qualification.ResponseUsable, $"{documentId}:{packId}:{qualification.ResponseFatalReason}");
            return new ReplayedPack(documentId, pack, atomsByDoc[documentId], response!, binding!, qualification, built[documentId]);
        }).ToArray();
    }

    [Fact]
    public void Frozen_canary_is_reclassified_with_partial_fail_closed_semantics()
    {
        var rawBefore = File.ReadAllBytes(TestRepository.Path(ResultPath));
        var replayed = ReplayFrozenCanary();

        Assert.Equal(ExpectedCanary.Length, replayed.Count);
        foreach (var (expected, actual) in ExpectedCanary.Zip(replayed))
        {
            var q = actual.Qualification;
            Assert.Equal(expected.DocumentId, actual.DocumentId);
            Assert.Equal(expected.PackId, actual.Pack.PackId);
            Assert.Equal(expected.Proposals, q.ProposalCount);
            Assert.Equal(expected.Bound, q.BoundCount);
            Assert.Equal(expected.Refused, q.RefusalCount);
            Assert.Equal(expected.Outcome, q.Outcome);
            Assert.Equal(expected.Refused == 0, q.BindingComplete);
            Assert.True(q.ResponseUsable);
            Assert.True(q.RuntimeProcessedSafely);
            Assert.Equal(0, q.UnsafeRepairCount);
            Assert.Equal(0, q.OutOfScopeAcceptedCount);
        }

        var aggregate = V5QualificationAggregate.From(replayed.Select(item => item.Qualification).ToArray());
        Assert.Equal(3, aggregate.ProviderCalls);
        Assert.Equal(3, aggregate.UsableResponses);
        Assert.Equal(0, aggregate.SchemaInvalidResponses);
        Assert.Equal(0, aggregate.FinishReasonLength);
        Assert.Equal(174, aggregate.TotalProposals);
        Assert.Equal(171, aggregate.TotalBound);
        Assert.Equal(3, aggregate.TotalRefused);
        Assert.Equal(171m / 174m, aggregate.BoundFraction);
        Assert.Equal(3, aggregate.OwnershipRefusals);
        Assert.Equal(0, aggregate.OtherBindingRefusals);
        Assert.Equal(1, aggregate.PacksBindingComplete);
        Assert.Equal(2, aggregate.PacksPartialBinding);
        Assert.Equal(0, aggregate.PacksBindingEmpty);
        Assert.Equal(3, aggregate.PacksRuntimeProcessedSafely);
        Assert.Equal(0, aggregate.UnsafeRepairs);
        Assert.Equal(0, aggregate.OutOfScopeClaimsAccepted);

        // The raw response artifact is evidence, not output: replay must never touch it.
        Assert.Equal(rawBefore, File.ReadAllBytes(TestRepository.Path(ResultPath)));

        WriteJson($"{ConvergenceRoot}/canary-qualification.v2.json", new
        {
            schemaVersion = "v5-canary-qualification-v2",
            sourceArtifact = ResultPath,
            sourceArtifactSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rawBefore)),
            providerCalls = 0,
            goldRead = false,
            supersedes = "runtimeAcceptable/passCount in the v1 result, which wrongly required zero claim-local refusals",
            aggregate,
            packs = replayed.Select(item => new
            {
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                qualification = item.Qualification.ToReport(),
            }).ToArray(),
        });
    }

    [Fact]
    public void Frozen_v2_1_canary_remains_historical_replay_only()
    {
        foreach (var item in ReplayFrozenCanary())
        {
            Assert.True(item.Qualification.ResponseUsable);
            Assert.Equal(item.Qualification.BoundCount, item.Binding.Bound.Count);
        }
    }

    [Fact]
    public void Every_halo_refusal_in_the_latest_canary_is_owned_exactly_once_elsewhere_and_loses_no_coverage()
    {
        var replayed = ReplayFrozenCanary();
        var refusals = replayed
            .SelectMany(item => item.Qualification.Refusals.Select(refusal => (Item: item, Reason: refusal.Value)))
            .ToArray();
        Assert.All(refusals, entry => Assert.StartsWith("subject-alias-not-owned:", entry.Reason));
        Assert.Equal(
            ExpectedHaloRefusals,
            refusals.Select(entry => (entry.Item.DocumentId, entry.Item.Pack.PackId, entry.Reason["subject-alias-not-owned:".Length..])).ToArray());

        var audit = new List<object>();
        var ownedElsewhere = 0;
        var lostCoverage = false;
        foreach (var (item, reason) in refusals)
        {
            var alias = reason["subject-alias-not-owned:".Length..];

            // Global exact-once ownership over this document's full source universe.
            var allOwned = item.AllPacks.SelectMany(pack => pack.OwnedAliases).ToArray();
            Assert.Equal(allOwned.Length, allOwned.Distinct(StringComparer.Ordinal).Count());
            lostCoverage |= !item.Atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal).SetEquals(allOwned);

            var owners = item.AllPacks.Where(pack => pack.OwnedAliases.Contains(alias, StringComparer.Ordinal)).ToArray();
            // No owner at all would be a packing bug, not a claim-local refusal.
            Assert.True(owners.Length == 1, $"{item.DocumentId}:{alias} owned by {owners.Length} packs - packing bug");
            var owner = owners[0];
            Assert.NotEqual(item.Pack.PackId, owner.PackId);
            // It reached the refusing pack only as halo context.
            Assert.Contains(alias, item.Pack.VisibleAliases);
            Assert.DoesNotContain(alias, item.Pack.OwnedAliases);
            ownedElsewhere++;

            // The same alias is still a legal subject in its owner pack.
            var probe = new SemanticClaimProposalV2_1(
                new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1(alias)]), "STRUCTURAL_REGION", "probe", EvidenceNeeds: []);
            var ownerBinding = ExactClaimBinderV2_1.Bind(owner.PackId, [probe], item.Atoms,
                ClaimBindingScope.Create(owner.OwnedAliases, owner.VisibleAliases));
            Assert.Single(ownerBinding.Bound);
            Assert.Empty(ownerBinding.Refusals);

            audit.Add(new
            {
                documentId = item.DocumentId,
                alias,
                refusedInPack = item.Pack.PackId,
                refusalReason = reason,
                ownedByPack = owner.PackId,
                ownerCount = owners.Length,
                availableAsSubjectInOwnerPack = true,
                enteredGraphFromRefusingPack = false,
                classification = "OWNED_BY_ANOTHER_PACK",
            });
        }

        Assert.Equal(3, ownedElsewhere);
        Assert.False(lostCoverage);
        WriteJson($"{ConvergenceRoot}/halo-ownership-audit.v2.json", new
        {
            schemaVersion = "v5-halo-ownership-audit-v2",
            sourceArtifact = ResultPath,
            providerCalls = 0,
            goldRead = false,
            haloRefusalsOwnedElsewhere = $"{ownedElsewhere}/{refusals.Length}",
            lostSourceCoverage = lostCoverage,
            audit,
        });
    }

    [Fact]
    public void Partial_binding_retains_valid_siblings_through_the_real_runtime()
    {
        // 10 proposals, 2 use a halo alias as subject and must be refused; the other 8 must still
        // enter state. Exercises DocumentAgentRuntime end to end, not only the standalone binder.
        var atoms = Enumerable.Range(0, 10)
            .Select(i => new DocxHeaderExtractor.Core.Models.SemanticSourceAtom($"A{i}", $"S{i}", i, 1, i, 0, $"Text{i}"))
            .ToArray();
        var owned = atoms.Take(8).Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var visible = atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var graph = EvidenceGraphBuilder.Build(atoms.Select(atom => new SourceObservation(
            $"E{atom.Ordinal}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length))));

        var reasoner = new FixedDecisionReasoner(atoms.Take(8).Select((atom, i) => new V5SemanticSubjectDecisionV3([
            new V5SemanticDecisionClaimV3("DESCRIBES", $"value{i}", EvidenceNeeds: []),
        ])).ToArray());

        var contract = Contract();
        var result = new DocumentAgentRuntime(reasoner, new InMemoryEvidenceRetriever())
            .RunAsync(contract, graph, atoms, owned, visible).Result;

        Assert.Equal(8, result.State.Claims.Count);
        Assert.DoesNotContain(result.State.Conflicts, issue => issue.Code == "CLAIM_BINDING");
    }

    [Fact]
    public async Task Refinement_keeps_harness_owned_subject_and_rejects_extra_halo_slot()
    {
        var atoms = new[]
        {
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A1", "S1", 1, 1, 1, 0, "Alpha"),
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A2", "S2", 2, 1, 2, 0, "Beta"),
        };
        var owned = new HashSet<string>(StringComparer.Ordinal) { "A1" };
        var visible = new HashSet<string>(StringComparer.Ordinal) { "A1", "A2" };
        var graph = EvidenceGraphBuilder.Build(atoms.Select(atom => new SourceObservation(
            $"E{atom.Ordinal}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length))));
        var contract = Contract() with { ExecutionBudget = new ExecutionBudget(MaxSemanticModelCalls: 2, MaxRetrievalRounds: 1) };

        var reasoner = new MigratingSubjectReasoner();
        var result = await new DocumentAgentRuntime(reasoner, new InMemoryEvidenceRetriever())
            .RunAsync(contract, graph, atoms, owned, visible);

        Assert.Contains(result.State.Conflicts, issue => issue.Code == "CLAIM_BINDING" &&
            issue.Message.Contains("additional-owned-index-out-of-range-or-order", StringComparison.Ordinal));
    }

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "v5-runtime-convergence-test",
        "Source-backed test task.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));

    private sealed class FixedDecisionReasoner(IReadOnlyList<V5SemanticSubjectDecisionV3> decisions) : ISemanticReasoner
    {
        public string Identity => "test-fixed-proposals";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SemanticReasoningResult(new V5SemanticDecisionResponseV3(decisions), new SemanticReasoningUsage()));
    }

    private sealed class MigratingSubjectReasoner : ISemanticReasoner
    {
        public string Identity => "test-migrating-subject";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            if (context.CallOrdinal == 0)
            {
                return ValueTask.FromResult(new SemanticReasoningResult(new V5SemanticDecisionResponseV3([
                    new([new V5SemanticDecisionClaimV3("DESCRIBES", State: ClaimResolutionState.OPEN, EvidenceNeeds: [EvidenceNeed.MORE_CONTEXT])]),
                ]), new SemanticReasoningUsage()));
            }
            // The only way to address another subject slot is an owned positional index; the
            // deliberately invalid next index is refused without resolving any alias from halo.
            var existingClaimId = context.OpenOrConflictedClaims.SingleOrDefault()?.ClaimId;
            return ValueTask.FromResult(new SemanticReasoningResult(new V5SemanticDecisionResponseV3([
                new([new V5SemanticDecisionClaimV3("DESCRIBES", "value", AdditionalSubjectParts: [new V5AdditionalOwnedSubjectPartV3(1)],
                    State: ClaimResolutionState.RESOLVED, EvidenceNeeds: [], ExistingClaimId: existingClaimId)]),
            ]), new SemanticReasoningUsage()));
        }
    }

    private static void WriteJson(string relativePath, object value)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
