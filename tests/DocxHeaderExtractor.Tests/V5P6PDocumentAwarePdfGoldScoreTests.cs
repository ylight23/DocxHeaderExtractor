using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>Hash-gated offline P6P score with the unchanged GENERIC_EXACT_SCORER_V1 authority.</summary>
public sealed class V5P6PDocumentAwarePdfGoldScoreTests
{
    private const string Root = "artifacts/v5-p6p-document-aware-pdf";
    private const string BaselinePath = "eval/a99-closed-loop/production-rebaseline-v1/production-rebaseline-score.v1.json";
    private const string P6NBScorePath = "artifacts/v5-p6nb-full31-reasoning-lane/full31-gold-score-after-pack007-repeat.v1.json";
    private const string P6NCScorePath = "artifacts/v5-p6nc-boundary-prompt-full31/paired-gold-score.v1.json";
    private const string RetryFile = "pack-retry-src089-pack002.v1.json";
    private static readonly (string Id, string Pdf)[] Documents =
        [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();

    private sealed record Prediction(string Identity, ExactScorer.Span[] Spans, string Text);

    [Fact]
    public async Task Score_P6P_only_after_manifest_and_all_raw_response_hashes_are_verified()
    {
        var repo = TestRepository.Root();
        using var manifestDoc = Read("execution-manifest.v1.json");
        using var resultDoc = Read("result.v1.json");
        using var freezeDoc = Read("response-hash-freeze.v1.json");
        var manifest = manifestDoc.RootElement;
        var result = resultDoc.RootElement;
        var freeze = freezeDoc.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest.GetProperty("status").GetString());
        Assert.Equal(0, manifest.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, manifest.GetProperty("rows").GetArrayLength());
        Assert.Equal(31, result.GetProperty("logicalProviderCalls").GetInt32());
        Assert.False(result.GetProperty("goldRead").GetBoolean());
        Assert.Equal("v5-p6p-document-aware-pdf-response-hash-freeze-v1", freeze.GetProperty("schemaVersion").GetString());
        Assert.True(freeze.GetProperty("verifiedBeforeGoldRead").GetBoolean());
        Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json"))), freeze.GetProperty("resultFileSha256").GetString());
        Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/execution-manifest.v1.json"))), freeze.GetProperty("manifestFileSha256").GetString());

        var requestRows = Rows(manifest).ToDictionary(Key, StringComparer.Ordinal);
        var frozenRows = Rows(freeze).ToDictionary(Key, StringComparer.Ordinal);
        var resultRows = Rows(result);
        Assert.Equal(31, resultRows.Length);
        Assert.Equal(31, frozenRows.Count);
        Assert.All(resultRows, row =>
        {
            var key = Key(row);
            var request = requestRows[key];
            var frozen = frozenRows[key];
            Assert.Equal("stop", S(row, "finishReason"));
            Assert.True(B(row, "transportAccepted"));
            Assert.True(B(row, "parserAccepted"));
            Assert.Equal(S(request, "providerRequestHash"), S(row, "providerRequestHash"));
            Assert.Equal(S(request, "userMessageSha256"), S(row, "userMessageSha256"));
            Assert.Equal(S(request, "locatorRegistryFingerprint"), S(row, "locatorRegistryFingerprint"));
            Assert.Equal(Hash(S(row, "rawContent")), S(row, "rawContentSha256"));
            Assert.Equal(Hash(S(row, "rawSse")), S(row, "rawSseSha256"));
            Assert.Equal(S(frozen, "rawContentSha256"), S(row, "rawContentSha256"));
            Assert.Equal(S(frozen, "rawSseSha256"), S(row, "rawSseSha256"));
        });

        // Rebuild the qualification-only production candidate before Gold is read; it must reproduce
        // the frozen exact body/registry/source universe and original P05 ownership for all 31 packs.
        var plans = Documents.Select(document => PdfHeadingMembershipProductionAdapter.Prepare(
            TestRepository.Path(document.Pdf), document.Id, Contract)).ToArray();
        var prepared = plans.SelectMany(plan => plan.Packs).ToDictionary(pack => $"{pack.DocumentId}|{pack.PackId}", StringComparer.Ordinal);
        Assert.Equal(31, prepared.Count);
        foreach (var request in requestRows.Values)
        {
            var pack = prepared[Key(request)];
            Assert.Equal(S(request, "providerRequestHash"), pack.ProviderRequestHash);
            Assert.Equal(S(request, "locatorRegistryFingerprint"), pack.Registry.Fingerprint);
            Assert.Equal(S(request, "sourceUniverseSha256"), plans.Single(plan => plan.DocumentId == pack.DocumentId).SourceUniverseSha256);
        }

        JsonDocument? retryDocument = null;
        var retryPath = TestRepository.Path($"{Root}/{RetryFile}");
        if (File.Exists(retryPath))
        {
            retryDocument = JsonDocument.Parse(File.ReadAllText(retryPath));
            var retry = retryDocument.RootElement;
            const string retryKey = "SRC-089|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002";
            Assert.Equal("v5-p6p-single-pack-rerun-v1", S(retry, "schemaVersion"));
            Assert.Equal(retryKey, S(retry, "target"));
            Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json"))), S(retry, "originalPrimaryResultSha256"));
            Assert.Equal(S(requestRows[retryKey], "providerRequestHash"), S(retry, "providerRequestHash"));
            Assert.Equal(S(requestRows[retryKey], "locatorRegistryFingerprint"), S(retry, "locatorRegistryFingerprint"));
            Assert.True(B(retry, "transportAccepted"));
            Assert.Equal("stop", S(retry, "finishReason"));
            Assert.True(B(retry, "parserAccepted"));
            Assert.Equal(Hash(S(retry, "rawContent")), S(retry, "rawContentSha256"));
            Assert.Equal(Hash(S(retry, "rawSse")), S(retry, "rawSseSha256"));
            Assert.Equal(I(retry, "quarantinedOccurrences") == 0, B(retry, "contractValid"));
        }

        // This is the first Gold access, after all 31 raw response/SSE hashes and all request hashes passed.
        var goldByDocument = Documents.ToDictionary(document => document.Id, document =>
        {
            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(document.Pdf));
            return ExactScorer.ReadGold(TestRepository.Path($"eval/a99-closed-loop/gold/{document.Id}.gold.json"), universe);
        }, StringComparer.Ordinal);

