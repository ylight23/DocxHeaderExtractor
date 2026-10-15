using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The DOCX heading selector: the V6 method over the paragraphs of a DOCX. The file is read with the production OOXML reader (with the route's
/// extraction options, so that its aliases are the ones the route's source snapshot gives) and cut into fixed windows of 48 paragraphs with 20
/// of context on each side; the request shows alias and text and the style table, and the style, numbering and place of a paragraph come from
/// the facts tool. The conversation is <see cref="HeadingAgentSelector"/>'s.
/// </summary>
public sealed class DocxHeadingAgentSelector : HeadingAgentSelector
{
    readonly ExtractionOptions? extraction;

    /// <param name="extraction">The route's extraction options (for example tables left out); null reads everything.</param>
    /// <inheritdoc cref="HeadingAgentSelector"/>
    public DocxHeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<ModelReply>? onModelReply = null,
        string? instruction = null, int firstSegments = 0, ExtractionOptions? extraction = null)
        : base(handlerFactory, parallelism, onModelReply, instruction ?? DocxAgentProtocol.Instruction, firstSegments) => this.extraction = extraction;

    public override string SelectorId => "docx-v6:" + DocxAgentSource.Version + ":instruction-" + InstructionTag;

    private protected override IAgentSource Load(string path) =>
        DocxAgentSource.Load(path, extraction, out var failure) ?? throw new InvalidOperationException("DOCX_AGENT_SOURCE_REFUSED:" + failure);
}
