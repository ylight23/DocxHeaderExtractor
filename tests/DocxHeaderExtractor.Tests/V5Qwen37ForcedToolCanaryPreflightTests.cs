using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free preflight for the qwen3.7-flash/alibaba forced-function-call output carrier
/// prototype (see <see cref="V5ForcedToolClaimCarrierV1Tests"/> for the carrier's own unit tests).
/// Same 3 packs, same TaskContract, packing and source-selection policy as the closed
/// source-selection remediation canary and the qwen3.8-27b:free experiment - only the output carrier
/// changes: <c>tools</c>/forced <c>tool_choice</c> instead of <c>response_format</c>. Freezes two
/// schema variants (structural-only, and structural+pack alias enums) so a later canary can choose
/// deliberately rather than by accident. Never calls a provider.
/// </summary>
public sealed class V5Qwen37ForcedToolCanaryPreflightTests
{
    private const string ArtifactRoot = "artifacts/v5-qwen37-forced-tool-canary";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderRoutingSlug = "alibaba";
    private const string ToolName = "submit_semantic_claims";
    private const string ToolDescription = "Submit the complete source-backed semantic claim response for the current task.";

    // The same 3 packs as the source-selection remediation canary and the qwen3.8-27b:free experiment,
    // for the same reason given there plus one: PACK_006 is Qwen3.7's own relation-has-value draw.
    private static readonly (string DocumentId, string Role, string PackId)[] Selection =
    [
        ("SRC-089", "same-atom-normalization-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006"),
        ("SRC-089", "true-multipart-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("SRC-095", "mixed", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022"),
    ];

    // The already-frozen json_object baseline for these exact 3 packs (source-selection-remediation-
    // canary-selection.v1.json, commit 35f27fd) - the comparison anchor for "did only the carrier change".
    private static readonly Dictionary<string, (string SemanticRequestHash, int SemanticRequestBytes)> JsonObjectBaseline = new(StringComparer.Ordinal)
    {
        ["RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006"] = ("1aee77de2662e24b2eee5ba880ecb683e21ceb827a325df7590a997d859fa204", 84894),
        ["RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"] = ("94e0ea2acd80472ec75c6835af71d145d22c338950656f95096c45a44924494e", 79027),
        ["RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022"] = ("5c70d3bb648fa118fb7dd7da66bd88c4fb9607466c57d1d8baad6f5ec634a7e6", 79716),
    };

    [Fact]
    public void Freeze_the_3_pack_forced_tool_preflight_without_a_provider_call()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_object", 300) { UsageInclude = true };

