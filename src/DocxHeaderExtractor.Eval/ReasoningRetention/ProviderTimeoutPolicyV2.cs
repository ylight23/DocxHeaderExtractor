namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Execution-only timeout policy. A provider attempt owns its 300-second window;
/// the document watchdog is only a high safety ceiling for orchestration failures.
/// </summary>
public static class ProviderTimeoutPolicyV2
{
    public const int DefaultPerAttemptHardTimeoutSeconds = 300;
    public const int DefaultDocumentSafetyCeilingSeconds = 7_200;

    public static TimeSpan ResolvePerAttemptHardTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_PER_ATTEMPT_TIMEOUT_SECONDS");
        var seconds = int.TryParse(raw, out var configured) && configured > 0
            ? configured
            : DefaultPerAttemptHardTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan ResolveDocumentSafetyCeiling()
    {
        var raw = Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_DOCUMENT_SAFETY_CEILING_SECONDS");
        var seconds = int.TryParse(raw, out var configured) && configured > 0
            ? configured
            : DefaultDocumentSafetyCeilingSeconds;
        return TimeSpan.FromSeconds(seconds);
    }
}
