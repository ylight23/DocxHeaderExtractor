using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>P5L: provider-free closure of the unreliable exhaustive V3.0 ledger and V3.1 sparse wire proof.</summary>
public sealed class V5P5LSparseDecisionProtocolTests
{
    private const string ArtifactRoot = "artifacts/v5-p5l-sparse-decision-protocol";

    [Fact]
    public async Task Close_exhaustive_ledger_and_prove_sparse_positional_quarantine_and_binding()
    {
        var fixture = Fixture.Create();
        var canonical = V5SemanticSparseDecisionComposerV3_1.BuildCanonical(fixture.Contract, fixture.Packet);
        var composed = V5SemanticSparseDecisionComposerV3_1.Compose(fixture.Contract, fixture.Packet);
        var schema = JsonSerializer.SerializeToElement(canonical.ResponseSchema, CanonicalJson.Options);
        var decisions = schema.GetProperty("properties").GetProperty("decisions");
        var decisionItem = decisions.GetProperty("items");

        Assert.Equal(V5SemanticSparseDecisionComposerV3_1.Version, canonical.ComposerVersion);
        Assert.Equal(V5Protocol.ClaimSchemaVersionV3_1, canonical.ProtocolVersion);
        Assert.Equal(0, decisions.GetProperty("minItems").GetInt32());
        Assert.Equal(fixture.Packet.SubjectEvidence.Count, decisions.GetProperty("maxItems").GetInt32());
        Assert.Contains("ownedIndex", decisionItem.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("claims", decisionItem.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var responseKeys = CollectKeys(schema);
        Assert.DoesNotContain("sourceAlias", responseKeys);
        Assert.DoesNotContain("sourceId", responseKeys);
        Assert.DoesNotContain("coordinates", responseKeys);
        Assert.DoesNotContain("subjectIndex", responseKeys);
        Assert.Throws<InvalidOperationException>(() => V5SemanticSparseDecisionContractV3_1.Parse(
            JsonDocument.Parse("{\"decisions\":[{\"claims\":[]}]}").RootElement, fixture.Contract, 3, 1));
        Assert.Throws<InvalidOperationException>(() => V5SemanticSparseDecisionContractV3_1.Parse(
            JsonDocument.Parse("{\"decisions\":[{\"ownedIndex\":0,\"claims\":[],\"sourceAlias\":\"forbidden\"}]}").RootElement, fixture.Contract, 3, 1));

        // A zero-proposal response is structurally valid. Omission is a recall signal, never a
        // response-wide transport/contract fault.
        var omitted = V5SemanticSparseDecisionContractV3_1.Parse(JsonDocument.Parse("{\"decisions\":[]}").RootElement,
            fixture.Contract, fixture.Packet.SubjectEvidence.Count, fixture.Packet.ContextOnlyEvidence.Count);
        var omittedBinding = Bind("p5l-omitted", omitted, fixture);
        Assert.Empty(omittedBinding.Bound);
        Assert.Empty(omittedBinding.Refusals);

        var structural = new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Owned one", EvidenceNeeds: []);
        // Parse deliberately does not reject an out-of-range index: JSON Schema is advisory at
        // this carrier, and decision-local quarantine must preserve valid siblings.
        var invalidThenValid = new V5SemanticSparseDecisionResponseV3_1([
            new(99, [structural]),
            new(1, [structural]),
        ]);
        var parsedInvalidThenValid = V5SemanticSparseDecisionContractV3_1.Parse(
            JsonDocument.Parse(JsonSerializer.Serialize(invalidThenValid, CanonicalJson.Options)).RootElement,
            fixture.Contract, fixture.Packet.SubjectEvidence.Count, fixture.Packet.ContextOnlyEvidence.Count);
        var siblingBinding = Bind("p5l-sibling", parsedInvalidThenValid, fixture);
        Assert.Equal("owned-index-out-of-range", siblingBinding.Refusals["sparse-decision-0"]);
        Assert.Equal("source-1:0-9", Assert.Single(siblingBinding.Bound).Claim.Subject.Identity);

        var duplicate = new V5SemanticSparseDecisionResponseV3_1([
            new(1, [structural]),
            new(1, [new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "duplicate", EvidenceNeeds: [])]),
        ]);
        var duplicateBinding = Bind("p5l-duplicate", duplicate, fixture);
        Assert.Equal("duplicate-owned-index", duplicateBinding.Refusals["sparse-decision-1"]);
        Assert.Equal("Owned one", Assert.Single(duplicateBinding.Bound).Claim.Value);

        // Wire order is not identity. The original owned ordinal and claim-local ordinal, rather
        // than sparse entry order, determine the exact binder's durable identity.
        var first = new V5SemanticSparseSubjectDecisionV3_1(0,
            [new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Owned zero", EvidenceNeeds: [])]);
        var third = new V5SemanticSparseSubjectDecisionV3_1(2,
            [new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Owned two", EvidenceNeeds: [])]);
        var forward = Bind("p5l-permutation", new V5SemanticSparseDecisionResponseV3_1([first, third]), fixture);
        var reverse = Bind("p5l-permutation", new V5SemanticSparseDecisionResponseV3_1([third, first]), fixture);
        var forwardThird = Assert.Single(forward.Bound, claim => claim.Claim.Subject.Parts[0].Alias == "OWN-2");
        var reverseThird = Assert.Single(reverse.Bound, claim => claim.Claim.Subject.Parts[0].Alias == "OWN-2");
        Assert.Equal(forwardThird.Claim.ClaimId, reverseThird.Claim.ClaimId);

        // Whole atoms remain implicit, multipart subjects use owned integers, and relation targets
        // retain the separate CONTEXT_ONLY request-local reference table.
        var relationContract = fixture.Contract with { Relations = [new SemanticRelationDefinition("RELATES_TO", "A relation.")] };
        var rich = new V5SemanticSparseDecisionResponseV3_1([
            new(0, [
                new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Owned zero plus one",
                    AdditionalSubjectParts: [new(1)], EvidenceNeeds: []),
                new V5SemanticDecisionClaimV3("RELATES_TO", TargetParts: [new("CONTEXT_ONLY", 0)], EvidenceNeeds: []),
            ]),
        ]);
        var richBinding = V5SemanticSparseDecisionContractV3_1.Bind("p5l-rich", rich, relationContract,
            fixture.Packet.SubjectEvidence, fixture.Packet.ContextOnlyEvidence, fixture.Atoms, fixture.Scope, fixture.OriginalOwnedOrdinals);
        Assert.Equal("source-0:0-10|source-1:0-9", richBinding.Bound.Single(claim => claim.Claim.Predicate == "STRUCTURAL_REGION").Claim.Subject.Identity);
        Assert.Equal("source-3:0-12", richBinding.Bound.Single(claim => claim.Claim.Predicate == "RELATES_TO").Claim.Object!.Identity);
        Assert.Empty(richBinding.Refusals);

        var liveReasoner = new SparseReasoner(new V5SemanticSparseDecisionResponseV3_1([new(1, [structural])]));
        var live = await new DocumentAgentRuntime(liveReasoner, new InMemoryEvidenceRetriever()).RunAsync(
            fixture.Contract with { Projections = [] }, fixture.Graph, fixture.Atoms,
            fixture.Packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal),
            fixture.Packet.SubjectEvidence.Concat(fixture.Packet.ContextOnlyEvidence).Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(V5Protocol.ClaimSchemaVersionV3_1, liveReasoner.ProtocolVersion);
        Assert.Equal(V5SemanticSparseDecisionComposerV3_1.Version, liveReasoner.ComposerVersion);
        Assert.Equal("source-1:0-9", Assert.Single(live.State.Claims).Subject.Identity);

        FreezeArtifact.AssertJson(ArtifactRoot, "closure.v1.json", new
        {
            schemaVersion = "v5-p5l-exhaustive-closure-v1",
            status = "CLOSED_NOT_PROMOTED_EXHAUSTIVE_PROTOCOL_UNRELIABLE",
            evidence = new
            {
                protocol = V5Protocol.ClaimSchemaVersionV3,
                at96 = "UNRELIABLE",
                at32 = new { stopBelowExpected = 1, stopAboveExpected = 2, exactValid = 1, total = 4 },
                conclusion = "Do not continue sharding to 16/8 or raise caps: stop responses below and above exact cardinality are behavioral noncompliance, not transport or truncation.",
            },
            providerCalls = 0,
            goldRead = false,
            sourceP5K = "artifacts/v5-p5k-v3-sharded-canary/audit.v1.json",
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "contract.v1.json", new
        {
            schemaVersion = "v5-p5l-sparse-decision-contract-v1",
            status = "P5L_PROVIDER_FREE_COMPLETE",
            providerCalls = 0,
            goldRead = false,
            oldProtocol = V5Protocol.ClaimSchemaVersionV3,
            newProtocol = V5Protocol.ClaimSchemaVersionV3_1,
            composer = V5SemanticSparseDecisionComposerV3_1.Version,
            outerCardinality = "0 <= decisions.Count <= ownedCount; each entry explicitly carries ownedIndex",
            localFailureSemantics = new
            {
                outOfRangeOwnedIndex = "QUARANTINE_DECISION_ONLY",
                duplicateOwnedIndex = "QUARANTINE_LATER_DUPLICATE_ONLY",
                malformedClaim = "QUARANTINE_DECISION_ONLY",
                omission = "NO_PROPOSAL_NOT_CONTRACT_FAILURE",
            },
            identity = "original semantic request authority + original owned ordinal + claim-local ordinal; independent of sparse response order",
            preserved = new[] { "harness-owned subject identity", "exact binder", "whole-atom no-echo", "multipart owned indices", "OWNED/CONTEXT_ONLY relation targets" },
            runtime = "DocumentAgentRuntime composes and binds V3.1; V3.0 dense responses are compatibility-adapted only for historical tests/replays.",
        });
    }

    private static V5DecisionBindingResultV3 Bind(string requestId, V5SemanticSparseDecisionResponseV3_1 response, Fixture fixture) =>
        V5SemanticSparseDecisionContractV3_1.Bind(requestId, response, fixture.Contract, fixture.Packet.SubjectEvidence,
            fixture.Packet.ContextOnlyEvidence, fixture.Atoms, fixture.Scope, fixture.OriginalOwnedOrdinals);

    private static HashSet<string> CollectKeys(JsonElement element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
                foreach (var property in current.EnumerateObject()) { keys.Add(property.Name); Visit(property.Value); }
            else if (current.ValueKind == JsonValueKind.Array)
                foreach (var item in current.EnumerateArray()) Visit(item);
        }
        Visit(element);
        return keys;
    }

    private sealed class SparseReasoner(V5SemanticSparseDecisionResponseV3_1 response) : ISemanticReasoner
    {
        public string Identity => "offline-p5l-sparse";
        public string? ProtocolVersion { get; private set; }
        public string? ComposerVersion { get; private set; }
        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            using var prompt = JsonDocument.Parse(context.Request.Prompt);
            ProtocolVersion = prompt.RootElement.GetProperty("protocolVersion").GetString();
            ComposerVersion = prompt.RootElement.GetProperty("composerVersion").GetString();
            return ValueTask.FromResult(new SemanticReasoningResult(response, new SemanticReasoningUsage()));
        }
    }

    private sealed record Fixture(DocumentTaskContract Contract, IReadOnlyList<SemanticSourceAtom> Atoms,
        UniversalEvidenceGraph Graph, V5SemanticDecisionRequestPacketV3 Packet, ClaimBindingScope Scope,
        IReadOnlyList<int> OriginalOwnedOrdinals)
    {
        public static Fixture Create()
        {
            var contract = new DocumentTaskContract(V5Protocol.TaskContractVersion, "p5l-sparse", "Sparse positional test.",
                [new SemanticPredicateDefinition("STRUCTURAL_REGION", "A structural region.")], [],
                [new ProjectionRequest("claims", "Claims.")], new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT]),
                "retain-open", new ExecutionBudget(MaxSemanticModelCalls: 1));
            var atoms = new[]
            {
                new SemanticSourceAtom("OWN-0", "source-0", 0, 1, 1, 0, "Owned zero"),
                new SemanticSourceAtom("OWN-1", "source-1", 1, 1, 2, 0, "Owned one"),
                new SemanticSourceAtom("OWN-2", "source-2", 2, 1, 3, 0, "Owned two"),
                new SemanticSourceAtom("HALO-0", "source-3", 3, 1, 4, 0, "Halo context"),
            };
            var graph = EvidenceGraphBuilder.Build(atoms.Select(atom => new SourceObservation($"E:{atom.Alias}", atom.SourceId,
                atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text, new StructuralSpan(0, atom.Text.Length))));
            var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
            var packet = new V5SemanticDecisionRequestPacketV3([byAlias["OWN-0"], byAlias["OWN-1"], byAlias["OWN-2"]],
                [byAlias["HALO-0"]], [], [], [], []);
            var scope = ClaimBindingScope.Create(packet.SubjectEvidence.Select(node => node.SourceAlias),
                packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence).Select(node => node.SourceAlias));
            return new(contract, atoms, graph, packet, scope, [7, 11, 23]);
        }
    }
}
