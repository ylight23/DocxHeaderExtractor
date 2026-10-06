namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Structural guards for the promoted source-adapter and heading-authority seam. These assertions
/// deliberately inspect source because a successful build cannot show an accidental dependency on
/// a parser, an OOXML implementation, or a concrete provider.
/// </summary>
public sealed class HeadingAuthorityArchitectureTests
{
    [Fact]
    public void Shared_heading_authority_has_no_parser_or_concrete_provider_dependency()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority");
        var forbidden = new[]
        {
            "UglyToad.PdfPig", "WordprocessingDocument", "OpenRouterHeaderExtractor",
            "LmStudioHeaderExtractor", "SglangHeaderExtractor", "LlamaHeaderExtractor",
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Materializer_does_not_choose_a_document_format()
    {
        var source = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/CanonicalStructureMaterializer.cs"));

        Assert.DoesNotContain("\"docx\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pdf\"", source, StringComparison.Ordinal);
        Assert.Contains("occurrence.SourceKind", source, StringComparison.Ordinal);
        Assert.Contains("sourceParagraph.BoundarySource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Docx_source_adapter_does_not_borrow_pdf_semantic_contracts_or_promote_docx()
    {
        var adapter = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxSourceOccurrenceAdapter.cs"));
        var authority = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/CanonicalSemanticDocxAuthorityAdapter.cs"));

        Assert.DoesNotContain("PdfSourceOccurrenceAdapter", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfSemantic", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("HeadingAuthorityPipeline", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("IFrozenInferenceTransport", authority, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_authority_route_uses_qualified_transport_without_canonical_text_fallback()
    {
        var route = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfCanonicalExtraction.cs"));
        var authority = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority/HeadingAuthorityPipeline.cs"));

        Assert.Contains("PdfSourceOccurrenceAdapter.Build", route, StringComparison.Ordinal);
        Assert.Contains("HeadingAuthorityPipeline.RunAsync", route, StringComparison.Ordinal);
        Assert.Contains("IPdfProductionAuthorizedFrozenRequestClassifier", route, StringComparison.Ordinal);
        Assert.Contains("IFrozenInferenceTransport", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalSemanticTextProductionEntryPoint", route, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_production_source_has_no_reverse_reference_to_qualification_project()
    {
        var project = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/DocxHeaderExtractor.DocumentProcessing.csproj"));
        var production = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing");

        Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification", project, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(production, "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
