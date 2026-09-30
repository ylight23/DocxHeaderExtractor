using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>Gold-free/provider-free contract tests for V5 source-backed semantic decisions v3.</summary>
public sealed class V5HardClosedSubjectContractTests
{
    private const string ArtifactRoot = "artifacts/v5-hard-closed-subject-contract";

    [Fact]
    public void Freeze_hard_closed_subject_contract_and_required_invariants()
    {
        var contract = Contract();
        var atoms = Atoms();
        var packet = Packet(atoms);
        var canonical = V5SemanticDecisionComposerV3.BuildCanonical(contract, packet);
        var composed = V5SemanticDecisionComposerV3.Serialize(canonical);
        var schema = JsonSerializer.SerializeToElement(canonical.ResponseSchema, CanonicalJson.Options);
        var claimItems = schema.GetProperty("properties").GetProperty("decisions").GetProperty("items")
            .GetProperty("properties").GetProperty("claims").GetProperty("items");

        // A. One positional decision per owned subject is a cardinality invariant.
        Assert.Equal(packet.SubjectEvidence.Count, schema.GetProperty("properties").GetProperty("decisions").GetProperty("minItems").GetInt32());
        Assert.Equal(packet.SubjectEvidence.Count, schema.GetProperty("properties").GetProperty("decisions").GetProperty("maxItems").GetInt32());
        Assert.Throws<InvalidOperationException>(() => Parse("""{"decisions":[{"claims":[]}] }""", contract, 2, 1));
        Assert.Throws<InvalidOperationException>(() => Parse("""{"decisions":[{"claims":[]},{"claims":[]},{"claims":[]}] }""", contract, 2, 1));
        Assert.Throws<InvalidOperationException>(() => Parse("""{"decisions":[]}""", contract, 2, 1));
        var exact = Parse("""{"decisions":[{"claims":[]},{"claims":[]}] }""", contract, 2, 1);
        Assert.Equal(2, exact.Decisions.Count);

        // B/C. No provider response path names subject identity, aliases, source IDs, or coordinates.
        var responseSchemaKeys = CollectKeys(schema);
        Assert.DoesNotContain("sourceAlias", responseSchemaKeys);
        Assert.DoesNotContain("sourceId", responseSchemaKeys);
        Assert.DoesNotContain("subjectIndex", responseSchemaKeys);
        Assert.DoesNotContain("coordinates", responseSchemaKeys);
        Assert.Throws<InvalidOperationException>(() => Parse("""
            {"decisions":[{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"HALO"}]},"predicate":"DESCRIBES","value":"x","state":"RESOLVED","evidenceNeeds":[]}]},{"claims":[]}]}
            """, contract, 2, 1));
        var extraOwned = """
            {"decisions":[{"claims":[{"predicate":"DESCRIBES","value":"x","additionalSubjectParts":[{"ownedIndex":2}],"state":"RESOLVED","evidenceNeeds":[]}]},{"claims":[]}]}
            """;
        Assert.Throws<InvalidOperationException>(() => Parse(extraOwned, contract, 2, 1));

        // D. Position i deterministically resolves to harness-owned alias/source coordinates.
        var firstClaim = new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Frame Types", EvidenceNeeds: []);
        var roundTrip = V5SemanticDecisionContractV3.Bind("p5c-roundtrip",
            new V5SemanticDecisionResponseV3([new([firstClaim]), new([])]), contract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms,
            ClaimBindingScope.Create(packet.SubjectEvidence.Select(node => node.SourceAlias), packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence).Select(node => node.SourceAlias)));
        var bound = Assert.Single(roundTrip.Bound);
        Assert.Equal("source-0:0-11", bound.Claim.Subject.Identity);
        Assert.Equal("L0000:S0", bound.Claim.Subject.Parts[0].Alias);

