using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5Q.2 is the document-scoped, span-aware successor to P5Q.1. It evaluates frozen P5O raw
/// decisions only: providerCalls=0, Gold is read but never changed, and runtime/binder behavior is
/// not modified. Semantic recognition is adjudicated on explicit source-backed candidate units;
/// occurrence fidelity goes through the production V3.1 sparse contract, never the generic binder.
/// </summary>
public sealed class V5P5Q2HardenedSemanticScoringTests
{
    private const string SourceRoot = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private const string Root = "artifacts/v5-p5q2-hardened-semantic-audit";
    private static readonly HashSet<string> OccurrencePredicates = new(StringComparer.Ordinal)
    {
        "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"
    };

    private static readonly (string Role, string Doc, string Pdf, string Pack)[] Selection =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Score_document_scoped_raw_evidence_with_actual_source_spans_and_source_review_verdicts()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{SourceRoot}/result.v1.json")));
        var sourceRows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var predictions = new Dictionary<string, List<RawPrediction>>(StringComparer.Ordinal) { ["SRC-089"] = [], ["SRC-095"] = [] };
        var productionUnits = new Dictionary<string, List<CanonicalPredictionUnit>>(StringComparer.Ordinal) { ["SRC-089"] = [], ["SRC-095"] = [] };
        var packsByDoc = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);

        foreach (var spec in Selection)
        {
            if (!packsByDoc.TryGetValue(spec.Doc, out var packs))
            {
                packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(spec.Pdf), spec.Doc, contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                packsByDoc[spec.Doc] = packs;
            }

            var pack = packs.Single(p => p.PackId == spec.Pack);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(spec.Pdf));
            var row = sourceRows.Single(item => item.GetProperty("role").GetString() == spec.Role);
            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var response = V5SemanticSparseDecisionContractV3_1.Parse(raw.RootElement, contract,
                pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);

            var scope = ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases);
            var requestHash = row.GetProperty("semanticRequestHash").GetString()!;
            var productionBinding = V5SemanticSparseDecisionContractV3_1.Bind(requestHash, response, contract,
                pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, atoms, scope);
            var productionProposalKeys = response.Decisions.OrderBy(decision => decision.OwnedIndex)
                .SelectMany(decision => decision.Claims.Select((_, claimIndex) => (decision.WireOrdinal, claimIndex)))
                .Select((claim, ordinal) => (claim.WireOrdinal, claim.claimIndex, Key: $"proposal-{ordinal + 1}"))
                .ToDictionary(item => (item.WireOrdinal, item.claimIndex), item => item.Key);
            productionUnits[spec.Doc].AddRange(productionBinding.Bound.Select(bound =>
            {
                var parts = bound.Claim.Subject.Parts.Select(part => new SourcePart(part.Alias, part.Start, part.End)).ToArray();
                return new CanonicalPredictionUnit(string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}")), parts,
                    [bound.Claim.Predicate]);
            }));
            foreach (var decision in response.Decisions)
            {
                if (decision.OwnedIndex < 0 || decision.OwnedIndex >= pack.Packet.SubjectEvidence.Count) continue;
                foreach (var (claim, claimIndex) in decision.Claims.Select((claim, index) => (claim, index)).Where(item => OccurrencePredicates.Contains(item.claim.Predicate)))
                {
                    var proposedParts = PartsFor(claim, decision.OwnedIndex, pack);
                    // This key is V3.1's durable original-owned-ordinal claim key. The full raw
                    // response above is bound once through production; do not substitute the
                    // generic SemanticSourcePartBinder here.
                    var proposalKey = productionProposalKeys[(decision.WireOrdinal, claimIndex)];
                    var primaryAlias = pack.Packet.SubjectEvidence[decision.OwnedIndex].SourceAlias;
                    predictions[spec.Doc].Add(new RawPrediction(
                        spec.Role, decision.WireOrdinal, decision.OwnedIndex, primaryAlias, atoms.Single(atom => atom.Alias == primaryAlias).Text,
                        claim.Predicate, claim.Value, proposedParts, [], null,
                        productionBinding.Refusals.GetValueOrDefault(proposalKey)));
                }
            }
        }

        var canonicalUnits = productionUnits.ToDictionary(pair => pair.Key, pair => pair.Value
            .GroupBy(unit => unit.Identity, StringComparer.Ordinal).Select(group => group.First()).ToArray(), StringComparer.Ordinal);
        var gold = Gold();
        var goldAliasesByDoc = gold.ToDictionary(pair => pair.Key,
            pair => pair.Value.SelectMany(heading => heading.Parts).Select(part => part.Alias).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var goldRows = new List<GoldAdjudication>();

        foreach (var documentId in gold.Keys.OrderBy(value => value, StringComparer.Ordinal))
        foreach (var heading in gold[documentId])
        {
            var rawEvidence = predictions[documentId].Where(prediction => prediction.SourceAliases.Intersect(heading.SourceAliases, StringComparer.Ordinal).Any()).ToArray();
            var exactUnits = canonicalUnits[documentId].Where(unit => SameParts(unit.Parts, heading.Parts)).ToArray();
            var exactPartCoverage = canonicalUnits[documentId].SelectMany(unit => unit.Parts)
                .Where(part => heading.Parts.Contains(part)).ToHashSet();
            var semanticMatch = rawEvidence.Length > 0;
            var occurrenceMatch = !semanticMatch ? "NO_MODEL_EVIDENCE"
                : exactUnits.Length > 0 ? heading.Parts.Count == 1 ? "EXACT" : "MULTIPART_EXACT"
                : exactPartCoverage.SetEquals(heading.Parts) ? "SPLIT_OF_GOLD"
                : rawEvidence.Any(prediction => prediction.BindingStatus == "whole-atom-must-omit-verbatim-text") ? "CONTRACT_REFUSED"
                : rawEvidence.Any(prediction => prediction.BindingStatus is not null) ? "BINDING_MISMATCH"
                : "PARTIAL_OF_GOLD";
            goldRows.Add(new GoldAdjudication(documentId, heading.Identity, heading.Text, heading.Parts, rawEvidence,
                exactUnits, semanticMatch, occurrenceMatch));
        }

        var review = SourceReview();
        var unmatched = predictions.SelectMany(pair => pair.Value.Select(prediction => new { DocumentId = pair.Key, Prediction = prediction }))
            .Where(row => !row.Prediction.SourceAliases.Any(alias => goldAliasesByDoc[row.DocumentId].Contains(alias)))
            .Select(row => new UnmatchedProposal(row.DocumentId, row.Prediction, review.Verdict(row.DocumentId, row.Prediction.SourceAliases)))
            .ToArray();
        Assert.All(unmatched, item => Assert.Equal("NON_HEADING", item.SourceReviewVerdict));

        // A semantic adjudication unit is exactly one source-backed candidate: every canonical
        // Gold heading is a positive unit, while every document-scoped unmatched model decision
        // is a reviewed non-heading unit. TP/FP/FN are all derived from this one universe.
        var unmatchedUnits = unmatched.GroupBy(item => $"{item.DocumentId}:{item.Prediction.Role}:{item.Prediction.RawDecisionIndex}:{item.Prediction.OwnedIndex}", StringComparer.Ordinal)
            .Select(group => new SemanticAdjudicationUnit(
                $"RAW:{group.Key}", group.First().DocumentId, "RAW_MODEL_DECISION", "NON_HEADING", "PROPOSED", group.First().SourceReviewVerdict))
            .ToArray();
        var semanticUnits = goldRows.Select(row => new SemanticAdjudicationUnit(
                $"GOLD:{row.DocumentId}:{row.GoldId}", row.DocumentId, "CANONICAL_GOLD_HEADING", "HEADING",
                row.SemanticMatch ? "DETECTED" : "NO_MODEL_EVIDENCE", null))
            .Concat(unmatchedUnits).ToArray();
        var semanticTp = semanticUnits.Count(unit => unit.Truth == "HEADING" && unit.Outcome == "DETECTED");
        var semanticFn = semanticUnits.Count(unit => unit.Truth == "HEADING" && unit.Outcome == "NO_MODEL_EVIDENCE");
        var semanticFp = semanticUnits.Count(unit => unit.Truth == "NON_HEADING" && unit.Outcome == "PROPOSED");
        var precision = (double)semanticTp / (semanticTp + semanticFp);
        var recall = (double)semanticTp / (semanticTp + semanticFn);
        var fidelity = goldRows.GroupBy(row => row.OccurrenceMatch).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // These assertions prevent regression to P5Q.1's alias-only conclusion: the raw DECREE
        // evidence maps to the heading semantically, but its actual L0008 span is broader than Gold.
        var decree = goldRows.Single(row => row.DocumentId == "SRC-089" && row.GoldText.StartsWith("DECREE DETAILING", StringComparison.Ordinal));
        Assert.True(decree.SemanticMatch);
        Assert.Equal("PARTIAL_OF_GOLD", decree.OccurrenceMatch);
        var l1472 = goldRows.Single(row => row.DocumentId == "SRC-095" && row.GoldText == "11.2.1. Frame Types");
        Assert.True(l1472.SemanticMatch);
        Assert.Equal("CONTRACT_REFUSED", l1472.OccurrenceMatch);
        var chapter = goldRows.Single(row => row.DocumentId == "SRC-089" && row.GoldText == "Chapter I GENERAL PROVISIONS");
        Assert.Equal("SPLIT_OF_GOLD", chapter.OccurrenceMatch);
        Assert.DoesNotContain(unmatched, item => item.SourceReviewVerdict != "NON_HEADING");

        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5q2-hardened-semantic-audit-v2",
            source = $"{SourceRoot}/result.v1.json",
            providerCalls = 0,
            goldRead = true,
            goldMutation = "NONE",
            comparisonAuthority = "documentId + raw ownedIndex -> harness source parts/spans -> canonical Gold source parts/spans",
            hardening = new
            {
                documentScopedCanonicalIdentity = true,
                actualSourcePartSpanComparison = true,
                sourceReviewReused = review.Provenance,
                canonicalPredictionUnits = canonicalUnits.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal),
            },
            semanticAdjudicationUnit = new
            {
                definition = "One document-scoped, source-backed candidate: canonical Gold headings are positive units; unmatched raw model decisions are reviewed non-heading units.",
                total = semanticUnits.Length,
                goldHeadingUnits = goldRows.Count,
                reviewedNonHeadingProposalUnits = unmatchedUnits.Length,
                units = semanticUnits,
            },
            semanticHeadingMetric = new
            {
                truePositive = semanticTp, falsePositive = semanticFp, falseNegative = semanticFn,
                precision = Math.Round(precision, 4), recall = Math.Round(recall, 4),
                f1 = Math.Round(2 * precision * recall / (precision + recall), 4),
                sourceReviewRequired = 0,
            },
            occurrenceFidelityMetric = new
            {
                exact = fidelity.GetValueOrDefault("EXACT"), multipartExact = fidelity.GetValueOrDefault("MULTIPART_EXACT"),
                splitOfGold = fidelity.GetValueOrDefault("SPLIT_OF_GOLD"), partialOfGold = fidelity.GetValueOrDefault("PARTIAL_OF_GOLD"),
                contractRefused = fidelity.GetValueOrDefault("CONTRACT_REFUSED"),
                bindingMismatch = fidelity.GetValueOrDefault("BINDING_MISMATCH"), noModelEvidence = fidelity.GetValueOrDefault("NO_MODEL_EVIDENCE"),
            },
            goldHeadings = goldRows.Select(row => new
            {
                documentId = row.DocumentId, goldId = row.GoldId, goldText = row.GoldText, goldSourceParts = row.GoldParts,
                modelEvidence = row.RawEvidence.Select(prediction => new
                {
                    role = prediction.Role, rawDecisionIndex = prediction.RawDecisionIndex, ownedIndex = prediction.OwnedIndex,
                    predicate = prediction.Predicate, value = prediction.Value, requestedSourceParts = prediction.RequestedParts,
                    resolvedSourceParts = prediction.BoundParts, canonicalIdentity = prediction.Identity, bindingStatus = prediction.BindingStatus,
                }).ToArray(),
                canonicalPredictionUnits = row.ExactUnits.Select(unit => new { unit.Identity, sourceParts = unit.Parts, sources = unit.Sources }).ToArray(),
                semanticMatch = row.SemanticMatch, occurrenceMatch = row.OccurrenceMatch,
                productionUsable = row.OccurrenceMatch is "EXACT" or "MULTIPART_EXACT" or "SPLIT_OF_GOLD" or "PARTIAL_OF_GOLD", goldMissing = false,
            }).ToArray(),
            unmatchedModelProposals = unmatched.Select(item => new
            {
                documentId = item.DocumentId, role = item.Prediction.Role, rawDecisionIndex = item.Prediction.RawDecisionIndex,
                ownedIndex = item.Prediction.OwnedIndex, sourceParts = item.Prediction.BoundParts, text = item.Prediction.PrimaryText,
                predicate = item.Prediction.Predicate, sourceReview = item.SourceReviewVerdict, goldMissing = false,
            }).ToArray(),
            knownGoldMissingHeadingsFromReviewedP5O = 0,
        });
    }

    private static IReadOnlyList<SemanticSourcePart> PartsFor(V5SemanticDecisionClaimV3 claim, int ownedIndex, V5PackedDecisionRequestV3 pack)
    {
        var parts = new List<SemanticSourcePart> { Part(pack.Packet.SubjectEvidence[ownedIndex].SourceAlias, claim.SubjectSelection) };
        foreach (var additional in claim.AdditionalSubjectParts ?? [])
            if (additional.OwnedIndex >= 0 && additional.OwnedIndex < pack.Packet.SubjectEvidence.Count)
                parts.Add(Part(pack.Packet.SubjectEvidence[additional.OwnedIndex].SourceAlias, additional.Selection));
        return parts;
    }

    private static SemanticSourcePart Part(string alias, V5DecisionTextSelectionV3? selection) => new(alias,
        selection?.VerbatimText is null ? CanonicalSemanticSelectionMode.WholeAlias : CanonicalSemanticSelectionMode.VerbatimText,
        selection?.VerbatimText, selection?.Occurrence, selection?.LeftExactContext, selection?.RightExactContext);

    private static bool SameParts(IReadOnlyList<SourcePart> left, IReadOnlyList<SourcePart> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private static Dictionary<string, IReadOnlyList<GoldHeading>> Gold()
    {
        var result = new Dictionary<string, IReadOnlyList<GoldHeading>>(StringComparer.Ordinal);
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence);
            using var document = CanonicalGoldRegistry.Resolve(id);
            result[id] = document.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim => new GoldHeading(
                claim.GetProperty("identity").GetString()!, claim.GetProperty("projectedText").GetString()!,
                claim.GetProperty("boundParts").EnumerateArray().Select(part => new SourcePart(
                    part.GetProperty("sourceAlias").GetString()!, part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray())).ToArray();
        }
        return result;
    }

    private static SourceReviewAuthority SourceReview()
    {
        var verdicts = Src089SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part =>
                (Key: $"SRC-089:{part.SourceAlias}", item.Verdict)))
            .Concat(Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part =>
                (Key: $"SRC-095:{part.SourceAlias}", item.Verdict))))
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Verdict).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        return new SourceReviewAuthority(
            "eval/a99-closed-loop/source-review-v1/SRC-089/review-items.json + SRC-095/review-items.json",
            "Both source reviews declare every atom not classified as a heading to be NON_HEADING; all raw unmatched P5O proposals resolve to that reviewed non-heading universe.",
            verdicts);
    }

    private sealed record SourceReviewAuthority(string Provenance, string Rule, IReadOnlyDictionary<string, string> ExplicitVerdicts)
    {
        public string Verdict(string documentId, IEnumerable<string> aliases)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
            ArgumentNullException.ThrowIfNull(aliases);
            var direct = aliases.Select(alias => ExplicitVerdicts.GetValueOrDefault($"{documentId}:{alias}"))
                .Where(verdict => verdict is not null).Distinct(StringComparer.Ordinal).ToArray();
            if (direct.Length > 1) throw new InvalidOperationException("source-review-conflicting-verdict");
            return direct.SingleOrDefault() ?? "NON_HEADING";
        }
    }

    private sealed record SourcePart(string Alias, int Start, int End);
    private sealed record GoldHeading(string Identity, string Text, IReadOnlyList<SourcePart> Parts)
    {
        public IReadOnlyList<string> SourceAliases => Parts.Select(part => part.Alias).ToArray();
    }
    private sealed record RawPrediction(string Role, int RawDecisionIndex, int OwnedIndex, string PrimaryAlias, string PrimaryText, string Predicate,
        string? Value, IReadOnlyList<SemanticSourcePart> RequestedParts, IReadOnlyList<SourcePart> BoundParts, string? Identity,
        string? BindingStatus)
    {
        public IReadOnlyList<string> SourceAliases => RequestedParts.Select(part => part.SourceAlias).ToArray();
    }
    private sealed record CanonicalPredictionUnit(string Identity, IReadOnlyList<SourcePart> Parts, IReadOnlyList<string> Sources);
    private sealed record GoldAdjudication(string DocumentId, string GoldId, string GoldText, IReadOnlyList<SourcePart> GoldParts,
        IReadOnlyList<RawPrediction> RawEvidence, IReadOnlyList<CanonicalPredictionUnit> ExactUnits, bool SemanticMatch, string OccurrenceMatch);
    private sealed record UnmatchedProposal(string DocumentId, RawPrediction Prediction, string SourceReviewVerdict);
    private sealed record SemanticAdjudicationUnit(string UnitId, string DocumentId, string Origin, string Truth, string Outcome,
        string? SourceReviewVerdict);
}
