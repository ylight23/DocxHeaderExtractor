using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Drives the real function-conditioned authority over a real PDF with a recording transport and
/// checks that what it puts on the wire is what was qualified. No provider is called; the fake
/// answers every occurrence OTHER, so the chain stops after the first stage and exactly one request
/// per pack is recorded.
/// </summary>
public sealed class HeadingAuthorityByteParityTests
{
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const string F1SystemPromptSha256 = "b44904b62d8807757ba288ee6f8192081f5a2a8742e91e40b1f447974601202a";

    /// <summary>
    /// Platform-independent gate: the frozen canonical source snapshot (the exact atoms and evidence
    /// the qualification saw) goes through the live authority. This isolates the authority and the
    /// protocol composers from PDF parsing, whose floating-point layout can differ between operating
    /// systems.
    /// </summary>
    [Theory]
    [InlineData("SRC-089", Src089)]
    [InlineData("SRC-095", Src095)]
    public async Task Live_authority_sends_the_frozen_function_request_for_the_first_pack(string documentId, string pdf)
    {
        var frozen = FrozenRow(documentId);
        var snapshot = LoadSnapshot(pdf);
        var authorityInput = snapshot.Rehydrate();
        var source = new SourceOccurrenceUniverse(
            authorityInput.Atoms, [], authorityInput.Evidence,
            authorityInput.SourceAliasUniverseSha256, authorityInput.ModelVisibleEvidenceSha256, authorityInput.SourceSha256,
            new Dictionary<string, HeadingSourceContext>(), new DocumentSourceCatalog([]), [],
            new Dictionary<string, int>())
        { SourceKind = "pdf" };

        await AssertFirstRequestMatches(frozen, source, authorityInput.LayoutBlockByAtom);
    }

    /// <summary>
    /// Same gate through the live PDF parser. It only applies where this platform reproduces the
    /// frozen source universe; elsewhere the parser itself diverges (the dedicated source-universe
    /// tests report that) and the snapshot-driven test above is the authority check.
    /// </summary>
    [Theory]
    [InlineData("SRC-089", Src089)]
    [InlineData("SRC-095", Src095)]
    public async Task Live_parsed_universe_sends_the_frozen_function_request_when_the_platform_reproduces_the_source(string documentId, string pdf)
    {
        var snapshot = LoadSnapshot(pdf);
        var build = PdfSourceOccurrenceAdapter.BuildWithDetails(TestRepository.Path(pdf));
        if (!string.Equals(build.Universe.SourceAliasUniverseHash, snapshot.SourceAliasUniverseSha256, StringComparison.Ordinal))
            return;

        await AssertFirstRequestMatches(FrozenRow(documentId), build.Universe, build.Details.LayoutBlockByAtom);
    }

    private static async Task AssertFirstRequestMatches(
        JsonElement frozen, SourceOccurrenceUniverse source, IReadOnlyDictionary<string, string> layoutBlockByAtom)
    {
        var transport = new RecordingTransport();
        IHeadingAuthority authority = new FunctionConditionedHeadingAuthority(transport, layoutBlockByAtom, () => { });

        var result = await authority.DecideAsync(source, CancellationToken.None);

        Assert.Empty(result.Decisions);
        var first = Assert.IsType<Recorded>(transport.Calls[0]);
        Assert.Equal(F1SystemPromptSha256, Sha256(first.SystemPrompt));
        Assert.Equal(frozen.GetProperty("requestHash").GetString(), Sha256(first.UserMessage));
        Assert.Equal(frozen.GetProperty("providerRequestHash").GetString(), Sha256(first.Body));
        Assert.Equal(frozen.GetProperty("providerRequestBytes").GetInt32(), first.Body.Length);
        Assert.Equal(frozen.GetProperty("maxCompletionTokens").GetInt32(), first.MaxTokens);
    }

    private static JsonElement FrozenRow(string documentId)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6tf1-preflight/two-pack-function-membership-manifest.v1.json")));
        return manifest.RootElement.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("documentId").GetString() == documentId).Clone();
    }

    private static PdfCanonicalSourceSnapshotV1 LoadSnapshot(string pdf)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(pdf));
        var path = TestRepository.Path($"eval/a99-closed-loop/pdf-canonical-source-v1/{sourceHash}.json");
        return JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(
                   File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("snapshot-deserialize-failed");
    }

    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Recorded(byte[] Body, int MaxTokens, string SystemPrompt, string UserMessage);

    private sealed class RecordingTransport : IFrozenInferenceTransport
    {
        public List<Recorded> Calls { get; } = [];
        public string ModelName => "recording";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "recording; no provider";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            Task.FromResult("{\"headings\":[]}");

        public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(
            byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Recorded(providerBody, maxTokens, systemPrompt, userMessage));
            using var request = JsonDocument.Parse(userMessage);
            var decisions = request.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(item => new { occurrence = item.GetProperty("id").GetString(), function = "OTHER" }).ToArray();
            return Task.FromResult(new FrozenInferenceResult(
                JsonSerializer.Serialize(new { decisions }), "stop", null, string.Empty, 0, 0));
        }

        public void Dispose() { }
    }
}
