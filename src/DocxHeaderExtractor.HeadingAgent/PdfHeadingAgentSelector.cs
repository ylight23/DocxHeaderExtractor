using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The production PDF heading selector: the V6 direct agent with the method frozen in D4.12 (docs/experiments/p7-f1-v6-d4.12-owner-trials.md,
/// reference plan of commit 7763b91), promoted on 2026-10-14. The PDF is parsed with the production parser and cut into layout segments (whole
/// pages, else parser layout blocks; at most 48 targets, 20 lines of context each side); the request shows alias, page and text, and the layout
/// comes from the facts tool. The conversation is <see cref="HeadingAgentSelector"/>'s.
/// </summary>
public sealed class PdfHeadingAgentSelector : HeadingAgentSelector
{
    /// <inheritdoc cref="HeadingAgentSelector"/>
    public PdfHeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<string, string?>? onModelReply = null,
        string? instruction = null, int firstSegments = 0)
        : base(handlerFactory, parallelism, onModelReply, instruction, firstSegments) { }

    public override string SelectorId => "p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:instruction-" + InstructionTag;

    private protected override IAgentSource Load(string path) =>
        AgentSource.Load(path, out var failure) ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
}
