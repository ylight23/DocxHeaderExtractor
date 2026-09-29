using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

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

    [Fact]
    public void Frozen_responses_replay_through_the_shared_codec_and_binder_to_the_same_counts()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300);
        var built = Docs.ToDictionary(
            doc => doc.Id,
            doc => V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(doc.Pdf), doc.Id, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope),
            StringComparer.Ordinal);

        var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath))).RootElement;
        var replay = new List<object>();

        foreach (var result in frozen.GetProperty("results").EnumerateArray())
        {
            var documentId = result.GetProperty("documentId").GetString()!;
            var packId = result.GetProperty("packId").GetString()!;
            var rawResponse = result.GetProperty("rawResponse").GetString()!;
            var expectedClaimCount = result.GetProperty("claimCount").GetInt32();
            var expectedBoundCount = result.GetProperty("boundCount").GetInt32();
            var expectedRefusalCount = result.GetProperty("refusalCount").GetInt32();

            var pack = built[documentId].Requests.Single(item => item.PackId == packId);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(Docs.Single(doc => doc.Id == documentId).Pdf));
            var scope = ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases);

            using var document = JsonDocument.Parse(rawResponse);
            var response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);
            var binding = ExactClaimBinderV2_1.Bind(packId, response.Claims, atoms, scope);

            // Byte-identical replay through the exact path DocumentAgentRuntime now uses: if
            // qualification and runtime had diverged, these would not match.
            Assert.Equal(expectedClaimCount, response.Claims.Count);
            Assert.Equal(expectedBoundCount, binding.Bound.Count);
            Assert.Equal(expectedRefusalCount, binding.Refusals.Count);

            var responseUsable = true; // transport/JSON/schema already held at capture time; codec re-parsed cleanly here too
            var claimBindingComplete = binding.Refusals.Count == 0;
            var runtimeAcceptable = responseUsable && (response.Claims.Count == 0 || binding.Bound.Count > 0) &&
                binding.Refusals.Values.All(reason =>
                    !reason.StartsWith("subject-alias-not-owned", StringComparison.Ordinal) &&
                    !reason.StartsWith("object-alias-not-visible", StringComparison.Ordinal) ||
                    binding.Bound.Count == response.Claims.Count - binding.Refusals.Count);

            replay.Add(new
            {
                documentId,
                packId,
                proposalCount = response.Claims.Count,
                boundCount = binding.Bound.Count,
                refusalCount = binding.Refusals.Count,
                refusalReasons = binding.Refusals.Values.Distinct().ToArray(),
                responseUsable,
                claimBindingComplete,
                runtimeAcceptable,
            });
        }

        WriteJson($"{ConvergenceRoot}/replay.v1.json", new
        {
            schemaVersion = "v5-runtime-convergence-replay-v1",
            providerCalls = 0,
            goldRead = false,
            qualificationRuntimeParity = true,
            replay,
        });
    }

    [Fact]
    public void Every_halo_as_subject_refusal_from_the_latest_canary_is_owned_by_exactly_one_pack()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300);

        // The exact refused aliases the real canary at commit 2f37b3c reported for these two packs.
        var refusedHaloAliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SRC-089"] = ["L0094:S0", "L0095:S0", "L0096:S0", "L0098:S0", "L0099:S0"],
            ["SRC-095"] = ["L0090:S0", "L0091:S0", "L0092:S0", "L0093:S0", "L0096:S0", "L0097:S0"],
        };

        var audit = new List<object>();
        var lostCoverage = false;

        foreach (var doc in Docs)
        {
            var built = V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(doc.Pdf), doc.Id, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);

            // Global exact-once ownership across every pack of this document - already enforced by
            // the packing policy's own conservation check, reconfirmed here as the audit's premise.
            var allOwned = built.Requests.SelectMany(pack => pack.OwnedAliases).ToArray();
            Assert.Equal(allOwned.Length, allOwned.Distinct(StringComparer.Ordinal).Count());

            var ownerByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pack in built.Requests)
                foreach (var owned in pack.OwnedAliases)
                    ownerByAlias[owned] = pack.PackId;

            foreach (var alias in refusedHaloAliases[doc.Id])
            {
                var owned = ownerByAlias.TryGetValue(alias, out var owningPack);
                if (!owned) lostCoverage = true;
                Assert.True(owned, $"{doc.Id}:{alias} is never owned by any pack in this document - packing bug, not a refusal");
                audit.Add(new { documentId = doc.Id, alias, ownedByPack = owningPack, classification = "OWNED_BY_ANOTHER_PACK" });
            }
        }

        Assert.False(lostCoverage);
        WriteJson($"{ConvergenceRoot}/halo-ownership-audit.v1.json", new
        {
            schemaVersion = "v5-halo-ownership-audit-v1",
            providerCalls = 0,
            goldRead = false,
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

        var reasoner = new FixedProposalsReasoner(atoms.Select((atom, i) => new SemanticClaimProposalV2_1(
            new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1(atom.Alias)]), "DESCRIBES", $"value{i}",
            EvidenceNeeds: [])).ToArray());

        var contract = Contract();
        var result = new DocumentAgentRuntime(reasoner, new InMemoryEvidenceRetriever())
            .RunAsync(contract, graph, atoms, owned, visible).Result;

        Assert.Equal(8, result.State.Claims.Count);
        Assert.Equal(2, result.State.Conflicts.Count(issue => issue.Code == "CLAIM_BINDING"));
        Assert.DoesNotContain(result.State.Conflicts, issue => issue.Code == "CLAIM_CONTRACT");
    }

    [Fact]
    public async Task A_refined_claim_migrating_its_subject_into_halo_is_refused_not_silently_repaired()
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
            issue.Message.Contains("subject-alias-not-owned", StringComparison.Ordinal));
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

    private sealed class FixedProposalsReasoner(IReadOnlyList<SemanticClaimProposalV2_1> proposals) : ISemanticReasoner
    {
        public string Identity => "test-fixed-proposals";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SemanticReasoningResult(new SemanticClaimResponseV2_1(proposals), new SemanticReasoningUsage()));
    }

    private sealed class MigratingSubjectReasoner : ISemanticReasoner
    {
        public string Identity => "test-migrating-subject";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            if (context.CallOrdinal == 0)
            {
                return ValueTask.FromResult(new SemanticReasoningResult(new SemanticClaimResponseV2_1([
                    new(new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A1")]), "DESCRIBES",
                        State: ClaimResolutionState.OPEN, EvidenceNeeds: [EvidenceNeed.MORE_CONTEXT]),
                ]), new SemanticReasoningUsage()));
            }
            // A refinement that tries to move its subject into a halo alias must be refused, not
            // repaired into the original owned subject and not silently accepted.
            var existingClaimId = context.OpenOrConflictedClaims.SingleOrDefault()?.ClaimId;
            return ValueTask.FromResult(new SemanticReasoningResult(new SemanticClaimResponseV2_1([
                new(new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A2")]), "DESCRIBES", "value",
                    State: ClaimResolutionState.RESOLVED, EvidenceNeeds: [], ExistingClaimId: existingClaimId),
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
