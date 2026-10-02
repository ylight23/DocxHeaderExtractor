using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores the frozen P6M qualification responses against the canonical occurrence Gold. This is
/// offline-only: it reads Gold and frozen provider artifacts, replays the qualification locator
/// parser/binder, and does not call a provider or alter Gold/runtime behavior.
/// </summary>
public sealed class V5P6MGoldOccurrenceScoringTests
{
    private const string ResultPath = "artifacts/v5-p6m-p6l-full31-qualification/result.v1.json";
    private const string ManifestPath = "artifacts/v5-p6m-p6l-full31-qualification/execution-manifest.v1.json";
    private const string ScoreRoot = "artifacts/v5-p6m-p6l-full31-qualification";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    [Fact]
    public void Score_frozen_P6M_full31_occurrences_against_Gold_offline()
    {
        using var resultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath)));
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ManifestPath)));
        var result = resultDoc.RootElement;
        var manifest = manifestDoc.RootElement;
        Assert.Equal("v5-p6m-p6l-full31-result-v1", result.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, result.GetProperty("providerCalls").GetInt32());
        Assert.False(result.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", result.GetProperty("semanticScore").GetString());
        Assert.Equal("v5-p6m-p6l-full31-manifest-v1", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest.GetProperty("status").GetString());

        var resultRows = result.GetProperty("rows").EnumerateArray().ToArray();
        var manifestRows = manifest.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, resultRows.Length);
        Assert.Equal(31, manifestRows.Length);

        var gold = ReadGold();
        var review = ReadSourceReview();
        var predictions = gold.Keys.ToDictionary(document => document, _ => new Dictionary<string, Prediction>(StringComparer.Ordinal), StringComparer.Ordinal);
        var parserQuarantines = new List<object>();
        var executionRows = new List<object>();
        var cache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();

        foreach (var row in resultRows.OrderBy(row => row.GetProperty("documentId").GetString(), StringComparer.Ordinal)
                     .ThenBy(row => row.GetProperty("parentOrdinal").GetInt32()))
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var pdf = PdfFor(documentId);
            if (!cache.TryGetValue(documentId, out var source))
            {
                var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), documentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
                source = (packs, V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf)).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(documentId, source);
            }

            var ordinal = row.GetProperty("parentOrdinal").GetInt32();
            var pack = source.Packs.Single(candidate => candidate.PackId == row.GetProperty("packId").GetString());
            var ownedAtoms = pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray();
            var registry = RequestLocalLocatorRegistry.Create(ownedAtoms);
            Assert.Equal(row.GetProperty("registryFingerprint").GetString(), registry.Fingerprint);

            var request = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var manifestRow = manifestRows.Single(item => item.GetProperty("documentId").GetString() == documentId &&
                item.GetProperty("parentOrdinal").GetInt32() == ordinal);
            Assert.Equal(manifestRow.GetProperty("semanticRequestHash").GetString(), request.UserMessageSha256);
            var providerBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage,
                pack.MaxCompletionTokens, Envelope);
            Assert.Equal(manifestRow.GetProperty("providerRequestHash").GetString(), providerBody.Hash);

            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var rawCount = raw.RootElement.GetProperty("occurrences").GetArrayLength();
            var rawBytes = row.GetProperty("rawResponseBytes").GetInt32();
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.True(rawBytes <= ResponseCap);
            Assert.Equal(row.GetProperty("analysis").GetProperty("rawOccurrences").GetInt32(), rawCount);
            var parsed = registry.Parse(raw.RootElement, rawBytes, ResponseCap, Enumerable.Range(0, ownedAtoms.Length).ToHashSet());
            Assert.Empty(parsed.Quarantined);
            Assert.Equal(row.GetProperty("analysis").GetProperty("boundOccurrences").GetInt32(), parsed.Response.Occurrences.Count);

            foreach (var occurrence in parsed.Response.Occurrences)
            {
                var endpoint = registry.Decode(occurrence);
                var parts = endpoint.Parts.Select(part => new SourcePart(part.Alias, part.Start, part.End)).ToArray();
                var identity = Identity(documentId, parts);
                if (predictions[documentId].TryGetValue(identity, out var existing))
                    predictions[documentId][identity] = existing with { Functions = existing.Functions.Union(occurrence.Functions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
                else
                    predictions[documentId].Add(identity, new Prediction(documentId, identity, parts,
                        occurrence.Functions.Order(StringComparer.Ordinal).ToArray(), string.Join(" ", endpoint.Parts.Select(part => part.Text)), ordinal));
            }

            executionRows.Add(new
            {
                documentId, parentOrdinal = ordinal, packId = pack.PackId, registryFingerprint = registry.Fingerprint,
                requestHashVerified = true, providerRequestHashVerified = true, rawOccurrences = rawCount,
                parsedBoundOccurrenceIdentities = parsed.Response.Occurrences.Count,
                parserQuarantines = parsed.Quarantined.Count,
            });
        }

        var sourcePartsByDocument = gold.Keys.ToDictionary(document => document,
            document => gold[document].SelectMany(heading => heading.Parts).Select(part => part.Alias).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var documents = new List<object>();
        var goldRows = new List<object>();
        var allFalsePositiveReviewVerdicts = new List<string>();
        var semanticTp = 0; var semanticFp = 0; var semanticFn = 0;
        var exactTp = 0; var exactFp = 0; var exactFn = 0;
        var functionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var documentId in gold.Keys.Order(StringComparer.Ordinal))
        {
            var docGold = gold[documentId];
            var docPredictions = predictions[documentId].Values.ToArray();
            var headingPredictions = docPredictions.Where(candidate => candidate.IsHeadingMember).ToArray();
            Assert.Equal(docGold.Count, docGold.Select(heading => heading.Identity).Distinct(StringComparer.Ordinal).Count());
            var goldIdentities = docGold.Select(heading => Identity(documentId, heading.Parts)).ToHashSet(StringComparer.Ordinal);
            var predictionRows = docGold.Select(heading =>
            {
                var exact = headingPredictions.Where(candidate => SameParts(candidate.Parts, heading.Parts)).ToArray();
                var overlap = headingPredictions.Where(candidate => Overlaps(candidate.Parts, heading.Parts)).ToArray();
                var nonMemberOverlap = docPredictions.Where(candidate => !candidate.IsHeadingMember && Overlaps(candidate.Parts, heading.Parts)).ToArray();
                var sameAlias = headingPredictions.Where(candidate => candidate.Parts.Any(part => heading.Parts.Any(goldPart => part.Alias == goldPart.Alias))).ToArray();
                var bucket = exact.Length > 0 ? "EXACT"
                    : overlap.Length > 0 ? "PARTIAL_OR_SPLIT_SOURCE_OVERLAP"
                    : nonMemberOverlap.Length > 0 ? "WRONG_SEMANTIC_FUNCTION"
                    : sameAlias.Length > 0 ? "MISLOCATED_SAME_SOURCE_ATOM"
                    : "NO_MODEL_PROPOSAL";
                return new GoldRow(heading, exact, overlap, sameAlias, nonMemberOverlap, bucket);
            }).ToArray();

            var semanticFalsePositives = headingPredictions.Where(candidate => !docGold.Any(heading => Overlaps(candidate.Parts, heading.Parts))).ToArray();
            var falsePositiveReviews = semanticFalsePositives.Select(candidate => new
            {
                candidate.Identity,
                sourceAliases = candidate.Parts.Select(part => part.Alias).Distinct(StringComparer.Ordinal).ToArray(),
                verdict = review.Verdict(documentId, candidate.Parts.Select(part => part.Alias)),
            }).ToArray();
            Assert.All(falsePositiveReviews, item => Assert.Equal("NON_HEADING", item.verdict));
            allFalsePositiveReviewVerdicts.AddRange(falsePositiveReviews.Select(item => item.verdict));

            var docSemanticTp = predictionRows.Count(row => row.Overlaps.Count > 0);
            var docSemanticFn = predictionRows.Length - docSemanticTp;
            var docExactTp = predictionRows.Count(row => row.Exact.Count > 0);
            var docExactFn = predictionRows.Length - docExactTp;
            var docExactFp = headingPredictions.Count(candidate => !goldIdentities.Contains(candidate.Identity));
            semanticTp += docSemanticTp; semanticFn += docSemanticFn; semanticFp += semanticFalsePositives.Length;
            exactTp += docExactTp; exactFn += docExactFn; exactFp += docExactFp;

            foreach (var prediction in docPredictions)
                foreach (var function in prediction.Functions)
                    functionCounts[function] = functionCounts.GetValueOrDefault(function) + 1;

            documents.Add(new
            {
                documentId,
                goldOccurrences = docGold.Count,
                boundPredictionUnits = docPredictions.Length,
                headingMemberPredictionUnits = headingPredictions.Length,
                exact = Metric(docExactTp, docExactFp, docExactFn),
                semanticOccurrence = Metric(docSemanticTp, semanticFalsePositives.Length, docSemanticFn),
                occurrenceOutcomes = predictionRows.GroupBy(row => row.Bucket).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                wrongSemanticProposalCount = semanticFalsePositives.Length,
                wrongSemanticFunctionGoldCount = predictionRows.Count(row => row.Bucket == "WRONG_SEMANTIC_FUNCTION"),
                wrongSemanticProposalSourceReview = falsePositiveReviews,
            });

            goldRows.AddRange(predictionRows.Select(row => new
            {
                documentId,
                goldId = row.Heading.Identity,
                goldText = row.Heading.Text,
                goldParts = row.Heading.Parts,
                outcome = row.Bucket,
                exactPredictions = row.Exact.Select(ToArtifactPrediction).ToArray(),
                overlappingPredictions = row.Overlaps.Select(ToArtifactPrediction).ToArray(),
                sameAliasMislocatedPredictions = row.SameAlias.Select(ToArtifactPrediction).ToArray(),
                wrongSemanticFunctionEvidence = row.NonMemberOverlap.Select(ToArtifactPrediction).ToArray(),
            }));
        }

        Assert.Equal(31, executionRows.Count);
        Assert.Equal(139, goldRows.Count);
        Assert.Equal(254, predictions.Values.Sum(document => document.Count));
        Assert.Empty(parserQuarantines);
        Assert.Equal(139, semanticTp + semanticFn);
        Assert.Equal(139, exactTp + exactFn);

        FreezeArtifact.AssertJson(ScoreRoot, "gold-occurrence-score.v1.json", new
        {
            schemaVersion = "v5-p6m-gold-occurrence-score-v1",
            source = new { result = ResultPath, manifest = ManifestPath },
            providerCalls = 0,
            frozenProviderCallsInSourceCohort = 31,
            goldRead = true,
            goldMutation = "NONE",
            runtimeChanged = false,
            scorerAuthority = new
            {
                unit = "document-scoped bound occurrence identity: ordered sourceAlias + exact UTF16 start/end for every part",
                binding = "P6M frozen response reparsed with RequestLocalLocatorRegistry.Parse/Decode and existing SemanticSourcePartBinder",
                semanticDetection = "heading membership follows SemanticFunctionMembershipContractV1: DOCUMENT_IDENTITY or STRUCTURAL_REGION. A Gold occurrence is detected by a heading-member candidate whose actual source spans overlap it. NAVIGATION_REPRESENTATION-only overlap is WRONG_SEMANTIC_FUNCTION; an unmatched heading-member candidate is a wrong semantic proposal only when source review says NON_HEADING.",
                exactOccurrence = "ordered source parts and exact UTF16 spans equal the Gold occurrence",
                sameAliasNonOverlap = "MISLOCATED_SAME_SOURCE_ATOM, not silently called NO_MODEL_PROPOSAL",
                functionTags = "existing heading-membership rule retained: DOCUMENT_IDENTITY or STRUCTURAL_REGION; NAVIGATION_REPRESENTATION-only is non-member. Gold has no finer per-function label axis.",
                sourceReview = review.Provenance,
            },
            execution = new
            {
                packs = executionRows.Count,
                ownedAtoms = 2884,
                requestAndRegistryParityVerified = true,
                parserQuarantineCount = parserQuarantines.Count,
                boundOccurrenceUnits = predictions.Values.Sum(document => document.Count),
                parserReplay = executionRows,
            },
            metrics = new
            {
                goldOccurrences = 139,
                semanticOccurrence = Metric(semanticTp, semanticFp, semanticFn),
                exactOccurrence = Metric(exactTp, exactFp, exactFn),
                predictionUnits = predictions.Values.Sum(document => document.Count),
                headingMemberPredictionUnits = predictions.Values.Sum(document => document.Values.Count(prediction => prediction.IsHeadingMember)),
                semanticTruePositiveGold = semanticTp,
                semanticFalsePositiveReviewedNonHeading = semanticFp,
                semanticFalseNegativeGold = semanticFn,
                exactTruePositiveGold = exactTp,
                exactFalsePositivePredictionUnits = exactFp,
                exactFalseNegativeGold = exactFn,
                functionTagCounts = functionCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                sourceReviewWrongProposalVerdicts = allFalsePositiveReviewVerdicts.Distinct(StringComparer.Ordinal).ToArray(),
            },
            historicalSameUnitBaseline = new
            {
                source = "artifacts/v5-p5q2-hardened-semantic-audit/full-31.audit.v1.json",
                note = "same canonical occurrence Gold and heading-membership/overlap unit; historical V4 cohort, not paired provider execution",
                semanticOccurrence = new { truePositive = 125, falsePositive = 55, falseNegative = 14, f1 = 0.7837 },
                exactOccurrence = new { truePositive = 120, falsePositive = 61, falseNegative = 19, f1 = 0.75 },
            },
            documents,
            goldAdjudications = goldRows,
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<GoldHeading>> ReadGold()
    {
        var result = new Dictionary<string, IReadOnlyList<GoldHeading>>(StringComparer.Ordinal);
        foreach (var documentId in new[] { "SRC-089", "SRC-095" })
        {
            CanonicalGoldRegistry.RequireCapability(documentId, GoldCapability.Occurrence);
            using var gold = CanonicalGoldRegistry.Resolve(documentId);
            result[documentId] = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim => new GoldHeading(
                claim.GetProperty("identity").GetString()!, claim.GetProperty("projectedText").GetString()!,
                claim.GetProperty("boundParts").EnumerateArray().Select(part => new SourcePart(
                    part.GetProperty("sourceAlias").GetString()!, part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray())).ToArray();
        }
        return result;
    }

    private static SourceReviewAuthority ReadSourceReview()
    {
        var verdicts = Src089SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part =>
                (Key: $"SRC-089:{part.SourceAlias}", item.Verdict)))
            .Concat(Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part =>
                (Key: $"SRC-095:{part.SourceAlias}", item.Verdict))))
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(row => row.Verdict).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        return new SourceReviewAuthority("eval/a99-closed-loop/source-review-v1/SRC-089/review-items.json + SRC-095/review-items.json", verdicts);
    }

    private static string PdfFor(string documentId) => documentId switch
    {
        "SRC-089" => SourcePdfCorpus.Src089,
        "SRC-095" => SourcePdfCorpus.Src095,
        _ => throw new InvalidOperationException($"unknown-P6M-document:{documentId}"),
    };

    private static string Identity(string documentId, IReadOnlyList<SourcePart> parts) =>
        $"{documentId}|" + string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));

    private static bool SameParts(IReadOnlyList<SourcePart> left, IReadOnlyList<SourcePart> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private static bool Overlaps(IReadOnlyList<SourcePart> left, IReadOnlyList<SourcePart> right) =>
        left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));

    private static object Metric(int truePositive, int falsePositive, int falseNegative)
    {
        var precision = truePositive + falsePositive == 0 ? 0d : (double)truePositive / (truePositive + falsePositive);
        var recall = truePositive + falseNegative == 0 ? 0d : (double)truePositive / (truePositive + falseNegative);
        return new
        {
            truePositive, falsePositive, falseNegative,
            precision = Math.Round(precision, 4), recall = Math.Round(recall, 4),
            f1 = Math.Round(precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall), 4),
        };
    }

    private static object ToArtifactPrediction(Prediction prediction) => new
    {
        prediction.Identity,
        prediction.Parts,
        prediction.Functions,
        prediction.Text,
        prediction.ParentOrdinal,
    };

    private sealed record SourcePart(string Alias, int Start, int End);
    private sealed record GoldHeading(string Identity, string Text, IReadOnlyList<SourcePart> Parts);
    private sealed record Prediction(string DocumentId, string Identity, IReadOnlyList<SourcePart> Parts,
        IReadOnlyList<string> Functions, string Text, int ParentOrdinal)
    {
        public bool IsHeadingMember => Functions.Contains("DOCUMENT_IDENTITY", StringComparer.Ordinal) ||
            Functions.Contains("STRUCTURAL_REGION", StringComparer.Ordinal);
    }
    private sealed record GoldRow(GoldHeading Heading, IReadOnlyList<Prediction> Exact, IReadOnlyList<Prediction> Overlaps,
        IReadOnlyList<Prediction> SameAlias, IReadOnlyList<Prediction> NonMemberOverlap, string Bucket);

    private sealed record SourceReviewAuthority(string Provenance, IReadOnlyDictionary<string, string> ExplicitVerdicts)
    {
        public string Verdict(string documentId, IEnumerable<string> aliases)
        {
            var direct = aliases.Select(alias => ExplicitVerdicts.GetValueOrDefault($"{documentId}:{alias}"))
                .Where(verdict => verdict is not null).Distinct(StringComparer.Ordinal).ToArray();
            if (direct.Length > 1) throw new InvalidOperationException("source-review-conflicting-verdict");
            return direct.SingleOrDefault() ?? "NON_HEADING";
        }
    }
}
