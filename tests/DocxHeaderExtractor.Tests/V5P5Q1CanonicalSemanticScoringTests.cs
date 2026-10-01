using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free re-adjudication of P5O raw sparse decisions. Unlike P5Q, this never intersects a
/// binder-internal identity with Gold: it recovers request-local ownedIndex evidence back to source
/// aliases and compares that canonical source evidence with Gold's source parts.
/// </summary>
public sealed class V5P5Q1CanonicalSemanticScoringTests
{
    private const string SourceRoot = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private const string Root = "artifacts/v5-p5q1-canonical-semantic-audit";
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

    // These are the source-review negative fixtures supplied with the P5Q.1 review. A new,
    // unmatched proposal is deliberately NOT classified GOLD_MISSING by name or by predicate alone.
    private static readonly string[] ReviewedNonHeadingFragments =
    [
        "THE GOVERNMENT", "THE SOCIALIST REPUBLIC OF VIETNAM", "No. 195/2013", "[QPACK]", "[RFC8174]", "[BREACH]",
        "Table 1:", "Figure 3", "Figure 4", "Figure 5", "Section 7.2.7", "Section 7.2.8"
    ];

    private static readonly string[] RequiredExistingGoldEvidence =
    [
        "DECREE DETAILING", "Chapter I GENERAL PROVISIONS", "Article 1.", "Article 2.", "Article 3.", "Article 4.",
        "11.2.1. Frame Types", "11.2.2. Settings Parameters", "12.2. Informative References",
        "Appendix A. Considerations", "A.1. Streams", "A.2. HTTP Frame Types", "7.1. Frame Layout",
        "7.2. Frame Deﬁnitions", "7.2.1. DATA", "7.2.2. HEADERS", "7.2.3. CANCEL_PUSH"
    ];

    [Fact]
    public void Adjudicate_raw_owned_index_evidence_against_canonical_gold_without_mutating_gold()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{SourceRoot}/result.v1.json")));
        var sourceRows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var packsByDoc = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        var evidenceByDoc = new Dictionary<string, List<ModelEvidence>>(StringComparer.Ordinal)
        {
            ["SRC-089"] = [], ["SRC-095"] = []
        };

        foreach (var spec in Selection)
        {
            if (!packsByDoc.TryGetValue(spec.Doc, out var packs))
            {
                packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(spec.Pdf), spec.Doc, contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                packsByDoc[spec.Doc] = packs;
            }

            var pack = packs.Single(p => p.PackId == spec.Pack);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(spec.Pdf))
                .ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var row = sourceRows.Single(item => item.GetProperty("role").GetString() == spec.Role);
            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var response = V5SemanticSparseDecisionContractV3_1.Parse(raw.RootElement, contract,
                pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);

            foreach (var decision in response.Decisions)
            {
                var claims = decision.Claims.Where(claim => OccurrencePredicates.Contains(claim.Predicate)).ToArray();
                if (claims.Length == 0 || decision.OwnedIndex < 0 || decision.OwnedIndex >= pack.Packet.SubjectEvidence.Count)
                    continue;

                var aliases = new HashSet<string>(StringComparer.Ordinal)
                {
                    pack.Packet.SubjectEvidence[decision.OwnedIndex].SourceAlias
                };
                foreach (var part in claims.SelectMany(claim => claim.AdditionalSubjectParts ?? []))
                    if (part.OwnedIndex >= 0 && part.OwnedIndex < pack.Packet.SubjectEvidence.Count)
                        aliases.Add(pack.Packet.SubjectEvidence[part.OwnedIndex].SourceAlias);

                var primary = atoms[pack.Packet.SubjectEvidence[decision.OwnedIndex].SourceAlias];
                evidenceByDoc[spec.Doc].Add(new ModelEvidence(
                    spec.Role, decision.WireOrdinal, decision.OwnedIndex, aliases.OrderBy(alias => alias, StringComparer.Ordinal).ToArray(),
                    aliases.OrderBy(alias => alias, StringComparer.Ordinal).Select(alias => new SourcePart(alias, 0, atoms[alias].Text.Length)).ToArray(),
                    primary.Text, claims.Select(claim => claim.Predicate).Distinct(StringComparer.Ordinal).OrderBy(predicate => predicate, StringComparer.Ordinal).ToArray(),
                    claims.Select(claim => claim.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()));
            }
        }

