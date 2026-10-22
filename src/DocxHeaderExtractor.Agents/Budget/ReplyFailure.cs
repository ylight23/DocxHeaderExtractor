namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>A call the provider or the audit refused; Code says why (TRANSPORT:..., AUDIT:..., CONTENT:..., ENDPOINT_OR_MODEL_UNEXPECTED).</summary>
internal sealed class ReplyFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
