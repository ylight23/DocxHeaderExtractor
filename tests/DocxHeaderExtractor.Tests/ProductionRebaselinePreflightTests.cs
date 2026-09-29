using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free preflight for the production-quality re-baseline of the neutral single-lane PDF route
/// (P05 packing, V4 semantic function, neutral source facts). It freezes, per leaf, exactly what the
/// run may send - the request body bytes, the semantic request hash and the provider envelope hash -
/// and proves the leaves own every atom exactly once. Gold is never read here.
/// <para>
/// This is a new baseline, not a continuation of T3/T4: the model-visible evidence changed (neutral
/// location facts, no harness markers, no scope labels), so the request bytes differ and no causal
/// comparison with earlier scores is claimed.
/// </para>
/// </summary>
public sealed class ProductionRebaselinePreflightTests
{
    private const string Root = "eval/a99-closed-loop/production-rebaseline-v1";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderRoute = "Alibaba";
    private const int ProductionMaxOutputTokens = 32768;

    private static readonly (string Id, string Pdf)[] Documents =
        [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];

    [Fact]
    public async Task Freeze_production_rebaseline_preflight()
    {
        var leaves = new List<object>();
        var ordinal = 0;
        var documents = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                TestRepository.Path(pdf), capture, CancellationToken.None, runPlacement: false);
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms
                .Select(atom => atom.Alias).ToArray();

            var owned = capture.Requests.Select(request => OwnedAliases(request.UserMessage)).ToArray();
            var allOwned = owned.SelectMany(x => x).ToArray();
            var overlap = allOwned.Length - allOwned.Distinct(StringComparer.Ordinal).Count();
            var covered = atoms.Intersect(allOwned, StringComparer.Ordinal).Count();
            Assert.Equal(0, overlap);
            Assert.Equal(atoms.Length, covered);
            Assert.Equal(atoms, allOwned);

            for (var leaf = 0; leaf < capture.Requests.Count; leaf++)
            {
                ordinal++;
                var request = capture.Requests[leaf];
                Assert.Equal(owned[leaf].Count, request.ExpectedItemCount);
                var maxTokens = Math.Clamp(96 + request.ExpectedItemCount * 128, 256, ProductionMaxOutputTokens);
                var body = RequestBody(request.SystemPrompt, request.UserMessage, maxTokens);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var semanticHash = Sha(request.SystemPrompt.ReplaceLineEndings("\n") + "\n" + request.UserMessage.ReplaceLineEndings("\n"));
                var envelopeHash = Sha(EnvelopeCanonicalJson(semanticHash, maxTokens));
                var requestFile = $"planned/leaf-{ordinal:000}.request.json";
                FreezeArtifact.AssertText(Root, requestFile, body);

                leaves.Add(new
                {
                    ordinal,
                    documentId = id,
                    leafInDocument = leaf + 1,
                    ownedAliasCount = owned[leaf].Count,
                    firstOwnedAlias = owned[leaf][0],
                    lastOwnedAlias = owned[leaf][^1],
                    ownedAliasesSha256 = Sha(string.Join("\n", owned[leaf])),
                    primaryInputBytes = bodyBytes.Length,
                    maxTokens,
                    semanticRequestHash = semanticHash,
                    providerEnvelopeHash = envelopeHash,
                    requestBodySha256 = Convert.ToHexStringLower(SHA256.HashData(bodyBytes)),
                    requestFile,
                });
            }

            documents.Add(new
            {
                documentId = id,
                atoms = atoms.Length,
                leaves = capture.Requests.Count,
                ownedAliasCoverage = $"{covered}/{atoms.Length}",
                ownedAliasCoveragePercent = Math.Round(100.0 * covered / atoms.Length, 2),
                ownershipOverlap = overlap,
            });
        }

        FreezeArtifact.AssertJson(Root, "production-rebaseline-preflight.v1.json", new
        {
            artifactKind = "a99_pdf_production_rebaseline_preflight",
            status = "FROZEN_BEFORE_PROVIDER_CALLS",
            baseline = "NEW_PRODUCTION_QUALITY_BASELINE",
            comparability = "Model-visible evidence changed (neutral location facts, no harness markers, no scope labels, single V4 PDF lane); request bytes differ from T3/T4, so no causal comparison with earlier scores is claimed.",
            route = "CanonicalSemanticPdfAuthorityAdapter.RunAsync, primary semantic pass only (placement off, as in every earlier V4 measurement)",
            providerCalls = 0,
            goldRead = false,
            productionChanged = false,
            actualLeafCount = ordinal,
            documents,
            pins = new
            {
                model = Model,
                providerRoute = ProviderRoute,
                allowFallbacks = false,
                reasoningEffort = "none",
                responseFormat = "json_object",
                temperature = 0,
                stream = true,
                usage = new { include = true },
                maxTokens = "production formula per leaf: clamp(96 + 128 * ownedAliasCount, 256, 32768)",
                openRouterResponseCache = "disabled",
                cacheControl = "not sent",
                sessionId = "not sent",
            },
            executionPolicy = new
            {
                concurrency = 1,
                deterministicPacingMsBetweenLeaves = 2500,
                maxTransportRetriesPerLeaf429 = 2,
                maxTransportRetriesPerLeaf5xxOrNetwork = 2,
                retryAfterHeader = "honor when present",
                noResendAfterContractValid = true,
                mediumFallback = false,
                semanticRecoveryDuringPrimary = false,
                productionMutationDuringRun = false,
                goldClosedDuringExecution = true,
                onLengthInvalidJsonOrContractInvalid = "record as semantic/execution failure; never retried as transport",
            },
            hashFormulas = new
            {
                semanticRequestHash = "sha256(lowercase hex) of UTF-8(systemPrompt + \"\\n\" + userMessage), LF line endings",
                providerEnvelopeHash = "sha256(lowercase hex) of UTF-8 canonical JSON " + EnvelopeCanonicalJson("<semanticRequestHash>", 0).Replace("\"maxTokens\":0", "\"maxTokens\":<maxTokens>"),
                primaryInputBytes = "UTF-8 byte length of the exact request body file",
            },
            leaves,
        });
    }

    internal static string RequestBody(string systemPrompt, string userMessage, int maxTokens)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["reasoning"] = new JsonObject { ["effort"] = "none" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["provider"] = new JsonObject
            {
                ["order"] = new JsonArray(ProviderRoute),
                ["allow_fallbacks"] = false,
                ["require_parameters"] = true,
                ["data_collection"] = "deny",
                ["zdr"] = false,
            },
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        return body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string EnvelopeCanonicalJson(string semanticHash, int maxTokens) =>
        "{\"providerRoute\":\"" + ProviderRoute + "\",\"model\":\"" + Model + "\",\"reasoningEffort\":\"none\"," +
        "\"responseFormat\":\"json_object\",\"explicitCacheControl\":null,\"stream\":true,\"usage\":{\"include\":true}," +
        "\"openRouterResponseCache\":\"disabled\",\"maxTokens\":" + maxTokens + ",\"semanticRequestHash\":\"" + semanticHash + "\"}";

    private static List<string> OwnedAliases(string userMessage)
    {
        using var packet = JsonDocument.Parse(userMessage[..userMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal)]);
        return packet.RootElement.GetProperty("ownedSourceAliases").EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
