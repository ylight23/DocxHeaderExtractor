using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free P6T-F1 preflight. This lane is total function membership and is intentionally independent from P6T-E1.
/// </summary>
public sealed class V5P6TF1FunctionMembershipContractTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tf1-preflight";

    [Fact]
    public void P6TF1_freezes_total_function_membership_preflight_without_E1_dependency()
    {
        var rows = new List<object>();
        foreach (var (documentId, source) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, BuildCorrespondences(pack));
            var request = prepared.Request;

            Assert.Equal(96, request.Occurrences.Count);
            Assert.Contains("ESTABLISHES_STRUCTURE", request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("REPRESENTS_STRUCTURE", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("STARTS_SEGMENT", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("CONTINUES_PREVIOUS", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("STANDALONE", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate", request.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceParts", request.UserMessage, StringComparison.Ordinal);

            var synthetic = JsonSerializer.Serialize(new
            {
                decisions = request.Occurrences.Select((value, index) => new
                {
                    occurrence = value.Id,
                    function = index % 3 == 0 ? "ESTABLISHES_STRUCTURE" : index % 3 == 1 ? "REPRESENTS_STRUCTURE" : "OTHER",
                }).ToArray(),
            });
            using var payload = JsonDocument.Parse(synthetic);
            var parsed = OccurrenceFunctionProtocolV1.Parse(payload.RootElement, Encoding.UTF8.GetByteCount(synthetic), PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, request.Occurrences);
            Assert.Equal(96, parsed.Decisions.Count);
            Assert.Equal(32, parsed.Decisions.Count(value => value.Function == OccurrenceFunction.EstablishesStructure));
            Assert.Equal(32, parsed.Decisions.Count(value => value.Function == OccurrenceFunction.RepresentsStructure));
            Assert.Equal(32, parsed.Decisions.Count(value => value.Function == OccurrenceFunction.Other));
            AssertParserRejects(request, new { decisions = request.Occurrences.Skip(1).Select(value => new { occurrence = value.Id, function = "OTHER" }).ToArray() }, "function-membership-decision-cardinality-invalid");
            AssertParserRejects(request, new { decisions = request.Occurrences.Select(value => new { occurrence = value.Id, function = "HEADING_START" }).ToArray() }, "function-membership-not-in-enum");
            AssertParserRejects(request, new { decisions = request.Occurrences.Select(value => new { occurrence = value.Id, function = "OTHER", reason = "forbidden" }).ToArray() }, "function-membership-decision-schema-invalid");
            AssertParserRejects(request, new { decisions = request.Occurrences.Select((value, index) => new { occurrence = index == 95 ? "O999" : value.Id, function = "OTHER" }).ToArray() }, "function-membership-occurrence-not-issued");
            AssertParserRejects(request, new { decisions = request.Occurrences.Select((value, index) => new { occurrence = index == 95 ? "O1" : value.Id, function = "OTHER" }).ToArray() }, "function-membership-occurrence-duplicate");
            AssertParserRejects(request, new { decisions = request.Occurrences.Select(value => new { occurrence = value.Id, function = "OTHER" }).ToArray(), extra = true }, "function-membership-root-invalid");
            rows.Add(new
            {
                documentId,
                packId = pack.PackId,
                issuedOccurrences = request.Occurrences.Count,
                requestHash = request.UserMessageSha256,
                systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
                providerRequestHash = prepared.ProviderRequestHash,
                providerRequestBytes = prepared.ProviderRequestBytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
                carrier = "OPENROUTER_QWEN37_ALIBABA_REASONING_ENABLED_JSON_OBJECT",
            });
        }

        FreezeArtifact.AssertJson(OutputRoot, "two-pack-function-membership-manifest.v1.json", new
        {
            schemaVersion = "v5-p6tf1-function-membership-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            providerCalls = 0,
            goldRead = false,
            goldMutation = "NONE",
            sharedRuntime = "UNCHANGED",
            segmentationDependency = "NONE",
            consumesP6TE1Output = false,
            explicitInvariant = "P6T_F1_DOES_NOT_CONSUME_P6T_E1_OUTPUT",
            extentOutput = "FORBIDDEN",
            groupingPass = "BLOCKED",
            exactExtentPass = "BLOCKED",
            outputContract = "TOTAL_96_DECISIONS_FUNCTION_MEMBERSHIP_ONLY",
            allowedFunctions = new[] { "ESTABLISHES_STRUCTURE", "REPRESENTS_STRUCTURE", "OTHER" },
            forbiddenOutput = new[] { "START_OR_SEGMENT_BOUNDARIES", "CANDIDATE_IDS", "SOURCE_PARTS", "SPANS", "LOCATORS", "HIERARCHY", "RELATIONS", "CONFIDENCE", "REASONS", "EXTENT" },
            inputEvidence = new[] { "OCCURRENCE_ID", "PAGE", "TEXT", "READ_ONLY_CORRESPONDENCES", "CONTEXT_ONLY_EVIDENCE" },
            expectedDecisions = 96,
            maximumFutureProviderCalls = 2,
            retry = 0,
            repair = false,
            fallback = false,
            rows,
        });
    }

    private static void AssertParserRejects(OccurrenceFunctionRequest request, object payloadObject, string failure)
    {
        var raw = JsonSerializer.Serialize(payloadObject);
        using var payload = JsonDocument.Parse(raw);
        var exception = Assert.Throws<InvalidOperationException>(() => OccurrenceFunctionProtocolV1.Parse(payload.RootElement, Encoding.UTF8.GetByteCount(raw), PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, request.Occurrences));
        Assert.Equal(failure, exception.Message);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var list = result.TryGetValue(key, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }
}
