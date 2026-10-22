using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The DOCX heading selector: the V6 method over the paragraphs of a DOCX. The file is read with the production OOXML reader (with the route's
/// extraction options, so that its aliases are the ones the route's source snapshot gives) and cut into fixed windows of 48 paragraphs with 20
/// of context on each side; the first request shows alias, text, the style table and targetFacts with the paragraphs' existing
/// core facts. The facts tool remains available for other paragraphs. The conversation is <see cref="HeadingAgentSelector"/>'s.
/// </summary>
public sealed class DocxHeadingAgentSelector : HeadingAgentSelector, DocxHeaderExtractor.DocumentProcessing.Routing.IPrimedDocumentSelector
{
    readonly ExtractionOptions? extraction;

    /// <param name="extraction">The route's extraction options (for example tables left out); null reads everything.</param>
    /// <inheritdoc cref="HeadingAgentSelector"/>
    public DocxHeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<ModelReply>? onModelReply = null,
        string? instruction = null, int firstSegments = 0, ExtractionOptions? extraction = null)
        : base(handlerFactory, parallelism, onModelReply, instruction ?? DocxAgentProtocol.Instruction, firstSegments) => this.extraction = extraction;

    public override string SelectorId => "docx-v6:" + DocxAgentSource.Version + ":facts-in-request:instruction-" + InstructionTag + ":" + BindingPolicyVersion;

        private (string Full, DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.ExtractionOptions? Options, DocxHeaderExtractor.Core.Models.SourceDocument Document)? primed;
    private readonly object primedLock = new();

    /// <inheritdoc/>
    public void PrimeDocument(string path, DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.ExtractionOptions? extraction, DocxHeaderExtractor.Core.Models.SourceDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        lock (primedLock) primed = (System.IO.Path.GetFullPath(path), extraction, document);
    }

    private protected override IAgentSource Load(string path)
    {
        (string Full, DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.ExtractionOptions? Options, DocxHeaderExtractor.Core.Models.SourceDocument Document)? take;
        lock (primedLock) { take = primed; primed = null; }
        if (take is { } given && given.Full == System.IO.Path.GetFullPath(path) && ReferenceEquals(given.Options, extraction))
        {
            var source = DocxAgentSource.From(given.Document, out var builtFailure)
                ?? throw new InvalidOperationException("DOCX_AGENT_SOURCE_REFUSED:" + builtFailure);
            source.Integrity = DocxHeaderExtractor.DocumentProcessing.Source.Docx.DocxSourceIntegrity.Read(path);
            return source;
        }
        return DocxAgentSource.Load(path, extraction, out var failure) ?? throw new InvalidOperationException("DOCX_AGENT_SOURCE_REFUSED:" + failure);
    }
}