        var predictionsByDocument = Documents.ToDictionary(document => document.Id, _ => new List<Prediction>(), StringComparer.Ordinal);
        var boundCount = 0;
        var quarantineCount = 0;
        var rawProposalCount = 0;
        foreach (var row in resultRows)
        {
            var key = Key(row);
            var pack = prepared[key];
            var useRetry = retryDocument is not null && key == "SRC-089|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002";
            var canonicalContent = useRetry ? S(retryDocument!.RootElement, "rawContent") : S(row, "rawContent");
            var canonicalBytes = useRetry ? I(retryDocument!.RootElement, "rawContentUtf8Bytes") : I(row, "rawContentUtf8Bytes");
            using var raw = JsonDocument.Parse(canonicalContent);
            var rawCount = raw.RootElement.GetProperty("headings").GetArrayLength();
            var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(raw.RootElement,
                canonicalBytes, 49_152, pack.Registry, Enumerable.Range(0, pack.Registry.AtomCount).ToHashSet());
            Assert.Equal(useRetry ? I(retryDocument!.RootElement, "boundOccurrences") : I(row, "boundOccurrences"), parsed.Response.Occurrences.Count);
            Assert.Equal(useRetry ? I(retryDocument!.RootElement, "quarantinedOccurrences") : I(row, "quarantinedOccurrences"), parsed.Quarantined.Count);
            rawProposalCount += rawCount;
            boundCount += parsed.Response.Occurrences.Count;
            quarantineCount += parsed.Quarantined.Count;
            foreach (var locator in parsed.Response.Occurrences)
            {
                var endpoint = pack.Registry.Decode(locator);
                var spans = endpoint.Parts.Select(part => new ExactScorer.Span(part.Alias, part.Start, part.End)).ToArray();
                predictionsByDocument[pack.DocumentId].Add(new Prediction(ExactScorer.Identity(spans), spans,
                    string.Join(" ", endpoint.Parts.Select(part => part.Text))));
            }
        }

