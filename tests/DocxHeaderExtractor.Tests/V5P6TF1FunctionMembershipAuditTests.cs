using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free audit of immutable P6T-F1 raw captures. It scores function membership only, never grouping or extent.</summary>
public sealed class V5P6TF1FunctionMembershipAuditTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Root = "artifacts/v5-p6t-function-membership/p6tf1-preflight";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tf1-audit";
    private static readonly IReadOnlyDictionary<string, SourceReview> Src089ResidualReviews = new Dictionary<string, SourceReview>(StringComparer.Ordinal)
    {
        ["L0002:S0"] = new("THE GOVERNMENT", "ISSUER_HEADER", "FUNCTION_FALSE_POSITIVE_NONHEADING"),
        ["L0002:S1"] = new("THE SOCIALIST REPUBLIC OF VIETNAM", "NATIONAL_HEADER", "FUNCTION_FALSE_POSITIVE_NONHEADING"),
        ["L0003:S1"] = new("Independence – Freedom – Happiness", "NATIONAL_MOTTO", "FUNCTION_FALSE_POSITIVE_NONHEADING"),
    };

    private sealed record SourceReview(string ExactText, string SourceFamily, string Classification);

    [Fact]
    public void P6TF1Delta_audits_function_membership_without_extent_or_gold_mutation()
    {
        using var primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        using var retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/retry-src089-result.v1.json")));
        using var anchorAudit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json")));
        var src089 = Audit089(primary.RootElement, retry.RootElement, anchorAudit.RootElement);
        var src095 = Audit095(primary.RootElement, anchorAudit.RootElement);
        FreezeArtifact.AssertJson(OutputRoot, "function-membership-audit.v1.json", new
        {
            schemaVersion = "v5-p6tf1-function-membership-audit-v1",
            providerCalls = 0,
            goldRead = true,
            goldMutation = "NONE",
            rawCaptureMutation = "NONE",
            scoreAxis = "FUNCTION_MEMBERSHIP_ONLY",
            groupingPass = "BLOCKED",
            exactExtentPass = "BLOCKED",
            executionReceipt = new
            {
                primaryProviderCalls = 2,
                recoveryProviderCalls = 1,
                totalProviderCalls = 3,
                src089 = new
                {
                    initialAttempt = InitialTransportReceipt(primary.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == "SRC-089")),
                    canonicalAuditCapture = Receipt(retry.RootElement.GetProperty("row")),
                },
                src095 = Receipt(primary.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == "SRC-095")),
            },
            src089,
            src095,
            conclusion = "FUNCTION_MEMBERSHIP_AUDIT_ONLY; NO_AUTOMATIC_GOLD_MUTATION; GROUPING_AND_EXACT_EXTENT_REMAIN_BLOCKED",
        });
    }

    private static object Audit095(JsonElement primary, JsonElement anchorAudit)
    {
        var prepared = Prepare("SRC-095", SourcePdfCorpus.Src095);
        var row = primary.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == "SRC-095");
        var functions = Parse(prepared, row);
        var tocRows = anchorAudit.GetProperty("src095").GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(53, tocRows.Length);
        var rows = tocRows.Select(value =>
        {
            var occurrence = value.GetProperty("occurrence").GetString()!;
            return new { occurrence, function = functions[occurrence] };
        }).ToArray();
        return new
        {
            reviewedTocOccurrences = rows.Length,
            representsStructure = rows.Count(value => value.function == "REPRESENTS_STRUCTURE"),
            establishesStructure = rows.Count(value => value.function == "ESTABLISHES_STRUCTURE"),
            other = rows.Count(value => value.function == "OTHER"),
            representationFunctionVerdict = rows.All(value => value.function == "REPRESENTS_STRUCTURE") ? "CONFIRMED_ON_REVIEWED_TOC_COHORT" : "NOT_CONFIRMED_ON_REVIEWED_TOC_COHORT",
            rows,
        };
    }

    private static object Audit089(JsonElement primary, JsonElement retry, JsonElement anchorAudit)
    {
        var prepared = Prepare("SRC-089", SourcePdfCorpus.Src089);
        var functions = Parse(prepared, retry.GetProperty("row"));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(FrozenHistoryGold.Src089AuthoredPath)));
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
            .ToArray();
        var reviewedPrimaries = anchorAudit.GetProperty("src089").GetProperty("goldUnits").EnumerateArray().Select(value => value.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var reviewedUnits = claims.Where(parts => parts.Length > 0 && reviewedPrimaries.Contains(parts[0])).ToArray();
        Assert.Equal(6, reviewedUnits.Length);
        var allGoldParts = claims.SelectMany(parts => parts).ToHashSet(StringComparer.Ordinal);
        var occurrenceByAlias = prepared.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var reviewedRows = reviewedUnits.SelectMany(parts => parts.Select(alias => new { alias, occurrence = occurrenceByAlias[alias], function = functions[occurrenceByAlias[alias]] })).ToArray();
        var establishes = functions.Where(value => value.Value == "ESTABLISHES_STRUCTURE").Select(value => value.Key).OrderBy(value => int.Parse(value[1..])).ToArray();
        var atomsByOccurrence = prepared.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom, StringComparer.Ordinal);
        var residuals = establishes.Where(id => !allGoldParts.Contains(atomsByOccurrence[id].Alias)).Select(id =>
        {
            var atom = atomsByOccurrence[id];
            Assert.True(Src089ResidualReviews.TryGetValue(atom.Alias, out var review), $"Missing read-only source review for {atom.Alias}");
            Assert.Equal(review.ExactText, atom.Text);
            return new
            {
                occurrence = id,
                alias = atom.Alias,
                page = atom.Page,
                ordinal = atom.Ordinal,
                text = atom.Text,
                canonicalGoldMembership = false,
                sourceReview = review.SourceFamily,
                classification = review.Classification,
            };
        }).ToArray();
        return new
        {
            reviewedHeadingUnits = reviewedUnits.Length,
            reviewedHeadingSourceParts = reviewedRows.Length,
            establishesStructure = reviewedRows.Count(value => value.function == "ESTABLISHES_STRUCTURE"),
            representsStructure = reviewedRows.Count(value => value.function == "REPRESENTS_STRUCTURE"),
            other = reviewedRows.Count(value => value.function == "OTHER"),
            reviewedRows,
            totalEstablishesStructure = establishes.Length,
            establishesOnCanonicalGoldParts = establishes.Count(id => allGoldParts.Contains(atomsByOccurrence[id].Alias)),
            falseEstablishAudit = new
            {
                residualCount = residuals.Length,
                sourceReviewedNonheading = residuals.Count(value => value.classification == "FUNCTION_FALSE_POSITIVE_NONHEADING"),
                residuals,
                authority = "READ_ONLY_SOURCE_REVIEW_COMPLETED; GOLD_NOT_MUTATED",
            },
        };
    }

    private static object Receipt(JsonElement row) => new
    {
        documentId = row.GetProperty("documentId").GetString(),
        finishReason = row.GetProperty("finishReason").GetString(),
        parserAccepted = row.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean(),
        semanticRequestHash = row.GetProperty("semanticRequestHash").GetString(),
        providerRequestHash = row.GetProperty("providerRequestHash").GetString(),
        rawResponseSha256 = row.GetProperty("rawResponseSha256").GetString(),
        reasoningTokens = NullableInt(row, "reasoningTokens"),
        promptTokens = NullableInt(row, "promptTokens"),
        completionTokens = NullableInt(row, "completionTokens"),
        retryCount = row.GetProperty("retryCount").GetInt32(),
    };

    private static object InitialTransportReceipt(JsonElement row) => new
    {
        documentId = row.GetProperty("documentId").GetString(),
        transportAccepted = row.GetProperty("transportAccepted").GetBoolean(),
        finishReason = row.GetProperty("finishReason").ValueKind == JsonValueKind.Null ? null : row.GetProperty("finishReason").GetString(),
        classification = row.GetProperty("analysis").GetProperty("classification").GetString(),
        rawResponseSha256 = row.GetProperty("rawResponseSha256").ValueKind == JsonValueKind.Null ? null : row.GetProperty("rawResponseSha256").GetString(),
        retryCount = row.GetProperty("retryCount").GetInt32(),
    };

    private static int? NullableInt(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Number ? row.GetProperty(name).GetInt32() : null;

    private static PdfFunctionMembershipPreparedPackF1 Prepare(string documentId, string source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
        var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        return PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, Correspondences(pack));
    }

    private static IReadOnlyDictionary<string, string> Parse(PdfFunctionMembershipPreparedPackF1 prepared, JsonElement row)
    {
        var raw = row.GetProperty("rawResponse").GetString()!;
        Assert.Equal(row.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw));
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(prepared, raw);
        Assert.Equal(96, parsed.Decisions.Count);
        return parsed.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function.Wire(), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(key, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = values;
        }
        return result;
    }
}
