namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>Safe resource metadata only: no source text, prompts, images, credentials or model reasons.</summary>
public sealed record RunResourceObservation(double CeilingUsd, int MaxAttempts, int Attempts,
    double ConfirmedUsd, double ProvisionalUsd, double OutstandingUsd)
{
    public string AccountingScope => "default-agent-transport-admissions";
}

public sealed record ResourceAttemptObservation(string AttemptId, string RequestSha256, int WireBytes,
    int InputTokenEstimate, string Estimator, int CompletionCap, int? PromptTokens, int? CompletionTokens,
    int? ReasoningTokens, double ReservedUsd, double? CostUsd, string Status)
{
    public TransportAttemptObservation? Transport { get; init; }
}

public sealed record TransportAttemptObservation(string UnitId, int Turn, int RetryIndex, int? HttpStatus,
    string Outcome, double RequestSeconds, double WaitSeconds, double? RetryAfterSeconds,
    double RetryDelaySeconds, int? ProviderErrorCode);

public sealed record ResourceStageObservation(string ProfileId, int ByteCap, double ConfirmedUsd,
    double UnconfirmedUsd, IReadOnlyList<ResourceAttemptObservation> Attempts);

public sealed record SelectionScopeObservation(int Segment, string FirstAlias, string LastAlias, int TargetCount,
    string Boundary, int FirstRequestBytes, string RequestSha256);

public sealed record SelectionPlanObservation(string PlanId, string PolicyVersion,
    IReadOnlyList<SelectionScopeObservation> Scopes);
