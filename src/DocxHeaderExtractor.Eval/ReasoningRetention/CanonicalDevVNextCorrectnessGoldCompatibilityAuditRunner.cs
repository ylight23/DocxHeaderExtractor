using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline compatibility audit for the DOC-0116 score inputs. This deliberately does not
/// rescore, repair, or mutate the frozen prediction or Gold artifacts.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldCompatibilityAuditRunner
{
    private const string DocumentId = "DOC-0116";
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1/compatibility-audit-v1";
    private const string AuthorityRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0116";
    private const string OccurrenceRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var authorityManifestPath = Full(repoRoot, AuthorityRoot + "/authority-freeze-manifest.json");
        var sourceAuthorityPath = Full(repoRoot, OccurrenceRoot + "/source-authority.json");
        var exactBindingsPath = Full(repoRoot, OccurrenceRoot + "/exact-bindings.json");
        var occurrenceValidationPath = Full(repoRoot, OccurrenceRoot + "/validation.json");
        var batchValidationPath = Full(repoRoot, "artifacts/authority-audit/canonical-batch-v3.3/validation.json");
        var batchManifestPath = Full(repoRoot, "artifacts/authority-audit/canonical-batch-v3.3/manifest.json");
        var sourceEvidencePath = Full(repoRoot, PreflightRoot + "/source-evidence.v1.json");
        var requestPath = Full(repoRoot, PreflightRoot + "/materialized-request-001.v1.json");

        var required = new[] { authorityManifestPath, sourceAuthorityPath, exactBindingsPath,
            occurrenceValidationPath, batchValidationPath, batchManifestPath, sourceEvidencePath, requestPath };
        if (required.Any(path => !File.Exists(path)))
            return Blocked("COMPATIBILITY_AUDIT_INPUT_MISSING");

        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityManifestPath, ct));
        using var sourceAuthority = JsonDocument.Parse(await File.ReadAllTextAsync(sourceAuthorityPath, ct));
        using var exactBindings = JsonDocument.Parse(await File.ReadAllTextAsync(exactBindingsPath, ct));
        using var occurrenceValidation = JsonDocument.Parse(await File.ReadAllTextAsync(occurrenceValidationPath, ct));
        using var batchValidation = JsonDocument.Parse(await File.ReadAllTextAsync(batchValidationPath, ct));
        using var batchManifest = JsonDocument.Parse(await File.ReadAllTextAsync(batchManifestPath, ct));
        using var sourceEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(sourceEvidencePath, ct));
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, ct));

        var sourcePath = Full(repoRoot, authority.RootElement.GetProperty("sourcePath").GetString()!);
        if (!File.Exists(sourcePath)) return Blocked("SOURCE_DOCUMENT_MISSING");

        var sourceSha = Sha256File(sourcePath);
        var expectedSourceSha = authority.RootElement.GetProperty("sourceSha256").GetString();
        var rawParagraphs = ReadRawParagraphs(sourcePath);
        var rawById = rawParagraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);

        var bindings = exactBindings.RootElement.GetProperty("bindings").EnumerateArray()
            .Select(item => new GoldBinding(
                item.GetProperty("occurrenceId").GetString()!,
                item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceSpan").GetProperty("start").GetInt32(),
                item.GetProperty("sourceSpan").GetProperty("end").GetInt32(),
                item.GetProperty("exactText").GetString()!,
                item.TryGetProperty("sourceContainerText", out var container) ? container.GetString() ?? "" : ""))
            .ToArray();

        var bindingChecks = bindings.Select(binding => CheckBinding(binding, rawById)).ToArray();
        var evidenceIds = sourceEvidence.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetString()!)
            .ToArray();
        var normalizedGroups = evidenceIds
            .GroupBy(NormalizeContainerId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                normalizedContainerId = group.Key,
                aliasCount = group.Count(),
                aliases = group.ToArray(),
                hasDirectAlias = group.Any(alias => string.Equals(alias, group.Key, StringComparison.Ordinal)),
            }).ToArray();
        var sourceContainerCount = sourceAuthority.RootElement.GetProperty("sourceContainerCount").GetInt32();

        var systemPrompt = request.RootElement.GetProperty("systemPrompt").GetString() ?? "";
        var roleContract = new
        {
            detectedStructuralLabelPhrase = systemPrompt.Contains("heading or structural label", StringComparison.OrdinalIgnoreCase),
            containsOtherStructuralLabelRole = systemPrompt.Contains("OTHER_STRUCTURAL_LABEL", StringComparison.Ordinal),
            systemPromptSha256 = Sha256Text(systemPrompt),
            promptExcerpt = systemPrompt[..Math.Min(systemPrompt.Length, 260)],
        };
        var goldContract = new
        {
            scopePolicy = sourceAuthority.RootElement.TryGetProperty("scopePolicy", out var scope) ? scope.GetString() : null,
            trueHeadingField = "isTrueHeading",
            headingKindField = "headingKind",
            sourceAuthorityStatus = sourceAuthority.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null,
        };

        var proposalValidationStatus = GetString(occurrenceValidation.RootElement, "status");
        var batchValidationStatus = GetString(batchValidation.RootElement, "status");
        var proposalAuthority = GetString(batchValidation.RootElement, "proposalAuthority");
        var newUserPromotion = GetBool(batchValidation.RootElement, "newUserReviewedPromotion");
        var claimedUserApproval = GetBool(authority.RootElement, "explicitUserApproval");
        var claimedAuthorityStatus = GetString(authority.RootElement, "authorityStatus");
        var authorityProvenanceConflict =
            string.Equals(proposalValidationStatus, "READY_FOR_USER_SEMANTIC_REVIEW", StringComparison.Ordinal) ||
            string.Equals(batchValidationStatus, "BATCH_V3_3_READY_FOR_USER_SEMANTIC_REVIEW", StringComparison.Ordinal) ||
            string.Equals(proposalAuthority, "AGENT_SOURCE_BACKED_ADJUDICATION", StringComparison.Ordinal) ||
            newUserPromotion == false;

        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var compatibility = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-compatibility-audit-v1",
            status = "OFFICIAL_SCORING_BLOCKED_PENDING_COMPATIBILITY_AUDIT",
            documentId = DocumentId,
            providerCalls = 0,
            modelCalls = 0,
            scoring = false,
            scoreAuthority = new
            {
                claimedAuthorityStatus,
                claimedUserApproval,
                proposalValidationStatus,
                batchValidationStatus,
                proposalAuthority,
                newUserReviewedPromotion = newUserPromotion,
                authorityProvenanceConflict,
                conclusion = authorityProvenanceConflict
                    ? "Freeze manifest claim is not independently corroborated by underlying proposal/batch provenance."
                    : "No provenance conflict detected by this audit."
            },
            source = new
            {
                sourcePath = Rel(repoRoot, sourcePath),
                expectedSourceSha256 = expectedSourceSha,
                actualSourceSha256 = sourceSha,
                sourceShaMatches = string.Equals(expectedSourceSha, sourceSha, StringComparison.OrdinalIgnoreCase),
                rawParagraphCount = rawParagraphs.Length,
            },
            exactBindingAudit = new
            {
                goldBindingCount = bindings.Length,
                sourceExists = bindingChecks.Count(item => item.SourceExists),
                spanWithinSource = bindingChecks.Count(item => item.SpanWithinSource),
                spanTextEqualsExactText = bindingChecks.Count(item => item.SpanTextEqualsExactText),
                exactTextEqualsContainerText = bindingChecks.Count(item => item.ExactTextEqualsContainerText),
                sourceContainerTextEqualsRaw = bindingChecks.Count(item => item.SourceContainerTextEqualsRaw),
                mismatchCount = bindingChecks.Count(item => !item.IsExactCompatible),
                mismatches = bindingChecks.Where(item => !item.IsExactCompatible).ToArray(),
            },
            occurrenceUniverseBridge = new
            {
                runtimeAliasCount = evidenceIds.Length,
                distinctRuntimeAliasCount = evidenceIds.Distinct(StringComparer.Ordinal).Count(),
                runtimeAliasesFoundInRawSource = evidenceIds.Count(rawById.ContainsKey),
                runtimeAliasesMissingFromRawSource = evidenceIds.Where(id => !rawById.ContainsKey(id)).ToArray(),
                normalizedContainerCount = normalizedGroups.Length,
                goldSourceContainerCount = sourceContainerCount,
                deltaRuntimeMinusGoldContainers = evidenceIds.Length - sourceContainerCount,
                deltaNormalizedMinusGoldContainers = normalizedGroups.Length - sourceContainerCount,
                representationGranularityDelta = evidenceIds.Length - normalizedGroups.Length,
                bridgeAcceptedByThisAudit = false,
                groupedNestedAliases = normalizedGroups.Where(group => group.aliasCount > 1).ToArray(),
                statement = "The 1921 runtime aliases and 1896 Gold containers are not directly comparable without an explicitly accepted normalization bridge."
            },
            contractAlignment = new
            {
            providerPrompt = roleContract,
            canonicalGold = goldContract,
                semanticTaskMismatch = roleContract.detectedStructuralLabelPhrase || roleContract.containsOtherStructuralLabelRole,
                statement = "Provider task includes structural labels; Gold authority declares true heading occurrences."
            },
            frozenScoreDisposition = new
            {
                priorScoreArtifactsPreserved = true,
                officialAccuracyClaim = false,
                disposition = "91/198/29 remains provisional and is not an official accuracy result until authority, binding, universe, and contract compatibility are resolved."
            },
            artifactInputs = required.Select(path => new { path = Rel(repoRoot, path), sha256 = Sha256File(path) }).ToArray(),
            auditReadCounts = new { goldReads = 1, providerCalls = 0, modelCalls = 0 },
            auditedAtUtc = DateTimeOffset.UtcNow,
        };

        var jsonPath = Path.Combine(outputDir, "compatibility-audit.v1.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(compatibility, JsonOptions) + Environment.NewLine, ct);
        var reportPath = Path.Combine(outputDir, "report.md");
        await File.WriteAllTextAsync(reportPath, BuildReport(compatibility, bindingChecks, normalizedGroups), Encoding.UTF8, ct);
        var manifest = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-compatibility-audit-manifest-v1",
            status = "OFFICIAL_SCORING_BLOCKED_PENDING_COMPATIBILITY_AUDIT",
            documentId = DocumentId,
            compatibilitySha256 = Sha256File(jsonPath),
            reportSha256 = Sha256File(reportPath),
            sourceSha256 = sourceSha,
            providerCalls = 0,
            goldReads = 1,
            modelCalls = 0,
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var manifestPath = Path.Combine(outputDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);

        Console.WriteLine("STATUS=GOLD_COMPATIBILITY_AUDIT_COMPLETE");
        Console.WriteLine($"BINDINGS={bindings.Length}");
        Console.WriteLine($"BINDING_MISMATCHES={bindingChecks.Count(item => !item.IsExactCompatible)}");
        Console.WriteLine($"RUNTIME_ALIASES={evidenceIds.Length}");
        Console.WriteLine($"NORMALIZED_CONTAINERS={normalizedGroups.Length}");
        Console.WriteLine("OFFICIAL_SCORING=false");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=1");
        return 0;
    }

    private static BindingCheck CheckBinding(GoldBinding binding, IReadOnlyDictionary<string, RawParagraph> rawById)
    {
        if (!rawById.TryGetValue(binding.SourceId, out var source))
            return new BindingCheck(binding, false, false, false, false, false, "SOURCE_ID_NOT_FOUND", null);
        var within = binding.Start >= 0 && binding.End >= binding.Start && binding.End <= source.Text.Length;
        var spanText = within ? source.Text[binding.Start..binding.End] : null;
        var spanEquals = within && string.Equals(spanText, binding.ExactText, StringComparison.Ordinal);
        var exactEqualsContainer = string.Equals(binding.ExactText, binding.SourceContainerText, StringComparison.Ordinal);
        var containerEqualsRaw = string.Equals(binding.SourceContainerText, source.Text, StringComparison.Ordinal);
        var compatible = within && spanEquals && exactEqualsContainer && containerEqualsRaw;
        var reason = compatible ? null : !within ? "SPAN_OUT_OF_RANGE" : !spanEquals ? "SPAN_TEXT_MISMATCH" : !exactEqualsContainer ? "EXACT_TEXT_CONTAINER_MISMATCH" : "CONTAINER_TEXT_SOURCE_MISMATCH";
        return new BindingCheck(binding, true, within, spanEquals, exactEqualsContainer, containerEqualsRaw, reason, source.Text);
    }

    private static RawParagraph[] ReadRawParagraphs(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidOperationException("DOCX body missing");
        var options = new ExtractionOptions { IncludeTables = true };
        var rows = new List<RawParagraph>();
        foreach (var walked in ParagraphWalker.Enumerate(body, options))
        {
            var nested = walked.Element.Descendants<TextBoxContent>().ToHashSet();
            var text = new StringBuilder();
            foreach (var run in walked.Element.Descendants<Run>())
            {
                if (run.Ancestors<TextBoxContent>().Any(nested.Contains) || run.Ancestors<DeletedRun>().Any()) continue;
                foreach (var element in run.Descendants())
                {
                    switch (element)
                    {
                        case Text t when !t.Ancestors<DeletedRun>().Any(): text.Append(t.Text); break;
                        case TabChar: text.Append('\t'); break;
                        case Break: text.Append(' '); break;
                        case NoBreakHyphen: text.Append('-'); break;
                    }
                }
            }
            rows.Add(new RawParagraph(walked.StableId, text.ToString(), walked.Element.InnerText));
        }
        return rows.ToArray();
    }

    private static string NormalizeContainerId(string sourceId)
    {
        var marker = "/txbxContent[";
        var index = sourceId.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? sourceId[..index] : sourceId;
    }

    private static string BuildReport(object compatibility, IReadOnlyList<BindingCheck> checks, IReadOnlyList<dynamic> groups) =>
        "# DOC-0116 Gold compatibility audit\n\n" +
        "Status: `OFFICIAL_SCORING_BLOCKED_PENDING_COMPATIBILITY_AUDIT`\n\n" +
        "This is an offline diagnostic. It does not mutate prediction, Gold, or the prior score.\n\n" +
        $"- Gold bindings checked: **{checks.Count}**\n" +
        $"- Exact binding-compatible rows: **{checks.Count(item => item.IsExactCompatible)}**\n" +
        $"- Binding mismatches: **{checks.Count(item => !item.IsExactCompatible)}**\n" +
        "- Runtime source-evidence aliases: **1921**\n" +
        "- Gold source containers: **1896**\n" +
        "- Provider calls: **0**\n" +
        "- Gold reads: **1**\n\n" +
        "The prior 91/198/29 score remains preserved but is not an official accuracy claim.\n\n" +
        "## Binding mismatches\n\n" +
        (checks.Where(item => !item.IsExactCompatible).Any()
            ? string.Join("\n", checks.Where(item => !item.IsExactCompatible).Select(item => $"- `{item.Binding.SourceId}` span `{item.Binding.Start}..{item.Binding.End}` reason `{item.Reason}`\n  - Gold container: `{item.Binding.SourceContainerText}`\n  - Gold exactText: `{item.Binding.ExactText}`\n  - Source raw: `{item.SourceText}`"))
            : "None.") + "\n\n" +
        "## Interpretation\n\n" +
        "The provider prompt asks for headings or structural labels, while the Gold policy is true heading occurrences. This semantic contract mismatch must be resolved before scoring is treated as official. The 1921-to-1896 difference is reported as a representation-granularity bridge, not silently accepted as equivalent.\n";

    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool GetBool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 2;
    }

    private sealed record GoldBinding(string OccurrenceId, string SourceId, int Start, int End, string ExactText, string SourceContainerText);
    private sealed record RawParagraph(string SourceId, string Text, string InnerText);
    private sealed record BindingCheck(GoldBinding Binding, bool SourceExists, bool SpanWithinSource, bool SpanTextEqualsExactText, bool ExactTextEqualsContainerText, bool SourceContainerTextEqualsRaw, string? Reason, string? SourceText)
    {
        public bool IsExactCompatible => SourceExists && SpanWithinSource && SpanTextEqualsExactText && ExactTextEqualsContainerText && SourceContainerTextEqualsRaw;
    }
}
