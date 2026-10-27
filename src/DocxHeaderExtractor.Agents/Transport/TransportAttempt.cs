namespace DocxHeaderExtractor.Agents.Transport;

/// <summary>Allowlisted transport metadata, never provider error messages or response bodies.</summary>
internal sealed record TransportAttempt(string UnitId, int Turn, int RetryIndex, int? HttpStatus,
    string Outcome, double RequestSeconds, double WaitSeconds, double? RetryAfterSeconds,
    double RetryDelaySeconds, int? ProviderErrorCode);
