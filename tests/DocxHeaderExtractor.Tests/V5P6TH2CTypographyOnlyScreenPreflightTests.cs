using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes a deliberately small, post-hoc diagnostic replication: historical H2-C Arm A versus
/// exactly that carrier plus the existing rich typography projection.  It neither sends provider
/// calls nor opens Gold.
/// </summary>
public sealed class V5P6TH2CTypographyOnlyScreenPreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string OutputRoot = Root + "/p6th2c-typography-only-screen-preflight";
    private const string EvidencePreflightPath = Root + "/p6th2c-evidence-complete-preflight/h2c-evidence-complete-preflight.v1.json";
    private const string HistoricalControlCommit = "dfe96aae9c2a73102dd942b9a5c21df5c20641bd";

    private sealed record Target(string DocumentId, string Anchor, string ExpectedPattern, string PrimaryDiagnostic);

    private static readonly Target[] Targets =
    [
        new("SRC-089", "O17", "NO_RECOVERY_EXPECTED", "EXACT_EXTENT_AND_UNDEREXTENT"),
        new("SRC-089", "O19", "NO_EXACT_RECOVERY_EXPECTED_DISTANCE_MAY_SHRINK", "EXACT_EXTENT_AND_SIGNED_BOUNDARY_DISTANCE"),
        new("SRC-041", "O4", "RECOVERY_PLAUSIBLE", "EXACT_EXTENT"),
        new("DOC-0256", "O1", "NO_RECOVERY_EXPECTED", "EXACT_EXTENT"),
    ];

    [Fact]
    public void Typography_only_four_call_screen_freezes_historical_control_replication_without_gold_or_provider()
    {
        var repo = TestRepository.Root();
        var allRequests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        Assert.Equal(31, allRequests.Count);
        Assert.Equal(4, Targets.Length);
        Assert.Equal(4, Targets.Select(value => (value.DocumentId, value.Anchor)).Distinct().Count());
        Assert.Equal(new[] { ("SRC-089", "O17"), ("SRC-089", "O19"), ("SRC-041", "O4"), ("DOC-0256", "O1") },
            Targets.Select(value => (value.DocumentId, value.Anchor)));

        var evidencePreflightBytes = File.ReadAllBytes(TestRepository.Path(EvidencePreflightPath));
        using var evidencePreflight = JsonDocument.Parse(evidencePreflightBytes);
        var historicalRows = evidencePreflight.RootElement.GetProperty("requests").EnumerateArray()
            .ToDictionary(value => Key(value.GetProperty("documentId").GetString()!, value.GetProperty("anchor").GetString()!), StringComparer.Ordinal);
        Assert.Equal(31, historicalRows.Count);

        var rows = new List<object>();
        foreach (var target in Targets)
        {
            var request = allRequests.Single(value => value.Source.DocumentId == target.DocumentId && value.Anchor == target.Anchor);
            var control = V5P6TH2CEvidenceCompletePreflightTests.BuildArmARequest(request);
            var treatment = V5P6TH2CEvidenceCompletePreflightTests.BuildTypographyOnlyRequest(repo, request);
            var compositeB = V5P6TH2CEvidenceCompletePreflightTests.BuildArmBRequest(repo, request);
            var historical = historicalRows[Key(target.DocumentId, target.Anchor)];

            Assert.Equal(historical.GetProperty("packId").GetString(), control.PackId);
            Assert.Equal(historical.GetProperty("armA").GetProperty("userMessageSha256").GetString(), control.UserMessageSha256);
            Assert.Equal(historical.GetProperty("armA").GetProperty("providerBodySha256").GetString(), control.ProviderBodySha256);
            Assert.Equal(control.SystemPrompt, treatment.SystemPrompt);
            Assert.Equal(control.MaxCompletionTokens, treatment.MaxCompletionTokens);
            Assert.Equal(control.OccurrenceHandles, treatment.OccurrenceHandles);
            Assert.Equal(control.SourceSha256, treatment.SourceSha256);
            Assert.Equal(control.SourceUniverseSha256, treatment.SourceUniverseSha256);
            Assert.NotEqual(control.UserMessageSha256, treatment.UserMessageSha256);
            Assert.NotEqual(control.ProviderBodySha256, treatment.ProviderBodySha256);
            AssertTypographyOnlyDelta(control.UserMessage, treatment.UserMessage, compositeB.UserMessage);

            rows.Add(new
            {
                documentId = target.DocumentId,
                packId = request.PackId,
                anchor = target.Anchor,
                sourceSha256 = control.SourceSha256,
                sourceUniverseSha256 = control.SourceUniverseSha256,
                occurrenceHandlesSha256 = Hash(string.Join("\n", control.OccurrenceHandles) + "\n"),
                occurrences = control.OccurrenceHandles.Count,
                frozenHistoricalPredictionExpectation = target.ExpectedPattern,
                primaryDiagnostic = target.PrimaryDiagnostic,
                control = new
                {
                    authority = "HISTORICAL_ARM_A_FROM_DFE96AA",
                    userMessageSha256 = control.UserMessageSha256,
                    userBytes = Encoding.UTF8.GetByteCount(control.UserMessage),
                    systemPromptSha256 = Hash(control.SystemPrompt),
                    providerBodySha256 = control.ProviderBodySha256,
                    providerBodyBytes = control.ProviderBodyBytes,
                },
                typographyOnlyTreatment = new
                {
                    userMessageSha256 = treatment.UserMessageSha256,
                    userBytes = Encoding.UTF8.GetByteCount(treatment.UserMessage),
                    systemPromptSha256 = Hash(treatment.SystemPrompt),
                    providerBodySha256 = treatment.ProviderBodySha256,
                    providerBodyBytes = treatment.ProviderBodyBytes,
                    richTypographyRows = treatment.OccurrenceHandles.Count,
                },
            });
        }

        FreezeArtifact.AssertJson(OutputRoot, "typography-only-screen-preflight.v1.json", new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-preflight-v1",
            status = "POST_HOC_DIAGNOSTIC_PREFLIGHT_FROZEN_PROVIDER_NOT_AUTHORIZED_GOLD_CLOSED",
            experiment = new
            {
                comparisonType = "POST_HOC_DIAGNOSTIC_REPLICATION_AGAINST_FROZEN_HISTORICAL_CONTROL",
                notPaired = true,
                notMatched = true,
                notCausal = true,
                question = "Does the pre-existing rich typography projection reproduce a useful signal on four frozen historical Arm-A error anchors?",
                cohort = "FOUR_POST_HOC_DIAGNOSTIC_ANCHORS_ONLY; NOT_A_FULL_31_OR_HELD_OUT_QUALIFICATION_COHORT",
                controlAuthority = "HISTORICAL_ARM_A_FROM_DFE96AA",
                treatmentDelta = "HISTORICAL_ARM_A_OCCURRENCE_PROJECTION_PLUS_RICH_TYPOGRAPHY_FIELDS_ONLY",
                noChange = new[]
                {
                    "model/provider", "reasoning", "temperature", "system prompt", "semantic instruction", "output schema",
                    "horizon", "occurrence handles/order/text", "basic existing style facts", "geometry", "spacing",
                    "parser block id", "source-block topology", "transport policy",
                },
                noTypographyTransitionObject = true,
                noEngineeredTypographyDelta = true,
                richTypographyProjection = "EXACT_EXISTING_COMPOSITE_B_TYPOGRAPHY_OBJECT_ONLY",
                richTypographyValues = new[]
                {
                    "nominalFontSize", "effectivePointSize", "fontBoldFlagRatio", "fontNameBoldRatio", "derivedBoldRatio", "fontName",
                    "dominantPointSize", "medianPointSize", "minPointSize", "maxPointSize", "dominantFontName", "boldGlyphRatio", "italicGlyphRatio",
                },
                rawProjectionProvenance = "The existing composite-B typography object also preserves its version and bold-evidence provenance metadata; no new derived transition facts are added here.",
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                temperature = 0,
                route = "OPENROUTER_ALIBABA_PINNED",
                protocolVersion = V5P6TH2CEvidenceCompletePreflightTests.ProtocolVersion,
                outputSchema = "UNCHANGED_H2C_EXACT_END_POINTER_WITH_FIRST_OUTSIDE_ROLE",
                horizon = "UNCHANGED_COMPLETE_OWNED_TAIL_FROM_FROZEN_G2A_HAS_ANCHOR",
            },
            authorities = new
            {
                historicalControlCommit = HistoricalControlCommit,
                evidencePreflightPath = EvidencePreflightPath,
                evidencePreflightSha256 = Hash(evidencePreflightBytes),
                historicalControlParity = "EACH_RECONSTRUCTED_CONTROL_USER_AND_PROVIDER_BODY_HASH_EQUALS_ITS_FROZEN_EVIDENCE_PREFLIGHT_ARM_A_ROW",
                canonicalGold = "CLOSED_AND_NOT_READ_BY_THIS_PREFLIGHT",
                rawProviderResponses = "NOT_READ_OR_COPIED",
            },
            interpretationGate = new
            {
                onlyO4Recovers = "TYPOGRAPHY_SIGNAL_SUPPORTED_FOR_ONE_OBSERVED_SUBCLASS; NOT_A_GENERAL_BOUNDARY_SOLUTION; DO_NOT_RUN_TYPOGRAPHY_FULL_31_YET",
                o4DoesNotRecover = "COMPOSITE_B_O4_IMPROVEMENT_NOT_ATTRIBUTABLE_TO_TYPOGRAPHY_ALONE",
                unexpectedO17O19OrO1Recovery = "FORENSIC_REQUIRED; NO_IMMEDIATE_GENERALIZATION",
                o19WrongButDistanceShrinks = "TYPOGRAPHY_AFFECTS_BOUNDARY_SEVERITY; NOT_AN_EXACT_BOUNDARY_SOLUTION",
                anyRegression = "RECORD_VERBATIM; NO_SELECTIVE_SUCCESS_CLAIM",
            },
            postCaptureScoring = new
            {
                timing = "ONLY_AFTER_ALL_FOUR_RAW_RESPONSES_AND_HASHES_ARE_FROZEN",
                perAnchor = new[] { "endOccurrence", "firstOutsideOccurrence", "extentOutcome", "signedBoundaryDistance", "absoluteBoundaryDistance" },
                transition = new[] { "WRONG_TO_RIGHT", "RIGHT_TO_WRONG", "BOTH_WRONG", "BOTH_RIGHT" },
                firstOutsideRole = new { recorded = true, scored = false, reason = "NO_FROZEN_GOLD_ROLE_AUTHORITY" },
                runtimePromotion = "FORBIDDEN_FROM_THIS_FOUR_CALL_POST_HOC_DIAGNOSTIC_SCREEN",
            },
            safety = new
            {
                providerCalls = 0,
                retry = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                providerAuthorization = "REQUIRED_BEFORE_ANY_FOUR_CALL_SCREEN_EXECUTION",
            },
            requests = rows,
        });

        FreezeArtifact.AssertJson(OutputRoot, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED_PROVIDER_CALLS_ZERO_GOLD_CLOSED",
            preflightSha256 = Hash(File.ReadAllBytes(TestRepository.Path(OutputRoot + "/typography-only-screen-preflight.v1.json"))),
            primaryCalls = 4,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            executionOrder = "FROZEN_TARGET_ORDER",
            requests = rows.Select((row, index) => new { callOrdinal = index + 1, arm = "TYPOGRAPHY_ONLY", row }),
        });
    }

    private static void AssertTypographyOnlyDelta(string controlMessage, string treatmentMessage, string compositeBMessage)
    {
        var control = JsonNode.Parse(controlMessage)?.AsObject() ?? throw new InvalidDataException("h2c-typography-control-json-invalid");
        var treatment = JsonNode.Parse(treatmentMessage)?.AsObject() ?? throw new InvalidDataException("h2c-typography-treatment-json-invalid");
        var compositeB = JsonNode.Parse(compositeBMessage)?.AsObject() ?? throw new InvalidDataException("h2c-typography-composite-json-invalid");
        var treatmentOccurrences = Occurrences(treatment);
        var compositeOccurrences = Occurrences(compositeB);
        Assert.Equal(Occurrences(control).Count, treatmentOccurrences.Count);
        Assert.Equal(treatmentOccurrences.Count, compositeOccurrences.Count);

        for (var index = 0; index < treatmentOccurrences.Count; index++)
        {
            var controlOccurrence = Occurrences(control)[index]!.AsObject();
            var treatmentOccurrence = treatmentOccurrences[index]!.AsObject();
            var compositeOccurrence = compositeOccurrences[index]!.AsObject();
            Assert.Equal(controlOccurrence["occurrence"]!.GetValue<string>(), treatmentOccurrence["occurrence"]!.GetValue<string>());
            Assert.Equal(controlOccurrence["page"]!.GetValue<int>(), treatmentOccurrence["page"]!.GetValue<int>());
            Assert.Equal(controlOccurrence["text"]!.GetValue<string>(), treatmentOccurrence["text"]!.GetValue<string>());
            Assert.True(treatmentOccurrence.ContainsKey("typography"));
            Assert.Equal(compositeOccurrence["typography"]!.ToJsonString(CanonicalJsonOptions),
                treatmentOccurrence["typography"]!.ToJsonString(CanonicalJsonOptions));
            AssertNoForbiddenEvidenceProjection(treatmentOccurrence);
            treatmentOccurrence.Remove("typography");
        }

        Assert.Equal(controlMessage, treatment.ToJsonString(CanonicalJsonOptions));
    }

    private static void AssertNoForbiddenEvidenceProjection(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    var name = property.Key;
                    Assert.False(name.Equals("geometry", StringComparison.OrdinalIgnoreCase));
                    Assert.False(name.Equals("parserLayoutBlockId", StringComparison.OrdinalIgnoreCase));
                    Assert.False(name.Equals("transitionFromPrevious", StringComparison.OrdinalIgnoreCase));
                    Assert.False(name.Equals("typographyTransition", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain("gap", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("block", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("coordinate", name, StringComparison.OrdinalIgnoreCase);
                    if (property.Value is not null) AssertNoForbiddenEvidenceProjection(property.Value);
                }
                break;
            case JsonArray array:
                foreach (var value in array.Where(value => value is not null)) AssertNoForbiddenEvidenceProjection(value!);
                break;
        }
    }

    private static JsonArray Occurrences(JsonObject root) => root["anchors"]!.AsArray()[0]!["occurrences"]!.AsArray();
    private static string Key(string documentId, string anchor) => documentId + "|" + anchor;
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
