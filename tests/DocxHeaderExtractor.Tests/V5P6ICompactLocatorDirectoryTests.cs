using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6ICompactLocatorDirectoryTests
{
    private const string ArtifactRoot = "artifacts/v5-p6i-compact-locator-directory";
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    [Fact]
    public void Freeze_compact_directory_for_31_packs_without_changing_locator_authority_or_calling_provider()
    {
        var rows = Audit31();
        Assert.Equal(31, rows.Count);
        Assert.Equal(2884, rows.Sum(row => row.OwnedAtoms));
        Assert.All(rows, row => Assert.True(row.CompactProviderBodyBytes < row.LegacyProviderBodyBytes));

        var maxLegacyBody = rows.Max(row => row.LegacyProviderBodyBytes);
        var maxCompactBody = rows.Max(row => row.CompactProviderBodyBytes);
        var maxLegacyDirectory = rows.Max(row => row.LegacyDirectoryBytes);
        var maxCompactDirectory = rows.Max(row => row.CompactDirectoryBytes);
        var maxCompactModelVisible = rows.Max(row => row.CompactModelVisibleBytes);
        var maxCompactSystemPrompt = rows.Max(row => row.CompactSystemPromptBytes);
        var totalLegacyBody = rows.Sum(row => row.LegacyProviderBodyBytes);
        var totalCompactBody = rows.Sum(row => row.CompactProviderBodyBytes);

        FreezeArtifact.AssertJson(ArtifactRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p6i-compact-locator-directory-v1",
            providerCalls = 0,
            goldRead = false,
            goldMutation = "NONE",
            sharedRuntime = "UNCHANGED",
            protocol = V5SparseCandidateRequestComposerV1.CompactDirectoryVersion,
            directoryEncoding = "each owned atom has ordered boundaryHandles; item i resolves through the unchanged request-local registry to Unicode-scalar boundary i; no offsets serialized",
            locatorAuthority = "A#/H# issuance, fingerprint, TryBoundary lookup, Decode, binder and response parser remain unchanged",
            roundTripProof = "for every issued boundary in all 31 pack registries, compact array position and handle resolve to the same atom and UTF16 offset as the legacy directory; scalar-interior boundaries are absent",
            rows = rows,
            summary = new
            {
                packCount = rows.Count,
                ownedAtoms = rows.Sum(row => row.OwnedAtoms),
                maxLegacyDirectoryBytes = maxLegacyDirectory,
                maxCompactDirectoryBytes = maxCompactDirectory,
                maxLegacyProviderBodyBytes = maxLegacyBody,
                maxCompactProviderBodyBytes = maxCompactBody,
                totalLegacyProviderBodyBytes = totalLegacyBody,
                totalCompactProviderBodyBytes = totalCompactBody,
                aggregateProviderBodyByteReductionPercent = (double)(totalLegacyBody - totalCompactBody) * 100 / totalLegacyBody,
                maxCompactModelVisibleSystemPlusUserBytes = maxCompactModelVisible,
                compactSystemPromptUtf8Bytes = maxCompactSystemPrompt,
            },
            tokenBudget = new
            {
                actualInputTokens = "NOT_MEASURED_NO_VERIFIED_OFFLINE_TOKENIZER",
                contextCapacity = "NOT_EVALUABLE_FROM_UTF8_BYTES",
                inputTokenAuthority = 991808,
                outputReserveMax = rows.Max(row => row.MaxCompletionTokens),
                effectiveInputCeiling = rows.Min(row => row.EffectiveInputTokenCeiling),
            },
            providerExecution = "BLOCKED_NOT_AUTHORIZED",
        });
    }

    private static IReadOnlyList<CompactAuditRow> Audit31()
    {
        var rows = new List<CompactAuditRow>();
        foreach (var (documentId, pdf, expectedPacks) in new[]
        {
            ("SRC-089", SourcePdfCorpus.Src089, 7),
            ("SRC-095", SourcePdfCorpus.Src095, 24),
        })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), documentId, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId,
                new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
                    { UsageInclude = true, OpenRouterResponseCacheDisabled = true });
            Assert.Equal(expectedPacks, packs.Count);
            var sourceAtoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf)).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            foreach (var (pack, ordinal) in packs.Select((pack, index) => (pack, index + 1)))
            {
                var ownedAtoms = pack.OwnedAliases.Select(alias => sourceAtoms[alias]).ToArray();
                var registry = RequestLocalLocatorRegistry.Create(ownedAtoms);
                var legacyDirectory = registry.Directory();
                var compactDirectory = registry.CompactDirectory();
                Assert.Equal(legacyDirectory.Atoms.Count, compactDirectory.Atoms.Count);
                foreach (var (oldAtom, newAtom, atomIndex) in legacyDirectory.Atoms.Zip(compactDirectory.Atoms).Select((pair, index) => (pair.First, pair.Second, index)))
                {
                    Assert.Equal(oldAtom.Atom, newAtom.Atom);
                    Assert.Equal(oldAtom.Text, newAtom.Text);
                    var expectedOffsets = ScalarBoundaryOffsets(oldAtom.Text);
                    Assert.Equal(expectedOffsets.Count, newAtom.BoundaryHandles.Count);
                    Assert.Equal(expectedOffsets.Count, oldAtom.Boundaries.Count);
                    for (var boundaryIndex = 0; boundaryIndex < newAtom.BoundaryHandles.Count; boundaryIndex++)
                    {
                        var handle = newAtom.BoundaryHandles[boundaryIndex];
                        Assert.Equal(oldAtom.Boundaries[boundaryIndex].Handle, handle);
                        Assert.Equal(expectedOffsets[boundaryIndex], oldAtom.Boundaries[boundaryIndex].Utf16Offset);
                        Assert.True(registry.TryBoundary(handle, out var resolvedAtom, out var resolvedOffset));
                        Assert.Equal(atomIndex, resolvedAtom);
                        Assert.Equal(expectedOffsets[boundaryIndex], resolvedOffset);
                    }
                }

                var compactDirectoryJson = JsonSerializer.Serialize(compactDirectory, CanonicalJson.Options);
                Assert.DoesNotContain("Utf16Offset", compactDirectoryJson, StringComparison.Ordinal);
                Assert.DoesNotContain("offset", compactDirectoryJson, StringComparison.OrdinalIgnoreCase);
                var legacyRequest = V5SparseCandidateRequestComposerV1.Compose(Contract, pack.Packet, registry);
                var compactRequest = V5SparseCandidateRequestComposerV1.ComposeCompactDirectory(Contract, pack.Packet, registry);
                Assert.DoesNotContain("PARENT_OF", compactRequest.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("sourceOrdinal", compactRequest.UserMessage, StringComparison.Ordinal);
                using (var compactMessage = JsonDocument.Parse(compactRequest.UserMessage))
                {
                    Assert.Equal(V5SparseCandidateRequestComposerV1.CompactDirectoryVersion,
                        compactMessage.RootElement.GetProperty("protocolVersion").GetString());
                    Assert.All(compactMessage.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray(), node =>
                    {
                        Assert.False(node.TryGetProperty("atom", out _));
                        Assert.False(node.TryGetProperty("boundaryHandles", out _));
                    });
                    var modelAtoms = compactMessage.RootElement.GetProperty("ownedSubjects").EnumerateArray().ToArray();
                    Assert.Equal(ownedAtoms.Length, modelAtoms.Length);
                    foreach (var (modelAtom, registryAtom) in modelAtoms.Zip(compactDirectory.Atoms))
                        Assert.Equal(registryAtom.BoundaryHandles, modelAtom.GetProperty("boundaryHandles").EnumerateArray().Select(item => item.GetString()!).ToArray());
                }
                var legacyBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(legacyRequest.SystemPrompt, legacyRequest.UserMessage, pack.MaxCompletionTokens, Envelope);
                var compactBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(compactRequest.SystemPrompt, compactRequest.UserMessage, pack.MaxCompletionTokens, Envelope);
                Assert.True(compactRequest.LocatorDirectoryUtf8Bytes < legacyRequest.LocatorDirectoryUtf8Bytes);
                Assert.True(compactBody.Bytes < legacyBody.Bytes);
                rows.Add(new CompactAuditRow(documentId, ordinal, ownedAtoms.Length,
                    legacyRequest.LocatorDirectoryUtf8Bytes, compactRequest.LocatorDirectoryUtf8Bytes,
                    legacyBody.Bytes, compactBody.Bytes,
                    (double)(legacyBody.Bytes - compactBody.Bytes) * 100 / legacyBody.Bytes,
                    compactRequest.SystemPromptUtf8Bytes + compactRequest.UserMessageUtf8Bytes,
                    compactRequest.SystemPromptUtf8Bytes, compactRequest.UserMessageUtf8Bytes,
                    pack.MaxCompletionTokens, Math.Min(991808, 1_000_000 - pack.MaxCompletionTokens),
                    legacyBody.Hash, compactBody.Hash, legacyRequest.UserMessageSha256, compactRequest.UserMessageSha256));
            }
        }
        return rows;
    }

    private static IReadOnlyList<int> ScalarBoundaryOffsets(string text)
    {
        var offsets = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;
            offsets.Add(index + 1);
        }
        return offsets;
    }

    private sealed record CompactAuditRow(string DocumentId, int ParentOrdinal, int OwnedAtoms,
        int LegacyDirectoryBytes, int CompactDirectoryBytes, int LegacyProviderBodyBytes, int CompactProviderBodyBytes,
        double ProviderBodyReductionPercent, int CompactModelVisibleBytes, int CompactSystemPromptBytes, int CompactUserMessageBytes,
        int MaxCompletionTokens, int EffectiveInputTokenCeiling,
        string LegacyProviderBodySha256, string CompactProviderBodySha256, string LegacyUserMessageSha256, string CompactUserMessageSha256);
}
