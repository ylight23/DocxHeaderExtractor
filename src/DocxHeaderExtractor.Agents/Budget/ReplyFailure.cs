namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>A call the provider or the audit refused; Code says why (TRANSPORT:..., AUDIT:..., CONTENT:..., ENDPOINT_OR_MODEL_UNEXPECTED).</summary>
internal sealed class ReplyFailure(string code, bool transportRetriesHandled = false) : Exception(code)
{
    public string Code { get; } = code;
    // A built-in transport has already applied its retry/wait limit. Callers must not restart it.
    public bool TransportRetriesHandled { get; } = transportRetriesHandled;
}
