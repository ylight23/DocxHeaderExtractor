using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests.Architecture;

/// <summary>
/// One upload, one lane.
/// <para>
/// These assert against the production Routing types on purpose. The previous contract test
/// declared its own copies of SourceCapabilities, AuthorityRoute and the policy inside the test
/// namespace and asserted against that copy, so it would have passed unchanged if the production
/// policy had been rewritten or deleted.
/// </para>
/// </summary>
public sealed class AuthorityRoutePolicyWiringTests
{
    [Theory]
    [InlineData(SourceType.Docx, true, AuthorityRoute.DocxAuthority)]
    [InlineData(SourceType.Docx, false, AuthorityRoute.DocxAuthority)]
    [InlineData(SourceType.Pdf, true, AuthorityRoute.PdfAuthority)]
    [InlineData(SourceType.Pdf, false, AuthorityRoute.PdfAuthority)]
    [InlineData(SourceType.Unknown, true, AuthorityRoute.Unsupported)]
    public void The_uploaded_file_alone_decides_the_lane(
        SourceType type, bool analyst, AuthorityRoute expected)
    {
        Assert.Equal(expected, new DefaultAuthorityRoutePolicy().Decide(new UploadedSource(type, analyst)));
    }

    [Fact]
    public void Analyst_availability_never_changes_which_format_owns_the_result()
    {
        // It decides how well a lane can reason, never which lane runs. The old policy let it flip
        // authority from the DOCX to a PDF the user had not uploaded.
        var policy = new DefaultAuthorityRoutePolicy();

        Assert.All(Enum.GetValues<SourceType>(), type =>
            Assert.Equal(
                policy.Decide(new UploadedSource(type, AnalystAvailable: false)),
                policy.Decide(new UploadedSource(type, AnalystAvailable: true))));
    }

    [Fact]
    public async Task A_docx_upload_keeps_docx_authority_even_with_a_same_named_pdf_beside_it()
    {
        // The regression this replaces: the route was decided by PdfTextbookOutline.FindSiblingPdf,
        // so a file nobody uploaded could take authority away from the file that was.
        var directory = Path.Combine(Path.GetTempPath(), $"dhx-sibling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var docx = Path.Combine(directory, "report.docx");
        try
        {
            SampleDocumentFactory.Create(docx);
            await File.WriteAllTextAsync(Path.Combine(directory, "report.pdf"), "%PDF-1.7\n% not a real pdf");
            var policy = new RecordingRoutePolicy();
            using var pipeline = new AuthorityExtractionPipeline(
                new PipelineOptions { DisableLlm = true }, policy);

            var outline = await pipeline.RunAsync(docx);

            Assert.Equal("docx-canonical-vnext", outline.DeterministicRoute);
            Assert.Equal(new UploadedSource(SourceType.Docx, AnalystAvailable: false), policy.LastSource);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_pdf_renamed_as_docx_is_rejected_by_content_not_accepted_by_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-mislabelled-{Guid.NewGuid():N}.docx");
        await File.WriteAllTextAsync(path, "%PDF-1.7\n% a pdf wearing a docx name");
        try
        {
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });

            var error = await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.RunAsync(path));

            Assert.Contains("Pdf", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task A_policy_that_returns_pdf_authority_for_a_docx_upload_fails_loudly()
    {
        // Nothing may quietly produce a DOCX result wearing a PDF label, or the reverse.
        var path = Path.Combine(Path.GetTempPath(), $"dhx-route-policy-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(
                new PipelineOptions { DisableLlm = true },
                new RecordingRoutePolicy(AuthorityRoute.PdfAuthority));

            await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.RunAsync(path));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void An_unreadable_or_foreign_file_is_unknown_rather_than_guessed_from_its_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-foreign-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04]);
        try
        {
            Assert.Equal(SourceType.Unknown, UploadedSourceDetector.Detect(path));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void A_real_docx_is_detected_as_docx_whatever_it_is_called()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-named-wrong-{Guid.NewGuid():N}.pdf");
        try
        {
            SampleDocumentFactory.Create(path);

            Assert.Equal(SourceType.Docx, UploadedSourceDetector.Detect(path));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    private sealed class RecordingRoutePolicy(AuthorityRoute? forced = null) : IAuthorityRoutePolicy
    {
        public UploadedSource? LastSource { get; private set; }

        public AuthorityRoute Decide(UploadedSource source)
        {
            LastSource = source;
            return forced ?? new DefaultAuthorityRoutePolicy().Decide(source);
        }
    }
}
