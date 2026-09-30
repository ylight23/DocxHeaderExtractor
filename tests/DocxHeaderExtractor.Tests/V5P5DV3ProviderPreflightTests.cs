using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline v3 wire/runtime qualification; never reads Gold or sends provider requests.</summary>
public sealed class V5P5DV3ProviderPreflightTests
{
    private const string ArtifactRoot = "artifacts/v5-p5d-v3-preflight";
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    [Fact]
    public async Task Freeze_exact_v3_31_pack_provider_preflight_and_exercise_real_runtime_offline()
    {
        var docs = new[]
        {
            (Id: "SRC-089", Pdf: SourcePdfCorpus.Src089, Expected: 7),
            (Id: "SRC-095", Pdf: SourcePdfCorpus.Src095, Expected: 24),
        };
        var rows = new List<object>();
        var allOwnedAliases = new List<string>();
        var maxDecisions = 0;
        var maxRequestBytes = 0;
        var maxProviderRequestBytes = 0;
        var maxCompletionTokens = 0;
        var maxSingleClaimBytes = 0;
        var maxSingleClaimTokenEstimate = 0;
        var packs = 0;
        var maxVisible = 0;

        foreach (var doc in docs)
        {
            var built = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(doc.Pdf), doc.Id, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(doc.Pdf));
            var pdfSha256 = V5CohortPreflightBuilder.Sha256(File.ReadAllBytes(TestRepository.Path(doc.Pdf)));
            Assert.Equal(doc.Expected, built.Count);
            var packOwned = built.SelectMany(item => item.OwnedAliases).ToArray();
            Assert.Equal(atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal), packOwned.ToHashSet(StringComparer.Ordinal));
            Assert.Equal(packOwned.Length, packOwned.Distinct(StringComparer.Ordinal).Count());
            allOwnedAliases.AddRange(packOwned);

