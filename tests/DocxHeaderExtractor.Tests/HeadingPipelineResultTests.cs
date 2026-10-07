using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingPipelineResultTests
{
    [Theory]
    [InlineData(false, "1ecae4e686e314640040d7af478b8c848871647d365c8c233e0bb34ab113b7f1")]
    [InlineData(true, "f746783dbbb886c47510330a2a492cdeb9e1e9a0023c770044d21db7a699e672")]
    public void Runtime_envelope_bytes_match_pre_S3_2656dfb(bool web, string expectedHash)
    {
        var graph = ValidatedStructureFactory.Create([]);
        var audit = new PipelineExecutionAudit("fixture", 0, 0, 0, 0, [], [], [], []);
        var catalog = new DocumentSourceCatalog([]);
        var context = new HeadingProjectionContext(
            new Dictionary<string, HeadingProjectionMetadata> { ["h1"] = new() { OutlineText = "Not serialized" } });
        var result = new HeadingPipelineResult(graph, audit, "fixture", new HashSet<string> { "h1" })
        {
            SourceCatalog = catalog,
            ProjectionContext = context,
        };
        // Captured before S3 from the actual StructuralAuthorityResult at 2656dfb.
        // This is a new compatibility fixture, not a frozen artifact rebaseline.
        var options = web ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, options);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.DoesNotContain("Not serialized", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Same(graph, result.Structure);
        Assert.Same(audit, result.Audit);
        Assert.Same(catalog, result.SourceCatalog);
        Assert.Same(context, result.ProjectionContext);
    }

    [Fact]
    public void Empty_lane_remains_representable_without_catalog_audit_or_projection_data()
    {
        var graph = ValidatedStructureFactory.Create([]);
        var result = new HeadingPipelineResult(graph, null, "empty-docx-source");
        Assert.Same(graph, result.Structure);
        Assert.Null(result.Audit);
        Assert.Null(result.SourceCatalog);
        Assert.Null(result.EmittedElementIds);
        Assert.Same(HeadingProjectionContext.Empty, result.ProjectionContext);
        Assert.Equal("empty-docx-source", result.Reason);
        Assert.Same(result, DocxExtractionPipeline.ApplyStructuralQuarantine(result, null));
    }

    [Fact]
    public void Both_heading_pipelines_return_runtime_result_and_retired_authority_envelope_is_absent()
    {
        Assert.Equal(typeof(Task<HeadingPipelineResult>), typeof(DocxHeadingPipeline).GetMethod("RunAsync")!.ReturnType);
        Assert.Equal(typeof(Task<HeadingPipelineResult>), typeof(PdfHeadingPipeline).GetMethod("RunAsync")!.ReturnType);
        Assert.Null(typeof(HeadingPipelineResult).Assembly.GetType(
            "DocxHeaderExtractor.DocumentProcessing.Authority.StructuralAuthorityResult"));
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing");
        Assert.False(File.Exists(Path.Combine(root, "Authority", "StructuralAuthorityResult.cs")));
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            Assert.DoesNotContain("StructuralAuthorityResult", File.ReadAllText(file), StringComparison.Ordinal);
        // Runtime ownership stays out of the Core authority graph.
        Assert.NotEqual(typeof(ValidatedStructure).Assembly, typeof(HeadingPipelineResult).Assembly);
        foreach (var field in new[] { "Audit", "ProjectionContext", "SourceCatalog", "EmittedElementIds", "Reason" })
            Assert.Null(typeof(ValidatedStructure).GetProperty(field));
    }
}
