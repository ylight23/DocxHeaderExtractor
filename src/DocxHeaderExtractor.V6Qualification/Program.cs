namespace DocxHeaderExtractor.V6Qualification;

/// <summary>
/// What remains of the qualification project is the V6 heading-agent experiment (P7F1V6*): the qualified code that the production
/// PdfHeadingAgent (in DocxHeaderExtractor.HeadingAgent) is held to byte for byte by the parity tests. It has no command of its own any more; the earlier P5-P7 commands and
/// canaries were removed on 2026-10-14 (see git history). Explicit boundary canary and cohort commands are qualification-only
/// experiments; Web/production never dispatches them.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() is "boundary-static-ab" or "boundary-suggested-c" or "boundary-grounded-d" or "boundary-grounded-cohort" or "boundary-grounded-e" or "boundary-oscillation-canary" or "hierarchy-comparative-canary" or "boundary-label-swap-canary" or "hierarchy-single-child-canary")
        {
            try { return args[0] switch
            {
                "boundary-static-ab" => BoundaryStaticCanaryCommand.Run(args),
                "boundary-grounded-cohort" => BoundaryGroundedCohortCommand.Run(args),
                "boundary-grounded-e" => BoundaryGroundedIndependentCommand.Run(args),
                "boundary-oscillation-canary" => SourceComparativeCanaries.Run(args, hierarchy: false),
                "hierarchy-comparative-canary" => SourceComparativeCanaries.Run(args, hierarchy: true),
                "boundary-label-swap-canary" => AdjudicationBiasCanaries.Run(args, hierarchy: false),
                "hierarchy-single-child-canary" => AdjudicationBiasCanaries.Run(args, hierarchy: true),
                _ => BoundarySuggestedCanaryCommand.Run(args)
            }; }
            catch (Exception e) { Console.Error.WriteLine("Qualification aborted (" + e.GetType().Name + "); raw payload withheld."); return 1; }
        }
        Console.Error.WriteLine("Qualification commands: boundary-static-ab, boundary-suggested-c, boundary-grounded-d, boundary-grounded-cohort, boundary-grounded-e, boundary-oscillation-canary, hierarchy-comparative-canary, boundary-label-swap-canary, hierarchy-single-child-canary (explicit --live required). See git history for retired commands.");
        return 2;
    }
}
