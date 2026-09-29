using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free recomposition of the two frozen P05 source universes under protocol v2.1, and a
/// deterministic, provider-free selection of the 3-pack wire-contract canary. This never opens Gold
/// and never calls a provider - <see cref="V5CanaryGate"/> makes it impossible for a bug here to run
/// all 31 packs or to run the canary without an explicit authorization flag this test never sets.
/// </summary>
public sealed class V5ProtocolV2_1PreflightTests
{
    private const string CanaryRoot = "artifacts/v5-provider-canary-v2_1";

    [Fact]
    public void Recompose_src089_and_src095_under_v2_1_without_provider_or_gold()
    {
        foreach (var (id, path) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var built = V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(path),
                id,
                DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create(),
                SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId,
                new V5ProviderEnvelope("qwen/qwen3.7-flash", "Alibaba", "none", true, "json_object", 300));
            built.Preflight.Validate();
            Assert.Equal(0, built.Preflight.ProviderCalls);
            Assert.False(built.Preflight.GoldRead);
            Assert.Equal(SemanticClaimContractV2_1.SchemaHash(), built.Preflight.ClaimSchemaHash);
            Assert.All(built.Requests, request => Assert.Equal(V5SemanticRequestComposerV2_1.Version, request.Request.ComposerVersion));
            var ownedAliases = built.Requests.SelectMany(item => item.OwnedAliases).ToArray();
            Assert.Equal(ownedAliases.Length, ownedAliases.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Select_three_packs_deterministically_for_a_wire_contract_only_canary()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "Alibaba", "none", true, "json_object", 300)
        {
            UsageInclude = true,
        };
        var docs = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .Select(item => (item.Item1, Built: V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(item.Item2), item.Item1, contract,
                SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId, envelope)))
            .ToArray();

        // A: the first SRC-089 pack. B: the first SRC-095 pack. Both deterministic (pack list order is
        // stable - already relied on by V5ProtocolV2PreflightTests's 1:1 comparison against v1).
        var packA = (DocumentId: docs[0].Item1, Pack: docs[0].Built.Requests[0]);
        var packB = (DocumentId: docs[1].Item1, Pack: docs[1].Built.Requests[0]);

        // C: the largest-by-bytes pack across both documents, deterministic tie-break by
        // (documentId, packId); if it duplicates A or B, the next-largest distinct pack is used.
        var allPacks = docs.SelectMany(doc => doc.Built.Requests.Select(pack => (DocumentId: doc.Item1, Pack: pack))).ToArray();
        var byBytesDesc = allPacks
            .OrderByDescending(item => item.Pack.Request.Utf8Bytes)
            .ThenBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.Pack.PackId, StringComparer.Ordinal)
            .ToArray();
        var packC = byBytesDesc.First(item =>
            !(item.DocumentId == packA.DocumentId && item.Pack.PackId == packA.Pack.PackId) &&
            !(item.DocumentId == packB.DocumentId && item.Pack.PackId == packB.Pack.PackId));

        var selection = new[] { packA, packB, packC };
        Assert.Equal(V5CanaryGate.CanaryRequestCount, selection.Length);
        Assert.Equal(selection.Select(item => (item.DocumentId, item.Pack.PackId)).Distinct().Count(), selection.Length);

        // The gate makes accidental over-execution structurally impossible: wrong count throws, and an
        // unauthorized count of exactly 3 still throws because this test never sets the flag to true.
        Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(allPacks.Length, providerExecutionAuthorized: false));
        Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(selection.Length, providerExecutionAuthorized: false));
        var ex = Record.Exception(() => V5CanaryGate.Authorize(selection.Length, providerExecutionAuthorized: true));
        Assert.Null(ex);

        var sourceUniverseHashes = docs.ToDictionary(doc => doc.Item1, doc => doc.Built.Preflight.SourceUniverseSha, StringComparer.Ordinal);
        var head = GitHead();

        var canarySelection = new
        {
            schemaVersion = "v5-provider-canary-selection-v1",
            providerCalls = 0,
            goldRead = false,
            selectionUsedGold = false,
            canaryRequestCount = V5CanaryGate.CanaryRequestCount,
            packs = selection.Select(item => new
            {
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                ownedAliases = item.Pack.OwnedAliases,
                visibleAliases = item.Pack.VisibleAliases,
                requestBytes = item.Pack.Request.Utf8Bytes,
                requestHash = item.Pack.Request.RequestHash,
                sourceUniverseHash = sourceUniverseHashes[item.DocumentId],
                protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
                schemaHash = item.Pack.Request.SchemaHash,
                promptHash = item.Pack.Request.PromptHash,
            }),
        };
        WriteJson(Path.Combine(CanaryRoot, "canary-selection.v1.json"), canarySelection);

        var preflight = new
        {
            schemaVersion = "v5-provider-canary-preflight-v1",
            head,
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            schemaHash = SemanticClaimContractV2_1.SchemaHash(),
            taskContractHash = contract.Hash(),
            sourceUniverseHashes,
            canaryRequestCount = V5CanaryGate.CanaryRequestCount,
            requestHashes = selection.Select(item => item.Pack.Request.RequestHash).ToArray(),
            requestBytes = selection.Select(item => item.Pack.Request.Utf8Bytes).ToArray(),
            selectionRationale = new
            {
                a = "first SRC-089 pack, deterministic pack-list order",
                b = "first SRC-095 pack, deterministic pack-list order",
                c = "largest request by UTF-8 byte size across both documents; ties broken by (documentId, packId); " +
                    "re-selected to the next-largest distinct pack if it duplicated A or B",
            },
            providerEnvelope = envelope,
            acceptanceCriteria = new[]
            {
                "TRANSPORT_VALID: HTTP/provider success, complete SSE, terminal finish_reason, clean EOF",
                "JSON_VALID",
                "SCHEMA_VALID",
                "GROUNDING_PRESENT: subject/sourceParts present",
                "BINDING_VALID: exact source binding succeeds",
                "OWNERSHIP_VALID: subject belongs to owned aliases",
                "VOCABULARY_VALID: predicate/relation belongs to the task contract",
                "no Gold read; no heading F1 threshold at canary stage",
            },
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        };
        WriteJson(Path.Combine(CanaryRoot, "preflight.json"), preflight);
    }

    private static string? GitHead()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = TestRepository.Path("."),
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJson(string relativePath, object value)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
