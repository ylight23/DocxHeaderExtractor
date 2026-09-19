using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class P4ReachabilityCleanupTests
{
    [Fact]
    public void Retired_pdf_analyst_prompt_profile_keeps_the_frozen_bytes_and_hash()
    {
        var expectedHash = "028ed77b71687bebadd8f5e702b7ad0890e11dca845597cf0a48652e8aec7aab";
        var recomputed = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(PdfStagePromptProfile.PromptProfileBytes)))
            .ToLowerInvariant();

        Assert.Equal(expectedHash, PdfStagePromptProfile.SemanticPromptSha256);
        Assert.Equal(expectedHash, recomputed);
    }

    [Fact]
    public async Task Pdf_authority_route_remains_explicitly_defensive_for_docx_authority_pipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-p4-route-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(
                new PipelineOptions { DisableLlm = true },
                new ForcedPdfRoutePolicy());

            var error = await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.RunAsync(path));

            Assert.Contains("PdfAuthority cannot be selected", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    private sealed class ForcedPdfRoutePolicy : DocxHeaderExtractor.DocumentProcessing.Routing.IAuthorityRoutePolicy
    {
        public DocxHeaderExtractor.DocumentProcessing.Routing.AuthorityRoute Decide(
            DocxHeaderExtractor.DocumentProcessing.Routing.UploadedSource source) =>
            DocxHeaderExtractor.DocumentProcessing.Routing.AuthorityRoute.PdfAuthority;
    }
}
