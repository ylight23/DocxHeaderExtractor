using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free V3.2 preflight for all 31 P05 parent packs; deliberately stops before transport.</summary>
public sealed class V5P5RV32Full31PreflightTests
{
    private const string ArtifactRoot = "artifacts/v5-full31-v32";
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        {
            UsageInclude = true,
            OpenRouterResponseCacheDisabled = true,
        };

    private sealed record DocumentSpec(string Id, string Pdf, int ExpectedPacks);

    private static readonly DocumentSpec[] Documents =
    [
        new("SRC-089", SourcePdfCorpus.Src089, 7),
        new("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void Freeze_full31_current_v32_preflight_and_stop_before_provider_execution()
    {
        Assert.Equal("v5-source-backed-decision-3.2", V5Protocol.ClaimSchemaVersionV3_2);
        Assert.Equal("v5-semantic-decision-composer-3.2", V5SemanticSparseDecisionComposerV3_1.Version);

        var packRows = new List<object>();
        var requestRows = new List<object>();
        var documentRows = new List<object>();
        var cohortPackCount = 0;
        var maxOwned = 0;
        var maxVisible = 0;
        var maxRequestBytes = 0;
        var maxCompletionTokens = 0;
        var maxResponseBytes = 0;
        var totalOwnedOccurrences = 0;

        foreach (var document in Documents)
        {
            var pdfPath = TestRepository.Path(document.Pdf);
            var packs = V5PdfPreflightBuilder.BuildV3(pdfPath, document.Id, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(pdfPath);
            Assert.Equal(document.ExpectedPacks, packs.Count);

            var allOwned = packs.SelectMany(pack => pack.OwnedAliases).ToArray();
            totalOwnedOccurrences += allOwned.Length;
            var atomAliases = atoms.Select(atom => atom.Alias).ToArray();
            Assert.Equal(atomAliases.ToHashSet(StringComparer.Ordinal), allOwned.ToHashSet(StringComparer.Ordinal));
            Assert.Equal(allOwned.Length, allOwned.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(atomAliases.Length, allOwned.Length);
            Assert.All(packs, pack => Assert.True(pack.OwnedAliases.Count <= 96));

            var sourceUniverseHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "a99-pdf-segment-atom-universe-v1",
                rows = atoms.Select(atom => new
                {
                    sourceAlias = atom.Alias,
                    sourceId = atom.SourceId,
                    ordinal = atom.Ordinal,
                    page = atom.Page,
                    row = atom.Row,
                    segment = atom.Segment,
                    text = atom.Text,
                }).ToArray(),
            }, CanonicalJson.Options));
            var contractHash = Hash(JsonSerializer.SerializeToUtf8Bytes(Contract, CanonicalJson.Options));

            var documentParentRows = new List<object>();
            for (var index = 0; index < packs.Count; index++)
            {
                var pack = packs[index];
                var parentOrdinal = index + 1;
                var composed = V5SemanticSparseDecisionComposerV3_1.Compose(Contract, pack.Packet);
                var expectedCompletionTokens = V5SemanticCompletionBudget.Compute(
                    composed.ResponseBounds.MaxDecisions,
                    composed.ResponseBounds.MaxDecisions,
                    composed.Utf8Bytes,
                    V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
                var body = OpenRouterQwen37JsonObjectCarrierV3.Build(composed, expectedCompletionTokens, Envelope);
                Assert.Equal(pack.OwnedAliases.Count, composed.ResponseBounds.MaxDecisions);
                Assert.Equal("v5-semantic-decision-composer-3.2", composed.ComposerVersion);
                Assert.Equal(pack.OwnedAliases.Count, pack.Packet.SubjectEvidence.Count);
                Assert.DoesNotContain(pack.Packet.ContextOnlyEvidence,
                    node => pack.OwnedAliases.Contains(node.SourceAlias, StringComparer.Ordinal));

                using var prompt = JsonDocument.Parse(composed.Prompt);
                Assert.Equal("v5-source-backed-decision-3.2", prompt.RootElement.GetProperty("protocolVersion").GetString());
                Assert.Equal("v5-semantic-decision-composer-3.2", prompt.RootElement.GetProperty("composerVersion").GetString());
                Assert.Equal(composed.ResponseBounds.MaxResponseUtf8Bytes,
                    prompt.RootElement.GetProperty("responseBounds").GetProperty("maxResponseUtf8Bytes").GetInt32());
                Assert.Equal(0, prompt.RootElement.GetProperty("responseSchema").GetProperty("properties")
                    .GetProperty("decisions").GetProperty("minItems").GetInt32());
                Assert.Equal(pack.OwnedAliases.Count, prompt.RootElement.GetProperty("responseSchema").GetProperty("properties")
                    .GetProperty("decisions").GetProperty("maxItems").GetInt32());

                using var wire = JsonDocument.Parse(body.PayloadBytes);
                Assert.Equal("qwen/qwen3.7-flash", wire.RootElement.GetProperty("model").GetString());
                Assert.Equal(0, wire.RootElement.GetProperty("temperature").GetInt32());
                Assert.Equal("none", wire.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Equal("json_object", wire.RootElement.GetProperty("response_format").GetProperty("type").GetString());
                Assert.True(wire.RootElement.GetProperty("stream").GetBoolean());
                Assert.Equal("alibaba", wire.RootElement.GetProperty("provider").GetProperty("order")[0].GetString());
                Assert.False(wire.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
                Assert.True(wire.RootElement.GetProperty("usage").GetProperty("include").GetBoolean());
                Assert.Equal(expectedCompletionTokens, wire.RootElement.GetProperty("max_tokens").GetInt32());

                var ownedAliasHash = Hash(Encoding.UTF8.GetBytes(string.Join("\n", pack.OwnedAliases)));
                var bodyFile = $"wire-bodies/{document.Id}_parent-{parentOrdinal:D2}.json";
                var bodyText = Encoding.UTF8.GetString(body.PayloadBytes);
                FreezeArtifact.AssertText(ArtifactRoot, bodyFile, bodyText);

                var row = new
                {
                    documentId = document.Id,
                    packId = pack.PackId,
                    parentOrdinal,
                    ownedAliasCount = pack.OwnedAliases.Count,
                    visibleAliasCount = pack.VisibleAliases.Count,
                    ownedAliasHash,
                    sourceUniverseHash,
                    semanticRequestHash = composed.RequestHash,
                    providerRequestHash = body.Hash,
                    providerRequestBytes = body.Bytes,
                    requestUtf8Bytes = composed.Utf8Bytes,
                    maxCompletionTokens = expectedCompletionTokens,
                    maxResponseUtf8Bytes = composed.ResponseBounds.MaxResponseUtf8Bytes,
                    protocolVersion = "v5-source-backed-decision-3.2",
                    composerVersion = "v5-semantic-decision-composer-3.2",
                    providerBodyFile = bodyFile,
                };
                documentParentRows.Add(row);
                packRows.Add(row);
                requestRows.Add(new
                {
                    documentId = document.Id,
                    packId = pack.PackId,
                    parentOrdinal,
                    ownedAliases = pack.OwnedAliases,
                    visibleAliases = pack.VisibleAliases,
                    ownedAliasHash,
                    sourceUniverseHash,
                    taskContractHash = contractHash,
                    semanticRequestHash = composed.RequestHash,
                    providerRequestHash = body.Hash,
                    providerRequestBytes = body.Bytes,
                    requestUtf8Bytes = composed.Utf8Bytes,
                    maxCompletionTokens = expectedCompletionTokens,
                    maxResponseUtf8Bytes = composed.ResponseBounds.MaxResponseUtf8Bytes,
                    protocolVersion = "v5-source-backed-decision-3.2",
                    composerVersion = "v5-semantic-decision-composer-3.2",
                    providerBodyFile = bodyFile,
                });

                cohortPackCount++;
                maxOwned = Math.Max(maxOwned, pack.OwnedAliases.Count);
                maxVisible = Math.Max(maxVisible, pack.VisibleAliases.Count);
                maxRequestBytes = Math.Max(maxRequestBytes, composed.Utf8Bytes);
                maxCompletionTokens = Math.Max(maxCompletionTokens, expectedCompletionTokens);
                maxResponseBytes = Math.Max(maxResponseBytes, composed.ResponseBounds.MaxResponseUtf8Bytes);
            }

            documentRows.Add(new
            {
                documentId = document.Id,
                expectedParentPacks = document.ExpectedPacks,
                actualParentPacks = packs.Count,
                sourceAtomCount = atoms.Count,
                ownedOccurrenceCount = allOwned.Length,
                ownedExactlyOnce = allOwned.Length == atomAliases.Length && allOwned.Distinct(StringComparer.Ordinal).Count() == atomAliases.Length,
                ownedOverlapCount = allOwned.Length - allOwned.Distinct(StringComparer.Ordinal).Count(),
                unownedOccurrenceCount = atomAliases.Except(allOwned, StringComparer.Ordinal).Count(),
                outsideUniverseCount = allOwned.Except(atomAliases, StringComparer.Ordinal).Count(),
                sourceUniverseHash,
                parents = documentParentRows,
            });
        }

        Assert.Equal(31, cohortPackCount);
        Assert.Equal(96, maxOwned);
        Assert.Equal(2884, totalOwnedOccurrences);

        var preflight = new
        {
            schemaVersion = "v5-p5r-v32-full31-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocol = "v5-source-backed-decision-3.2",
            composer = "v5-semantic-decision-composer-3.2",
            packingPolicy = V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId,
            documents = documentRows,
            cohort = new
            {
                parentPacks = cohortPackCount,
                acceptedLeaves = 0,
                ownedOccurrenceCount = totalOwnedOccurrences,
                ownedExactlyOnce = true,
                ownedOverlapCount = 0,
                maxOwnedPerPack = maxOwned,
                maxVisiblePerPack = maxVisible,
                maxRequestUtf8Bytes = maxRequestBytes,
                maxCompletionTokens = maxCompletionTokens,
                maxResponseUtf8Bytes = maxResponseBytes,
            },
            providerCalls = 0,
            goldRead = false,
            goldMutation = "NONE",
        };

        var executionManifest = new
        {
            schemaVersion = "v5-p5r-v32-full31-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            route = new
            {
                gateway = "openrouter",
                api = "chat-completions",
                model = Envelope.Model,
                providerPin = Envelope.Provider,
                temperature = 0,
                reasoning = Envelope.Reasoning,
                fallback = false,
                repair = false,
                stream = Envelope.Streaming,
                usageInclude = Envelope.UsageInclude,
                responseFormat = Envelope.ResponseFormat,
                maxProviderCompletionTokens = V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling,
            },
            protocol = "v5-source-backed-decision-3.2",
            composer = "v5-semantic-decision-composer-3.2",
            requests = requestRows,
            providerCalls = 0,
            goldRead = false,
            authorization = new
            {
                providerExecutionAuthorized = false,
                full31ParentPackExecutionAuthorized = false,
                explicitAuthorizationRequired = true,
                retryPolicy = "NOT_RUN; any future transport retry must follow a separately frozen policy and lineage",
            },
        };

        FreezeArtifact.AssertJson(ArtifactRoot, "preflight.v1.json", preflight);
        FreezeArtifact.AssertJson(ArtifactRoot, "execution-manifest.v1.json", executionManifest);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
