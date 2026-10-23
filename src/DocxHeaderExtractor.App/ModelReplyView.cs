using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.App;

/// <summary>One model reply as a host shows it: what the model said, the tools it called and the results it was given. Hosts read replies
/// through this type, so they never name the agents' own types.</summary>
public sealed record ModelReplyView(
    string Unit,
    int Turn,
    string? Reasoning,
    string? Content,
    string? FinishReason,
    string RequestSha256,
    string? PrecedingSelected,
    IReadOnlyList<ToolCallView> ToolCalls,
    IReadOnlyList<ToolResultView> ToolResults);

/// <summary>A tool call of a reply: the tool's name and the arguments as the model wrote them.</summary>
public sealed record ToolCallView(string? Name, string Arguments);

/// <summary>A tool result the conversation gave the model: the call it answers and the content the tool returned.</summary>
public sealed record ToolResultView(string? ToolCallId, string Content);

public static class ModelReplyViews
{
    public static ModelReplyView From(ModelReply r) => new(
        r.Unit, r.Turn, r.Reasoning, r.Content, r.FinishReason, r.RequestSha256, r.PrecedingSelected,
        r.ToolCalls.Select(c => new ToolCallView(c.Name, c.Arguments)).ToArray(),
        r.ToolResults.Select(x => new ToolResultView(x.ToolCallId, x.Content)).ToArray());

    /// <summary>The reply callback of the agents, adapted to the host's view. Null stays null: nothing is recorded.</summary>
    public static Action<ModelReply>? Adapt(Action<ModelReplyView>? onModelReply) =>
        onModelReply is null ? null : r => onModelReply(From(r));
}
