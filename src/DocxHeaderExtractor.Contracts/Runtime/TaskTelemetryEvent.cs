namespace DocxHeaderExtractor.Contracts.Runtime;

public sealed record TaskTelemetryEvent(
    string RunId,
    string Stage,
    string Outcome,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Dimensions);
