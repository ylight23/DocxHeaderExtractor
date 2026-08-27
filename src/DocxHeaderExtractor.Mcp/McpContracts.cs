using DocxHeaderExtractor.Core.Models;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Mcp;

public sealed record McpBackendStatus(
    bool Ready,
    string Backend,
    string Endpoint,
    string? ConfiguredModel,
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<string> AllowedRoots,
    string Message);

public sealed record McpHeadingResult(
    int Index,
    string? StableId,
    int? Level,
    string Text,
    string Source,
    double Confidence,
    string DecisionStatus,
    bool Disputed,
    bool ModelConfirmed,
    bool CriticConfirmed,
    HeadingEvidence? Evidence);

public sealed record McpTraceResult(
    int Sequence,
    string Stage,
    string Kind,
    string Message);

public sealed record McpExtractionResult(
    string RunId,
    string Outcome,
    string File,
    string Backend,
    int ParagraphCount,
    int CandidateCount,
    int HeadingCount,
    int RequiresReview,
    int RepairAttempts,
    long ElapsedMs,
    string? Model,
    IReadOnlyList<McpHeadingResult> Headings,
    IReadOnlyList<McpTraceResult> Trace);

public sealed record McpJobStartResult(
    string JobId,
    string State,
    string File,
    int RecommendedPollSeconds,
    string Message);

public sealed record McpJobStatusResult(
    string JobId,
    string State,
    string File,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] McpExtractionResult? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    int RecommendedPollSeconds);
