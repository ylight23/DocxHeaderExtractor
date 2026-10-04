using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free E1Δ audit. It preserves carrier evidence but does not invent segmentation truth without segmentation Gold.</summary>
public sealed class V5P6TE1SegmentationAuditTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string E1Path = "artifacts/v5-p6t-total-occurrence-role/p6te1-unit-topology/result.v1.json";
    private const string ReviewPath = "artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6te1-unit-topology";

    [Fact]
    public void P6TE1Delta_corrects_segmentation_authority_without_heading_scoring()
    {
        using var e1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(E1Path)));
        using var review = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ReviewPath)));
        var src089 = Audit089(e1.RootElement, review.RootElement);
        var src095 = Audit095(e1.RootElement, review.RootElement);
        FreezeArtifact.AssertJson(OutputRoot, "segmentation-audit.v2.json", new
        {
            schemaVersion = "v5-p6te1-segmentation-audit-v2", providerCalls = 0, goldRead = true, goldMutation = "NONE", headingScore = "NOT_COMPUTED", functionScore = "NOT_COMPUTED", extentPass = "BLOCKED", sourceAudit = "READ_ONLY",
            src089, src095,
            conclusion = "E1_CARRIER_LEDGER_PASS; GENERAL_SEGMENTATION_ACCURACY_NOT_EVALUABLE; SEGMENT_GRANULARITY_NOT_AUTHORITATIVE; NO_DOWNSTREAM_FUNCTION_PASS_FROM_E1",
        });
    }

    private static object Audit089(JsonElement e1, JsonElement review)
    {
        var (plan, roles, aliasToOccurrence, _) = Build("SRC-089", SourcePdfCorpus.Src089, e1);
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("eval/a99-closed-loop/gold/SRC-089.gold.json")));
        var reviewedPrimaries = review.GetProperty("src089").GetProperty("goldUnits").EnumerateArray().Select(unit => unit.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var units = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
            .Where(parts => parts.Length > 0 && reviewedPrimaries.Contains(parts[0]))
            .ToArray();
        Assert.Equal(6, units.Length);
        var unitRows = units.Select(parts =>
        {
            var ids = parts.Select(alias => aliasToOccurrence[alias]).ToArray();
            var observed = ids.Select(id => roles[id]).ToArray();
            var expected = parts.Length == 1 ? new[] { "STANDALONE" } : new[] { "STARTS_SEGMENT" }.Concat(Enumerable.Repeat("CONTINUES_PREVIOUS", parts.Length - 1)).ToArray();
            return new { primary = parts[0], partCount = parts.Length, expected, occurrences = ids, observed, exact = observed.SequenceEqual(expected, StringComparer.Ordinal) };
        }).ToArray();
        var l0016 = aliasToOccurrence["L0016:S0"];
        var allContinues = roles.Where(value => value.Value == "CONTINUES_PREVIOUS").Select(value => value.Key).ToArray();
        var potentialFalse = allContinues.Where(id =>
        {
            var index = int.Parse(id[1..]); if (index <= 1) return true;
            var currentAlias = aliasToOccurrence.Single(value => value.Value == id).Key;
            var previousAlias = aliasToOccurrence.Single(value => value.Value == $"O{index - 1}").Key;
            var current = plan.SourceAtoms.Single(atom => atom.Alias == currentAlias);
            var previous = plan.SourceAtoms.Single(atom => atom.Alias == previousAlias);
            return current.Ordinal != previous.Ordinal + 1;
        }).ToArray();
        return new
        {
            reviewedUnits = unitRows.Length,
            reviewedMultipartUnits = unitRows.Count(value => value.partCount > 1),
            reviewedSingletonUnits = unitRows.Count(value => value.partCount == 1),
            unitRows,
            segmentationGoldAvailable = false,
            headingConditionedBoundaryAgreement = new
            {
                exactSegment = unitRows.Count(value => value.exact),
                disagree = unitRows.Count(value => !value.exact),
                wrongStart = unitRows.Where(value => value.partCount > 1).Count(value => value.observed[0] != "STARTS_SEGMENT"),
                wrongContinuation = unitRows.Where(value => value.partCount > 1).Sum(value => value.observed.Skip(1).Count(role => role != "CONTINUES_PREVIOUS")),
                singletonWrongBoundary = unitRows.Where(value => value.partCount == 1).Count(value => !value.exact),
                classification = "HEADING_CONDITIONED_COMPARISON_NOT_GENERAL_SEGMENTATION_ACCURACY"
            },
            singleOccurrenceGold = new { alias = "L0016:S0", occurrence = l0016, observed = roles[l0016], exact = roles[l0016] == "STANDALONE" },
            outputDistribution = roles.GroupBy(value => value.Value).OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count()),
            continuationAudit = new { total = allContinues.Length, nonAdjacentSourceOrder = potentialFalse.Length, classification = "SOURCE_ORDER_ONLY; DOES_NOT_DETERMINE_LOGICAL_SEGMENT_MEMBERSHIP" },
        };
    }

    private static object Audit095(JsonElement e1, JsonElement review)
    {
        var (_, roles, aliasToOccurrence, _) = Build("SRC-095", SourcePdfCorpus.Src095, e1);
        var tocRows = review.GetProperty("src095").GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(53, tocRows.Length);
        var rows = new List<(string CandidateId, string Occurrence, string Alias, string Convention, string Observed, string Interpretation, bool AgreesWithConvention)>();
        foreach (var row in tocRows)
        {
            var candidateId = row.GetProperty("CandidateId").GetString()!; var occurrence = row.GetProperty("occurrence").GetString()!;
            var alias = aliasToOccurrence.Single(value => value.Value == occurrence).Key;
            Assert.EndsWith(":S0", alias, StringComparison.Ordinal);
            rows.Add((candidateId, occurrence, alias, "ONE_REVIEWED_TOC_ATOM_PER_SEGMENT", roles[occurrence], roles[occurrence] == "STANDALONE" ? "SINGLE_ATOM_SEGMENT" : roles[occurrence], roles[occurrence] == "STANDALONE"));
        }
        return new
        {
            reviewedTocEntries = rows.Count,
            segmentationGoldAvailable = false,
            segmentationAccuracy = "NOT_EVALUABLE",
            sourceReviewedShape = "ONE_S0_ATOM_PER_REVIEWED_TOC_ENTRY",
            auditConvention = "ONE_REVIEWED_TOC_ATOM_PER_SEGMENT",
            agreementWithConvention = new { STANDALONE = rows.Count(value => value.Observed == "STANDALONE") },
            disagreementWithConvention = new { CONTINUES_PREVIOUS = rows.Count(value => value.Observed == "CONTINUES_PREVIOUS"), STARTS_SEGMENT = rows.Count(value => value.Observed == "STARTS_SEGMENT") },
            rows = rows.Select(value => new { candidateId = value.CandidateId, occurrence = value.Occurrence, alias = value.Alias, convention = value.Convention, observed = value.Observed, agreesWithConvention = value.AgreesWithConvention, segmentationInterpretation = value.Interpretation }).ToArray()
        };
    }

    private static (PdfCandidateAuthorityDocumentPlan Plan, Dictionary<string, string> Roles, Dictionary<string, string> AliasToOccurrence, Dictionary<string, int> OrdinalByAlias) Build(string id, string source, JsonElement e1)
    {
        var hash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source)); var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{hash}.json"), id); var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var orderedAliases = pack.OwnedAliases.Select(alias => plan.SourceAtoms.Single(atom => atom.Alias == alias)).OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).Select(atom => atom.Alias).ToArray(); var aliasToOccurrence = orderedAliases.Select((alias, index) => new { alias, id = $"O{index + 1}" }).ToDictionary(value => value.alias, value => value.id, StringComparer.Ordinal); var ordinalByAlias = plan.SourceAtoms.ToDictionary(value => value.Alias, value => value.Ordinal, StringComparer.Ordinal);
        var row = e1.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == id); using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!); var roles = raw.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(value => value.GetProperty("occurrence").GetString()!, value => value.GetProperty("topology").GetString()!, StringComparer.Ordinal);
        Assert.Equal(96, roles.Count); return (plan, roles, aliasToOccurrence, ordinalByAlias);
    }
}
