using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Eval.Accuracy99;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Cli;

internal static class Accuracy99Runner
{
    private const string StructuralProfile = "structural";
    private const string ProductProfile = "product";
    private const string FixedPrompt = "Trích xuất heading của văn bản này.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var operation = options.Accuracy99Operation?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation) || operation is "help" or "-h")
        {
            Console.WriteLine("accuracy99 operations: packet, inventory, evaluate, baseline, observability, reference-campaign, early-dev-campaign, review-ui, review-ui-v3, gold-validate, gold-import-dev, gold-validate-v2, gold-import-dev-v2, gold-validate-v3, gold-import-dev-v3, gold-validate-strict-v3, gold-import-strict-dev-v3, gold-preflight-strict-dev, gold-authority-policy-v2, gold-occurrence-bindings, reasoning-retention, reasoning-retention-smoke, openrouter-qwen35-smoke, openrouter-qwen9b-true-ceiling, openrouter-qwen37-flash-control, openrouter-qwen37-request-ladder, openrouter-qwen37-flash-reasoning-ceiling, openrouter-qwen37-flash-visual-ceiling, openrouter-qwen37-flash-visual-audit, doc0205-semantic-taxonomy-audit, doc0205-semantic-contract-reconciliation, semantic-text-exact-binding, semantic-text-generalization, semantic-text-generalization-offline, semantic-text-generalization-recover-0258-r2, semantic-text-omission-review, semantic-text-omission-review-offline, semantic-text-omission-review-recover-0001-r1, semantic-text-repeat-stability-audit, semantic-text-stable-error-repair, semantic-text-stable-error-repair-clean-completion, canonical-heading-contract-v2, heading-target-ontology-v4, structure-preserving-ir, structure-preserving-ir-vlm, doc0205-contract-audit, flash-heading-contract-realignment, openrouter-qwen9b-executable-ceiling, openrouter-qwen9b-per-segment-recovery, openrouter-qwen9b-provider-diagnosis, openrouter-qwen9b-provider-resume, openrouter-qwen9b-multipass-doc0205, doc-0205-canary, mode-stratification, doc-0027-support-canary");
            return 0;
        }

