using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class ExtractionRoutingContractTests
{
    [Theory]
    [InlineData(false, "db349c3207de4652cab345b635a6610baf44220e9a38516930e96d9c83aabb5a",
        "28d34f377a937e37bd19247b79de7890a787bb3130bf3888f317d8d511d619c8")]
    [InlineData(true, "8d4cb7026ac97885d86957bf35ab55c085e08ba801b9c80823b7635a06bb3021",
        "0bd7de40374243cb91722db05ead26b57624f8cc7c753b195fe8d6d142bf4f42")]
    public void Routing_envelope_and_request_bytes_match_pre_S10_e411bd8(bool web, string resultHash, string requestHash)
    {
        var fixture = new DocumentExtractionExecutionResult(
            new DocumentExtractionResult(new DocumentIdentity("fixture", "fixture.pdf", "pdf", "fixture.pdf"),
                new DocumentSourceCatalog([]), ValidatedStructureFactory.Create([]), [], [],
                new DocumentExtractionProvenance("pdf-function-anchor-extent", "fixture-source", 0)),
            new DocumentOutline { File = "fixture.pdf", Headings = [],
                RouteAudit = new PipelineExecutionAudit("fixture", 0, 0, 0, 0, [], [], [], []) });
        // Fixed value-only fixture, not an alternate production way of constructing an uploaded file.
        // Real byte detection/routing is covered separately by ExtractionBoundaryTests.
        var constructor = typeof(UploadedFile).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 4);
        var file = (UploadedFile)constructor.Invoke(["fixture.pdf", "fixture.pdf", SourceType.Pdf, new string('a', 64)]);
        var options = web ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new();
        Assert.Equal(resultHash, Hash(fixture));
        Assert.Equal(resultHash, Hash(fixture with
        {
            HeadingPipeline = new HeadingPipelineResult(fixture.Result.Structure, fixture.Outline.RouteAudit, "runtime-only")
        }));
        Assert.Equal(requestHash, Hash(new DocumentExtractionRequest(file)));
        string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, options)));
    }

    [Fact]
    public void Format_handlers_keep_the_file_only_routing_contract_and_one_execution_envelope()
    {
        var method = typeof(IDocumentExtractionHandler).GetMethod("ExtractAsync")!;
        Assert.Equal(typeof(Task<DocumentExtractionExecutionResult>), method.ReturnType);
        Assert.Equal(new[] { typeof(UploadedFile), typeof(IReadOnlySet<int>), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType));
        Assert.Contains(typeof(IDocumentExtractionHandler), typeof(DocxExtractionHandler).GetInterfaces());
        Assert.Contains(typeof(IDocumentExtractionHandler), typeof(PdfExtractionHandler).GetInterfaces());
        Assert.Equal(new[] { "Result", "Outline" },
            typeof(DocumentExtractionExecutionResult).GetProperties()
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null).Select(p => p.Name));
        var sidecar = typeof(DocumentExtractionExecutionResult).GetProperty("HeadingPipeline")!;
        Assert.Equal(typeof(HeadingPipelineResult), sidecar.PropertyType);
        Assert.NotNull(sidecar.GetCustomAttribute<JsonIgnoreAttribute>());
        Assert.Equal(new[] { typeof(UploadedFile) },
            typeof(DocumentExtractionRequest).GetProperties().Select(p => p.PropertyType));
    }
}
