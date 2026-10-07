using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Paired, offline Gold score for the four P6M/P6N-B canary packs.</summary>
public sealed class V5P6NBBoundLocatorGoldScoreTests
{
    private const string P6MRoot = "artifacts/v5-p6m-p6l-full31-qualification";
    private const string P6NBRoot = "artifacts/v5-p6nb-free-semantic-bound-locator";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Role, string DocumentId, string Pdf, int Ordinal)[] Packs =
    [
        ("MAX_REQUEST_BODY", "SRC-089", SourcePdfCorpus.Src089, 4),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, 17),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, 20),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, 11),
    ];

    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(string Identity, string DocumentId, string Text, IReadOnlyList<Part> Parts);
    private sealed record Prediction(string Identity, string DocumentId, IReadOnlyList<Part> Parts,
        IReadOnlyList<string> Functions, string Text, int ParentOrdinal)
    {
        public bool IsHeadingMember => Functions.Contains("DOCUMENT_IDENTITY", StringComparer.Ordinal) ||
            Functions.Contains("STRUCTURAL_REGION", StringComparer.Ordinal);
    }
    private sealed record Pair((string Role, string DocumentId, string Pdf, int Ordinal) Pack,
        IReadOnlyList<Gold> Gold, IReadOnlyList<Gold> AllGold, IReadOnlyList<Prediction> P6M, IReadOnlyList<Prediction> P6NB,
        string P6MRawHash, string P6NBRawHash, string P6MBodyHash, string P6NBBodyHash,
        int P6MQuarantines, int P6NBQuarantines, int P6NBSelectedRetryAttempt, string? P6NBSecondRetryRawHash,
        int? P6NBSecondRetryBoundCount, bool? P6NBSecondRetrySameLocatorSet);
    private sealed record ScoredArm(int ExactTp, int ExactFp, int ExactFn, int SemanticTp, int SemanticFp, int SemanticFn,
        int OutOfScopeExactMatches, int OutOfScopeSemanticOverlaps, IReadOnlyList<Prediction> ExactFalsePositives,
        IReadOnlyList<Prediction> SemanticFalsePositives, IReadOnlyDictionary<string, int> Extent);

    [Fact]
    public void Score_P6NB_four_pack_bound_locator_against_paired_P6M_offline()
    {
        var repo = TestRepository.Root();
        var p6mResultPath = $"{P6MRoot}/result.v1.json";
        var p6mManifestPath = $"{P6MRoot}/execution-manifest.v1.json";
        var p6mScorePath = $"{P6MRoot}/gold-occurrence-score.v1.json";
        var p6nbManifestPath = $"{P6NBRoot}/execution-manifest.v1.json";
        var p6nbResultPath = $"{P6NBRoot}/result.v1.json";
        using var p6mResultDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, p6mResultPath.Replace('/', Path.DirectorySeparatorChar))));
        using var p6mManifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, p6mManifestPath.Replace('/', Path.DirectorySeparatorChar))));
        using var p6mScoreDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, p6mScorePath.Replace('/', Path.DirectorySeparatorChar))));
        using var p6nbManifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, p6nbManifestPath.Replace('/', Path.DirectorySeparatorChar))));
        using var p6nbResultDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, p6nbResultPath.Replace('/', Path.DirectorySeparatorChar))));
        var p6mResult = p6mResultDoc.RootElement;
        var p6mManifest = p6mManifestDoc.RootElement;
        var p6mFullScore = p6mScoreDoc.RootElement;
        var p6nbManifest = p6nbManifestDoc.RootElement;
        var p6nbResult = p6nbResultDoc.RootElement;

        Assert.Equal("v5-p6m-p6l-full31-result-v1", p6mResult.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, p6mResult.GetProperty("providerCalls").GetInt32());
        Assert.Equal("v5-p6m-gold-occurrence-score-v1", p6mFullScore.GetProperty("schemaVersion").GetString());
        Assert.True(p6mFullScore.GetProperty("goldRead").GetBoolean());
        Assert.Equal("v5-p6nb-free-semantic-bound-locator-result-v1", p6nbResult.GetProperty("schemaVersion").GetString());
        Assert.Equal(6, p6nbResult.GetProperty("providerCalls").GetInt32());
        Assert.Equal(2, p6nbResult.GetProperty("additionalRetryAttempts").GetInt32());
        Assert.False(p6nbResult.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", p6nbResult.GetProperty("semanticScore").GetString());
        Assert.Equal("CLOSED_AFTER_TWO_AUTHORIZED_CALL4_RETRIES", p6nbResult.GetProperty("stopGate").GetString());
        Assert.Equal("P6M vs P6N-B changes ontology and reasoning together; result estimates only their combined arm difference.",
            p6nbManifest.GetProperty("causalLimit").GetString());

        var p6mRows = p6mResult.GetProperty("rows").EnumerateArray().ToArray();
        var p6mManifestRows = p6mManifest.GetProperty("rows").EnumerateArray().ToArray();
        var p6nbRows = p6nbResult.GetProperty("rows").EnumerateArray().ToArray();
        var p6nbManifestRows = p6nbManifest.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, p6mRows.Length);
        Assert.Equal(4, p6nbRows.Length);
        Assert.Equal(4, p6nbManifestRows.Length);
        Assert.Equal(0, p6nbManifest.GetProperty("providerCalls").GetInt32());
        Assert.Equal("PREPARED_NOT_AUTHORIZED", p6nbManifest.GetProperty("status").GetString());

        var goldByDocument = ReadGold();
        var sourceReview = ReadSourceReview();
        var cache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>(StringComparer.Ordinal);
        var pairs = new List<Pair>();
        var packGoldOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var selected in Packs)
        {
            if (!cache.TryGetValue(selected.DocumentId, out var corpus))
            {
                var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(selected.Pdf), selected.DocumentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope, FrozenSourceSnapshots.V1Root);
                var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(selected.Pdf), FrozenSourceSnapshots.V1Root).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
                corpus = (packs, atoms);
                cache.Add(selected.DocumentId, corpus);
            }

            var pack = corpus.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{selected.Ordinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => corpus.Atoms[alias]).ToArray());
            var p6mRow = p6mRows.Single(row => row.GetProperty("documentId").GetString() == selected.DocumentId &&
                row.GetProperty("parentOrdinal").GetInt32() == selected.Ordinal);
            var p6mManifestRow = p6mManifestRows.Single(row => row.GetProperty("documentId").GetString() == selected.DocumentId &&
                row.GetProperty("parentOrdinal").GetInt32() == selected.Ordinal);
            var p6nbRow = p6nbRows.Single(row => row.GetProperty("documentId").GetString() == selected.DocumentId &&
                row.GetProperty("parentOrdinal").GetInt32() == selected.Ordinal);
            var p6nbManifestRow = p6nbManifestRows.Single(row => row.GetProperty("role").GetString() == selected.Role);

            Assert.Equal(pack.PackId, p6mRow.GetProperty("packId").GetString());
            Assert.Equal(pack.PackId, p6nbRow.GetProperty("packId").GetString());
            Assert.Equal(registry.Fingerprint, p6mRow.GetProperty("registryFingerprint").GetString());
            Assert.Equal(registry.Fingerprint, p6nbRow.GetProperty("registryFingerprint").GetString());
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var p6mBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(canonical.SystemPrompt, canonical.UserMessage,
                pack.MaxCompletionTokens, Envelope);
            Assert.Equal(p6mManifestRow.GetProperty("semanticRequestHash").GetString(), canonical.UserMessageSha256);
            Assert.Equal(p6mManifestRow.GetProperty("providerRequestHash").GetString(), p6mBody.Hash);
            var p6nbRequest = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            var p6nbBody = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(p6nbRequest, pack.MaxCompletionTokens);
            Assert.Equal(p6nbManifestRow.GetProperty("semanticRequestHash").GetString(), p6nbRequest.UserMessageSha256);
            Assert.Equal(p6nbManifestRow.GetProperty("providerRequestHash").GetString(), p6nbBody.Hash);
            Assert.Equal(p6mBody.Hash, p6mManifestRow.GetProperty("providerRequestHash").GetString());

            var p6mRawText = p6mRow.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(p6mRawText), p6mRow.GetProperty("rawResponseSha256").GetString());
            Assert.True(p6mRow.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", p6mRow.GetProperty("finishReason").GetString());
            Assert.Equal(0, p6mRow.GetProperty("retryCount").GetInt32());
            Assert.True(p6mRow.GetProperty("rawResponseBytes").GetInt32() <= ResponseCap);
            using var p6mRaw = JsonDocument.Parse(p6mRawText);
            Assert.Equal(p6mRow.GetProperty("analysis").GetProperty("rawOccurrences").GetInt32(),
                p6mRaw.RootElement.GetProperty("occurrences").GetArrayLength());
            var p6mParsed = registry.Parse(p6mRaw.RootElement, p6mRow.GetProperty("rawResponseBytes").GetInt32(), ResponseCap,
                Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(p6mParsed.Quarantined);
            var p6mPredictions = DecodePredictions(selected, registry, p6mParsed.Response.Occurrences);

            var isRetrySelected = selected.Role == "MULTIPART_RELATION";
            JsonElement p6nbCanonicalResponse;
            var retryRows = Array.Empty<JsonElement>();
            if (isRetrySelected)
            {
                retryRows = p6nbRow.GetProperty("retryAttempts").EnumerateArray().ToArray();
                Assert.Equal(2, retryRows.Length);
                Assert.True(p6nbRow.GetProperty("transportError").GetString()!.Contains("429", StringComparison.Ordinal));
                p6nbCanonicalResponse = retryRows.OrderBy(row => row.GetProperty("additionalAttempt").GetInt32()).First();
                Assert.Equal(1, p6nbCanonicalResponse.GetProperty("additionalAttempt").GetInt32());
                Assert.Equal("PARSER_BINDER_VALID", p6nbCanonicalResponse.GetProperty("analysis").GetProperty("classification").GetString());
                Assert.Equal(0, p6nbCanonicalResponse.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32());
            }
            else
            {
                p6nbCanonicalResponse = p6nbRow;
                Assert.Equal("PARSER_BINDER_VALID", p6nbRow.GetProperty("analysis").GetProperty("classification").GetString());
                Assert.Equal(0, p6nbRow.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32());
            }
            Assert.Equal(p6nbManifestRow.GetProperty("providerRequestHash").GetString(), p6nbCanonicalResponse.GetProperty("providerRequestHash").GetString());
            Assert.Equal("stop", p6nbCanonicalResponse.GetProperty("finishReason").GetString());
            Assert.Equal(0, p6nbCanonicalResponse.GetProperty("retryCount").GetInt32());
            var p6nbRawText = p6nbCanonicalResponse.GetProperty("rawResponse").GetString()!;
            var p6nbRawHash = Hash(p6nbRawText);
            Assert.Equal(p6nbRawHash, p6nbCanonicalResponse.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(p6nbCanonicalResponse.GetProperty("responseBytes").GetInt32(), Encoding.UTF8.GetByteCount(p6nbRawText));
            using var p6nbRaw = JsonDocument.Parse(p6nbRawText);
            Assert.Equal(p6nbCanonicalResponse.GetProperty("analysis").GetProperty("rawHeadings").GetInt32(),
                p6nbRaw.RootElement.GetProperty("headings").GetArrayLength());
            var p6nbParsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(p6nbRaw.RootElement,
                p6nbCanonicalResponse.GetProperty("responseBytes").GetInt32(), ResponseCap, registry,
                Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(p6nbParsed.Quarantined);
            Assert.Equal(p6nbCanonicalResponse.GetProperty("analysis").GetProperty("boundHeadings").GetInt32(), p6nbParsed.Response.Occurrences.Count);
            var p6nbPredictions = DecodePredictions(selected, registry, p6nbParsed.Response.Occurrences, freeMembership: true);
            string? secondRetryRawHash = null;
            int? secondRetryBoundCount = null;
            bool? secondRetrySameLocatorSet = null;
            if (isRetrySelected)
            {
                var secondRetry = retryRows.Single(row => row.GetProperty("additionalAttempt").GetInt32() == 2);
                Assert.Equal(p6nbManifestRow.GetProperty("providerRequestHash").GetString(), secondRetry.GetProperty("providerRequestHash").GetString());
                Assert.Equal("stop", secondRetry.GetProperty("finishReason").GetString());
                Assert.Equal("PARSER_BINDER_VALID", secondRetry.GetProperty("analysis").GetProperty("classification").GetString());
                Assert.Equal(0, secondRetry.GetProperty("analysis").GetProperty("quarantinedHeadings").GetInt32());
                var secondRetryRaw = secondRetry.GetProperty("rawResponse").GetString()!;
                secondRetryRawHash = Hash(secondRetryRaw);
                Assert.Equal(secondRetryRawHash, secondRetry.GetProperty("rawResponseSha256").GetString());
                using var secondRetryJson = JsonDocument.Parse(secondRetryRaw);
                var secondRetryParsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(secondRetryJson.RootElement,
                    secondRetry.GetProperty("responseBytes").GetInt32(), ResponseCap, registry,
                    Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
                Assert.Empty(secondRetryParsed.Quarantined);
                var secondRetryPredictions = DecodePredictions(selected, registry, secondRetryParsed.Response.Occurrences, freeMembership: true);
                secondRetryBoundCount = secondRetryPredictions.Count;
                secondRetrySameLocatorSet = p6nbPredictions.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(secondRetryPredictions.Select(item => item.Identity));
            }

            var selectedGold = goldByDocument[selected.DocumentId].Where(item => pack.OwnedAliases.Contains(item.Parts[0].Alias, StringComparer.Ordinal)).ToArray();
            foreach (var gold in selectedGold)
            {
                var id = $"{selected.DocumentId}|{gold.Identity}";
                Assert.DoesNotContain(id, packGoldOwner.Keys);
                packGoldOwner.Add(id, selected.Role);
            }
            pairs.Add(new Pair(selected, selectedGold, goldByDocument[selected.DocumentId], p6mPredictions, p6nbPredictions,
                Hash(p6mRawText), p6nbRawHash, p6mBody.Hash, p6nbBody.Hash, p6mParsed.Quarantined.Count,
                p6nbParsed.Quarantined.Count, isRetrySelected ? 1 : 0, secondRetryRawHash,
                secondRetryBoundCount, secondRetrySameLocatorSet));
        }

        var selectedGoldAll = pairs.SelectMany(pair => pair.Gold).ToArray();
        Assert.Equal(selectedGoldAll.Length, selectedGoldAll.Select(item => $"{item.DocumentId}|{item.Identity}").Distinct(StringComparer.Ordinal).Count());
        Assert.All(pairs, pair => Assert.Equal(0, pair.P6MQuarantines + pair.P6NBQuarantines));

        var p6mPredictionsAll = pairs.SelectMany(pair => pair.P6M).ToArray();
        var p6nbPredictionsAll = pairs.SelectMany(pair => pair.P6NB).ToArray();
        var selectedByDocument = selectedGoldAll.GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Gold>)group.ToArray(), StringComparer.Ordinal);
        var allGoldByDocument = goldByDocument.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var p6mScore = Score(selectedByDocument, allGoldByDocument, p6mPredictionsAll, sourceReview, freeMembership: false);
        var p6nbScore = Score(selectedByDocument, allGoldByDocument, p6nbPredictionsAll, sourceReview, freeMembership: true);

        var perPack = pairs.Select(pair =>
        {
            var goldScope = new Dictionary<string, IReadOnlyList<Gold>>(StringComparer.Ordinal) { [pair.Pack.DocumentId] = pair.Gold };
            var allScope = new Dictionary<string, IReadOnlyList<Gold>>(StringComparer.Ordinal) { [pair.Pack.DocumentId] = pair.AllGold };
            var p6m = Score(goldScope, allScope, pair.P6M, sourceReview, freeMembership: false);
            var p6nb = Score(goldScope, allScope, pair.P6NB, sourceReview, freeMembership: true);
            var p6mExactIds = DetectedExactGoldIds(pair.Gold, pair.P6M.Where(item => item.IsHeadingMember));
            var p6nbExactIds = DetectedExactGoldIds(pair.Gold, pair.P6NB);
            var exactBoth = p6mExactIds.Intersect(p6nbExactIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var exactP6mOnly = p6mExactIds.Except(p6nbExactIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var exactP6nbOnly = p6nbExactIds.Except(p6mExactIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var p6mSemanticIds = DetectedGoldIds(pair.Gold, pair.P6M.Where(item => item.IsHeadingMember));
            var p6nbSemanticIds = DetectedGoldIds(pair.Gold, pair.P6NB);
            var bothTp = p6mSemanticIds.Intersect(p6nbSemanticIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var p6mOnly = p6mSemanticIds.Except(p6nbSemanticIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var p6nbOnly = p6nbSemanticIds.Except(p6mSemanticIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var p6mFpIds = p6m.SemanticFalsePositives.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
            var p6nbFpIds = p6nb.SemanticFalsePositives.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
            return new
            {
                role = pair.Pack.Role, documentId = pair.Pack.DocumentId, parentOrdinal = pair.Pack.Ordinal,
                packId = $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{pair.Pack.Ordinal:000}",
                goldOccurrenceCount = pair.Gold.Count,
                p6m = ArmArtifact(pair.P6M, p6m, freeMembership: false),
                p6nb = ArmArtifact(pair.P6NB, p6nb, freeMembership: true),
                exactTpComparison = new
                {
                    both = exactBoth.Length, p6mOnly = exactP6mOnly.Length, p6nbOnly = exactP6nbOnly.Length,
                    p6nbLostTruePositives = exactP6mOnly.Length,
                    p6mOnlyGoldIdentities = exactP6mOnly, p6nbOnlyGoldIdentities = exactP6nbOnly,
                },
                semanticTpComparison = new
                {
                    both = bothTp.Length, p6mOnly = p6mOnly.Length, p6nbOnly = p6nbOnly.Length,
                    p6mOnlyGoldIdentities = p6mOnly, p6nbOnlyGoldIdentities = p6nbOnly,
                    p6nbLostTruePositives = p6mOnly.Length,
                },
                falsePositiveComparison = new
                {
                    p6nbFalsePositives = p6nbFpIds.Count,
                    newP6nbFalsePositivesVersusP6m = p6nbFpIds.Except(p6mFpIds, StringComparer.Ordinal).Count(),
                    sharedFalsePositiveIdentities = p6nbFpIds.Intersect(p6mFpIds, StringComparer.Ordinal).Count(),
                    p6mOnlyFalsePositiveIdentities = p6mFpIds.Except(p6nbFpIds, StringComparer.Ordinal).Count(),
                },
            };
        }).ToArray();

        var p6mSemanticIdsAll = DetectedGoldIds(selectedGoldAll, p6mPredictionsAll.Where(item => item.IsHeadingMember));
        var p6nbSemanticIdsAll = DetectedGoldIds(selectedGoldAll, p6nbPredictionsAll);
        var p6mExactIdsAll = DetectedExactGoldIds(selectedGoldAll, p6mPredictionsAll.Where(item => item.IsHeadingMember));
        var p6nbExactIdsAll = DetectedExactGoldIds(selectedGoldAll, p6nbPredictionsAll);
        var exactOnlyP6mAll = p6mExactIdsAll.Except(p6nbExactIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var exactOnlyP6nbAll = p6nbExactIdsAll.Except(p6mExactIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var exactBothAll = p6mExactIdsAll.Intersect(p6nbExactIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var p6mOnlyAll = p6mSemanticIdsAll.Except(p6nbSemanticIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var p6nbOnlyAll = p6nbSemanticIdsAll.Except(p6mSemanticIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var bothAll = p6mSemanticIdsAll.Intersect(p6nbSemanticIdsAll, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        FreezeArtifact.AssertJson(P6NBRoot, "paired-gold-score.v1.json", new
        {
            schemaVersion = "v5-p6nb-paired-four-pack-gold-score-v1",
            source = new { p6mResult = $"{P6MRoot}/result.v1.json", p6mManifest = $"{P6MRoot}/execution-manifest.v1.json",
                p6mGoldScoreAuthority = p6mFullScore.GetProperty("schemaVersion").GetString(),
                p6nbResult = $"{P6NBRoot}/result.v1.json", p6nbManifest = $"{P6NBRoot}/execution-manifest.v1.json" },
            providerCalls = 0, goldRead = true, goldMutation = "NONE", sharedRuntimeChanged = false,
            rawResponses = new
            {
                immutable = true,
                p6m = pairs.Select(pair => new { pair.Pack.Role, responseSha256 = pair.P6MRawHash, bodySha256 = pair.P6MBodyHash }).ToArray(),
                p6nb = pairs.Select(pair => new
                {
                    pair.Pack.Role, responseSha256 = pair.P6NBRawHash, bodySha256 = pair.P6NBBodyHash,
                    selectedRetryAttempt = pair.P6NBSelectedRetryAttempt,
                    selectionRule = pair.Pack.Role == "MULTIPART_RELATION"
                        ? "first contract-valid successful response after the initial authorized 429; later successful response is reproducibility-only"
                        : "initial successful contract-valid response",
                    secondRetryReproducibilityEvidence = new
                    {
                        rawResponseSha256 = pair.P6NBSecondRetryRawHash,
                        boundLocatorCount = pair.P6NBSecondRetryBoundCount,
                        sameCanonicalLocatorSetAsScoredResponse = pair.P6NBSecondRetrySameLocatorSet,
                        usedForGoldScore = false,
                    },
                }).ToArray(),
            },
            authority = new
            {
                pairKey = "documentId + frozen packId/parentOrdinal + request-body lineage; same four P05 source packs",
                identity = "document-scoped ordered sourceAlias + exact UTF16 start/end for every part",
                semantic = "P6M heading membership is DOCUMENT_IDENTITY or STRUCTURAL_REGION; P6N-B headings[] are all free semantic heading proposals; Gold hit means actual source-span overlap",
                exact = "ordered source parts and exact UTF16 spans equal the Gold occurrence",
                goldScope = "Gold occurrences whose primary source alias is owned by exactly one of the four selected packs; deduplicated across packs",
                outOfScope = "candidate overlapping a same-document Gold occurrence outside the four-pack Gold scope is reported and excluded from in-scope FP rather than mislabelled NON_HEADING",
                falsePositiveReview = "every in-scope semantic FP checked against the existing source-review-v1 verdicts",
                extentBuckets = "EXACT; PARTIAL; SPLIT; MULTIPART_DIFFERENCE; NO_PROPOSAL; WRONG_SEMANTIC_FUNCTION (P6M only)",
                causalLimit = "P6M vs P6N-B changes ontology and reasoning together; paired result identifies only the combined arm difference, not either factor alone",
            },
            execution = new
            {
                p6mSourceProviderCalls = 31, p6nbInitialAttempts = 4, p6nbAuthorizedAdditionalAttempts = 2,
                p6nbRetryPolicy = "only frozen MULTIPART_RELATION body; exactly two extra attempts; no internal retry",
                selectedP6nbResponse = "MULTIPART_RELATION uses first successful contract-valid retry only; second successful response is not scored",
                p6mParserQuarantines = pairs.Sum(pair => pair.P6MQuarantines), p6nbParserQuarantines = pairs.Sum(pair => pair.P6NBQuarantines),
                goldOccurrenceCount = selectedGoldAll.Length, p6mBoundUnits = p6mPredictionsAll.Length, p6nbBoundUnits = p6nbPredictionsAll.Length,
            },
            metrics = new
            {
                p6m = new { exact = Metric(p6mScore.ExactTp, p6mScore.ExactFp, p6mScore.ExactFn),
                    semanticOccurrence = Metric(p6mScore.SemanticTp, p6mScore.SemanticFp, p6mScore.SemanticFn),
                    headingUnits = p6mPredictionsAll.Count(item => item.IsHeadingMember), outOfScopeExactMatches = p6mScore.OutOfScopeExactMatches,
                    outOfScopeSemanticOverlaps = p6mScore.OutOfScopeSemanticOverlaps, extent = p6mScore.Extent },
                p6nb = new { exact = Metric(p6nbScore.ExactTp, p6nbScore.ExactFp, p6nbScore.ExactFn),
                    semanticOccurrence = Metric(p6nbScore.SemanticTp, p6nbScore.SemanticFp, p6nbScore.SemanticFn),
                    headingUnits = p6nbPredictionsAll.Length, outOfScopeExactMatches = p6nbScore.OutOfScopeExactMatches,
                    outOfScopeSemanticOverlaps = p6nbScore.OutOfScopeSemanticOverlaps, extent = p6nbScore.Extent },
                pairedExactTp = new { both = exactBothAll.Length, p6mOnly = exactOnlyP6mAll.Length,
                    p6nbOnly = exactOnlyP6nbAll.Length, p6nbLostTruePositives = exactOnlyP6mAll.Length,
                    bothGoldIdentities = exactBothAll, p6mOnlyGoldIdentities = exactOnlyP6mAll,
                    p6nbOnlyGoldIdentities = exactOnlyP6nbAll },
                pairedSemanticTp = new { both = bothAll.Length, p6mOnly = p6mOnlyAll.Length, p6nbOnly = p6nbOnlyAll.Length,
                    p6nbLostTruePositives = p6mOnlyAll.Length, bothGoldIdentities = bothAll, p6mOnlyGoldIdentities = p6mOnlyAll, p6nbOnlyGoldIdentities = p6nbOnlyAll },
                falsePositiveComparison = new
                {
                    p6nbFalsePositives = p6nbScore.SemanticFalsePositives.Count,
                    newP6nbFalsePositivesVersusP6m = p6nbScore.SemanticFalsePositives.Select(item => item.Identity)
                        .Except(p6mScore.SemanticFalsePositives.Select(item => item.Identity), StringComparer.Ordinal).Count(),
                    sharedFalsePositiveIdentities = p6nbScore.SemanticFalsePositives.Select(item => item.Identity)
                        .Intersect(p6mScore.SemanticFalsePositives.Select(item => item.Identity), StringComparer.Ordinal).Count(),
                    p6mFalsePositives = p6mScore.SemanticFalsePositives.Count,
                    p6mOnlyFalsePositiveIdentities = p6mScore.SemanticFalsePositives.Select(item => item.Identity)
                        .Except(p6nbScore.SemanticFalsePositives.Select(item => item.Identity), StringComparer.Ordinal).Count(),
                    p6nbFalsePositiveReviews = Reviews(p6nbScore.SemanticFalsePositives, sourceReview),
                },
            },
            perPack = perPack,
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Gold>> ReadGold()
    {
        var result = new Dictionary<string, IReadOnlyList<Gold>>(StringComparer.Ordinal);
        foreach (var documentId in Packs.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal))
        {
            FrozenHistoryGold.RequireCapability(documentId, GoldCapability.Occurrence);
            using var gold = FrozenHistoryGold.Resolve(documentId);
            result[documentId] = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim =>
            {
                var parts = claim.GetProperty("boundParts").EnumerateArray().Select(part => new Part(
                    part.GetProperty("sourceAlias").GetString()!, part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray();
                return new Gold(claim.GetProperty("identity").GetString()!, documentId, claim.GetProperty("projectedText").GetString()!, parts);
            }).ToArray();
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadSourceReview() => Src089SourceReviewTests.Items()
        .SelectMany(item => item.Parts.Select(part => (Key: $"SRC-089:{part.SourceAlias}", item.Verdict)))
        .Concat(Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => (Key: $"SRC-095:{part.SourceAlias}", item.Verdict))))
        .GroupBy(row => row.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(row => row.Verdict).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);

    private static IReadOnlyList<Prediction> DecodePredictions((string Role, string DocumentId, string Pdf, int Ordinal) pack,
        RequestLocalLocatorRegistry registry, IReadOnlyList<OccurrenceLocator> locators, bool freeMembership = false)
    {
        var byIdentity = new Dictionary<string, Prediction>(StringComparer.Ordinal);
        foreach (var locator in locators)
        {
            var endpoint = registry.Decode(locator);
            var parts = endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
            var identity = Identity(pack.DocumentId, parts);
            var functions = freeMembership ? Array.Empty<string>() : locator.Functions.Order(StringComparer.Ordinal).ToArray();
            if (byIdentity.TryGetValue(identity, out var existing))
                byIdentity[identity] = existing with { Functions = existing.Functions.Union(functions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
            else
                byIdentity.Add(identity, new Prediction(identity, pack.DocumentId, parts, functions,
                    string.Join(" ", endpoint.Parts.Select(part => part.Text)), pack.Ordinal));
        }
        return byIdentity.Values.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
    }

    private static ScoredArm Score(IReadOnlyDictionary<string, IReadOnlyList<Gold>> goldScope,
        IReadOnlyDictionary<string, IReadOnlyList<Gold>> allGold, IReadOnlyList<Prediction> candidates,
        IReadOnlyDictionary<string, string> review, bool freeMembership)
    {
        var headingCandidates = freeMembership ? candidates : candidates.Where(item => item.IsHeadingMember).ToArray();
        var selectedGold = goldScope.Values.SelectMany(items => items).ToArray();
        var goldIdentitySet = allGold.Values.SelectMany(items => items).Select(item => Identity(item.DocumentId, item.Parts)).ToHashSet(StringComparer.Ordinal);
        var exactGold = new List<Gold>(); var semanticGold = new List<Gold>(); var extent = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gold in selectedGold)
        {
            var sameDocument = headingCandidates.Where(item => item.DocumentId == gold.DocumentId).ToArray();
            var exact = sameDocument.Any(item => SameParts(item.Parts, gold.Parts));
            var overlaps = sameDocument.Where(item => Overlaps(item.Parts, gold.Parts)).ToArray();
            var nonMemberOverlap = !freeMembership && candidates.Any(item => item.DocumentId == gold.DocumentId && !item.IsHeadingMember && Overlaps(item.Parts, gold.Parts));
            if (exact) exactGold.Add(gold);
            if (overlaps.Length > 0) semanticGold.Add(gold);
            var bucket = exact ? "EXACT" : overlaps.Length > 1 ? "SPLIT"
                : overlaps.Length == 1 && (gold.Parts.Count > 1 || overlaps[0].Parts.Count > 1) ? "MULTIPART_DIFFERENCE"
                : overlaps.Length == 1 ? "PARTIAL" : nonMemberOverlap ? "WRONG_SEMANTIC_FUNCTION" : "NO_PROPOSAL";
            extent[bucket] = extent.GetValueOrDefault(bucket) + 1;
        }

        var exactFpList = headingCandidates.Where(candidate => !goldIdentitySet.Contains(candidate.Identity)).ToArray();
        var semanticFpList = headingCandidates.Where(candidate => !allGold[candidate.DocumentId].Any(gold => Overlaps(candidate.Parts, gold.Parts))).ToArray();
        foreach (var candidate in semanticFpList)
        {
            var verdicts = candidate.Parts.Select(part => review.GetValueOrDefault($"{candidate.DocumentId}:{part.Alias}"))
                .Where(verdict => verdict is not null).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(verdicts.Length <= 1, $"conflicting source review {candidate.Identity}");
            Assert.Equal("NON_HEADING", verdicts.SingleOrDefault() ?? "NON_HEADING");
        }

        var outOfScopeExact = headingCandidates.Count(candidate => goldIdentitySet.Contains(candidate.Identity) &&
            !selectedGold.Any(gold => gold.DocumentId == candidate.DocumentId && SameParts(candidate.Parts, gold.Parts)));
        var outOfScopeSemantic = headingCandidates.Count(candidate => allGold[candidate.DocumentId].Any(gold => Overlaps(candidate.Parts, gold.Parts)) &&
            !selectedGold.Any(gold => gold.DocumentId == candidate.DocumentId && Overlaps(candidate.Parts, gold.Parts)));
        return new ScoredArm(exactGold.Count, exactFpList.Length, selectedGold.Length - exactGold.Count,
            semanticGold.Count, semanticFpList.Length, selectedGold.Length - semanticGold.Count,
            outOfScopeExact, outOfScopeSemantic, exactFpList, semanticFpList, extent);
    }

    private static object ArmArtifact(IReadOnlyList<Prediction> candidates, ScoredArm score, bool freeMembership) => new
    {
        rawBoundOccurrenceUnits = candidates.Count,
        headingUnits = freeMembership ? candidates.Count : candidates.Count(item => item.IsHeadingMember),
        exact = Metric(score.ExactTp, score.ExactFp, score.ExactFn),
        semanticOccurrence = Metric(score.SemanticTp, score.SemanticFp, score.SemanticFn),
        outOfScopeExactMatches = score.OutOfScopeExactMatches, outOfScopeSemanticOverlaps = score.OutOfScopeSemanticOverlaps,
        extent = score.Extent,
        semanticFalsePositiveIdentities = score.SemanticFalsePositives.Select(item => item.Identity).Order(StringComparer.Ordinal).ToArray(),
        exactFalsePositiveIdentities = score.ExactFalsePositives.Select(item => item.Identity).Order(StringComparer.Ordinal).ToArray(),
    };

    private static IReadOnlySet<string> DetectedGoldIds(IReadOnlyList<Gold> gold, IEnumerable<Prediction> candidates) => gold
        .Where(item => candidates.Any(candidate => candidate.DocumentId == item.DocumentId && Overlaps(candidate.Parts, item.Parts)))
        .Select(item => $"{item.DocumentId}|{item.Identity}").ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> DetectedExactGoldIds(IReadOnlyList<Gold> gold, IEnumerable<Prediction> candidates) => gold
        .Where(item => candidates.Any(candidate => candidate.DocumentId == item.DocumentId && SameParts(candidate.Parts, item.Parts)))
        .Select(item => $"{item.DocumentId}|{item.Identity}").ToHashSet(StringComparer.Ordinal);

    private static object Reviews(IEnumerable<Prediction> candidates, IReadOnlyDictionary<string, string> review) => candidates.Select(item => new
    {
        item.Identity, sourceAliases = item.Parts.Select(part => part.Alias).Distinct(StringComparer.Ordinal).ToArray(),
        verdict = item.Parts.Select(part => review.GetValueOrDefault($"{item.DocumentId}:{part.Alias}")).FirstOrDefault(value => value is not null) ?? "NON_HEADING",
    }).ToArray();

    private static object Metric(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new { truePositive = tp, falsePositive = fp, falseNegative = fn,
            precision = Math.Round(precision, 4), recall = Math.Round(recall, 4),
            f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4) };
    }

    private static string Identity(string documentId, IReadOnlyList<Part> parts) =>
        $"{documentId}|" + string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static bool SameParts(IReadOnlyList<Part> left, IReadOnlyList<Part> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    private static bool Overlaps(IReadOnlyList<Part> left, IReadOnlyList<Part> right) =>
        left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
