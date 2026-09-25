using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Pipeline.HistoricalContracts;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The V1_ATTENTION_LEGACY request, for tests that reproduce an experiment or replay frozen before
/// REMOVE_MODEL_VISIBLE_ATTENTION_V1. Selection goes only through <see cref="AttentionLegacyV1.Select"/>;
/// a test that uses this helper declares that the artifact it checks was produced under V1.
/// </summary>
internal static class HistoricalRequest
{
    public static CanonicalSemanticExperiment Of(CanonicalSemanticExperiment experiment) =>
        AttentionLegacyV1.Select(experiment);

    public static CanonicalSemanticExperiment Baseline { get; } = Of(CanonicalSemanticExperiment.Baseline);

    public static SemanticRequestVersion Version => Baseline.RequestVersion;

    public static string SystemPrompt => AttentionLegacyV1.SystemPrompt;

    public static string Stage1MembershipPrompt => AttentionLegacyV1.Stage1MembershipPrompt;
}
