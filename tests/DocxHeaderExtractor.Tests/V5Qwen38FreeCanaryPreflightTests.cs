using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free preflight for the qwen3.8-27b:free model-capability canary
/// (<c>capability-preflight.v2.json</c>: capability label STRUCTURED_OUTPUT_SUPPORTED, strict-schema
/// acceptance UNVERIFIED_UNTIL_REQUEST_ACCEPTED). Freezes the exact three provider request bodies -
/// same 3 packs as the source-selection remediation canary (SRC-089 PACK_006/PACK_001, SRC-095
/// PACK_022), same TaskContract, packing and source-selection policy, same claims[] ontology - but
/// with <see cref="V5StrictClaimSchemaCompilerV1"/>'s strict schema in place of the production
/// json_object schema. Never calls a provider; every body is rebuilt twice in-process and required to
/// be byte-identical before it is trusted. Real execution is a separate, explicitly-authorized gate.
/// </summary>
public sealed class V5Qwen38FreeCanaryPreflightTests
{
    private const string ArtifactRoot = "artifacts/qwen38-27b-free-3pack-canary";
    private const string Model = "qwen/qwen3.8-27b:free";
    // The only endpoint OpenRouter routes this slug to (capability-preflight.v2.json).
    private const string ProviderRoutingSlug = "modelrun";

    [Fact]
    public void Freeze_the_3_pack_strict_schema_preflight_without_a_provider_call()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        // ResponseFormat="json_schema" here is descriptive only - V5ProviderEnvelope is not used to
        // build the body for this canary; V5StrictSchemaProviderRequestBodyV1 builds it directly.
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_schema", 300) { UsageInclude = true };

        var builtByDoc = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .ToDictionary(item => item.Item1, item => V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(item.Item2), item.Item1, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope));

        var selection = new[]
        {
            ("SRC-089", "same-atom-normalization-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006"),
            ("SRC-089", "true-multipart-heavy", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
            ("SRC-095", "mixed", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_022"),
        };

        var schemaName = SemanticClaimContractV2_1.SchemaVersion.Replace('-', '_').Replace('.', '_');
        var bodiesDir = TestRepository.Path($"{ArtifactRoot}/bodies");
        Directory.CreateDirectory(bodiesDir);

        var frozen = selection.Select(item =>
        {
            var (documentId, role, packId) = item;
            var built = builtByDoc[documentId];
            var pack = built.Requests.Single(request => request.PackId == packId);

            var compiledSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, pack.OwnedAliases, pack.VisibleAliases);
            var schemaJsonA = JsonSerializer.Serialize(compiledSchema, new JsonSerializerOptions { WriteIndented = false });
            var schemaJsonB = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(contract, pack.OwnedAliases, pack.VisibleAliases),
                new JsonSerializerOptions { WriteIndented = false });
            Assert.Equal(schemaJsonA, schemaJsonB);
            var schemaHash = Sha256(schemaJsonA);

            var bodyA = V5StrictSchemaProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, compiledSchema, schemaName, "none");
            var bodyB = V5StrictSchemaProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, compiledSchema, schemaName, "none");
            Assert.Equal(bodyA.Hash, bodyB.Hash);
            Assert.True(bodyA.PayloadBytes.AsSpan().SequenceEqual(bodyB.PayloadBytes));

            var bodyFileName = $"{documentId}-{packId[(packId.LastIndexOf(':') + 1)..]}.json";
            File.WriteAllBytes(Path.Combine(bodiesDir, bodyFileName), bodyA.PayloadBytes);

            return new
            {
                role,
                documentId,
                packId,
                sourceUniverseHash = built.Preflight.SourceUniverseSha,
                semanticRequestHash = pack.Request.RequestHash,
                semanticRequestBytes = pack.Request.Utf8Bytes,
                compiledSchemaHash = schemaHash,
                compiledSchemaBytes = Encoding.UTF8.GetByteCount(schemaJsonA),
                providerRequestHash = bodyA.Hash,
                providerRequestBytes = bodyA.Bytes,
                bodyFile = "bodies/" + bodyFileName,
                model = Model,
                actualProviderRoute = ProviderRoutingSlug,
                responseFormatType = "json_schema",
                strict = true,
                reasoningEffort = "none",
                maxCompletionTokens = pack.MaxCompletionTokens,
                ownedAliasCount = pack.OwnedAliases.Count,
                visibleAliasCount = pack.VisibleAliases.Count,
            };
        }).ToArray();

        Assert.Equal(3, frozen.Length);
        Assert.Equal(frozen.Select(p => p.providerRequestHash).Distinct(StringComparer.Ordinal).Count(), frozen.Length);

        var report = new
        {
            schemaVersion = "v5-qwen38-27b-free-canary-preflight-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION",
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            strictSchemaCompilerVersion = V5StrictClaimSchemaCompilerV1.Version,
            model = Model,
            capabilityStatus = "STRUCTURED_OUTPUT_SUPPORTED (capability label, from capability-preflight.v2.json); " +
                "strict-schema acceptance UNVERIFIED_UNTIL_REQUEST_ACCEPTED",
            schemaCapabilities = new
            {
                jsonSchema = "requested",
                strict = "requested",
                oneOf = "used - one branch per predicate/relation, pinned by a single-value enum on predicate",
                additionalPropertiesFalse = "used at every object level (root, claim branch, subject/object endpoint, source part)",
                @enum = "used for predicate pinning, state, evidenceNeeds, and sourceAlias when an owned/visible set is given",
                dynamicAliasEnum = "used - each pack's own owned aliases (subject) and visible aliases (object) injected",
                refsOrDefs = "NOT used - schema is fully inlined, no $ref/$defs",
                constKeyword = "NOT used - enum-of-one used instead for broader strict-mode compatibility",
                minLengthMinItemsMinimum = "NOT used - deferred entirely to ExactClaimBinderV2_1/SemanticSourcePartBinder, " +
                    "the real exact-coordinate authority; the schema layer is only an earlier rejection layer",
                acceptanceVerified = false,
            },
            sourceSelectionPolicy = "unchanged - V5SourceSelectionPolicy.Generate() and the v2.1 composer instructions are reused as-is via pack.Request.Prompt",
            claimsOntology = "unchanged - claims[] wrapper, no unaryClaims[]/relationClaims[] split",
            packs = frozen,
            baselineComparison = new[]
            {
                "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json (Qwen3.7 Flash, json_object) - PACK_001, PACK_022",
                "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-raw-observation.v1.json (PACK_006 raw structural read)",
            },
            baselineRerun = false,
            providerCalls = 0,
            goldRead = false,
            semanticRetries = 0,
            providerExecutionAuthorized = false,
        };

        WriteJson($"{ArtifactRoot}/preflight.json", report);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteJson(string relativePath, object value)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
