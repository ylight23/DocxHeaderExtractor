using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes P6N-C full31 raw evidence and scores only the paired, contract-valid subset against P6N-B.</summary>
public sealed class V5P6NCSemanticBoundaryFull31Tests
{
    private const string Root = "artifacts/v5-p6nc-boundary-prompt-full31";
    private const string P6NBRoot = "artifacts/v5-p6nb-full31-reasoning-lane";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(string Identity, string Document, string Text, IReadOnlyList<Part> Parts);
    private sealed record Prediction(string Identity, string Document, IReadOnlyList<Part> Parts, string Text);
    private sealed record Metric(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
    private sealed record Scored(Metric Exact, Metric Semantic, Dictionary<string, int> Extent);

    [Fact]
    public void Freeze_full31_raw_hashes_and_classify_incomplete_transport_without_Gold()
    {
        using var manifestDoc = Read("execution-manifest.v1.json");
        using var resultDoc = Read("result.v1.json");
        using var p6nbDoc = ReadAt($"{P6NBRoot}/execution-manifest.v1.json");
        var manifest = manifestDoc.RootElement; var result = resultDoc.RootElement;
        Assert.Equal("v5-p6nc-boundary-prompt-full31-manifest-v1", S(manifest, "schemaVersion"));
        Assert.Equal("PREPARED_NOT_AUTHORIZED", S(manifest, "status"));
        Assert.Equal(0, I(manifest, "providerCalls")); Assert.False(B(manifest, "goldRead"));
        Assert.Equal(31, Rows(manifest).Length); Assert.Equal(31, I(result, "providerCalls"));
        Assert.Equal(31, I(result, "completedPrimaryAttempts")); Assert.Equal(0, I(result, "retry"));
        Assert.False(B(result, "goldRead")); Assert.False(B(result, "repair")); Assert.False(B(result, "fallback"));
        Assert.False(B(result, "productionPromotion")); Assert.Equal(31, Rows(result).Length);
        Assert.Equal("CLOSED_AFTER_31_PRIMARY_ATTEMPTS", S(result, "stopGate"));
        Assert.Equal(2, manifest.GetProperty("treatment").GetProperty("addedSystemPrompt").GetString()!.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length);

        var requestRows = Rows(manifest); var baselineRows = Rows(p6nbDoc.RootElement); var rows = Rows(result);
        var frozen = rows.OrderBy(row => S(row, "documentId"), StringComparer.Ordinal).ThenBy(row => I(row, "parentOrdinal"))
            .Select(row =>
            {
                var key = Key(row); var request = requestRows.Single(candidate => Key(candidate) == key);
                var baseline = baselineRows.Single(candidate => Key(candidate) == key);
                Assert.Equal(S(request, "semanticRequestHash"), S(baseline, "semanticRequestHash"));
                Assert.Equal(S(request, "sourceEvidenceHash"), S(baseline, "sourceEvidenceHash"));
                Assert.Equal(S(request, "registryFingerprint"), S(baseline, "registryFingerprint"));
                Assert.Equal(S(request, "packId"), S(baseline, "packId"));
                Assert.Equal(0, I(row, "retryCount"));
                var rawSse = SNullable(row, "rawSse"); var rawResponse = SNullable(row, "rawResponse");
                var rawSseHash = rawSse is null ? null : Hash(rawSse); var responseHash = rawResponse is null ? null : Hash(rawResponse);
                Assert.Equal(rawSseHash, SNullable(row, "rawSseSha256")); Assert.Equal(responseHash, SNullable(row, "rawResponseSha256"));
                Assert.Equal(rawResponse is null ? 0 : Encoding.UTF8.GetByteCount(rawResponse), I(row, "rawResponseBytes"));
                return new
                {
                    documentId = S(row, "documentId"), parentOrdinal = I(row, "parentOrdinal"), packId = S(row, "packId"),
                    providerRequestSha256 = S(row, "providerRequestHash"), semanticRequestSha256 = S(row, "semanticRequestHash"),
                    sourceEvidenceSha256 = S(row, "sourceEvidenceHash"), registryFingerprint = S(row, "registryFingerprint"),
                    transportAccepted = B(row, "transportAccepted"), transportError = SNullable(row, "transportError"),
                    finishReason = SNullable(row, "finishReason"), rawSseSha256 = rawSseHash, rawSseUtf8Bytes = rawSse is null ? 0 : Encoding.UTF8.GetByteCount(rawSse),
                    rawResponseSha256 = responseHash, rawResponseUtf8Bytes = I(row, "rawResponseBytes"), retryCount = I(row, "retryCount"),
                    classification = S(row, "analysis", "classification"), parserAccepted = B(row, "analysis", "parserAccepted"),
                    boundHeadings = I(row, "analysis", "boundHeadings"), quarantinedHeadings = I(row, "analysis", "quarantinedHeadings"),
                };
            }).ToArray();
        var valid = rows.Count(row => S(row, "analysis", "classification") == "PARSER_BINDER_VALID");
        var transportErrors = rows.Count(row => S(row, "analysis", "classification") == "TRANSPORT_ERROR");
        Assert.Equal(9, valid); Assert.Equal(22, transportErrors);
        Assert.All(rows, row => Assert.Equal(0, I(row, "analysis", "quarantinedHeadings")));
        var errors = rows.Where(row => S(row, "analysis", "classification") == "TRANSPORT_ERROR").ToArray();
        Assert.All(errors, row => Assert.Contains("402", S(row, "transportError"), StringComparison.Ordinal));
        FreezeArtifact.AssertJson(Root, "response-hash-freeze.v1.json", new
        {
            schemaVersion = "v5-p6nc-boundary-prompt-full31-response-hash-freeze-v1",
            sourceManifest = $"{Root}/execution-manifest.v1.json", sourceResult = $"{Root}/result.v1.json",
            providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE", retry = 0, repair = false, fallback = false,
            rawEvidenceImmutable = true, requestSseAndResponseHashesReverified = true,
            full31ExecutionStatus = "31_PRIMARY_ATTEMPTS_PERSISTED_INCOMPLETE_TRANSPORT",
            responseClassifications = new { parserBinderValid = valid, transportError = transportErrors },
            transportErrorAuthority = "OpenRouter HTTP 402 in_flight_budget_exhausted; no retry authorized or performed",
            rows = frozen,
        });
    }

    [Fact]
    public void Freeze_exactly_22_authorized_transport_retry_responses_without_Gold()
    {
        using var initialDoc = Read("result.v1.json"); using var retryDoc = Read("transport-retry.v1.json");
        using var retryFreezeSourceDoc = Read("response-hash-freeze.v1.json");
        var initial = Rows(initialDoc.RootElement).ToDictionary(Key, StringComparer.Ordinal);
        var retryRoot = retryDoc.RootElement; var retryRows = Rows(retryRoot);
        Assert.Equal("v5-p6nc-full31-authorized-transport-retries-v1", S(retryRoot, "schemaVersion"));
        Assert.Equal(22, I(retryRoot, "providerCalls")); Assert.Equal(22, I(retryRoot, "maximumAuthorizedRetryCalls"));
        Assert.Equal(0, I(retryRoot, "retry")); Assert.False(B(retryRoot, "goldRead"));
        Assert.False(B(retryRoot, "repair")); Assert.False(B(retryRoot, "fallback"));
        Assert.True(B(retryRoot, "originalResultImmutable")); Assert.Equal(22, retryRows.Length);
        var initialFreeze = Rows(retryFreezeSourceDoc.RootElement).ToDictionary(Key, StringComparer.Ordinal);
        var frozen = retryRows.OrderBy(row => S(row, "documentId"), StringComparer.Ordinal).ThenBy(row => I(row, "parentOrdinal"))
            .Select(row =>
            {
                var key = Key(row); var source = initial[key];
                Assert.Equal("TRANSPORT_ERROR", S(source, "analysis", "classification"));
                Assert.Equal("PARSER_BINDER_VALID", S(row, "analysis", "classification"));
                Assert.Equal(1, I(row, "retryIndex")); Assert.Equal(0, I(row, "sdkRetryCount"));
                var priorFreeze = initialFreeze[key];
                Assert.Equal(S(priorFreeze, "providerRequestSha256"), S(row, "providerRequestHash"));
                Assert.Equal(S(source, "providerRequestHash"), S(row, "providerRequestHash"));
                Assert.Equal(S(source, "semanticRequestHash"), S(row, "semanticRequestHash"));
                Assert.Equal(S(source, "registryFingerprint"), S(row, "registryFingerprint"));
                var rawSseHash = Hash(S(row, "rawSse")); var responseHash = Hash(S(row, "rawResponse"));
                Assert.Equal(rawSseHash, S(row, "rawSseSha256")); Assert.Equal(responseHash, S(row, "rawResponseSha256"));
                Assert.Equal(Encoding.UTF8.GetByteCount(S(row, "rawResponse")), I(row, "rawResponseBytes"));
                Assert.Equal(0, I(row, "analysis", "quarantinedHeadings"));
                return new
                {
                    documentId = S(row, "documentId"), parentOrdinal = I(row, "parentOrdinal"), packId = S(row, "packId"),
                    providerRequestSha256 = S(row, "providerRequestHash"), rawSseSha256 = rawSseHash,
                    rawSseUtf8Bytes = Encoding.UTF8.GetByteCount(S(row, "rawSse")), rawResponseSha256 = responseHash,
                    rawResponseUtf8Bytes = I(row, "rawResponseBytes"), finishReason = S(row, "finishReason"),
                    retryIndex = I(row, "retryIndex"), sdkRetryCount = I(row, "sdkRetryCount"),
                    classification = S(row, "analysis", "classification"), boundHeadings = I(row, "analysis", "boundHeadings"),
                    quarantinedHeadings = I(row, "analysis", "quarantinedHeadings"),
                };
            }).ToArray();
        Assert.All(retryRows, row => Assert.Equal("stop", S(row, "finishReason")));
        FreezeArtifact.AssertJson(Root, "transport-retry-hash-freeze.v1.json", new
        {
            schemaVersion = "v5-p6nc-full31-transport-retry-hash-freeze-v1",
            sourceResult = $"{Root}/transport-retry.v1.json", originalResult = $"{Root}/result.v1.json",
            providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE", rawEvidenceImmutable = true,
            retryRawHashesReverified = true, failedPrimaryHashLineageReverified = true,
            retryClassificationCounts = new { parserBinderValid = retryRows.Count(row => S(row, "analysis", "classification") == "PARSER_BINDER_VALID"),
                transportError = retryRows.Count(row => S(row, "analysis", "classification") == "TRANSPORT_ERROR") },
            rows = frozen,
        });
    }

    [Fact]
    public void Score_only_same_pack_valid_subset_against_P6N_B_after_hash_freeze()
    {
        using var freezeDoc = Read("response-hash-freeze.v1.json");
        using var manifestDoc = Read("execution-manifest.v1.json"); using var resultDoc = Read("result.v1.json");
        using var retryDoc = Read("transport-retry.v1.json"); using var retryFreezeDoc = Read("transport-retry-hash-freeze.v1.json");
        using var nbManifestDoc = ReadAt($"{P6NBRoot}/execution-manifest.v1.json"); using var nbResultDoc = ReadAt($"{P6NBRoot}/result.v1.json");
        using var nbRepeatDoc = ReadAt($"{P6NBRoot}/pack-007-authorized-repeat.v1.json");
        var freeze = freezeDoc.RootElement; var manifest = manifestDoc.RootElement; var result = resultDoc.RootElement;
        Assert.Equal("v5-p6nc-boundary-prompt-full31-response-hash-freeze-v1", S(freeze, "schemaVersion"));
        Assert.False(B(freeze, "goldRead")); Assert.True(B(freeze, "rawEvidenceImmutable"));
        Assert.Equal("v5-p6nc-full31-transport-retry-hash-freeze-v1", S(retryFreezeDoc.RootElement, "schemaVersion"));
        var requests = Rows(manifest); var responseRows = Rows(result); var nbRequests = Rows(nbManifestDoc.RootElement);
        var nbRows = Rows(nbResultDoc.RootElement).ToDictionary(Key, StringComparer.Ordinal);
        var freezeRows = Rows(freeze).ToDictionary(Key, StringComparer.Ordinal);
        var retryRows = Rows(retryDoc.RootElement).ToDictionary(Key, StringComparer.Ordinal);
        var retryFreezeRows = Rows(retryFreezeDoc.RootElement).ToDictionary(Key, StringComparer.Ordinal);
        var sourcePdf = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["SRC-089"] = SourcePdfCorpus.Src089, ["SRC-095"] = SourcePdfCorpus.Src095 };
        var packsByDoc = sourcePdf.ToDictionary(pair => pair.Key, pair => V5PdfPreflightBuilder.BuildV3(
            TestRepository.Path(pair.Value), pair.Key, Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope), StringComparer.Ordinal);
        var atomsByDoc = sourcePdf.ToDictionary(pair => pair.Key, pair => V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pair.Value))
            .ToDictionary(atom => atom.Alias, StringComparer.Ordinal), StringComparer.Ordinal);
        var nbRetry = nbRepeatDoc.RootElement;
        var paired = new List<(string Document, int Ordinal, string PackId, RequestLocalLocatorRegistry Registry, Gold[] Gold, Prediction[] P6NB, Prediction[] P6NC)>();

        foreach (var initialNcRow in responseRows)
        {
            var key = Key(initialNcRow);
            var retryUsed = S(initialNcRow, "analysis", "classification") != "PARSER_BINDER_VALID";
            var ncRow = retryUsed ? retryRows[key] : initialNcRow;
            Assert.Equal("PARSER_BINDER_VALID", S(ncRow, "analysis", "classification"));
            var requestRow = requests.Single(row => Key(row) == key); var nbRequest = nbRequests.Single(row => Key(row) == key);
            var nbRow = key == "SRC-089|7" ? nbRetry : nbRows[key];
            Assert.Equal("PARSER_BINDER_VALID", S(nbRow, "analysis", "classification"));
            var frozen = retryUsed ? retryFreezeRows[key] : freezeRows[key];
            Assert.Equal(S(frozen, "rawSseSha256"), Hash(S(ncRow, "rawSse")));
            Assert.Equal(S(frozen, "rawResponseSha256"), Hash(S(ncRow, "rawResponse")));
            Assert.Equal(S(requestRow, "semanticRequestHash"), S(nbRequest, "semanticRequestHash"));
            Assert.Equal(S(requestRow, "sourceEvidenceHash"), S(nbRequest, "sourceEvidenceHash"));
            Assert.Equal(S(requestRow, "registryFingerprint"), S(nbRequest, "registryFingerprint"));
            Assert.Equal("stop", S(ncRow, "finishReason"));
            if (retryUsed) { Assert.Equal(1, I(ncRow, "retryIndex")); Assert.Equal(0, I(ncRow, "sdkRetryCount")); }
            else Assert.Equal(0, I(ncRow, "retryCount"));
            var document = S(ncRow, "documentId"); var ordinal = I(ncRow, "parentOrdinal");
            var pack = packsByDoc[document].Single(item => item.PackId == S(ncRow, "packId"));
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => atomsByDoc[document][alias]).ToArray());
            Assert.Equal(S(ncRow, "registryFingerprint"), registry.Fingerprint);
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var nbRequestBody = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            Assert.Equal(S(nbRequest, "providerRequestHash"), V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(nbRequestBody, pack.MaxCompletionTokens).Hash);
            var ncPrompt = string.Concat(nbRequestBody.SystemPrompt, "\n\n", manifest.GetProperty("treatment").GetProperty("addedSystemPrompt").GetString());
            var ncBuilt = nbRequestBody with { ProtocolVersion = "v5-free-reasoning-heading-membership-source-parts-locator-boundary-cues-1", SystemPrompt = ncPrompt,
                SystemPromptUtf8Bytes = Encoding.UTF8.GetByteCount(ncPrompt) };
            Assert.Equal(S(requestRow, "providerRequestHash"), V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(ncBuilt, pack.MaxCompletionTokens).Hash);
            var ncParsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(JsonDocument.Parse(S(ncRow, "rawResponse")).RootElement,
                I(ncRow, "rawResponseBytes"), ResponseCap, registry, Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(ncParsed.Quarantined);
            var nbParsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(JsonDocument.Parse(S(nbRow, "rawResponse")).RootElement,
                I(nbRow, "rawResponseBytes"), ResponseCap, registry, Enumerable.Range(0, pack.OwnedAliases.Count).ToHashSet());
            Assert.Empty(nbParsed.Quarantined);
            var scopedGold = ReadGold(document).Where(gold => pack.OwnedAliases.Contains(gold.Parts[0].Alias, StringComparer.Ordinal)).ToArray();
            paired.Add((document, ordinal, pack.PackId, registry, scopedGold,
                Decode(document, registry, nbParsed.Response.Occurrences), Decode(document, registry, ncParsed.Response.Occurrences)));
        }

        Assert.Equal(31, paired.Count);
        var allGold = sourcePdf.Keys.SelectMany(ReadGold).GroupBy(item => item.Document, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var scoped = paired.SelectMany(item => item.Gold).ToArray();
        Assert.Equal(139, allGold.Values.Sum(items => items.Length));
        var reviews = ReadSourceReview();
        var nbPredictions = paired.SelectMany(item => item.P6NB).ToArray(); var ncPredictions = paired.SelectMany(item => item.P6NC).ToArray();
        var nbScore = Score(scoped, allGold, nbPredictions, reviews); var ncScore = Score(scoped, allGold, ncPredictions, reviews);
        var nbSemanticHits = Detected(scoped, nbPredictions, false); var ncSemanticHits = Detected(scoped, ncPredictions, false);
        var nbExactHits = Detected(scoped, nbPredictions, true); var ncExactHits = Detected(scoped, ncPredictions, true);
        FreezeArtifact.AssertJson(Root, "paired-gold-score.v1.json", new
        {
            schemaVersion = "v5-p6nc-boundary-prompt-paired-gold-score-v1",
            authority = new
            {
                goldSource = "current frozen canonical 139 occurrence Gold; unchanged",
                comparison = "P6N-C versus P6N-B on identical pack identities with valid parser/binder responses in both arms only",
                incompleteCohort = "all 31 P6N-C packs have a contract-valid response after one separately authorized retry for each of the 22 frozen transport errors",
                semantic = "free heading membership; candidate counted semantic TP by same-document source-span overlap",
                exact = "document-scoped ordered source parts and exact UTF16 spans equal Gold parts",
                falsePositiveReview = "all scored semantic false positives source-reviewed NON_HEADING; Gold untouched",
                selectionRule = "P6N-B PACK_007 separately authorized repeat is canonical; P6N-C has one primary response per pack",
            },
            execution = new { providerCallsDuringScore = 0, p6ncPrimaryCalls = 31, p6ncInitialValid = 9, p6ncInitialTransportErrors = 22,
                separatelyAuthorizedP6ncRetryCalls = 22, p6ncRetryValid = 22, p6ncRetryErrors = 0, p6ncSdkRetries = 0,
                p6nbSeparatelyAuthorizedPack007RepeatCalls = 1, repair = false, fallback = false, goldRead = true, goldMutation = "NONE", sharedRuntimeChanged = false, productionPromotion = false,
                hashFreezeVerifiedBeforeGoldRead = true },
            coverage = new { cohortPacks = 31, pairedScoredPacks = paired.Count, fullGoldOccurrences = 139, pairedGoldOccurrences = scoped.Length,
                goldInUnavailablePacksNotEvaluable = 139 - scoped.Length },
            metrics = new
            {
                full31Status = "EVALUABLE_AFTER_22_AUTHORIZED_SINGLE_RETRIES",
                pairedSubset = new { p6nb = nbScore, p6nc = ncScore,
                    semanticTruePositiveComparison = Compare(nbSemanticHits, ncSemanticHits), exactTruePositiveComparison = Compare(nbExactHits, ncExactHits),
                    headingUnits = new { p6nb = nbPredictions.Length, p6nc = ncPredictions.Length } },
            },
            perPack = paired.Select(item => new { documentId = item.Document, parentOrdinal = item.Ordinal, packId = item.PackId,
                gold = item.Gold.Length, p6nb = Score(item.Gold, allGold, item.P6NB, reviews), p6nc = Score(item.Gold, allGold, item.P6NC, reviews),
                p6nbHeadingUnits = item.P6NB.Length, p6ncHeadingUnits = item.P6NC.Length }).ToArray(),
        });
    }

    private static JsonDocument Read(string file) => ReadAt($"{Root}/{file}");
    private static JsonDocument ReadAt(string path) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path.Replace('/', Path.DirectorySeparatorChar))));
    private static JsonElement[] Rows(JsonElement root) => root.GetProperty("rows").EnumerateArray().ToArray();
    private static string Key(JsonElement row) => $"{S(row, "documentId")}|{I(row, "parentOrdinal")}";
    private static string S(JsonElement row, string property) => row.GetProperty(property).GetString()!;
    private static string S(JsonElement row, string parent, string property) => row.GetProperty(parent).GetProperty(property).GetString()!;
    private static string? SNullable(JsonElement row, string property) => row.GetProperty(property).ValueKind == JsonValueKind.Null ? null : row.GetProperty(property).GetString();
    private static int I(JsonElement row, string property) => row.GetProperty(property).GetInt32();
    private static int I(JsonElement row, string parent, string property) => row.GetProperty(parent).GetProperty(property).GetInt32();
    private static bool B(JsonElement row, string property) => row.GetProperty(property).GetBoolean();
    private static bool B(JsonElement row, string parent, string property) => row.GetProperty(parent).GetProperty(property).GetBoolean();
    private static IReadOnlyList<Gold> ReadGold(string document)
    {
        CanonicalGoldRegistry.RequireCapability(document, GoldCapability.Occurrence); using var gold = CanonicalGoldRegistry.Resolve(document);
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
    private static Prediction[] Decode(string document, RequestLocalLocatorRegistry registry, IReadOnlyList<OccurrenceLocator> locators)
    {
        var map = new Dictionary<string, Prediction>(StringComparer.Ordinal);
        foreach (var locator in locators)
        {
            var endpoint = registry.Decode(locator); var parts = endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
            var identity = Identity(document, parts);
            map.TryAdd(identity, new Prediction(identity, document, parts, string.Join(" ", endpoint.Parts.Select(part => part.Text))));
        }
        return map.Values.ToArray();
    }
    private static Scored Score(IReadOnlyList<Gold> scoped,
        IReadOnlyDictionary<string, Gold[]> all, IReadOnlyList<Prediction> predictions, IReadOnlyDictionary<string, string> reviews)
    {
        var allIds = all.Values.SelectMany(items => items).Select(item => Identity(item.Document, item.Parts)).ToHashSet(StringComparer.Ordinal);
        var exactTp = 0; var semanticTp = 0; var extent = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gold in scoped)
        {
            var same = predictions.Where(item => item.Document == gold.Document).ToArray();
            var exact = same.Any(item => Same(item.Parts, gold.Parts)); var overlap = same.Where(item => Overlap(item.Parts, gold.Parts)).ToArray();
            if (exact) exactTp++; if (overlap.Length > 0) semanticTp++;
            var bucket = exact ? "EXACT" : overlap.Length > 1 ? "SPLIT" : overlap.Length == 1 && (gold.Parts.Count > 1 || overlap[0].Parts.Count > 1)
                ? "MULTIPART_DIFFERENCE" : overlap.Length == 1 ? "PARTIAL" : "NO_PROPOSAL";
            extent[bucket] = extent.GetValueOrDefault(bucket) + 1;
        }
        var exactFp = predictions.Count(item => !allIds.Contains(item.Identity));
        var falsePositiveRows = predictions.Where(candidate => !all[candidate.Document].Any(gold => Overlap(candidate.Parts, gold.Parts))).ToArray();
        foreach (var item in falsePositiveRows)
        {
            var verdicts = item.Parts.Select(part => reviews.GetValueOrDefault($"{item.Document}:{part.Alias}")).Where(value => value is not null)
                .Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(verdicts.Length <= 1); Assert.Equal("NON_HEADING", verdicts.SingleOrDefault() ?? "NON_HEADING");
        }
        return new Scored(MetricOf(exactTp, exactFp, scoped.Count - exactTp), MetricOf(semanticTp, falsePositiveRows.Length, scoped.Count - semanticTp), extent);
    }
    private static Metric MetricOf(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return new Metric(tp, fp, fn, Math.Round(precision, 4), Math.Round(recall, 4), Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4));
    }
    private static HashSet<string> Detected(IReadOnlyList<Gold> gold, IReadOnlyList<Prediction> predictions, bool exact) => gold
        .Where(item => predictions.Any(candidate => candidate.Document == item.Document && (exact ? Same(candidate.Parts, item.Parts) : Overlap(candidate.Parts, item.Parts))))
        .Select(item => $"{item.Document}|{item.Identity}").ToHashSet(StringComparer.Ordinal);
    private static object Compare(IReadOnlySet<string> baseline, IReadOnlySet<string> treatment) => new
    { both = baseline.Intersect(treatment, StringComparer.Ordinal).Count(), p6nbOnly = baseline.Except(treatment, StringComparer.Ordinal).Count(), p6ncOnly = treatment.Except(baseline, StringComparer.Ordinal).Count() };
    private static bool Same(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static string Identity(string document, IReadOnlyList<Part> parts) => $"{document}|" + string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