        var perDocument = new List<object>();
        var candidateDocF1 = new Dictionary<string, double>(StringComparer.Ordinal);
        var totalGold = 0; var totalHypotheses = 0; var totalTp = 0; var totalFp = 0; var totalFn = 0;
        var goldAxisRecovery = new Dictionary<string, (int Total, int P6PTp, int BaselineTp)>(StringComparer.Ordinal);
        var subgroupRecovery = new Dictionary<string, (int Total, int P6PTp, int BaselineTp)>(StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            var gold = goldByDocument[document.Id];
            var unique = predictionsByDocument[document.Id].GroupBy(item => item.Identity, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            var hypotheses = unique.Select((item, index) => new ExactScorer.Hypothesis(index, item.Text, "TRUE", [], null, null, [], "TITLE", null, [], item.Identity, null, item.Spans)).ToArray();
            var score = ExactScorer.Compute(gold, hypotheses);
            var headline = score.Headline();
            var headlineJson = JsonSerializer.SerializeToElement(headline);
            candidateDocF1[document.Id] = D(headlineJson, "f1");
            totalGold += gold.Count; totalHypotheses += unique.Length;
            totalTp += I(headlineJson, "truePositives"); totalFp += I(headlineJson, "falsePositives"); totalFn += I(headlineJson, "falseNegatives");
            var tpIds = gold.Where(claim => hypotheses.Any(hypothesis => hypothesis.Identity == claim.Identity))
                .Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);
            var baselineIdentities = await ReadProductionBaselineIdentities(document.Id, document.Pdf);
            foreach (var claim in gold)
            {
                var category = GoldCategory(claim);
                var old = goldAxisRecovery.GetValueOrDefault(category);
                goldAxisRecovery[category] = (old.Total + 1, old.P6PTp + (tpIds.Contains(claim.Identity) ? 1 : 0),
                    old.BaselineTp + (baselineIdentities.Contains(claim.Identity) ? 1 : 0));
                var subgroup = Subgroup(document.Id, claim);
                if (subgroup is not null)
                {
                    var previous = subgroupRecovery.GetValueOrDefault(subgroup);
                    subgroupRecovery[subgroup] = (previous.Total + 1, previous.P6PTp + (tpIds.Contains(claim.Identity) ? 1 : 0),
                        previous.BaselineTp + (baselineIdentities.Contains(claim.Identity) ? 1 : 0));
                }
            }
            perDocument.Add(new
            {
                documentId = document.Id, gold = gold.Count, distinctBoundOccurrences = unique.Length,
                exact = headline,
                baseline = new { exactTruePositives = gold.Count(claim => baselineIdentities.Contains(claim.Identity)) },
                goldAxisRecovery = gold.GroupBy(GoldCategory, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => new { gold = group.Count(), tp = group.Count(claim => tpIds.Contains(claim.Identity)), fn = group.Count(claim => !tpIds.Contains(claim.Identity)) }),
            });
        }
        var precision = totalHypotheses == 0 ? 0 : Math.Round((double)totalTp / totalHypotheses, 4);
        var recall = totalGold == 0 ? 0 : Math.Round((double)totalTp / totalGold, 4);
        var aggregate = new
        {
            scorer = ExactScorer.ScorerId, goldClaims = totalGold, distinctBoundOccurrences = totalHypotheses,
            truePositives = totalTp, falsePositives = totalFp, falseNegatives = totalFn,
            truePrecision = precision, trueRecall = recall,
            f1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4),
        };
        using var baseline = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(BaselinePath)));
        using var p6nb = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P6NBScorePath)));
        using var p6nc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P6NCScorePath)));
        var baselineTotal = baseline.RootElement.GetProperty("total");
        var baselineDocRows = baseline.RootElement.GetProperty("documents").EnumerateArray().ToDictionary(
            row => S(row, "documentId"), StringComparer.Ordinal);
        var current = JsonSerializer.SerializeToElement(aggregate);
        var tpTotal = I(current, "truePositives"); var fpTotal = I(current, "falsePositives"); var fnTotal = I(current, "falseNegatives");
        var p = D(current, "truePrecision"); var r = D(current, "trueRecall"); var f1 = D(current, "f1");
        var manifestContractValid = resultRows.Count(row => B(row, "contractValid"));
        var canonicalContractValid = retryDocument is null ? manifestContractValid :
            manifestContractValid - (B(resultRows.Single(row => Key(row) == "SRC-089|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002"), "contractValid") ? 1 : 0) +
            (B(retryDocument.RootElement, "contractValid") ? 1 : 0);
        var sourceOwnershipPass = manifest.GetProperty("sourceAuthority").GetProperty("exactOnceOwnership").GetBoolean() &&
            manifest.GetProperty("sourceAuthority").GetProperty("unchangedPackPartitionComparedWithP6NB").GetBoolean();
        var sourceHashOwnershipPass = sourceOwnershipPass && resultRows.All(row => S(row, "sourceSha256") == S(requestRows[Key(row)], "sourceSha256") &&
            S(row, "sourceUniverseSha256") == S(requestRows[Key(row)], "sourceUniverseSha256"));
        var subgroupNoRegression = subgroupRecovery.Count > 0 && subgroupRecovery.All(item => item.Value.P6PTp >= item.Value.BaselineTp);
        var perDocumentGates = Documents.ToDictionary(document => document.Id, document =>
        {
            var minimum = document.Id == "SRC-089" ? 0.7687 : 0.7238;
            return new
            {
                requiredMinimumF1 = minimum, p6pF1 = candidateDocF1[document.Id],
                baselineF1 = D(baselineDocRows[document.Id], "f1"),
                passes = candidateDocF1[document.Id] >= minimum,
            };
        }, StringComparer.Ordinal);
        var perDocumentGatePass = perDocumentGates.Values.All(value => value.passes);
        const bool tocIndexFalsePositiveFamilyPass = false; // No source-reviewed P6P FP family adjudication exists; fail closed.
        var gate = new
        {
            allPacksContractValid = canonicalContractValid == 31,
            noSourceHashOwnershipViolation = sourceHashOwnershipPass,
            exactF1AtLeastBaseline = f1 >= D(baselineTotal, "f1"),
            recallAtLeastBaseline = r >= D(baselineTotal, "recall"),
            precisionAtLeastMinimum = p >= 0.6582,
            tpAtLeastBaseline = tpTotal >= 118,
            titleLocalStructureIndexSubgroupNoRegression = subgroupNoRegression,
            perDocumentF1 = perDocumentGates,
            tocIndexFalsePositiveFamilyNoNewDominantFamily = tocIndexFalsePositiveFamilyPass,
            allPromotionPredicatesPass = canonicalContractValid == 31 && sourceHashOwnershipPass && f1 >= D(baselineTotal, "f1") &&
                r >= D(baselineTotal, "recall") && p >= 0.6582 && tpTotal >= 118 && subgroupNoRegression && perDocumentGatePass && tocIndexFalsePositiveFamilyPass,
            promotion = false,
            titleLocalStructureIndexSubgroups = subgroupRecovery.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
                pair => new { gold = pair.Value.Total, p6pTp = pair.Value.P6PTp, baselineTp = pair.Value.BaselineTp, deltaTp = pair.Value.P6PTp - pair.Value.BaselineTp }),
            tocIndexFalsePositiveFamilyGate = "NOT_EVALUATED: no frozen FP-family adjudication for this P6P cohort; fails closed",
            blockedFurtherChecks = canonicalContractValid != 31
                ? "No promotion because a canonical P6P pack remains contract-invalid. Subgroup and FP-family gates also fail closed until source-reviewed."
                : "No promotion: metric/per-document gates fail and TOC/index FP-family review is not established.",
        };

        FreezeArtifact.AssertJson(Root, retryDocument is null ? "gold-score.v1.json" : "gold-score-after-pack-rerun.v1.json", new
        {
            schemaVersion = "v5-p6p-document-aware-pdf-gold-score-v1",
            authority = new
            {
                gold = "current canonical 139-occurrence Gold; read-only",
                scorer = ExactScorer.ScorerId,
                unit = "document-scoped ordered source aliases and exact UTF-16 spans",
                semanticSelection = "every distinct bound sparse heading locator counts as a positive membership proposal",
                output = "candidate sparse locator binding, placement off; runtime unpromoted",
                manifest = "P6P request/body/P05 ownership parity and raw content/SSE hashes verified before Gold access",
            },
            execution = new
            {
                logicalPrimaryCalls = result.GetProperty("logicalProviderCalls").GetInt32(),
                authorizedSinglePackRerunCalls = retryDocument is null ? 0 : 1,
                rerunArtifactSha256 = retryDocument is null ? null : Hash(File.ReadAllText(retryPath)),
                attemptSelection = retryDocument is null
                    ? "original P6P response per pack"
                    : "the sole pre-identified contract-invalid pack uses its one authorized rerun, independent of Gold; all other packs use original primary responses",
                transportRetries = resultRows.Sum(row => I(row, "retryCount")),
                rerunTransportRetries = retryDocument is null ? 0 : I(retryDocument.RootElement, "retryCount"),
                finishStopPacks = resultRows.Count(row => S(row, "finishReason") == "stop"),
                parserAcceptedPacks = resultRows.Count(row => B(row, "parserAccepted")),
                parserBinderContractValidPacks = canonicalContractValid,
                distinctBoundOccurrences = boundCount,
                rawProposedOccurrences = rawProposalCount,
                quarantinedOccurrences = quarantineCount,
                maxRawResponseUtf8Bytes = resultRows.Max(row => I(row, "rawContentUtf8Bytes")),
                providerCallsDuringScore = 0, goldRead = true, goldMutation = "NONE", placement = "OFF", runtimePromoted = false,
            },
            metric = aggregate,
            baseline = new
            {
                scorer = ExactScorer.ScorerId,
                exact = baselineTotal,
                delta = new { tp = tpTotal - I(baselineTotal, "tp"), fp = fpTotal - I(baselineTotal, "fp"), fn = fnTotal - I(baselineTotal, "fn"),
                    precision = Math.Round(p - D(baselineTotal, "precision"), 4), recall = Math.Round(r - D(baselineTotal, "recall"), 4), f1 = Math.Round(f1 - D(baselineTotal, "f1"), 4) },
            },
            p6nDiagnostics = new
            {
                comparability = "P6N-B/C use the frozen P6N overlap/fidelity score artifacts; shown as diagnostics, not substituted for GENERIC_EXACT_SCORER_V1.",
                p6nb = p6nb.RootElement.GetProperty("metrics").GetProperty("full31").GetProperty("p6nb"),
                p6nc = p6nc.RootElement.GetProperty("metrics").GetProperty("pairedSubset").GetProperty("p6nc"),
            },
            goldAxisRecovery = goldAxisRecovery.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
                pair => new { gold = pair.Value.Total, p6pTp = pair.Value.P6PTp, baselineTp = pair.Value.BaselineTp,
                    p6pFn = pair.Value.Total - pair.Value.P6PTp, baselineFn = pair.Value.Total - pair.Value.BaselineTp }),
            promotionGate = gate,
            documents = perDocument,
        });
    }

    private static string GoldCategory(ExactScorer.Claim claim) => $"{claim.Primary ?? "UNSPECIFIED"}|{claim.Scope ?? "UNSPECIFIED"}|{claim.TitleRelation}";
    private static string? Subgroup(string documentId, ExactScorer.Claim claim)
    {
        if (claim.Primary == "IDENTITY" && claim.Scope is "DOCUMENT" or "DOCUMENT_PART") return "DOCUMENT_TITLE";
        if (claim.Primary == "STRUCTURE" && claim.Scope == "SECTION") return "LOCAL_STRUCTURAL_HEADING";
        if (documentId == "SRC-095" && (claim.Text is "Index" or "C" or "D" or "G" or "H" or "M" or "P" or "R" or "S")) return "INDEX_GROUP";
        return null;
    }

    private static async Task<HashSet<string>> ReadProductionBaselineIdentities(string documentId, string relativePdf)
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(BaselinePath)));
        var runId = score.RootElement.GetProperty("run").GetProperty("runId").GetString()!;
        var runDir = TestRepository.Path($"eval/a99-closed-loop/production-rebaseline-v1/{runId}");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "run-manifest.json")));
        var leaves = manifest.RootElement.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("DocumentId").GetString() == documentId)
            .OrderBy(row => row.GetProperty("Ordinal").GetInt32()).ToArray();
        var replies = leaves.Select(row =>
        {
            var ordinal = row.GetProperty("Ordinal").GetInt32();
            var file = Path.Combine(runDir, $"leaf-{ordinal:000}.content.json");
            return row.GetProperty("ContractValidAfterReassembly").GetBoolean() && File.Exists(file) ? File.ReadAllText(file) : "{}";
        }).ToArray();
        using var replay = new FrozenReplyClassifier(replies);
        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(relativePdf), replay,
            CancellationToken.None, runPlacement: false).ConfigureAwait(false);
        Assert.Equal(leaves.Length, replay.Requests.Count);
        Assert.Equal(0, replay.CallsBeyondRecording);
        var aliases = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(relativePdf)).Atoms
            .ToDictionary(atom => atom.SourceId, atom => atom.Alias, StringComparer.Ordinal);
        var emitted = authority.EmittedElementIds;
        return authority.Structure.Elements.Where(element => emitted is null || emitted.Contains(element.Id))
            .Select(element => ExactScorer.Identity(element.Sources.Select(source =>
                new ExactScorer.Span(aliases[source.SourceId], source.Span.Start, source.Span.End))))
            .ToHashSet(StringComparer.Ordinal);
    }
    private static JsonDocument Read(string file) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{file}")));
    private static JsonElement[] Rows(JsonElement value) => value.GetProperty("rows").EnumerateArray().ToArray();
    private static string Key(JsonElement row) => $"{S(row, "documentId")}|{S(row, "packId")}";
    private static string S(JsonElement row, string name) => row.GetProperty(name).GetString()!;
    private static int I(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static double D(JsonElement row, string name) => row.GetProperty(name).GetDouble();
    private static bool B(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
