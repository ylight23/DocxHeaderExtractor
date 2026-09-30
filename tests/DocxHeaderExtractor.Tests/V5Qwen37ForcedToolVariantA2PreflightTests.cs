using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free preflight for Variant A2: identical to Variant A in every respect except
/// <c>tool_choice</c> - the plain string <c>"required"</c> (OpenRouter's generic "call at least one
/// tool" mode) instead of the named-function-forcing object shape Qwen3.7/Alibaba's routing rejected
/// with 404 "Filter by Tool Compatibility" (commit 71cc694). Freezes all 3 packs; only PACK_006 is
/// authorized for an actual call next, one logical call at a time, per the decision tree: if routing
/// rejects "required" too, the structural-tool-carrier avenue closes for Qwen3.7/Alibaba entirely and
/// json_object + the fail-closed validator remains the trade-off; only if PACK_006 completes does
/// PACK_001/PACK_022 get considered. Never calls a provider.
/// </summary>
public sealed class V5Qwen37ForcedToolVariantA2PreflightTests
{
    private const string ArtifactRoot = "artifacts/v5-qwen37-forced-tool-canary";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderRoutingSlug = "alibaba";
    private const string ToolName = "submit_semantic_claims";
    private const string ToolDescription = "Submit the complete source-backed semantic claim response for the current task.";

    private static readonly (string DocumentId, string Role, string PackId)[] Selection =
    [
        ("SRC-089", "same-atom-normalization-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006"),
        ("SRC-089", "true-multipart-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("SRC-095", "mixed", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022"),
    ];

    [Fact]
    public void Freeze_variant_A2_required_tool_choice_and_confirm_only_tool_choice_differs_from_variant_A()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_object", 300) { UsageInclude = true };

        var builtByDoc = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .ToDictionary(item => item.Item1, item => V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(item.Item2), item.Item1, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope));

        var structuralSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters);
        var bodiesDir = TestRepository.Path($"{ArtifactRoot}/bodies");
        Directory.CreateDirectory(bodiesDir);

        var frozen = Selection.Select(item =>
        {
            var built = builtByDoc[item.DocumentId];
            var pack = built.Requests.Single(request => request.PackId == item.PackId);

            var bodyA = V5ForcedToolProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralSchema, "none");
            var bodyA2First = V5ForcedToolProviderRequestBodyV1.BuildWithRequiredToolChoice(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralSchema, "none");
            var bodyA2Second = V5ForcedToolProviderRequestBodyV1.BuildWithRequiredToolChoice(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, ToolName, ToolDescription, structuralSchema, "none");
            Assert.Equal(bodyA2First.Hash, bodyA2Second.Hash);
            Assert.True(bodyA2First.PayloadBytes.AsSpan().SequenceEqual(bodyA2Second.PayloadBytes));
            Assert.NotEqual(bodyA.Hash, bodyA2First.Hash);

            // Structural proof, not eyeballing: remove tool_choice from both and require everything
            // else - model, tools[], provider routing, reasoning, messages (so the semantic content) -
            // to be byte-for-byte identical.
            var nodeA = JsonNode.Parse(bodyA.PayloadBytes)!.AsObject();
            var nodeA2 = JsonNode.Parse(bodyA2First.PayloadBytes)!.AsObject();
            var toolChoiceA = nodeA["tool_choice"]!.ToJsonString();
            var toolChoiceA2 = nodeA2["tool_choice"]!.ToJsonString();
            nodeA.Remove("tool_choice");
            nodeA2.Remove("tool_choice");
            Assert.Equal(nodeA.ToJsonString(), nodeA2.ToJsonString());
            Assert.NotEqual(toolChoiceA, toolChoiceA2);
            Assert.Equal("\"required\"", toolChoiceA2);

            var suffix = item.PackId[(item.PackId.LastIndexOf(':') + 1)..];
            File.WriteAllBytes(Path.Combine(bodiesDir, $"{item.DocumentId}-{suffix}-variantA2-required.json"), bodyA2First.PayloadBytes);

            return new
            {
                role = item.Role,
                documentId = item.DocumentId,
                packId = item.PackId,
                semanticRequestHash = pack.Request.RequestHash,
                semanticRequestBytes = pack.Request.Utf8Bytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
                variantA_providerBodyHash = bodyA.Hash,
                variantA2_providerBodyHash = bodyA2First.Hash,
                variantA2_providerBodyBytes = bodyA2First.Bytes,
                onlyDifferenceIsToolChoice = true,
            };
        }).ToArray();

        Assert.Equal(3, frozen.Length);
        Assert.Equal(frozen.Select(p => p.variantA2_providerBodyHash).Distinct(StringComparer.Ordinal).Count(), frozen.Length);

        var report = new
        {
            schemaVersion = "v5-qwen37-forced-tool-canary-variantA2-preflight-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION - provider-free prototype only",
            model = Model,
            provider = ProviderRoutingSlug,
            tool = new { name = ToolName, description = ToolDescription },
            toolChoice = "required",
            variantDescription = "Identical to Variant A (structural-only schema) except tool_choice is the plain string \"required\" instead of the named-function-forcing object shape",
            supersededFinding = "Variant A's named tool_choice was empirically UNSUPPORTED on Qwen3.7/Alibaba (71cc694): HTTP 404, routing_funnel failedRoutingStep=\"Filter by Tool Compatibility\". Whether \"required\" is supported is independently unverified.",
            executionPlan = new
            {
                step1 = "PACK_006 alone, exactly 1 logical call - answers: routing acceptance, real tool_calls returned, argument schema compliance, relation-has-value gone, alias-only preserved",
                step2 = "only if PACK_006 completes: PACK_001 (source-selection/multipart/coverage) and PACK_022 (ownership/refusal behavior)",
                ifPack006Returns404 = "close the structural-tool-carrier avenue for Qwen3.7/Alibaba entirely; retain json_object + the fail-closed TaskContract validator + exact binder as the trade-off for this model",
                autoModeNotTested = "deliberately - auto lets the model skip the tool entirely and return prose, which stops answering the question this experiment measures",
            },
            packs = frozen,
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        };

        WriteJson($"{ArtifactRoot}/preflight-variantA2.json", report);
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
