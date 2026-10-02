using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6JContextCapacityDiagnosticTests
{
    private const string P6iArtifactPath = "artifacts/v5-p6i-compact-locator-directory/audit.v1.json";
    private const string ArtifactRoot = "artifacts/v5-p6j-context-capacity";

    private static readonly DiagnosticRow[] SurrogateRows =
    [
        new("SRC-089", 1, 3603, 40132, 192, 43594),
        new("SRC-089", 2, 3905, 43816, 192, 47580),
        new("SRC-089", 3, 3828, 41272, 192, 44959),
        new("SRC-089", 4, 3941, 45646, 192, 49446),
        new("SRC-089", 5, 3965, 43432, 192, 47256),
        new("SRC-089", 6, 4083, 41770, 192, 45712),
        new("SRC-089", 7, 435, 1036, 192, 1956),
        new("SRC-095", 1, 3193, 19150, 192, 22203),
        new("SRC-095", 2, 3955, 32518, 192, 36360),
        new("SRC-095", 3, 4183, 36838, 192, 41241),
        new("SRC-095", 4, 4415, 44614, 192, 48930),
        new("SRC-095", 5, 4357, 43396, 192, 47659),
        new("SRC-095", 6, 4586, 41620, 192, 46267),
        new("SRC-095", 7, 4381, 43996, 192, 48256),
        new("SRC-095", 8, 4274, 43576, 192, 47699),
        new("SRC-095", 9, 4185, 42256, 192, 46293),
        new("SRC-095", 10, 3558, 26110, 192, 29529),
        new("SRC-095", 11, 3684, 26242, 192, 29777),
        new("SRC-095", 12, 4207, 39370, 192, 43455),
        new("SRC-095", 13, 4112, 35392, 192, 39364),
        new("SRC-095", 14, 4137, 37642, 192, 41665),
        new("SRC-095", 15, 4307, 42154, 192, 46312),
        new("SRC-095", 16, 4100, 37786, 192, 41791),
        new("SRC-095", 17, 3549, 22954, 192, 26396),
        new("SRC-095", 18, 3175, 15190, 192, 18242),
        new("SRC-095", 19, 3746, 19048, 192, 22825),
        new("SRC-095", 20, 4804, 35668, 192, 40715),
        new("SRC-095", 21, 4427, 38962, 192, 43224),
        new("SRC-095", 22, 4102, 32668, 192, 36627),
        new("SRC-095", 23, 4720, 30622, 192, 35206),
        new("SRC-095", 24, 4670, 34478, 192, 39026),
    ];

    [Fact]
    public void Freeze_non_authoritative_qwen3_tokenizer_diagnostic_without_promoting_context_gate()
    {
        var p6iPath = TestRepository.Path(P6iArtifactPath);
        var p6iJson = File.ReadAllText(p6iPath);
        Assert.Equal("18293fd26e01e945c931cbcfc15fe4de4b560492025024c5b72c436efa6391cb",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p6iPath))).ToLowerInvariant());
        using var p6i = JsonDocument.Parse(p6iJson);
        var p6iRows = p6i.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, p6iRows.Length);
        Assert.Equal(SurrogateRows.Length, p6iRows.Length);
        var rows = p6iRows.Zip(SurrogateRows, (request, diagnostic) =>
        {
            Assert.Equal(diagnostic.DocumentId, request.GetProperty("DocumentId").GetString());
            Assert.Equal(diagnostic.ParentOrdinal, request.GetProperty("ParentOrdinal").GetInt32());
            var effectiveCeiling = request.GetProperty("EffectiveInputTokenCeiling").GetInt32();
            return new
            {
                documentId = diagnostic.DocumentId,
                parentOrdinal = diagnostic.ParentOrdinal,
                p6iUserMessageSha256 = request.GetProperty("CompactUserMessageSha256").GetString(),
                systemTokens = 315,
                sourceContextTokens = diagnostic.SourceContextTokens,
                locatorDirectoryTokens = diagnostic.LocatorDirectoryTokens,
                instructionsSchemaTokens = diagnostic.InstructionsSchemaTokens,
                qwen3SurrogateChatTemplateInputTokens = diagnostic.TotalTokens,
                maxCompletionTokens = request.GetProperty("MaxCompletionTokens").GetInt32(),
                effectiveInputCeiling = effectiveCeiling,
                diagnosticOnlyEstimatedMargin = effectiveCeiling - diagnostic.TotalTokens,
            };
        }).ToArray();

        FreezeArtifact.AssertJson(ArtifactRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p6j-tokenizer-context-capacity-v1",
            capturedUtcDate = "2026-10-02",
            providerCalls = 0,
            goldRead = false,
            goldMutation = "NONE",
            sharedRuntime = "UNCHANGED",
            sourceRequestArtifact = new
            {
                path = P6iArtifactPath,
                sha256 = "18293fd26e01e945c931cbcfc15fe4de4b560492025024c5b72c436efa6391cb",
                messageParity = "All 31 measured user-message SHA256 values match the frozen P6I compact request rows.",
            },
            limitAuthority = new
            {
                model = "qwen3.7-flash",
                maxInputTokens = 991808,
                contextWindowTokens = 1000000,
                configuredMaxCompletionTokens = 25088,
                effectiveInputCeilingForMaxReservation = 974912,
                exactModelTokenizer = "NOT_FOUND_IN_OFFICIAL_MODEL_DOCUMENTATION_OR_REPOSITORY",
                exactProviderChatTemplate = "NOT_FOUND_OR_VERIFIED_FOR_OPENROUTER_ALIBABA_ROUTE",
                verdict = "CONTEXT_CAPACITY_NOT_PROVEN",
                authoritySource = "https://www.alibabacloud.com/help/en/model-studio/qwen3-7-flash",
            },
            surrogate = new
            {
                tokenizerAuthority = "NON_AUTHORITATIVE_SURROGATE",
                reason = "Qwen3-4B and Qwen3-8B public tokenizer assets match each other byte-for-byte, but no authoritative evidence links either tokenizer/chat template to proprietary qwen3.7-flash on Alibaba Model Studio/OpenRouter.",
                models = new[] { "Qwen/Qwen3-4B", "Qwen/Qwen3-8B" },
                tokenizerJsonSha256 = "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4",
                tokenizerConfigSha256 = "d5d09f07b48c3086c508b30d1c9114bd1189145b74e982a265350c923acd8101",
                chatTemplateSha256 = "a55ee1b1660128b7098723e0abcd92caa0788061051c62d51cbe87d9cf1974d8",
                tokenizerHashesIdenticalAcross4BAnd8B = true,
                tokenizerRuntime = "HuggingFace tokenizers 0.23.2; no model inference",
                chatTemplateApproximation = "Qwen3-4B tokenizer_config system+user template, add_generation_prompt=true, enable_thinking=false; not established as Alibaba Qwen3.7 Flash route template",
                sourceTokenizerJson = "https://huggingface.co/Qwen/Qwen3-4B/blob/main/tokenizer.json",
                sourceTokenizerConfig = "https://huggingface.co/Qwen/Qwen3-4B/blob/main/tokenizer_config.json",
                sourceQwenTemplateGuidance = "https://github.com/QwenLM/Qwen3/blob/main/docs/source/getting_started/quickstart.md",
            },
            componentMethod = new
            {
                sourceContext = "tokenize JSON of owned text/facts and context-only text/facts with boundaryHandles excluded",
                locatorDirectory = "tokenize JSON containing each owned atom handle and its ordered boundaryHandles only",
                instructionsSchema = "tokenize protocol metadata, task descriptor and response contract JSON",
                system = "tokenize the exact P6I system message content",
                total = "tokenize the exact P6I system/user contents wrapped in the Qwen3-4B surrogate chat template plus non-thinking assistant generation prefix",
                componentCountsAreIndependentAndNonAdditive = true,
                inputTokensFromProvider = (int?)null,
            },
            summary = new
            {
                packCount = 31,
                systemTokens = new { min = 315, p50 = 315, p95 = 315, max = 315 },
                sourceContextTokens = new { min = 435, p50 = 4102, p95 = 4720, max = 4804 },
                locatorDirectoryTokens = new { min = 1036, p50 = 37786, p95 = 44614, max = 45646 },
                instructionsSchemaTokens = new { min = 192, p50 = 192, p95 = 192, max = 192 },
                totalQwen3SurrogateChatTemplateTokens = new { min = 1956, p50 = 41791, p95 = 48930, max = 49446 },
                worstDiagnosticEstimatedMarginToEffectiveCeiling = 925466,
                quantile = "nearest-rank; p50 rank 16/31; p95 rank 30/31",
            },
            rows,
            gates = new
            {
                INPUT_EFFICIENCY = "P6I_MEASURED_COMPACT; request body max 167173 UTF8 bytes",
                INPUT_CONTEXT_CAPACITY = "NOT_PROVEN; surrogate diagnostic only",
                OUTPUT_RESPONSE = "P6G_SPARSE_CANDIDATE_WITH_FAIL_CLOSED_ADAPTIVE_OWNED_ATOM_SPLIT",
                SINGLETON_OVERFLOW = "NOT_EVALUABLE_NO_PARSE_OR_BIND",
                PROVIDER_EXECUTION = "BLOCKED",
            },
        });
    }

    private sealed record DiagnosticRow(string DocumentId, int ParentOrdinal, int SourceContextTokens,
        int LocatorDirectoryTokens, int InstructionsSchemaTokens, int TotalTokens);
}
