namespace DocxHeaderExtractor.Core.Application.Routing;

public interface IAuthorityRoutePolicy
{
    AuthorityRoute Decide(SourceCapabilities capabilities);
}
