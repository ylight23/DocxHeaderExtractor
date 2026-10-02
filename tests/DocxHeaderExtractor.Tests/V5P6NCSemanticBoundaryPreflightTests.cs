using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes a four-pack P6N-C prompt-only variant; it never calls a provider or reads Gold.</summary>
public sealed class V5P6NCSemanticBoundaryPreflightTests
{
    private const string Root = "artifacts/v5-p6nc-boundary-prompt-preflight";
    private const string Full31Root = "artifacts/v5-p6nb-full31-reasoning-lane";
    private const string P6NBRoot = "artifacts/v5-p6nb-free-semantic-bound-locator";
    private const string BoundaryGuidance = "Semantic boundary: A heading names or opens a structural region; an ordinary proposition remains body content even if subordinate material follows. Navigation entries that point elsewhere are not headings; a label that opens a subgroup within the current document region may be.";
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Role, string Document, string Pdf, int Ordinal)[] Selected =
    [
        ("MAX_REQUEST_BODY", "SRC-089", SourcePdfCorpus.Src089, 4),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, 17),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, 20),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, 11),
    ];

    [Fact]
    public void Freeze_four_P6NC_requests_with_only_two_generic_semantic_boundary_sentences()
    {
        var full31 = Read($"{Full31Root}/execution-manifest.v1.json").RootElement;
        var p6nb = Read($"{P6NBRoot}/execution-manifest.v1.json").RootElement;
        Assert.Equal(0, full31.GetProperty("providerCalls").GetInt32());
        Assert.False(full31.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, p6nb.GetProperty("providerCalls").GetInt32());
        Assert.False(p6nb.GetProperty("goldRead").GetBoolean());
        var fullRows = full31.GetProperty("rows").EnumerateArray().ToArray();
        var p6nbRows = p6nb.GetProperty("rows").EnumerateArray().ToArray();
        var output = new List<object>();

        foreach (var selected in Selected)
        {
            var pdf = TestRepository.Path(selected.Pdf);
            var pack = V5PdfPreflightBuilder.BuildV3(pdf, selected.Document, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope)
                .Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{selected.Ordinal:000}");
            var atoms = V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => atoms[alias]).ToArray());
            var baseline = fullRows.Single(row => S(row, "documentId") == selected.Document && I(row, "parentOrdinal") == selected.Ordinal);
            var baselineCanary = p6nbRows.Single(row => S(row, "role") == selected.Role);
            Assert.Equal(S(baseline, "packId"), pack.PackId);
            Assert.Equal(S(baseline, "registryFingerprint"), registry.Fingerprint);
            Assert.Equal(S(baselineCanary, "registryFingerprint"), registry.Fingerprint);

            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var baselineRequest = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            var baselineBody = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(baselineRequest, pack.MaxCompletionTokens);
            Assert.Equal(S(baseline, "providerRequestHash"), baselineBody.Hash);
            Assert.Equal(S(baselineCanary, "providerRequestHash"), baselineBody.Hash);

            var correctedPrompt = $"{baselineRequest.SystemPrompt}\n\n{BoundaryGuidance}";
            var correctedRequest = baselineRequest with
            {
                ProtocolVersion = "v5-free-reasoning-heading-membership-source-parts-locator-boundary-cues-1",
                SystemPrompt = correctedPrompt,
                SystemPromptUtf8Bytes = Encoding.UTF8.GetByteCount(correctedPrompt),
            };
            var correctedBody = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(correctedRequest, pack.MaxCompletionTokens);
            Assert.Equal(baselineRequest.UserMessage, correctedRequest.UserMessage);
            Assert.Equal(baselineRequest.UserMessageSha256, correctedRequest.UserMessageSha256);
            Assert.NotEqual(baselineBody.Hash, correctedBody.Hash);
            AssertOnlySystemPromptChanged(baselineBody.PayloadBytes, correctedBody.PayloadBytes);
            output.Add(new
            {
                role = selected.Role, documentId = selected.Document, parentOrdinal = selected.Ordinal, packId = pack.PackId,
                ownedAtoms = pack.OwnedAliases.Count, sourceEvidenceHash = S(baseline, "sourceEvidenceHash"),
                registryFingerprint = registry.Fingerprint, unchangedSemanticRequestHash = correctedRequest.UserMessageSha256,
                baselineProviderRequestHash = baselineBody.Hash, providerRequestHash = correctedBody.Hash,
                providerRequestBytes = correctedBody.PayloadBytes.Length, baselineProviderRequestBytes = baselineBody.PayloadBytes.Length,
                maxCompletionTokens = pack.MaxCompletionTokens, systemPromptUtf8Bytes = correctedRequest.SystemPromptUtf8Bytes,
                promptDeltaUtf8Bytes = Encoding.UTF8.GetByteCount(BoundaryGuidance) + 2,
            });
        }

        FreezeArtifact.AssertJson(Root, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p6nc-boundary-prompt-preflight-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            preparedFrom = new { p6nbFull31Manifest = $"{Full31Root}/execution-manifest.v1.json", p6nbFourPackManifest = $"{P6NBRoot}/execution-manifest.v1.json" },
            providerCalls = 0, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN",
            model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0,
            reasoning = new { enabled = true, effort = "OMITTED" }, ontologyPrompt = false,
            locatorSchema = V5FreeHeadingCandidateProtocolV1.BoundLocatorVersion,
            sourcePacking = "same four frozen P05 packs", sourceContext = "unchanged", parser = "unchanged", binder = "unchanged",
            changes = new[] { BoundaryGuidance },
            controls = new { outputShape = "unchanged headings[].sourceParts[]", jsonObjectCarrier = "unchanged",
                maxCompletionTokens = "unchanged per pack", repair = false, fallback = false, retry = 0, postFilter = false,
                full31 = false, goldDuringRun = false, productionPromotion = false, sharedRuntime = "UNCHANGED" },
            hypothesis = "Generic boundary cues reduce body-proposition and navigation-entry false positives while retaining free semantic judgement and exact source locator behavior.",
            causalLimit = "Compared with P6N-B, only the appended two-sentence semantic boundary guidance changes; any provider cohort remains small-sample qualification, not production proof.",
            executionGate = new { maximumProviderCalls = 4, exactlyOneAttemptPerFrozenPack = true, providerCalls = 0,
                explicitAuthorizationRequired = true, retry = 0, repair = false, fallback = false, goldRead = false,
                full31 = false, semanticScoreDuringRun = "NOT_RUN" },
            packCount = output.Count, rows = output,
        });
    }

    private static JsonDocument Read(string relative) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(relative.Replace('/', Path.DirectorySeparatorChar))));
    private static string S(JsonElement row, string property) => row.GetProperty(property).GetString()!;
    private static int I(JsonElement row, string property) => row.GetProperty(property).GetInt32();

    private static void AssertOnlySystemPromptChanged(byte[] baselineBody, byte[] correctedBody)
    {
        using var baseline = JsonDocument.Parse(baselineBody); using var corrected = JsonDocument.Parse(correctedBody);
        var left = baseline.RootElement; var right = corrected.RootElement;
        Assert.Equal(S(left, "model"), S(right, "model"));
        Assert.Equal(left.GetProperty("temperature").GetInt32(), right.GetProperty("temperature").GetInt32());
        Assert.Equal(left.GetProperty("max_tokens").GetInt32(), right.GetProperty("max_tokens").GetInt32());
        Assert.Equal(left.GetProperty("reasoning").GetRawText(), right.GetProperty("reasoning").GetRawText());
        Assert.Equal(left.GetProperty("messages")[1].GetRawText(), right.GetProperty("messages")[1].GetRawText());
        Assert.Equal(left.GetProperty("response_format").GetRawText(), right.GetProperty("response_format").GetRawText());
        Assert.Equal(left.GetProperty("provider").GetRawText(), right.GetProperty("provider").GetRawText());
        Assert.Equal(left.GetProperty("stream").GetBoolean(), right.GetProperty("stream").GetBoolean());
        Assert.Equal(left.GetProperty("usage").GetRawText(), right.GetProperty("usage").GetRawText());
        Assert.Equal("system", S(left.GetProperty("messages")[0], "role"));
        Assert.Equal("system", S(right.GetProperty("messages")[0], "role"));
        Assert.StartsWith(left.GetProperty("messages")[0].GetProperty("content").GetString()!,
            right.GetProperty("messages")[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.EndsWith(BoundaryGuidance, right.GetProperty("messages")[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
    }
}
