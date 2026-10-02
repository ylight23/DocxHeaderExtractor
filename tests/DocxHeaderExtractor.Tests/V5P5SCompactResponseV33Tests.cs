using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free compact V3.3 wire qualification. V3.2 remains the replay contract.</summary>
public sealed class V5P5SCompactResponseV33Tests
{
    private const string Root = "artifacts/v5-v33-compact-response";
    private const string P5URoot = "artifacts/v5-p5u-claim-volume";

    [Fact]
    public void Prove_compact_handle_only_whole_atoms_and_audit_parent05_counterfactual()
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var atoms = new[]
        {
            new SemanticSourceAtom("O0", "s0", 0, 1, 1, 0, "Diﬀerences"),
            new SemanticSourceAtom("O1", "s1", 1, 1, 2, 0, "Deﬁnitions"),
            new SemanticSourceAtom("C0", "s2", 2, 1, 3, 0, "Target context"),
        };
        var graph = EvidenceGraphBuilder.Build(atoms.Select(atom => new SourceObservation($"E:{atom.Alias}", atom.SourceId,
            atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text, new StructuralSpan(0, atom.Text.Length))));
        var nodes = graph.Nodes.ToArray();
        var packet = new V5SemanticDecisionRequestPacketV3(nodes[..2], [nodes[2]], [], [], [], []);
        var composed = V5CompactDecisionComposerV3_3.Compose(contract, packet);
        Assert.Equal(V5CompactDecisionComposerV3_3.Version, composed.ComposerVersion);
        using var prompt = JsonDocument.Parse(composed.Prompt);
        var keys = Keys(prompt.RootElement.GetProperty("responseSchema"));
        Assert.DoesNotContain("leftExactContext", keys); Assert.DoesNotContain("rightExactContext", keys);
        var providerKeys = Keys(prompt.RootElement);
        Assert.DoesNotContain("leftExactContext", providerKeys); Assert.DoesNotContain("rightExactContext", providerKeys);

