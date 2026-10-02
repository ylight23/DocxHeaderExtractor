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

        var whole = Parse("""{"decisions":[{"ownedIndex":0,"claims":[{"predicate":"STRUCTURAL_REGION","value":"x","state":"RESOLVED","evidenceNeeds":[]},{"predicate":"CONTINUES","targetParts":[{"sourceGroup":"CONTEXT_ONLY","sourceIndex":0}],"state":"RESOLVED","evidenceNeeds":[]}]}]}""", contract, 2, 1);
        var wholeBinding = V5CompactDecisionContractV3_3.Bind("whole", whole, contract, packet.SubjectEvidence, packet.ContextOnlyEvidence, atoms, ClaimBindingScope.Create(["O0", "O1"], ["O0", "O1", "C0"]));
        Assert.Equal(2, wholeBinding.Bound.Count);
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
