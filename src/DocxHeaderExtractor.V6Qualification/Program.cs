namespace DocxHeaderExtractor.V6Qualification;

/// <summary>
/// What remains of the qualification project is the V6 heading-agent experiment (P7F1V6*): the qualified code that the production
/// PdfHeadingAgent (in DocxHeaderExtractor.HeadingAgent) is held to byte for byte by the parity tests. It has no command of its own any more; the earlier P5-P7 commands and
/// canaries were removed on 2026-10-14 (see git history). The boundary static, suggested and grounded commands were removed later; the two
/// comparative canaries that remain are qualification-only experiments, and Web/production never dispatches them.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() is "boundary-oscillation-canary" or "hierarchy-comparative-canary" or "boundary-label-swap-canary" or "hierarchy-single-child-canary")
        {
            try { return args[0] switch
            {
                "boundary-oscillation-canary" => SourceComparativeCanaries.Run(args, hierarchy: false),
                "hierarchy-comparative-canary" => SourceComparativeCanaries.Run(args, hierarchy: true),
                "boundary-label-swap-canary" => AdjudicationBiasCanaries.Run(args, hierarchy: false),
                "hierarchy-single-child-canary" => AdjudicationBiasCanaries.Run(args, hierarchy: true),
                _ => throw new InvalidOperationException("QUALIFICATION_COMMAND_UNKNOWN")
            }; }
            catch (Exception e) { Console.Error.WriteLine("Qualification aborted (" + e.GetType().Name + "); raw payload withheld."); return 1; }
        }
        Console.Error.WriteLine("Qualification commands: boundary-oscillation-canary, hierarchy-comparative-canary, boundary-label-swap-canary, hierarchy-single-child-canary.");
        return 2;
    }
}
