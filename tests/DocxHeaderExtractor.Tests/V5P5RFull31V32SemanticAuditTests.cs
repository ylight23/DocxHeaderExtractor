using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Phase D/E: Gold is read only after the V3.2 execution and contract artifacts are frozen.</summary>
public sealed class V5P5RFull31V32SemanticAuditTests
{
    private const string Root = "artifacts/v5-full31-v32";
    private static readonly HashSet<string> HeadingPredicates = new(StringComparer.Ordinal)
    {
        "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"
    };

    [Fact]
    public void Audit_v32_bound_claims_against_gold_without_claiming_a_full31_score_after_transport_loss()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/contract-audit.v1.json")));
        var rows = contract.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, rows.Length);
        var candidates = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal) { ["SRC-089"] = [], ["SRC-095"] = [] };
        var unavailableOwners = new HashSet<(string DocumentId, int ParentOrdinal)>();
        foreach (var row in rows)
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var parentOrdinal = row.GetProperty("parentOrdinal").GetInt32();
            if (row.GetProperty("parser").GetString() != "PARSER_VALID")
            {
                unavailableOwners.Add((documentId, parentOrdinal));
                continue;
            }
            foreach (var bound in row.GetProperty("bound").EnumerateArray())
            {
                var parts = bound.GetProperty("sourceParts").EnumerateArray().Select(part => new Part(
                    part.GetProperty("Alias").GetString()!, part.GetProperty("Start").GetInt32(), part.GetProperty("End").GetInt32())).ToArray();
                candidates[documentId].Add(new Candidate(documentId, parentOrdinal,
                    bound.GetProperty("predicate").GetString()!, parts));
            }
        }

        var ownership = OwnerByAlias();
        var gold = Gold();
        var review = Review();
        var goldRows = new List<object>();
        var evaluable = new List<GoldHeading>();
        var exactTp = 0; var semanticTp = 0;
        foreach (var (documentId, headings) in gold)
        foreach (var heading in headings)
        {
            var ownerUnavailable = heading.Parts.Any(part => unavailableOwners.Contains((documentId, ownership[(documentId, part.Alias)])));
            var matching = candidates[documentId].Where(candidate => candidate.IsHeading && Overlaps(candidate.Parts, heading.Parts)).ToArray();
            var exact = matching.Any(candidate => SameParts(candidate.Parts, heading.Parts));
            var partial = !exact && matching.Length > 0;
            var bucket = ownerUnavailable ? "TRANSPORT_OR_PARSER_NOT_EVALUABLE"
                : exact ? "EXACT" : partial ? "PARTIAL_OF_GOLD" : "NO_MODEL_PROPOSAL";
            if (!ownerUnavailable)
            {
                evaluable.Add(heading);
                if (exact) exactTp++;
                if (exact || partial) semanticTp++;
            }
            goldRows.Add(new
            {
                documentId, goldId = heading.Identity, goldText = heading.Text, goldSourceParts = heading.Parts,
                occurrenceMatch = bucket, exact, partial, ownerUnavailable,
                productionBoundEvidence = matching.Select(candidate => new { candidate.ParentOrdinal, predicate = candidate.Predicate, sourceParts = candidate.Parts }).ToArray(),
            });
        }

        var allCandidates = candidates.SelectMany(pair => pair.Value).GroupBy(candidate => candidate.Identity, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var headingCandidates = allCandidates.Where(candidate => candidate.IsHeading).ToArray();
        var semanticFpCandidates = headingCandidates.Where(candidate => !gold[candidate.DocumentId].Any(heading => Overlaps(candidate.Parts, heading.Parts))).ToArray();
        Assert.All(semanticFpCandidates, candidate => Assert.Equal("NON_HEADING", review.Verdict(candidate.DocumentId, candidate.Parts.Select(part => part.Alias))));

        var exactFp = headingCandidates.Count(candidate => !gold[candidate.DocumentId].Any(heading => SameParts(candidate.Parts, heading.Parts)));
        var exactFn = evaluable.Count - exactTp;
        var semanticFn = evaluable.Count - semanticTp;
        var notEvaluableGold = goldRows.Count(row => (bool)row.GetType().GetProperty("ownerUnavailable")!.GetValue(row)!);
        Assert.Equal(139, goldRows.Count);
        Assert.True(notEvaluableGold > 0);

        FreezeArtifact.AssertJson(Root, "semantic-audit.v1.json", new
        {
            schemaVersion = "v5-p5r-v32-full31-semantic-audit-v1",
            sourceExecution = $"{Root}/execution-result.v1.json",
            sourceContractAudit = $"{Root}/contract-audit.v1.json",
            providerCalls = 0,
            goldRead = true,
            goldMutation = "NONE",
            status = "FULL31_METRIC_NOT_EVALUABLE_INCOMPLETE_TRANSPORT",
            reason = "Eight provider attempts were upstream 429 transport failures and one stop response was parser-invalid; those parent ownership sets have no accepted V3.2 claim output.",
            authority = new
            {
                binding = "V5SemanticSparseDecisionContractV3_1.Bind output only",
                gold = "canonical occurrence Gold: SRC-089=36, SRC-095=103",
                identity = "document-scoped exact source alias/span identity",
                sourceReview = review.Provenance,
            },
            coverage = new
            {
                goldHeadings = 139,
                evaluableGoldHeadings = evaluable.Count,
                notEvaluableGoldHeadings = notEvaluableGold,
                parserValidLeaves = 22,
                unavailableParentPacks = unavailableOwners.Count,
            },
            observedParserValidLeafMetrics = new
            {
                exactOccurrence = Metric(exactTp, exactFp, exactFn),
                semanticHeading = Metric(semanticTp, semanticFpCandidates.Length, semanticFn),
                note = "These metrics cover only Gold headings whose owning parent had parser-valid V3.2 output. They are not comparable to the full 31-pack V4/P05 baseline.",
            },
            goldHeadings = goldRows,
            observedSemanticFalsePositives = semanticFpCandidates.Select(candidate => new
            {
                candidate.DocumentId, candidate.ParentOrdinal, predicate = candidate.Predicate, sourceParts = candidate.Parts,
                sourceReview = review.Verdict(candidate.DocumentId, candidate.Parts.Select(part => part.Alias)),
            }).ToArray(),
        });

        FreezeArtifact.AssertJson(Root, "comparison-v4-p05.v1.json", new
        {
            schemaVersion = "v5-full31-v32-comparison-v4-p05-v2",
            baselineCommit = "083d025",
            baseline = new
            {
                parserInvalidLeaves = 0, boundClaims = "NOT_REPORTED_AS_COMPARABLE", binderRefusals = 47,
                exact = new { tp = 120, fp = 61, fn = 19, f1 = 0.7500 },
                semantic = new { tp = 125, fp = 55, fn = 14, f1 = 0.7837 },
            },
            v32 = new
            {
                attemptedParentPacks = 31, transportValidLeaves = 23, parserValidLeaves = 22, parserInvalidLeaves = 1,
                boundClaims = 155, binderRefusals = 300,
                exact = "NOT_EVALUABLE_FULL31", semantic = "NOT_EVALUABLE_FULL31",
                observedParserValidLeafMetricsOnly = true,
            },
            verdicts = new
            {
                transport = "REGRESSED: 8/31 upstream 429 failures under zero-retry authorization",
                parser = "REGRESSED: 1 stop response exceeded the frozen V3.2 byte bound",
                binder = "NOT_COMPARABLE: V4 used a different response protocol and V3.2 had 9 unavailable parent sets",
                wholeAtom = "NOT_FIXED_IN_OBSERVED_V3.2_OUTPUT: 205 whole-atom-must-omit-verbatim-text refusals",
                unicode = "NOT_EVALUABLE_SEPARATELY: no TextNotInAtom refusal was emitted in this partial execution",
                ownership = "NOT_FIXED_IN_OBSERVED_V3.2_OUTPUT: 37 ownership/index-related refusals",
                semanticQuality = "NOT_EVALUABLE_FULL31",
            },
        });
    }

    private static Dictionary<(string DocumentId, string Alias), int> OwnerByAlias()
    {
        var result = new Dictionary<(string, string), int>();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        foreach (var document in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Item2), document.Item1, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
            for (var index = 0; index < packs.Count; index++)
                foreach (var alias in packs[index].OwnedAliases) result.Add((document.Item1, alias), index + 1);
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<GoldHeading>> Gold()
    {
        var result = new Dictionary<string, IReadOnlyList<GoldHeading>>(StringComparer.Ordinal);
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence);
            using var document = CanonicalGoldRegistry.Resolve(id);
            result[id] = document.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim => new GoldHeading(
                claim.GetProperty("identity").GetString()!, claim.GetProperty("projectedText").GetString()!,
                claim.GetProperty("boundParts").EnumerateArray().Select(part => new Part(part.GetProperty("sourceAlias").GetString()!,
                    part.GetProperty("utf16Span").GetProperty("start").GetInt32(), part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray())).ToArray();
        }
        return result;
    }

    private static ReviewAuthority Review()
    {
        var verdicts = Src089SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => ($"SRC-089:{part.SourceAlias}", item.Verdict)))
            .Concat(Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => ($"SRC-095:{part.SourceAlias}", item.Verdict))))
            .GroupBy(row => row.Item1, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(row => row.Verdict).Distinct().Single(), StringComparer.Ordinal);
        return new ReviewAuthority("eval/a99-closed-loop/source-review-v1/SRC-089/review-items.json + SRC-095/review-items.json", verdicts);
    }

    private static bool SameParts(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
    private static bool Overlaps(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static object Metric(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(precision, 4), recall = Math.Round(recall, 4), f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4) };
    }

    private sealed record Part(string Alias, int Start, int End);
    private sealed record Candidate(string DocumentId, int ParentOrdinal, string Predicate, IReadOnlyList<Part> Parts)
    {
        public bool IsHeading => HeadingPredicates.Contains(Predicate);
        public string Identity => string.Join("|", Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    }
    private sealed record GoldHeading(string Identity, string Text, IReadOnlyList<Part> Parts);
    private sealed record ReviewAuthority(string Provenance, IReadOnlyDictionary<string, string> Verdicts)
    {
        public string Verdict(string documentId, IEnumerable<string> aliases) => aliases.Select(alias => Verdicts.GetValueOrDefault($"{documentId}:{alias}"))
            .FirstOrDefault(verdict => verdict is not null) ?? "NON_HEADING";
    }
}
