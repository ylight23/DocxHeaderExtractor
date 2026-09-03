namespace DocxHeaderExtractor.DocumentProcessing.Routing;

public interface IAuthorityRoutePolicy
{
    AuthorityRoute Decide(SourceCapabilities capabilities);
}
