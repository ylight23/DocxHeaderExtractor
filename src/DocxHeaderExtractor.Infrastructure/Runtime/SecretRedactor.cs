using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Application.Runtime;

/// <summary>Conservative redaction for logs/telemetry; it never attempts to recover secret values.</summary>
public sealed class SecretRedactor : ISecretRedactor
{
    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignment = new(
        @"(?<name>\b(?:api[_-]?key|token|password|secret)\b\s*[:=]\s*)(?<quote>[""']?)(?<value>[^\s,;""']+)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = BearerToken.Replace(value, "Bearer [REDACTED]");
        return SecretAssignment.Replace(redacted, "${name}${quote}[REDACTED]${quote}");
    }
}