        var gold = Gold();
        var headings = new List<object>();
        var semanticTp = 0; var semanticFn = 0; var exact = 0; var multipartExact = 0; var split = 0; var partial = 0; var noEvidence = 0;
        foreach (var documentId in gold.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            foreach (var goldHeading in gold[documentId])
            {
                var matching = evidenceByDoc[documentId]
                    .Where(evidence => evidence.SourceAliases.Intersect(goldHeading.SourceAliases, StringComparer.Ordinal).Any())
                    .ToArray();
                var covered = matching.SelectMany(evidence => evidence.SourceAliases)
                    .Intersect(goldHeading.SourceAliases, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                var fullDecision = matching.FirstOrDefault(evidence => goldHeading.SourceAliases.All(evidence.SourceAliases.Contains));
                var semanticMatch = matching.Length > 0;
                var occurrenceMatch = !semanticMatch ? "NO_MODEL_EVIDENCE"
                    : fullDecision is not null ? goldHeading.SourceAliases.Count == 1 ? "EXACT" : "MULTIPART_EXACT"
                    : covered.SetEquals(goldHeading.SourceAliases) ? "SPLIT_OF_GOLD"
                    : "PARTIAL_OF_GOLD";

                if (semanticMatch) semanticTp++; else semanticFn++;
                switch (occurrenceMatch)
                {
                    case "EXACT": exact++; break;
                    case "MULTIPART_EXACT": multipartExact++; break;
                    case "SPLIT_OF_GOLD": split++; break;
                    case "PARTIAL_OF_GOLD": partial++; break;
                    default: noEvidence++; break;
                }

                headings.Add(new
                {
                    documentId,
                    goldId = goldHeading.Identity,
                    goldText = goldHeading.Text,
                    goldSourceParts = goldHeading.SourceParts,
                    modelEvidence = matching.Select(evidence => new
                    {
                        role = evidence.Role, rawDecisionIndex = evidence.RawDecisionIndex, ownedIndex = evidence.OwnedIndex,
                        sourceParts = evidence.SourceParts, text = evidence.PrimaryText, predicates = evidence.Predicates, values = evidence.Values
                    }).ToArray(),
                    semanticMatch,
                    occurrenceMatch,
                    goldMissing = false,
                });
            }
        }

        var goldAliasUniverse = gold.Values.SelectMany(rows => rows).SelectMany(row => row.SourceAliases).ToHashSet(StringComparer.Ordinal);
        var unmatched = evidenceByDoc.SelectMany(pair => pair.Value.Select(evidence => new { documentId = pair.Key, evidence }))
            .Where(row => !row.evidence.SourceAliases.Any(goldAliasUniverse.Contains))
            .Select(row => new
            {
                row.documentId, role = row.evidence.Role, rawDecisionIndex = row.evidence.RawDecisionIndex,
                ownedIndex = row.evidence.OwnedIndex, sourceParts = row.evidence.SourceParts, text = row.evidence.PrimaryText,
                predicates = row.evidence.Predicates, sourceReview = SourceReview(row.evidence.PrimaryText),
                goldMissing = false,
            }).ToArray();
        var semanticFp = unmatched.Count(row => row.sourceReview == "NON_HEADING");
        var unreviewed = unmatched.Count(row => row.sourceReview == "SOURCE_REVIEW_REQUIRED");

        var headingRows = headings.Cast<dynamic>().ToArray();
        foreach (var required in RequiredExistingGoldEvidence)
        {
            var heading = headingRows.Single(row => ((string)row.goldText).StartsWith(required, StringComparison.Ordinal));
            Assert.True((bool)heading.semanticMatch, $"raw P5O evidence missing for existing Gold heading: {required}");
            Assert.False((bool)heading.goldMissing);
        }
        Assert.All(unmatched.Where(row => row.sourceReview == "NON_HEADING"), row => Assert.False(row.goldMissing));
        Assert.DoesNotContain(unmatched, row => row.goldMissing);

        var precision = semanticTp + semanticFp == 0 ? 1d : (double)semanticTp / (semanticTp + semanticFp);
        var recall = semanticTp + semanticFn == 0 ? 1d : (double)semanticTp / (semanticTp + semanticFn);
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5q1-canonical-semantic-audit-v1",
            source = $"{SourceRoot}/result.v1.json",
            providerCalls = 0,
            goldRead = true,
            goldMutation = "NONE",
            legacyP5Q = new
            {
                status = "INVALID_FOR_SEMANTIC_QUALITY",
                reason = "binder-internal identity was directly intersected with canonical Gold source-part identity",
                score = new { truePositive = 0, falsePositive = 29, falseNegative = 139, f1 = 0 },
            },
            comparisonAuthority = "raw decision ownedIndex -> harness source aliases -> canonical Gold sourceParts",
            semanticHeadingMetric = new
            {
                scope = "all canonical Gold headings in SRC-089 and SRC-095; false positives only where source review confirms NON_HEADING",
                truePositive = semanticTp, falsePositive = semanticFp, falseNegative = semanticFn,
                unreviewedUnmatchedProposals = unreviewed,
                precision = Math.Round(precision, 4), recall = Math.Round(recall, 4),
                f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4),
            },
            occurrenceFidelityMetric = new { exact, multipartExact, splitOfGold = split, partialOfGold = partial, noModelEvidence = noEvidence },
            goldHeadings = headings,
            unmatchedModelProposals = unmatched,
            knownGoldMissingHeadingsFromReviewedP5O = 0,
            goldMissingPolicy = "Only source-review-confirmed headings absent from canonical Gold may enter GOLD_MISSING. Unmatched proposals are never promoted by predicate/name alone.",
            negativeFixtures = ReviewedNonHeadingFragments.Select(text => new { text, classification = "NON_HEADING", goldMissing = false }).ToArray(),
        });
    }

    private static string SourceReview(string text) =>
        string.Equals(text, "Frame", StringComparison.Ordinal) || string.Equals(text, "Section", StringComparison.Ordinal) ||
        ReviewedNonHeadingFragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            ? "NON_HEADING" : "SOURCE_REVIEW_REQUIRED";

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

    private sealed record SourcePart(string Alias, int Start, int End);
    private sealed record GoldHeading(string Identity, string Text, IReadOnlyList<SourcePart> SourceParts)
    {
        public IReadOnlyList<string> SourceAliases => SourceParts.Select(part => part.Alias).ToArray();
    }
    private sealed record ModelEvidence(string Role, int RawDecisionIndex, int OwnedIndex, IReadOnlyList<string> SourceAliases,
        IReadOnlyList<SourcePart> SourceParts, string PrimaryText, IReadOnlyList<string> Predicates, IReadOnlyList<string?> Values);
}
