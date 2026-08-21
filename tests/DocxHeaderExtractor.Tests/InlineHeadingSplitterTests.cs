using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class InlineHeadingSplitterTests
{
    [Fact]
    public void DoesNotRecutTaggedPdfGroundedTitle()
    {
        const string text = "1. Schedule: the meeting will begin at 09:00.";
        var paragraph = new SlimParagraph
        {
            Index = 1,
            Text = text,
            TextSpans =
            [
                new SlimTextSpan(0, 12, true, false, false, 13),
                new SlimTextSpan(12, text.Length, false, false, false, 13),
            ],
        };
        var heading = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = "Schedule",
            OriginalText = text,
            ConfidenceBasis = PdfTaggedEvidenceOutline.Basis,
        };
        var document = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [paragraph] }.Build();

        Assert.Equal(0, InlineHeadingSplitter.Apply([heading], document));
        Assert.Equal("Schedule", heading.Text);
    }

    [Fact]
    public void Splits_at_bold_to_normal_run_boundary_with_separator()
    {
        const string text = "2.3.1. Thành công: Tỉ lệ thành công: 20%";
        var p = new SlimParagraph
        {
            Index = 1,
            Text = text,
            TextSpans =
            [
                new SlimTextSpan(0, 17, true, false, false, 13),
                new SlimTextSpan(17, text.Length, false, false, false, 13),
            ],
        };
        var h = new HeadingRecord { Index = 1, Level = 3, Text = text };
        var doc = new SlimDocument { FileName = "test.docx", SourcePath = "test.docx", Paragraphs = [p] }.Build();

        var count = InlineHeadingSplitter.Apply([h], doc);

        Assert.Equal(1, count);
        Assert.Equal("2.3.1. Thành công", h.Text);
        Assert.Equal("Tỉ lệ thành công: 20%", h.InlineBody);
        Assert.Equal(new TextOffsetSpan(0, 17), h.HeadingSpan);
        Assert.Equal(new TextOffsetSpan(19, text.Length), h.InlineBodySpan);
    }

    [Fact]
    public void Does_not_split_a_colon_inside_uniform_or_ambiguous_heading()
    {
        const string text = "2.3.1. Thành công: Kết quả, nguyên nhân và bài học";
        var p = new SlimParagraph
        {
            Index = 1,
            Text = text,
            TextSpans = [new SlimTextSpan(0, text.Length, true, false, false, 13)],
        };

        Assert.False(InlineHeadingSplitter.TryFindBoundary(p, out _, out _));
    }

    [Fact]
    public void Does_not_split_when_bold_is_only_the_numbering_prefix()
    {
        const string text = "2.3.1. Thành công";
        var p = new SlimParagraph
        {
            Index = 1,
            Text = text,
            TextSpans =
            [
                new SlimTextSpan(0, 6, true, false, false, 13),
                new SlimTextSpan(6, text.Length, false, false, false, 13),
            ],
        };

        Assert.False(InlineHeadingSplitter.TryFindBoundary(p, out _, out _));
    }

    [Theory]
    [InlineData("1.MUC (chỉ số tổng hợp): 5005/2401", "1.MUC (chỉ số tổng hợp)", "5005/2401")]
    [InlineData("2. MB quân sự ta (tổng số tốp/lần chiếc/tốp đêm): 32/32/0", "2. MB quân sự ta (tổng số tốp/lần chiếc/tốp đêm)", "32/32/0")]
    [InlineData("3. MB quân sự nước ngoài (tổng số tốp/số chiếc/tốp đêm): 02/02/0 (0/0)", "3. MB quân sự nước ngoài (tổng số tốp/số chiếc/tốp đêm)", "02/02/0 (0/0)")]
    [InlineData("a. KQ Trung Quốc: 02/02/0", "a. KQ Trung Quốc", "02/02/0")]
    [InlineData("4. Phát hiện của đài QSPK, VQSM (lượt tốp/đài, vọng quan sát): 1.508/92", "4. Phát hiện của đài QSPK, VQSM (lượt tốp/đài, vọng quan sát)", "1.508/92")]
    public void Splits_numeric_payload_after_separator_without_keyword_rules(
        string text, string expectedHeading, string expectedBody)
    {
        var p = new SlimParagraph { Index = 1, Text = text };
        var h = new HeadingRecord { Index = 1, Level = 2, Text = text };
        var doc = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [p] }.Build();

        Assert.Equal(1, InlineHeadingSplitter.Apply([h], doc));
        Assert.Equal(expectedHeading, h.Text);
        Assert.Equal(expectedBody, h.InlineBody);
        Assert.Equal("NumericPayloadAfterSeparator", h.BoundarySource);
        Assert.NotNull(p.VerifiedHeadingEnd);
        Assert.NotNull(p.VerifiedBodyStart);
    }

    [Fact]
    public void Does_not_split_semantic_subtitle_after_colon_when_suffix_contains_words()
    {
        var p = new SlimParagraph { Index = 1, Text = "2.3.1. Thành công: Kết quả và bài học kinh nghiệm" };

        Assert.False(InlineHeadingSplitter.TryFindBoundary(p, out _, out _));
    }

    [Fact]
    public void Does_not_rewrite_typed_slices_from_same_paragraph()
    {
        const string text = "SECTION V: body before list: 1. Loan guarantees: details 2. Payment guarantees: details";
        var p = new SlimParagraph { Index = 1, Text = text };
        var first = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = "SECTION V: body before list",
            ConfidenceBasis = "typed_number_depth",
        };
        var second = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = "1. Loan guarantees",
            ConfidenceBasis = "typed_number_depth",
        };
        var doc = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [p] }.Build();

        Assert.Equal(0, InlineHeadingSplitter.Apply([first, second], doc));
        Assert.Equal("SECTION V: body before list", first.Text);
        Assert.Equal("1. Loan guarantees", second.Text);
    }

    [Fact]
    public void Does_not_split_outline_anchor_table_heading_with_semicolon_in_title()
    {
        const string text = "33. Loss of or Damage to Property; Accident or Injury to Workers; Indemnification";
        var p = new SlimParagraph { Index = 1, Text = text };
        var h = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = text,
            ConfidenceBasis = "outline_anchor_table_custom_style",
        };
        var doc = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [p] }.Build();

        Assert.Equal(0, InlineHeadingSplitter.Apply([h], doc));
        Assert.Equal(text, h.Text);
        Assert.Null(h.InlineBody);
    }

    [Fact]
    public void Does_not_split_outline_level_declared_heading_with_colon_in_title()
    {
        const string text = "J. Second Stage: Evaluation of Technical Part";
        var p = new SlimParagraph { Index = 1, Text = text };
        var h = new HeadingRecord
        {
            Index = 1,
            Level = 1,
            Text = text,
            ConfidenceBasis = "outline_level_declared",
        };
        var doc = new SlimDocument { FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [p] }.Build();

        Assert.Equal(0, InlineHeadingSplitter.Apply([h], doc));
        Assert.Equal(text, h.Text);
        Assert.Null(h.InlineBody);
    }

    [Fact]
    public void Paragraph_splitter_accepts_colon_after_labelled_roman_marker()
    {
        const string text =
            "Opening notes SESSION I: Welcome and meeting objectives SESSION II: Update on the cycle";

        var slices = ParagraphHeadingSplitter.Split(text);

        Assert.Equal(2, slices.Count);
        Assert.Equal("SESSION I: Welcome and meeting objectives", slices[0].Text);
        Assert.Equal("SESSION II: Update on the cycle", slices[1].Text);
    }
}
