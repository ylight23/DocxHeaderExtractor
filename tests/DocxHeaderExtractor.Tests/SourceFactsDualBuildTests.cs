using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceFactsDualBuildTests
{
    [Fact]
    public void ExtractWithSourceFacts_returns_direct_source_equivalent_to_compatibility_adapter()
    {
        using var fixture = Fixture.Create(withDuplicateText: true);

        var result = new DocxSlimExtractor().ExtractWithSourceFacts(fixture.Path);
        var adapted = SlimSourceFactsAdapter.Adapt(result.Slim);

        Assert.Equal(JsonSerializer.Serialize(adapted), JsonSerializer.Serialize(result.Source));
    }

    [Fact]
    public void Direct_source_build_preserves_duplicate_text_identity()
    {
        using var fixture = Fixture.Create(withDuplicateText: true);

        var result = new DocxSlimExtractor().ExtractWithSourceFacts(fixture.Path);
        var repeated = result.Source.Paragraphs
            .Where(p => p.Text == "Repeated source text")
            .ToArray();

        Assert.Equal(2, repeated.Length);
        Assert.NotEqual(repeated[0].SourceId, repeated[1].SourceId);
    }

    [Fact]
    public void Source_snapshot_is_independent_of_slim_policy_mutation()
    {
        using var fixture = Fixture.Create();

        var result = new DocxSlimExtractor().ExtractWithSourceFacts(fixture.Path);
        var before = JsonSerializer.Serialize(result.Source);

        Assert.NotEmpty(result.Slim.Paragraphs);
        var paragraph = result.Slim.Paragraphs.First();
        paragraph.Role = Core.Models.ParagraphRole.Normal;
        paragraph.Score = 999;
        paragraph.GuessedLevel = 7;

        Assert.Equal(before, JsonSerializer.Serialize(result.Source));
    }

    [Fact]
    public void Existing_extract_api_returns_the_same_slim_compatibility_shape()
    {
        using var fixture = Fixture.Create();

        var dual = new DocxSlimExtractor().ExtractWithSourceFacts(fixture.Path);
        var legacy = new DocxSlimExtractor().Extract(fixture.Path);

        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(dual.Slim));
    }

    [Fact]
    public void Source_document_contract_contains_no_policy_properties()
    {
        var propertyNames = typeof(Core.Models.SourceParagraph).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Role", propertyNames);
        Assert.DoesNotContain("Score", propertyNames);
        Assert.DoesNotContain("GuessedLevel", propertyNames);
        Assert.DoesNotContain("IsCandidate", propertyNames);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path) => Path = path;

        public string Path { get; }

        public static Fixture Create(bool withDuplicateText = false)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"dhx-source-dual-{Guid.NewGuid():N}.docx");
            SampleDocumentFactory.Create(path);
            if (withDuplicateText)
            {
                using var document = WordprocessingDocument.Open(path, true);
                var body = document.MainDocumentPart!.Document!.Body!;
                body.Append(new Paragraph(new Run(new Text("Repeated source text"))));
                body.Append(new Paragraph(new Run(new Text("Repeated source text"))));
                document.MainDocumentPart.Document.Save();
            }
            return new Fixture(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
