using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Full-corpus production baseline orchestration. Gold is not opened while this runner materializes
/// production predictions. Scoring is deliberately a later phase after prediction freeze.
/// </summary>
public static class CanonicalDevV1BaselineRunner
{
    private const string AuthorityManifest = "artifacts/authority-audit/canonical-authority-freeze-v1/corpus-manifest.json";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-dev-v1-exec-v2";
    private const string Benchmark = "CANONICAL_DEV_V1";
    private const string CampaignId = "CANONICAL_DEV_V1_EXEC_V2";
    private const string ProductionSemanticCheckpoint = "40a0d5f";
    private const string ExecutionHarnessCheckpoint = "7823e42";
    private const int ExpectedDocuments = 15;
    private const int ExpectedOccurrences = 1908;
    private const int ExpectedSemanticNodes = 1887;
    private const int ExpectedParentEdges = 1887;
    private const string CodeCheckpoint = "40a0d5f";
    private const int MaxAttemptsPerLogicalCall = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        var authorityPath = Path.Combine(repoRoot, AuthorityManifest.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(authorityPath)) return await BlockAsync(output, "CANONICAL_GOLD_AGGREGATE_MISMATCH", ct);
        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityPath, ct));
        var documents = authority.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new CorpusDocument(
                item.GetProperty("documentId").GetString()!,
                item.GetProperty("occurrences").GetInt32(),
                item.GetProperty("semanticNodes").GetInt32(),
                item.GetProperty("parentEdges").GetInt32(),
                item.GetProperty("sourcePath").GetString() ?? "",
                item.GetProperty("sourceSha256").GetString() ?? ""))
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToArray();
        if (documents.Length != ExpectedDocuments ||
            documents.Sum(item => item.Occurrences) != ExpectedOccurrences ||
            documents.Sum(item => item.SemanticNodes) != ExpectedSemanticNodes ||
            documents.Sum(item => item.ParentEdges) != ExpectedParentEdges)
            return await BlockAsync(output, "CANONICAL_GOLD_AGGREGATE_MISMATCH", ct);

        var inventory = LoadInventory(repoRoot);
        var resolved = documents.Select(item => ResolveSource(repoRoot, item, inventory)).ToArray();
        if (resolved.Any(item => !item.SourceExists || !string.Equals(item.SourceSha256, item.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
            {
                schemaVersion = "a99-canonical-dev-v1-manifest-v1",
                status = "BLOCKED_ON_SOURCE_LINEAGE",
                benchmark = Benchmark,
                devExposed = true,
                blindHoldout = false,
                goldAvailableBeforePredictionFreeze = false,
                authorityManifestSha256 = Sha256File(authorityPath),
                documents = resolved,
                goldReadCount = 0,
                providerCalls = 0,
            }, ct);
            return 2;
        }

        var envRemote = RemoteInferenceOptions.FromEnvironment("openrouter");
        var documentTimeout = ResolveDocumentTimeout();
        var runConfiguration = BuildRunConfiguration(repoRoot, authorityPath, envRemote, documentTimeout);
        var runConfigurationPath = Path.Combine(output, "run-configuration.v1.json");
        var runConfigurationHash = Sha256Text(JsonSerializer.Serialize(runConfiguration, JsonOptions));
        if (File.Exists(runConfigurationPath) && !string.Equals(Sha256CanonicalJsonFile(runConfigurationPath), runConfigurationHash, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(output, "RUN_CONFIGURATION_DRIFT", ct);
        await WriteJsonJsonIfAbsentAsync(runConfigurationPath, runConfiguration);

        await WriteJsonJsonIfAbsentAsync(Path.Combine(output, "campaign-registry.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-campaign-registry-v1",
            benchmark = Benchmark,
            campaigns = new[]
            {
                new { campaignId = "CANONICAL_DEV_V1_EXEC_V1", status = "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY", scorable = false, reason = "MISSING_RUN_CONFIGURATION_HASH_FOR_EXISTING_ACCEPTED_PREDICTION" },
                new { campaignId = CampaignId, status = "AUTHORIZED_FOR_PROVIDER_EXECUTION", scorable = false, reason = "PREDICTION_FREEZE_PENDING" },
            },
            oldCampaignArtifactRoot = "artifacts/level-accuracy/canonical-dev-v1/",
            created = DateTimeOffset.UtcNow,
        });
        var manifest = new
        {
            schemaVersion = "a99-canonical-dev-v1-manifest-v1",
            status = "PRODUCTION_RUN_STARTED",
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticCheckpoint = ProductionSemanticCheckpoint,
            executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
            devExposed = true,
            blindHoldout = false,
            generalizationClaim = false,
            authorityFreezeLineage = new { commit = "51ebb87", completionCommit = "327d5d2", path = AuthorityManifest, sha256 = Sha256File(authorityPath) },
            aggregate = new { documents = ExpectedDocuments, occurrences = ExpectedOccurrences, semanticNodes = ExpectedSemanticNodes, parentEdges = ExpectedParentEdges },
            sourceManifest = resolved,
            goldAvailableBeforePredictionFreeze = false,
            goldReadCount = 0,
            historicalLevelRead = false,
            historicalParentRead = false,
            historicalHierarchyRead = false,
            predictionMutationAfterFreeze = false,
            productionEntryPoint = "AuthorityExtractionPipeline",
            model = envRemote.Model,
            endpoint = envRemote.Endpoint.ToString(),
            runConfigurationHash,
            runConfigurationPath = Path.GetRelativePath(repoRoot, runConfigurationPath).Replace('\\', '/'),
        };
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), manifest, ct);

        var selection = new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = envRemote,
        };
        selection.Remote.Validate();

        var documentRuns = new List<object>();
        var totalProviderCalls = 0;
        var runAborted = false;
        foreach (var source in resolved)
        {
            var docDir = Path.Combine(output, source.DocumentId);
            Directory.CreateDirectory(docDir);
            var predictionPath = Path.Combine(docDir, "prediction.v1.json");
            var attemptsPath = Path.Combine(docDir, "attempts.v1.json");

            if (File.Exists(predictionPath) && File.Exists(attemptsPath))
            {
                var existingCalls = ReadProviderCalls(attemptsPath);
                totalProviderCalls += existingCalls;
                documentRuns.Add(new
                {
                    documentId = source.DocumentId,
                    status = "REUSED_COMPLETED_ARTIFACT",
                    predictionPath = Path.Combine(source.DocumentId, "prediction.v1.json").Replace('\\', '/'),
                    providerCalls = existingCalls,
                    reusedExisting = true,
                });
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                runAborted = true;
                var cancelledAt = DateTimeOffset.UtcNow;
                var cancelledOrdinal = Directory.EnumerateFiles(docDir, "attempt*.started.v1.json", SearchOption.TopDirectoryOnly).Count() + 1;
                var cancelledAttemptId = $"{CampaignId}:{source.DocumentId}:A{cancelledOrdinal:D2}:{cancelledAt:yyyyMMddTHHmmssfffZ}";
                await WriteFailureArtifactAsync(docDir, source, "CANCELLED_BEFORE_DOCUMENT", null, cancelledAt, cancelledAttemptId, runConfigurationHash, CancellationToken.None);
                documentRuns.Add(new { documentId = source.DocumentId, status = "CANCELLED", predictionPath = (string?)null, providerCalls = 0, error = "Cancellation requested before document execution." });
                break;
            }

            var started = DateTimeOffset.UtcNow;
            var attemptOrdinal = Directory.EnumerateFiles(docDir, "attempt*.started.v1.json", SearchOption.TopDirectoryOnly).Count() + 1;
            var attemptId = $"{CampaignId}:{source.DocumentId}:A{attemptOrdinal:D2}:{started:yyyyMMddTHHmmssfffZ}";
            var attemptStartedPath = Path.Combine(docDir, $"attempt-{attemptOrdinal:D2}.started.v1.json");
            await WriteJsonAsync(attemptStartedPath, new
            {
                schemaVersion = "a99-canonical-dev-v1-attempt-start-v1",
                benchmark = Benchmark,
                campaignId = CampaignId,
                documentId = source.DocumentId,
                sourcePath = source.SourcePath,
                sourceSha256 = source.SourceSha256,
                started,
                timeoutSeconds = documentTimeout.TotalSeconds,
                runConfigurationHash,
                attemptId,
                retryPolicy = "NONE",
                goldReadBeforePredictionFreeze = false,
                goldReadCount = 0,
            }, CancellationToken.None);
            try
            {
                using var documentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                documentCts.CancelAfter(documentTimeout);
                using var pipeline = new AuthorityExtractionPipeline(
                    new PipelineOptions { DisableLlm = false },
                    new HeaderClassifierFactory(selection));
                var execution = await pipeline.RunDocumentExecutionAsync(source.SourcePath, ct: documentCts.Token);
                var result = execution.Result;
                var audit = execution.CompatibilityOutline.RouteAudit;
                var elements = result.Structure.Elements
                    .Where(item => item.Type is StructuralElementType.Title or StructuralElementType.Subtitle or StructuralElementType.Heading)
                    .OrderBy(item => item.Sources.FirstOrDefault()?.SourceOrdinal ?? int.MaxValue)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();
                var elementIds = elements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var relations = result.Structure.Relations.Where(item => item.Type == StructuralRelationType.ParentChild &&
                    elementIds.Contains(item.FromId) && elementIds.Contains(item.ToId)).ToArray();
                var depth = DeriveDepth(elements, relations);
                var providerCalls = result.Provenance.ProviderCalls;
                totalProviderCalls += providerCalls;
                var requestHash = Sha256Text(JsonSerializer.Serialize(audit?.ModelRequests ?? [], JsonOptions));
                var responseHash = Sha256Text(JsonSerializer.Serialize(audit?.RawAnalystResponses ?? [], JsonOptions));
                var prediction = new
                {
                    schemaVersion = "a99-canonical-dev-v1-production-prediction-v1",
                    benchmark = Benchmark,
                    campaignId = CampaignId,
                    productionSemanticCheckpoint = ProductionSemanticCheckpoint,
                    executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
                    runConfigurationHash,
                    attemptId,
                    requestHash,
                    responseHash,
                    documentId = source.DocumentId,
                    sourcePath = source.SourcePath,
                    sourceSha256 = source.SourceSha256,
                    productionEntryPoint = "AuthorityExtractionPipeline",
                    goldReadBeforePredictionFreeze = false,
                    goldDerivedInput = false,
                    occurrencePredictions = elements.Select(element => new
                    {
                        occurrenceId = element.Sources.FirstOrDefault()?.SourceId,
                        sourceOrdinal = element.Sources.FirstOrDefault()?.SourceOrdinal,
                        sourceSpan = element.Sources.FirstOrDefault()?.Span,
                        sourceText = element.Text,
                        predictedSemanticNodeId = element.Id,
                        predictedRole = element.Role.ToString(),
                        predictedParentSemanticNodeId = element.ParentId,
                        predictedLevel = depth.GetValueOrDefault(element.Id),
                        parserLevel = element.Level,
                    }).ToArray(),
                    semanticNodes = elements.Select(element => new
                    {
                        semanticNodeId = element.Id,
                        memberOccurrenceIds = element.Sources.Select(item => item.SourceId).ToArray(),
                        canonicalText = element.Text,
                        sourceOrder = element.Sources.FirstOrDefault()?.SourceOrdinal ?? int.MaxValue,
                    }).ToArray(),
                    parentEdges = relations.Select(item => new { parentSemanticNodeId = item.FromId, childSemanticNodeId = item.ToId }).ToArray(),
                    derivedLevels = depth.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new { semanticNodeId = item.Key, level = item.Value }).ToArray(),
                    treeValidation = ValidateTree(elements.Select(item => item.Id), relations, depth),
                    sourceTraces = audit?.OccurrenceTraces ?? [],
                    parentCandidateContracts = audit?.ModelInputContracts ?? [],
                    modelRequests = audit?.ModelRequests ?? [],
                    rawResponses = audit?.RawAnalystResponses ?? [],
                    providerCalls,
                    started,
                    completed = DateTimeOffset.UtcNow,
                };
                await WriteJsonJsonIfAbsentAsync(predictionPath, prediction);
                await WriteJsonJsonIfAbsentAsync(attemptsPath, new
                {
                    schemaVersion = "a99-canonical-dev-v1-attempts-v1",
                    documentId = source.DocumentId,
                    campaignId = CampaignId,
                    sourceSha256 = source.SourceSha256,
                    requests = audit?.ModelRequests ?? [],
                    responseCount = audit?.RawAnalystResponses.Count ?? 0,
                    requestContractCount = audit?.ModelInputContracts.Count ?? 0,
                    providerCalls,
                    runConfigurationHash,
                    attemptId,
                    requestHash,
                    responseHash,
                    parseStatus = "PRODUCTION_PIPELINE_COMPLETED",
                    bindingStatus = "PRODUCTION_PIPELINE_BOUND",
                    failureStatus = (string?)null,
                    retryLineage = new { retries = 0, pipelineOwnedRetries = "captured_in_provider_route_if_any" },
                });
                documentRuns.Add(new { documentId = source.DocumentId, status = "COMPLETE", predictionPath = Path.Combine(source.DocumentId, "prediction.v1.json").Replace('\\', '/'), providerCalls, predictedOccurrences = elements.Length, predictedSemanticNodes = elements.Length, predictedParentEdges = relations.Length, elapsedMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds });
            }
            catch (Exception ex)
            {
                var status = ex is OperationCanceledException
                    ? (ct.IsCancellationRequested ? "CANCELLED" : "DOCUMENT_TIMEOUT")
                    : "PRODUCTION_PIPELINE_FAILURE";
                await WriteFailureArtifactAsync(docDir, source, status, ex, started, attemptId, runConfigurationHash, CancellationToken.None);
                documentRuns.Add(new { documentId = source.DocumentId, status, predictionPath = (string?)null, providerCalls = 0, error = ex.Message });
                runAborted = true;
                if (ct.IsCancellationRequested) break;
            }
        }

        var predictionFiles = Directory.EnumerateFiles(output, "prediction.v1.json", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var failureFiles = Directory.EnumerateFiles(output, "failure*.v1.json", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var completedDocumentIds = predictionFiles
            .Select(item => new DirectoryInfo(Path.GetDirectoryName(item)!).Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allPredictionsComplete = !runAborted && completedDocumentIds.Count == ExpectedDocuments && failureFiles.Length == 0;
        if (allPredictionsComplete)
        {
            await WriteJsonAsync(Path.Combine(output, "prediction-freeze-manifest.json"), new
            {
                schemaVersion = "a99-canonical-dev-v1-prediction-freeze-v1",
                status = "PREDICTIONS_FROZEN_BEFORE_SCORING",
                benchmark = Benchmark,
                campaignId = CampaignId,
                runConfigurationHash,
                productionSemanticCheckpoint = ProductionSemanticCheckpoint,
                executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
                frozenAt = DateTimeOffset.UtcNow,
                goldReadBeforePredictionFreeze = false,
                predictionMutation = false,
                documentsScheduled = ExpectedDocuments,
                documentsCompleted = completedDocumentIds.Count,
                documentsFailed = failureFiles.Length,
                predictionArtifacts = predictionFiles.Select(item => new { path = Path.GetRelativePath(repoRoot, item).Replace('\\', '/'), sha256 = Sha256File(item) }).ToArray(),
                failureArtifacts = failureFiles.Select(item => new { path = Path.GetRelativePath(repoRoot, item).Replace('\\', '/'), sha256 = Sha256File(item) }).ToArray(),
                providerCalls = totalProviderCalls,
            }, CancellationToken.None);
        }
        if (allPredictionsComplete)
        {
            var lineageFiles = Directory.EnumerateFiles(output, "*.v1.json", SearchOption.AllDirectories)
                .Where(item => Path.GetFileName(item).Contains("attempt", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileName(item).Contains("failure", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileName(item).Contains("prediction", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(item => new { path = Path.GetRelativePath(repoRoot, item).Replace('\\', '/'), sha256 = Sha256File(item) })
                .ToArray();
            await WriteJsonAsync(Path.Combine(output, "provider-attempt-manifest.json"), new
            {
                schemaVersion = "a99-canonical-dev-v1-provider-attempt-manifest-v1",
                benchmark = Benchmark,
                campaignId = CampaignId,
                runConfigurationHash,
                providerCalls = totalProviderCalls,
                artifacts = lineageFiles,
                frozenAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-production-run-manifest-v1",
            status = allPredictionsComplete ? "PRODUCTION_PREDICTIONS_FROZEN" : "PRODUCTION_RUN_ABORTED",
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash,
            productionSemanticCheckpoint = ProductionSemanticCheckpoint,
            executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
            goldReadCount = 0,
            documents = documentRuns,
            providerCalls = totalProviderCalls,
            documentTimeoutSeconds = documentTimeout.TotalSeconds,
            predictionFreezeManifestSha256 = allPredictionsComplete ? Sha256File(Path.Combine(output, "prediction-freeze-manifest.json")) : null,
        }, CancellationToken.None);
        return allPredictionsComplete ? 0 : 2;
    }

    private static TimeSpan ResolveDocumentTimeout()
    {
        const int defaultSeconds = 300;
        var raw = Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_DOCUMENT_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultSeconds);
    }

    private static object BuildRunConfiguration(string repoRoot, string authorityPath, RemoteInferenceOptions remote, TimeSpan documentTimeout)
    {
        var sourceFiles = new[]
        {
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/AuthorityExtractionPipeline.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs",
        };
        return new
        {
            schemaVersion = "a99-canonical-dev-v1-run-configuration-v1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticCheckpoint = ProductionSemanticCheckpoint,
            executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
            codeCheckpoint = CodeCheckpoint,
            provider = "OpenRouter",
            model = remote.Model,
            endpoint = remote.Endpoint.ToString(),
            contextSize = remote.ContextSize,
            maxOutputTokens = remote.MaxOutputTokens,
            requestTimeoutSeconds = remote.RequestTimeoutSeconds,
            transientRequestRetries = remote.TransientRequestRetries,
            missingIdRetries = remote.MissingIdRetries,
            maxParallelRequests = remote.MaxParallelRequests,
            temperature = "pipeline/provider default; not exposed by RemoteInferenceOptions",
            topP = "pipeline/provider default; not exposed by RemoteInferenceOptions",
            promptVersion = "AuthorityExtractionPipeline production contracts",
            promptHash = "PIPELINE_OWNED_PROMPTS_NOT_EXPOSED_AS_SINGLE_TEMPLATE",
            candidateGenerationVersion = "DocxAuthorityPipeline production candidate path",
            candidateGenerationHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs"),
            parserVersion = "OpenXmlDocumentSource production parser",
            parserHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs"),
            bindingVersion = "RouteOccurrenceTraceBuilder production binding",
            bindingHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteOccurrenceTraceBuilder.cs"),
            sourceUniverseManifestHash = Sha256File(authorityPath),
            productionSemanticConfigurationHash = Sha256Text(string.Join("|", sourceFiles.Select(path => FileHashOrMissing(repoRoot, path)))),
            sourceImplementationHashes = sourceFiles.ToDictionary(
                path => path,
                path => File.Exists(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)))
                    ? Sha256File(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)))
                    : "MISSING",
                StringComparer.Ordinal),
            timeoutPerAttemptSeconds = documentTimeout.TotalSeconds,
            maxAttemptsPerLogicalCall = MaxAttemptsPerLogicalCall,
            retryPolicy = "SEQUENTIAL_ATTEMPTS_ACCEPT_FIRST_VALID_BINDABLE_RESPONSE",
            goldReadsBeforePredictionFreeze = 0,
            historicalReadsBeforePredictionFreeze = 0,
        };
    }

    private static string FileHashOrMissing(string repoRoot, string relativePath)
    {
        var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? Sha256File(path) : "MISSING";
    }

    private static (bool Valid, string Reason) ValidateExistingDoc0001(string predictionPath, string attemptsPath, ResolvedSource source, string runConfigurationHash)
    {
        if (!File.Exists(predictionPath) || !File.Exists(attemptsPath))
            return (false, "DOC-0001_COMPLETION_ARTIFACT_INCOMPLETE");
        try
        {
            using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
            using var attempts = JsonDocument.Parse(File.ReadAllText(attemptsPath));
            var predictionRoot = prediction.RootElement;
            var attemptsRoot = attempts.RootElement;
            var predictionSource = predictionRoot.TryGetProperty("sourceSha256", out var predictionSha) ? predictionSha.GetString() : null;
            var attemptsSource = attemptsRoot.TryGetProperty("sourceSha256", out var attemptsSha) ? attemptsSha.GetString() : null;
            var storedConfig = attemptsRoot.TryGetProperty("runConfigurationHash", out var stored) ? stored.GetString() : null;
            if (!string.Equals(predictionSource, source.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(attemptsSource, source.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return (false, "DOC-0001_SOURCE_SHA_MISMATCH");
            if (!string.Equals(storedConfig, runConfigurationHash, StringComparison.OrdinalIgnoreCase))
                return (false, "DOC-0001_RUN_CONFIGURATION_HASH_MISSING_OR_MISMATCH");
            if (!predictionRoot.TryGetProperty("goldReadBeforePredictionFreeze", out var goldFlag) || goldFlag.GetBoolean())
                return (false, "DOC-0001_GOLD_FIREWALL_FAILED");
            return (true, "VALID");
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return (false, "DOC-0001_ARTIFACT_PARSE_FAILURE");
        }
    }

    private static int ReadProviderCalls(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("providerCalls", out var calls) && calls.TryGetInt32(out var value) ? value : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static async Task WriteFailureArtifactAsync(string docDir, ResolvedSource source, string status, Exception? ex, DateTimeOffset started, string attemptId, string runConfigurationHash, CancellationToken ct)
    {
        var suffix = attemptId[(attemptId.LastIndexOf(':') + 1)..];
        var path = Path.Combine(docDir, $"failure-{suffix}.v1.json");
        await WriteJsonAsync(path, new
        {
            schemaVersion = "a99-canonical-dev-v1-failure-v1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            documentId = source.DocumentId,
            attemptId,
            runConfigurationHash,
            sourcePath = source.SourcePath,
            sourceSha256 = source.SourceSha256,
            goldReadBeforePredictionFreeze = false,
            status,
            exceptionType = ex?.GetType().FullName,
            message = ex?.Message,
            started,
            completed = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static Task WriteJsonJsonIfAbsentAsync(string path, object value)
    {
        if (File.Exists(path)) return Task.CompletedTask;
        return WriteJsonAsync(path, value, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, int> DeriveDepth(
        IReadOnlyList<ValidatedStructuralElement> elements,
        IReadOnlyList<StructuralRelation> relations)
    {
        var ids = elements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var parent = relations.ToDictionary(item => item.ToId, item => item.FromId, StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = id;
            var level = 0;
            while (parent.TryGetValue(current, out var p))
            {
                if (!ids.Contains(p) || !seen.Add(current)) throw new InvalidDataException("PREDICTED_TREE_INVALID");
                current = p;
                level++;
            }
            result[id] = level;
        }
        return result;
    }

    private static object ValidateTree(IEnumerable<string> ids, IReadOnlyList<StructuralRelation> relations, IReadOnlyDictionary<string, int> depth)
    {
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        var parentCounts = relations.GroupBy(item => item.ToId, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
        var dangling = relations.Count(item => !idSet.Contains(item.FromId) || !idSet.Contains(item.ToId));
        var multipleParents = parentCounts.Values.Count(item => item > 1);
        return new { valid = dangling == 0 && multipleParents == 0 && depth.Count == idSet.Count, cycles = 0, multipleParents, dangling, unreachable = idSet.Count - depth.Count, rootCount = idSet.Count - parentCounts.Count, maxDepth = depth.Values.DefaultIfEmpty(0).Max() };
    }

    private static SourceManifestEntry[] LoadInventory(string repoRoot)
    {
        var path = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array) return [];
        return docs.EnumerateArray().Select(item => new SourceManifestEntry(item.GetProperty("documentId").GetString()!, item.GetProperty("sourcePath").GetString() ?? "", item.GetProperty("sourceSha256").GetString() ?? "")).ToArray();
    }

    private static ResolvedSource ResolveSource(string repoRoot, CorpusDocument item, IReadOnlyList<SourceManifestEntry> inventory)
    {
        var sourcePath = item.SourcePath;
        var expectedSha = item.SourceSha256;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            var found = inventory.FirstOrDefault(source => string.Equals(source.DocumentId, item.DocumentId, StringComparison.Ordinal));
            sourcePath = found?.SourcePath ?? "";
            expectedSha = found?.SourceSha256 ?? "";
        }
        var full = string.IsNullOrWhiteSpace(sourcePath) ? "" : Path.GetFullPath(Path.Combine(repoRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var exists = full.Length > 0 && File.Exists(full);
        var sha = exists ? Sha256File(full) : "";
        return new(item.DocumentId, full, expectedSha, sha, exists);
    }

    private static async Task<int> BlockAsync(string output, string reason, CancellationToken ct)
    {
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-canonical-dev-v1-manifest-v1", status = "BLOCKED", reason, goldReadCount = 0, providerCalls = 0 }, ct);
        return 2;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256CanonicalJsonFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Sha256Text(JsonSerializer.Serialize(document.RootElement, JsonOptions));
    }

    private sealed record CorpusDocument(string DocumentId, int Occurrences, int SemanticNodes, int ParentEdges, string SourcePath, string SourceSha256);
    private sealed record SourceManifestEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ResolvedSource(string DocumentId, string SourcePath, string ExpectedSourceSha256, string SourceSha256, bool SourceExists);
}
