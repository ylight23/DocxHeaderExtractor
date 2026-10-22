namespace DocxHeaderExtractor.V6Qualification;

/// <summary>
/// What remains of the qualification project is the V6 heading-agent experiment (P7F1V6*): the qualified code that the production
/// PdfHeadingAgent (in DocxHeaderExtractor.HeadingAgent) is held to byte for byte by the parity tests. It has no command of its own any more.
/// The comparative canaries and the adjudication canaries were removed later, as qualification-only experiments that production never dispatched.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine("Qualification has no commands: the V6 experiment is checked by the test suite (DocxHeaderExtractor.Tests).");
        return 2;
    }
}
