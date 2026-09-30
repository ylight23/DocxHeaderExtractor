using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P1 of the canonical-semantic-request extraction: <see cref="V5SemanticRequestComposerV2_1"/> now
/// builds a typed <see cref="CanonicalSemanticRequestV2_1"/> (task vocabulary, arity, response
/// schema, source-selection policy, instructions, evidence - nothing about transport) and serializes
/// it, instead of assembling an anonymous payload inline. This is a zero-byte-drift refactor: every
/// test here either proves the new path reproduces the pre-extraction output exactly, or replays a
/// real frozen artifact end to end. Never calls a provider, never reads Gold.
/// <para>
/// <see cref="OldReferenceCompose"/> is a verbatim copy of <c>Compose</c>'s body immediately before
/// this extraction (commit 12d15a6) - the reference this refactor must reproduce byte for byte, kept
/// here specifically so a later, unrelated change to the real composer cannot silently make this
/// suite compare the new code against itself.
/// </para>
/// </summary>
public sealed class V5CanonicalSemanticRequestV1Tests
{
    private static readonly DocumentTaskContract Contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private const string ArtifactPath = "artifacts/v5-canonical-semantic-request/extraction-equivalence.v1.json";
    private const string RemediationSelectionPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-selection.v1.json";

    private static V5ComposedSemanticRequest OldReferenceCompose(DocumentTaskContract contract, V5EvidencePacketV2_1 packet)
    {
        contract.Validate();
        var instructions = string.Join("\n", new[]
        {
            "You are a task-defined semantic reasoner.",
            "The task contract vocabulary is authoritative; use only declared predicates and relations.",
            "claimShapes is authoritative for arity: a UNARY predicate must never carry an object; a RELATION must never carry a value and requires an object once RESOLVED.",
            "Every claim must contain evidenceNeeds explicitly: RESOLVED sends evidenceNeeds: []; OPEN and CONFLICTED send at least one need.",
            "A source part never has a selectionMode field. sourceSelectionPolicy in this request defines the exact wire shape for each of the three legal cases: whole atom, strict substring, multi-atom region.",
            "Default to sourceAlias only. Do NOT include verbatimText when the intended selection is the whole atom.",
            "verbatimText is exceptional: use it only when the intended selection is a strict substring smaller than the atom. Never rewrite, normalize, repair, respell, re-space or paraphrase source text inside verbatimText - copy it exactly.",
            "If the intended source selection spans more than one evidence atom, do not place the combined text into one sourcePart. Emit one sourcePart per contributing sourceAlias, in source order; a fully-included atom uses sourceAlias alone, and only a genuinely partial boundary atom may carry verbatimText.",
            "A claim subject must come from subjectEvidence only. contextOnlyEvidence is never an eligible claim subject; it may serve as a relation's object or as context.",
            "Use the exact supplied sourceAlias; never invent one, and never invent text or coordinates.",
            "A relation with an unknown target remains OPEN with evidenceNeeds including GLOBAL_TARGET; do not invent an object.",
            "OPEN is preferable to fabricated certainty.",
            "The harness owns claim identity. Do not emit claimId.",
            "existingClaimId appears only for a claim you were explicitly given to refine.",
            "If nothing is claimable, return exactly {\"claims\":[]}.",
            "Return only the declared source-backed claim schema.",
        });
        var payload = new
        {
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            contract,
            claimShapes = V5ClaimShapesV2_1.Generate(contract),
            responseSchema = SemanticClaimContractV2_1.Schema(),
            sourceSelectionPolicy = V5SourceSelectionPolicy.Generate(),
            instructions,
            packet,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false });
        var prompt = json.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(prompt);
        return new V5ComposedSemanticRequest(
            V5SemanticRequestComposerV2_1.Version, prompt,
            Hashing.Sha256(instructions.ReplaceLineEndings("\n")), SemanticClaimContractV2_1.SchemaHash(),
            Hashing.Sha256(prompt), bytes.Length);
    }

    private static V5EvidencePacketV2_1 EmptyPacket() => new([], [], [], [], [], []);

    /// <summary>Exercises every one of the packet's 6 slots non-trivially, with real PDF-derived nodes.</summary>
    private static V5EvidencePacketV2_1 FullSyntheticPacket()
    {
        const string text1 = "CHAPTER I";
        const string text2 = "GENERAL PROVISIONS";
        const string text3 = "Article 1. Scope of regulation";
        var graph = EvidenceGraphBuilder.Build([
            new SourceObservation("E1", "S1", "L0000:S0", 0, EvidenceModality.TEXT, text1, new StructuralSpan(0, text1.Length)),
            new SourceObservation("E2", "S2", "L0001:S0", 1, EvidenceModality.TEXT, text2, new StructuralSpan(0, text2.Length)),
            new SourceObservation("E3", "S3", "L0002:S0", 2, EvidenceModality.TEXT, text3, new StructuralSpan(0, text3.Length)),
        ]);
        var nodes = graph.Nodes.ToArray();
        var claim = new BoundSemanticClaim(
            "v5claim21-sample",
            new BoundClaimEndpoint([new BoundSourcePart("L0000:S0", "S1", 0, 0, 9, "CHAPTER I", SemanticSourceLocality.SameSegment)]),
            "STRUCTURAL_REGION", "CHAPTER", null, ClaimResolutionState.OPEN, [EvidenceNeed.MORE_CONTEXT]);
        var candidate = new EvidenceCandidate("E3", 1, "exact", "alias-match");
        return new V5EvidencePacketV2_1(
            SubjectEvidence: [nodes[0]],
            ContextOnlyEvidence: [nodes[1]],
            OpenOrConflictedClaims: [claim],
            RetrievedEvidence: [candidate],
            LayoutEvidence: [nodes[2]],
            VisualEvidence: [nodes[2]]);
    }

    private static IEnumerable<V5EvidencePacketV2_1> RepresentativePackets()
    {
        yield return EmptyPacket();
        yield return FullSyntheticPacket();
    }

    [Fact]
    public void Canonical_request_contains_no_provider_or_model_fields()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, FullSyntheticPacket());
        var json = JsonSerializer.Serialize(canonical, CanonicalJson.Options);
        var forbidden = new[] { "model", "provider", "reasoning", "temperature", "response_format", "tool_choice" };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        CollectKeys(JsonDocument.Parse(json).RootElement, keys);
        foreach (var name in forbidden)
            Assert.DoesNotContain(name, keys);
    }

    private static void CollectKeys(JsonElement element, HashSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);
                    CollectKeys(property.Value, keys);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectKeys(item, keys);
                break;
        }
    }

    [Fact]
    public void Serialization_is_deterministic()
    {
        foreach (var packet in RepresentativePackets())
        {
            var first = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
            var second = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
            Assert.Equal(first.Prompt, second.Prompt);
            Assert.Equal(first.RequestHash, second.RequestHash);
        }
    }

    [Fact]
    public void Property_order_reproduces_historical_bytes()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, FullSyntheticPacket());
        var json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false });
        var names = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            ["composerVersion", "protocolVersion", "contract", "claimShapes", "responseSchema", "sourceSelectionPolicy", "instructions", "packet"],
            names);
    }

    [Fact]
    public void Compose_output_is_byte_identical_to_the_pre_extraction_reference()
    {
        foreach (var packet in RepresentativePackets())
        {
            var expected = OldReferenceCompose(Contract, packet);
            var actual = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
            Assert.Equal(expected.Prompt, actual.Prompt);
            Assert.Equal(expected.Utf8Bytes, actual.Utf8Bytes);
        }
    }

    [Fact]
    public void Semantic_and_schema_hashes_are_unchanged_by_extraction()
    {
        foreach (var packet in RepresentativePackets())
        {
            var expected = OldReferenceCompose(Contract, packet);
            var actual = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
            Assert.Equal(expected.RequestHash, actual.RequestHash);
            Assert.Equal(expected.PromptHash, actual.PromptHash);
            Assert.Equal(expected.SchemaHash, actual.SchemaHash);
            Assert.Equal(expected.ComposerVersion, actual.ComposerVersion);
        }
    }

    [Fact]
    public void Provider_body_hash_is_unchanged_by_extraction()
    {
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300);
        foreach (var packet in RepresentativePackets())
        {
            var expected = OldReferenceCompose(Contract, packet);
            var actual = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
            var expectedBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, expected.Prompt, 4096, envelope);
            var actualBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, actual.Prompt, 4096, envelope);
            Assert.Equal(expectedBody.Hash, actualBody.Hash);
            Assert.Equal(expectedBody.Bytes, actualBody.Bytes);
        }
    }

    [Fact]
    public void TaskContract_survives_the_canonical_request_round_trip_unchanged()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, FullSyntheticPacket());
        Assert.Same(Contract, canonical.Contract);
        var embedded = JsonDocument.Parse(JsonSerializer.Serialize(canonical, CanonicalJson.Options)).RootElement.GetProperty("contract");
        var standalone = JsonDocument.Parse(JsonSerializer.Serialize(Contract, CanonicalJson.Options)).RootElement;
        Assert.Equal(standalone.GetRawText(), embedded.GetRawText());
    }

    [Fact]
    public void EvidencePacket_survives_the_canonical_request_unchanged()
    {
        var packet = FullSyntheticPacket();
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, packet);
        Assert.Equal(packet, canonical.Packet);
        var embedded = JsonDocument.Parse(JsonSerializer.Serialize(canonical, CanonicalJson.Options)).RootElement.GetProperty("packet");
        var standalone = JsonDocument.Parse(JsonSerializer.Serialize(packet, CanonicalJson.Options)).RootElement;
        Assert.Equal(standalone.GetRawText(), embedded.GetRawText());
    }

    [Fact]
    public void SourceSelectionPolicy_is_unchanged()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, FullSyntheticPacket());
        var embedded = JsonDocument.Parse(JsonSerializer.Serialize(canonical, CanonicalJson.Options)).RootElement.GetProperty("sourceSelectionPolicy");
        var standalone = JsonDocument.Parse(JsonSerializer.Serialize(V5SourceSelectionPolicy.Generate(), CanonicalJson.Options)).RootElement;
        Assert.Equal(standalone.GetRawText(), embedded.GetRawText());
    }

    [Fact]
    public void ResponseSchema_is_unchanged()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, FullSyntheticPacket());
        var embedded = JsonDocument.Parse(JsonSerializer.Serialize(canonical, CanonicalJson.Options)).RootElement.GetProperty("responseSchema");
        var standalone = JsonDocument.Parse(JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema(), CanonicalJson.Options)).RootElement;
        Assert.Equal(standalone.GetRawText(), embedded.GetRawText());
    }

    // ---- Frozen replay: real PDFs, real packs, real frozen artifacts -------------------------

    /// <summary>
    /// Reconstructs the exact same packets <see cref="V5PdfPreflightBuilder.BuildV2_1"/> builds
    /// internally for one document's packs, from the same public pieces it uses
    /// (<see cref="PdfStructuredSourceAuthorityBuilder"/>, the resource-bounded packing policy,
    /// <see cref="EvidenceGraphBuilder"/>) - duplicated here, not imported, so this replay can
    /// compare the real composer's output against <see cref="OldReferenceCompose"/> without needing
    /// either implementation to expose its intermediate packet.
    /// </summary>
    private static IReadOnlyList<(string PackId, V5EvidencePacketV2_1 Packet)> RealPacketsFor(string documentId, string pdfPath)
    {
        var authority = PdfStructuredSourceAuthorityBuilder.Build(pdfPath);
        var graph = EvidenceGraphBuilder.Build(authority.Atoms.Select(atom => new SourceObservation(
            $"V5:{atom.SourceId}", atom.SourceId, atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text,
            new StructuralSpan(0, atom.Text.Length), new EvidenceGeometry(atom.Page),
            new Dictionary<string, string?> { ["sourceType"] = "PDF", ["documentId"] = documentId })));
        var byAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var packs = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        return packs.Select(pack =>
        {
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
            var owned = pack.Owned.Select(item => byAlias[item.SourceAlias]).ToArray();
            var visible = pack.Visible.Select(item => byAlias[item.SourceAlias]).ToArray();
            var contextOnly = visible.Where(node => !ownedAliases.Contains(node.SourceAlias)).ToArray();
            return (pack.PackId, new V5EvidencePacketV2_1(owned, contextOnly, [], [], [], []));
        }).ToArray();
    }

    [Fact]
    public void Frozen_replay_proves_zero_drift_across_the_31_pack_cohort_and_the_3_remediation_packs()
    {
        var docs = new[] { ("SRC-089", SourcePdfCorpus.Src089, 7), ("SRC-095", SourcePdfCorpus.Src095, 24) };
        var semanticByteDrift = 0;
        var semanticHashDrift = 0;
        var providerByteDrift = 0;
        var providerHashDrift = 0;
        var checked31 = 0;
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var perPack = new List<object>();

        foreach (var (documentId, pdfRelative, expectedPacks) in docs)
        {
            var packets = RealPacketsFor(documentId, TestRepository.Path(pdfRelative));
            Assert.Equal(expectedPacks, packets.Count);
            foreach (var (packId, packet) in packets)
            {
                checked31++;
                var expected = OldReferenceCompose(Contract, packet);
                var actual = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
                var byteDrift = expected.Prompt != actual.Prompt;
                var hashDrift = expected.RequestHash != actual.RequestHash;
                var expectedBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, expected.Prompt, expected.Utf8Bytes, envelope);
                var actualBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, actual.Prompt, actual.Utf8Bytes, envelope);
                var providerByteDrifted = expectedBody.Bytes != actualBody.Bytes;
                var providerHashDrifted = expectedBody.Hash != actualBody.Hash;
                if (byteDrift) semanticByteDrift++;
                if (hashDrift) semanticHashDrift++;
                if (providerByteDrifted) providerByteDrift++;
                if (providerHashDrifted) providerHashDrift++;
                perPack.Add(new { documentId, packId, byteDrift, hashDrift, providerByteDrifted, providerHashDrifted });
            }
        }
        Assert.Equal(31, checked31);
        Assert.Equal(0, semanticByteDrift);
        Assert.Equal(0, semanticHashDrift);
        Assert.Equal(0, providerByteDrift);
        Assert.Equal(0, providerHashDrift);

        // The 3-pack source-selection remediation canary inputs: rebuild via the real, unchanged
        // V5PdfPreflightBuilder.BuildV2_1 -> V5SemanticRequestComposerV2_1.Compose call chain and
        // require byte-for-byte equality with the frozen post-policy hashes RemediationCanary.cs
        // itself refuses to call a provider without matching - the same parity standard the
        // production tool already enforces at runtime.
        var selection = JsonNode.Parse(File.ReadAllText(TestRepository.Path(RemediationSelectionPath)))!;
        var builtByDoc = new Dictionary<string, IReadOnlyList<V5PackedSourceRequest>>(StringComparer.Ordinal);
        var remediationChecked = 0;
        foreach (var frozen in selection["packs"]!.AsArray())
        {
            var documentId = frozen!["documentId"]!.GetValue<string>();
            var packId = frozen["packId"]!.GetValue<string>();
            if (!builtByDoc.TryGetValue(documentId, out var requests))
            {
                var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                var built = V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(pdf), documentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                builtByDoc[documentId] = requests = built.Requests;
            }
            var pack = requests.Single(r => r.PackId == packId);
            Assert.Equal(frozen["newSemanticRequestHash"]!.GetValue<string>(), pack.Request.RequestHash);
            Assert.Equal(frozen["newProviderRequestHash"]!.GetValue<string>(), pack.ProviderRequestHash);
            Assert.Equal(frozen["newMaxCompletionTokens"]!.GetValue<int>(), pack.MaxCompletionTokens);
            remediationChecked++;
        }
        Assert.Equal(3, remediationChecked);

        var path = TestRepository.Path(ArtifactPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-canonical-semantic-request-extraction-equivalence-v1",
                subject = "P1: extract CanonicalSemanticRequestV2_1 from V5SemanticRequestComposerV2_1.Compose's anonymous payload",
                responsesChecked = new { cohortPacks = checked31, remediationPacks = remediationChecked },
                semanticByteDriftCount = semanticByteDrift,
                semanticHashDriftCount = semanticHashDrift,
                providerByteDriftCount = providerByteDrift,
                providerHashDriftCount = providerHashDrift,
                allDriftCountsZero = semanticByteDrift == 0 && semanticHashDrift == 0 && providerByteDrift == 0 && providerHashDrift == 0,
                note = "cohortPacks compares the new composer against a verbatim copy of the pre-extraction Compose body (OldReferenceCompose), using packets reconstructed from the real 31-pack cohort's own PDFs and packing policy - not against artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json, which was independently confirmed (by reverting V5SemanticRequestComposerV2_1.cs to its pre-extraction state and rerunning) to already mismatch current HEAD for all 31 rows, for reasons that predate and are unrelated to this refactor. remediationPacks compares against the real frozen artifact and is the genuine before/after-this-refactor check for those 3 packs.",
                perCohortPack = perPack,
                providerCalls = 0,
                goldRead = false,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
