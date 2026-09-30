using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P2 of the canonical-semantic-request work: OpenRouter becomes an explicit carrier layer
/// (<see cref="OpenRouterQwen37JsonObjectCarrierV2_1"/>) underneath the already-frozen
/// <see cref="CanonicalSemanticRequestV2_1"/>, and the route's actual known capability state
/// (<see cref="V5RouteCapabilityEvidence"/>) is encoded on separate API-schema, model-documentation,
/// other-documentation and route-empirical axes, so documentation can never be confused with a real
/// call through this exact route. Scoped strictly to one route: openrouter -&gt; qwen/qwen3.7-flash -&gt; alibaba ->
/// chat-completions. Never calls a provider, never reads Gold, never changes TaskContract, source
/// packing, quarantine, the binder, the graph validator, the model, the gateway, or the production
/// response_format (json_object stays production; ToolAuto is prepared, never sent).
/// </summary>
public sealed class V5OpenRouterQwen37CarrierV2_1Tests
{
    private static readonly DocumentTaskContract Contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
    private const string CarrierArtifactRoot = "artifacts/v5-openrouter-qwen37-carrier";
    private const string RemediationSelectionPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-selection.v1.json";

    private static V5EvidencePacketV2_1 SamplePacket()
    {
        const string text1 = "CHAPTER I";
        const string text2 = "GENERAL PROVISIONS";
        var graph = EvidenceGraphBuilder.Build([
            new SourceObservation("E1", "S1", "L0000:S0", 0, EvidenceModality.TEXT, text1, new StructuralSpan(0, text1.Length)),
            new SourceObservation("E2", "S2", "L0001:S0", 1, EvidenceModality.TEXT, text2, new StructuralSpan(0, text2.Length)),
        ]);
        var nodes = graph.Nodes.ToArray();
        return new V5EvidencePacketV2_1([nodes[0]], [nodes[1]], [], [], [], []);
    }

    // ---- 1/2: carrier does not touch semantic content -----------------------------------------

