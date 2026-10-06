using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// One streamed SSE delta fragment naming a tool call by its array index - the OpenAI-compatible
/// streaming convention OpenRouter's own docs reference (<c>delta.tool_calls[]</c>) without spelling
/// out the reassembly contract explicitly. <c>Id</c>/<c>FunctionName</c> typically arrive once, in the
/// first fragment for that index; <c>ArgumentsChunk</c> arrives repeatedly and must be concatenated in
/// arrival order, never assumed to arrive whole in one event.
/// </summary>
public sealed record V5ReassembledToolCall(int Index, string? Id, string? FunctionName, string Arguments);

/// <summary>
/// Deterministic, provider-free reassembly of streamed tool-call fragments by index. Never assumes a
/// single-chunk arrival; never reorders arguments text - concatenation is strictly arrival order
/// within an index, and the tool calls themselves are returned ordered by index.
/// </summary>
public static class V5ToolCallArgumentsReassembler
{
    public static IReadOnlyList<V5ReassembledToolCall> Reassemble(IEnumerable<V5ToolCallDeltaFragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        var byIndex = new SortedDictionary<int, (string? Id, string? FunctionName, StringBuilder Arguments)>();
        foreach (var fragment in fragments)
        {
            if (!byIndex.TryGetValue(fragment.Index, out var entry))
            {
                entry = (null, null, new StringBuilder());
                byIndex[fragment.Index] = entry;
            }
            entry.Id ??= fragment.Id;
            entry.FunctionName ??= fragment.FunctionName;
            if (fragment.ArgumentsChunk is not null) entry.Arguments.Append(fragment.ArgumentsChunk);
            byIndex[fragment.Index] = entry;
        }
        return byIndex.Select(pair => new V5ReassembledToolCall(pair.Key, pair.Value.Id, pair.Value.FunctionName, pair.Value.Arguments.ToString())).ToArray();
    }
}

/// <summary>
/// Where a forced-tool-call response landed, before treating a successful call as semantic authority.
/// Each layer is kept distinct rather than collapsed into one fatal bucket, so a future real canary can
/// tell "the model ignored the forced choice" apart from "it called the right function with malformed
/// JSON" apart from "the arguments were well-formed JSON but violated the task's arity rules" apart
/// from "the arguments were valid but named source text the binder could not verify".
/// </summary>
public enum V5ForcedToolOutcome
{
    NO_TOOL_CALL,
    MULTIPLE_TOOL_CALLS,
    WRONG_TOOL_NAME,
    ARGUMENTS_NOT_JSON,
    ARGUMENT_SCHEMA_INVALID,
    TASK_CONTRACT_INVALID,
    BINDING_REFUSED,
    BOUND,
}

public sealed record V5ForcedToolQualification(
    V5ForcedToolOutcome Outcome,
    string? Detail,
    SemanticClaimResponseV2_1? Response,
    ClaimBindingResultV2_1? Binding);

/// <summary>
/// Classifies one forced-tool-call response, then - only once a well-formed, in-contract claim set
/// exists - hands it to the unchanged v2.1 codec and <see cref="ExactClaimBinderV2_1"/>. Never repairs,
/// never coerces a relation+value into a relation, never drops an unknown field to make parsing
/// succeed: every layer fails closed on its own defect rather than falling through to the next.
/// </summary>
public static class V5ForcedToolQualifierV1
{
    private static readonly string[] TaskContractIssueMarkers =
    [
        "predicate-not-in-contract", "relation-has-value", "unary-claim-has-object",
        "relation-missing-object", "evidence-needs-missing", "evidence-need",
    ];

    public static V5ForcedToolQualification Qualify(
        IReadOnlyList<V5ReassembledToolCall> toolCalls, string expectedToolName, string requestId,
        DocumentTaskContract contract, IReadOnlyList<SemanticSourceAtom> atoms, ClaimBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToolName);

        if (toolCalls.Count == 0) return new(V5ForcedToolOutcome.NO_TOOL_CALL, null, null, null);
        if (toolCalls.Count > 1) return new(V5ForcedToolOutcome.MULTIPLE_TOOL_CALLS, $"received {toolCalls.Count} tool calls", null, null);

        var call = toolCalls[0];
        if (!string.Equals(call.FunctionName, expectedToolName, StringComparison.Ordinal))
            return new(V5ForcedToolOutcome.WRONG_TOOL_NAME, $"expected '{expectedToolName}', got '{call.FunctionName ?? "(null)"}'", null, null);

        if (string.IsNullOrEmpty(call.Arguments))
            return new(V5ForcedToolOutcome.ARGUMENTS_NOT_JSON, "empty arguments", null, null);

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(call.Arguments);
        }
        catch (JsonException ex)
        {
            return new(V5ForcedToolOutcome.ARGUMENTS_NOT_JSON, ex.Message, null, null);
        }

        using (arguments)
        {
            SemanticClaimResponseV2_1 response;
            try
            {
                response = SemanticClaimResponseCodecV2_1.Parse(arguments.RootElement, contract);
            }
            catch (InvalidOperationException ex)
            {
                var outcome = TaskContractIssueMarkers.Any(marker => ex.Message.Contains(marker, StringComparison.Ordinal))
                    ? V5ForcedToolOutcome.TASK_CONTRACT_INVALID
                    : V5ForcedToolOutcome.ARGUMENT_SCHEMA_INVALID;
                return new(outcome, ex.Message, null, null);
            }

            var binding = ExactClaimBinderV2_1.Bind(requestId, response.Claims, atoms, scope);
            if (binding.Bound.Count == 0 && binding.Refusals.Count > 0)
                return new(V5ForcedToolOutcome.BINDING_REFUSED, string.Join("; ", binding.Refusals.Values.Distinct()), response, binding);

            return new(V5ForcedToolOutcome.BOUND, null, response, binding);
        }
    }
}