        return operation switch
        {
            "packet" => await BuildPacketAsync(options, cancellationToken),
            "inventory" => await BuildInventoryAsync(options, cancellationToken),
            "evaluate" => await EvaluateAsync(options, cancellationToken),
            "baseline" => await BuildBaselineAsync(options, cancellationToken),
            "observability" => await BuildObservabilityAsync(options, cancellationToken),
            "reference-campaign" => await BuildReferenceCampaignAsync(options, cancellationToken),
            "early-dev-campaign" => await BuildEarlyDevCampaignAsync(options, cancellationToken),
            "review-ui" => BuildReviewUi(options),
            "review-ui-v3" => BuildReviewUiV3(options),
            "gold-validate" => await ValidateGoldAsync(options, cancellationToken),
            "gold-import-dev" => await ImportDevGoldAsync(options, cancellationToken),
            "gold-validate-v2" => await ValidateEarlyDevGoldV2Async(options, cancellationToken),
            "gold-import-dev-v2" => await ImportEarlyDevGoldV2Async(options, cancellationToken),
            "gold-validate-v3" => await ValidateEarlyDevGoldV3Async(options, cancellationToken),
            "gold-import-dev-v3" => await ImportEarlyDevGoldV3Async(options, cancellationToken),
            "gold-validate-strict-v3" => await ValidateStrictEarlyDevGoldV3Async(options, cancellationToken, import: false),
            "gold-import-strict-dev-v3" => await ValidateStrictEarlyDevGoldV3Async(options, cancellationToken, import: true),
            "gold-preflight-strict-dev" => await PreflightStrictDevCohortAsync(options, cancellationToken),
            "gold-authority-policy-v2" => await ReconcileStrictGoldAuthorityPolicyV2Async(options, cancellationToken),
            "gold-occurrence-bindings" => MaterializeStrictGoldOccurrences(options),
            "reasoning-retention" => await RunReasoningRetentionAsync(options, cancellationToken),
            "reasoning-retention-smoke" => await RunReasoningRetentionSmokeAsync(options, cancellationToken),
            "openrouter-qwen35-smoke" => await RunOpenRouterQwen35SmokeAsync(options, cancellationToken),
            "openrouter-qwen9b-true-ceiling" => await RunOpenRouterQwen9BTrueCeilingAsync(options, cancellationToken),
            "openrouter-qwen37-flash-control" => await RunOpenRouterQwen37FlashControlAsync(options, cancellationToken),
            "openrouter-qwen37-request-ladder" => await RunOpenRouterQwen37RequestLadderAsync(options, cancellationToken),
            "openrouter-qwen37-flash-reasoning-ceiling" => await RunOpenRouterQwen37FlashReasoningCeilingAsync(options, cancellationToken),
            "openrouter-qwen37-flash-visual-ceiling" => await RunOpenRouterQwen37FlashVisualCeilingAsync(options, cancellationToken),
            "openrouter-qwen37-flash-visual-audit" => await RunOpenRouterQwen37FlashVisualAuditAsync(options, cancellationToken),
            "doc0205-semantic-taxonomy-audit" => await RunDoc0205SemanticTaxonomyAuditAsync(options, cancellationToken),
            "doc0205-semantic-contract-reconciliation" => await RunDoc0205SemanticContractReconciliationAsync(options, cancellationToken),
            "semantic-text-exact-binding" => await RunSemanticTextExactBindingAsync(options, cancellationToken),
            "semantic-text-exact-binding-offline" => await RunSemanticTextExactBindingOfflineAsync(options, cancellationToken),
            "semantic-text-generalization" => await RunSemanticTextGeneralizationAsync(options, cancellationToken),
            "semantic-text-generalization-offline" => await RunSemanticTextGeneralizationOfflineAsync(options, cancellationToken),
            "semantic-text-generalization-recover-0258-r2" => await RunSemanticTextGeneralizationRecover0258R2Async(options, cancellationToken),
            "semantic-text-omission-review" => await RunSemanticTextOmissionReviewAsync(options, cancellationToken),
            "semantic-text-omission-review-offline" => await RunSemanticTextOmissionReviewOfflineAsync(options, cancellationToken),
            "semantic-text-omission-review-recover-0001-r1" => await RunSemanticTextOmissionReviewRecover0001R1Async(options, cancellationToken),
            "semantic-text-repeat-stability-audit" => await RunSemanticTextRepeatStabilityAuditAsync(options, cancellationToken),
            "semantic-text-stable-error-repair" => await RunSemanticTextStableErrorRepairAsync(options, cancellationToken),
            "semantic-text-stable-error-repair-recover-0252-r4" => await RunSemanticTextStableErrorRepairRecoveryAsync(options, cancellationToken),
            "semantic-text-stable-error-repair-clean-completion" => await RunStructuralContextEnrichmentCleanCompletionAsync(options, cancellationToken),
            "canonical-heading-contract-v2" => await RunCanonicalHeadingContractV2Async(options, cancellationToken),
            "heading-target-ontology-v4" => await RunHeadingTargetOntologyV4Async(options, cancellationToken),
            "structure-preserving-ir" => await RunStructurePreservingIrAsync(options, cancellationToken),
            "structure-preserving-ir-vlm" => await RunStructurePreservingIrVlmAsync(options, cancellationToken),
            "doc0205-contract-audit" => await RunDoc0205ContractAuditAsync(options, cancellationToken),
            "flash-heading-contract-realignment" => await RunFlashHeadingContractRealignmentAsync(options, cancellationToken),
            "openrouter-qwen9b-executable-ceiling" => await RunOpenRouterQwen9BExecutableCeilingAsync(options, cancellationToken),
            "openrouter-qwen9b-per-segment-recovery" => await RunOpenRouterQwen9BPerSegmentRecoveryAsync(options, cancellationToken),
            "openrouter-qwen9b-provider-diagnosis" => await RunOpenRouterQwen9BProviderDiagnosisAsync(options, cancellationToken),
            "openrouter-qwen9b-provider-resume" => await RunOpenRouterQwen9BProviderResumeAsync(options, cancellationToken),
            "openrouter-qwen9b-multipass-doc0205" => await RunOpenRouterQwen9BMultiPassDoc0205Async(options, cancellationToken),
            "reasoning-retention-timing-diagnostic" => await RunReasoningRetentionTimingDiagnosticAsync(options, cancellationToken),
            "reasoning-retention-source-stats" => await RunReasoningRetentionSourceStatsAsync(options, cancellationToken),
            "doc-0205-canary" => await RunDoc0205CanaryAsync(options, cancellationToken),
            "mode-stratification" => await BuildModeStratificationAsync(options, cancellationToken),
            "doc-0027-support-canary" => await RunDoc0027SupportCanaryAsync(options, cancellationToken),
            _ => throw new ArgumentException($"accuracy99 operation không hợp lệ: {operation}"),
        };
    }

    private static Task<int> RunOpenRouterQwen35SmokeAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen35CeilingSmokeRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen9BTrueCeilingAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen9BTrueCeilingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen37FlashControlAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen9BTrueCeilingRunner.RunFlashControlAsync(repoRoot, cancellationToken);
    }

    private static async Task<int> RunOpenRouterQwen37RequestLadderAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var ladderExitCode = await OpenRouterQwen37RequestLadderRunner.RunAsync(repoRoot, cancellationToken);
        if (ladderExitCode != 0) return ladderExitCode;
        Console.WriteLine("REQUEST_CONTRACT_PROVEN=true");
        return await OpenRouterQwen9BTrueCeilingRunner.RunFlashControlAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen37FlashReasoningCeilingAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37ReasoningCeilingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen37FlashVisualCeilingAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37VisualCeilingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen37FlashVisualAuditAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37VisualCeilingRunner.RunOfflineAuditAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunDoc0205SemanticTaxonomyAuditAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return Doc0205SemanticTaxonomyAuditRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunDoc0205SemanticContractReconciliationAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return Doc0205SemanticContractReconciliationRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextExactBindingAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextExactBindingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextExactBindingOfflineAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextExactBindingRunner.RunOfflineComparisonAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextGeneralizationAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextGeneralizationOfflineAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RunOfflineAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextGeneralizationRecover0258R2Async(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RecoverDoc0258R2Async(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextOmissionReviewAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RunOmissionReviewAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextOmissionReviewOfflineAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RunOmissionReviewOfflineAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextOmissionReviewRecover0001R1Async(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RecoverOmissionReviewDoc0001R1Async(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextRepeatStabilityAuditAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextRepeatStabilityAuditRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextStableErrorRepairAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RunStableErrorRepairAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunSemanticTextStableErrorRepairRecoveryAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return SemanticTextGeneralizationRunner.RecoverStableErrorRepairDoc0252R4Async(repoRoot, cancellationToken);
    }

    private static Task<int> RunStructuralContextEnrichmentCleanCompletionAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return StructuralContextEnrichmentCleanCompletionRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunCanonicalHeadingContractV2Async(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37ReasoningCeilingRunner.RunContractV2Async(repoRoot, cancellationToken);
    }

    private static Task<int> RunHeadingTargetOntologyV4Async(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return HeadingTargetOntologyRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunStructurePreservingIrAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37StructurePreservingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunStructurePreservingIrVlmAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen37StructurePreservingRunner.RunVisualOnlyAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunDoc0205ContractAuditAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return Doc0205ContractAuditRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunFlashHeadingContractRealignmentAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return FlashHeadingContractRealignmentRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen9BExecutableCeilingAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen9BExecutableCeilingRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen9BPerSegmentRecoveryAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen9BPerSegmentRecoveryRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static Task<int> RunOpenRouterQwen9BMultiPassDoc0205Async(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return OpenRouterQwen9BMultiPassRunner.RunAsync(repoRoot, cancellationToken);
    }

    private static async Task<int> RunOpenRouterQwen9BProviderDiagnosisAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var (diagnosisExitCode, profile) = await OpenRouterQwen9BProviderDiagnosticRunner.RunAsync(repoRoot, cancellationToken);
        if (diagnosisExitCode != 0 || profile is null) return diagnosisExitCode == 0 ? 1 : diagnosisExitCode;
        Console.WriteLine("PROVIDER_DIAGNOSIS_COMPLETE=true");
        var resumeExitCode = await OpenRouterQwen9BMultiPassRunner.RunAsync(repoRoot, cancellationToken, profile);
        Console.WriteLine($"RESUME_EXIT_CODE={resumeExitCode}");
        return resumeExitCode;
    }

    private static async Task<int> RunOpenRouterQwen9BProviderResumeAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var profilePath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "qwen9b-multipass-doc0205", "provider-execution-profile.v1.json");
        if (!File.Exists(profilePath)) throw new InvalidDataException("PROVIDER_EXECUTION_PROFILE_MISSING");
        var profile = JsonSerializer.Deserialize<Qwen9BExecutionProfile>(await File.ReadAllTextAsync(profilePath, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("PROVIDER_EXECUTION_PROFILE_INVALID");
        return await OpenRouterQwen9BMultiPassRunner.RunAsync(repoRoot, cancellationToken, profile);
    }

    private static int MaterializeStrictGoldOccurrences(CommandLineOptions options)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        StrictGoldOccurrenceMaterializer.WriteAll(repoRoot);
        return 0;
    }

    private static Task<int> RunReasoningRetentionAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return ReasoningRetentionRunner.RunAsync(repoRoot, options.Provider.Remote, cancellationToken);
    }

    private static Task<int> RunReasoningRetentionSmokeAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return ReasoningRetentionRunner.RunSmokeAsync(repoRoot, options.Provider.Remote, cancellationToken);
    }

    private static Task<int> RunReasoningRetentionTimingDiagnosticAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return ReasoningRetentionRunner.RunTimingDiagnosticAsync(repoRoot, options.Provider.Remote, cancellationToken);
    }

    private static Task<int> RunReasoningRetentionSourceStatsAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        return ReasoningRetentionRunner.RunSourceStatsAsync(repoRoot, cancellationToken);
    }

    private static async Task<int> BuildEarlyDevCampaignAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var campaign = LoadCampaign(repoRoot);
        var early = A99EarlyDevCampaignBuilder.Build(campaign, GitRevision(repoRoot) ?? "UNRESOLVED");
        var artifactRoot = Path.Combine(repoRoot, "eval", "a99-closed-loop");
        Directory.CreateDirectory(Path.Combine(artifactRoot, "review"));
        await WriteAsync(Path.Combine(artifactRoot, "early-dev-campaign.v1.json"), A99ReviewJson.Serialize(early), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "review", "gold-schema.v2.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_human_gold_schema",
            schemaVersion = "a99-human-gold-v2",
            positiveSetOnly = true,
            exhaustiveCertificate = new[] { "reviewedEntireDocument", "headingSetExhaustive" },
            finalAuthority = "USER",
            provenanceField = "independentOfModelPrediction; truthful audit metadata only",
            requiredHeadingFields = new[] { "sourceId", "headingOccurrenceId", "stableId", "sourceOrdinal", "sourceSpan", "sourceTextHash", "headingSpan", "role", "level", "parentOccurrenceId" },
            unsureSourceIds = "optional; any value blocks final HUMAN_GOLD certification",
            forbidden = new[] { "prediction", "candidate", "confidence", "validatorOutput", "decision" },
        }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "early-dev-reference-audit.v1.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_early_dev_reference_audit",
            schemaVersion = "a99-early-dev-reference-audit-v1",
            policy = "existing references are provenance clues only; never auto-promoted to HUMAN_GOLD",
            entries = early.Documents.Select(document =>
            {
                var sourcePath = Path.Combine(repoRoot, document.SourcePath.Replace('/', Path.DirectorySeparatorChar));
                var stem = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath));
                var v1Gold = stem + ".human-gold.json";
                var v2Gold = stem + ".human-gold-v2.json";
                var humanKey = stem + ".key";
                var reviewPacket = Path.Combine(repoRoot, "eval", "harness-lift", "review-packets-v2", document.DocumentId + ".v2.json");
                var classification = File.Exists(v2Gold) ? "V2_HUMAN_GOLD_PRESENT_REQUIRES_VALIDATION"
                    : File.Exists(v1Gold) ? "V1_HUMAN_GOLD_PRESENT_NOT_AUTO_PROMOTED"
                    : File.Exists(humanKey) ? "HUMAN_KEY_PRESENT_NOT_AUTO_PROMOTED"
                    : File.Exists(reviewPacket) ? "EXHAUSTIVE_PACKET_REFERENCE_NOT_GOLD"
                    : "NONE_FOUND";
                return new
                {
                    documentId = document.DocumentId,
                    sourcePath = document.SourcePath,
                    classification,
                    v1GoldPresent = File.Exists(v1Gold),
                    v2GoldPresent = File.Exists(v2Gold),
                    humanKeyPresent = File.Exists(humanKey),
                    existingReviewPacketPresent = File.Exists(reviewPacket),
                };
            }).ToArray(),
            providerCalls = 0,
        }, JsonOptions), cancellationToken);
        Console.WriteLine($"A99 early DEV campaign ready: {early.Documents.Count} docs/{early.SourceOccurrences} occurrences; families={string.Join(",", early.FamiliesCovered)}; status=HUMAN_REFERENCE_REQUIRED");
        return 0;
    }

    private static async Task<int> BuildReferenceCampaignAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var packetRoot = options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets");
        var campaign = A99ReferenceCampaignBuilder.Build(repoRoot, packetRoot, createdFromRevision: GitRevision(repoRoot));
        var manifests = A99ReferenceCampaignBuilder.BuildReviewManifests(campaign, packetRoot);
        var artifactRoot = Path.Combine(repoRoot, "eval", "a99-closed-loop");
        var reviewRoot = Path.Combine(artifactRoot, "review");
        Directory.CreateDirectory(reviewRoot);
        await WriteAsync(Path.Combine(artifactRoot, "reference-campaign.v1.json"), A99ReviewJson.Serialize(campaign), cancellationToken);
        await WriteAsync(Path.Combine(reviewRoot, "dev-manifest.v1.json"), A99ReviewJson.Serialize(manifests.Dev), cancellationToken);
        await WriteAsync(Path.Combine(reviewRoot, "holdout-manifest.v1.json"), A99ReviewJson.Serialize(manifests.Holdout), cancellationToken);
        await WriteReviewSchemasAsync(reviewRoot, cancellationToken);

        var coverage = A99GoldImportService.ValidateAndImportDev(
            campaign,
            packetRoot,
            options.Accuracy99GoldRoot ?? Path.Combine("C:\\A99-Gold", "dev"),
            artifactRoot);
        await WritePhaseBArtifactsAsync(repoRoot, campaign, coverage, cancellationToken);
        Console.WriteLine($"A99 reference campaign ready: DEV={campaign.DevDocuments.Count} docs/{campaign.DevDocuments.Sum(x => x.SourceOccurrenceCount)} occurrences; " +
                          $"HOLDOUT={campaign.HoldoutDocuments.Count} docs/{campaign.HoldoutDocuments.Sum(x => x.SourceOccurrenceCount)} occurrences; " +
                          $"DEV gold={coverage.DevDocumentsValidated}/{coverage.DevDocumentsExpected}; status={coverage.Status}");
        Console.WriteLine(coverage.Status == "READY"
            ? "DEV gold is sufficient for the next baseline command."
            : "HUMAN_REFERENCE_REQUIRED: review source-first DEV packets before evaluation.");
        return 0;
    }

    private static async Task WritePhaseBArtifactsAsync(
        string repoRoot,
        A99ReferenceCampaign campaign,
        A99GoldImportCoverage coverage,
        CancellationToken cancellationToken)
    {
        var artifactRoot = Path.Combine(repoRoot, "eval", "a99-closed-loop");
        var devDocuments = campaign.DevDocuments.Count;
        var holdoutDocuments = campaign.HoldoutDocuments.Count;
        var devOccurrences = campaign.DevDocuments.Sum(x => x.SourceOccurrenceCount);
        var holdoutOccurrences = campaign.HoldoutDocuments.Sum(x => x.SourceOccurrenceCount);
        var goldStatus = coverage.Status;

        await WriteAsync(Path.Combine(artifactRoot, "reference-sufficiency.v2.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_reference_sufficiency",
            schemaVersion = "2.0",
            status = goldStatus == "READY" ? "READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED",
            devDocumentsSelected = devDocuments,
            devOccurrencesSelected = devOccurrences,
            holdoutDocumentsSelected = holdoutDocuments,
            holdoutOccurrencesSelected = holdoutOccurrences,
            devDocumentsValidated = coverage.DevDocumentsValidated,
            explicitNegatives = coverage.HeadingNo,
            headingPositives = coverage.HeadingYes,
            unsure = coverage.HeadingUnsure,
            preferredMinimumDevDocuments = 12,
            preferredMinimumFamilies = campaign.FamilySummary.Count,
            goldMustBeExhaustive = true,
            providerCalls = 0,
        }, JsonOptions), cancellationToken);

        await WriteAsync(Path.Combine(artifactRoot, "baseline-manifest.v2.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_baseline_manifest",
            schemaVersion = "2.0",
            status = "HUMAN_REFERENCE_REQUIRED",
            codeSha = GitRevision(repoRoot) ?? "UNRESOLVED",
            sourceCorpus = campaign.SourceCorpus,
            splitAuthority = "eval/a99-dataset/evaluation-splits.v1.json",
            devDocumentIds = campaign.DevDocuments.Select(x => x.DocumentId),
            model = "NOT_RUN_UNTIL_VALID_HUMAN_GOLD",
            provider = "NOT_RUN_UNTIL_VALID_HUMAN_GOLD",
            promptIdentity = "NOT_RUN_UNTIL_VALID_HUMAN_GOLD",
            candidatePolicy = "CURRENT_PRODUCTION_CONFIG_AT_BASELINE_RUN",
            validationPolicy = "CURRENT_PRODUCTION_CONFIG_AT_BASELINE_RUN",
            repairPolicy = "CURRENT_PRODUCTION_CONFIG_AT_BASELINE_RUN",
            goldStatus,
            providerCalls = 0,
        }, JsonOptions), cancellationToken);

        var notMeasured = new
        {
            status = "NOT_MEASURED",
            reason = "valid exhaustive DEV HUMAN_GOLD is required before quality measurement",
            devDocuments = devDocuments,
            devOccurrences,
            providerCalls = 0,
        };
        await WriteAsync(Path.Combine(artifactRoot, "baseline-result.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_baseline_result", schemaVersion = "2.0", notMeasured }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "error-ownership.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_error_ownership", schemaVersion = "2.0", status = "NOT_MEASURED", reason = notMeasured.reason, stages = Enum.GetNames<Accuracy99FirstLossStage>(), providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "optimization-history.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_optimization_history", schemaVersion = "2.0", status = "BLOCKED_ON_REFERENCE", iterations = Array.Empty<object>(), accepted = 0, rejected = 0, providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "best-dev-result.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_best_dev_result", schemaVersion = "2.0", status = "NOT_MEASURED", reason = notMeasured.reason, providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "release-candidate-manifest.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_release_candidate_manifest", schemaVersion = "2.0", status = "NOT_FROZEN", reason = "DEV gate has not been measured", providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "holdout-result.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_holdout_result", schemaVersion = "2.0", status = "TRUE_BLIND_REQUIRED", statusReason = "release candidate is not frozen", providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "autonomous-coverage.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_autonomous_coverage", schemaVersion = "2.0", status = "NOT_MEASURED", devDocuments, devOccurrences, holdoutDocuments, holdoutOccurrences, providerCalls = 0 }, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "final-a99-decision.v2.json"), JsonSerializer.Serialize(new { artifactKind = "a99_final_decision", schemaVersion = "2.0", status = "HUMAN_REFERENCE_REQUIRED", accuracyClaim = "A99_NOT_MEASURED", trueBlindAvailable = false, providerCalls = 0 }, JsonOptions), cancellationToken);
    }

    private static int BuildReviewUi(CommandLineOptions options)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var source = Path.Combine(repoRoot, "tools", "accuracy99-reviewer", "index.html");
        if (!File.Exists(source)) throw new FileNotFoundException("A99 reviewer UI source missing", source);
        var destination = options.Accuracy99ReviewerOutput ?? Path.Combine("C:\\A99-Gold", "reviewer", "index.html");
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.Copy(source, destination, overwrite: true);
        Console.WriteLine(Path.GetFullPath(destination));
        return 0;
    }

    private static int BuildReviewUiV3(CommandLineOptions options)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var source = Path.Combine(repoRoot, "tools", "accuracy99-reviewer", "index.html");
        if (!File.Exists(source)) throw new FileNotFoundException("A99 reviewer UI source missing", source);
        var destination = options.Accuracy99ReviewerOutput ?? Path.Combine("C:\\A99-Gold", "reviewer-v3", "index.html");
        var html = File.ReadAllText(source)
            .Replace("source-first heading reviewer v2", "source-first heading reviewer v3", StringComparison.Ordinal)
            .Replace("HUMAN_GOLD v2", "HUMAN_GOLD v3", StringComparison.Ordinal)
            .Replace("a99-human-gold-v2", "a99-human-gold-v3", StringComparison.Ordinal)
            .Replace("human-gold-v2", "human-gold-v3", StringComparison.Ordinal)
            .Replace("a99-review-v2:", "a99-review-v3:", StringComparison.Ordinal)
            .Replace("['ROOT', 'UNKNOWN', ...state.headings", "['ROOT', ...state.headings", StringComparison.Ordinal)
            .Replace("parentOccurrenceId:h.parent || 'UNKNOWN'", "parentHeadingOccurrenceId:h.parent || 'ROOT'", StringComparison.Ordinal)
            .Replace("unsureSourceIds:[]", "unsureSpans:[]", StringComparison.Ordinal);
        html = html.Replace(
            "const unsureIds = Object.keys(state.unsure || {});",
            "const unsureSpans = Object.keys(state.unsure || {}).map(sourceId => { const source = packet.occurrences.find(x => x.sourceId === sourceId); return { sourceId, span: { start: 0, end: source?.sourceText.length || 0 }, reason: 'reviewer-marked-uncertain' }; });",
            StringComparison.Ordinal)
            .Replace("if (unsureIds.length)", "if (unsureSpans.length)", StringComparison.Ordinal)
            .Replace("unsureSourceIds:[]", "unsureSpans", StringComparison.Ordinal);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(destination, html, new UTF8Encoding(false));
        Console.WriteLine(Path.GetFullPath(destination));
        return 0;
    }

    private static async Task<int> ValidateGoldAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var packetRoot = options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets");
        var goldRoot = options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev");
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        var campaign = LoadCampaign(repoRoot);
        var results = new List<object>();
        var valid = 0;
        var errors = 0;
        foreach (var goldPath in Directory.Exists(goldRoot)
                     ? Directory.EnumerateFiles(goldRoot, "*.human-gold.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                     : Array.Empty<string>())
        {
            var documentId = Path.GetFileName(goldPath).Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
            var campaignDocument = campaign.DevDocuments.Concat(campaign.HoldoutDocuments)
                .FirstOrDefault(x => x.DocumentId.Equals(documentId, StringComparison.Ordinal));
            if (campaignDocument is null) { errors++; results.Add(new { documentId, status = "INVALID", error = "document-not-in-campaign" }); continue; }
            var packetPath = Path.Combine(Path.GetFullPath(packetRoot), campaignDocument.Split == "DEV" ? "dev" : "holdout-sealed", documentId + ".v1.json");
            if (!File.Exists(packetPath)) { errors++; results.Add(new { documentId, status = "INVALID", error = "packet-missing" }); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldDocument>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldValidator.Validate(packet, gold);
                if (validation.IsValid) valid++; else errors += validation.Errors.Count;
                results.Add(new { documentId, status = validation.IsValid ? "VALID" : "INVALID", errors = validation.Errors });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors++; results.Add(new { documentId, status = "INVALID", error = ex.Message });
            }
        }
        var report = new
        {
            artifactKind = "a99_human_gold_validation_report",
            status = errors == 0 && valid > 0 ? "PASS" : "HUMAN_REFERENCE_REQUIRED",
            packetRoot,
            goldRoot,
            validDocuments = valid,
            errorCount = errors,
            documents = results,
            providerCalls = 0,
        };
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return errors == 0 && valid > 0 ? 0 : 1;
    }

    private static async Task<int> ImportDevGoldAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var campaign = LoadCampaign(repoRoot);
        var coverage = A99GoldImportService.ValidateAndImportDev(
            campaign,
            options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets"),
            options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev"),
            Path.Combine(repoRoot, "eval", "a99-closed-loop"));
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(coverage, JsonOptions), cancellationToken);
        Console.WriteLine($"DEV gold: {coverage.DevDocumentsValidated}/{coverage.DevDocumentsExpected} documents, status={coverage.Status}");
        return coverage.Status == "READY" ? 0 : 1;
    }

    private static async Task<int> ValidateEarlyDevGoldV2Async(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var early = LoadEarlyDevCampaign(repoRoot);
        var packetRoot = options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets");
        var goldRoot = options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev-v2");
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        var results = new List<object>();
        var valid = 0;
        var errors = 0;
        foreach (var document in early.Documents)
        {
            var packetPath = Path.Combine(Path.GetFullPath(packetRoot), "dev", document.DocumentId + ".v1.json");
            if (!File.Exists(packetPath)) packetPath = Path.Combine(Path.GetFullPath(packetRoot), document.DocumentId + ".v1.json");
            var goldPath = Path.Combine(Path.GetFullPath(goldRoot), document.DocumentId + ".human-gold-v2.json");
            if (!File.Exists(goldPath)) { errors++; results.Add(new { documentId = document.DocumentId, status = "MISSING", error = "gold-missing" }); continue; }
            if (!File.Exists(packetPath)) { errors++; results.Add(new { documentId = document.DocumentId, status = "INVALID", error = "packet-missing" }); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV2Document>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldV2Validator.Validate(packet, gold);
                if (!string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    validation = new A99GoldValidationResult(false, [.. validation.Errors, "packet-source-sha-not-bound-to-early-campaign"]);
                }
                if (!string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase))
                {
                    validation = new A99GoldValidationResult(false, [.. validation.Errors, "packet-sha-not-bound-to-early-campaign"]);
                }
                if (validation.IsValid) valid++; else errors += validation.Errors.Count;
                results.Add(new { documentId = document.DocumentId, status = validation.IsValid ? "VALID" : "INVALID", errors = validation.Errors });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors++; results.Add(new { documentId = document.DocumentId, status = "INVALID", error = ex.Message });
            }
        }
        var report = new
        {
            artifactKind = "a99_early_dev_gold_validation_report",
            schemaVersion = "a99-early-dev-gold-validation-v2",
            status = errors == 0 && valid == early.Documents.Count ? "READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED",
            expectedDocuments = early.Documents.Count,
            validDocuments = valid,
            errorCount = errors,
            documents = results,
            providerCalls = 0,
        };
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "early-dev-gold-validation.v2.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return errors == 0 && valid == early.Documents.Count ? 0 : 1;
    }

    private static async Task<int> ImportEarlyDevGoldV2Async(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var early = LoadEarlyDevCampaign(repoRoot);
        var coverage = A99GoldImportService.ValidateAndImportEarlyDevV2(
            early,
            options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets"),
            options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev-v2"),
            Path.Combine(repoRoot, "eval", "a99-closed-loop"));
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(coverage, JsonOptions), cancellationToken);
        Console.WriteLine($"Early DEV v2 gold: {coverage.DocumentsValidated}/{coverage.DocumentsExpected} documents, {coverage.HeadingPositives} positives, status={coverage.Status}");
        return coverage.Status == "READY_FOR_BASELINE" ? 0 : 1;
    }

    private static async Task<int> ValidateEarlyDevGoldV3Async(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var early = LoadEarlyDevCampaign(repoRoot);
        var packetRoot = options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets");
        var goldRoot = options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev-v3");
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        var results = new List<object>();
        var valid = 0;
        var errors = 0;
        foreach (var document in early.Documents)
        {
            var packetPath = Path.Combine(Path.GetFullPath(packetRoot), "dev", document.DocumentId + ".v1.json");
            var goldPath = Path.Combine(Path.GetFullPath(goldRoot), document.DocumentId + ".human-gold-v3.json");
            if (!File.Exists(goldPath)) { errors++; results.Add(new { documentId = document.DocumentId, status = "MISSING", error = "gold-missing" }); continue; }
            if (!File.Exists(packetPath)) { errors++; results.Add(new { documentId = document.DocumentId, status = "INVALID", error = "packet-missing" }); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV3Document>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldV3Validator.Validate(packet, gold);
                if (!string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    validation = new A99GoldValidationResult(false, [.. validation.Errors, "packet-source-sha-not-bound-to-early-campaign"]);
                if (!string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase))
                    validation = new A99GoldValidationResult(false, [.. validation.Errors, "packet-sha-not-bound-to-early-campaign"]);
                if (validation.IsValid) valid++; else errors += validation.Errors.Count;
                results.Add(new { documentId = document.DocumentId, status = validation.IsValid ? "VALID" : "INVALID", errors = validation.Errors });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors++; results.Add(new { documentId = document.DocumentId, status = "INVALID", error = ex.Message });
            }
        }
        var report = new
        {
            artifactKind = "a99_early_dev_gold_validation_report",
            schemaVersion = "a99-early-dev-gold-validation-v3",
            status = errors == 0 && valid == early.Documents.Count ? "READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED",
            expectedDocuments = early.Documents.Count,
            validDocuments = valid,
            errorCount = errors,
            documents = results,
            providerCalls = 0,
        };
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "early-dev-gold-validation.v3.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return errors == 0 && valid == early.Documents.Count ? 0 : 1;
    }

    private static async Task<int> ImportEarlyDevGoldV3Async(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var early = LoadEarlyDevCampaign(repoRoot);
        var packetRoot = options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets");
        var goldRoot = options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev-v3");
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        var validated = 0;
        var positives = 0;
        var errors = new List<string>();
        foreach (var document in early.Documents)
        {
            var packetPath = Path.Combine(Path.GetFullPath(packetRoot), "dev", document.DocumentId + ".v1.json");
            var goldPath = Path.Combine(Path.GetFullPath(goldRoot), document.DocumentId + ".human-gold-v3.json");
            if (!File.Exists(packetPath) || !File.Exists(goldPath)) { errors.Add($"{document.DocumentId}:missing"); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV3Document>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldV3Validator.Validate(packet, gold);
                if (!validation.IsValid) { errors.Add($"{document.DocumentId}:{string.Join(",", validation.Errors)}"); continue; }
                if (!string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase))
                { errors.Add($"{document.DocumentId}:campaign-sha-mismatch"); continue; }
                validated++; positives += gold.Rows.Count;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            { errors.Add($"{document.DocumentId}:{ex.Message}"); }
        }
        var status = validated == early.Documents.Count && errors.Count == 0 ? "READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED";
        var report = new
        {
            artifactKind = "a99_early_dev_gold_coverage",
            schemaVersion = "a99-early-dev-gold-coverage-v3",
            status,
            documentsExpected = early.Documents.Count,
            documentsValidated = validated,
            headingPositives = positives,
            errors,
            providerCalls = 0,
        };
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "early-dev-gold-coverage.v3.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        Console.WriteLine($"Early DEV v3 gold: {validated}/{early.Documents.Count} documents, {positives} positives, status={status}");
        return status == "READY_FOR_BASELINE" ? 0 : 1;
    }

    private static async Task<int> PreflightStrictDevCohortAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var sourceRoot = Path.GetFullPath(options.Accuracy99SourceRoot ?? "C:\\DocxHeaderExtractor-harness-lift-v2");
        var packetRoot = Path.GetFullPath(options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets"));
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"A99 source root missing: {sourceRoot}");

        var cohortPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-dev-gold-cohort.v1.json");
        var authorityPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-authority.v1.json");
        var cohortNode = JsonNode.Parse(await File.ReadAllTextAsync(cohortPath, cancellationToken))?.AsObject()
                         ?? throw new InvalidDataException("strict cohort JSON is not an object");
        var authorityNode = JsonNode.Parse(await File.ReadAllTextAsync(authorityPath, cancellationToken))?.AsObject()
                            ?? throw new InvalidDataException("strict authority JSON is not an object");
        var cohort = DeserializeRequired<A99StrictDevGoldCohortArtifact>(cohortNode.ToJsonString());
        if (cohort.StrictCohortDocumentCount != 15 || cohort.StrictCohort.Count != 15)
            throw new InvalidDataException("strict-cohort-must-contain-15-documents");
        if (!string.Equals(cohort.Holdout, "SEALED", StringComparison.Ordinal))
            throw new InvalidDataException("strict-cohort-holdout-must-be-sealed");

        var inventory = ReadStrictPreflightInventory(Path.Combine(repoRoot, "eval", "a99-dataset", "document-inventory.v1.json"));
        var splits = ReadSplits(Path.Combine(repoRoot, "eval", "a99-dataset", "evaluation-splits.v1.json"));
        var assisted = new HashSet<string>(cohort.AssistedPilotDocumentIds, StringComparer.Ordinal);
        var activeIds = cohort.StrictCohort.Select(x => x.DocumentId).ToHashSet(StringComparer.Ordinal);
        var replacementPool = inventory
            .Where(x => string.Equals(x.MediaType, "DOCX", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.MediaType, "DOCM", StringComparison.OrdinalIgnoreCase))
            .Where(x => splits.TryGetValue(x.DocumentGroupId, out var split) && string.Equals(split, "DEV", StringComparison.Ordinal))
            .Where(x => !activeIds.Contains(x.DocumentId) && !assisted.Contains(x.DocumentId))
            .OrderBy(x => x.DocumentId, StringComparer.Ordinal)
            .ToList();

        var activeRows = cohort.StrictCohort.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var results = new List<StrictPreflightReport>();
        var replacements = new List<object>();
        var usedReplacementIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var original in cohort.StrictCohort)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ToStrictPreflightCandidate(original);
            var attempt = TryCreateStrictPreflightPacket(current, sourceRoot);
            var selected = current;
            var replaced = false;

            if (!attempt.IsRepresentable)
            {
                StrictPreflightAttempt? replacementAttempt = null;
                StrictPreflightCandidate? replacement = null;
                foreach (var candidate in replacementPool.Where(x => !usedReplacementIds.Contains(x.DocumentId)))
                {
                    var candidateValue = new StrictPreflightCandidate(
                        candidate.DocumentId, candidate.DocumentGroupId, candidate.SourcePath,
                        candidate.SourceSha256, candidate.FamilyId, candidate.FamilyAssignmentAuthority);
                    var candidateAttempt = TryCreateStrictPreflightPacket(candidateValue, sourceRoot);
                    if (candidateAttempt.IsRepresentable)
                    {
                        replacement = candidateValue;
                        replacementAttempt = candidateAttempt;
                        break;
                    }
                }

                if (replacement is null || replacementAttempt is null)
                    throw new InvalidDataException($"strict-cohort-cannot-replace:{original.DocumentId}:{attempt.FailureReason}");

                usedReplacementIds.Add(replacement.DocumentId);
                replacements.Add(new
                {
                    replacedDocumentId = original.DocumentId,
                    replacementDocumentId = replacement.DocumentId,
                    originalStatus = attempt.Representability?.Status.ToString() ?? "RepresentationIndeterminate",
                    originalReason = attempt.FailureReason,
                    replacementStatus = replacementAttempt.Representability!.Status.ToString(),
                    replacementReason = replacementAttempt.Representability.Reason,
                });
                selected = replacement;
                attempt = replacementAttempt;
                replaced = true;
            }

            var packetPath = Path.Combine(packetRoot, "dev", selected.DocumentId + ".v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(packetPath)!);
            await File.WriteAllTextAsync(
                packetPath,
                A99ReviewJson.Serialize(attempt.Packet!) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);

            var report = new StrictPreflightReport
            {
                DocumentId = selected.DocumentId,
                OriginalDocumentId = replaced ? original.DocumentId : null,
                DocumentGroupId = selected.DocumentGroupId,
                SourcePath = selected.SourcePath,
                SourceSha256 = attempt.ActualSourceSha256 ?? selected.SourceSha256,
                SourceHashReconciled = attempt.SourceHashReconciled,
                PacketPath = Path.Combine("dev", selected.DocumentId + ".v1.json").Replace('\\', '/'),
                PacketSha256 = attempt.Packet!.PacketSha256!,
                SourceOccurrenceCount = attempt.Packet.Occurrences.Count,
                Representability = attempt.Representability!,
                Disposition = replaced ? "REPLACEMENT_ACTIVE_STRICT" : "RETAINED_REPRESENTABLE",
            };
            results.Add(report);
            activeRows.Remove(original.DocumentId);
            activeRows[selected.DocumentId] = new A99StrictCohortDocument
            {
                DocumentId = selected.DocumentId,
                DocumentGroupId = selected.DocumentGroupId,
                Split = "DEV",
                FamilyId = selected.FamilyId,
                FamilyAssignmentAuthority = selected.FamilyAssignmentAuthority,
                SourcePath = selected.SourcePath,
                SourceSha256 = report.SourceSha256,
                PacketPath = report.PacketPath,
                PacketSha256 = report.PacketSha256,
                SelectionRole = report.Disposition,
                Reason = replaced
                    ? $"Replacement selected by strict representability preflight for {original.DocumentId}; source-only and DEV metadata constrained."
                    : original.Reason,
                ExposureStatus = "NOT_MODEL_ASSISTED",
            };
        }

        if (activeRows.Count != 15 || results.Count != 15 || results.Any(x => x.Representability.Status != A99SourceRepresentabilityStatus.CharacterSpanRepresentable))
            throw new InvalidDataException("strict-review-queue-not-exactly-15-representable-documents");

        UpdateStrictCohortJson(cohortNode, activeRows.Values.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray(), replacements);
        await WriteAsync(cohortPath, cohortNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        UpdateStrictAuthorityJson(authorityNode, cohort.StrictCohort.Select(x => x.DocumentId).ToHashSet(StringComparer.Ordinal), activeRows.Values);
        await WriteAsync(authorityPath, authorityNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        var ordered = results
            .OrderBy(x => x.SourceOccurrenceCount)
            .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
            .Select((x, index) => new
            {
                rank = index + 1,
                documentId = x.DocumentId,
                originalDocumentId = x.OriginalDocumentId,
                x.DocumentGroupId,
                split = "DEV",
                x.SourcePath,
                sourceDocumentSha256 = x.SourceSha256,
                packetPath = Path.GetFullPath(Path.Combine(packetRoot, "dev", x.DocumentId + ".v1.json")),
                x.PacketSha256,
                sourceOccurrenceCount = x.SourceOccurrenceCount,
                representability = x.Representability,
                disposition = x.Disposition,
            }).ToArray();

        var artifactRoot = Path.Combine(repoRoot, "eval", "a99-closed-loop");
        var preflight = new
        {
            artifactKind = "a99_strict_cohort_preflight",
            schemaVersion = "a99-strict-cohort-preflight-v1",
            status = "PASS_FROZEN_FOR_HUMAN_REVIEW",
            strictCohortDocumentCount = ordered.Length,
            representableDocuments = ordered.Length,
            imageOnlyOrIndeterminateReplacements = replacements.Count,
            documents = ordered,
            replacements,
            ordering = "SOURCE_OCCURRENCE_COUNT_ASCENDING_THEN_DOCUMENT_ID",
            humanReview = "REQUIRED_15_OF_15",
            holdoutTouched = false,
            providerCalls = 0,
            sourceMetadataReconciliations = results.Where(x => x.SourceHashReconciled).Select(x => new
            {
                x.DocumentId,
                declaredSha256 = cohort.StrictCohort.FirstOrDefault(y => y.DocumentId == (x.OriginalDocumentId ?? x.DocumentId))?.SourceSha256,
                actualSha256 = x.SourceSha256,
                reason = "source-file-hash-is-authoritative; stale cohort metadata corrected without replacing the DEV document",
            }).Where(x => x.declaredSha256 is not null && !string.Equals(x.declaredSha256, x.actualSha256, StringComparison.OrdinalIgnoreCase)).ToArray(),
        };
        var queue = new
        {
            artifactKind = "a99_strict_review_queue",
            schemaVersion = "a99-strict-review-queue-v1",
            status = "FROZEN_FOR_HUMAN_REVIEW",
            split = "DEV",
            documentCount = ordered.Length,
            order = "SMALL_TO_LARGE_SOURCE_OCCURRENCE_COUNT",
            entries = ordered,
            allTextNative = true,
            holdoutTouched = false,
            providerCalls = 0,
            nextAction = $"HUMAN_REVIEW_{ordered[0].documentId}",
        };
        await WriteAsync(Path.Combine(artifactRoot, "strict-cohort-preflight.v1.json"), JsonSerializer.Serialize(preflight, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(artifactRoot, "strict-review-queue.v1.json"), JsonSerializer.Serialize(queue, JsonOptions), cancellationToken);

        var first = ordered[0];
        var nextReview = new
        {
            artifactKind = "a99_next_strict_human_review",
            schemaVersion = "a99-next-strict-review-v1",
            status = "READY_FOR_SOURCE_ONLY_REVIEW",
            nextAction = $"HUMAN_REVIEW_{first.documentId}",
            documentId = first.documentId,
            documentGroupId = first.DocumentGroupId,
            split = "DEV",
            sourcePath = first.SourcePath,
            sourceDocumentSha256 = first.sourceDocumentSha256,
            packetPath = first.packetPath,
            packetSha256 = first.PacketSha256,
            packetArtifactKind = "a99_exhaustive_source_first_review_packet",
            sourceOnly = true,
            modelSuggestionsIncluded = false,
            predictionFieldsIncluded = false,
            providerCalls = 0,
            holdoutTouched = false,
            reviewOutput = Path.Combine("C:\\A99-Gold", "dev-v3", first.documentId + ".human-gold-v3.json"),
            queueArtifact = Path.Combine(artifactRoot, "strict-review-queue.v1.json"),
        };
        await WriteAsync(Path.Combine(artifactRoot, "next-strict-review.v1.json"), JsonSerializer.Serialize(nextReview, JsonOptions), cancellationToken);

        var markdown = new StringBuilder()
            .AppendLine("# A99 Strict Cohort Preflight")
            .AppendLine()
            .AppendLine("The DEV strict queue is frozen only after parser-owned source representability passed for every active slot.")
            .AppendLine()
            .AppendLine($"- Active documents: {ordered.Length}")
            .AppendLine($"- Representable documents: {ordered.Length}")
            .AppendLine($"- Replacements: {replacements.Count}")
            .AppendLine("- Holdout touched: false")
            .AppendLine("- Provider calls: 0")
            .AppendLine()
            .AppendLine("## Review order")
            .AppendLine()
            .AppendLine("| Rank | Document | Occurrences | Source | Status |")
            .AppendLine("| ---: | --- | ---: | --- | --- |")
            .ToString();
        foreach (var entry in ordered)
            markdown += $"| {entry.rank} | `{entry.documentId}` | {entry.sourceOccurrenceCount} | `{entry.SourcePath}` | `{entry.representability.Status}` |{Environment.NewLine}";
        markdown += Environment.NewLine + "Human review remains required; no Gold, baseline, holdout, or accuracy claim was generated by this preflight." + Environment.NewLine;
        await WriteAsync(Path.Combine(repoRoot, "docs", "accuracy", "accuracy99-strict-cohort-preflight.md"), markdown, cancellationToken);

        Console.WriteLine($"A99 strict preflight: {ordered.Length}/{ordered.Length} representable; replacements={replacements.Count}; queue=frozen; first={first.documentId}; providerCalls=0; holdoutTouched=false");
        return 0;
    }

    private static StrictPreflightAttempt TryCreateStrictPreflightPacket(
        StrictPreflightCandidate candidate,
        string sourceRoot)
    {
        try
        {
            var sourcePath = Path.Combine(sourceRoot, candidate.SourcePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath)) return StrictPreflightAttempt.Failed("source-missing");
            var actualSha = HumanGoldValidator.ComputeSha256(sourcePath);
            var sourceHashReconciled = !actualSha.Equals(candidate.SourceSha256, StringComparison.OrdinalIgnoreCase);
            var effectiveCandidate = candidate with { SourceSha256 = actualSha };
            var source = new OpenXmlDocumentSource().Read(sourcePath);
            var packet = A99ReviewPacketBuilder.Create(new A99CampaignDocument
            {
                DocumentId = effectiveCandidate.DocumentId,
                DocumentGroupId = effectiveCandidate.DocumentGroupId,
                Split = "DEV",
                FamilyId = effectiveCandidate.FamilyId,
                SourcePath = sourcePath,
                SourceSha256 = effectiveCandidate.SourceSha256,
                SourceOccurrenceCount = source.Paragraphs.Count,
                PacketPath = $"dev/{effectiveCandidate.DocumentId}.v1.json",
                PacketSha256 = "",
            }, source);
            var representability = A99SourceRepresentabilityGate.Evaluate(packet);
            return new StrictPreflightAttempt(packet, representability, representability.Status == A99SourceRepresentabilityStatus.CharacterSpanRepresentable, representability.Reason, actualSha, sourceHashReconciled);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or ArgumentException)
        {
            return StrictPreflightAttempt.Failed(ex.Message);
        }
    }

    private static void UpdateStrictCohortJson(JsonObject root, IReadOnlyList<A99StrictCohortDocument> documents, IReadOnlyList<object> replacements)
    {
        root["status"] = "FROZEN_FOR_HUMAN_REVIEW_PREFLIGHT_PASS";
        root["strictCohortDocumentCount"] = documents.Count;
        root["providerCalls"] = 0;
        var rows = new JsonArray();
        foreach (var document in documents)
            rows.Add(JsonNode.Parse(A99ReviewJson.Serialize(document)));
        root["strictCohort"] = rows;
        if (replacements.Count > 0)
        {
            var history = root["preflightReplacements"] as JsonArray ?? new JsonArray();
            foreach (var replacement in replacements)
                history.Add(JsonNode.Parse(JsonSerializer.Serialize(replacement, JsonOptions)));
            root["preflightReplacements"] = history;
        }
    }

    private static void UpdateStrictAuthorityJson(
        JsonObject root,
        IReadOnlySet<string> previousStrictIds,
        IEnumerable<A99StrictCohortDocument> activeDocuments)
    {
        var active = activeDocuments.ToArray();
        var activeIds = active.Select(x => x.DocumentId).ToHashSet(StringComparer.Ordinal);
        var documents = root["documents"] as JsonArray ?? new JsonArray();
        for (var index = documents.Count - 1; index >= 0; index--)
        {
            var id = documents[index]?["documentId"]?.GetValue<string>();
            if (id is not null && previousStrictIds.Contains(id) && !activeIds.Contains(id))
                documents.RemoveAt(index);
        }

        var existing = documents
            .Select(x => x?["documentId"]?.GetValue<string>())
            .Where(x => x is not null)
            .ToHashSet(StringComparer.Ordinal)!;
        foreach (var document in active.Where(x => !existing.Contains(x.DocumentId)))
        {
            documents.Add(new JsonObject
            {
                ["documentId"] = document.DocumentId,
                ["validatorStatus"] = "MISSING",
                ["referenceAuthority"] = A99StrictGoldAuthorityRules.NotReviewed,
                ["eligibleForStrictA99Claim"] = false,
                ["exposureStatus"] = "NOT_MODEL_ASSISTED",
                ["note"] = "Replacement strict slot selected by representability preflight; source-only human review pending.",
                ["goldStatus"] = A99StrictGoldAuthorityRules.NotReviewedGold,
                ["finalAuthority"] = A99StrictGoldAuthorityRules.NoFinalAuthority,
                ["referenceProvenance"] = A99StrictGoldAuthorityRules.HumanOnlyProvenance,
                ["userFinalApproval"] = false,
                ["reviewedEntireDocument"] = false,
                ["headingSetExhaustive"] = false,
                ["unresolvedSemanticUncertainty"] = false,
                ["semanticEvaluable"] = false,
                ["occurrenceEvaluable"] = false,
                ["characterSpanEvaluable"] = false,
                ["roleEvaluable"] = false,
                ["levelEvaluable"] = false,
                ["parentEvaluable"] = false,
                ["hierarchyEvaluable"] = false,
            });
        }

        root["documents"] = documents;
        root["strictCohortDocumentCount"] = active.Length;
        root["cohortRevision"] = "v3";
        root["status"] = "FROZEN_COHORT_NOT_READY_FOR_BASELINE";
        root["holdoutStatus"] = "SEALED";
        root["providerCalls"] = 0;
    }

    private static IReadOnlyList<StrictPreflightInventoryItem> ReadStrictPreflightInventory(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray().Select(item => new StrictPreflightInventoryItem(
            item.GetProperty("documentId").GetString()!,
            item.GetProperty("documentGroupId").GetString()!,
            item.GetProperty("sourcePath").GetString()!,
            item.GetProperty("sourceSha256").GetString()!,
            item.GetProperty("familyId").GetString()!,
            item.TryGetProperty("familyAssignmentAuthority", out var authority) ? authority.GetString() ?? "CAMPAIGN_METADATA" : "CAMPAIGN_METADATA",
            item.GetProperty("mediaType").GetString()!)).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadSplits(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("splits").EnumerateArray().ToDictionary(
            item => item.GetProperty("documentGroupId").GetString()!,
            item => item.GetProperty("split").GetString()!,
            StringComparer.Ordinal);
    }

    private static StrictPreflightCandidate ToStrictPreflightCandidate(A99StrictCohortDocument document) => new(
        document.DocumentId,
        document.DocumentGroupId,
        document.SourcePath,
        document.SourceSha256,
        document.FamilyId,
        document.FamilyAssignmentAuthority);

    private sealed record StrictPreflightCandidate(
        string DocumentId,
        string DocumentGroupId,
        string SourcePath,
        string SourceSha256,
        string FamilyId,
        string FamilyAssignmentAuthority);

    private sealed record StrictPreflightInventoryItem(
        string DocumentId,
        string DocumentGroupId,
        string SourcePath,
        string SourceSha256,
        string FamilyId,
        string FamilyAssignmentAuthority,
        string MediaType);

    private sealed record StrictPreflightAttempt(
        A99ReviewPacket? Packet,
        A99SourceRepresentabilityResult? Representability,
        bool IsRepresentable,
        string FailureReason,
        string? ActualSourceSha256 = null,
        bool SourceHashReconciled = false)
    {
        public static StrictPreflightAttempt Failed(string reason) => new(null, null, false, reason);
    }

    private sealed record StrictPreflightReport
    {
        public required string DocumentId { get; init; }
        public string? OriginalDocumentId { get; init; }
        public required string DocumentGroupId { get; init; }
        public required string SourcePath { get; init; }
        public required string SourceSha256 { get; init; }
        public bool SourceHashReconciled { get; init; }
        public required string PacketPath { get; init; }
        public required string PacketSha256 { get; init; }
        public int SourceOccurrenceCount { get; init; }
        public required A99SourceRepresentabilityResult Representability { get; init; }
        public required string Disposition { get; init; }
    }

    private static async Task<int> ReconcileStrictGoldAuthorityPolicyV2Async(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var authorityPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-authority.v1.json");
        var cohortPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-dev-gold-cohort.v1.json");
        var authority = JsonNode.Parse(await File.ReadAllTextAsync(authorityPath, cancellationToken))?.AsObject()
                       ?? throw new InvalidDataException("strict authority JSON is not an object");
        var cohort = DeserializeRequired<A99StrictDevGoldCohortArtifact>(await File.ReadAllTextAsync(cohortPath, cancellationToken));
        if (cohort.StrictCohortDocumentCount != 15 || cohort.StrictCohort.Count != 15)
            throw new InvalidDataException("strict-cohort-must-contain-15-documents");

        var documents = authority["documents"] as JsonArray ?? new JsonArray();
        var known = new Dictionary<string, (int? SemanticTotal, bool CharacterSpan, bool Occurrence, bool Role, bool Level, bool Parent, bool Hierarchy)>(StringComparer.Ordinal)
        {
            ["DOC-0255"] = (null, true, true, true, true, true, true),
            ["DOC-0259"] = (null, true, true, true, true, true, true),
            ["DOC-0202"] = (111, false, false, true, true, true, true),
            ["DOC-0123"] = (317, false, false, false, false, false, false),
        };

        foreach (var (documentId, capability) in known)
        {
            var entry = documents
                .OfType<JsonObject>()
                .SingleOrDefault(x => string.Equals(x["documentId"]?.GetValue<string>(), documentId, StringComparison.Ordinal));
            if (entry is null)
            {
                entry = new JsonObject { ["documentId"] = documentId };
                documents.Add(entry);
            }

            entry["validatorStatus"] = "VALID";
            entry["referenceAuthority"] = A99StrictGoldAuthorityRules.UserFinalizedStrictGold;
            entry["eligibleForStrictA99Claim"] = true;
            entry["exposureStatus"] = "MODEL_ASSISTED";
            entry["note"] = "USER-finalized exhaustive reference; assistance remains truthful provenance metadata and does not disqualify Strict Gold.";
            entry["goldStatus"] = A99StrictGoldAuthorityRules.StrictGold;
            entry["finalAuthority"] = A99StrictGoldAuthorityRules.UserFinalAuthority;
            entry["referenceProvenance"] = A99StrictGoldAuthorityRules.HumanWithModelAssistanceProvenance;
            entry["userFinalApproval"] = true;
            entry["reviewedEntireDocument"] = true;
            entry["headingSetExhaustive"] = true;
            entry["unresolvedSemanticUncertainty"] = false;
            entry["semanticEvaluable"] = true;
            entry["occurrenceEvaluable"] = capability.Occurrence;
            entry["characterSpanEvaluable"] = capability.CharacterSpan;
            entry["roleEvaluable"] = capability.Role;
            entry["levelEvaluable"] = capability.Level;
            entry["parentEvaluable"] = capability.Parent;
            entry["hierarchyEvaluable"] = capability.Hierarchy;
            entry["semanticHeadingTotal"] = capability.SemanticTotal;
        }

        foreach (var entry in documents.OfType<JsonObject>())
        {
            entry["goldStatus"] ??= A99StrictGoldAuthorityRules.NotReviewedGold;
            entry["finalAuthority"] ??= A99StrictGoldAuthorityRules.NoFinalAuthority;
            entry["referenceProvenance"] ??= A99StrictGoldAuthorityRules.HumanOnlyProvenance;
            entry["userFinalApproval"] ??= false;
            entry["reviewedEntireDocument"] ??= false;
            entry["headingSetExhaustive"] ??= false;
            entry["unresolvedSemanticUncertainty"] ??= false;
            entry["semanticEvaluable"] ??= false;
            entry["occurrenceEvaluable"] ??= false;
            entry["characterSpanEvaluable"] ??= false;
            entry["roleEvaluable"] ??= false;
            entry["levelEvaluable"] ??= false;
            entry["parentEvaluable"] ??= false;
            entry["hierarchyEvaluable"] ??= false;
        }

        var activeIds = cohort.StrictCohort.Select(x => x.DocumentId).ToHashSet(StringComparer.Ordinal);
        var typedEntries = documents.OfType<JsonObject>().ToArray();
        var strictEntries = typedEntries.Where(IsCanonicalStrictGold).ToArray();
        authority["documents"] = documents;
        authority["policyVersion"] = "USER_FINALIZED_STRICT_GOLD_V2";
        authority["strictCohortDocumentCount"] = cohort.StrictCohortDocumentCount;
        authority["globalStrictGoldTotal"] = strictEntries.Length;
        authority["strictGoldUserFinalized"] = strictEntries.Count(x => string.Equals(x["finalAuthority"]?.GetValue<string>(), A99StrictGoldAuthorityRules.UserFinalAuthority, StringComparison.Ordinal));
        authority["strictGoldHumanOnly"] = strictEntries.Count(x => string.Equals(x["referenceProvenance"]?.GetValue<string>(), A99StrictGoldAuthorityRules.HumanOnlyProvenance, StringComparison.Ordinal));
        authority["strictGoldHumanWithModelAssistance"] = strictEntries.Count(x => string.Equals(x["referenceProvenance"]?.GetValue<string>(), A99StrictGoldAuthorityRules.HumanWithModelAssistanceProvenance, StringComparison.Ordinal));
        authority["activeStrictCohortGoldValid"] = strictEntries.Count(x => activeIds.Contains(x["documentId"]?.GetValue<string>() ?? string.Empty));
        authority["semanticEvaluableStrictGold"] = strictEntries.Count(x => x["semanticEvaluable"]?.GetValue<bool>() == true);
        authority["characterSpanEvaluableStrictGold"] = strictEntries.Count(x => x["characterSpanEvaluable"]?.GetValue<bool>() == true);
        authority["holdoutStatus"] = "SEALED";
        authority["providerCalls"] = 0;
        await WriteAsync(authorityPath, authority.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        var policy = new JsonObject
        {
            ["artifactKind"] = "a99_strict_gold_authority_policy",
            ["policyVersion"] = "USER_FINALIZED_STRICT_GOLD_V2",
            ["strictGoldDefinition"] = "USER_FINAL_APPROVAL plus reviewedEntireDocument plus headingSetExhaustive plus unresolvedSemanticUncertainty=false",
            ["finalAuthorityRule"] = "FINAL_AUTHORITY=USER is required; model assistance is advisory",
            ["provenanceRule"] = "REFERENCE_PROVENANCE is truthful audit metadata and does not disqualify Strict Gold",
            ["capabilityRule"] = "Semantic Gold and character-span evaluation capability are reported independently; unsupported dimensions are NOT_EVALUABLE",
            ["knownFinalizedDocuments"] = new JsonArray("DOC-0255", "DOC-0259", "DOC-0202", "DOC-0123"),
            ["activeCohortPolicy"] = "Frozen 15-document DEV cohort is unchanged; historical Strict Gold outside the cohort does not reselection-bias it",
            ["globalStrictGoldTotal"] = strictEntries.Length,
            ["activeStrictCohortDocuments"] = cohort.StrictCohortDocumentCount,
            ["activeStrictCohortGoldValid"] = strictEntries.Count(x => activeIds.Contains(x["documentId"]?.GetValue<string>() ?? string.Empty)),
            ["holdoutTouched"] = false,
            ["providerCalls"] = 0,
        };
        await WriteAsync(Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-authority-policy.v2.json"), policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        Console.WriteLine($"A99 strict authority policy v2: globalStrictGold={strictEntries.Length}; activeCohort={cohort.StrictCohortDocumentCount}; activeCohortGold={strictEntries.Count(x => activeIds.Contains(x["documentId"]?.GetValue<string>() ?? string.Empty))}; providerCalls=0; holdoutTouched=false");
        return 0;
    }

    private static bool IsCanonicalStrictGold(JsonObject entry) =>
        string.Equals(entry["goldStatus"]?.GetValue<string>(), A99StrictGoldAuthorityRules.StrictGold, StringComparison.Ordinal) &&
        string.Equals(entry["finalAuthority"]?.GetValue<string>(), A99StrictGoldAuthorityRules.UserFinalAuthority, StringComparison.Ordinal) &&
        entry["userFinalApproval"]?.GetValue<bool>() == true &&
        entry["reviewedEntireDocument"]?.GetValue<bool>() == true &&
        entry["headingSetExhaustive"]?.GetValue<bool>() == true &&
        entry["unresolvedSemanticUncertainty"]?.GetValue<bool>() != true;

    private static async Task<int> ValidateStrictEarlyDevGoldV3Async(
        CommandLineOptions options,
        CancellationToken cancellationToken,
        bool import)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var cohortPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-dev-gold-cohort.v1.json");
        var authorityPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-authority.v1.json");
        var cohort = DeserializeRequired<A99StrictDevGoldCohortArtifact>(await File.ReadAllTextAsync(cohortPath, cancellationToken));
        var authority = DeserializeRequired<A99StrictGoldAuthorityArtifact>(await File.ReadAllTextAsync(authorityPath, cancellationToken));
        if (cohort.StrictCohortDocumentCount != 15 || cohort.StrictCohort.Count != 15)
            throw new InvalidDataException("strict-cohort-must-contain-15-documents");
        if (!string.Equals(cohort.Holdout, "SEALED", StringComparison.Ordinal))
            throw new InvalidDataException("strict-cohort-holdout-must-be-sealed");

        var packetRoot = Path.GetFullPath(options.Accuracy99PacketRoot ?? Path.Combine("C:\\A99-Gold", "packets"));
        var goldRoot = Path.GetFullPath(options.Accuracy99GoldRoot ?? options.Accuracy99GoldPath ?? Path.Combine("C:\\A99-Gold", "dev-v3"));
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        var results = new List<object>();
        var strictValid = 0;
        var strictEligible = 0;
        var schemaValidAssisted = 0;
        var errors = 0;

        foreach (var document in cohort.StrictCohort)
        {
            var authorityEntry = authority.Documents.SingleOrDefault(x => string.Equals(x.DocumentId, document.DocumentId, StringComparison.Ordinal));
            if (authorityEntry is null)
            {
                errors++;
                results.Add(new { documentId = document.DocumentId, status = "INVALID", error = "authority-entry-missing" });
                continue;
            }

            var packetRelativePath = document.PacketPath ?? $"dev/{document.DocumentId}.v1.json";
            var packetPath = Path.Combine(packetRoot, packetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var goldPath = Path.Combine(goldRoot, document.DocumentId + ".human-gold-v3.json");

            A99SourceRepresentabilityResult? representability = null;
            if (File.Exists(packetPath))
            {
                try
                {
                    var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                    representability = A99SourceRepresentabilityGate.Evaluate(packet);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    errors++;
                    results.Add(new { documentId = document.DocumentId, status = "INVALID", error = ex.Message });
                    continue;
                }
            }

            if (representability is not null && representability.Status != A99SourceRepresentabilityStatus.CharacterSpanRepresentable)
            {
                errors++;
                results.Add(new
                {
                    documentId = document.DocumentId,
                    status = "NOT_REPRESENTABLE",
                    validatorStatus = authorityEntry.ValidatorStatus,
                    referenceAuthority = authorityEntry.ReferenceAuthority,
                    eligibleForStrictA99Claim = false,
                    representability,
                });
                continue;
            }

            if (!File.Exists(packetPath) || !File.Exists(goldPath))
            {
                errors++;
                results.Add(new
                {
                    documentId = document.DocumentId,
                    status = "MISSING",
                    validatorStatus = authorityEntry.ValidatorStatus,
                    referenceAuthority = authorityEntry.ReferenceAuthority,
                    eligibleForStrictA99Claim = false,
                    representability,
                    error = !File.Exists(packetPath) ? "packet-missing" : "gold-missing",
                });
                continue;
            }

            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV3Document>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldV3Validator.Validate(packet, gold);
                var campaignBound = string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase);
                var eligible = validation.IsValid && campaignBound &&
                               A99StrictGoldAuthorityRules.IsEligible(authorityEntry, referenceValidated: true);
                if (validation.IsValid && campaignBound) strictValid++;
                if (eligible) strictEligible++;
                if (!eligible) errors++;
                results.Add(new
                {
                    documentId = document.DocumentId,
                    status = validation.IsValid && campaignBound ? "VALID_SCHEMA" : "INVALID",
                    validatorStatus = authorityEntry.ValidatorStatus,
                    referenceAuthority = authorityEntry.ReferenceAuthority,
                    goldStatus = authorityEntry.GoldStatus,
                    finalAuthority = authorityEntry.FinalAuthority,
                    referenceProvenance = authorityEntry.ReferenceProvenance,
                    eligibleForStrictA99Claim = eligible,
                    representability,
                    errors = validation.Errors,
                });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors++;
                results.Add(new { documentId = document.DocumentId, status = "INVALID", error = ex.Message });
            }
        }

        foreach (var documentId in authority.AssistedPilotDocumentIds)
        {
            var packetPath = Path.Combine(packetRoot, "dev", documentId + ".v1.json");
            var goldPath = Path.Combine(goldRoot, documentId + ".human-gold-v3.json");
            if (!File.Exists(packetPath) || !File.Exists(goldPath))
            {
                errors++;
                results.Add(new { documentId, status = "MISSING_ASSISTED_PILOT" });
                continue;
            }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(await File.ReadAllTextAsync(packetPath, cancellationToken));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV3Document>(await File.ReadAllTextAsync(goldPath, cancellationToken));
                var validation = A99HumanGoldV3Validator.Validate(packet, gold);
                if (validation.IsValid) schemaValidAssisted++;
                else errors += validation.Errors.Count;
                results.Add(new { documentId, status = validation.IsValid ? "VALID_SCHEMA_ASSISTED" : "INVALID_ASSISTED", errors = validation.Errors });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors++;
                results.Add(new { documentId, status = "INVALID_ASSISTED", error = ex.Message });
            }
        }

        var ready = strictEligible == cohort.StrictCohortDocumentCount &&
                    strictValid == cohort.StrictCohortDocumentCount &&
                    schemaValidAssisted == authority.AssistedPilotDocumentIds.Count &&
                    errors == 0;
        var report = new
        {
            artifactKind = import ? "a99_strict_dev_gold_coverage" : "a99_strict_gold_validation_report",
            schemaVersion = import ? "a99-strict-dev-gold-coverage-v1" : "a99-strict-gold-validation-v1",
            status = ready ? "STRICT_READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED",
            strictCohortDocuments = cohort.StrictCohortDocumentCount,
            strictHumanGoldValid = strictValid,
            strictEligibleForClaim = strictEligible,
            schemaValidAssisted = schemaValidAssisted,
            assistedPilotDocuments = authority.AssistedPilotDocumentIds.Count,
            errors,
            results,
            holdoutTouched = false,
            providerCalls = 0,
            baseline = "NOT_RUN",
        };
        var defaultName = import ? "strict-dev-gold-coverage.v1.json" : "strict-gold-validation.v1.json";
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", defaultName), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        return ready ? 0 : 1;
    }

    private static async Task<int> RunDoc0205CanaryAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var report = A99Doc0205ReconciliationCanary.Run(
            Path.Combine(repoRoot, "eval", "harness-lift", "reference-occurrence-bridge.v1.json"),
            Path.Combine(repoRoot, "eval", "harness-lift", "model-occurrence-bridge.v1.json"));
        var output = options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "doc-0205-reconciliation-canary.v1.json");
        await WriteAsync(output, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        Console.WriteLine($"DOC-0205 canary: {report.ReferenceCount} references, status={report.Status}, unresolved={report.ClassificationCounts.GetValueOrDefault(A99Doc0205CanaryClassification.Unresolved)}");
        return 0;
    }

    private static async Task<int> BuildModeStratificationAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var corpusPath = Path.Combine(repoRoot, "eval", "harness-lift", "corpus-map.v1.json");
        var documents = ReadCorpus(corpusPath)
            .Where(item => string.Equals(item.Split, "DEV", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .Select(item => new
            {
                documentId = item.DocumentId,
                documentGroupId = item.DocumentGroupId ?? item.DocumentId,
                split = "DEV",
                provenanceFamilyId = item.FamilyId ?? "UNKNOWN",
                familyAssignmentAuthority = item.FamilyAssignmentAuthority ?? "NOT_OBSERVED",
                documentMode = (string?)null,
                documentModeStatus = "NOT_OBSERVED",
                modeEvidence = Array.Empty<object>(),
            }).ToArray();
        var aggregates = documents
            .GroupBy(item => item.documentMode ?? "NOT_OBSERVED", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                mode = group.Key,
                documents = group.Count(),
                groups = group.Select(item => item.documentGroupId).Distinct(StringComparer.Ordinal).Count(),
            }).ToArray();
        var artifact = new
        {
            artifactKind = "a99_document_mode_stratification",
            schemaVersion = "a99-document-mode-stratification-v1",
            status = "PASS",
            source = "eval/harness-lift/corpus-map.v1.json",
            splitPolicy = "DEV_ONLY; GENERALIZATION_HOLDOUT_NOT_OPENED",
            documents,
            aggregates,
            qualityMetricStatus = "NOT_MEASURED",
            providerCalls = 0,
            note = "Stratification preserves provenance family and structural-mode axis. It is not a performance measurement and does not reinterpret PDF_CONVERTED as a capability family.",
        };
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "document-mode-stratification.v1.json"), JsonSerializer.Serialize(artifact, JsonOptions), cancellationToken);
        Console.WriteLine($"A99 mode stratification: {documents.Length} authorized DEV documents; quality=NOT_MEASURED");
        return 0;
    }

    private static async Task<int> RunDoc0027SupportCanaryAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var item = ReadCorpus(Path.Combine(repoRoot, "eval", "harness-lift", "corpus-map.v1.json"))
            .SingleOrDefault(x => string.Equals(x.DocumentId, "DOC-0027", StringComparison.Ordinal));
        if (item is null) throw new InvalidDataException("DOC-0027 is not present in the authorized corpus map.");
        var inputPath = Path.Combine(repoRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(inputPath)) throw new FileNotFoundException("DOC-0027 source file missing.", inputPath);
        var actualSha = HumanGoldValidator.ComputeSha256(inputPath);
        if (!string.Equals(actualSha, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DOC-0027 source SHA mismatch.");
        var pipelineOptions = options.Pipeline;
        pipelineOptions.DisableLlm = true;
        using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
        var execution = await pipeline.RunDocumentExecutionAsync(inputPath, ct: cancellationToken);
        var outline = execution.CompatibilityOutline;
        var audit = outline.RouteAudit;
        var support = DocumentSupportStatus.From(outline);
        var artifact = new
        {
            artifactKind = "a99_doc_0027_support_canary",
            schemaVersion = "a99-doc-0027-support-canary-v1",
            status = "AUDIT_ONLY",
            documentId = item.DocumentId,
            sourceSha256 = actualSha,
            referenceStatus = "NOT_AVAILABLE",
            profile = item.FamilyId ?? "NOT_OBSERVED",
            familyPathHint = item.FamilyId,
            modeEvidence = new
            {
                documentMode = outline.DocumentMode?.Mode.ToString() ?? "NOT_OBSERVED",
                documentModeStatus = outline.DocumentMode is null ? "NOT_OBSERVED" : "OBSERVED_RUNTIME",
                report = outline.DocumentMode,
            },
            route = outline.DeterministicRoute ?? "NOT_OBSERVED",
            candidateConstruction = new
            {
                available = audit?.CandidatesAvailable,
                selected = audit?.CandidatesSelected,
                observed = audit is not null,
            },
            final = new
            {
                headingCount = outline.Headings.Count,
                empty = outline.Headings.Count == 0,
                extractionStatus = support.ExtractionStatus,
                reliabilityStatus = support.ReliabilityStatus,
            },
            providerCalls = execution.Result.Provenance.ProviderCalls,
            productionChanged = false,
            note = "Support-behavior canary only. Historical candidate/selection signatures are retained as context, not expected headings or accuracy labels.",
        };
        await WriteAsync(options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "doc-0027-support-canary.v1.json"), JsonSerializer.Serialize(artifact, JsonOptions), cancellationToken);
        Console.WriteLine($"DOC-0027 support canary: mode={outline.DocumentMode?.Mode.ToString() ?? "NOT_OBSERVED"}, headings={outline.Headings.Count}, status={support.ReliabilityStatus}");
        return 0;
    }

    private static A99ReferenceCampaign LoadCampaign(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "reference-campaign.v1.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Run accuracy99 reference-campaign first.", path);
        return A99ReviewJson.Deserialize<A99ReferenceCampaign>(File.ReadAllText(path));
    }

    private static A99EarlyDevCampaign LoadEarlyDevCampaign(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "early-dev-campaign.v1.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Run accuracy99 early-dev-campaign first.", path);
        return A99ReviewJson.Deserialize<A99EarlyDevCampaign>(File.ReadAllText(path));
    }

    private static async Task WriteReviewSchemasAsync(string reviewRoot, CancellationToken cancellationToken)
    {
        var packetSchema = new
        {
            artifactKind = "a99_review_packet_schema",
            schemaVersion = "a99-review-packet-v1",
            authority = "PARSER_SOURCE_FACTS_ONLY",
            required = new[] { "documentId", "documentGroupId", "split", "familyId", "sourceDocumentSha256", "occurrences" },
            occurrenceRequired = new[] { "sourceId", "stableId", "sourceOrdinal", "sourceSpan", "sourceTextHash", "sourceText", "style", "numbering", "layout" },
            forbidden = new[] { "prediction", "candidate", "confidence", "selected", "validated", "goldLabel", "headingSpan", "parentSourceId" },
        };
        var goldSchema = new
        {
            artifactKind = "a99_human_gold_schema",
            schemaVersion = "a99-human-gold-v1",
            labels = new[] { "YES", "NO", "UNSURE" },
            required = new[] { "reviewerAlias", "reviewedAt", "reviewVersion", "independentOfModelPrediction", "sourceDocumentSha256", "packetSha256", "rows" },
            yes = new { role = "heading|title|caption|label|other", headingSpan = "required", level = "1..9", parentOccurrenceId = "ROOT|sourceId|UNKNOWN" },
            no = new { role = "body|non-heading|other", headingSpan = "absent", level = "absent", parentOccurrenceId = "absent" },
            unsure = new { headingSpan = "absent", level = "absent", parentOccurrenceId = "absent", excludedFromOfficialDenominator = true },
        };
        var policy = new
        {
            artifactKind = "a99_gold_validation_policy",
            schemaVersion = "a99-human-gold-validation-policy-v1",
            exhaustive = true,
            sourceShaAndPacketShaRequired = true,
            duplicateActiveOccurrence = false,
            sourceFirst = true,
            independentOfModelPrediction = "truthful provenance metadata; not an eligibility requirement",
            finalAuthority = "USER_FINAL_APPROVAL",
            provenanceDisqualifiesGold = false,
            holdoutSealedUntilReleaseFreeze = true,
        };
        await WriteAsync(Path.Combine(reviewRoot, "packet-schema.v1.json"), JsonSerializer.Serialize(packetSchema, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(reviewRoot, "gold-schema.v1.json"), JsonSerializer.Serialize(goldSchema, JsonOptions), cancellationToken);
        await WriteAsync(Path.Combine(reviewRoot, "gold-validation-policy.v1.json"), JsonSerializer.Serialize(policy, JsonOptions), cancellationToken);
    }

    private static async Task<int> BuildObservabilityAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var manifestPath = Path.Combine(repoRoot, "eval", "harness-lift", "current-measurement-manifest.v2.json");
        var corpusPath = Path.Combine(repoRoot, "eval", "harness-lift", "corpus-map.v1.json");
        if (!File.Exists(manifestPath) || !File.Exists(corpusPath))
            throw new FileNotFoundException("A99 observability cần current-measurement-manifest.v2.json và corpus-map.v1.json.");

        var trustedDocumentIds = ReadStringArray(manifestPath, "documentIds");
        var corpus = ReadCorpus(corpusPath)
            .Where(item => trustedDocumentIds.Contains(item.DocumentId, StringComparer.Ordinal))
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToArray();
        var missing = trustedDocumentIds
            .Where(id => corpus.All(item => !string.Equals(item.DocumentId, id, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Trusted document không có exact corpus mapping: {string.Join(", ", missing)}");

        var documents = new List<object>();
        var allTraces = new List<RouteOccurrenceTrace>();
        var errors = new List<object>();
        var providerCalls = 0;
        foreach (var item in corpus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(repoRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(inputPath))
            {
                errors.Add(new { item.DocumentId, item.Path, error = "source-file-missing" });
                continue;
            }

            var actualSha = HumanGoldValidator.ComputeSha256(inputPath);
            if (!string.Equals(actualSha, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new { item.DocumentId, item.Path, error = "source-sha-mismatch", expected = item.SourceSha256, actual = actualSha });
                continue;
            }

            var pipelineOptions = options.Pipeline;
            pipelineOptions.DisableLlm = true;
            try
            {
                using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
                var execution = await pipeline.RunDocumentExecutionAsync(inputPath, ct: cancellationToken);
                var audit = execution.CompatibilityOutline.RouteAudit;
                var traces = (audit?.OccurrenceTraces ?? [])
                    .Select(trace => trace with
                    {
                        DocumentId = item.DocumentId,
                        DocumentGroupId = item.DocumentGroupId ?? item.DocumentId,
                    })
                    .ToArray();
                providerCalls += execution.Result.Provenance.ProviderCalls;
                allTraces.AddRange(traces);
                documents.Add(new
                {
                    documentId = item.DocumentId,
                    documentGroupId = item.DocumentGroupId ?? item.DocumentId,
                    split = item.Split,
                    sourceSha256 = actualSha,
                    route = execution.CompatibilityOutline.DeterministicRoute,
                    occurrenceCount = traces.Length,
                    routeObservable = traces.Count(trace => trace.RouteOwner is not "UNKNOWN_ROUTE" and not "ROUTE_NOT_OBSERVABLE"),
                    finalLineageObservable = traces.Count(trace => trace.RepresentationId is not null),
                    modelExposureObservable = traces.Count(trace => trace.ModelRequestMembership is not "UNKNOWN"),
                    traces,
                });
            }
            catch (Exception ex)
            {
                errors.Add(new { item.DocumentId, item.Path, error = ex.GetType().Name, message = ex.Message });
            }
        }

        var total = allTraces.Count;
        var routeObservable = allTraces.Count(trace => trace.RouteOwner is not "UNKNOWN_ROUTE" and not "ROUTE_NOT_OBSERVABLE");
        var finalLineageObservable = allTraces.Count(trace => trace.RepresentationId is not null);
        var modelExposureObservable = allTraces.Count(trace => trace.ModelRequestMembership is not "UNKNOWN");
        var executionRevision = GitRevision(repoRoot) ?? "UNRESOLVED";
        var artifact = new
        {
            artifactKind = "a99_observability_audit",
            schemaVersion = "1.0",
            status = errors.Count == 0 && total > 0 ? "PASS" : "BLOCKED",
            baseRevision = "ee6d68373f63545f4f31e91509f2c00d1033ae05",
            executionRevision,
            population = new
            {
                source = "eval/harness-lift/current-measurement-manifest.v2.json",
                documentIds = trustedDocumentIds,
                documents = documents.Count,
                traces = total,
            },
            coverage = new
            {
                routeObservable,
                routeObservableRate = Rate(routeObservable, total),
                modelExposureObservable,
                modelExposureObservableRate = Rate(modelExposureObservable, total),
                finalLineageObservable,
                finalLineageObservableRate = Rate(finalLineageObservable, total),
                representationMismatchRemaining = allTraces.Count(trace => trace.RepresentationId is null),
                traceUnknownRemaining = allTraces.Count(trace => trace.ModelRequestMembership == "UNKNOWN"),
                knownFinalAbsenceIsObservable = true,
            },
            providerCalls,
            productionOutputDelta = 0,
            expectedChanged = false,
            humanGold = "NOT_IMPORTED",
            trueBlindAvailable = false,
            errors,
            documents,
        };

        var artifactPath = options.OutputPath ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "observability-audit.v1.json");
        await WriteAsync(artifactPath, JsonSerializer.Serialize(artifact, JsonOptions), cancellationToken);
        var schema = new
        {
            artifactKind = "a99_canonical_decision_trace_schema",
            schemaVersion = "1.0",
            authority = new
            {
                source = "parser-owned source occurrence",
                joins = new[] { "EXACT_SOURCE_ID", "EXACT_STABLE_ID", "EXACT_SPAN", "EXPLICIT_BLOCK_SOURCE_REFERENCE", "EXPLICIT_ALIGNMENT", "PARSER_OWNED_LINEAGE" },
                fuzzyJoins = false,
                unknownMustBeRetained = true,
                modelExposureRequiresExplicitRequestMembership = true,
                humanGoldMustNotEnterRuntimeReasoning = true,
            },
            occurrenceFields = new[]
            {
                "documentId", "documentGroupId", "sourceSha256", "sourceId", "stableId", "sourceOrdinal", "sourceSpan",
                "representationId", "representationKind", "candidateId", "routeOwner", "candidateConstructed", "candidateSelected",
                "modelRequestIds", "modelRequestMembership", "modelProposalPresent", "modelRole", "modelLevel", "modelParent", "modelSpan",
                "validationStatus", "validationIssues", "markerBefore", "markerAfter", "markerReason", "structuralBefore", "structuralAfter",
                "structuralReason", "finalIncluded", "finalRole", "finalLevel", "finalParent", "finalSpan",
            },
        };
        var schemaPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "canonical-decision-trace-schema.v1.json");
        await WriteAsync(schemaPath, JsonSerializer.Serialize(schema, JsonOptions), cancellationToken);
        return errors.Count == 0 && total > 0 && providerCalls == 0 ? 0 : 1;
    }

    private static async Task<int> BuildPacketAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var path = RequireInput(options, "packet");
        var source = ReadSource(path);
        var packet = BlindSourcePacketBuilder.CreateFromFile(source);
        var json = JsonSerializer.Serialize(packet, JsonOptions);
        BlindSourcePacketLeakageValidator.EnsureClean(json);
        await WriteAsync(options.OutputPath, json, cancellationToken);
        return 0;
    }

    private static async Task<int> BuildInventoryAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var root = options.Accuracy99Root ?? RequireInput(options, "inventory");
        var inventory = Accuracy99DatasetInventoryBuilder.Discover(root);
        var json = JsonSerializer.Serialize(inventory, JsonOptions);
        await WriteAsync(options.OutputPath, json, cancellationToken);
        return inventory.InvalidSourceCount == 0 ? 0 : 1;
    }

    private static async Task<int> EvaluateAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = RequireInput(options, "evaluate");
        var goldPath = options.Accuracy99GoldPath
                       ?? throw new ArgumentException("evaluate cần --accuracy-gold <path>");
        var predictionPath = options.Accuracy99PredictionPath
                             ?? throw new ArgumentException("evaluate cần --prediction <path>");
        var source = ReadSource(sourcePath);
        var gold = DeserializeRequired<HumanGoldArtifact>(await File.ReadAllTextAsync(goldPath, cancellationToken));
        var outline = await ReadOutlineAsync(predictionPath, cancellationToken);
        var metric = Accuracy99Evaluator.Evaluate(
            source, gold, outline, HumanGoldValidator.ComputeSha256(source.SourcePath));
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(metric, JsonOptions), cancellationToken);
        return 0;
    }

    private static async Task<int> BuildBaselineAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = RequireInput(options, "baseline");
        var profile = options.Accuracy99Profile?.Trim().ToLowerInvariant() ?? StructuralProfile;
        var source = ReadSource(sourcePath);
        if (profile == ProductProfile)
        {
            var notMeasured = new
            {
                artifactKind = "accuracy99_baseline",
                status = "NOT_MEASURED",
                profile,
                reason = "product-provider-not-run-by-accuracy99-infrastructure",
                sourceDocumentSha256 = HumanGoldValidator.ComputeSha256(source.SourcePath),
                providerCalls = 0,
                expectedChanged = false,
                fixedPrompt = FixedPrompt,
            };
            await WriteAsync(options.OutputPath, JsonSerializer.Serialize(notMeasured, JsonOptions), cancellationToken);
            return 0;
        }
        if (profile != StructuralProfile)
            throw new ArgumentException($"accuracy99 baseline profile không hợp lệ: {profile}");

        var pipelineOptions = options.Pipeline;
        pipelineOptions.DisableLlm = true;
        using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
        var outline = await pipeline.RunAsync(sourcePath, cancellationToken);
        var predictions = outline.Headings.Select(Accuracy99Evaluator.FromHeading).ToArray();
        var measured = new
        {
            artifactKind = "accuracy99_baseline",
            status = "MEASURED",
            profile,
            documentId = source.DocumentId,
            sourceDocumentSha256 = HumanGoldValidator.ComputeSha256(source.SourcePath),
            fixedPrompt = FixedPrompt,
            providerCalls = 0,
            expectedChanged = false,
            predictionCount = predictions.Length,
            predictions,
            outline,
            accuracyClaim = "NOT_YET_ESTABLISHED",
        };
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(measured, JsonOptions), cancellationToken);
        return 0;
    }

    private static SourceDocument ReadSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "accuracy99 source packet/evaluator hiện chỉ đọc parser-owned DOCX/ DOCM; PDF reader chưa được cấu hình.");
        return new OpenXmlDocumentSource().Read(path);
    }

    private static async Task<DocumentOutline> ReadOutlineAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("outline", out var outline))
            return DeserializeRequired<DocumentOutline>(outline.GetRawText());
        return DeserializeRequired<DocumentOutline>(json);
    }

    private static T DeserializeRequired<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidDataException($"Không đọc được JSON {typeof(T).Name}.");
    }

    private static string RequireInput(CommandLineOptions options, string operation)
    {
        if (options.Inputs.Count == 0)
            throw new ArgumentException($"{operation} cần một input path.");
        return options.Inputs[0];
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "eval", "harness-lift", "corpus-map.v1.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Không tìm thấy repository từ {start}.");
    }

    private static IReadOnlyList<string> ReadStringArray(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(propertyName).EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidDataException($"{propertyName} chứa giá trị rỗng."))
            .ToArray();
    }

    private static IReadOnlyList<ObservabilityCorpusEntry> ReadCorpus(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray().Select(item => new ObservabilityCorpusEntry(
            item.GetProperty("documentId").GetString() ?? throw new InvalidDataException("corpus documentId missing"),
            item.GetProperty("path").GetString() ?? throw new InvalidDataException("corpus path missing"),
            item.GetProperty("sourceSha256").GetString() ?? throw new InvalidDataException("corpus sourceSha256 missing"),
            item.TryGetProperty("documentGroupId", out var group) ? group.GetString() : null,
            item.TryGetProperty("split", out var split) ? split.GetString() : null,
            item.TryGetProperty("familyId", out var family) ? family.GetString() : null,
            item.TryGetProperty("familyAssignmentAuthority", out var familyAuthority) ? familyAuthority.GetString() : null)).ToArray();
    }

    private static string? GitRevision(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 ? output : null;
    }

    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;

    private sealed record ObservabilityCorpusEntry(
        string DocumentId,
        string Path,
        string SourceSha256,
        string? DocumentGroupId,
        string? Split,
        string? FamilyId = null,
        string? FamilyAssignmentAuthority = null);

    private static async Task WriteAsync(
        string? outputPath,
        string content,
        CancellationToken cancellationToken)
    {
        if (outputPath is null)
        {
            Console.WriteLine(content);
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outputPath, content + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
    }
}
