using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6P production-candidate parity, document context and exact binding tests.</summary>
public sealed class V5P6PDocumentAwarePdfCandidateTests
{
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly (string Id, string Pdf, int ExpectedPacks)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, 7),
        ("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void Candidate_preserves_exact_P05_pack_partition_and_conserves_owned_atoms()
    {
        var plans = Documents.Select(doc => PdfHeadingMembershipProductionAdapter.Prepare(
            TestRepository.Path(doc.Pdf), doc.Id, Contract)).ToArray();

        Assert.Equal(31, plans.Sum(plan => plan.Packs.Count));
        Assert.Equal(2_884, plans.Sum(plan => plan.SourceOccurrenceTotal));
        Assert.Equal(7, plans[0].Packs.Count);
        Assert.Equal(24, plans[1].Packs.Count);
        Assert.Equal(7, Documents[0].ExpectedPacks);
        Assert.Equal(24, Documents[1].ExpectedPacks);
        foreach (var plan in plans)
        {
            var owned = plan.Packs.SelectMany(pack => pack.OwnedAliases).ToArray();
            Assert.Equal(plan.SourceOccurrenceTotal, owned.Length);
            Assert.Equal(owned.Length, owned.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(plan.SourceAtoms.Select(atom => atom.Alias).Order(StringComparer.Ordinal), owned.Order(StringComparer.Ordinal));
        }

        var oldManifestPath = TestRepository.Path("artifacts/v5-p6nb-full31-reasoning-lane/execution-manifest.v1.json");
        using var oldManifest = JsonDocument.Parse(File.ReadAllText(oldManifestPath));
        var oldRows = oldManifest.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => (row.GetProperty("documentId").GetString()!, row.GetProperty("packId").GetString()!));
        foreach (var pack in plans.SelectMany(plan => plan.Packs))
        {
            var lineage = oldRows[(pack.DocumentId, pack.PackId)];
            Assert.Equal(pack.Registry.Fingerprint, lineage.GetProperty("registryFingerprint").GetString());
            Assert.Equal(pack.OwnedAliases.Count, lineage.GetProperty("ownedAtoms").GetInt32());
            Assert.Equal(pack.MaxCompletionTokens, lineage.GetProperty("maxCompletionTokens").GetInt32());
        }
    }

    [Fact]
    public void Document_context_is_deterministic_neutral_and_never_issues_selectable_handles()
    {
        var first = PdfHeadingMembershipProductionAdapter.Prepare(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        var second = PdfHeadingMembershipProductionAdapter.Prepare(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        Assert.Equal(first.Packs.Select(pack => pack.ProviderRequestHash), second.Packs.Select(pack => pack.ProviderRequestHash));

        foreach (var pack in first.Packs)
        {
            using var root = JsonDocument.Parse(pack.Request.UserMessage);
            Assert.Equal(PdfHeadingMembershipProductionAdapter.ProtocolVersion, root.RootElement.GetProperty("protocolVersion").GetString());
            var documentContext = root.RootElement.GetProperty("documentContext");
            Assert.Equal(first.SourceOccurrenceTotal, documentContext.GetProperty("sourceOccurrenceTotal").GetInt32());
            Assert.Equal(first.PhysicalPageTotal, documentContext.GetProperty("physicalPageTotal").GetInt32());
            Assert.InRange(documentContext.GetProperty("before").GetArrayLength(), 0, PdfHeadingMembershipProductionAdapter.WiderContextOccurrencesPerSide);
            Assert.InRange(documentContext.GetProperty("after").GetArrayLength(), 0, PdfHeadingMembershipProductionAdapter.WiderContextOccurrencesPerSide);
            AssertNoForbiddenContextProperties(documentContext);

            var owned = root.RootElement.GetProperty("ownedSubjects");
            Assert.Equal(pack.OwnedAliases.Count, owned.GetArrayLength());
            Assert.All(owned.EnumerateArray(), item => Assert.True(item.TryGetProperty("atom", out _)));
            var contextOnly = root.RootElement.GetProperty("contextOnlyEvidence");
            Assert.All(contextOnly.EnumerateArray(), item =>
            {
                Assert.False(item.TryGetProperty("atom", out _));
                Assert.False(item.TryGetProperty("boundaryHandles", out _));
            });
        }

        Assert.Equal(V5FreeHeadingCandidateProtocolV1.BoundLocatorSystemPrompt, first.Packs[0].Request.SystemPrompt);
        Assert.DoesNotContain("document genre", first.Packs[0].Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("P6N-C", first.Packs[0].Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOC", first.Packs[0].Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INDEX", first.Packs[0].Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Candidate_execution_uses_frozen_production_body_and_real_parser_binder()
    {
        var plan = PdfHeadingMembershipProductionAdapter.Prepare(TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", Contract);
        var pack = plan.Packs[0];
        var fake = new FrozenExecutor("""{"headings":[{"sourceParts":[{"atom":"A0"}]}]}""");

        var result = await PdfHeadingMembershipProductionAdapter.ExecuteAndBindAsync(pack, fake);

        Assert.Equal(pack.ProviderBody, fake.CapturedBody);
        Assert.Equal(pack.ProviderRequestHash, fake.CapturedHash);
        Assert.Equal(pack.MaxCompletionTokens, fake.CapturedMaxTokens);
        Assert.Equal(pack.Request.SystemPrompt, fake.CapturedSystem);
        Assert.Equal(pack.Request.UserMessage, fake.CapturedUser);
        Assert.Null(result.ParseError);
        Assert.NotNull(result.Binding);
        Assert.Single(result.Binding!.Response.Occurrences);
        Assert.Empty(result.Binding.Quarantined);

        using var body = JsonDocument.Parse(fake.CapturedBody!);
        var root = body.RootElement;
        Assert.Equal("qwen/qwen3.7-flash", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.True(root.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(root.GetProperty("reasoning").TryGetProperty("effort", out _));
        Assert.Equal("alibaba", root.GetProperty("provider").GetProperty("order")[0].GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("usage").GetProperty("include").GetBoolean());
    }

    [Fact]
    public void Context_package_contains_neutral_document_scale_and_recurrence_facts()
    {
        var plan = PdfHeadingMembershipProductionAdapter.Prepare(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        using var root = JsonDocument.Parse(plan.Packs[0].Request.UserMessage);
        var context = root.RootElement.GetProperty("documentContext");
        Assert.True(context.GetProperty("pageMap").GetArrayLength() > 0);
        Assert.True(context.GetProperty("currentOwned").GetProperty("occurrenceCount").GetInt32() > 0);
        Assert.True(context.GetProperty("repeatedTextPositions").ValueKind is JsonValueKind.Array);
        Assert.DoesNotContain("semanticFunction", plan.Packs[0].Request.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Layout_aware_arm_preserves_P6P_prompt_locator_and_P05_ownership_but_exposes_every_parser_fact()
    {
        var baseline = Documents.Select(doc => PdfHeadingMembershipProductionAdapter.Prepare(
            TestRepository.Path(doc.Pdf), doc.Id, Contract)).ToArray();
        var layout = Documents.Select(doc => PdfHeadingMembershipProductionAdapter.PrepareLayoutAware(
            TestRepository.Path(doc.Pdf), doc.Id, Contract)).ToArray();

        Assert.Equal(31, layout.Sum(plan => plan.Packs.Count));
        Assert.Equal(2_884, layout.Sum(plan => plan.SourceOccurrenceTotal));
        foreach (var pair in baseline.Zip(layout))
        {
            Assert.Equal(pair.First.SourceSha256, pair.Second.SourceSha256);
            Assert.Equal(pair.First.SourceUniverseSha256, pair.Second.SourceUniverseSha256);
            foreach (var packs in pair.First.Packs.Zip(pair.Second.Packs))
            {
                Assert.Equal(packs.First.PackId, packs.Second.PackId);
                Assert.Equal(packs.First.OwnedAliases, packs.Second.OwnedAliases);
                Assert.Equal(packs.First.VisibleAliases, packs.Second.VisibleAliases);
                Assert.Equal(packs.First.Registry.Fingerprint, packs.Second.Registry.Fingerprint);
                Assert.Equal(packs.First.Request.SystemPrompt, packs.Second.Request.SystemPrompt);
                Assert.NotEqual(packs.First.ProviderRequestHash, packs.Second.ProviderRequestHash);

                using var root = JsonDocument.Parse(packs.Second.Request.UserMessage);
                Assert.Equal(PdfHeadingMembershipProductionAdapter.LayoutAwareProtocolVersion, root.RootElement.GetProperty("protocolVersion").GetString());
                var owned = root.RootElement.GetProperty("ownedSubjects").EnumerateArray().ToArray();
                Assert.Equal(packs.Second.OwnedAliases.Count, owned.Length);
                Assert.All(owned, item => AssertNeutralLayoutFacts(item.GetProperty("layoutFacts")));
                Assert.All(root.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray(), item =>
                {
                    Assert.False(item.TryGetProperty("atom", out _));
                    Assert.False(item.TryGetProperty("boundaryHandles", out _));
                    AssertNeutralLayoutFacts(item.GetProperty("layoutFacts"));
                });
            }
        }
    }

    private static void AssertNeutralLayoutFacts(JsonElement facts)
    {
        Assert.Equal(JsonValueKind.Object, facts.ValueKind);
        Assert.True(facts.TryGetProperty("page", out _));
        Assert.True(facts.TryGetProperty("verticalPosition", out _));
        Assert.True(facts.TryGetProperty("left", out _));
        Assert.True(facts.TryGetProperty("right", out _));
        Assert.True(facts.TryGetProperty("width", out _));
        Assert.True(facts.TryGetProperty("lineCount", out _));
        Assert.True(facts.TryGetProperty("boldRatio", out _));
        Assert.True(facts.TryGetProperty("fontSizeToBodyRatio", out _));
        Assert.True(facts.TryGetProperty("sameNormalizedTextPageCount", out _));
        Assert.All(facts.EnumerateObject(), property =>
            Assert.DoesNotContain(property.Name, new[] { "alias", "id", "ordinal", "heading", "region", "scope", "score", "candidate" }, StringComparer.OrdinalIgnoreCase));
    }

    private static void AssertNoForbiddenContextProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        foreach (var property in node.EnumerateObject())
        {
            Assert.DoesNotContain(property.Name, new[] { "atom", "boundaryHandles", "sourceAlias", "sourceId", "sourceOrdinal", "anchor", "semanticFunction", "regionLabel" }, StringComparer.OrdinalIgnoreCase);
            AssertNoForbiddenContextProperties(property.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) AssertNoForbiddenContextProperties(child);
    }

    private sealed class FrozenExecutor(string content) : IFrozenRequestHeaderClassifier
    {
        public string ModelName => "qwen/qwen3.7-flash";
        public int ContextSize => 1_000_000;
        public string RuntimeDescription => "provider-free test executor";
        public int SharedPrefixTokens => 0;
        public byte[]? CapturedBody { get; private set; }
        public string? CapturedHash { get; private set; }
        public int CapturedMaxTokens { get; private set; }
        public string? CapturedSystem { get; private set; }
        public string? CapturedUser { get; private set; }
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new NotSupportedException();
        public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens,
            string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            CapturedBody = providerBody.ToArray(); CapturedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(providerBody));
            CapturedMaxTokens = maxTokens; CapturedSystem = systemPrompt; CapturedUser = userMessage;
            return Task.FromResult(new FrozenHeaderExecutionResult(content, "stop", null, "data: synthetic\n\n", 1, 0));
        }
        public void Dispose() { }
    }
}
