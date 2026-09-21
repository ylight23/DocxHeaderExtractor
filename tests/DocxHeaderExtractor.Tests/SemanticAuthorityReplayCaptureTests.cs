using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticAuthorityReplayCaptureTests
{
    [Fact]
    public async Task Live_boundary_cut_json_uses_parser_then_capture_before_ownership_filter()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("p1", 1, "Heading", new SourceAnchor { SourceType = "DOCX", ParagraphId = "p1" }, new(0, 7))]);
        var raw = "{\"headings\":[{\"sourceAlias\":\"S0001\",\"isHeading\":true,\"verbatimText\":\"Heading\",\"semanticRole\":\"SECTION\"},{\"sourceAlias\":\"S9999\",\"isHeading\":true,\"verbatimText\":\"Invented\",\"semanticRole\":\"SECTION\"}]}";
        var input = new CanonicalSemanticProductionInput(
            catalog, null, "source-hash", [new CanonicalSemanticPageEvidence("P0001", true, 0, "test")],
            [], [], [], [], DocumentId: "DOC-LIVE-CAPTURE", SourceEvidence: Evidence("S0001", "Heading"))
        {
            ReplayCapture = new SemanticAuthorityCaptureMetadata(
                "DOCX", "source-universe-hash", "synthetic-model", "boundary-cut", "prompt-hash"),
        };

        using var classifier = new RawJsonClassifier(raw);
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(classifier);
        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(input, model);

        var bundle = result.ReplayBundle;
        Assert.NotNull(bundle);
        Assert.Equal(2, bundle.Proposals.Count);
        Assert.Contains(bundle.Proposals, proposal => proposal.SourceAlias == "S9999");
        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Equal(SemanticAuthorityReplayHashing.RawModelResponseHash([raw]), bundle.RawModelResponseHash);
    }

    [Fact]
    public async Task Live_model_parse_is_captured_before_source_aware_validation()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("p1", 1, "Heading", new SourceAnchor { SourceType = "DOCX", ParagraphId = "p1" }, new(0, 7))]);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var parsed = new[]
        {
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION"),
            new CanonicalSemanticProposal("S9999", true, "Invented", SemanticRole: "SECTION"),
        };
        var rawHash = SemanticAuthorityReplayHashing.RawModelResponseHash([JsonSerializer.Serialize(new { headings = parsed })]);
        var input = new CanonicalSemanticProductionInput(
            catalog, null, "source-hash", [new CanonicalSemanticPageEvidence("P0001", true, 0, "test")],
            [], [], [], [], DocumentId: "DOC-CAPTURE", SourceEvidence: Evidence("S0001", "Heading"))
        {
            ReplayCapture = new SemanticAuthorityCaptureMetadata(
                "DOCX", "source-universe-hash", "synthetic-model", "offline-test-route", "prompt-hash"),
        };

        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(
            input,
            new ParsedProposalModel(parsed, rawHash));

        var bundle = result.ReplayBundle;
        Assert.NotNull(bundle);
        Assert.Equal(parsed, bundle.Proposals);
        Assert.Equal(rawHash, bundle.RawModelResponseHash);
        Assert.Contains(result.ContractIssues, issue => issue.Code == "UNKNOWN_ALIAS");
        Assert.Equal(1, result.ContractInvalidProposalCount);
        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Equal("S0001", result.TextPipeline.BoundHeadings[0].Parts[0].Alias);
        Assert.Equal(aliases, bundle.AliasCatalog);
    }

    [Fact]
    public void Atomic_writer_reloads_one_valid_replay_authority_artifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = SemanticAuthorityReplayBundleFactory.Create(
                "DOC-CAPTURE", "PDF", "source-hash", "source-universe-hash",
                [new SemanticSourceAlias("S0001", "p1", 1, "Heading", new(0, 7))],
                "synthetic-model", "offline-test-route", "prompt-hash", "raw-hash",
                [new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION")]);

            var path = SemanticAuthorityReplayArtifactWriter.WriteAtomic(bundle, directory);
            var secondPath = SemanticAuthorityReplayArtifactWriter.WriteAtomic(bundle, directory);
            var restored = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path))!;

            Assert.Equal(path, secondPath);
            Assert.Equal(bundle.BundleHash, restored.BundleHash);
            Assert.Empty(SemanticAuthorityReplay.Validate(restored));
            Assert.Single(Directory.GetFiles(directory, "*.json"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Required_capture_fails_closed_when_bundle_is_missing()
    {
        var request = new SemanticAuthorityReplayCaptureRequest(
            new SemanticAuthorityCaptureMetadata("PDF", "universe", "model", "route", "prompt"),
            Path.Combine(Path.GetTempPath(), "dhx-replay-missing-" + Guid.NewGuid().ToString("N")));

        var error = Assert.Throws<InvalidOperationException>(() => request.Persist(null));

        Assert.Equal("REPLAY_BUNDLE_REQUIRED_BUT_NOT_CAPTURED", error.Message);
    }

    [Fact]
    public void Optional_capture_reports_persistence_failure_without_throwing()
    {
        var occupiedPath = Path.Combine(Path.GetTempPath(), "dhx-replay-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(occupiedPath, "occupied");
        try
        {
            var request = new SemanticAuthorityReplayCaptureRequest(
                new SemanticAuthorityCaptureMetadata("PDF", "universe", "model", "route", "prompt"),
                occupiedPath,
                RequireReplayBundle: false);
            var bundle = SemanticAuthorityReplayBundleFactory.Create(
                "DOC-CAPTURE", "PDF", "source", "universe",
                [new SemanticSourceAlias("S0001", "p1", 1, "Heading", new(0, 7))],
                "model", "route", "prompt", "raw", [new CanonicalSemanticProposal("S0001", true, "Heading")]);

            var result = request.Persist(bundle);

            Assert.False(result.Persisted);
            Assert.NotNull(result.Error);
        }
        finally
        {
            if (File.Exists(occupiedPath)) File.Delete(occupiedPath);
        }
    }

    private static IReadOnlyList<CanonicalSemanticSourceEvidence> Evidence(string alias, string text) =>
        [new(
            alias, "p1", 1, text, "body", null, 0, false, false, [], new { }, new { }, [], [], [], [], [],
            new SemanticCandidateAttentionHint(alias, false, "test"))];

    private sealed class ParsedProposalModel(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        string rawHash) : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult(
                proposals, new CanonicalSemanticInferenceTelemetry("synthetic", "stop"))
            {
                ParsedProposals = proposals,
                RawModelResponseHash = rawHash,
            });
    }

    private sealed class RawJsonClassifier(string raw) : IHeaderClassifier
    {
        public string ModelName => "synthetic-model";
        public int ContextSize => 1024;
        public string RuntimeDescription => "synthetic raw JSON";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            Task.FromResult(raw);

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
