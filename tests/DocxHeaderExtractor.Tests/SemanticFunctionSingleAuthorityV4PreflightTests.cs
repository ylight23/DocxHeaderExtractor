using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline-only preflight for the first semantic-function arm. It composes the actual PDF route's
/// requests into a capturing classifier; it neither constructs a provider nor opens Gold.
/// </summary>
public sealed class SemanticFunctionSingleAuthorityV4PreflightTests
{
    private const string Root = "eval/a99-closed-loop/semantic-function-single-authority-v4";
    private const string Model = "qwen/qwen3.7-flash";
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
    public void V4_is_explicit_only_and_normal_host_selection_remains_v2()
    {
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, CanonicalSemanticExperiment.Baseline.RequestVersion);
        Assert.NotEqual(V4.RequestVersion, CanonicalSemanticExperiment.Baseline.RequestVersion);
        Assert.Equal(SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
            SemanticRequestVersions.Require(V4.RequestVersion));

        var normalPrompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts, CanonicalSemanticExperiment.Baseline);
        var v4Prompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4);
        Assert.NotEqual(normalPrompt, v4Prompt);
        Assert.DoesNotContain("semanticFunction", normalPrompt, StringComparison.Ordinal);
        Assert.Contains("semanticFunction", v4Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void V4_schema_is_closed_and_has_no_legacy_or_descriptive_membership_axis()
    {
        var schema = JsonSerializer.Serialize(SemanticFunctionMembershipContractV1.Schema());
        foreach (var absent in new[] { "isHeading", "semanticRole", "occurrenceRole", "scope", "titleRelation", "\"heading\"" })
            Assert.DoesNotContain(absent, schema, StringComparison.Ordinal);
        Assert.Equal(9, SemanticFunctionMembershipContractV1.Functions.Length);
        Assert.True(SemanticFunctionMembershipContractV1.IsMember("DOCUMENT_IDENTITY"));
        Assert.True(SemanticFunctionMembershipContractV1.IsMember("REGION_STRUCTURE"));
        Assert.False(SemanticFunctionMembershipContractV1.IsMember("NAVIGATION"));
        Assert.False(SemanticFunctionMembershipContractV1.IsMember("unknown"));

        using var invalid = JsonDocument.Parse("""
            {"headings":[{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"NAVIGATION","isHeading":true}]}
            """);
        Assert.Contains(SemanticFunctionMembershipContractV1.ValidateJson(invalid.RootElement),
            issue => issue.Code == "FIELD_NOT_IN_CONTRACT" && issue.SourceAlias == "isHeading");
    }

    [Fact]
    public void V4_runtime_derives_membership_from_the_closed_function_not_from_a_second_field()
    {
        using var reply = JsonDocument.Parse("""
            {"headings":[
              {"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"NAVIGATION"},
              {"sourceParts":[{"sourceAlias":"L0002:S0"}],"semanticFunction":"REGION_STRUCTURE"}
            ]}
            """);
        Assert.Empty(SemanticFunctionMembershipContractV1.ValidateJson(reply.RootElement));
        var entries = reply.RootElement.GetProperty("headings").EnumerateArray().ToArray();
        var navigation = Assert.Single(SemanticFunctionMembershipContractV1.Decode(entries[0]).Proposals);
        var region = Assert.Single(SemanticFunctionMembershipContractV1.Decode(entries[1]).Proposals);
        Assert.False(navigation.IsHeading);
        Assert.True(region.IsHeading);
        Assert.Equal("NAVIGATION", navigation.SemanticRole);
        Assert.Equal("REGION_STRUCTURE", region.SemanticRole);
    }

    [Fact]
    public async Task Freeze_the_provider_free_v4_preflight()
    {
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_V4_RUN") is "1" or "true" or "TRUE");
        var captured = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                TestRepository.Path(pdf), capture, CancellationToken.None,
                experiment: V4,
                profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics,
                runPlacement: false);
            Assert.NotEmpty(capture.Requests);
            Assert.All(capture.Requests, request =>
            {
                Assert.Equal(CanonicalSemanticEngine.SystemPromptFor(
                    SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4), request.SystemPrompt);
                Assert.Contains("\"semanticFunction\"", request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("\"isHeading\"", request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("\"semanticRole\"", request.UserMessage, StringComparison.Ordinal);
            });
            captured.Add(new
            {
                documentId = id,
                requests = capture.Requests.Count,
                requestSha256 = capture.Requests.Select(request => Sha(request.UserMessage)).ToArray(),
                expectedOwnedItems = capture.Requests.Select(request => request.ExpectedItemCount).ToArray(),
            });
        }

        FreezeArtifact.AssertJson(Root, "preflight.v1.json", new
        {
            artifactKind = "a99_semantic_function_single_authority_v4_preflight",
            study = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            preflightOnly = true,
            modelProviderCalls = 0,
            requestVersion = V4.RequestVersion.ToString(),
            prompt = new
            {
                sha256 = Sha(CanonicalSemanticEngine.SystemPromptFor(
                    SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4)),
                lineEndings = "LF normalized by CanonicalSemanticEngine",
            },
            schema = new
            {
                protocol = SemanticFunctionMembershipContractV1.ProtocolVersion,
                sha256 = SemanticCoordinateContract.PdfSemanticFunctionMembershipV1.SchemaHash(),
                outputFields = new[] { "sourceParts", "semanticFunction" },
            },
            factsVersion = PdfSourceFactsVersion.V3_RobustGlyphStatistics.ToString(),
            packing = new
            {
                policy = SemanticEvidencePackingPolicies.FixedOwnedCount120.PolicyId,
                version = SemanticEvidencePackingPolicies.FixedOwnedCount120.PolicyVersion,
                ownedPerPack = SemanticEvidencePackingPolicies.OwnedPerPack,
            },
            model = new { identity = Model, provider = "OpenRouter", notConstructedInPreflight = true },
            binder = "SemanticSourcePartCanonicalizer + SemanticSourcePartBinder (existing production binder)",
            documents = captured,
            gates = new
            {
                v2ProductionDefaultUnchanged = SemanticRequestVersions.ProductionDefault == SemanticRequestVersion.V2_ATTENTION_FREE,
                v4ExplicitSelectionOnly = true,
                normalHostPathCannotSelectV4WithoutExperiment = true,
                legacyFieldsAbsentFromSchema = true,
                goldFilesOpenedBeforeRawPredictionsPersist = false,
                noCohortExpansion = true,
                noPostFilter = true,
                noHierarchyArm = true,
            },
            nextGate = "Provider execution requires explicit authorization; persist raw predictions before opening Gold.",
        });
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
