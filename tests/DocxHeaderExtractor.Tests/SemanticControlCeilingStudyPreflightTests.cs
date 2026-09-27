using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free preflight for the reasoning-only counterfactual. It never constructs a provider.</summary>
public sealed class SemanticControlCeilingStudyPreflightTests
{
    private const string Root = "eval/a99-closed-loop/semantic-control-ceiling-study";
    private const string Model = "qwen/qwen3.7-flash";
    private const string PromptSha256 = "e996bef4346efff9b0544f34777192d5b59c7e7f75a749dbf91cbe9f0d5df93b";
    private const string SchemaSha256 = "7d8ae805c0373dad3795d819cd5b0229b9421c28600d5d6b001e818f839225db";
    private const int ProductionMaxOutputTokens = 32768;
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];

    private static readonly CanonicalSemanticExperiment V4 = CanonicalSemanticExperiment.Baseline with
    {
        RequestVersion = SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
    };

    [Fact]
    public async Task Freeze_reasoning_only_preflight_and_stop_before_provider()
    {
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_V4R1_RUN") is "1" or "true" or "TRUE");
        var documents = new List<object>();
        var allSameSemanticRequestHashes = true;
        var allCalls = 0;
        var maxOutputCeiling = 0;

        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            using var treatmentCapture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), capture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), treatmentCapture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            var baselineSemanticHashes = capture.Requests.Select(request => Sha(request.SystemPrompt + "\n" + request.UserMessage)).ToArray();
            var treatmentSemanticHashes = treatmentCapture.Requests.Select(request => Sha(request.SystemPrompt + "\n" + request.UserMessage)).ToArray();
            Assert.Equal(baselineSemanticHashes, treatmentSemanticHashes);

            var baseline = new RemoteInferenceOptions { Model = Model, OpenRouterReasoningEffort = "none" };
            var r1 = new RemoteInferenceOptions { Model = Model, OpenRouterReasoningEffort = "medium" };
            var rows = capture.Requests.Select(request =>
            {
                var requestHash = Sha(request.UserMessage);
                var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                    request.UserMessage, request.ExpectedItemCount, ProductionMaxOutputTokens);
                var baselineFingerprint = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(baseline, maxTokens, SchemaSha256);
                var r1Fingerprint = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(r1, maxTokens, SchemaSha256);
                allCalls++;
                return new
                {
                    requestSha256 = requestHash,
                    expectedItemCount = request.ExpectedItemCount,
                    maxTokens,
                    baselineFingerprint = baselineFingerprint.Sha256,
                    r1Fingerprint = r1Fingerprint.Sha256,
                    semanticRequestUnchanged = true,
                    reasoningOnlyDelta = baselineFingerprint.Sha256 != r1Fingerprint.Sha256,
                };
            }).ToArray();
            allSameSemanticRequestHashes &= rows.All(row => row.semanticRequestUnchanged) && baselineSemanticHashes.SequenceEqual(treatmentSemanticHashes);
            maxOutputCeiling += rows.Sum(row => row.maxTokens);
            documents.Add(new { documentId = id, calls = rows.Length, requests = rows });
        }

        Assert.Equal(25, allCalls);
        Assert.True(allSameSemanticRequestHashes);
        FreezeArtifact.AssertJson(Root, "r1-reasoning-preflight.v1.json", new
        {
            artifactKind = "a99_semantic_control_ceiling_reasoning_only_preflight",
            study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
            requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
            treatment = new
            {
                onlyChangedField = "OpenRouter reasoning.effort",
                baselineValue = "none",
                treatmentValue = "medium",
                semanticContract = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            },
            pins = new
            {
                model = Model,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                factsVersion = "V3_RobustGlyphStatistics",
                packing = "FIXED_OWNED_COUNT_120",
                binder = "SemanticSourcePartCanonicalizer + SemanticSourcePartBinder",
            },
            providerExecution = new
            {
                baselineReasoningFingerprint = "derived per request; effort=none",
                treatmentReasoningFingerprint = "derived per request; effort=medium",
                requestSemanticHashesIdentical = allSameSemanticRequestHashes,
            },
            documents,
            calls = new { total = allCalls, SRC089 = 5, SRC095 = 20 },
            budget = new
            {
                inputTokensReferenceFromFrozenV4 = 712286,
                outputTokensObservedFromFrozenV4 = 46095,
                outputTokensPerRequestCeilingSum = maxOutputCeiling,
                hardCaps = new { calls = 30, input = 2000000, output = 250000 },
                treatmentOutputEstimate = "UNKNOWN_UNTIL_PROVIDER",
            },
            gates = new
            {
                goldOpened = false,
                hierarchyRun = false,
                postFilterApplied = false,
                cohortExpanded = false,
                providerCalls = 0,
                providerAuthorizationRequired = true,
                frozenV4ArtifactsUntouched = true,
            },
            nextGate = "Explicit authorization required before any V4R1 provider transport or raw prediction persistence.",
        });
    }

    [Fact]
    public void R1_requires_explicit_authorization_and_cannot_be_selected_by_normal_host_path()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")));
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", artifact.RootElement.GetProperty("requestVersion").GetString());
        Assert.True(artifact.RootElement.GetProperty("gates").GetProperty("providerAuthorizationRequired").GetBoolean());
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
