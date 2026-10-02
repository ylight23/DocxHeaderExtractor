using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline paired P6N-B/P6M Gold score, excluding only the frozen length-truncated pack.</summary>
public sealed class V5P6NBBoundLocatorFull31GoldScoreTests
{
    private const string Root = "artifacts/v5-p6nb-full31-reasoning-lane";
    private const string P6MRoot = "artifacts/v5-p6m-p6l-full31-qualification";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(string Identity, string Document, string Text, IReadOnlyList<Part> Parts);
    private sealed record Prediction(string Identity, string Document, IReadOnlyList<Part> Parts, string[] Functions, string Text)
    { public bool Heading => Functions.Contains("DOCUMENT_IDENTITY", StringComparer.Ordinal) || Functions.Contains("STRUCTURAL_REGION", StringComparer.Ordinal); }
    private sealed record MetricRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);

    [Fact]
    public void Score_full31_against_frozen_Gold_only_after_hash_freeze()
    {
        var repo = TestRepository.Root();
        using var manifestDoc = Read($"{Root}/execution-manifest.v1.json");
        using var resultDoc = Read($"{Root}/result.v1.json");
        using var freezeDoc = Read($"{Root}/response-hash-freeze.v1.json");
        using var retryDoc = Read($"{Root}/pack-007-authorized-repeat.v1.json");
        using var retryFreezeDoc = Read($"{Root}/pack-007-repeat-hash-freeze.v1.json");
        using var p6mManifestDoc = Read($"{P6MRoot}/execution-manifest.v1.json");
        using var p6mResultDoc = Read($"{P6MRoot}/result.v1.json");
        using var p6mScoreDoc = Read($"{P6MRoot}/gold-occurrence-score.v1.json");
        var manifest = manifestDoc.RootElement; var result = resultDoc.RootElement; var freeze = freezeDoc.RootElement;
        var retry = retryDoc.RootElement; var retryFreeze = retryFreezeDoc.RootElement;
        var p6mManifest = p6mManifestDoc.RootElement; var p6mResult = p6mResultDoc.RootElement;
        Assert.Equal("v5-p6nb-full31-reasoning-lane-manifest-v1", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, manifest.GetProperty("rows").GetArrayLength());
        Assert.Equal(31, result.GetProperty("providerCalls").GetInt32());
        Assert.False(result.GetProperty("goldRead").GetBoolean());
        Assert.False(result.GetProperty("repair").GetBoolean()); Assert.False(result.GetProperty("fallback").GetBoolean());
        Assert.Equal(0, result.GetProperty("retry").GetInt32()); Assert.False(result.GetProperty("productionPromotion").GetBoolean());
        Assert.Equal("v5-p6nb-full31-response-hash-freeze-v1", freeze.GetProperty("schemaVersion").GetString());
        Assert.False(freeze.GetProperty("goldRead").GetBoolean());
        Assert.Equal(31, freeze.GetProperty("rows").GetArrayLength());
        Assert.Equal("v5-p6nb-full31-pack007-repeat-hash-freeze-v1", retryFreeze.GetProperty("schemaVersion").GetString());
        Assert.False(retryFreeze.GetProperty("goldRead").GetBoolean());
        Assert.Equal("v5-p6nb-full31-pack007-authorized-repeat-v1", retry.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, retry.GetProperty("providerCalls").GetInt32());
        Assert.False(retry.GetProperty("goldRead").GetBoolean());
        Assert.False(retry.GetProperty("repair").GetBoolean()); Assert.False(retry.GetProperty("fallback").GetBoolean());
        Assert.Equal("v5-p6m-gold-occurrence-score-v1", p6mScoreDoc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, p6mResult.GetProperty("rows").GetArrayLength());

        var manifestRows = Rows(manifest); var rows = Rows(result); var freezeRows = Rows(freeze);
        var p6mManifestRows = Rows(p6mManifest); var p6mRows = Rows(p6mResult);
        Assert.Equal(31, rows.Length); Assert.Equal(31, p6mRows.Length);
        Assert.Equal(30, rows.Count(row => S(row, "analysis", "classification") == "PARSER_BINDER_VALID"));
        Assert.Equal(1, rows.Count(row => S(row, "analysis", "classification") == "RESPONSE_OVERFLOW"));
        Assert.Equal(1, rows.Count(row => S(row, "finishReason") == "length"));
        Assert.All(rows, row => Assert.Equal(0, I(row, "retryCount")));

        // Reverify raw evidence hashes before the first Gold read; the truncated pack remains present but unscored.
        foreach (var row in rows)
        {
            var key = Key(row); var frozen = freezeRows.Single(item => Key(item) == key);
            var request = manifestRows.Single(item => Key(item) == key);
            Assert.Equal(S(request, "providerRequestHash"), S(row, "providerRequestHash"));
            Assert.Equal(S(request, "semanticRequestHash"), S(row, "semanticRequestHash"));
            Assert.Equal(S(request, "registryFingerprint"), S(row, "registryFingerprint"));
            Assert.Equal(S(frozen, "rawSseSha256"), Hash(S(row, "rawSse")));
            Assert.Equal(S(frozen, "rawResponseSha256"), Hash(S(row, "rawResponse")));
            Assert.Equal(S(row, "rawSseSha256"), S(frozen, "rawSseSha256"));
            Assert.Equal(S(row, "rawResponseSha256"), S(frozen, "rawResponseSha256"));
        }

        var retryRequest = manifestRows.Single(row => Key(row) == "SRC-089|7");
        var retryFreezeRow = retryFreeze.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal("SRC-089", S(retry, "documentId")); Assert.Equal(7, I(retry, "parentOrdinal"));
        Assert.Equal(S(retryRequest, "providerRequestHash"), S(retry, "providerRequestHash"));
        Assert.Equal("stop", S(retry, "finishReason"));
        Assert.Equal("PARSER_BINDER_VALID", S(retry, "analysis", "classification"));
        Assert.Equal(Hash(S(retry, "rawSse")), S(retry, "rawSseSha256"));
        Assert.Equal(Hash(S(retry, "rawResponse")), S(retry, "rawResponseSha256"));
        Assert.Equal(S(retryFreezeRow, "rawSseSha256"), S(retry, "rawSseSha256"));
        Assert.Equal(S(retryFreezeRow, "rawResponseSha256"), S(retry, "rawResponseSha256"));

        var scoredResponseRows = rows.Where(row => Key(row) != "SRC-089|7").Append(retry).ToArray();
        var validKeys = scoredResponseRows.Where(row => S(row, "analysis", "classification") == "PARSER_BINDER_VALID")
            .Select(Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(31, validKeys.Count);
        var sourcePdf = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["SRC-089"] = SourcePdfCorpus.Src089, ["SRC-095"] = SourcePdfCorpus.Src095 };
        var packsByDoc = sourcePdf.ToDictionary(pair => pair.Key, pair => V5PdfPreflightBuilder.BuildV3(
            TestRepository.Path(pair.Value), pair.Key, Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope), StringComparer.Ordinal);
        var atomsByDoc = sourcePdf.ToDictionary(pair => pair.Key, pair => V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pair.Value))
            .ToDictionary(atom => atom.Alias, StringComparer.Ordinal), StringComparer.Ordinal);

        // Hash/body parity and real parser+binder execution use the same frozen request and registry as the provider lane.
        var packRows = new List<(string Document, int Ordinal, string PackId, int Owned, string Registry, IReadOnlyList<Gold> Gold, Prediction[] P6M, Prediction[] P6NB)>();
        foreach (var request in manifestRows.OrderBy(row => S(row, "documentId"), StringComparer.Ordinal).ThenBy(row => I(row, "parentOrdinal")))
        {
            var key = Key(request); var document = S(request, "documentId"); var ordinal = I(request, "parentOrdinal");
            var p6mRow = p6mRows.Single(row => Key(row) == key); var p6mRequest = p6mManifestRows.Single(row => Key(row) == key);
            var pack = packsByDoc[document].Single(item => item.PackId == S(request, "packId"));
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => atomsByDoc[document][alias]).ToArray());
            Assert.Equal(S(request, "registryFingerprint"), registry.Fingerprint);
            Assert.Equal(S(p6mRequest, "registryFingerprint"), registry.Fingerprint);
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var p6mBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(canonical.SystemPrompt, canonical.UserMessage, pack.MaxCompletionTokens, Envelope);
            Assert.Equal(S(p6mRequest, "providerRequestHash"), p6mBody.Hash);
            Assert.Equal(S(p6mRequest, "semanticRequestHash"), canonical.UserMessageSha256);
            var freeRequest = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            var freeBody = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(freeRequest, pack.MaxCompletionTokens);
            Assert.Equal(S(request, "providerRequestHash"), freeBody.Hash);
            Assert.Equal(S(request, "semanticRequestHash"), freeRequest.UserMessageSha256);

            var p6mText = S(p6mRow, "rawResponse"); Assert.Equal(Hash(p6mText), S(p6mRow, "rawResponseSha256"));
            Assert.Equal("stop", S(p6mRow, "finishReason")); Assert.Equal(0, I(p6mRow, "retryCount"));
            using var p6mJson = JsonDocument.Parse(p6mText);
            var p6mParsed = registry.Parse(p6mJson.RootElement, I(p6mRow, "rawResponseBytes"), ResponseCap,
                Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(p6mParsed.Quarantined);
            var p6mPredictions = Decode(document, registry, p6mParsed.Response.Occurrences);

            if (!validKeys.Contains(key))
            {
                Assert.Equal("length", S(rows.Single(row => Key(row) == key), "finishReason"));
                packRows.Add((document, ordinal, S(request, "packId"), I(request, "ownedAtoms"), registry.Fingerprint,
                    Array.Empty<Gold>(), p6mPredictions, Array.Empty<Prediction>()));
                continue;
            }
            var p6nbRow = scoredResponseRows.Single(row => Key(row) == key);
            Assert.Equal("stop", S(p6nbRow, "finishReason")); Assert.Equal("PARSER_BINDER_VALID", S(p6nbRow, "analysis", "classification"));
            Assert.Equal(0, I(p6nbRow, "analysis", "quarantinedHeadings"));
            var response = S(p6nbRow, "rawResponse"); Assert.Equal(Hash(response), S(p6nbRow, "rawResponseSha256"));
            using var p6nbJson = JsonDocument.Parse(response);
            var p6nbParsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(p6nbJson.RootElement,
                I(p6nbRow, "rawResponseBytes"), ResponseCap, registry, Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(p6nbParsed.Quarantined);
            var p6nbPredictions = Decode(document, registry, p6nbParsed.Response.Occurrences, true);
            packRows.Add((document, ordinal, S(request, "packId"), I(request, "ownedAtoms"), registry.Fingerprint,
                Array.Empty<Gold>(), p6mPredictions, p6nbPredictions));
        }

        // Gold is opened only after the frozen raw/request hashes above were reverified.
        var goldByDoc = ReadGold(sourcePdf.Keys);
        var goldOwners = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < packRows.Count; index++)
        {
            var packRow = packRows[index];
            if (!validKeys.Contains($"{packRow.Document}|{packRow.Ordinal}")) continue;
            var pack = packsByDoc[packRow.Document].Single(item => item.PackId == packRow.PackId);
            var ownedGold = goldByDoc[packRow.Document].Where(item => pack.OwnedAliases.Contains(item.Parts[0].Alias, StringComparer.Ordinal)).ToArray();
            foreach (var gold in ownedGold) Assert.True(goldOwners.Add($"{gold.Document}|{gold.Identity}"), $"Gold primary identity owned twice: {gold.Identity}");
            packRows[index] = (packRow.Document, packRow.Ordinal, packRow.PackId, packRow.Owned, packRow.Registry, ownedGold,
                packRow.P6M, packRow.P6NB);
        }
        Assert.Equal(139, goldByDoc.Values.Sum(items => items.Count));
        var scoredRows = packRows.Where(pack => validKeys.Contains($"{pack.Document}|{pack.Ordinal}")).ToArray();
        var scoredGold = scoredRows.SelectMany(pack => pack.Gold).ToArray();
        var reviews = ReadSourceReview();
        var p6mPredictionsAll = scoredRows.SelectMany(pack => pack.P6M).ToArray();
        var p6nbPredictionsAll = scoredRows.SelectMany(pack => pack.P6NB).ToArray();
        var scoredGoldByDoc = scoredGold.GroupBy(item => item.Document, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Gold>)group.ToArray(), StringComparer.Ordinal);
        var allGoldByDoc = goldByDoc.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var p6m = Score(scoredGoldByDoc, allGoldByDoc, p6mPredictionsAll, reviews, false);
        var p6nb = Score(scoredGoldByDoc, allGoldByDoc, p6nbPredictionsAll, reviews, true);
        var perPack = packRows.Select(pack =>
        {
            var rowGold = pack.Gold;
            var comparable = validKeys.Contains($"{pack.Document}|{pack.Ordinal}");
            if (!comparable) return (object)new { documentId = pack.Document, parentOrdinal = pack.Ordinal, packId = pack.PackId,
                status = "NOT_EVALUABLE_RESPONSE_OVERFLOW", goldOwnedByPack = (int?)null, p6m = (object?)null, p6nb = (object?)null };
            var g = new Dictionary<string, IReadOnlyList<Gold>>(StringComparer.Ordinal) { [pack.Document] = rowGold };
            var p6mRowScore = Score(g, allGoldByDoc, pack.P6M, reviews, false);
            var p6nbRowScore = Score(g, allGoldByDoc, pack.P6NB, reviews, true);
            return (object)new { documentId = pack.Document, parentOrdinal = pack.Ordinal, packId = pack.PackId,
                status = "SCORED", goldOwnedByPack = (int?)rowGold.Count, p6m = Arm(pack.P6M, p6mRowScore, false), p6nb = Arm(pack.P6NB, p6nbRowScore, true) };
        }).ToArray();
        var p6mSemantic = Detected(scoredGold, p6mPredictionsAll.Where(item => item.Heading), false);
        var p6nbSemantic = Detected(scoredGold, p6nbPredictionsAll, false);
        var p6mExact = Detected(scoredGold, p6mPredictionsAll.Where(item => item.Heading), true);
        var p6nbExact = Detected(scoredGold, p6nbPredictionsAll, true);
        var p6mFpIds = p6m.SemanticFps.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        var p6nbFpIds = p6nb.SemanticFps.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        var p6nbOnlyGold = scoredGold.Where(item => p6nbSemantic.Contains($"{item.Document}|{item.Identity}") && !p6mSemantic.Contains($"{item.Document}|{item.Identity}")).ToArray();
        var p6mOnlyGold = scoredGold.Where(item => p6mSemantic.Contains($"{item.Document}|{item.Identity}") && !p6nbSemantic.Contains($"{item.Document}|{item.Identity}")).ToArray();
        var p6nbMissedGold = scoredGold.Where(item => !p6nbSemantic.Contains($"{item.Document}|{item.Identity}")).ToArray();
        var p6nbNewFalsePositives = p6nb.SemanticFps.Where(item => !p6mFpIds.Contains(item.Identity)).ToArray();
        Assert.Equal(13, p6nbOnlyGold.Length); Assert.Equal(1, p6mOnlyGold.Length);
        Assert.Equal(8, p6nbMissedGold.Length); Assert.Equal(16, p6nbNewFalsePositives.Length);

        FreezeArtifact.AssertJson(Root, "full31-gold-score-after-pack007-repeat.v1.json", new
        {
            schemaVersion = "v5-p6nb-full31-gold-score-after-pack007-repeat-v1",
            authority = new
            {
                goldSource = "current frozen canonical occurrence Gold; unchanged",
                goldScope = "Gold primary source aliases owned by parser/binder-valid P6N-B packs only; each scoped Gold identity has exactly one pack owner",
                pairedSubset = "same valid P05 pack identities for P6M and P6N-B; response-overflow pack excluded from both arms",
                full31Metric = "all 31 pack identities have a successful contract-valid response after the separately authorized one-time PACK_007 exact-body repeat; original length response remains immutable",
                semantic = "P6M heading membership is DOCUMENT_IDENTITY or STRUCTURAL_REGION; all bound P6N-B headings are free semantic proposals; TP by source-span overlap",
                exact = "document-scoped ordered source parts and exact UTF16 spans equal Gold parts",
                falsePositiveReview = "all scored semantic FPs checked against source-review verdicts; absent review item defaults NON_HEADING as prior cohort scorer",
                extent = "EXACT/PARTIAL/SPLIT/MULTIPART_DIFFERENCE/NO_PROPOSAL/WRONG_SEMANTIC_FUNCTION (P6M only)",
            },
            execution = new { providerCallsDuringScore = 0, totalP6NBPrimaryCalls = 31, separatelyAuthorizedExactRepeatCalls = 1, automaticRetries = 0, repair = false, fallback = false,
                goldRead = true, goldMutation = "NONE", sharedRuntimeChanged = false, productionPromotion = false,
                hashFreezeVerifiedBeforeGoldRead = true, p6nbValidResponses = 31, p6nbOverflowResponsesReplacedByAuthorizedRepeat = 1,
                repeatedPack = new { documentId = S(retry, "documentId"), parentOrdinal = I(retry, "parentOrdinal"), packId = S(retry, "packId"), originalLengthResponseRetained = true,
                    repeatProviderRequestHash = S(retry, "providerRequestHash"), repeatFinishReason = S(retry, "finishReason") } },
            coverage = new { cohortPacks = 31, pairedScoredPacks = scoredRows.Length, fullGoldOccurrences = 139,
                pairedGoldOccurrences = scoredGold.Length, overflowOwnedGoldNotEvaluable = goldByDoc.Values.Sum(items => items.Count) - scoredGold.Length },
            metrics = new
            {
                full31Status = "EVALUABLE_AFTER_AUTHORIZED_SINGLE_PACK_REPEAT",
                full31 = new { p6m = new { exact = ToMetric(p6m.Exact), semanticOccurrence = ToMetric(p6m.Semantic), headingUnits = p6mPredictionsAll.Count(item => item.Heading), extent = p6m.Extent },
                    p6nb = new { exact = ToMetric(p6nb.Exact), semanticOccurrence = ToMetric(p6nb.Semantic), headingUnits = p6nbPredictionsAll.Length, extent = p6nb.Extent },
                    pairedSemanticTruePositives = Compare(p6mSemantic, p6nbSemantic), pairedExactTruePositives = Compare(p6mExact, p6nbExact),
                    falsePositiveComparison = new { p6m = p6m.SemanticFps.Length, p6nb = p6nb.SemanticFps.Length,
                        p6nbNewVsP6m = p6nbNewFalsePositives.Length,
                        shared = p6nbFpIds.Intersect(p6mFpIds, StringComparer.Ordinal).Count(),
                        p6mOnly = p6mFpIds.Except(p6nbFpIds, StringComparer.Ordinal).Count() } },
            },
            pairedFalsePositiveReviews = Reviews(p6nb.SemanticFps, reviews),
            diagnosticContext = new
            {
                purpose = "compact human-review context for the frozen paired delta; this does not alter score authority or Gold",
                semanticBoundaryCues = new[]
                {
                    "A heading names or opens a structural region; an ordinary proposition remains body content even if subordinate material follows.",
                    "Navigation entries pointing elsewhere are not headings; a label opening a subgroup in the current document may be.",
                },
                recoveryInterpretation = "P6N-B-only hits and remaining misses are listed with exact frozen Gold text and source spans; examples can suggest front-matter, local-region, appendix, identity, or short-label patterns but are not new labels or Gold rules.",
                falsePositiveInterpretation = "New P6N-B false positives are listed with bound source text for review of body-proposition and navigation/index boundary errors.",
                goldDisposition = "NO_GOLD_CHANGE: model disagreement/context cues do not authorize editing frozen Gold.",
                productionDisposition = "NO_PROMOTION: this is offline interpretation only; runtime, prompt, and provider requests are unchanged.",
                p6nbOnlyGoldHits = p6nbOnlyGold.Select(DescribeGold).ToArray(),
                p6mOnlyGoldHits = p6mOnlyGold.Select(DescribeGold).ToArray(),
                p6nbRemainingGoldMisses = p6nbMissedGold.Select(DescribeGold).ToArray(),
                p6nbNewFalsePositives = p6nbNewFalsePositives.Select(DescribePrediction).ToArray(),
            },
            perPack = perPack,
        });
    }

    private static JsonDocument Read(string relative) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(relative.Replace('/', Path.DirectorySeparatorChar))));
    private static JsonElement[] Rows(JsonElement root) => root.GetProperty("rows").EnumerateArray().ToArray();
    private static string Key(JsonElement row) => $"{S(row, "documentId")}|{I(row, "parentOrdinal")}";
    private static string S(JsonElement row, string property) => row.GetProperty(property).GetString()!;
    private static string S(JsonElement row, string parent, string property) => row.GetProperty(parent).GetProperty(property).GetString()!;
    private static int I(JsonElement row, string property) => row.GetProperty(property).GetInt32();
    private static int I(JsonElement row, string parent, string property) => row.GetProperty(parent).GetProperty(property).GetInt32();

    private static IReadOnlyDictionary<string, IReadOnlyList<Gold>> ReadGold(IEnumerable<string> documents) => documents.ToDictionary(document => document, ReadGoldDocument, StringComparer.Ordinal);

    private static IReadOnlyList<Gold> ReadGoldDocument(string document)
    {
        CanonicalGoldRegistry.RequireCapability(document, GoldCapability.Occurrence);
        using var gold = CanonicalGoldRegistry.Resolve(document);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var parts = claim.GetProperty("boundParts").EnumerateArray().Select(part => new Part(part.GetProperty("sourceAlias").GetString()!,
                part.GetProperty("utf16Span").GetProperty("start").GetInt32(), part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray();
            return new Gold(claim.GetProperty("identity").GetString()!, document, claim.GetProperty("projectedText").GetString()!, parts);
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadSourceReview() => Src089SourceReviewTests.Items()
        .SelectMany(item => item.Parts.Select(part => (Key: $"SRC-089:{part.SourceAlias}", item.Verdict)))
        .Concat(Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => (Key: $"SRC-095:{part.SourceAlias}", item.Verdict))))
        .GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.Select(row => row.Verdict).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);

    private static Prediction[] Decode(string document, RequestLocalLocatorRegistry registry, IReadOnlyList<OccurrenceLocator> locators, bool free = false)
    {
        var map = new Dictionary<string, Prediction>(StringComparer.Ordinal);
        foreach (var locator in locators)
        {
            var endpoint = registry.Decode(locator); var parts = endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
            var id = Identity(document, parts); var functions = free ? Array.Empty<string>() : locator.Functions.Order(StringComparer.Ordinal).ToArray();
            if (map.TryGetValue(id, out var prior)) map[id] = prior with { Functions = prior.Functions.Union(functions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
            else map.Add(id, new Prediction(id, document, parts, functions, string.Join(" ", endpoint.Parts.Select(part => part.Text))));
        }
        return map.Values.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
    }

    private sealed record Scored(MetricRow Exact, MetricRow Semantic, Dictionary<string, int> Extent, Prediction[] SemanticFps);
    private static Scored Score(IReadOnlyDictionary<string, IReadOnlyList<Gold>> scope, IReadOnlyDictionary<string, IReadOnlyList<Gold>> all,
        IReadOnlyList<Prediction> candidates, IReadOnlyDictionary<string, string> reviews, bool free)
    {
        var heading = free ? candidates : candidates.Where(item => item.Heading).ToArray(); var gold = scope.Values.SelectMany(items => items).ToArray();
        var allIdentities = all.Values.SelectMany(items => items).Select(item => Identity(item.Document, item.Parts)).ToHashSet(StringComparer.Ordinal);
        var exactTp = 0; var semanticTp = 0; var extent = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var same = heading.Where(candidate => candidate.Document == item.Document).ToArray();
            var exact = same.Any(candidate => Same(candidate.Parts, item.Parts)); var overlap = same.Where(candidate => Overlap(candidate.Parts, item.Parts)).ToArray();
            if (exact) exactTp++; if (overlap.Length > 0) semanticTp++;
            var wrongFunction = !free && candidates.Any(candidate => candidate.Document == item.Document && !candidate.Heading && Overlap(candidate.Parts, item.Parts));
            var bucket = exact ? "EXACT" : overlap.Length > 1 ? "SPLIT" : overlap.Length == 1 && (item.Parts.Count > 1 || overlap[0].Parts.Count > 1)
                ? "MULTIPART_DIFFERENCE" : overlap.Length == 1 ? "PARTIAL" : wrongFunction ? "WRONG_SEMANTIC_FUNCTION" : "NO_PROPOSAL";
            extent[bucket] = extent.GetValueOrDefault(bucket) + 1;
        }
        var exactFp = heading.Count(candidate => !allIdentities.Contains(candidate.Identity));
        var semanticFpRows = heading.Where(candidate => !all[candidate.Document].Any(item => Overlap(candidate.Parts, item.Parts))).ToArray();
        foreach (var candidate in semanticFpRows)
        {
            var verdicts = candidate.Parts.Select(part => reviews.GetValueOrDefault($"{candidate.Document}:{part.Alias}"))
                .Where(verdict => verdict is not null).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(verdicts.Length <= 1, $"Conflicting source-review decisions for {candidate.Identity}");
            Assert.Equal("NON_HEADING", verdicts.SingleOrDefault() ?? "NON_HEADING");
        }
        return new Scored(Metric(exactTp, exactFp, gold.Length - exactTp), Metric(semanticTp, semanticFpRows.Length, gold.Length - semanticTp), extent, semanticFpRows);
    }

    private static MetricRow Metric(int tp, int fp, int fn)
    {
        var p = tp + fp == 0 ? 0 : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return new MetricRow(tp, fp, fn, Math.Round(p, 4), Math.Round(r, 4), Math.Round(p + r == 0 ? 0 : 2 * p * r / (p + r), 4));
    }
    private static MetricRow ToMetric(MetricRow row) => row;
    private static object Arm(IReadOnlyList<Prediction> predictions, Scored score, bool free) => new
    { rawBoundOccurrenceUnits = predictions.Count, headingUnits = free ? predictions.Count : predictions.Count(item => item.Heading), exact = score.Exact,
        semanticOccurrence = score.Semantic, extent = score.Extent };
    private static IReadOnlySet<string> Detected(IReadOnlyList<Gold> gold, IEnumerable<Prediction> predictions, bool exact) => gold
        .Where(item => predictions.Any(candidate => candidate.Document == item.Document && (exact ? Same(candidate.Parts, item.Parts) : Overlap(candidate.Parts, item.Parts))))
        .Select(item => $"{item.Document}|{item.Identity}").ToHashSet(StringComparer.Ordinal);
    private static object Compare(IReadOnlySet<string> left, IReadOnlySet<string> right)
    { var both = left.Intersect(right, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(); var a = left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(); var b = right.Except(left, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(); return new { both = both.Length, p6mOnly = a.Length, p6nbOnly = b.Length, p6nbLostTruePositives = a.Length, bothGold = both, p6mOnlyGold = a, p6nbOnlyGold = b }; }
    private static object Reviews(IEnumerable<Prediction> predictions, IReadOnlyDictionary<string, string> reviews) => predictions.Select(item => new
    { item.Identity, sourceAliases = item.Parts.Select(part => part.Alias).Distinct(StringComparer.Ordinal).ToArray(), verdict = "NON_HEADING" }).ToArray();
    private static object DescribeGold(Gold item) => new { documentId = item.Document, goldIdentity = item.Identity, text = item.Text,
        parts = item.Parts.Select(part => new { sourceAlias = part.Alias, start = part.Start, end = part.End }).ToArray() };
    private static object DescribePrediction(Prediction item) => new { documentId = item.Document, predictionIdentity = item.Identity, text = item.Text,
        parts = item.Parts.Select(part => new { sourceAlias = part.Alias, start = part.Start, end = part.End }).ToArray() };
    private static string Identity(string document, IReadOnlyList<Part> parts) => $"{document}|" + string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static bool Same(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
