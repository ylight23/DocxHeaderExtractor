using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>A versioned acceptance scope, not a replacement for historical receipts or execution authorization.</summary>
internal static class P7OfflineClosureQualification
{
    public const string Version = "P7_D23_OFFLINE_CLOSURE_ACCEPTANCE_V2";
    public static IReadOnlyList<string> RequiredChecks { get; } = Array.AsReadOnly(new[] {
        "PINNED_INPUTS_UNCHANGED", "VALID_FIXTURES_DETERMINISTIC",
        "INVALID_MISSING_LINEAGE_REJECTED", "NO_GOLD_OR_SIDECAR_GENERATION_INPUT",
        "FIXTURES_NOT_ACCURACY_OR_BASELINE", "HISTORICAL_CAPTURE_NOT_ADOPTED",
        "MODES_AND_METRICS_SEPARATE", "DEPENDENCY_HASHES_TRACK_UPSTREAM",
        "NO_SILENT_FALLBACK", "OFFLINE_PASS_NOT_EXECUTION_AUTHORIZATION" });

    public static P7OfflineClosureVerdict Qualify(IReadOnlyCollection<string> verifiedChecks)
    {
        if (verifiedChecks.Count != RequiredChecks.Count ||
            verifiedChecks.Distinct(StringComparer.Ordinal).Count() != RequiredChecks.Count ||
            !RequiredChecks.All(c => verifiedChecks.Contains(c, StringComparer.Ordinal)))
            throw new InvalidOperationException("P7_OFFLINE_CLOSURE_INCOMPLETE_OR_UNKNOWN_CHECK");
        return new(Version, "PASS", "DEFERRED_UNTIL_EXECUTION", "PENDING", "BLOCKED", "LOCKED", "LOCKED",
            "NOT_MEASURED", "HISTORICAL_DIAGNOSTIC_NOT_ADOPTED", false);
    }

    /// <summary>Fingerprint protocol fixtures only. Does not create executable pilot manifests,
    /// synthesize upstream decisions, or grant provider authorization.</summary>
    public static P7ProtocolFixtureBinding FingerprintProtocolFixture(P7GeneratedStage stage,
        DocumentSourceSnapshot source, SemanticEvidencePack pack, byte[] providerBody, string generationRecipeSha256)
    {
        if (stage.Request.Stage == InterpretationStage.F1 ||
            stage.Mode is not (P7ExperimentMode.ControlledDownstream or P7ExperimentMode.NaturalEndToEnd) ||
            !Enum.IsDefined(stage.Arm) || !HashValid(generationRecipeSha256) ||
            stage.ParentCaptureSha256.Count != (stage.Request.Stage == InterpretationStage.G2A ? 1 : 2) ||
            !stage.ParentCaptureSha256.All(HashValid) ||
            stage.ParentCaptureSha256.Distinct().Count() != stage.ParentCaptureSha256.Count)
            throw new InvalidOperationException("P7_FIXTURE_BINDING_INVALID_LINEAGE");
        var system = stage.Arm == P7CaptureArm.Control ? stage.Request.ControlSystemPrompt : stage.Request.SystemPrompt;
        var user = stage.Arm == P7CaptureArm.Control ? stage.Request.ControlUserMessage : stage.Request.UserMessage;
        using var json = JsonDocument.Parse(providerBody);
        var messages = json.RootElement.GetProperty("messages");
        if (messages.GetArrayLength() != 2 || messages[0].GetProperty("role").GetString() != "system" ||
            messages[1].GetProperty("role").GetString() != "user" ||
            messages[0].GetProperty("content").GetString() != system ||
            messages[1].GetProperty("content").GetString() != user)
            throw new InvalidOperationException("P7_FIXTURE_BODY_PROJECTION_DRIFT");
        return new("PROTOCOL_TEST_FIXTURE_ONLY", false, false,
            string.IsNullOrEmpty(source.DocumentId) ? "PDF-" + source.SourceSha256 : source.DocumentId,
            source.SourceSha256, source.SourceAliasUniverseHash, pack.PackId,
            SpatialCanonical.Hash(SpatialCanonical.Bytes(pack)), stage.Request.Stage, stage.Arm, stage.Mode,
            stage.Mode == P7ExperimentMode.ControlledDownstream ? "CONTROLLED_STAGE_ISOLATED" : "NATURAL_END_TO_END",
            P7UpstreamCaptureReadiness.IssueHash(stage.Request), SpatialCanonical.Hash(providerBody),
            TextHash(system), TextHash(user), generationRecipeSha256,
            Array.AsReadOnly(stage.ParentCaptureSha256.ToArray()));
    }

    private static string TextHash(string value) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(value));
    private static bool HashValid(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record P7OfflineClosureVerdict(string AcceptanceVersion, string D23OfflineFreeze,
    string DownstreamBodyMaterialization, string ProviderUsageCalibration, string ExactTokenizerMapping,
    string D3ProviderExecution, string ProductionPromotion, string SemanticAccuracy,
    string HistoricalCaptureStatus, bool HistoricalStatesRewritten);

internal sealed record P7ProtocolFixtureBinding(string Origin, bool ExecutionAuthorized, bool SemanticAccuracyEligible,
    string Document, string SourceSha256, string SourceUniverseSha256, string Pack, string PackSha256,
    InterpretationStage Stage, P7CaptureArm Arm, P7ExperimentMode Mode, string MetricNamespace,
    string IssuedUniverseSha256, string ProviderBodySha256, string SystemPromptSha256, string UserMessageSha256,
    string GenerationRecipeSha256, IReadOnlyList<string> ParentCaptureSha256);
