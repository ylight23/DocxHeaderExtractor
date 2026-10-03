using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline P6S-D4 score over immutable P6S-D3 responses and current canonical Gold.</summary>
public sealed class V5P6SCandidateAuthorityFull31GoldScoreTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldBasisPath = "eval/a99-closed-loop/gold-current/evaluation-basis.v1.json";
    private static readonly (string Id, string Pdf, string GoldSha, int GoldCount)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, "50d9e57225d9cf9dcf174e5b5b7422158c06d7327241d77fc4d019831b8a808d", 36),
        ("SRC-095", SourcePdfCorpus.Src095, "8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5", 103),
    ];

    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(string Identity, IReadOnlyList<Part> Parts);
    private sealed record Prediction(string DocumentId, string CandidateId, string Identity, IReadOnlyList<Part> Parts, string Kind);
    private sealed record PackScore(string DocumentId, string PackId, bool Transport, string? Finish, bool ParserAccepted,
        int RawDecisions, int AcceptedBeforeOverlap, int AcceptedAfterOverlap, int DuplicateCollapsed,
        int Quarantined, int OverlapQuarantined, int BoundBeforeOverlap, int BoundAfterOverlap,
        int? PromptTokens, int? CompletionTokens, int ResponseBytes, double? LatencyMs);

    [Fact]
    public void P6SD_full31_is_scored_against_hash_pinned_canonical_gold_offline()
    {
        var resultPath = TestRepository.Path($"{Root}/result.v1.json");
        var manifestPath = TestRepository.Path($"{Root}/execution-manifest.v1.json");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var resultRoot = result.RootElement;
        Assert.Equal(31, resultRoot.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, resultRoot.GetProperty("rows").GetArrayLength());
        Assert.False(resultRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, resultRoot.GetProperty("retry").GetInt32());
        Assert.False(resultRoot.GetProperty("repair").GetBoolean());
        Assert.False(resultRoot.GetProperty("fallback").GetBoolean());
        Assert.Equal("NONE", resultRoot.GetProperty("goldMutation").GetString());
        Assert.Equal("UNCHANGED", resultRoot.GetProperty("sharedRuntime").GetString());
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest.RootElement.GetProperty("status").GetString());

        var plans = Documents.ToDictionary(document => document.Id, document =>
        {
            var pdf = TestRepository.Path(document.Pdf);
            var sourceSha = CanonicalSemanticSourceHash.Compute(pdf);
            var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json");
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, document.Id);
        }, StringComparer.Ordinal);
        Assert.Equal(31, plans.Values.Sum(plan => plan.Packs.Count));
        Assert.Equal(2884, plans.Values.Sum(plan => plan.SourceOccurrenceTotal));

        var gold = ReadAndVerifyGold(plans);
        var rows = resultRoot.GetProperty("rows").EnumerateArray().ToArray();
        var manifestRows = manifest.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, manifestRows.Length);
        Assert.Equal(31, rows.Length);

        var preHeadings = new List<Prediction>();
        var finalHeadings = new List<Prediction>();
        var representations = new List<Prediction>();
        var packScores = new List<PackScore>();
        var goldDiagnostics = new List<JsonElement>();
        var completedPacks = 0;

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var manifestRow = manifestRows[index];
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            Assert.Equal(manifestRow.GetProperty("DocumentId").GetString(), documentId);
            Assert.Equal(manifestRow.GetProperty("PackId").GetString(), packId);
            var plan = plans[documentId];
            var pack = plan.Packs.Single(value => value.PackId == packId);
            Assert.Equal(pack.PackOrdinal, row.GetProperty("packOrdinal").GetInt32());
            Assert.Equal(pack.Request.UserMessageSha256, row.GetProperty("semanticRequestHash").GetString());
            Assert.Equal(pack.ProviderRequestHash, row.GetProperty("providerRequestHash").GetString());
            Assert.Equal(pack.ProviderRequestHash, manifestRow.GetProperty("ProviderRequestHash").GetString());
            Assert.Equal(pack.ProviderRequestBytes, row.GetProperty("providerRequestBytes").GetInt32());
            Assert.Equal(pack.Universe.Fingerprint, row.GetProperty("candidateUniverseFingerprint").GetString());
            Assert.Equal(plan.SourceSha256, row.GetProperty("sourceSha256").GetString());

            var transport = row.GetProperty("transportAccepted").GetBoolean();
            var finish = row.GetProperty("finishReason").ValueKind == JsonValueKind.String ? row.GetProperty("finishReason").GetString() : null;
            var parserAccepted = false;
            var rawDecisionCount = 0; var before = 0; var after = 0; var collapsed = 0; var quarantine = 0; var overlapQuarantine = 0;
            var boundBefore = 0; var boundAfter = 0;
            var responseBytes = row.GetProperty("rawResponseUtf8Bytes").GetInt32();
            if (transport)
            {
                Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
                var rawSse = row.GetProperty("rawSse").GetString()!;
                var rawResponse = row.GetProperty("rawResponse").GetString()!;
                Assert.Equal(Hash(rawSse), row.GetProperty("rawSseSha256").GetString());
                Assert.Equal(Hash(rawResponse), row.GetProperty("rawResponseSha256").GetString());
                Assert.Equal(Encoding.UTF8.GetByteCount(rawResponse), responseBytes);
                if (string.Equals(finish, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, rawResponse);
                        parserAccepted = true; rawDecisionCount = parsed.RawDecisionCount; before = parsed.AcceptedBeforeOverlapQuarantine.Count;
                        after = parsed.Accepted.Count; collapsed = parsed.DuplicateDecisionsCollapsed; quarantine = parsed.Quarantined.Count;
                        overlapQuarantine = parsed.Quarantined.Count(value => value.Reason == "candidate-overlap-conflict");
                        foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
                        {
                            var rebound = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                            Assert.True(rebound.IsBound, rebound.Reason);
                            boundBefore++;
                            var prediction = ToPrediction(documentId, decision.Candidate.Id, rebound.Parts, decision.Kind.ToString());
                            if (decision.Kind == V5CandidateDecisionKind.HEADING) preHeadings.Add(prediction);
                            else representations.Add(prediction);
                        }
                        foreach (var decision in parsed.Headings)
                        {
                            var rebound = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                            Assert.True(rebound.IsBound, rebound.Reason);
                            boundAfter++;
                            finalHeadings.Add(ToPrediction(documentId, decision.Candidate.Id, rebound.Parts, "HEADING"));
                        }
                    }
                    catch (JsonException) { }
                    catch (InvalidOperationException) { }
                }
            }
            if (transport && string.Equals(finish, "stop", StringComparison.OrdinalIgnoreCase) && parserAccepted) completedPacks++;
            packScores.Add(new PackScore(documentId, packId, transport, finish, parserAccepted, rawDecisionCount, before, after,
                collapsed, quarantine, overlapQuarantine, boundBefore, boundAfter, Usage(row, "prompt_tokens"),
                Usage(row, "completion_tokens"), responseBytes,
                row.TryGetProperty("latencyMs", out var latency) && latency.ValueKind == JsonValueKind.Number ? latency.GetDouble() : null));
        }

        foreach (var document in Documents)
        foreach (var (item, goldIndex) in gold[document.Id].Select((value, index) => (value, index)))
            goldDiagnostics.Add(JsonSerializer.SerializeToElement(DescribeGold(document.Id, goldIndex, item,
                preHeadings.Where(value => value.DocumentId == document.Id).ToArray(),
                finalHeadings.Where(value => value.DocumentId == document.Id).ToArray(),
                representations.Where(value => value.DocumentId == document.Id).ToArray(), gold[document.Id])));

        var allGold = gold.SelectMany(pair => pair.Value.Select(value => (DocumentId: pair.Key, Gold: value))).ToArray();
        var fullEvaluable = completedPacks == 31;
        Assert.Equal(31, completedPacks);
        Assert.Equal(139, goldDiagnostics.Count);
        var output = new
        {
            schemaVersion = "v5-p6sd-candidate-authority-full31-multiaxis-gold-score-v1",
            execution = new { providerCallsDuringScore = 0, primaryCalls = resultRoot.GetProperty("providerCalls").GetInt32(), retries = 0,
                goldRead = true, goldMutation = "NONE", runtimeChanged = false, manifestBodyParity = true,
                totalPacks = 31, packsWithStopAndParserAcceptance = completedPacks, fullCohortEvaluable = fullEvaluable },
            goldAuthority = GoldAuthority(plans),
            comparisonAuthority = new
            {
                productionBaseline = new { scorer = "GENERIC_EXACT_SCORER_V1", tp = 118, fp = 56, fn = 21, precision = .6782, recall = .8489, f1 = .7540 },
                p6nb = new { exactF1 = .6352, semanticOverlapF1 = .6950, note = "separate historical scorer; semantic overlap is not directly interchangeable with exact matching" },
                p6p = new { scorer = "GENERIC_EXACT_SCORER_V1", tp = 125, fp = 112, fn = 14, precision = .5274, recall = .8993, f1 = .6649 },
                p6r = new { scorer = "GENERIC_EXACT_SCORER_V1", tp = 101, fp = 134, fn = 38, f1 = .5824 },
            },
            metrics = new
            {
                status = fullEvaluable ? "EVALUABLE_FULL31" : "NOT_EVALUABLE_INCOMPLETE_TRANSPORT_OR_PARSE",
                headingRegionDetectionBeforeOverlapQuarantine = RegionMetrics(preHeadings, allGold),
                finalExactOccurrenceAfterQuarantine = ExactMetrics(finalHeadings, allGold),
                extentOutcomeCounts = goldDiagnostics.GroupBy(value => value.GetProperty("outcome").GetString()!, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()),
                outcomeTotals = new { gold = allGold.Length, exact = CountOutcome(goldDiagnostics, "EXACT"), truncated = CountOutcome(goldDiagnostics, "TRUNCATED_EXTENT"),
                    excess = CountOutcome(goldDiagnostics, "EXCESS_EXTENT"), boundaryMismatch = CountOutcome(goldDiagnostics, "BOUNDARY_MISMATCH"),
                    overSegmented = CountOutcome(goldDiagnostics, "OVER_SEGMENTED"), underSegmented = CountOutcome(goldDiagnostics, "UNDER_SEGMENTED"),
                    roleError = CountOutcome(goldDiagnostics, "ROLE_ERROR"), semanticMiss = CountOutcome(goldDiagnostics, "SEMANTIC_MISS") },
                binding = new { acceptedDecisionsBeforeOverlapQuarantine = packScores.Sum(value => value.BoundBeforeOverlap), reboundSuccessfullyBeforeOverlapQuarantine = packScores.Sum(value => value.BoundBeforeOverlap),
                    acceptedHeadingDecisionsAfterOverlapQuarantine = packScores.Sum(value => value.BoundAfterOverlap), bindingRefusals = 0 },
                contract = new
                {
                    packs = packScores.Select(value => new { value.DocumentId, value.PackId, transportAccepted = value.Transport, value.Finish,
                        value.ParserAccepted, value.RawDecisions, value.AcceptedBeforeOverlap, value.AcceptedAfterOverlap, value.DuplicateCollapsed,
                        value.Quarantined, value.OverlapQuarantined, value.BoundBeforeOverlap, value.BoundAfterOverlap,
                        value.PromptTokens, value.CompletionTokens, value.ResponseBytes, value.LatencyMs }).ToArray(),
                    transportSuccess = packScores.Count(value => value.Transport), stopFinish = packScores.Count(value => value.Finish == "stop"),
                    parserAcceptedPacks = packScores.Count(value => value.ParserAccepted), totalQuarantinedDecisions = packScores.Sum(value => value.Quarantined),
                    duplicateDecisionsCollapsed = packScores.Sum(value => value.DuplicateCollapsed), overlapConflictQuarantines = packScores.Sum(value => value.OverlapQuarantined),
                    maxResponseUtf8Bytes = packScores.Max(value => value.ResponseBytes), maxPromptTokens = packScores.Where(value => value.PromptTokens.HasValue).Select(value => value.PromptTokens!.Value).DefaultIfEmpty().Max(),
                    maxCompletionTokens = packScores.Where(value => value.CompletionTokens.HasValue).Select(value => value.CompletionTokens!.Value).DefaultIfEmpty().Max(),
                    maxLatencyMs = packScores.Where(value => value.LatencyMs.HasValue).Select(value => value.LatencyMs!.Value).DefaultIfEmpty().Max(),
                },
                perDocument = Documents.Select(document => ScoreDocument(document.Id, preHeadings, finalHeadings, representations, gold[document.Id])).ToArray(),
            },
            relationSelectionAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            goldOccurrenceDiagnostics = goldDiagnostics,
            modelHeadingCandidatesBeforeOverlapQuarantine = preHeadings,
            finalHeadingCandidatesAfterOverlapQuarantine = finalHeadings,
            representationCandidates = representations,
        };
        FreezeArtifact.AssertJson(Root, "full31-gold-multiaxis-score.v1.json", output);
    }

    private static Dictionary<string, List<Gold>> ReadAndVerifyGold(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        using var basis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldBasisPath)));
        var basisRoot = basis.RootElement;
        Assert.Equal("USER_RECONFIRMED_FROZEN", basisRoot.GetProperty("status").GetString());
        Assert.Equal(139, basisRoot.GetProperty("cohort").GetProperty("totalGoldOccurrences").GetInt32());
        var result = new Dictionary<string, List<Gold>>(StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            var basisDocument = basisRoot.GetProperty("cohort").GetProperty("documentsById").GetProperty(document.Id);
            var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{document.Id}.gold.v1.json");
            var goldBytes = File.ReadAllBytes(goldPath);
            Assert.Equal(document.GoldSha, Hash(goldBytes));
            Assert.Equal(document.GoldSha, basisDocument.GetProperty("canonicalGoldSha256").GetString());
            var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(document.Pdf));
            Assert.Equal(sourceSha, plans[document.Id].SourceSha256);
            Assert.Equal(sourceSha, basisDocument.GetProperty("sourceSha256").GetString());
            Assert.Equal(document.GoldCount, basisDocument.GetProperty("semanticHeadingTotal").GetInt32());

            using var goldJson = JsonDocument.Parse(goldBytes);
            var claims = goldJson.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
            Assert.Equal(document.GoldCount, claims.Length);
            var goldRows = new List<Gold>(claims.Length);
            foreach (var claim in claims)
            {
                var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
                var binding = SemanticSourcePartBinder.Bind(plans[document.Id].SourceAtoms, sourceParts);
                Assert.True(binding.IsBound, binding.Reason);
                goldRows.Add(new Gold(binding.Identity, binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray()));
            }
            Assert.Equal(goldRows.Count, goldRows.Select(value => value.Identity).Distinct(StringComparer.Ordinal).Count());
            result.Add(document.Id, goldRows);
        }
        return result;
    }

    private static object DescribeGold(string documentId, int index, Gold gold, IReadOnlyList<Prediction> pre,
        IReadOnlyList<Prediction> final, IReadOnlyList<Prediction> representations, IReadOnlyList<Gold> allGold)
    {
        var overlap = pre.Where(prediction => Overlap(prediction.Parts, gold.Parts)).ToArray();
        var finalOverlap = final.Where(prediction => Overlap(prediction.Parts, gold.Parts)).ToArray();
        var representationOverlap = representations.Where(prediction => Overlap(prediction.Parts, gold.Parts)).ToArray();
        var under = pre.Where(prediction => Covers(prediction.Parts, gold.Parts) &&
            allGold.Count(other => Covers(prediction.Parts, other.Parts)) > 1).ToArray();
        string outcome;
        double coverage;
        if (overlap.Any(value => Same(value.Parts, gold.Parts))) { outcome = "EXACT"; coverage = 1; }
        else if (under.Length > 0) { outcome = "UNDER_SEGMENTED"; coverage = Coverage(gold.Parts, under.SelectMany(value => value.Parts).ToArray()); }
        else if (IsDisjointSplit(overlap, gold.Parts))
        { outcome = "OVER_SEGMENTED"; coverage = Coverage(gold.Parts, overlap.SelectMany(value => value.Parts).ToArray()); }
        else if (overlap.Length == 0)
        { outcome = representationOverlap.Length > 0 ? "ROLE_ERROR" : "SEMANTIC_MISS"; coverage = 0; }
        else
        {
            var prediction = overlap.OrderByDescending(value => Coverage(gold.Parts, value.Parts)).ThenBy(value => value.Identity, StringComparer.Ordinal).First();
            coverage = Coverage(gold.Parts, prediction.Parts);
            if (Same(prediction.Parts, gold.Parts)) outcome = "EXACT";
            else if (StrictSubset(prediction.Parts, gold.Parts)) outcome = "TRUNCATED_EXTENT";
            else if (StrictSubset(gold.Parts, prediction.Parts)) outcome = "EXCESS_EXTENT";
            else outcome = "BOUNDARY_MISMATCH";
        }
        var ids = overlap.Select(value => value.CandidateId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var finalIds = finalOverlap.Select(value => value.CandidateId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new { documentId, goldOrdinal = index + 1, goldIdentity = gold.Identity, outcome, modelHeadingCandidates = ids,
            finalAcceptedCandidates = finalIds, representationCandidates = representationOverlap.Select(value => value.CandidateId).Distinct(StringComparer.Ordinal).ToArray(),
            contractLoss = ids.Except(finalIds, StringComparer.Ordinal).Any() ? "OVERLAP_OR_CONTRACT_QUARANTINE" : "NONE", coverage = Math.Round(coverage, 4) };
    }

    private static object ScoreDocument(string id, IReadOnlyList<Prediction> pre, IReadOnlyList<Prediction> final,
        IReadOnlyList<Prediction> representations, IReadOnlyList<Gold> gold)
    {
        var goldRows = gold.Select(item => (DocumentId: id, Gold: item)).ToArray();
        var localPre = pre.Where(value => value.DocumentId == id).ToArray();
        var localFinal = final.Where(value => value.DocumentId == id).ToArray();
        return new { documentId = id, gold = gold.Count, headingRegionDetection = RegionMetrics(localPre, goldRows),
            finalExactOccurrence = ExactMetrics(localFinal, goldRows), representationCandidates = representations.Count(value => value.DocumentId == id) };
    }

    private static object RegionMetrics(IReadOnlyList<Prediction> predictions, IReadOnlyList<(string DocumentId, Gold Gold)> gold)
    {
        var tp = gold.Count(item => predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var fp = predictions.Count(prediction => !gold.Any(item => item.DocumentId == prediction.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        return Metric(tp, fp, gold.Count - tp);
    }

    private static object ExactMetrics(IReadOnlyList<Prediction> predictions, IReadOnlyList<(string DocumentId, Gold Gold)> gold)
    {
        var tp = gold.Count(item => predictions.Any(prediction => prediction.DocumentId == item.DocumentId && prediction.Identity == item.Gold.Identity));
        var fp = predictions.Count(prediction => !gold.Any(item => item.DocumentId == prediction.DocumentId && prediction.Identity == item.Gold.Identity));
        return Metric(tp, fp, gold.Count - tp);
    }

    private static object Metric(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(precision, 4), recall = Math.Round(recall, 4), f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4) };
    }

    private static Prediction ToPrediction(string documentId, string id, IReadOnlyList<BoundSourcePart> parts, string kind)
    {
        var mapped = parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
        return new Prediction(documentId, id, string.Join("|", mapped.Select(part => $"{part.Alias}:{part.Start}-{part.End}")), mapped, kind);
    }

    private static bool Same(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static int CountChars(IReadOnlyList<Part> parts) => parts.Sum(part => part.End - part.Start);
    private static bool StrictSubset(IReadOnlyList<Part> small, IReadOnlyList<Part> large) => CountChars(small) < CountChars(large) &&
        small.All(part => large.Any(container => container.Alias == part.Alias && container.Start <= part.Start && part.End <= container.End));
    private static bool Covers(IReadOnlyList<Part> cover, IReadOnlyList<Part> target) => target.All(part =>
        cover.Any(candidate => candidate.Alias == part.Alias && candidate.Start <= part.Start && candidate.End >= part.End));
    private static bool IsDisjointSplit(IReadOnlyList<Prediction> predictions, IReadOnlyList<Part> gold)
    {
        if (predictions.Select(value => value.CandidateId).Distinct(StringComparer.Ordinal).Count() < 2 ||
            predictions.Any(value => !StrictSubset(value.Parts, gold))) return false;
        var allParts = predictions.SelectMany(value => value.Parts).ToArray();
        for (var left = 0; left < allParts.Length; left++)
        for (var right = left + 1; right < allParts.Length; right++)
            if (allParts[left].Alias == allParts[right].Alias && Math.Max(allParts[left].Start, allParts[right].Start) < Math.Min(allParts[left].End, allParts[right].End))
                return false;
        return Coverage(gold, allParts) >= 0.9999;
    }
    private static double Coverage(IReadOnlyList<Part> gold, IReadOnlyList<Part> predicted)
    {
        var total = CountChars(gold);
        if (total == 0) return 0;
        var covered = 0;
        foreach (var expected in gold)
        {
            var intervals = predicted.Where(value => value.Alias == expected.Alias)
                .Select(value => (Start: Math.Max(expected.Start, value.Start), End: Math.Min(expected.End, value.End)))
                .Where(value => value.End > value.Start).OrderBy(value => value.Start).ToArray();
            var cursor = -1; var end = -1;
            foreach (var interval in intervals)
            {
                if (cursor < 0) { cursor = interval.Start; end = interval.End; }
                else if (interval.Start <= end) end = Math.Max(end, interval.End);
                else { covered += end - cursor; cursor = interval.Start; end = interval.End; }
            }
            if (cursor >= 0) covered += end - cursor;
        }
        return Math.Min(1d, (double)covered / total);
    }

    private static int CountOutcome(IEnumerable<JsonElement> diagnostics, string outcome) => diagnostics
        .Count(value => value.GetProperty("outcome").GetString() == outcome);
    private static int? Usage(JsonElement row, string property) => row.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static object GoldAuthority(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        using var basis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldBasisPath)));
        return new { basis = GoldBasisPath, status = basis.RootElement.GetProperty("status").GetString(), goldMutation = "NONE",
            total = 139, documents = Documents.Select(document => new { documentId = document.Id, sourceSha256 = plans[document.Id].SourceSha256,
                goldSha256 = document.GoldSha, goldOccurrences = document.GoldCount }).ToArray() };
    }
}