        var builtByDoc = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .ToDictionary(item => item.Item1, item => V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(item.Item2), item.Item1, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope));

        var bodiesDir = TestRepository.Path($"{ArtifactRoot}/bodies");
        Directory.CreateDirectory(bodiesDir);

        var structuralOnlySchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters);
        var structuralOnlySchemaJson = JsonSerializer.Serialize(structuralOnlySchema, new JsonSerializerOptions { WriteIndented = false });
        WriteRaw($"{ArtifactRoot}/schema-structural-only.json", structuralOnlySchemaJson);

        var frozen = Selection.Select(item =>
        {
            var built = builtByDoc[item.DocumentId];
            var pack = built.Requests.Single(request => request.PackId == item.PackId);

            // Source-selection policy / composer / semantic content is untouched: the same pack.Request
            // this test rebuilds must match the already-frozen json_object baseline's semantic bytes
            // exactly, before the output carrier ever enters the picture.
            var baseline = JsonObjectBaseline[item.PackId];
            Assert.Equal(baseline.SemanticRequestHash, pack.Request.RequestHash);
            Assert.Equal(baseline.SemanticRequestBytes, pack.Request.Utf8Bytes);

            var aliasEnumSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters, pack.OwnedAliases, pack.VisibleAliases);
            var aliasEnumSchemaJsonA = JsonSerializer.Serialize(aliasEnumSchema, new JsonSerializerOptions { WriteIndented = false });
            var aliasEnumSchemaJsonB = JsonSerializer.Serialize(
                V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters, pack.OwnedAliases, pack.VisibleAliases),
                new JsonSerializerOptions { WriteIndented = false });
            Assert.Equal(aliasEnumSchemaJsonA, aliasEnumSchemaJsonB);

            // Variant A - structural only (no alias enums): binder retains sole ownership authority.
            var bodyA1 = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralOnlySchema, "none");
            var bodyA2 = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralOnlySchema, "none");
            Assert.Equal(bodyA1.Hash, bodyA2.Hash);
            Assert.True(bodyA1.PayloadBytes.AsSpan().SequenceEqual(bodyA2.PayloadBytes));

            // Variant B - structural + this pack's owned/visible alias enums.
            var bodyB1 = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, aliasEnumSchema, "none");
            var bodyB2 = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, aliasEnumSchema, "none");
            Assert.Equal(bodyB1.Hash, bodyB2.Hash);
            Assert.True(bodyB1.PayloadBytes.AsSpan().SequenceEqual(bodyB2.PayloadBytes));

            var suffix = item.PackId[(item.PackId.LastIndexOf(':') + 1)..];
            File.WriteAllBytes(Path.Combine(bodiesDir, $"{item.DocumentId}-{suffix}-variantA-structural.json"), bodyA1.PayloadBytes);
            File.WriteAllBytes(Path.Combine(bodiesDir, $"{item.DocumentId}-{suffix}-variantB-alias-enum.json"), bodyB1.PayloadBytes);

            return new
            {
                role = item.Role,
                documentId = item.DocumentId,
                packId = item.PackId,
                sourceUniverseHash = built.Preflight.SourceUniverseSha,
                semanticRequestHash = pack.Request.RequestHash,
                semanticRequestBytes = pack.Request.Utf8Bytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
                ownedAliasCount = pack.OwnedAliases.Count,
                visibleAliasCount = pack.VisibleAliases.Count,
                variantA_structuralOnly = new
                {
                    toolSchemaBytes = Encoding.UTF8.GetByteCount(structuralOnlySchemaJson),
                    providerBodyHash = bodyA1.Hash,
                    providerBodyBytes = bodyA1.Bytes,
                },
                variantB_aliasEnum = new
                {
                    toolSchemaBytes = Encoding.UTF8.GetByteCount(aliasEnumSchemaJsonA),
                    providerBodyHash = bodyB1.Hash,
                    providerBodyBytes = bodyB1.Bytes,
                    aliasEnumOverheadBytes = Encoding.UTF8.GetByteCount(aliasEnumSchemaJsonA) - Encoding.UTF8.GetByteCount(structuralOnlySchemaJson),
                },
                jsonObjectBaseline = new
                {
                    providerRequestBytes = item.PackId switch
                    {
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006" => 114894,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001" => 107551,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022" => 110368,
                        _ => 0,
                    },
                },
                forcedToolBodyOverheadVsJsonObject = new
                {
                    variantA = bodyA1.Bytes - (item.PackId switch
                    {
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006" => 114894,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001" => 107551,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022" => 110368,
                        _ => 0,
                    }),
                    variantB = bodyB1.Bytes - (item.PackId switch
                    {
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006" => 114894,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001" => 107551,
                        "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022" => 110368,
                        _ => 0,
                    }),
                },
            };
        }).ToArray();

        Assert.Equal(3, frozen.Length);
        Assert.Equal(frozen.Select(p => p.variantA_structuralOnly.providerBodyHash).Distinct(StringComparer.Ordinal).Count(), frozen.Length);
        Assert.Equal(frozen.Select(p => p.variantB_aliasEnum.providerBodyHash).Distinct(StringComparer.Ordinal).Count(), frozen.Length);

        // Alias enums are per-pack (owned/visible sets differ), so there is no single canonical
        // alias-enum schema the way there is a structural-only one - this writes the first pack's, as a
        // representative sample, named for exactly which pack it came from.
        var samplePack = Selection[0];
        var sampleBuilt = builtByDoc[samplePack.DocumentId].Requests.Single(request => request.PackId == samplePack.PackId);
        var sampleAliasEnumSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters, sampleBuilt.OwnedAliases, sampleBuilt.VisibleAliases);
        // Pure JSON (the sample's provenance - which pack it came from - is documented in preflight.json,
        // not inline here, since alias enums are per-pack and this file is a representative sample only).
        WriteRaw($"{ArtifactRoot}/schema-alias-enum.json",
            JsonSerializer.Serialize(sampleAliasEnumSchema, new JsonSerializerOptions { WriteIndented = false }));

        var report = new
        {
            schemaVersion = "v5-qwen37-forced-tool-canary-preflight-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION - provider-free prototype only",
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            strictSchemaCompilerVersion = V5StrictClaimSchemaCompilerV1.Version,
            model = Model,
            provider = ProviderRoutingSlug,
            tool = new { name = ToolName, description = ToolDescription },
            capabilities = new
            {
                jsonSchemaStrictSupported = false, // unchanged from ProviderStructuredOutputRegistry.QwenFlashAlibaba
                jsonObjectSupported = true,
                toolsSupported = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.ToolsSupported,
                specificToolChoiceSupported = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.SpecificToolChoiceSupported,
                toolArgumentSchemaCarrierSupported = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.ToolArgumentSchemaSupported,
                functionStrictFlagSupported = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.FunctionStrictFlagSupported.ToString(),
                toolArgumentStrictEnforcement = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.ToolArgumentStrictEnforcement.ToString(),
                evidenceSource = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba.EvidenceSource,
            },
            claimsWording = "relation-has-value is structurally excluded by the advertised tool parameter schema, but enforcement by Qwen3.7/Alibaba remains empirically unverified until a real call is made. Schema-present != decoder-enforced.",
            schemaAliasEnumSampleProvenance = $"{samplePack.DocumentId} {samplePack.PackId} - alias enums are per-pack (owned/visible differ), so schema-alias-enum.json is a representative sample, not a canonical file",
            sourceSelectionPolicy = "unchanged - reused pack.Request.Prompt verbatim, verified byte-identical to the json_object baseline's frozen semantic request hash for all 3 packs",
            claimsOntology = "unchanged - claims[] wrapper, no unaryClaims[]/relationClaims[] split; the tool is an output carrier, not a second ontology",
            noSecondProviderTurn = true,
            noLocalFunctionExecution = true,
            packs = frozen,
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        };

        WriteJson($"{ArtifactRoot}/preflight.json", report);
        WriteJson($"{ArtifactRoot}/capability-preflight.v1.json", new
        {
            schemaVersion = "v5-model-capability-preflight-v1",
            model = Model,
            provider = ProviderRoutingSlug,
            capabilities = V5ToolCallingCapabilityRegistry.QwenFlashAlibaba,
            existingNativeStrictSchemaCapability = "JsonSchemaStrictSupported=false (ProviderStructuredOutputRegistry.QwenFlashAlibaba, unchanged)",
            providerCalls = 0,
            goldRead = false,
        });
    }

    private static void WriteJson(string relativePath, object value)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void WriteRaw(string relativePath, string content)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + Environment.NewLine, new UTF8Encoding(false));
    }
}