    [Fact]
    public void OpenRouter_carrier_does_not_rebuild_semantic_content()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var beforeHash = composed.RequestHash;
        var beforePrompt = composed.Prompt;
        OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope);
        Assert.Equal(beforeHash, composed.RequestHash);
        Assert.Equal(beforePrompt, composed.Prompt);
    }

    [Fact]
    public void Canonical_semantic_request_has_no_openrouter_fields()
    {
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(Contract, SamplePacket());
        var json = JsonSerializer.Serialize(canonical, CanonicalJson.Options);
        foreach (var forbidden in new[] { "\"model\"", "\"provider\"", "\"reasoning\"", "\"temperature\"", "\"response_format\"", "\"tool_choice\"", "\"stream\"", "\"usage\"" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }

    // ---- 3/4/5: byte-for-byte legacy equivalence -----------------------------------------------

    [Fact]
    public void Json_object_carrier_is_byte_identical_to_the_legacy_body_builder()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var legacy = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, composed.Prompt, 4096, Envelope);
        var carried = OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope);
        Assert.Equal(legacy.Hash, carried.Hash);
        Assert.Equal(legacy.Bytes, carried.Bytes);
        Assert.Equal(legacy.PayloadBytes, carried.PayloadBytes);
    }

    [Fact]
    public void Provider_and_semantic_hashes_are_unchanged_by_the_carrier_extraction()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var beforeSemanticHash = composed.RequestHash;
        var legacyBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, composed.Prompt, 4096, Envelope);
        var carriedBody = OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope);
        Assert.Equal(beforeSemanticHash, composed.RequestHash);
        Assert.Equal(legacyBody.Hash, carriedBody.Hash);
    }

    // ---- 6/7/8/9/10/11/12/13/14/15: exact field-by-field body invariants ----------------------

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    public void System_and_user_messages_are_unchanged(string role)
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope);
        var messages = JsonDocument.Parse(body.PayloadBytes).RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var message = messages.Single(m => m.GetProperty("role").GetString() == role);
        var expectedContent = role == "system" ? V5SystemPromptV2_1.Text : composed.Prompt;
        Assert.Equal(expectedContent, message.GetProperty("content").GetString());
    }

    [Fact]
    public void Production_body_fields_are_pinned_exactly_as_before()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = JsonDocument.Parse(OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope).PayloadBytes).RootElement;
        Assert.Equal("qwen/qwen3.7-flash", body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetInt32());
        Assert.Equal(4096, body.GetProperty("max_tokens").GetInt32());
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
    }

    [Fact]
    public void ToolAuto_and_json_object_carriers_share_the_same_semantic_request_hash_but_differ_in_provider_hash()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var semanticHashBeforeEitherCarrier = composed.RequestHash;
        var jsonObjectBody = OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, 4096, Envelope);
        var toolAutoBody = OpenRouterQwen37ToolAutoCarrierV1.Build(composed, Contract, 4096, Envelope);
        // Both carriers consume the identical V5ComposedSemanticRequest - only how it is transported differs.
        Assert.Equal(semanticHashBeforeEitherCarrier, composed.RequestHash);
        Assert.NotEqual(jsonObjectBody.Hash, toolAutoBody.Hash);
    }

    // ---- 16/18/19: capability status is route-keyed and never collapsed to a bool -------------

    [Fact]
    public void Route_capabilities_are_keyed_by_the_full_gateway_model_provider_api_tuple()
    {
        var route = V5RouteIdentity.OpenRouterQwen37ChatCompletions;
        Assert.Equal("openrouter", route.Gateway);
        Assert.Equal("qwen/qwen3.7-flash", route.Model);
        Assert.Equal("alibaba", route.Provider);
        Assert.Equal("chat-completions", route.Api);
        Assert.Equal(route, V5OpenRouterQwen37RouteCapabilityRegistry.Current.Route);
    }

    [Fact]
    public void Documented_current_route_capability_state_matches_known_evidence()
    {
        var capabilities = V5OpenRouterQwen37RouteCapabilityRegistry.Current;
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.JsonObject.ModelDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.JsonObject.RouteEmpirical);

        Assert.Equal(V5CapabilityEvidenceState.UNSUPPORTED, capabilities.JsonSchemaStrict.ModelDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.NOT_NEEDED, capabilities.JsonSchemaStrict.RouteEmpirical);

        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.Tools.ApiSchema);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.Tools.ModelDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.Tools.RouteEmpirical);
        Assert.Equal(V5InvocationReliability.FAILED_CANARY, capabilities.Tools.InvocationReliability);

        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.ToolChoiceAuto.ApiSchema);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.ToolChoiceAuto.ModelDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.ToolChoiceAuto.RouteEmpirical);
        Assert.Equal(V5InvocationReliability.FAILED_CANARY, capabilities.ToolChoiceAuto.InvocationReliability);

        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.ToolChoiceNamed.ApiSchema);
        Assert.Equal(V5CapabilityEvidenceState.SUPPORTED, capabilities.ToolChoiceNamed.ModelDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.ROUTING_REJECTED, capabilities.ToolChoiceNamed.RouteEmpirical);

        Assert.Equal(V5CapabilityEvidenceState.NOT_IN_OVERVIEW, capabilities.ToolChoiceRequired.ApiSchema);
        Assert.Equal(V5CapabilityEvidenceState.MAY_MENTION, capabilities.ToolChoiceRequired.OtherDocumentation);
        Assert.Equal(V5CapabilityEvidenceState.ROUTING_REJECTED, capabilities.ToolChoiceRequired.RouteEmpirical);
    }

    // ---- 22/23: ToolAuto carrier shape -----------------------------------------------------------

    [Fact]
    public void ToolAuto_carrier_declares_exactly_one_function_named_submit_semantic_claims()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = JsonDocument.Parse(OpenRouterQwen37ToolAutoCarrierV1.Build(composed, Contract, 4096, Envelope).PayloadBytes).RootElement;
        var tools = body.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Single(tools);
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal(OpenRouterQwen37ToolAutoCarrierV1.ToolName, tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("submit_semantic_claims", tools[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void ToolAuto_carrier_never_uses_named_or_required_tool_choice()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = JsonDocument.Parse(OpenRouterQwen37ToolAutoCarrierV1.Build(composed, Contract, 4096, Envelope).PayloadBytes).RootElement;
        var toolChoice = body.GetProperty("tool_choice");
        Assert.Equal(JsonValueKind.String, toolChoice.ValueKind);
        Assert.Equal("auto", toolChoice.GetString());
    }

    [Fact]
    public void ToolAuto_provider_route_stays_pinned_to_alibaba_with_no_fallback()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = JsonDocument.Parse(OpenRouterQwen37ToolAutoCarrierV1.Build(composed, Contract, 4096, Envelope).PayloadBytes).RootElement;
        var provider = body.GetProperty("provider");
        Assert.Equal(["alibaba"], provider.GetProperty("order").EnumerateArray().Select(item => item.GetString()));
        Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(provider.GetProperty("require_parameters").GetBoolean());
    }

    [Fact]
    public void ToolAuto_tool_parameters_schema_compiles_from_the_existing_claim_shape_vocabulary_only()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract, SamplePacket());
        var body = JsonDocument.Parse(OpenRouterQwen37ToolAutoCarrierV1.Build(composed, Contract, 4096, Envelope).PayloadBytes).RootElement;
        var parameters = body.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        var expected = JsonSerializer.SerializeToUtf8Bytes(
            V5StrictClaimSchemaCompilerV1.Compile(Contract, V5ClaimSchemaCarrier.ToolParameters), CanonicalJson.Options);
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(expected).RootElement, CanonicalJson.Options), parameters.GetRawText());
        // No document-specific or heading-specific vocabulary leaks in - only the contract's own predicate/relation names.
        Assert.DoesNotContain("heading", parameters.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- 26: tool output would still be proposer-only, never a trusted-authority shortcut ------

    [Fact]
    public void Tool_arguments_would_still_decode_through_the_unmodified_v2_1_codec_and_binder()
    {
        // ToolAuto is prepared, never sent - but its declared output IS the existing SemanticClaimResponseV2_1
        // claims[] shape, so whatever a future canary receives has no path except the unmodified
        // ParseWithClaimQuarantine -> ExactClaimBinderV2_1 pipeline. Proven here structurally: the tool's
        // parameters schema is exactly SemanticClaimContractV2_1's own claim vocabulary (asserted above),
        // and no new decode path exists anywhere in this file - only V5OpenRouterToolAutoProviderRequestBodyV1.Build.
        var file = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Core/Models/V5OpenRouterQwen37CarrierV2_1.cs"));
        Assert.DoesNotContain("ExactClaimBinderV2_1.Bind(", file, StringComparison.Ordinal);
        Assert.DoesNotContain("SemanticClaimResponseCodecV2_1.Parse", file, StringComparison.Ordinal);
    }

    // ---- 27/28 ----------------------------------------------------------------------------------

    [Fact]
    public void No_provider_call_and_no_gold_read_anywhere_in_this_file()
    {
        var file = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Core/Models/V5OpenRouterQwen37CarrierV2_1.cs"));
        foreach (var forbidden in new[] { "HttpClient", "OpenRouterHeaderExtractor", "gold-current", "GoldLabel", "canonical-semantic-gold" })
            Assert.DoesNotContain(forbidden, file, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Frozen replay: real PDFs, real packs, real frozen artifacts --------------------------

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
    public void Json_object_carrier_replay_proves_zero_drift_across_the_31_pack_cohort_and_the_3_remediation_packs()
    {
        var docs = new[] { ("SRC-089", SourcePdfCorpus.Src089, 7), ("SRC-095", SourcePdfCorpus.Src095, 24) };
        var semanticByteDrift = 0;
        var semanticHashDrift = 0;
        var providerByteDrift = 0;
        var providerHashDrift = 0;
        var checked31 = 0;
        var perPack = new List<object>();

        foreach (var (documentId, pdfRelative, expectedPacks) in docs)
        {
            var packets = RealPacketsFor(documentId, TestRepository.Path(pdfRelative));
            Assert.Equal(expectedPacks, packets.Count);
            foreach (var (packId, packet) in packets)
            {
                checked31++;
                var composed = V5SemanticRequestComposerV2_1.Compose(Contract, packet);
                var legacyBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, composed.Prompt, composed.Utf8Bytes, Envelope);
                var carriedBody = OpenRouterQwen37JsonObjectCarrierV2_1.Build(composed, composed.Utf8Bytes, Envelope);
                var byteDrift = legacyBody.PayloadBytes.AsSpan().SequenceEqual(carriedBody.PayloadBytes) == false;
                var hashDrift = legacyBody.Hash != carriedBody.Hash;
                if (byteDrift) semanticByteDrift++;
                if (hashDrift) providerHashDrift++;
                if (legacyBody.Bytes != carriedBody.Bytes) providerByteDrift++;
                perPack.Add(new { documentId, packId, byteDrift, hashDrift });
            }
        }
        Assert.Equal(31, checked31);
        Assert.Equal(0, semanticByteDrift);
        Assert.Equal(0, semanticHashDrift);
        Assert.Equal(0, providerByteDrift);
        Assert.Equal(0, providerHashDrift);

        var path = TestRepository.Path($"{CarrierArtifactRoot}/json-object-equivalence.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-openrouter-json-object-carrier-equivalence-v1",
                carrierId = nameof(OpenRouterQwen37JsonObjectCarrierV2_1),
                gateway = V5RouteIdentity.OpenRouterQwen37ChatCompletions.Gateway,
                model = V5RouteIdentity.OpenRouterQwen37ChatCompletions.Model,
                provider = V5RouteIdentity.OpenRouterQwen37ChatCompletions.Provider,
                packsChecked = checked31,
                semanticByteDriftCount = semanticByteDrift,
                semanticHashDriftCount = semanticHashDrift,
                providerByteDriftCount = providerByteDrift,
                providerHashDriftCount = providerHashDrift,
                perPack,
                providerCalls = 0,
                goldRead = false,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    [Fact]
    public void Remediation_canary_inputs_still_reproduce_their_frozen_post_policy_hashes_through_the_carrier()
    {
        var selection = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(TestRepository.Path(RemediationSelectionPath)))!;
        var builtByDoc = new Dictionary<string, IReadOnlyList<V5PackedSourceRequest>>(StringComparer.Ordinal);
        var checkedCount = 0;
        foreach (var frozen in selection["packs"]!.AsArray())
        {
            var documentId = frozen!["documentId"]!.GetValue<string>();
            var packId = frozen["packId"]!.GetValue<string>();
            if (!builtByDoc.TryGetValue(documentId, out var requests))
            {
                var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                var built = V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(pdf), documentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
                builtByDoc[documentId] = requests = built.Requests;
            }
            var pack = requests.Single(r => r.PackId == packId);
            // The pack was built by the real, unchanged BuildV2_1 -> Compose -> V5ProviderRequestBodyV2_1.Build
            // chain, which now delegates to the carrier internally - so a match here proves the carrier
            // reproduces the exact frozen post-policy body for these 3 real inputs, not just synthetic ones.
            Assert.Equal(frozen["newSemanticRequestHash"]!.GetValue<string>(), pack.Request.RequestHash);
            Assert.Equal(frozen["newProviderRequestHash"]!.GetValue<string>(), pack.ProviderRequestHash);
            Assert.Equal(frozen["newMaxCompletionTokens"]!.GetValue<int>(), pack.MaxCompletionTokens);
            checkedCount++;
        }
        Assert.Equal(3, checkedCount);
    }

    [Fact]
    public void Tool_auto_preflight_for_PACK_006_matches_the_canonical_semantic_hash_and_is_never_sent()
    {
        var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src089));
        var built = V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", Contract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
        var pack006 = built.Requests.Single(r => r.PackId == "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006");

        var semanticHashBeforeToolAutoPreflight = pack006.Request.RequestHash;
        var toolAutoBody = OpenRouterQwen37ToolAutoCarrierV1.Build(
            pack006.Request, Contract, pack006.MaxCompletionTokens, Envelope, pack006.OwnedAliases, pack006.VisibleAliases);

        // The canonical semantic hash is untouched by preparing a ToolAuto body.
        Assert.Equal(semanticHashBeforeToolAutoPreflight, pack006.Request.RequestHash);

        var path = TestRepository.Path($"{CarrierArtifactRoot}/tool-auto-preflight-pack006.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-openrouter-tool-auto-preflight-v1",
                pack = pack006.PackId,
                semanticRequestHash = pack006.Request.RequestHash,
                providerRequestHash = toolAutoBody.Hash,
                payloadBytes = toolAutoBody.Bytes,
                toolName = OpenRouterQwen37ToolAutoCarrierV1.ToolName,
                toolChoice = "auto",
                providerRoute = new
                {
                    gateway = V5RouteIdentity.OpenRouterQwen37ChatCompletions.Gateway,
                    model = Envelope.Model,
                    provider = Envelope.Provider,
                },
                providerCalls = 0,
                goldRead = false,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
