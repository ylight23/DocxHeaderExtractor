using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfTextUtilitiesTests
{
    [Theory]
    [InlineData("I ntrod u ction", "Introduction")]
    [InlineData("I DA Replen ish ment", "IDA Replenishment")]
    [InlineData("Twentieth Replen ish ment of Resou rces (I DA 20)", "Twentieth Replenishment of Resources (IDA 20)")]
    [InlineData("Fi nan cial Busi ness Model", "Financial Business Model")]
    [InlineData("Su m mary of Fi nan cial Resu lts", "Summary of Financial Results")]
    [InlineData("Financial Results an d Portfol io Performan ce", "Financial Results and Portfolio Performance")]
    [InlineData("SECTION III: IDA ’ S FINANCIAL RESOURCES", "SECTION III: IDA’s FINANCIAL RESOURCES")]
    [InlineData("Concessional Scale - up Window – Shorter Maturity Loans (SUW - SML)", "Concessional Scale-up Window – Shorter Maturity Loans (SUW-SML)")]
    public void HeadingReadableRepairsPdfWordFragments(string input, string expected)
    {
        Assert.Equal(expected, PdfTextUtilities.HeadingReadable(input));
    }

    [Theory]
    [InlineData("SECTION I: OVERVIEW")]
    [InlineData("Cash and Investments")]
    [InlineData("Basis of Reporting")]
    public void HeadingReadableDoesNotCollapseNormalPhrases(string text)
    {
        Assert.Equal(text, PdfTextUtilities.HeadingReadable(text));
    }

    [Fact]
    public void CanonicalForMatchIgnoresSpacingAndPunctuationButDoesNotRewriteSource()
    {
        var source = "I ntrod u ction";

        Assert.Equal(PdfTextUtilities.CanonicalForMatch("Introduction"), PdfTextUtilities.CanonicalForMatch(source));
        Assert.Equal("I ntrod u ction", source);
    }
}