            foreach (var pack in built)
            {
                packs++;
                var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
                var composed = V5SemanticDecisionComposerV3.Compose(Contract, pack.Packet);
                var body = OpenRouterQwen37JsonObjectCarrierV3.Build(composed, pack.MaxCompletionTokens, Envelope);
                Assert.Equal(pack.Request.RequestHash, composed.RequestHash);
                Assert.Equal(pack.ProviderRequestHash, body.Hash);
                Assert.Equal(pack.ProviderRequestBytes, body.Bytes);
                Assert.Equal(pack.OwnedAliases.Count, pack.Packet.SubjectEvidence.Count);
                Assert.DoesNotContain(pack.Packet.ContextOnlyEvidence, node => owned.Contains(node.SourceAlias));

                var promptRoot = JsonDocument.Parse(pack.Request.Prompt).RootElement;
                var schema = promptRoot.GetProperty("responseSchema");
                var decisions = schema.GetProperty("properties").GetProperty("decisions");
                Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("minItems").GetInt32());
                Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("maxItems").GetInt32());
                var wire = JsonDocument.Parse(pack.ProviderBody).RootElement;
                Assert.Equal(Envelope.Model, wire.GetProperty("model").GetString());
                Assert.Equal(0, wire.GetProperty("temperature").GetInt32());
                Assert.Equal(pack.MaxCompletionTokens, wire.GetProperty("max_tokens").GetInt32());
                Assert.Equal("none", wire.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Equal("json_object", wire.GetProperty("response_format").GetProperty("type").GetString());
                Assert.True(wire.GetProperty("stream").GetBoolean());
                Assert.Equal("alibaba", wire.GetProperty("provider").GetProperty("order")[0].GetString());
                Assert.False(wire.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
                var bodyFileName = $"{doc.Id}_{pack.PackId.Replace(':', '_')}.json";
                var bodyRelativePath = $"{ArtifactRoot}/wire-bodies/{bodyFileName}";
                var bodyPath = TestRepository.Path(bodyRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(bodyPath)!);
                File.WriteAllBytes(bodyPath, pack.ProviderBody);
                Assert.Equal(pack.ProviderRequestHash, V5CohortPreflightBuilder.Sha256(File.ReadAllBytes(bodyPath)));

                var oneClaim = new V5SemanticDecisionResponseV3(pack.OwnedAliases.Select(_ =>
                    new V5SemanticSubjectDecisionV3([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "x", EvidenceNeeds: [])])).ToArray());
                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(oneClaim, CanonicalJson.Options).Length;
                var responseTokenEstimate = (int)Math.Ceiling(responseBytes / 4d);
                maxSingleClaimBytes = Math.Max(maxSingleClaimBytes, responseBytes);
                maxSingleClaimTokenEstimate = Math.Max(maxSingleClaimTokenEstimate, responseTokenEstimate);
                maxDecisions = Math.Max(maxDecisions, pack.OwnedAliases.Count);
                maxRequestBytes = Math.Max(maxRequestBytes, pack.Request.Utf8Bytes);
                maxProviderRequestBytes = Math.Max(maxProviderRequestBytes, pack.ProviderRequestBytes);
                maxCompletionTokens = Math.Max(maxCompletionTokens, pack.MaxCompletionTokens);
                maxVisible = Math.Max(maxVisible, pack.VisibleAliases.Count);
                rows.Add(new
                {
                    documentId = doc.Id,
                    sourcePdfSha256 = pdfSha256,
                    packId = pack.PackId,
                    ownedCount = pack.OwnedAliases.Count,
                    contextOnlyCount = pack.Packet.ContextOnlyEvidence.Count,
                    ownedAliases = pack.OwnedAliases,
                    visibleAliases = pack.VisibleAliases,
                    semanticRequestHash = pack.Request.RequestHash,
                    schemaHash = pack.Request.SchemaHash,
                    semanticRequestBytes = pack.Request.Utf8Bytes,
                    providerRequestHash = pack.ProviderRequestHash,
                    providerRequestBytes = pack.ProviderRequestBytes,
                    providerBodyFile = $"wire-bodies/{bodyFileName}",
                    maxCompletionTokens = pack.MaxCompletionTokens,
                    expectedDecisionCount = pack.OwnedAliases.Count,
                    emptyLedgerResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                        new V5SemanticDecisionResponseV3(pack.OwnedAliases.Select(_ => new V5SemanticSubjectDecisionV3([])).ToArray()), CanonicalJson.Options).Length,
                    oneSimpleClaimPerOwnedResponseBytes = responseBytes,
                    oneSimpleClaimTokenEstimate = responseTokenEstimate,
                    providerBodySha256 = pack.ProviderRequestHash,
                });
            }
        }

        Assert.Equal(31, packs);
        Assert.Equal(2884, allOwnedAliases.Count);
        Assert.Equal(96, maxDecisions);

        // The same v3 parser is exercised on a chunked content stream, then its typed response
        // traverses DocumentAgentRuntime and the production binder using a deterministic reasoner.
        var sse = new OpenRouterHeaderExtractor.SseReassembly();
        var streamedJson = "{\"decisions\":[{\"claims\":[{\"predicate\":\"STRUCTURAL_REGION\",\"value\":\"Header\",\"evidenceNeeds\":[]}]},{\"claims\":[]}]}";
        var split = streamedJson.Length / 2;
        FeedContent(sse, streamedJson[..split]);
        FeedContent(sse, streamedJson[split..]);
        Feed(sse, "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}");
        Feed(sse, "data: [DONE]", final: true);
        Assert.True(sse.TransportComplete);
        var parsed = V5SemanticDecisionContractV3.Parse(JsonDocument.Parse(sse.Content).RootElement, Contract, 2, 1);
        Assert.Equal(2, parsed.Decisions.Count);

        Assert.Throws<InvalidOperationException>(() => Parse("{\"decisions\":[{\"claims\":[]}]}", 2, 1));
        Assert.Throws<InvalidOperationException>(() => Parse("{\"decisions\":[null,{\"claims\":[]}]}", 2, 1));
        Assert.Throws<InvalidOperationException>(() => Parse("{\"decisions\":[{\"claims\":null},{\"claims\":[]}]}", 2, 1));

        var testAtoms = new[]
        {
            new SemanticSourceAtom("OWN-0", "source-0", 0, 1, 1, 0, "Header"),
            new SemanticSourceAtom("OWN-1", "source-1", 1, 1, 2, 0, "Next"),
            new SemanticSourceAtom("HALO-0", "source-2", 2, 1, 3, 0, "Context"),
        };
        var graph = EvidenceGraphBuilder.Build(testAtoms.Select(atom => new SourceObservation($"E:{atom.Alias}", atom.SourceId,
            atom.Alias, atom.Ordinal, EvidenceModality.TEXT, atom.Text, new StructuralSpan(0, atom.Text.Length))));
        var packet = new V5SemanticDecisionRequestPacketV3(graph.Nodes.Take(2).ToArray(), [graph.Nodes[2]], [], [], [], []);
        var multiAtom = new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Header Next", AdditionalSubjectParts: [new(1)], EvidenceNeeds: [])]), new([]),
        ]);
        var scope = ClaimBindingScope.Create(["OWN-0", "OWN-1"], ["OWN-0", "OWN-1", "HALO-0"]);
        var multiBinding = V5SemanticDecisionContractV3.Bind("p5d-multi", multiAtom, Contract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, testAtoms, scope);
        Assert.Equal("source-0:0-6|source-1:0-4", Assert.Single(multiBinding.Bound).Claim.Subject.Identity);
        var relationContract = Contract with { Relations = [new SemanticRelationDefinition("RELATES_TO", "A test relation.")] };
        var relation = new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("RELATES_TO", TargetParts: [new("CONTEXT_ONLY", 0)], EvidenceNeeds: [])]), new([]),
        ]);
        var relationBinding = V5SemanticDecisionContractV3.Bind("p5d-halo-target", relation, relationContract,
            packet.SubjectEvidence, packet.ContextOnlyEvidence, testAtoms, scope);
        Assert.Equal("source-2:0-7", Assert.Single(relationBinding.Bound).Claim.Object!.Identity);

        var validReasoner = new SyntheticV3Reasoner(new V5SemanticDecisionResponseV3([
            new([new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "Header", EvidenceNeeds: [])]), new([]),
        ]));
        var runtimeContract = Contract with { Projections = [] };
        var runtimeResult = await new DocumentAgentRuntime(validReasoner, new InMemoryEvidenceRetriever())
            .RunAsync(runtimeContract, graph, testAtoms, new HashSet<string>(["OWN-0", "OWN-1"], StringComparer.Ordinal),
                new HashSet<string>(["OWN-0", "OWN-1", "HALO-0"], StringComparer.Ordinal));
        Assert.Equal("source-0:0-6", Assert.Single(runtimeResult.State.Claims).Subject.Identity);
        Assert.Equal(1, validReasoner.Calls);

        var invalidReasoner = new SyntheticV3Reasoner(new V5SemanticDecisionResponseV3([new([])]));
        var invalidRuntime = await new DocumentAgentRuntime(invalidReasoner, new InMemoryEvidenceRetriever())
            .RunAsync(runtimeContract, graph, testAtoms, new HashSet<string>(["OWN-0", "OWN-1"], StringComparer.Ordinal),
                new HashSet<string>(["OWN-0", "OWN-1", "HALO-0"], StringComparer.Ordinal));
        Assert.Contains(invalidRuntime.State.Conflicts, issue => issue.Code == "CLAIM_BINDING" && issue.Message.Contains("decision-cardinality-invalid", StringComparison.Ordinal));

        Write("execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5d-v3-provider-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocol = V5Protocol.ClaimSchemaVersionV3,
            route = new { gateway = "openrouter", api = "chat-completions", model = Envelope.Model, providerPin = Envelope.Provider,
                temperature = 0, reasoning = Envelope.Reasoning, streaming = Envelope.Streaming, responseFormat = Envelope.ResponseFormat },
            requestCount = packs,
            providerRequestBodyFormat = "OpenRouter chat-completions, stream=true, json_object; exact UTF-8 body bytes stored under wire-bodies/ and hashed per pack below",
            modelDocumentation = new
            {
                url = "https://openrouter.ai/qwen/qwen3.7-flash",
                jsonObject = "SUPPORTED_WITHOUT_JSON_SCHEMA_ENFORCEMENT",
                toolsAndToolChoice = "DOCUMENTED_SUPPORTED; NOT_EXERCISED_IN_THIS_PREFLIGHT",
                maxCompletionTokens = 65536,
                providerPin = "Alibaba Cloud Int.",
                checkedOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
            },
            rows,
            historicalBaseline = new { artifactProtocol = V5Protocol.ClaimSchemaVersionV2_1, value = "82/519/57", currentV3Score = false },
            providerCalls = 0,
            goldRead = false,
        });
        Write("summary.v1.json", new
        {
            schemaVersion = "v5-p5d-v3-provider-preflight-summary-v1",
            v3Requests = packs,
            ownedTotal = allOwnedAliases.Count,
            ownedExactOnce = true,
            positionalMappingStable = true,
            haloSubjectRepresentable = false,
            multiAtomRepresentable = true,
            relationHaloTarget = true,
            streamReassemblyV3 = "PASS",
            malformedNullAndCardinalityResponses = "FAIL_CLOSED",
            maxDecisionsPerRequest = maxDecisions,
            maxVisiblePerRequest = maxVisible,
            maxRequestBytes,
            maxProviderRequestBytes,
            maxCompletionBudget = maxCompletionTokens,
            qwenDocumentedMaxCompletionTokens = 65536,
            maxSimpleOneClaimResponseBytes = maxSingleClaimBytes,
            maxSimpleOneClaimTokenEstimate = maxSingleClaimTokenEstimate,
            completionBudgetFormula = $"max(1024, 512 + ownedCount*256, requestBytes/6), capped at 32768; max observed={maxCompletionTokens}",
            truncationRisk = "OPEN_UNBOUNDED_SCHEMA: claims and optional selection-context strings have no finite schema maximum; simple one-claim-per-owned fixture fits budget, but offline preflight cannot guarantee provider completion",
            qwenOpenRouterCarrier = "JSON_OBJECT_AND_STREAM_BODY_MATCH_QUALIFIED_ROUTE; V3_MODEL_COMPLIANCE_NOT_TESTED",
            runtimeSynthetic = new { validResponseBound = true, invalidCardinalityRefused = true },
            providerCalls = 0,
            goldRead = false,
            historicalBaseline = new { value = "82/519/57", protocol = "V2.1_ONLY_NOT_CURRENT_V3_SCORE" },
        });
    }

    private static V5SemanticDecisionResponseV3 Parse(string json, int owned, int context) =>
        V5SemanticDecisionContractV3.Parse(JsonDocument.Parse(json).RootElement, Contract, owned, context);

    private sealed class SyntheticV3Reasoner(V5SemanticDecisionResponseV3 response) : ISemanticReasoner
    {
        public int Calls { get; private set; }
        public string Identity => "offline-p5d-synthetic-v3";
        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            Calls++;
            var schema = JsonDocument.Parse(context.Request.Prompt).RootElement.GetProperty("responseSchema");
            var cardinality = schema.GetProperty("properties").GetProperty("decisions").GetProperty("maxItems").GetInt32();
            Assert.Equal(context.RequestPacket.SubjectEvidence.Count, cardinality);
            return ValueTask.FromResult(new SemanticReasoningResult(response, new SemanticReasoningUsage()));
        }
    }

    private static void FeedContent(OpenRouterHeaderExtractor.SseReassembly stream, string content)
    {
        var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });
        Feed(stream, $"data: {chunk}");
    }

    private static void Feed(OpenRouterHeaderExtractor.SseReassembly stream, string line, bool final = false)
    {
        var pending = new StringBuilder(line + "\n\n");
        stream.Feed(pending, final);
    }

    private static void Write(string name, object content)
    {
        var path = TestRepository.Path($"{ArtifactRoot}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
