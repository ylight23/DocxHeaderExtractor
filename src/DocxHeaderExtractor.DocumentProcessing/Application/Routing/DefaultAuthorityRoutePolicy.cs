namespace DocxHeaderExtractor.Core.Application.Routing;

/// <summary>
/// Normal authority route policy. It consumes only source/provider capability facts;
/// legacy fallback and quality signals are intentionally outside this contract.
/// </summary>
public sealed class DefaultAuthorityRoutePolicy : IAuthorityRoutePolicy
{
    public AuthorityRoute Decide(SourceCapabilities capabilities)
    {
        if (!capabilities.HasDocx) return AuthorityRoute.Unsupported;
        return capabilities.HasPdf && capabilities.AnalystAvailable
            ? AuthorityRoute.PdfAuthority
            : AuthorityRoute.DocxAuthority;
    }
}
