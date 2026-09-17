using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline forensic companion to the V2A shadow audit. It explains the source-owned evidence
/// behind the nine V7 heading-like occurrences that the shadow policy would skip. It never
/// changes the V2A policy, production blocks, or V7 predictions.
/// </summary>
public static class CanonicalDevV2A1RolePrefilterForensicRunner
{
    private const string BaselineCheckpoint = "04516c74ac86e1da8a626c3836b2655414c3cfb4";
    private const string V7BaselineCheckpoint = "c2d01c2ec42912000fd5155174b7ae906c7472bc";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string V2APath = "artifacts/level-accuracy/canonical-dev-v1-v2a-role-prefilter-shadow-v2/DOC-0116/shadow-audit.v1.json";
    private const string V7Path = "artifacts/level-accuracy/canonical-dev-v1-exec-v7-optimized/DOC-0116/prediction.v1.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-dev-v1-v2a1-role-prefilter-forensic-v3";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var startHead = GitSha(repoRoot);
        if (!startHead.Equals(BaselineCheckpoint, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(repoRoot, startHead, "BASELINE_CHECKPOINT_MISMATCH", ct);

        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            return await BlockAsync(repoRoot, startHead, "V2A1_OUTPUT_ALREADY_EXISTS", ct);
        Directory.CreateDirectory(output);

        var inventory = LoadInventory(repoRoot).Single(item => item.DocumentId == "DOC-0116");
        var sourcePath = Path.GetFullPath(Path.Combine(repoRoot, inventory.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(sourcePath) || !Sha256File(sourcePath).Equals(inventory.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(repoRoot, startHead, "SOURCE_HASH_MISMATCH:DOC-0116", ct);

        var v2aPath = Path.Combine(repoRoot, V2APath.Replace('/', Path.DirectorySeparatorChar));
        var v7Path = Path.Combine(repoRoot, V7Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(v2aPath) || !File.Exists(v7Path))
            return await BlockAsync(repoRoot, startHead, "FROZEN_INPUT_ARTIFACT_MISSING", ct);

        using var v2a = JsonDocument.Parse(await File.ReadAllTextAsync(v2aPath, ct));
        using var v7 = JsonDocument.Parse(await File.ReadAllTextAsync(v7Path, ct));
        var v2aRoot = v2a.RootElement;
        var v7Root = v7.RootElement;
        var v7SourceSha = ReadString(v7Root, "sourceSha256");
        if (!string.Equals(v7SourceSha, inventory.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(repoRoot, startHead, "V7_SOURCE_HASH_MISMATCH", ct);

        var v2aDecisions = ReadV2ADecisions(v2aRoot);
        var v7Predictions = ReadV7Predictions(v7Root);
        var v7Traces = ReadV7Traces(v7Root);
        var skipped = v2aDecisions.Values.Where(decision => !decision.Eligible).ToArray();
        var skippedHeadingLike = skipped.Where(decision => v7Predictions.ContainsKey(decision.SourceId)).ToArray();
        var skippedUncertain = skipped.Where(decision =>
            v7Traces.TryGetValue(decision.SourceId, out var trace) &&
            trace.ModelRole is "Unknown" or "Uncertain").ToArray();

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = "DOC-0116" };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var mode = DocumentModeClassifier.Measure(policy.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        var authoritySource = DocxAuthorityPipeline.BuildForAudit(policy, mode);
        var orderedSource = authoritySource.Contexts.Values.OrderBy(context => context.Source.SourceOrdinal).ToArray();

        var rows = skippedHeadingLike
            .OrderBy(item => item.SourceOrdinal)
            .Select(item => BuildRow(item, authoritySource, orderedSource, v7Predictions[item.SourceId], v7Traces.GetValueOrDefault(item.SourceId)))
            .ToArray();
        var uncertainRows = skippedUncertain
            .OrderBy(item => item.SourceOrdinal)
            .Select(item => BuildUncertainRow(item, authoritySource, orderedSource, v7Traces.GetValueOrDefault(item.SourceId)))
            .ToArray();

        var headingGroups = rows.GroupBy(row => row.EvidenceSignature, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { evidenceSignature = group.Key, count = group.Count(), sourceIds = group.Select(item => item.SourceId).ToArray() })
            .ToArray();
        var uncertainGroups = uncertainRows.GroupBy(row => row.EvidenceSignature, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { evidenceSignature = group.Key, count = group.Count(), sourceIds = group.Select(item => item.SourceId).ToArray() })
            .ToArray();

        var forensic = new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a1-role-prefilter-forensic-v1",
            status = "V2A1_FORENSIC_RECOVERY_AUDIT_COMPLETE",
            baselineCheckpoint = BaselineCheckpoint,
            v7BaselineCheckpoint = V7BaselineCheckpoint,
            v2aShadowArtifact = Relative(repoRoot, v2aPath),
            v7PredictionArtifact = Relative(repoRoot, v7Path),
            documentId = "DOC-0116",
            sourceSha256 = inventory.SourceSha256,
            sourceParagraphCount = source.Paragraphs.Count,
            v2aSkippedCount = skipped.Length,
            v2aWouldSkipV7HeadingLikeCount = rows.Length,
            v2aWouldSkipV7UncertainCount = uncertainRows.Length,
            grouping = new
            {
                basis = "SOURCE_DERIVED_EVIDENCE_SIGNATURE_ONLY",
                documentSpecificRules = false,
                textLiteralRules = false,
                headingLikeGroups = headingGroups,
                uncertainGroups,
            },
            headingLikeOccurrences = rows,
            uncertainOccurrences = uncertainRows,
            gates = new
            {
                providerCalls = 0,
                modelCalls = 0,
                goldReads = 0,
                scoring = false,
                productionPrefilterActivated = false,
                v2bStatus = "BLOCKED_ON_BEHAVIOR_PRESERVATION",
            },
        };

        var forensicPath = Path.Combine(output, "DOC-0116", "forensic.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(forensicPath)!);
        await WriteJsonAsync(forensicPath, forensic, ct);

        var summary = new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a1-role-prefilter-forensic-summary-v1",
            status = "V2A1_FORENSIC_RECOVERY_AUDIT_COMPLETE",
            baselineCheckpoint = BaselineCheckpoint,
            v7BaselineCheckpoint = V7BaselineCheckpoint,
            documentId = "DOC-0116",
            sourceSha256 = inventory.SourceSha256,
            skippedHeadingLikeCount = rows.Length,
            skippedUncertainCount = uncertainRows.Length,
            headingLikeEvidenceGroups = headingGroups,
            uncertainEvidenceGroups = uncertainGroups,
            targetWouldSkipV7HeadingLikeCount = 0,
            currentWouldSkipV7HeadingLikeCount = rows.Length,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPrefilterActivated = false,
            nextPhase = "V2B_BLOCKED_UNTIL_GENERIC_RECOVERY_POLICY_AUDIT",
        };
        var summaryPath = Path.Combine(output, "summary.v1.json");
        await WriteJsonAsync(summaryPath, summary, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(rows, uncertainRows, headingGroups, uncertainGroups), Encoding.UTF8, ct);

        var manifestEntries = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.v1.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new { path = Relative(repoRoot, path), sha256 = Sha256File(path) })
            .ToArray();
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a1-role-prefilter-forensic-manifest-v1",
            status = "V2A1_FORENSIC_AUDIT_FROZEN",
            baselineCheckpoint = BaselineCheckpoint,
            v7BaselineCheckpoint = V7BaselineCheckpoint,
            artifacts = manifestEntries,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
            productionPrefilterActivated = false,
        }, ct);

        Console.WriteLine("V2A1_STATUS=V2A1_FORENSIC_RECOVERY_AUDIT_COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        Console.WriteLine($"V2A1_SUMMARY={summaryPath}");
        Console.WriteLine($"SKIPPED_V7_HEADING_LIKE={rows.Length}");
        Console.WriteLine($"SKIPPED_V7_UNCERTAIN={uncertainRows.Length}");
        return 0;
    }

    private static ForensicRow BuildRow(
        V2ADecision decision,
        DocxAuthoritySource authority,
        IReadOnlyList<DocxAuthorityContext> ordered,
        JsonElement prediction,
        V7Trace? trace)
    {
        var context = authority.Contexts[decision.SourceId];
        var source = context.Source;
        var paragraph = context.Paragraph;
        var modelFacts = context.ModelContext.Source;
        var marker = modelFacts.Marker;
        var signature = EvidenceSignature(context, decision);
        return new ForensicRow(
            decision.SourceId,
            source.Text,
            source.SourceOrdinal,
            paragraph.Role.ToString(),
            paragraph.Score,
            paragraph.IsCandidate,
            new
            {
                source.Style.StyleId,
                source.Style.StyleName,
                source.Style.BuiltInHeadingStyleLevel,
                source.Style.OutlineLevel,
                source.Style.Bold,
                source.Style.Italic,
                source.Style.Underline,
                source.Style.AllCaps,
                source.Style.FontSizePt,
                source.Style.Alignment,
            },
            new
            {
                source.Numbering.NumberingId,
                source.Numbering.NumberingLevel,
                source.Numbering.NumberLabel,
                source.Numbering.NumberingFormat,
                source.Numbering.NumberingStyleHeadingLevel,
            },
            marker is null ? null : new { marker.Value.Signature, marker.Value.Depth, marker.Value.Family, marker.Value.IsPath, components = marker.Value.Components.ToArray() },
            new
            {
                context.Scope,
                parserStructuralScope = modelFacts.StructuralScope,
                tableDepth = source.Layout.TableDepth,
                modelFacts.ScopeHostSourceId,
                modelFacts.ScopeTargetDocument,
            },
            new
            {
                domainRole = modelFacts.DomainRole.ToString(),
                role = modelFacts.DomainEvidence.Role.ToString(),
                modelFacts.DomainEvidence.ProposedLevel,
                modelFacts.DomainEvidence.ProposesOutlineExclusion,
                modelFacts.DomainEvidence.IsStructuralRole,
                modelFacts.DomainEvidence.Basis,
                observedEvidence = modelFacts.ObservedEvidence,
                evidenceDetails = modelFacts.EvidenceDetails,
            },
            Neighbors(ordered, source.SourceOrdinal),
            decision.Decision,
            decision.Signals,
            decision.StructuralScope,
            decision.DomainRole,
            decision.PolicyCandidate,
            ReadString(prediction, "predictedRole"),
            ReadScalar(prediction, "confidence"),
            trace?.ModelRole,
            trace?.ModelProposalPresent,
            trace?.ValidationStatus,
            trace?.FinalIncluded,
            trace is null ? "NOT_PERSISTED" : "NOT_PERSISTED_IN_FROZEN_V7_ARTIFACT",
            signature);
    }

    private static UncertainRow BuildUncertainRow(
        V2ADecision decision,
        DocxAuthoritySource authority,
        IReadOnlyList<DocxAuthorityContext> ordered,
        V7Trace? trace)
    {
        var context = authority.Contexts[decision.SourceId];
        return new UncertainRow(decision.SourceId, context.Source.SourceOrdinal, context.Source.Text,
            context.Paragraph.Role.ToString(), context.Paragraph.Score, context.Paragraph.IsCandidate,
            decision.Decision, decision.Signals, EvidenceSignature(context, decision), trace?.ModelRole);
    }

    private static string EvidenceSignature(DocxAuthorityContext context, V2ADecision decision)
    {
        var source = context.Source;
        var paragraph = context.Paragraph;
        var facts = context.ModelContext.Source;
        var marker = facts.Marker;
        var terminal = source.Text.TrimEnd() switch
        {
            var text when text.EndsWith(".", StringComparison.Ordinal) => "period",
            var text when text.EndsWith("?", StringComparison.Ordinal) => "question",
            var text when text.EndsWith("!", StringComparison.Ordinal) => "exclamation",
            var text when text.EndsWith(";", StringComparison.Ordinal) => "semicolon",
            _ => "other",
        };
        var length = source.Text.Trim().Length switch
        {
            <= 40 => "short",
            <= 120 => "medium",
            _ => "long",
        };
        return string.Join("|", new[]
        {
            decision.Decision,
            paragraph.Role.ToString(),
            paragraph.IsCandidate ? "candidate" : "not-candidate",
            facts.StructuralScope,
            facts.DomainRole.ToString(),
            marker is null ? "no-marker" : $"marker:{marker.Value.Family}",
            source.Layout.TableDepth > 0 ? "table" : "not-table",
            source.Style.Bold || source.Style.AllCaps || source.Style.Underline ? "format-signal" : "no-format-signal",
            source.Numbering.NumberingId is not null || source.Numbering.NumberLabel is not null ? "numbering-signal" : "no-numbering-signal",
            terminal,
            length,
        });
    }

    private static object Neighbors(IReadOnlyList<DocxAuthorityContext> ordered, int ordinal)
    {
        var index = Array.FindIndex(ordered.ToArray(), item => item.Source.SourceOrdinal == ordinal);
        var previous = index < 0 ? [] : ordered.Skip(Math.Max(0, index - 2)).Take(index - Math.Max(0, index - 2)).Select(Neighbor).ToArray();
        var next = index < 0 ? [] : ordered.Skip(index + 1).Take(2).Select(Neighbor).ToArray();
        return new { previous, next };
    }

    private static object Neighbor(DocxAuthorityContext context) => new
    {
        sourceId = context.Source.SourceId,
        sourceOrdinal = context.Source.SourceOrdinal,
        text = context.Source.Text,
        role = context.Paragraph.Role.ToString(),
    };

    private static string BuildReport(IReadOnlyList<ForensicRow> rows, IReadOnlyList<UncertainRow> uncertainRows, object[] headingGroups, object[] uncertainGroups)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# V2A.1 Role Prefilter Forensic Recovery Audit");
        builder.AppendLine();
        builder.AppendLine("Offline source-backed forensic audit only. V2A production prefilter remains inactive; no provider, Gold, or scoring path was used.");
        builder.AppendLine();
        builder.AppendLine($"- V7 heading-like occurrences that V2A would skip: **{rows.Count}**");
        builder.AppendLine($"- V7 uncertain occurrences that V2A would skip: **{uncertainRows.Count}**");
        builder.AppendLine("- Grouping basis: generic source-derived evidence signatures; no document-ID or text-literal rule was added.");
        builder.AppendLine("- V2B status: **BLOCKED_ON_BEHAVIOR_PRESERVATION** until a generic recovery policy is separately designed and audited.");
        builder.AppendLine();
        builder.AppendLine("## Heading-like evidence groups");
        foreach (var group in headingGroups)
            builder.AppendLine($"- `{JsonSerializer.Serialize(group)}`");
        builder.AppendLine();
        builder.AppendLine("## Uncertain evidence groups");
        foreach (var group in uncertainGroups)
            builder.AppendLine($"- `{JsonSerializer.Serialize(group)}`");
        builder.AppendLine();
        builder.AppendLine("## Nine-case index");
        builder.AppendLine("| Source ID | Ordinal | Role | Score | Candidate | Skip reason | V7 predicted role | Evidence signature |");
        builder.AppendLine("|---|---:|---|---:|---|---|---|---|");
        foreach (var row in rows)
            builder.AppendLine($"| `{row.SourceId}` | {row.SourceOrdinal} | {row.ParagraphRole} | {row.Score:0.###} | {row.IsCandidate} | {row.SkipReason} | {row.V7PredictedRole ?? "(not persisted)"} | `{row.EvidenceSignature}` |");
        builder.AppendLine();
        builder.AppendLine("Confidence was not persisted in the frozen V7 prediction artifact; it is reported as unavailable rather than reconstructed.");
        builder.AppendLine("providerCalls=0; goldReads=0; scoring=false; productionPrefilterActivated=false.");
        return builder.ToString();
    }

    private static Dictionary<string, V2ADecision> ReadV2ADecisions(JsonElement root)
    {
        var result = new Dictionary<string, V2ADecision>(StringComparer.Ordinal);
        if (!root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in decisions.EnumerateArray())
        {
            var sourceId = ReadString(item, "sourceId");
            if (sourceId is null) continue;
            var signals = item.TryGetProperty("signals", out var signalArray) && signalArray.ValueKind == JsonValueKind.Array
                ? signalArray.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
                : Array.Empty<string>();
            result[sourceId] = new V2ADecision(sourceId, ReadInt(item, "sourceOrdinal"), ReadBool(item, "eligibleForRoleModel"), ReadString(item, "decision") ?? "unknown", signals,
                ReadString(item, "structuralScope") ?? "unknown", ReadString(item, "domainRole") ?? "Unknown", ReadBool(item, "policyCandidate"));
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ReadV7Predictions(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("occurrencePredictions", out var values) || values.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in values.EnumerateArray())
        {
            var id = ReadString(item, "occurrenceId");
            if (id is not null) result[id] = item.Clone();
        }
        return result;
    }

    private static Dictionary<string, V7Trace> ReadV7Traces(JsonElement root)
    {
        var result = new Dictionary<string, V7Trace>(StringComparer.Ordinal);
        if (!root.TryGetProperty("sourceTraces", out var values) || values.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in values.EnumerateArray())
        {
            var id = ReadString(item, "sourceId");
            if (id is not null)
                result[id] = new V7Trace(id, ReadString(item, "modelRole"), ReadBoolNullable(item, "modelProposalPresent"), ReadString(item, "validationStatus"), ReadBoolNullable(item, "finalIncluded"));
        }
        return result;
    }

    private static object? ReadScalar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool? ReadBoolNullable(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static SourceEntry[] LoadInventory(string repoRoot)
    {
        var path = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new SourceEntry(ReadString(item, "documentId")!, ReadString(item, "sourcePath") ?? "", ReadString(item, "sourceSha256") ?? ""))
            .ToArray();
    }

    private static async Task<int> BlockAsync(string repoRoot, string head, string reason, CancellationToken ct)
    {
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "blocked.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-v2a1-role-prefilter-forensic-blocked-v1",
            status = "V2A1_FORENSIC_AUDIT_BLOCKED",
            baselineCheckpoint = BaselineCheckpoint,
            currentHead = head,
            reason,
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
        }, ct);
        Console.Error.WriteLine($"V2A1_STATUS=BLOCKED:{reason}");
        return 2;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private static string Relative(string repoRoot, string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private static string GitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        });
        return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN";
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record SourceEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record V2ADecision(string SourceId, int SourceOrdinal, bool Eligible, string Decision, IReadOnlyList<string> Signals, string StructuralScope, string DomainRole, bool PolicyCandidate);
    private sealed record V7Trace(string SourceId, string? ModelRole, bool? ModelProposalPresent, string? ValidationStatus, bool? FinalIncluded);
    private sealed record ForensicRow(
        string SourceId, string SourceText, int SourceOrdinal, string ParagraphRole, double Score, bool IsCandidate,
        object StyleFacts, object NumberingFacts, object? MarkerFacts, object Scope, object DomainEvidence,
        object Neighbors, string SkipReason, IReadOnlyList<string> SkipSignals, string PrefilterScope, string PrefilterDomainRole,
        bool PolicyCandidate, string? V7PredictedRole, object? V7Confidence, string? V7ModelRole, bool? V7ModelProposalPresent,
        string? V7ValidationStatus, bool? V7FinalIncluded, string V7ConfidenceSource, string EvidenceSignature);
    private sealed record UncertainRow(string SourceId, int SourceOrdinal, string SourceText, string ParagraphRole, double Score, bool IsCandidate, string SkipReason, IReadOnlyList<string> SkipSignals, string EvidenceSignature, string? V7ModelRole);
}
