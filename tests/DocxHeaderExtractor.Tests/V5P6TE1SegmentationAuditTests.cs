using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free E1Δ audit: segmentation boundaries only, never heading precision/recall.</summary>
public sealed class V5P6TE1SegmentationAuditTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string E1Path = "artifacts/v5-p6t-total-occurrence-role/p6te1-unit-topology/result.v1.json";
    private const string ReviewPath = "artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6te1-unit-topology";

    [Fact]
    public void P6TE1Delta_freezes_boundary_exactness_without_heading_scoring()
    {
        using var e1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(E1Path)));
        using var review = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ReviewPath)));
        var src089 = Audit089(e1.RootElement, review.RootElement);
        var src095 = Audit095(e1.RootElement, review.RootElement);
        FreezeArtifact.AssertJson(OutputRoot, "segmentation-audit.v1.json", new
        {
            schemaVersion = "v5-p6te1-segmentation-audit-v1", providerCalls = 0, goldRead = true, goldMutation = "NONE", headingScore = "NOT_COMPUTED", functionScore = "NOT_COMPUTED", extentPass = "BLOCKED", sourceAudit = "READ_ONLY",
            src089, src095,
            conclusion = "E1_BOUNDARY_EXACTNESS_AUDIT_ONLY; E2_FUNCTION_AND_E3_EXTENT_REMAIN_BLOCKED",
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
            goldMultipartUnits = unitRows.Length, unitRows,
            boundaryExactness = new
            {
                exactSegment = unitRows.Count(value => value.exact),
                overMerged = unitRows.Where(value => value.partCount > 1).Count(value => value.observed[0] == "CONTINUES_PREVIOUS"),
                underSplit = unitRows.Where(value => value.partCount > 1).Sum(value => value.observed.Skip(1).Count(role => role == "STARTS_SEGMENT" || role == "STANDALONE")),
                wrongStart = unitRows.Where(value => value.partCount > 1).Count(value => value.observed[0] != "STARTS_SEGMENT"),
                wrongContinuation = unitRows.Where(value => value.partCount > 1).Sum(value => value.observed.Skip(1).Count(role => role != "CONTINUES_PREVIOUS")),
                singletonWrongBoundary = unitRows.Where(value => value.partCount == 1).Count(value => !value.exact),
                classification = "SOURCE_REVIEWED_PACK_001_UNITS"
            },
            singleOccurrenceGold = new { alias = "L0016:S0", occurrence = l0016, observed = roles[l0016], exact = roles[l0016] == "STANDALONE" },
            outputDistribution = roles.GroupBy(value => value.Value).OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count()),
            continuationAudit = new { total = allContinues.Length, potentialNonAdjacentSourceOrder = potentialFalse.Length, classification = "POTENTIAL_FALSE_CONTINUATION_REQUIRES_SOURCE_REVIEW" },
        };
    }

    private static object Audit095(JsonElement e1, JsonElement review)
    {
        var (_, roles, aliasToOccurrence, _) = Build("SRC-095", SourcePdfCorpus.Src095, e1);
        var tocRows = review.GetProperty("src095").GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(53, tocRows.Length);
        var rows = new List<(string CandidateId, string Occurrence, string Alias, string Expected, string Observed, string Interpretation, bool Exact)>();
        foreach (var row in tocRows)
        {
            var candidateId = row.GetProperty("CandidateId").GetString()!; var occurrence = row.GetProperty("occurrence").GetString()!;
            var alias = aliasToOccurrence.Single(value => value.Value == occurrence).Key;
            Assert.EndsWith(":S0", alias, StringComparison.Ordinal);
            rows.Add((candidateId, occurrence, alias, "STANDALONE", roles[occurrence], roles[occurrence] == "STANDALONE" ? "SINGLE_ATOM_SEGMENT" : roles[occurrence], roles[occurrence] == "STANDALONE"));
        }
        return new
        {
            reviewedTocEntries = rows.Count,
            sourceReviewedShape = "ONE_S0_ATOM_PER_REVIEWED_TOC_ENTRY",
            standalone = rows.Count(value => value.Observed == "STANDALONE"),
            startsSegment = rows.Count(value => value.Observed == "STARTS_SEGMENT"),
            continuesPrevious = rows.Count(value => value.Observed == "CONTINUES_PREVIOUS"),
            boundaryExactness = new { exactSegment = rows.Count(value => value.Exact), overMerged = rows.Count(value => !value.Exact && value.Observed == "CONTINUES_PREVIOUS"), underSplit = 0, wrongStart = rows.Count(value => !value.Exact && value.Observed != "STANDALONE"), classification = "TOC_ENTRIES_ARE_NOT_HEADING_FP;_SINGLE_ATOM_SOURCE_REVIEW" },
            rows = rows.Select(value => new { candidateId = value.CandidateId, occurrence = value.Occurrence, alias = value.Alias, expected = value.Expected, observed = value.Observed, exact = value.Exact, segmentationInterpretation = value.Interpretation }).ToArray()
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
