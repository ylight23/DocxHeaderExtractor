namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>Why a request and its transport options cannot be sent together.</summary>
public sealed record TransportCompatibilityResult(bool IsCompatible, string? Reason, string? Detail)
{
    public static readonly TransportCompatibilityResult Compatible = new(true, null, null);
}

/// <summary>
/// Constraints that hold between a request's content and the transport options it is sent with.
/// <para>
/// Two experiments taught this the expensive way. A run matched every frozen request byte and still
/// sent a different call, because <c>max_tokens</c> is derived from an argument that appears in none
/// of those bytes. Then a run matched every frozen byte and every transport parameter, and the
/// provider refused it outright: <c>response_format: json_object</c> requires the messages to ask
/// for JSON, and neither the messages nor the parameter was wrong on its own.
/// </para>
/// <para>
/// So a call's authority is three things, not two - the model input, the transport parameters, and
/// the constraints between them. This validates the third. It deliberately does not repair
/// anything: silently appending a sentence to a prompt would change bytes a preflight had frozen,
/// which is the one thing a transport layer must never do.
/// </para>
/// </summary>
public static class TransportCompatibility
{
    public const string JsonObjectResponseFormat = "json_object";
    public const string IncompatibleReason = "TRANSPORT_INPUT_PARAMETER_INCOMPATIBLE";

    /// <summary>
    /// OpenRouter and the upstream providers behind it reject a JSON-mode request whose messages
    /// never mention JSON. The check is on the messages as they will be sent, both of them,
    /// because the requirement is satisfied by either.
    /// </summary>
    public static TransportCompatibilityResult Validate(
        string? systemPrompt, string? userMessage, string? responseFormat)
    {
        if (!string.Equals(responseFormat, JsonObjectResponseFormat, StringComparison.Ordinal))
            return TransportCompatibilityResult.Compatible;

        var mentionsJson =
            (systemPrompt?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (userMessage?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false);

        return mentionsJson
            ? TransportCompatibilityResult.Compatible
            : new TransportCompatibilityResult(
                false,
                IncompatibleReason,
                "response_format is json_object, but neither message mentions JSON. The provider "
                + "refuses this pair. Add the instruction to the prompt - the transport will not "
                + "add it for you, because that would change bytes a preflight has frozen.");
    }

    /// <summary>Validates and throws, for a caller that has nothing useful to do with a refusal.</summary>
    public static void EnsureCompatible(string? systemPrompt, string? userMessage, string? responseFormat)
    {
        var result = Validate(systemPrompt, userMessage, responseFormat);
        if (!result.IsCompatible)
            throw new InvalidOperationException($"{result.Reason}: {result.Detail}");
    }
}
