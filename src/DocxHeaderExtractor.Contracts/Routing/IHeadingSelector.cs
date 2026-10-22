namespace DocxHeaderExtractor.Contracts.Routing;

/// <summary>
/// The PDF heading authority's model side: reads one PDF and answers which source lines are headings, by the source alias the PDF parser
/// gives each line (the alias of <c>DocumentSourceSnapshot</c> atoms). Hierarchy, binding and materialization stay in the shared stages.
/// <para>
/// The PDF lane's only authority since the V6 promotion (2026-10-14). The implementation lives outside this assembly (the V6 direct agent,
/// qualified as the D4.12 method), so the lane depends on this seam, not on the agent.
/// </para>
/// </summary>
public interface IHeadingSelector
{
    /// <summary>Identifies the method in the route audit, for example the agent's arm and instruction.</summary>
    string SelectorId { get; }

    /// <param name="pdfPath">The uploaded PDF; the selector parses it with the same parser as the lane, so the aliases agree.</param>
    /// <param name="apiKey">The provider key of the authorized PDF route.</param>
    Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct);
}

/// <summary>
/// A selector that can take a document its caller has just read from the same file under the same extraction options, so the file is
/// not read and built a second time. The document is used once: the next load of that path takes it; a load of any other path reads the
/// file as usual.
/// </summary>
public interface IPrimedDocumentSelector
{
    void PrimeDocument(string path, DocxHeaderExtractor.Contracts.Docx.ExtractionOptions? extraction, DocxHeaderExtractor.Contracts.Models.SourceDocument document);
}
