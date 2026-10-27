using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Conversation;

/// <summary>
/// One problem a <see cref="ToolConversation"/> works on: the request it sends each turn, the tools the model may call, how a final answer is
/// judged and what the conversation ends with. The loop owns the turns, retries, budget and tool rounds; the task owns everything about its
/// problem. Heading selection is one task (HeadingAgent/Runtime/SelectionTask); another problem writes its own.
/// </summary>
internal interface IConversationTask<TOutcome>
{
    /// <summary>The id the model calls and the outcome carry (one segment, one review).</summary>
    string UnitId { get; }

    ConversationLimits Limits { get; }

    IConversationTools Tools { get; }

    /// <summary>The text of the user message that carries the images the tools returned in a turn.</summary>
    string ImagesNote { get; }

    /// <summary>The request of one turn: the task's payload, the conversation so far and this turn's tool_choice.</summary>
    PreparedRequest Build(JsonArray history, string toolChoice);

    /// <summary>Opt-in recovery from an exhausted completion budget. Never accepts a partial answer.
    /// Returns false when the task's bounded recovery allowance has been exhausted.</summary>
    bool TryIncreaseCompletionBudget() => false;

    /// <summary>The tool_choice of the turns after a repair of <paramref name="failure"/>, in order.</summary>
    IReadOnlyList<string> ChoicesAfterRepair(string failure);

    /// <summary>The user message that names a refused answer's failure.</summary>
    string RepairMessage(string failure, IReadOnlyList<string>? missing, string? detail);

    /// <summary>Called for every reply that calls tools, before the calls run.</summary>
    void NoteToolRound(ModelTurn reply);

    /// <summary>Judges a final answer (a reply without tool calls): the outcome, or a repair turn.</summary>
    Verdict<TOutcome> Judge(ModelTurn reply, ConversationState state);

    /// <summary>The outcome of a conversation the loop ended (budget, call failure, protocol breach).</summary>
    TOutcome Stop(ConversationState state, string status, string failure);
}

/// <summary>The read-only tools of one conversation, as the loop runs them.</summary>
internal interface IConversationTools
{
    /// <summary>The names the loop reserves rejection room for in the tool rounds still to come.</summary>
    IReadOnlyList<string> ReservedNames { get; }
    int MaxResultBytes { get; }
    int RejectionBytes(string evidenceId, string? name);
    ToolOutput Reject(string evidenceId, string? name, string? rawArguments, string code);
    ToolOutput Execute(string evidenceId, string? name, string? rawArguments);
    /// <summary>The images the calls since the last take returned, in call order; they travel in a user message after the tool results.</summary>
    IReadOnlyList<JsonObject> TakeImages();
}

/// <summary>What one tool call returned: its status ("OK" or a refusal) and the exact bytes of the tool message.</summary>
internal sealed record ToolOutput(string Status, byte[] Content);

/// <summary>The limits of one conversation.</summary>
/// <param name="MaxTurns">Turns before repairs; each repair adds two.</param>
/// <param name="MaxToolTurns">Turns that declare tool_choice auto; later turns ask for the answer (none).</param>
/// <param name="MaxCallsPerTurn">Calls run per turn; the rest are refused.</param>
/// <param name="MaxToolMessageBytes">The content bytes of every tool message of the conversation together.</param>
/// <param name="TransportRetries">How many times a call the transport failed is sent again.</param>
/// <param name="RetriableTransport">The transport failures sent again.</param>
/// <param name="RetryOnceCodes">Failure codes (matched as parts of the code) sent once more, beyond the transport retries.</param>
internal sealed record ConversationLimits(int MaxTurns, int MaxToolTurns, int MaxCallsPerTurn, int MaxToolMessageBytes, int TransportRetries,
    IReadOnlyList<string> RetriableTransport, IReadOnlyList<string> RetryOnceCodes);

/// <summary>What the task says about a final answer.</summary>
internal abstract record Verdict<TOutcome>
{
    private Verdict() { }

    /// <summary>The conversation ends with this outcome.</summary>
    internal sealed record Finish(TOutcome Outcome) : Verdict<TOutcome>;

    /// <summary>The answer is refused and the model gets a repair turn naming <paramref name="Failure"/>. With
    /// <paramref name="StopIfUnchanged"/>, the same refused answer sent again word for word ends the conversation instead.</summary>
    internal sealed record Repair(string Failure, IReadOnlyList<string>? Missing = null, string? Detail = null, bool StopIfUnchanged = true)
        : Verdict<TOutcome>;
}
