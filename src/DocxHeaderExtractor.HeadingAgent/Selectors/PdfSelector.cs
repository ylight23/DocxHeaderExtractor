using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>
/// The production PDF heading selector: the V6 direct agent based on D4.12 (docs/experiments/p7-f1-v6-d4.12-owner-trials.md,
/// reference plan of commit 7763b91), promoted on 2026-10-14. The PDF is parsed with the production parser and cut into layout segments (whole
/// pages, else parser layout blocks; at most 48 targets, split further for large facts, 20 lines of context each side); the first request
/// supplies the targets' existing tool facts alongside their alias, page and text. The conversation is <see cref="AgentSelector"/>'s.
/// </summary>
public sealed class PdfSelector : AgentSelector
{
    /// <inheritdoc cref="AgentSelector"/>
    public PdfSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<ModelReply>? onModelReply = null,
        string? instruction = null, int firstSegments = 0)
        : base(handlerFactory, parallelism, onModelReply, instruction, firstSegments) { }

    public override string SelectorId => "p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:facts-in-request:instruction-" + InstructionTag + ":" + BindingPolicyVersion;

    private protected override IAgentSource Load(string path) =>
        AgentSource.Load(path, out var failure) ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
}
