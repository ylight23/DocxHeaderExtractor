using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfStyleClusterProfileTests
{
    [Fact]
    public void LearnsBodyBaselineFromMostReadableCharactersAndKeepsHeadingCandidateStyle()
    {
        var lines = new List<PdfLine>();
        for (var i = 0; i < 20; i++)
        {
            lines.Add(new PdfLine(
                Page: i / 5 + 1,
                Y: 500 - i,
                FontSize: 10,
                Text: "This is a long body sentence with ordinary document text.",
                BoldRatio: 0,
                LeadingBoldPrefix: "",
                ItalicRatio: 0,
                Left: 72,
                Right: 420,
                FontName: "sans",
                FillColorKey: "0.00,0.00,0.00"));
        }

        lines.AddRange([
            Heading("Introduction", page: 1),
            Heading("Method", page: 2),
            Heading("Results", page: 3),
            Heading("Conclusion", page: 4),
        ]);

        var profile = PdfStyleClusterProfile.Learn(
            lines,
            line => line.FontSize > 12 && line.Text.Length is >= 4 and <= 80);

        Assert.Equal(new PdfStyleKey(10, "sans", "0.00,0.00,0.00"), profile.BodyStyle);
        Assert.True(profile.IsLikelyTitleStyle(Heading("Appendix", page: 5)));
        Assert.False(profile.IsCandidateStyle(lines[0]));
    }

    private static PdfLine Heading(string text, int page) => new(
        Page: page,
        Y: 720,
        FontSize: 14.2,
        Text: text,
        BoldRatio: 0.8,
        LeadingBoldPrefix: text,
        ItalicRatio: 0,
        Left: 72,
        Right: 240,
        FontName: "serif",
        FillColorKey: "0.00,0.20,0.40");
}