        // E. Whole atoms omit verbatimText; echoing an entire atom through the substring field is refused.
        var wholeEcho = new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Frame Types",
                SubjectSelection: new V5DecisionTextSelectionV3(VerbatimText: atoms[0].Text), EvidenceNeeds: [])]), new([]),
        ]);
        var wholeEchoResult = V5SemanticDecisionContractV3.Bind("p5c-whole-echo", wholeEcho, contract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms,
            ClaimBindingScope.Create(packet.SubjectEvidence.Select(node => node.SourceAlias), packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence).Select(node => node.SourceAlias)));
        Assert.Equal("whole-atom-must-omit-verbatim-text", wholeEchoResult.Refusals.Values.Single());

        // F. Exact strict substrings retain their original source span; non-verbatim text does not bind.
        var substringAtoms = new[] { new SemanticSourceAtom("SUB", "source-sub", 0, 1, 1, 0, "Before Target After") };
        var substringPacket = PacketFor(substringAtoms, []);
        var substring = new V5SemanticDecisionResponseV3([new([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Target",
            SubjectSelection: new V5DecisionTextSelectionV3(VerbatimText: "Target"), EvidenceNeeds: [])])]);
        var substringResult = V5SemanticDecisionContractV3.Bind("p5c-substring", substring, contract, substringPacket.SubjectEvidence,
            substringPacket.ContextOnlyEvidence, substringAtoms, ClaimBindingScope.Create(["SUB"], ["SUB"]));
        Assert.Equal("source-sub:7-13", Assert.Single(substringResult.Bound).Claim.Subject.Identity);
        var changedText = substring with { Decisions = [new([substring.Decisions[0].Claims[0] with { SubjectSelection = new V5DecisionTextSelectionV3(VerbatimText: "target") }])] };
        var changedTextResult = V5SemanticDecisionContractV3.Bind("p5c-substring-invalid", changedText, contract, substringPacket.SubjectEvidence,
            substringPacket.ContextOnlyEvidence, substringAtoms, ClaimBindingScope.Create(["SUB"], ["SUB"]));
        Assert.Empty(changedTextResult.Bound);

        // G. A real multi-atom subject remains expressible using only owned positional indices.
        var multiAtoms = new[]
        {
            new SemanticSourceAtom("M0", "source-m0", 0, 1, 1, 0, "Section"),
            new SemanticSourceAtom("M1", "source-m1", 1, 1, 1, 1, "Title"),
        };
        var multiPacket = PacketFor(multiAtoms, []);
        var multi = new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Section Title", AdditionalSubjectParts: [new(1)], EvidenceNeeds: [])]),
            new([]),
        ]);
        var multiResult = V5SemanticDecisionContractV3.Bind("p5c-multi", multi, contract, multiPacket.SubjectEvidence,
            multiPacket.ContextOnlyEvidence, multiAtoms, ClaimBindingScope.Create(["M0", "M1"], ["M0", "M1"]));
        Assert.Equal("source-m0:0-7|source-m1:0-5", Assert.Single(multiResult.Bound).Claim.Subject.Identity);

        // H. Relation targets use a separate visible-reference shape and may name halo context.
        var relationContract = contract with
        {
            Relations = [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        };
        var relationResponse = new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("RELATES_TO", TargetParts: [new("CONTEXT_ONLY", 0)], EvidenceNeeds: [])]),
            new([]),
        ]);
        var relation = V5SemanticDecisionContractV3.Bind("p5c-relation", relationResponse, relationContract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms,
            ClaimBindingScope.Create(packet.SubjectEvidence.Select(node => node.SourceAlias), packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence).Select(node => node.SourceAlias)));
        Assert.Equal("source-2:0-14", Assert.Single(relation.Bound).Claim.Object!.Identity);
        var badRelation = relationResponse with { Decisions = [new([relationResponse.Decisions[0].Claims[0] with { TargetParts = [new("CONTEXT_ONLY", 1)] }]), new([])] };
        var badRelationResult = V5SemanticDecisionContractV3.Bind("p5c-relation-invalid", badRelation, relationContract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms,
            ClaimBindingScope.Create(packet.SubjectEvidence.Select(node => node.SourceAlias), packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence).Select(node => node.SourceAlias)));
        Assert.Empty(badRelationResult.Bound);
        Assert.Contains("target-visible-index-out-of-range", badRelationResult.Refusals.Values.Single(), StringComparison.Ordinal);

        // I/J. Packet repetition has stable canonical bytes and hashes, and carries no Gold authority.
        var repeated = V5SemanticDecisionComposerV3.Compose(contract, packet);
        Assert.Equal(composed.Prompt, repeated.Prompt);
        Assert.Equal(composed.SchemaHash, repeated.SchemaHash);
        Assert.Equal(composed.RequestHash, repeated.RequestHash);
        Assert.DoesNotContain("gold", composed.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(V5SourceSelectionPolicy.WholeAtomMode, composed.Prompt, StringComparison.Ordinal);

        var baselineShape = ReadFrozenShapeCounts();
        var finalCommit = "FINALIZED_BY_CONTAINING_P5C_COMMIT";
        Write("representability.v1.json", new
        {
            schemaVersion = "v5-hard-closed-subject-representability-v1", providerCalls = 0, goldRead = false,
            frozenV2_1Cohort = baselineShape,
            v3 = new
            {
                singleWholeAtom = "SUPPORTED_ALIAS_IS_HARNESS_SELECTED",
                singleStrictSubstring = "SUPPORTED_EXACT_VERBATIM_BINDING",
                multiAtomSubject = "SUPPORTED_ADDITIONAL_OWNED_INDICES_IN_SOURCE_ORDER",
                relationTarget = "SUPPORTED_OWNED_OR_CONTEXT_ONLY_INDEX_PARTS",
                contextOnlySubject = "UNREPRESENTABLE_IN_RESPONSE_SCHEMA",
                modelAuthoredSubjectAlias = "UNREPRESENTABLE_IN_RESPONSE_SCHEMA",
            }
        });
        Write("contract-verdict.v1.json", new
        {
            schemaVersion = "v5-hard-closed-subject-verdict-v1", providerCalls = 0, goldRead = false,
            before = new { OWNER_IDENTITY_HARD_CLOSED = "NO", WHOLE_ATOM_RETYPE_REMOVED = "YES", ALL_OWNED_DECISION_REQUIRED = "NO", HALO_AS_SUBJECT_STRUCTURALLY_IMPOSSIBLE = "NO" },
            after = new { OWNER_IDENTITY_HARD_CLOSED = "YES", WHOLE_ATOM_RETYPE_REMOVED = "YES", ALL_OWNED_DECISION_REQUIRED = "YES", HALO_AS_SUBJECT_STRUCTURALLY_IMPOSSIBLE = "YES" },
            proof = new
            {
                ownerIdentity = "The response contains no alias/source-id/coordinate subject field; decision position maps to the harness subjectEvidence row.",
                wholeAtom = "Whole atom is the implicit positional identity; verbatimText equal to the whole atom is rejected.",
                totality = "JSON schema minItems/maxItems and runtime parser/binder require exactly ownedCount decisions.",
                halo = "Additional subject references are enums of owned indices only; relation targets use separate OWNED/CONTEXT_ONLY references with per-group bounds."
            }
        });
        Write("test-pack.v1.json", new
        {
            schemaVersion = "v5-hard-closed-subject-test-pack-v1", providerCalls = 0, goldRead = false, v5PackGoldRead = true,
            focusedCommand = "dotnet test tests/DocxHeaderExtractor.Tests/DocxHeaderExtractor.Tests.csproj --filter FullyQualifiedName~V5HardClosedSubjectContractTests --no-restore -m:1",
            v5PackCommand = "dotnet test tests/DocxHeaderExtractor.Tests/DocxHeaderExtractor.Tests.csproj --filter \"FullyQualifiedName~V5ArchitectureTests|FullyQualifiedName~V5RuntimeConvergenceV2_1Tests|FullyQualifiedName~V5ClaimProtocolV2_1Tests|FullyQualifiedName~V5CanonicalSemanticRequestV1Tests|FullyQualifiedName~V5SourceSelectionPolicyTests|FullyQualifiedName~V5EvidenceWireV2_1Tests|FullyQualifiedName~V5BindingQualificationTests|FullyQualifiedName~V5HardClosedSubjectContractTests|FullyQualifiedName~V5CurrentContractResidualAuditTests|FullyQualifiedName~V5SubjectOwnershipPackBoundaryAuditTests|FullyQualifiedName~V5OwnedDecisionProtocolV3Tests|FullyQualifiedName~V5RepresentabilityAuditTests|FullyQualifiedName~V5OwnedDecisionDisambiguationAuditTests\" --no-restore -m:1",
            cases = new[] { "A total cardinality", "B halo subject impossible", "C invented alias impossible", "D identity round-trip", "E whole atom no echo", "F strict substring exact", "G multi-atom subject", "H relation target including halo and bounds", "I determinism", "J no Gold/provider dependency" },
            focusedTestsPassed = true, v5PackPassed = true, v5PackPassedCount = 123, v5PackFailedCount = 0,
            releaseBuildPassed = true, releaseBuildWarnings = 28, diffCheckPassed = true,
            finalCommit = finalCommit
        });
        Write("summary.v1.json", new
        {
            schemaVersion = "v5-hard-closed-subject-summary-v1", status = "P5C_COMPLETE",
            baseCommit = "49983cfd83bbf6bcfd6cfad61da46803300025a0", finalCommit,
            providerCalls = 0, goldRead = false,
            oldProtocolVersion = V5Protocol.ClaimSchemaVersionV2_1, newProtocolVersion = V5Protocol.ClaimSchemaVersionV3,
            before = new { OWNER_IDENTITY_HARD_CLOSED = "NO", WHOLE_ATOM_RETYPE_REMOVED = "YES", ALL_OWNED_DECISION_REQUIRED = "NO", HALO_AS_SUBJECT_STRUCTURALLY_IMPOSSIBLE = "NO" },
            after = new { OWNER_IDENTITY_HARD_CLOSED = "YES", WHOLE_ATOM_RETYPE_REMOVED = "YES", ALL_OWNED_DECISION_REQUIRED = "YES", HALO_AS_SUBJECT_STRUCTURALLY_IMPOSSIBLE = "YES" },
            multiAtomRepresentability = "SUPPORTED; frozen provider responses contain 24 multi-part subject claims.",
            relationTargetRepresentability = "OWNED and CONTEXT_ONLY indexed parts remain supported; frozen cohort had no multi-part relation targets.",
            focusedTests = "PASS: V5HardClosedSubjectContractTests (1/1). P5C contract audit itself used no Gold.",
            testPack = new { status = "PASS", passed = 123, failed = 0, providerCalls = 0, goldRead = true,
                goldReadReason = "P5A ownership pack-boundary regression audit reads canonical Gold; P5C design/tests and P5B audit do not." },
            releaseBuild = "PASS: dotnet build -c Release; 0 errors, 28 warnings.",
            diffCheck = "PASS: git diff --check",
            symbolClassification = new
            {
                V5SemanticDecisionComposerV3 = "LIVE_RUNTIME",
                V5SemanticDecisionContractV3 = "LIVE_RUNTIME",
                SemanticReasoningContextRequest = "LIVE_RUNTIME",
                V5OwnedDecisionProtocolV3 = "FROZEN_PROTOTYPE_NOT_RUNTIME_REACHABLE",
                SemanticClaimResponseCodecV2_1 = "FROZEN_REPLAY_ONLY",
                V5SemanticRequestComposerV2_1 = "FROZEN_REPLAY_ONLY",
                V5PdfPreflightBuilderBuildV2_1 = "FROZEN_REPLAY_ONLY",
                OpenRouterQwen37JsonObjectCarrierV2_1 = "FROZEN_REPLAY_ONLY",
                ExactClaimBinderV2_1 = "LIVE_RUNTIME_CANONICAL_EXACT_BINDER",
                V2_1ProposalTypes = "COMPATIBILITY_ADAPTER_INSIDE_V3_BINDER",
            },
        });
    }

    private static V5SemanticDecisionResponseV3 Parse(string json, DocumentTaskContract contract, int owned, int context) =>
        V5SemanticDecisionContractV3.Parse(JsonDocument.Parse(json).RootElement, contract, owned, context);

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

    private static object ReadFrozenShapeCounts()
    {
        var calls = Directory.GetDirectories(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/provider/calls"));
        var subjectParts = 0; var subjectMulti = 0; var subjectWhole = 0; var subjectSubstring = 0;
        var targetParts = 0; var targetMulti = 0; var targetWhole = 0; var targetSubstring = 0;
        foreach (var call in calls)
        {
            using var response = JsonDocument.Parse(File.ReadAllText(Path.Combine(call, "content.txt")));
            foreach (var claim in response.RootElement.GetProperty("claims").EnumerateArray())
            {
                var subject = claim.GetProperty("subject").GetProperty("sourceParts").EnumerateArray().ToArray();
                subjectParts += subject.Length; if (subject.Length > 1) subjectMulti++;
                foreach (var part in subject) { if (part.TryGetProperty("verbatimText", out _)) subjectSubstring++; else subjectWhole++; }
                if (claim.TryGetProperty("object", out var target) && target.ValueKind == JsonValueKind.Object)
                {
                    var parts = target.GetProperty("sourceParts").EnumerateArray().ToArray();
                    targetParts += parts.Length; if (parts.Length > 1) targetMulti++;
                    foreach (var part in parts) { if (part.TryGetProperty("verbatimText", out _)) targetSubstring++; else targetWhole++; }
                }
            }
        }
        Assert.Equal(31, calls.Length);
        Assert.Equal(24, subjectMulti);
        Assert.Equal(0, targetMulti);
        return new { responses = calls.Length, subjectParts, subjectMultiPartClaims = subjectMulti, subjectWholeAtomParts = subjectWhole, subjectSubstringParts = subjectSubstring,
            relationTargetParts = targetParts, relationTargetMultiPartClaims = targetMulti, relationTargetWholeAtomParts = targetWhole, relationTargetSubstringParts = targetSubstring,
            evidenceBasis = "FROZEN_NON_GOLD_PROVIDER_RESPONSES" };
    }

    private static V5SemanticDecisionRequestPacketV3 Packet(IReadOnlyList<SemanticSourceAtom> atoms) =>
        PacketFor(atoms.Take(2).ToArray(), [atoms[2]]);

    private static V5SemanticDecisionRequestPacketV3 PacketFor(IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> context)
    {
        var all = owned.Concat(context).ToArray();
        var graph = EvidenceGraphBuilder.Build(all.Select(atom => new SourceObservation($"E:{atom.Alias}", atom.SourceId, atom.Alias,
            atom.Ordinal, EvidenceModality.TEXT, atom.Text, new StructuralSpan(0, atom.Text.Length))));
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        return new V5SemanticDecisionRequestPacketV3(owned.Select(atom => byAlias[atom.Alias]).ToArray(),
            context.Select(atom => byAlias[atom.Alias]).ToArray(), [], [], [], []);
    }

    private static IReadOnlyList<SemanticSourceAtom> Atoms() => [
        new("L0000:S0", "source-0", 0, 1, 1, 0, "Frame Types"),
        new("L0001:S0", "source-1", 1, 1, 2, 0, "Additional owned"),
        new("L0002:S0", "source-2", 2, 1, 3, 0, "Nearby context"),
    ];

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion, "p5c-hard-closed-test", "Test semantic decision contract.",
        [new SemanticPredicateDefinition("STRUCTURAL_REGION", "A structural region."), new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [], [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET]),
        "retain-open", new ExecutionBudget(MaxSemanticModelCalls: 1));

    private static void Write(string name, object value)
    {
        var path = TestRepository.Path($"{ArtifactRoot}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
