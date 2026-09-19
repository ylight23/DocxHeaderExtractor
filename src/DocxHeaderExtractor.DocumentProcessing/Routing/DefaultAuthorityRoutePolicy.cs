namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>
/// Routes the uploaded file to the authority that owns its format.
/// <para>
/// One upload, one lane. A DOCX is extracted as a DOCX and a PDF as a PDF; neither format's
/// presence anywhere else on the machine changes how the other is processed. The previous policy
/// asked whether a PDF existed and, if one did, handed authority to the PDF lane - so a file the
/// user never uploaded could replace the source of truth for the file they did.
/// </para>
/// <para>
/// A second format is evidence, never authority. If a PDF rendering of an uploaded DOCX is ever
/// used, it belongs in the cross-modal evidence channel that
/// <c>CanonicalSemanticProductionInput</c> already exposes, reconciled against the DOCX occurrences
/// - not substituted for them.
/// </para>
/// </summary>
public sealed class DefaultAuthorityRoutePolicy : IAuthorityRoutePolicy
{
    public AuthorityRoute Decide(UploadedSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Type switch
        {
            SourceType.Docx => AuthorityRoute.DocxAuthority,
            SourceType.Pdf => AuthorityRoute.PdfAuthority,
            _ => AuthorityRoute.Unsupported,
        };
    }
}
