using System.Reflection;
using System.Runtime.CompilerServices;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceFactsCompatibilityTests
{
    [Fact]
    public void Adapter_preserves_all_source_and_normalized_fact_groups()
    {
        var paragraph = new SlimParagraph
        {
            Index = 7,
            StableId = "body[1]/p[7]",
            Text = "1. Title",
            TextSpans = [new SlimTextSpan(0, 8, true, false, true, 14)],
            LineBreakOffsets = [4],
            SourceSegments = [new SlimSourceSegment(0, 8, 2, 11)],
            StyleId = "Heading1",
            StyleName = "Heading 1",
            OutlineLevel = 0,
            Bold = true,
            Italic = false,
            Underline = true,
            AllCaps = false,
            FontSizePt = 14,
            Alignment = "center",
            NumberingId = 3,
            InContentControl = true,
            NumberingLevel = 0,
            NumberLabel = "1.",
            NumberingFormat = "decimal",
            KeepNext = true,
            PageBreakBefore = true,
            TableDepth = 1,
            SectionIndex = 2,
            Role = ParagraphRole.StyledHeading,
            GuessedLevel = 1,
            Score = 1,
            BodyFontSizePt = 11,
            HasBuiltInHeadingStyle = true,
            InTableOfContents = true,
            VerifiedHeadingEnd = 8,
            VerifiedBodyStart = 8,
            VerifiedBoundarySource = "test-only",
        };
        var slim = new SlimDocument
        {
            FileName = "source.docx",
            SourcePath = "source.docx",
            Paragraphs = [paragraph],
            PageHeaders = ["Header"],
            PageFooters = ["Footer"],
            DefaultFontSizePt = 10,
        }.Build();

        var source = SlimSourceFactsAdapter.Adapt(slim);
        var mapped = Assert.Single(source.Paragraphs);

        Assert.Equal("source.docx", source.DocumentId);
        Assert.Equal("docx", source.SourceKind);
        Assert.Equal("body[1]/p[7]", mapped.SourceId);
        Assert.Equal(7, mapped.SourceOrdinal);
        Assert.Equal("1. Title", mapped.Text);
        Assert.Equal(new SourceTextRunSpan(0, 8, true, false, true, 14), Assert.Single(mapped.TextSpans));
        Assert.Equal([4], mapped.LineBreakOffsets);
        Assert.Equal(new SourceSegment(0, 8, 2, 11), Assert.Single(mapped.SourceSegments));
        Assert.Equal("Heading1", mapped.Style.StyleId);
        Assert.Equal(0, mapped.Style.OutlineLevel);
        Assert.True(mapped.Style.Bold);
        Assert.Equal(3, mapped.Numbering.NumberingId);
        Assert.Equal("1.", mapped.Numbering.NumberLabel);
        Assert.Equal("decimal", mapped.Numbering.NumberingFormat);
        Assert.True(mapped.Layout.InContentControl);
        Assert.Equal(1, mapped.Layout.TableDepth);
        Assert.Equal(2, mapped.Layout.SectionIndex);

        Assert.DoesNotContain("Score", typeof(SourceParagraph).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Role", typeof(SourceParagraph).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("GuessedLevel", typeof(SourceParagraph).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void Duplicate_text_paragraphs_keep_distinct_source_identity()
    {
        var slim = new SlimDocument
        {
            FileName = "duplicate.docx",
            SourcePath = "duplicate.docx",
            Paragraphs =
            [
                new SlimParagraph { Index = 1, StableId = "body[1]/p[1]", Text = "Repeated" },
                new SlimParagraph { Index = 2, StableId = "body[1]/p[2]", Text = "Repeated" },
            ],
        }.Build();

        var source = SlimSourceFactsAdapter.Adapt(slim);

        Assert.Equal(2, source.Paragraphs.Count);
        Assert.Equal(["body[1]/p[1]", "body[1]/p[2]"], source.Paragraphs.Select(p => p.SourceId));
        Assert.Equal(source.Paragraphs[0].Text, source.Paragraphs[1].Text);
    }

    [Fact]
    public void Source_contract_properties_are_init_only_and_collections_are_read_only()
    {
        var contractTypes = new[]
        {
            typeof(SourceDocument), typeof(SourceParagraph), typeof(SourceTextRunSpan),
            typeof(SourceSegment), typeof(SourceStyleFacts), typeof(SourceNumberingFacts),
            typeof(SourceLayoutFacts),
        };

        foreach (var type in contractTypes)
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.SetMethod is null) continue;
            Assert.Contains(typeof(IsExternalInit), property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        }

        var slim = new SlimDocument
        {
            FileName = "readonly.docx",
            SourcePath = "readonly.docx",
            Paragraphs = [new SlimParagraph { Index = 0, StableId = "p0", Text = "x" }],
        }.Build();
        var source = SlimSourceFactsAdapter.Adapt(slim);

        Assert.IsNotType<List<SourceParagraph>>(source.Paragraphs);
        Assert.IsNotType<List<int>>(source.Paragraphs[0].LineBreakOffsets);
        Assert.IsNotType<List<SourceSegment>>(source.Paragraphs[0].SourceSegments);
    }
}
