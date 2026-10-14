namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// What remains of the qualification project is the V6 heading-agent experiment (P7F1V6*): the qualified code that the production
/// PdfHeadingAgent is held to byte for byte by the parity tests. It has no command of its own any more; the earlier P5-P7 commands and
/// canaries were removed on 2026-10-14 (see git history).
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("dhx-v5-qualify has no commands: it keeps the qualified V6 heading-agent code for the parity tests. See git history for the retired commands.");
        return 2;
    }
}
