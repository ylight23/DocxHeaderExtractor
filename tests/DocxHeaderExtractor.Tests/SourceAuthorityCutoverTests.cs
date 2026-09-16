using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceAuthorityCutoverTests
{
    [Fact]
    public void Native_authority_context_reads_source_text()
    {
        var source = new SourceDocument
        {
            DocumentId = "source.docx",
            FileName = "source.docx",
            SourcePath = "source.docx",
            SourceKind = "docx",
            Paragraphs = [SourceParagraph("p0", "authoritative source text")],
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var authority = DocxAuthorityPipeline.BuildForAudit(policy, Mode());

        Assert.Equal("authoritative source text", authority.Blocks.Single().DisplayText);
        Assert.Equal("authoritative source text", authority.ModelContexts["p0"].Source.RawText);
    }

    [Fact]
    public async Task Docx_authority_produces_generic_structure_before_compatibility_projection()
    {
        var source = new SourceDocument
        {
            DocumentId = "source.docx",
            FileName = "source.docx",
            SourcePath = "source.docx",
            SourceKind = "docx",
            Paragraphs = [SourceParagraph("p0", "1. Authoritative heading", builtInLevel: 1)],
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());

        var result = await DocxAuthorityPipeline.RunAsync(
            policy, Mode(), analyst: null);
        var element = Assert.Single(result.Structure.Elements);
        var projected = Assert.Single(HeadingOutlineProjection.Project(result.Structure));

        Assert.NotEqual(element.Id, Assert.Single(element.Sources).SourceId);
        Assert.Equal("p0", element.Sources[0].SourceId);
        Assert.Equal(new StructuralSpan(0, source.Paragraphs[0].Text.Length), element.Sources[0].Span);
        Assert.Equal("1. Authoritative heading", projected.Text);
        Assert.Null(projected.Level);
        Assert.Equal("p0", projected.SourceId);
    }

    [Fact]
    public async Task Docx_route_materializes_aggregate_batch_telemetry()
    {
        var source = new SourceDocument
        {
            DocumentId = "telemetry.docx",
            FileName = "telemetry.docx",
            SourcePath = "telemetry.docx",
            SourceKind = "docx",
            Paragraphs = Enumerable.Range(0, 3).Select(index => new SourceParagraph
            {
                SourceId = $"p{index}",
                SourceOrdinal = index,
                Text = $"Heading {index}",
                Style = new SourceStyleFacts { StyleName = "Normal", FontSizePt = 11, BuiltInHeadingStyleLevel = 1 },
                Numbering = new SourceNumberingFacts(),
                Layout = new SourceLayoutFacts(),
            }).ToArray(),
        };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var policy = DocxPolicyStateBuilder.Build(source, features,
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());

        using var classifier = new TelemetryClassifier();
        var result = await DocxAuthorityPipeline.RunAsync(policy, Mode(), classifier);
        Assert.NotNull(result.Audit);
        var audit = result.Audit!;
        Assert.NotNull(audit.BatchTelemetry);
        var telemetry = audit.BatchTelemetry!;

        Assert.Equal(source.Paragraphs.Count, telemetry.SourceParagraphCount);
        Assert.Equal(audit.ModelRequests.Count(request => request.ProviderCallAttempted), telemetry.TotalProviderCalls);
        Assert.Equal(audit.RawAnalystResponses.Count, telemetry.TotalResponses);
        Assert.Equal(
            telemetry.RoleProviderCalls + telemetry.SpanProviderCalls + telemetry.HierarchyProviderCalls,
            telemetry.TotalProviderCalls);
        Assert.True(telemetry.RoleBatchCount >= 1);
        Assert.True(telemetry.SpanBatchCount >= 1);
    }

    private static SourceParagraph SourceParagraph(string id, string text, int? builtInLevel = null) => new()
    {
        SourceId = id,
        SourceOrdinal = 0,
        Text = text,
        Style = new SourceStyleFacts { StyleName = "Normal", FontSizePt = 11, BuiltInHeadingStyleLevel = builtInLevel },
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };

    private static DocumentModeReport Mode() => new(DocumentMode.SemanticOnly, 1, 0, 0, 0, 0, 0, false);

    private sealed class TelemetryClassifier : IHeaderClassifier
    {
        public string ModelName => "test/docx-telemetry";
        public int ContextSize => 32_768;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(userMessage);
            if (document.RootElement.TryGetProperty("blocks", out var blocks))
            {
                var pointer = blocks.EnumerateArray().Any(block =>
                    block.TryGetProperty("allowed_start_offsets", out _));
                if (pointer)
                {
                    var items = blocks.EnumerateArray().Select(block => new
                    {
                        id = block.GetProperty("id").GetString()!,
                        heading_span = new { start = 0, end = block.GetProperty("source_length").GetInt32() },
                    }).ToArray();
                    return Task.FromResult(JsonSerializer.Serialize(new { blocks = items }));
                }

                var roleItems = blocks.EnumerateArray().Select(block => new
                {
                    id = block.GetProperty("id").GetString()!,
                    role = "heading_topic",
                    confidence = 0.9,
                    heading_span = new { start = 0, end = block.GetProperty("source_length").GetInt32() },
                }).ToArray();
                return Task.FromResult(JsonSerializer.Serialize(new { blocks = roleItems }));
            }

            return Task.FromResult("{\"items\":[]}");
        }

        public void Dispose() { }
    }
}
