namespace DocxHeaderExtractor.DocumentProcessing.Routing;

public interface IAuthorityRoutePolicy
{
    AuthorityRoute Decide(UploadedSource source);
}