        var whole = Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","value":"x","state":"RESOLVED","evidenceNeeds":[]},{"predicate":"CONTINUES","targetParts":[{"sourceGroup":"CONTEXT_ONLY","sourceIndex":0}],"state":"RESOLVED","evidenceNeeds":[]}]},{"ownedIndex":1,"claims":[{"predicate":"STRUCTURAL_REGION","value":"y","state":"RESOLVED","evidenceNeeds":[]}]}]}""", contract, 2, 1);
        var wholeBinding = V5CompactDecisionContractV3_3.Bind("whole", whole, contract, packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms, ClaimBindingScope.Create(["O0", "O1"], ["O0", "O1", "C0"]));
        Assert.Equal(3, wholeBinding.Bound.Count);
        Assert.Equal("s0:0-10", wholeBinding.Bound[0].Claim.Subject.Identity);
        Assert.Equal("s2:0-14", wholeBinding.Bound.Single(claim => claim.Claim.Predicate == "CONTINUES").Claim.Object!.Identity);
        using var overflowPayload = JsonDocument.Parse($$"""{"decisions":[],"padding":"{{new string('x', composed.ResponseBounds.MaxResponseUtf8Bytes)}}"}""");
        var overflow = Assert.Throws<InvalidOperationException>(() => V5CompactDecisionContractV3_3.Parse(overflowPayload.RootElement, contract, 2, 1));
        Assert.StartsWith("compact-response-byte-budget-exceeded", overflow.Message, StringComparison.Ordinal);

        var unicode = Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","value":"x","subjectSelection":{"verbatimText":"ﬀerences"},"additionalSubjectParts":[{"ownedIndex":1,"selection":{"verbatimText":"ﬁnitions"}}],"state":"RESOLVED","evidenceNeeds":[]}]},{"ownedIndex":1,"claims":[{"predicate":"CONTINUES","targetParts":[{"sourceGroup":"CONTEXT_ONLY","sourceIndex":0,"selection":{"verbatimText":"Target"}}],"state":"RESOLVED","evidenceNeeds":[]}]}]}""", contract, 2, 1);
        var unicodeBinding = V5CompactDecisionContractV3_3.Bind("unicode", unicode, contract, packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms, ClaimBindingScope.Create(["O0", "O1"], ["O0", "O1", "C0"]));
        Assert.Equal(2, unicodeBinding.Bound.Count);

        var contextEcho = Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","subjectSelection":{"verbatimText":"x","leftExactContext":"forbidden"},"state":"RESOLVED","evidenceNeeds":[]}]}]}""", contract, 2, 1);
        Assert.Equal("json-decision-schema-invalid", contextEcho.ParseRefusals["compact-decision-0"]);
        using var wholeEchoPayload = JsonDocument.Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","subjectSelection":{"verbatimText":"Diﬀerences"},"state":"RESOLVED","evidenceNeeds":[]}]}]}""");
        var wholeEcho = V5CompactDecisionContractV3_3.Parse(wholeEchoPayload.RootElement, contract, packet.SubjectEvidence, packet.ContextOnlyEvidence);
        Assert.Empty(wholeEcho.Decisions);
        Assert.Equal("whole-atom-selection-retyped", wholeEcho.ParseRefusals["compact-decision-0"]);
        using var nonExactPayload = JsonDocument.Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","value":"bad","subjectSelection":{"verbatimText":"not-source"},"state":"RESOLVED","evidenceNeeds":[]}]}]}""");
        var nonExact = V5CompactDecisionContractV3_3.Parse(nonExactPayload.RootElement, contract, packet.SubjectEvidence, packet.ContextOnlyEvidence);
        Assert.Equal("compact-selection-not-exact-source-substring", nonExact.ParseRefusals["compact-decision-0"]);

        // V3.2 remains byte/behavior replay compatible: its old parser still accepts its frozen raw response.
        using var oldResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p5o-v32-semantic-cohort-manifest/result.v1.json")));
        var oldRaw = oldResult.RootElement.GetProperty("rows")[0].GetProperty("rawResponse").GetString()!;
        var oldContract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var oldPacks = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", oldContract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true });
        using var oldPayload = JsonDocument.Parse(oldRaw);
        Assert.NotEmpty(V5SemanticSparseDecisionContractV3_1.Parse(oldPayload.RootElement, oldContract, oldPacks[0].OwnedAliases.Count, oldPacks[0].Packet.ContextOnlyEvidence.Count).Decisions);

        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-full31-v32/execution-manifest.v1.json")));
        var all31 = manifest.RootElement.GetProperty("requests").EnumerateArray().Select(row => new
        {
            Owned = row.GetProperty("ownedAliases").GetArrayLength(),
            Visible = row.GetProperty("visibleAliases").GetArrayLength(),
            OldBound = row.GetProperty("maxResponseUtf8Bytes").GetInt32(),
        }).ToArray();
        Assert.Equal(31, all31.Length);
        var oldGlobalBound = all31.Max(row => row.OldBound);
        var newGlobalBound = all31.Max(row => V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(row.Owned, row.Visible).MaxResponseUtf8Bytes);

        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-full31-v32/execution-result.v1.json")));
        var parent05 = execution.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095" && row.GetProperty("parentOrdinal").GetInt32() == 5);
        var raw = parent05.GetProperty("assembledContent").GetString()!;
        var compact = JsonNode.Parse(raw)!.AsObject();
        var parentAtoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src095)).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var parentContract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var parentPacks = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", parentContract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true });
        // parentOrdinal is one-based in the frozen execution artifact; packing IDs are global labels.
        var parentPack = parentPacks[4];
        StripContextAndWholeEcho(compact, parentPack, parentAtoms);
        var compactBytes = Encoding.UTF8.GetByteCount(compact.ToJsonString());
        using var compactPayload = JsonDocument.Parse(compact.ToJsonString());
        var parentParsed = V5CompactDecisionContractV3_3.Parse(compactPayload.RootElement, parentContract,
            parentPack.Packet.SubjectEvidence, parentPack.Packet.ContextOnlyEvidence);
        var parentBinding = V5CompactDecisionContractV3_3.Bind("SRC-095:05-counterfactual", parentParsed, parentContract,
            parentPack.Packet.SubjectEvidence, parentPack.Packet.ContextOnlyEvidence, parentAtoms.Values.ToArray(),
            ClaimBindingScope.Create(parentPack.OwnedAliases, parentPack.VisibleAliases));
        const int oldBound = 52224;
        Assert.Equal(oldBound, oldGlobalBound);
        Assert.True(newGlobalBound <= oldGlobalBound);
        var relationClaims = JsonDocument.Parse(raw).RootElement.GetProperty("decisions").EnumerateArray().SelectMany(d => d.GetProperty("claims").EnumerateArray()).Count(c => c.GetProperty("predicate").GetString() == "CONTINUES");

        FreezeArtifact.AssertJson(Root, "contract.v1.json", new
        {
            status = "P5S_PROVIDER_FREE_COMPACT_GRAMMAR_COMPLETE", providerCalls = 0, goldRead = false, goldMutation = "NONE",
            oldProtocol = V5Protocol.ClaimSchemaVersionV3_2, newProtocol = V5CompactDecisionComposerV3_3.Protocol,
            oldComposer = V5SemanticSparseDecisionComposerV3_1.Version, newComposer = V5CompactDecisionComposerV3_3.Version,
            wholeAtom = "HANDLE_ONLY", strictSubstring = "selection.verbatimText required; occurrence optional", contextEcho = "UNREPRESENTABLE_IN_V3_3_SCHEMA",
            preserved = new[] { "exact binder fail-closed", "multipart owned handles", "OWNED/CONTEXT_ONLY target handles", "V3.2 replay" },
        });
        FreezeArtifact.AssertJson(Root, "v32-replay-compat.v1.json", new { providerCalls = 0, goldRead = false, v32FrozenRawParsedByV32 = true, v33DoesNotRewriteV32 = true });
        FreezeArtifact.AssertJson(Root, "bounds-audit.v1.json", new
        {
            providerCalls = 0, goldRead = false, packCount = all31.Length, oldMaxResponseUtf8Bytes = oldGlobalBound, newCompactMaxResponseUtf8Bytes = newGlobalBound,
            deltaBytes = newGlobalBound - oldGlobalBound,
            deltaPercent = (newGlobalBound - oldGlobalBound) * 100.0 / oldGlobalBound,
            synthetic = "Representative handle-only whole subject, multipart, and target serializes and parses under the compact grammar.",
            worstCaseSchemaProof = "NOT_ESTABLISHED: 129 claims remain representable; no finite aggregate proof below the inherited bound without a relation-volume limit.",
        });
        FreezeArtifact.AssertJson(Root, "parent05-counterfactual-size.v1.json", new
        {
            providerCalls = 0, goldRead = false, parent = "SRC-095:05", oldBytes = 96485, oldMaxResponseUtf8Bytes = oldBound,
            compactBytes, compactWithinOldBound = compactBytes <= oldBound, continuesClaims = relationClaims,
            parserAcceptedDecisions = parentParsed.Decisions.Count, parserRefusals = parentParsed.ParseRefusals.Count,
            binderRefusals = parentBinding.Refusals.Count, boundClaims = parentBinding.Bound.Count,
            parseBind = parentParsed.ParseRefusals.Count == 0 ? "PASS_OR_BINDER_CLASSIFIED" : "PARSE_QUARANTINE_CLASSIFIED",
            conclusion = compactBytes <= oldBound ? "SELECTION_BLOAT_REMOVAL_SUFFICIENT_FOR_THIS_PAYLOAD" : "SELECTION_BLOAT_REMOVAL_INSUFFICIENT; RELATION_VOLUME_AUDIT_REQUIRED_BEFORE_ANY_BYTE_CAP_CHANGE",
        });
    }

    [Fact]
    public void Audit_historical_claim_volume_and_v33_worst_case()
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var audit = BuildHistoricalVolumeAudit();
        var owned = Enumerable.Range(0, 96).Select(index => new EvidenceNode($"E{index}", $"S{index}", $"A{index}", index,
            EvidenceModality.TEXT, new string('a', 636), new EvidenceAnchor($"S{index}", index, new StructuralSpan(0, 636)),
            new Dictionary<string, string?>())).ToArray();
        var context = Enumerable.Range(0, 16).Select(index => new EvidenceNode($"C{index}", $"CS{index}", $"CA{index}", 1000 + index,
            EvidenceModality.TEXT, new string('a', 636), new EvidenceAnchor($"CS{index}", 1000 + index, new StructuralSpan(0, 636)),
            new Dictionary<string, string?>())).ToArray();
        var worst = BuildWorstCasePayload();
        var worstBytes = Encoding.UTF8.GetByteCount(worst.ToJsonString());
        var handlesOnly = (JsonObject)worst.DeepClone();
        foreach (var decision in handlesOnly["decisions"]!.AsArray().OfType<JsonObject>())
        foreach (var claim in decision["claims"]!.AsArray().OfType<JsonObject>())
        {
            claim.Remove("subjectSelection");
            foreach (var part in claim["additionalSubjectParts"]!.AsArray().OfType<JsonObject>()) part.Remove("selection");
            foreach (var part in claim["targetParts"]!.AsArray().OfType<JsonObject>()) part.Remove("selection");
        }
        var handlesOnlyBytes = Encoding.UTF8.GetByteCount(handlesOnly.ToJsonString());
        var withoutTargets = (JsonObject)handlesOnly.DeepClone();
        var withoutAdditional = (JsonObject)handlesOnly.DeepClone();
        var withoutTargetOrAdditional = (JsonObject)handlesOnly.DeepClone();
        foreach (var decision in withoutTargets["decisions"]!.AsArray().OfType<JsonObject>())
        foreach (var claim in decision["claims"]!.AsArray().OfType<JsonObject>()) claim.Remove("targetParts");
        foreach (var decision in withoutAdditional["decisions"]!.AsArray().OfType<JsonObject>())
        foreach (var claim in decision["claims"]!.AsArray().OfType<JsonObject>()) claim.Remove("additionalSubjectParts");
        foreach (var decision in withoutTargetOrAdditional["decisions"]!.AsArray().OfType<JsonObject>())
        foreach (var claim in decision["claims"]!.AsArray().OfType<JsonObject>()) { claim.Remove("targetParts"); claim.Remove("additionalSubjectParts"); }
        using var worstDoc = JsonDocument.Parse(worst.ToJsonString());
        var parserResult = Record.Exception(() => V5CompactDecisionContractV3_3.Parse(worstDoc.RootElement, contract, owned, context));

        // The 129-claim maximum is not task-derived. The legal schema permits many serialized
        // selections per claim, so the measured maximum response cannot fit the inherited cap.
        Assert.True(worstBytes > V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(96, 112).MaxResponseUtf8Bytes);
        Assert.IsType<InvalidOperationException>(parserResult);
        FreezeArtifact.AssertJson(P5URoot, "volume-audit.v1.json", audit);
        FreezeArtifact.AssertJson(P5URoot, "worst-case.v1.json", new
        {
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            claims = 129, decisions = 13, claimsPerDecisionMaximum = 10,
            subjectPartsPerClaim = 6, targetPartsPerClaim = 6, selectionBytesPerPart = 318,
            maxValueUtf8Bytes = V5SemanticDecisionResponseBoundsV3.HistoricalMaxValueUtf8Bytes,
            maxExistingClaimIdUtf8Bytes = V5SemanticDecisionResponseBoundsV3.DurableClaimIdUtf8Bytes,
            serializedUtf8Bytes = worstBytes, configuredMaxResponseUtf8Bytes = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(96, 112).MaxResponseUtf8Bytes,
            handlesOnlyCounterfactualBytes = handlesOnlyBytes, strictSubstringSelectionContributionBytes = worstBytes - handlesOnlyBytes,
            handlesOnlyWithoutTargetsBytes = Encoding.UTF8.GetByteCount(withoutTargets.ToJsonString()),
            handlesOnlyWithoutAdditionalSubjectPartsBytes = Encoding.UTF8.GetByteCount(withoutAdditional.ToJsonString()),
            handlesOnlyWithoutEitherMultipartDimensionBytes = Encoding.UTF8.GetByteCount(withoutTargetOrAdditional.ToJsonString()),
            parserOutcome = parserResult?.Message,
            conclusion = "WORST_CASE_EXCEEDS_BOUND; largest repeated payload dimension is strict-substring text on primary/additional/target parts; current task has no derived claim or relation cardinality cap.",
        });
        FreezeArtifact.AssertJson(P5URoot, "parser-semantic-contract.v1.json", new
        {
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            authority = "SemanticClaimContractV2_1.Validate invoked inside V3.3 ValidateDecision before parser acceptance",
            malformedRelationArity = "decision-local quarantine before binder",
            validSiblingPreserved = true,
            nullClaim = "SAFE_DECISION_QUARANTINE", nullAdditionalPart = "SAFE_DECISION_QUARANTINE", nullTargetPart = "SAFE_DECISION_QUARANTINE",
            totalClaimsOverLimit = "RESPONSE_WIDE_REJECT", decisionClaimsOverLimit = "DECISION_LOCAL_QUARANTINE",
            rawByteBound = "RESPONSE_WIDE_REJECT", canonicalSerializedByteBound = "RESPONSE_WIDE_REJECT",
        });
        FreezeArtifact.AssertJson(P5URoot, "contract.v1.json", new
        {
            status = "PARSER_SEMANTIC_CLOSURE_PASS_WORST_CASE_VOLUME_FAIL_PROVIDER_BLOCKED",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            taskDerivedFiniteClaimBound = "NOT_ESTABLISHED",
            historicalObservedMaxAcceptedClaimsPerOwnedSubject = 2,
            historicalObservedMaxRawClaimsPerOwnedSubject = 8,
            historicalObservedMaxAcceptedClaimsPerPack = 40,
            historicalObservedMaxRawClaimsPerPack = 89,
            maxResponseUtf8Bytes = 49152,
            legalWorstCaseBytesFor129Claims = worstBytes,
            requiredDecision = "Do not change the 129 cap without task-level evidence. Current V3.3 grammar can exceed response bytes at that cap; retain provider gate closed pending a justified finite task bound or a justified smaller field dimension.",
        });
    }

    [Fact]
    public void Quarantine_v33_semantic_shape_and_null_decision_local_failures()
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var valid = "{\"ownedIndex\":1,\"claims\":[{\"predicate\":\"STRUCTURAL_REGION\",\"value\":\"x\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}]}";
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["null-claim"] = "{\"ownedIndex\":0,\"claims\":[null]}",
            ["null-additional-part"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"STRUCTURAL_REGION\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[],\"additionalSubjectParts\":[null]}]}",
            ["null-target-part"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"CONTINUES\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[],\"targetParts\":[null]}]}",
            ["semantic-arity"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"CONTINUES\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}]}",
            ["bad-target-group"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"CONTINUES\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[],\"targetParts\":[{\"sourceGroup\":\"HALO\",\"sourceIndex\":0}]}]}",
            ["target-index-range"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"CONTINUES\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[],\"targetParts\":[{\"sourceGroup\":\"OWNED\",\"sourceIndex\":9}]}]}",
            ["additional-reverse-order"] = "{\"ownedIndex\":0,\"claims\":[{\"predicate\":\"STRUCTURAL_REGION\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[],\"additionalSubjectParts\":[{\"ownedIndex\":2},{\"ownedIndex\":1}]}]}",
            ["eleven-claims"] = "{\"ownedIndex\":0,\"claims\":[" + string.Join(',', Enumerable.Repeat("{\"predicate\":\"STRUCTURAL_REGION\",\"value\":\"x\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}", 11)) + "]}",
        };
        foreach (var (name, badDecision) in cases)
        {
            using var payload = JsonDocument.Parse($"{{\"decisions\":[{badDecision},{valid}]}}");
            var parsed = V5CompactDecisionContractV3_3.Parse(payload.RootElement, contract, 3, 1);
            Assert.Single(parsed.Decisions);
            Assert.True(parsed.ParseRefusals.ContainsKey("compact-decision-0"), name);
        }
        var claim = "{\"predicate\":\"STRUCTURAL_REGION\",\"value\":\"x\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}";
        using var totalOverflow = JsonDocument.Parse($"{{\"decisions\":[{{\"ownedIndex\":0,\"claims\":[{string.Join(',', Enumerable.Repeat(claim, 10))}]}},{{\"ownedIndex\":1,\"claims\":[{string.Join(',', Enumerable.Repeat(claim, 11))}]}}]}}");
        var overflow = Assert.Throws<InvalidOperationException>(() => V5CompactDecisionContractV3_3.Parse(totalOverflow.RootElement, contract, 2, 1));
        Assert.StartsWith("compact-total-claims-bound-exceeded", overflow.Message, StringComparison.Ordinal);
        using var canonicalOverflow = JsonDocument.Parse($"{{\"decisions\":[{{\"ownedIndex\":0,\"claims\":[{{\"predicate\":\"STRUCTURAL_REGION\",\"value\":\"{new string('<', 543)}\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}}]}}]}}");
        var canonical = Assert.Throws<InvalidOperationException>(() => V5CompactDecisionContractV3_3.Parse(canonicalOverflow.RootElement, contract, 2, 1));
        Assert.StartsWith("compact-canonical-response-byte-budget-exceeded", canonical.Message, StringComparison.Ordinal);
        using var indexCases = JsonDocument.Parse("""{"decisions":[{"ownedIndex":0,"claims":[]},{"ownedIndex":0,"claims":[]},{"ownedIndex":7,"claims":[]},{"ownedIndex":1,"claims":[{"predicate":"STRUCTURAL_REGION","value":"ok","state":"RESOLVED","evidenceNeeds":[]}]}]}""");
        var indexes = V5CompactDecisionContractV3_3.Parse(indexCases.RootElement, contract, 4, 1);
        Assert.Equal(new[] { 0, 1 }, indexes.Decisions.Select(decision => decision.OwnedIndex));
        Assert.Equal("duplicate-owned-index", indexes.ParseRefusals["compact-decision-1"]);
        Assert.Equal("owned-index-out-of-range", indexes.ParseRefusals["compact-decision-2"]);
    }

    private static object BuildHistoricalVolumeAudit()
    {
        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-full31-v32/execution-result.v1.json")));
        using var contractAudit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-full31-v32/contract-audit.v1.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-full31-v32/execution-manifest.v1.json")));
        var auditRows = contractAudit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var manifestRows = manifest.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var rawPredicate = new Dictionary<string, int>(StringComparer.Ordinal);
        var validRawPredicate = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundPredicate = new Dictionary<string, int>(StringComparer.Ordinal);
        var perPack = new List<object>();
        var allRelations = new List<(string Pack, int Subject, string Group, int Target)>();
        var maxRawSubjectClaims = 0; var maxBoundSubjectClaims = 0; var maxRawPackClaims = 0; var maxBoundPackClaims = 0; var maxParserAcceptedPackClaims = 0;
        var parserValid = 0; var decisionsTotal = 0; var rawClaimsTotal = 0; var validRawClaimsTotal = 0; var boundClaimsTotal = 0;
        foreach (var row in execution.RootElement.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var parentOrdinal = row.GetProperty("parentOrdinal").GetInt32();
            var packKey = $"{documentId}:{parentOrdinal}";
            var ar = auditRows.Single(item => item.GetProperty("documentId").GetString() == documentId && item.GetProperty("parentOrdinal").GetInt32() == parentOrdinal);
            if (row.GetProperty("assembledContent").ValueKind != JsonValueKind.String) continue;
            using var payload = JsonDocument.Parse(row.GetProperty("assembledContent").GetString()!);
            var decisions = payload.RootElement.GetProperty("decisions").EnumerateArray().ToArray();
            var raw = decisions.SelectMany(decision => decision.GetProperty("claims").EnumerateArray().Select(claim => (Decision: decision, Claim: claim))).ToArray();
            foreach (var claim in raw) Increment(rawPredicate, claim.Claim.GetProperty("predicate").GetString()!);
            rawClaimsTotal += raw.Length; decisionsTotal += decisions.Length;
            maxRawPackClaims = Math.Max(maxRawPackClaims, raw.Length);
            var rawPerSubject = decisions.GroupBy(item => item.GetProperty("ownedIndex").GetInt32()).ToDictionary(group => group.Key, group => group.Sum(item => item.GetProperty("claims").GetArrayLength()));
            maxRawSubjectClaims = Math.Max(maxRawSubjectClaims, rawPerSubject.Values.DefaultIfEmpty().Max());
            if (ar.GetProperty("parser").GetString() != "PARSER_VALID")
            {
                perPack.Add(new { pack = packKey, parser = ar.GetProperty("parser").GetString(), decisions = decisions.Length,
                    rawClaims = raw.Length, acceptedBoundClaims = 0, rawByPredicate = raw.GroupBy(item => item.Claim.GetProperty("predicate").GetString()!).ToDictionary(group => group.Key, group => group.Count()),
                    boundByPredicate = new Dictionary<string, int>() });
                continue;
            }
            parserValid++;
            var boundItems = ar.GetProperty("bound").EnumerateArray().ToArray();
            var parserAcceptedClaims = ar.GetProperty("rawClaims").GetInt32();
            var boundPerAlias = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var claim in raw) Increment(validRawPredicate, claim.Claim.GetProperty("predicate").GetString()!);
            foreach (var claim in boundItems)
            {
                Increment(boundPredicate, claim.GetProperty("predicate").GetString()!);
                if (claim.TryGetProperty("sourceParts", out var sourceParts) && sourceParts.ValueKind == JsonValueKind.Array && sourceParts.GetArrayLength() > 0)
                {
                    var alias = sourceParts[0].GetProperty("Alias").GetString()!;
                    boundPerAlias[alias] = boundPerAlias.GetValueOrDefault(alias) + 1;
                }
            }
            // Map every observed target to its frozen local request universe. The full lane uses only
            // V3.2 response artifacts; this is accounting, not semantic validation against Gold.
            var manifestRow = manifestRows.Single(item => item.GetProperty("documentId").GetString() == documentId && item.GetProperty("parentOrdinal").GetInt32() == parentOrdinal);
            var ownedCount = manifestRow.GetProperty("ownedAliases").GetArrayLength();
            var contextCount = manifestRow.GetProperty("visibleAliases").GetArrayLength() - ownedCount;
            foreach (var item in raw.Where(item => IsRelation(item.Claim.GetProperty("predicate").GetString()!)))
            {
                var subject = item.Decision.GetProperty("ownedIndex").GetInt32();
                if (!item.Claim.TryGetProperty("targetParts", out var targets) || targets.ValueKind != JsonValueKind.Array) continue;
                foreach (var target in targets.EnumerateArray())
                {
                    var group = target.GetProperty("sourceGroup").GetString()!;
                    var index = target.GetProperty("sourceIndex").GetInt32();
                    if (index >= 0 && index < (group == "OWNED" ? ownedCount : group == "CONTEXT_ONLY" ? contextCount : 0))
                        allRelations.Add((packKey, subject, group, index));
                }
            }
            var rawCounts = raw.GroupBy(item => item.Claim.GetProperty("predicate").GetString()!).ToDictionary(group => group.Key, group => group.Count());
            var boundCounts = boundItems.GroupBy(item => item.GetProperty("predicate").GetString()!).ToDictionary(group => group.Key, group => group.Count());
            var rawCount = raw.Length; var boundCount = boundItems.Length;
            maxBoundPackClaims = Math.Max(maxBoundPackClaims, boundCount);
            maxBoundSubjectClaims = Math.Max(maxBoundSubjectClaims, boundPerAlias.Values.DefaultIfEmpty().Max());
            validRawClaimsTotal += rawCount; boundClaimsTotal += boundCount;
            maxParserAcceptedPackClaims = Math.Max(maxParserAcceptedPackClaims, parserAcceptedClaims);
            perPack.Add(new { pack = packKey, parser = "PARSER_VALID", decisions = decisions.Length, rawResponseClaims = rawCount, v32ParseAcceptedClaims = parserAcceptedClaims, acceptedBoundClaims = boundCount, maxRawClaimsPerOwnedSubject = rawPerSubject.Values.DefaultIfEmpty().Max(), maxBoundClaimsPerOwnedSubject = boundPerAlias.Values.DefaultIfEmpty().Max(), rawByPredicate = rawCounts, boundByPredicate = boundCounts });
        }
        var duplicates = allRelations.Count - allRelations.Distinct().Count();
        var parent05 = execution.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095" && row.GetProperty("parentOrdinal").GetInt32() == 5);
        using var parent05Response = JsonDocument.Parse(parent05.GetProperty("assembledContent").GetString()!);
        var parent05Edges = parent05Response.RootElement.GetProperty("decisions").EnumerateArray().SelectMany(decision =>
            decision.GetProperty("claims").EnumerateArray().Where(claim => claim.GetProperty("predicate").GetString() == "CONTINUES")
                .SelectMany(claim => claim.GetProperty("targetParts").EnumerateArray().Select(target =>
                    (Subject: decision.GetProperty("ownedIndex").GetInt32(), Group: target.GetProperty("sourceGroup").GetString()!, Target: target.GetProperty("sourceIndex").GetInt32())))).ToArray();
        var parent05UniqueEdges = parent05Edges.Distinct().Count();
        var relationByPack = allRelations.GroupBy(item => item.Pack).Select(group => new
        {
            pack = group.Key, targetParts = group.Count(), uniqueSubjectTargetPairs = group.Distinct().Count(), duplicatePairs = group.Count() - group.Distinct().Count(),
            ownSequentialEdges = group.Count(item => item.Group == "OWNED" && item.Target == item.Subject + 1),
            distinctTargetsPerSubjectMaximum = group.GroupBy(item => item.Subject).Select(s => s.Select(item => (item.Group, item.Target)).Distinct().Count()).DefaultIfEmpty().Max(),
        }).ToArray();
        return new
        {
            providerCalls = 0, goldRead = false, goldMutation = "NONE", source = "frozen V3.2 full31 artifacts; 31 attempts, 23 complete JSON responses, 22 parser-valid",
            attemptedPacks = 31, completeJsonResponses = 23, parserValidPacks = parserValid, unavailableOrParserInvalid = 31 - parserValid,
            rawSparseDecisions = decisionsTotal,
            rawResponseClaims = rawClaimsTotal, parserValidRawResponseClaims = validRawClaimsTotal,
            v32ParseAcceptedClaims = contractAudit.RootElement.GetProperty("aggregate").GetProperty("rawClaims").GetInt32(), acceptedBoundClaims = boundClaimsTotal,
            maxRawClaimsPerPack = maxRawPackClaims, maxV32ParseAcceptedClaimsPerPack = maxParserAcceptedPackClaims, maxAcceptedClaimsPerPack = maxBoundPackClaims,
            maxRawClaimsPerOwnedSubject = maxRawSubjectClaims, maxAcceptedClaimsPerOwnedSubject = maxBoundSubjectClaims,
            rawByPredicate = rawPredicate, parserValidRawByPredicate = validRawPredicate, acceptedByPredicate = boundPredicate, packs = perPack,
            relationAudit = new { source = "parser-valid V3.2 subset; target handles checked against frozen owned/context universe", legalTargetParts = allRelations.Count, duplicateSubjectTargetPairs = duplicates, totalPairs = allRelations.Count, sequentialOwnedEdges = allRelations.Count(item => item.Group == "OWNED" && item.Target == item.Subject + 1), packBreakdown = relationByPack,
                parent05Counterexample = new { predicate = "CONTINUES", targetParts = parent05Edges.Length, uniqueSubjectTargetPairs = parent05UniqueEdges, duplicatePairs = parent05Edges.Length - parent05UniqueEdges,
                    forwardOwnedAdjacentEdges = parent05Edges.Count(edge => edge.Group == "OWNED" && edge.Target == edge.Subject + 1), structurallyInRange = "YES; checked in previous V3.2 binder result for valid claims; raw target indexes all within 96-owned universe", semanticRedundancy = "NOT_ADJUDICATED_WITHOUT_GOLD_OR_DOCUMENT_STRUCTURE_RULE" } },
            taskCardinalityAuthority = "NO_TASK_DERIVED_FINITE_CLAIM_OR_RELATION_CARDINALITY_FOUND; historical maxima are observational only",
        };
    }

    private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    private static bool IsRelation(string predicate) => predicate is "PARENT_OF" or "REFERENCES" or "SAME_ENTITY" or "CONTINUES";

    private static JsonObject BuildWorstCasePayload()
    {
        var decisions = new JsonArray();
        var claimCount = 0;
        for (var ownedIndex = 0; ownedIndex < 13; ownedIndex++)
        {
            var claims = new JsonArray();
            var count = ownedIndex == 12 ? 9 : 10;
            for (var c = 0; c < count; c++)
            {
                var additional = new JsonArray();
                for (var index = ownedIndex + 1; index <= ownedIndex + 5; index++)
                    additional.Add(new JsonObject { ["ownedIndex"] = index, ["selection"] = Selection() });
                var targets = new JsonArray();
                for (var index = 0; index < 6; index++) targets.Add(new JsonObject { ["sourceGroup"] = "OWNED", ["sourceIndex"] = index, ["selection"] = Selection() });
                claims.Add(new JsonObject
                {
                    ["predicate"] = "CONTINUES", ["existingClaimId"] = new string('i', 42),
                    ["subjectSelection"] = Selection(), ["additionalSubjectParts"] = additional, ["targetParts"] = targets,
                    ["state"] = "OPEN", ["evidenceNeeds"] = new JsonArray("GLOBAL_TARGET", "MORE_CONTEXT", "IDENTITY_DISAMBIGUATION", "STRUCTURAL_CONTEXT", "LAYOUT_EVIDENCE", "VISUAL_EVIDENCE"),
                });
                claimCount++;
            }
            decisions.Add(new JsonObject { ["ownedIndex"] = ownedIndex, ["claims"] = claims });
        }
        Assert.Equal(129, claimCount);
        return new JsonObject { ["decisions"] = decisions };
        static JsonObject Selection() => new() { ["verbatimText"] = new string('a', 318), ["occurrence"] = 2 };
    }

    private static V5CompactDecisionResponseV3_3 Parse(string raw, DocumentTaskContract contract, int owned, int context) =>
        V5CompactDecisionContractV3_3.Parse(JsonDocument.Parse(raw).RootElement, contract, owned, context);
    private static HashSet<string> Keys(JsonElement element) { var keys = new HashSet<string>(StringComparer.Ordinal); void Visit(JsonElement e) { if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) { keys.Add(p.Name); Visit(p.Value); } else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Visit(x); } Visit(element); return keys; }
    private static void StripContextAndWholeEcho(JsonObject root, V5PackedDecisionRequestV3 pack,
        IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        foreach (var decision in root["decisions"]!.AsArray().OfType<JsonObject>())
        foreach (var claim in decision["claims"]!.AsArray().OfType<JsonObject>())
        {
            var primaryIndex = decision["ownedIndex"]?.GetValue<int>() ?? -1;
            Strip(claim, "subjectSelection", Alias(pack.Packet.SubjectEvidence, primaryIndex));
            foreach (var part in claim["additionalSubjectParts"]?.AsArray().OfType<JsonObject>() ?? [])
                Strip(part, "selection", Alias(pack.Packet.SubjectEvidence, part["ownedIndex"]?.GetValue<int>() ?? -1));
            foreach (var part in claim["targetParts"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var group = part["sourceGroup"]?.GetValue<string>();
                var source = string.Equals(group, "CONTEXT_ONLY", StringComparison.Ordinal)
                    ? pack.Packet.ContextOnlyEvidence : pack.Packet.SubjectEvidence;
                Strip(part, "selection", Alias(source, part["sourceIndex"]?.GetValue<int>() ?? -1));
            }
        }
        string? Alias(IReadOnlyList<EvidenceNode> nodes, int index) => index >= 0 && index < nodes.Count ? nodes[index].SourceAlias : null;
        void Strip(JsonObject parent, string key, string? alias)
        {
            if (parent[key] is not JsonObject selection) return;
            selection.Remove("leftExactContext"); selection.Remove("rightExactContext");
            if (selection.Count == 0 || alias is not null && atoms.TryGetValue(alias, out var atom) &&
                string.Equals(selection["verbatimText"]?.GetValue<string>(), atom.Text, StringComparison.Ordinal))
                parent.Remove(key);
        }
    }
}
