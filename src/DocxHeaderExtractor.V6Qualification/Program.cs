namespace DocxHeaderExtractor.V6Qualification;

/// <summary>
/// What remains of the qualification project is the V6 heading-agent experiment (P7F1V6*): the qualified code that the production
/// PdfHeadingAgent (in DocxHeaderExtractor.HeadingAgent) is held to byte for byte by the parity tests. It has no command of its own any more; the earlier P5-P7 commands and
/// canaries were removed on 2026-10-14 (see git history). The explicitly invoked boundary-static-ab and boundary-suggested-c
/// commands are qualification-only experiments added on 2026-10-18; Web/production never dispatches them.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() is "boundary-static-ab" or "boundary-suggested-c")
        {
            try { return args[0] == "boundary-static-ab" ? BoundaryStaticCanaryCommand.Run(args) : BoundarySuggestedCanaryCommand.Run(args); }
            catch (Exception e) { Console.Error.WriteLine("Qualification aborted (" + e.GetType().Name + "); raw payload withheld."); return 1; }
        }
        Console.Error.WriteLine("Qualification commands: boundary-static-ab, boundary-suggested-c (explicit --live required). See git history for retired commands.");
        return 2;
    }
}
