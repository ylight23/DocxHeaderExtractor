using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Phase-B live diagnostic for one already observed semantic conflict. The frozen proposals are
/// used only to construct the existing alternatives; Gold and scoring are deliberately absent.
/// The model can select an existing alternative or remain unresolved, while the harness retains
/// source identity and performs the downstream exact bind.
/// </summary>
public static class SemanticConflictAdjudicationRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string ConflictAlias = "S0239";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string FrozenPredictionPath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/prediction.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/semantic-conflict-adjudication/DOC-0205/S0239";
    private const int Calls = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string SystemPrompt = """
You are resolving one semantic disagreement among already discovered proposals.
Do not discover headings. Do not create, modify, or rename source identities. Do not return
source text, coordinates, offsets, hierarchy, levels, parents, relations, confidence, or
explanations. Select exactly one existing alternativeId from this case, or return UNRESOLVED
when the alternatives cannot be resolved from the supplied evidence. The harness owns source
identity, exact text, coordinates, and binding. Return only the JSON object described by the
schema.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var callsDirectory = Path.Combine(output, "calls");
        Directory.CreateDirectory(callsDirectory);

        if (Directory.EnumerateFiles(callsDirectory, "call-*.v1.json").Any())
        {
            await WriteJson(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-semantic-conflict-adjudication-v1",
                status = "ALREADY_FROZEN",
                documentId = DocumentId,
                sourceAlias = ConflictAlias,
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
            }, ct);
            return 1;
        }

        var context = LoadConflictContext(repoRoot);
        var schema = SemanticAdjudicationContract.Schema();
        var caseJson = JsonSerializer.Serialize(context.Case, JsonOptions);
        var userPrompt = "Resolve this one existing semantic conflict. Do not discover or add any proposal.\n" + caseJson;
        var caseHash = Sha256Text(caseJson);
        var schemaHash = Sha256Text(JsonSerializer.Serialize(schema));
        var systemHash = Sha256Text(SystemPrompt);
        var startHead = GitSha(repoRoot);

        // This manifest is written before capability discovery or any inference/provider call.
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-conflict-adjudication-manifest-v1",
            status = "READY_FOR_LIVE_CAMPAIGN",
            baseHead = "dfd484866ee77bebf8ed88ab2f7dafbfb8e9a725",
            startHead,
            documentId = DocumentId,
            sourceAlias = ConflictAlias,
            sourceOccurrence = context.Case.PhysicalSourceIdentity,
            sourceSha256 = context.SourceSha256,
            frozenPredictionPath = FrozenPredictionPath,
            caseId = context.Case.CaseId,
            caseSha256 = caseHash,
            semanticConflictCount = 1,
            model = Model,
            endpoint = Endpoint,
            repeats = Calls,
            sequential = true,
            automaticRetries = false,
            providerRoute = "AUTO_UNPINNED",
            reasoningEnabled = true,
            goldReadBeforeFreeze = false,
            expectedAnswerEncoded = false,
            hierarchyIncluded = false,
            goldIncluded = false,
            promptSha256 = systemHash,
            schemaSha256 = schemaHash,
            taskDecompositionDelta = "CONFLICT_ADJUDICATION_ONLY",
            modelCalls = 0,
            providerCalls = 0,
        }, ct);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return await Blocked(output, "OPENROUTER_API_KEY_MISSING", startHead, ct);

        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint),
            Model = Model,
            ApiKey = apiKey,
            ContextSize = 1_000_000,
            MaxOutputTokens = 48_000,
            RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var capability = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!capability.Available || capability.Capability is null ||
            !string.Equals(capability.Capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.Capability.ReasoningSupported || !capability.Capability.StructuredOutputSupported)
            return await Blocked(output, "MODEL_CAPABILITY_MISMATCH", startHead, ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(
            repoRoot, "semantic-conflict-adjudication", $"{DocumentId}:{ConflictAlias}", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var results = new List<CallResult>();

        for (var number = 1; number <= Calls; number++)
        {
            ct.ThrowIfCancellationRequested();
            CallResult result;
            try
            {
                var provider = await model.CompleteRawStructuredSemanticAsync(
                    DocumentId,
                    ReasoningRoute.ModelCapabilityCeiling.ToString(),
                    $"A99SemanticConflictAdjudication:{DocumentId}:{ConflictAlias}:R{number}",
                    caseJson,
                    context.Case.SourceEvidence.Sum(item => item.Text.Length),
                    context.Case.SourceEvidence.Count,
                    context.Case.SourceEvidence.Count,
                    SystemPrompt,
                    userPrompt,
                    schema,
                    "semantic_conflict_adjudication_v1",
                    ct);
                result = EvaluateCall(number, context, provider.Content, provider.Telemetry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var telemetry = model.Telemetry.LastOrDefault();
                result = FailedCall(number, context.Case, telemetry, ex);
            }

            var callPath = Path.Combine(callsDirectory, $"call-{number:00}.v1.json");
            await WriteJson(callPath, result, ct);
            await WriteJson(Path.Combine(callsDirectory, $"call-{number:00}.freeze.v1.json"), new
            {
                schemaVersion = "a99-semantic-conflict-adjudication-call-freeze-v1",
                callNumber = number,
                caseId = context.Case.CaseId,
                callSha256 = Sha256(callPath),
                model = Model,
                goldReadBeforeFreeze = false,
                frozenUtc = DateTimeOffset.UtcNow,
            }, ct);
            results.Add(result);
            Console.WriteLine($"ADJUDICATION_CALL={number}/{Calls} DECISION={result.Decision ?? "ERROR"} VALID={result.Validation.IsValid} PROVIDER={result.ActualProvider ?? "NONE"}");
        }

        var valid = results.Where(item => item.Validation.IsValid).ToArray();
        var classification = Classify(results, valid);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-conflict-adjudication-summary-v1",
            status = "COMPLETE",
            documentId = DocumentId,
            sourceAlias = ConflictAlias,
            sourceOccurrence = context.Case.PhysicalSourceIdentity,
            caseId = context.Case.CaseId,
            model = Model,
            endpoint = Endpoint,
            startHead,
            endHead = GitSha(repoRoot),
            calls = Calls,
            modelCalls = results.Count(item => item.ProviderCallObserved),
            providerCalls = results.Count(item => item.ProviderCallObserved),
            adjudicationResolved = results.Count(item => item.Validation.IsValid && item.Decision == SemanticAdjudicationDecision.Resolved),
            adjudicationUnresolved = results.Count(item => item.Validation.IsValid && item.Decision == SemanticAdjudicationDecision.Unresolved),
            adjudicationInvalid = results.Count(item => !item.Validation.IsValid),
            classification,
            selectionIds = valid.Where(item => item.Decision == SemanticAdjudicationDecision.Resolved)
                .Select(item => item.SelectedAlternativeId).Distinct(StringComparer.Ordinal).ToArray(),
            telemetry = results.Select(item => new
            {
                item.CallNumber, item.RequestBodySha256, item.RawResponseContentSha256,
                item.ActualProvider, item.ResponseModel, item.ProviderCallId, item.FinishReason,
                item.InputTokens, item.ReasoningTokens, item.OutputTokens, item.LatencyMs,
            }).ToArray(),
            semanticConflictsInput = 1,
            binderInputAfterAdjudication = results.Sum(item => item.BinderInputAfterAdjudication),
            bindFailureAfterAdjudication = results.Sum(item => item.BindFailureAfterAdjudication),
            systemLoss = results.Sum(item => item.SystemLoss),
            goldReadBeforeFreeze = false,
            expectedAnswerEncoded = false,
            hierarchyIncluded = false,
        }, ct);

        return results.Any(item => item.ProviderFailure) ? 1 : 0;
    }

    private static CallResult EvaluateCall(int number, ConflictContext context, string content, RequestPacketTelemetry telemetry)
    {
        SemanticAdjudicationResponse? response = null;
        var parseErrors = new List<string>();
        try
        {
            response = JsonSerializer.Deserialize<SemanticAdjudicationResponse>(ExtractJson(content), JsonOptions);
            if (response is null) parseErrors.Add("EMPTY_RESPONSE");
        }
        catch (Exception ex)
        {
            parseErrors.Add("RESPONSE_PARSE_ERROR:" + ex.GetType().Name);
        }

        SemanticAdjudicationValidationResult validation;
        if (response is null)
        {
            validation = new(false, "INVALID", null, parseErrors);
        }
        else
        {
            var validated = SemanticConflictAdjudicator.ValidateResponse(context.Case, response);
            validation = parseErrors.Count == 0
                ? validated
                : validated with
                {
                    IsValid = false,
                    Status = "INVALID",
                    AcceptedProposal = null,
                    Errors = parseErrors.Concat(validated.Errors).Distinct(StringComparer.Ordinal).ToArray(),
                };
        }

        var accepted = validation.IsValid ? validation.AcceptedProposal : null;
        var binding = accepted is null
            ? new BindingOutcome(0, 0, 0, 0, [])
            : Bind(accepted, context);
        telemetry.StructuredOutputParsed = response is not null && validation.IsValid;
        telemetry.HeadingOutputCount = accepted is null ? 0 : 1;
        return new(
            number,
            context.Case.CaseId,
            telemetry.RequestBodyHash,
            telemetry.CanonicalRequestHash,
            Sha256Text(content),
            response?.Decision,
            response?.SelectedAlternativeId,
            validation,
            telemetry.ProviderRoute,
            telemetry.Model,
            telemetry.ProviderCallId,
            telemetry.FinishReason,
            telemetry.ReportedInputTokens,
            telemetry.ReportedReasoningTokens,
            telemetry.ReportedOutputTokens,
            telemetry.ElapsedMs,
            true,
            false,
            binding.BinderInput,
            binding.BoundCount,
            binding.BindFailure,
            binding.SystemLoss,
            accepted?.SemanticRole);
    }

    private static BindingOutcome Bind(CanonicalSemanticProposal accepted, ConflictContext context)
    {
        var bound = CanonicalSemanticExactBinder.Bind([accepted], context.Aliases, out var observations);
        var hard = CanonicalSemanticHardBindingValidator.Validate(bound, context.Aliases, context.SourceSha256, context.SourceSha256);
        var failed = observations.Count(item => item.Proposal.IsHeading && item.Status != CanonicalSemanticBindingStatus.Bound) +
                     (hard.IsValid ? 0 : hard.Errors.Count);
        var systemLoss = Math.Max(0, 1 - bound.Count);
        return new(1, bound.Count, failed, systemLoss, hard.Errors);
    }

    private static CallResult FailedCall(int number, SemanticAdjudicationCase adjudicationCase, RequestPacketTelemetry? telemetry, Exception exception) =>
        new(
            number,
            adjudicationCase.CaseId,
            telemetry?.RequestBodyHash,
            telemetry?.CanonicalRequestHash,
            null,
            null,
            null,
            new(false, "PROVIDER_FAILURE", null, [$"PROVIDER_FAILURE:{exception.GetType().Name}"]),
            telemetry?.ProviderRoute,
            telemetry?.Model ?? Model,
            telemetry?.ProviderCallId,
            telemetry?.FinishReason,
            telemetry?.ReportedInputTokens,
            telemetry?.ReportedReasoningTokens,
            telemetry?.ReportedOutputTokens,
            telemetry?.ElapsedMs ?? 0,
            telemetry is not null,
            true,
            0,
            0,
            0,
            0,
            null);

    private static string Classify(IReadOnlyList<CallResult> results, IReadOnlyList<CallResult> valid)
    {
        if (results.Any(item => !item.Validation.IsValid)) return "ADJUDICATION_INVALID_OUTPUT";
        if (valid.All(item => item.Decision == SemanticAdjudicationDecision.Unresolved)) return "ADJUDICATION_ALL_UNRESOLVED";
        var selected = valid.Where(item => item.Decision == SemanticAdjudicationDecision.Resolved)
            .Select(item => item.SelectedAlternativeId).Distinct(StringComparer.Ordinal).ToArray();
        return selected.Length == 1 && valid.Count(item => item.Decision == SemanticAdjudicationDecision.Resolved) == valid.Count
            ? "ADJUDICATION_SELECTION_STABLE"
            : "ADJUDICATION_SELECTION_VARIABLE";
    }

    private static ConflictContext LoadConflictContext(string repoRoot)
    {
        var sourcePath = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var predictionPath = Path.Combine(repoRoot, FrozenPredictionPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath)) throw new InvalidDataException("FAITHFUL_SOURCE_MISSING");
        if (!File.Exists(predictionPath)) throw new InvalidDataException("FROZEN_CONFLICT_PREDICTION_MISSING");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var sourceSha = Sha256(sourcePath);
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", item.SourceId, item.SourceOrdinal, item.Text,
                new StructuralSpan(0, item.Text.Length),
                new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = item.SourceId, ParagraphIndex = item.SourceOrdinal }))
            .ToArray();

        using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
        var proposals = JsonSerializer.Deserialize<CanonicalSemanticProposal[]>(
            prediction.RootElement.GetProperty("proposals").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? [];
        var conflictProposals = proposals.Where(item =>
                string.Equals(item.SourceAlias, ConflictAlias, StringComparison.Ordinal) && item.IsHeading)
            .ToArray();
        if (conflictProposals.Length != 2)
            throw new InvalidDataException($"EXPECTED_TWO_FROZEN_ALTERNATIVES:{conflictProposals.Length}");
        var normalization = SemanticConflictNormalizer.Normalize(conflictProposals, aliases);
        if (normalization.AttributeConflicts.Count != 1 || normalization.Conflicts.Count != 0)
            throw new InvalidDataException($"EXPECTED_ONE_ATTRIBUTE_CONFLICT:{normalization.AttributeConflicts.Count}:BLOCKING={normalization.Conflicts.Count}");
        var adjudicationCase = SemanticConflictAdjudicator.CreateCase(normalization.AttributeConflicts[0], aliases);
        if (!string.Equals(adjudicationCase.SourceEvidence.Single().Text, "Chương III", StringComparison.Ordinal))
            throw new InvalidDataException("UNEXPECTED_CONFLICT_SOURCE_TEXT");
        return new(sourceSha, aliases, adjudicationCase);
    }

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```") && trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            trimmed = firstNewline >= 0 ? trimmed[(firstNewline + 1)..^3].Trim() : trimmed[3..^3].Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false,
        });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED";
    }

    private static async Task<int> Blocked(string output, string reason, string head, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-conflict-adjudication-summary-v1",
            status = "BLOCKED", reason, startHead = head, model = Model,
            modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
        return 1;
    }

    private static async Task WriteJson(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false), ct);

    private sealed record ConflictContext(string SourceSha256, IReadOnlyList<SemanticSourceAlias> Aliases, SemanticAdjudicationCase Case);
    private sealed record BindingOutcome(int BinderInput, int BoundCount, int BindFailure, int SystemLoss, IReadOnlyList<string> HardErrors);

    private sealed record CallResult(
        int CallNumber,
        string CaseId,
        string? RequestBodySha256,
        string? CanonicalRequestSha256,
        string? RawResponseContentSha256,
        string? Decision,
        string? SelectedAlternativeId,
        SemanticAdjudicationValidationResult Validation,
        string? ActualProvider,
        string ResponseModel,
        string? ProviderCallId,
        string? FinishReason,
        int? InputTokens,
        int? ReasoningTokens,
        int? OutputTokens,
        long LatencyMs,
        bool ProviderCallObserved,
        bool ProviderFailure,
        int BinderInputAfterAdjudication,
        int BoundCount,
        int BindFailureAfterAdjudication,
        int SystemLoss,
        string? AcceptedRole);
}
