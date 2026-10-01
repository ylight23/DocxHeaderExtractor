using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Phase C for the frozen P5R execution. Uses the live sparse V3.1 parser and binder only.</summary>
public sealed class V5P5RFull31V32ContractAuditTests
{
    private const string Root = "artifacts/v5-full31-v32";
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    [Fact]
    public void Audit_frozen_full31_execution_through_live_v31_parse_and_bind()
    {
        using var execution = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/execution-result.v1.json")));
        var sourceRows = execution.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, sourceRows.Length);
        Assert.Equal(31, execution.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(execution.RootElement.GetProperty("goldRead").GetBoolean());

        var packs = BuildPacks();
        var atomsByDocument = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal)
        {
            ["SRC-089"] = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src089)),
            ["SRC-095"] = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src095)),
        };
        var resultRows = new List<object>();
        var allRefusals = new List<string>();
        var transportValid = 0; var parserValid = 0; var rawDecisions = 0; var rawClaims = 0; var boundClaims = 0;
        var proposalRefusalRecords = 0; var decisionRefusalRecords = 0;
        foreach (var row in sourceRows)
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var parentOrdinal = row.GetProperty("parentOrdinal").GetInt32();
            var pack = packs[(documentId, parentOrdinal)];
            var error = row.GetProperty("transportError").ValueKind == JsonValueKind.Null ? null : row.GetProperty("transportError").GetString();
            if (error is not null)
            {
                resultRows.Add(new { documentId, parentOrdinal, packId = pack.PackId, transport = "TRANSPORT_INVALID", parser = "NOT_EVALUABLE", binding = "NOT_EVALUABLE", semantic = "NOT_EVALUABLE", transportError = error });
                continue;
            }
            transportValid++;
            var raw = row.GetProperty("assembledContent").GetString()!;
            try
            {
                using var payload = JsonDocument.Parse(raw);
                var parsed = V5SemanticSparseDecisionContractV3_1.Parse(payload.RootElement, Contract,
                    pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);
                parserValid++;
                rawDecisions += parsed.Decisions.Count;
                rawClaims += parsed.Decisions.Sum(decision => decision.Claims.Count);
                var atoms = atomsByDocument[documentId];
                var binding = V5SemanticSparseDecisionContractV3_1.Bind(row.GetProperty("semanticRequestHash").GetString()!, parsed, Contract,
                    pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, atoms, ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases));
                boundClaims += binding.Bound.Count;
                allRefusals.AddRange(binding.Refusals.Values);
                proposalRefusalRecords += binding.Refusals.Keys.Count(key => key.StartsWith("proposal-", StringComparison.Ordinal));
                decisionRefusalRecords += binding.Refusals.Keys.Count(key => key.StartsWith("sparse-decision-", StringComparison.Ordinal));
                resultRows.Add(new
                {
                    documentId, parentOrdinal, packId = pack.PackId,
                    transport = "TRANSPORT_VALID", parser = "PARSER_VALID",
                    binding = binding.Bound.Count > 0 ? "BINDER_EXECUTED_WITH_BOUND_CLAIMS" : "BINDER_EXECUTED_NO_BOUND_CLAIMS",
                    semantic = "PENDING_GOLD_AUDIT",
                    rawSparseDecisions = parsed.Decisions.Count,
                    rawClaims = parsed.Decisions.Sum(decision => decision.Claims.Count),
                    boundClaims = binding.Bound.Count,
                    refusalRecords = binding.Refusals.Select(pair => new { key = pair.Key, level = pair.Key.StartsWith("proposal-", StringComparison.Ordinal) ? "CLAIM" : "DECISION", reason = pair.Value }).ToArray(),
                    bound = binding.Bound.Select(item => new
                    {
                        predicate = item.Claim.Predicate, value = item.Claim.Value,
                        sourceParts = item.Claim.Subject.Parts.Select(part => new { part.Alias, part.Start, part.End, part.Text }).ToArray(),
                    }).ToArray(),
                });
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                var usage = row.GetProperty("usage");
                resultRows.Add(new
                {
                    documentId, parentOrdinal, packId = pack.PackId, transport = "TRANSPORT_VALID", parser = "PARSER_INVALID", binding = "NOT_EXECUTED", semantic = "NOT_EVALUABLE",
                    parseError = ex.Message,
                    completionTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : (int?)null,
                    maxCompletionTokens = row.GetProperty("maxCompletionTokens").GetInt32(),
                    assembledResponseUtf8Bytes = row.GetProperty("assembledContentUtf8Bytes").GetInt32(),
                    maxResponseUtf8Bytes = row.GetProperty("maxResponseUtf8Bytes").GetInt32(),
                });
            }
        }

        Assert.Equal(23, transportValid);
        Assert.Equal(22, parserValid);
        var refusalGroups = allRefusals.GroupBy(ClassifyRefusal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        FreezeArtifact.AssertJson(Root, "contract-audit.v1.json", new
        {
            schemaVersion = "v5-p5r-v32-full31-contract-audit-v1",
            sourceExecution = $"{Root}/execution-result.v1.json",
            providerCalls = 0,
            goldRead = false,
            authority = "V5SemanticSparseDecisionContractV3_1.Parse + V5SemanticSparseDecisionContractV3_1.Bind",
            aggregate = new
            {
                attemptedParentPacks = 31,
                transportValidLeaves = transportValid,
                transportInvalidLeaves = 31 - transportValid,
                parserValidLeaves = parserValid,
                parserInvalidLeaves = transportValid - parserValid,
                rawSparseDecisions = rawDecisions,
                rawClaims,
                boundClaims,
                refusalRecords = allRefusals.Count,
                claimLevelProposalRefusalRecords = proposalRefusalRecords,
                decisionLevelRefusalRecords = decisionRefusalRecords,
                refusalDecomposition = refusalGroups,
            },
            rows = resultRows,
        });
    }

    private static string ClassifyRefusal(string reason)
    {
        if (reason.Contains("whole-atom-must-omit-verbatim-text", StringComparison.Ordinal)) return "whole-atom-must-omit-verbatim-text";
        if (reason.Contains("TextNotInAtom", StringComparison.Ordinal)) return "TextNotInAtom";
        if (reason.Contains("OUT_OF_OWNED_SEGMENT", StringComparison.Ordinal) || reason.Contains("owned", StringComparison.OrdinalIgnoreCase)) return "ownership refusal";
        if (reason.Contains("OutOfSourceOrder", StringComparison.Ordinal)) return "OutOfSourceOrder";
        if (reason.Contains("target", StringComparison.OrdinalIgnoreCase) || reason.Contains("context", StringComparison.OrdinalIgnoreCase)) return "binding/context mismatch";
        if (reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) return "duplicate decision/index";
        if (reason.Contains("relation", StringComparison.OrdinalIgnoreCase)) return "relation target violation";
        return "other";
    }

    private static Dictionary<(string DocumentId, int ParentOrdinal), V5PackedDecisionRequestV3> BuildPacks()
    {
        var result = new Dictionary<(string, int), V5PackedDecisionRequestV3>();
        foreach (var document in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Item2), document.Item1, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            for (var index = 0; index < packs.Count; index++) result[(document.Item1, index + 1)] = packs[index];
        }
        Assert.Equal(31, result.Count);
        return result;
    }
}
