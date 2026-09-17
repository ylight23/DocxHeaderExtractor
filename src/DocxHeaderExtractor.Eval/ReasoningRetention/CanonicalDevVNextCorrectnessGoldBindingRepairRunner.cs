using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Builds a successor binding artifact from raw DOCX coordinates. Existing Gold and prediction
/// artifacts are inputs only and are never overwritten or promoted by this runner.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldBindingRepairRunner
{
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string AuthorityRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0116";
    private const string OccurrenceRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1/gold-binding-repair-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var authorityPath = Full(repoRoot, AuthorityRoot + "/authority-freeze-manifest.json");
        var bindingsPath = Full(repoRoot, OccurrenceRoot + "/exact-bindings.json");
        var predictionPath = Full(repoRoot, ExecutionRoot + "/prediction-freeze.v1.json");
        var auditPath = Full(repoRoot, ExecutionRoot + "/gold-scoring-v1/compatibility-audit-v1/compatibility-audit.v1.json");
        if (new[] { authorityPath, bindingsPath, predictionPath, auditPath }.Any(path => !File.Exists(path)))
            return Blocked("REPAIR_INPUT_MISSING");

        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityPath, ct));
        using var bindings = JsonDocument.Parse(await File.ReadAllTextAsync(bindingsPath, ct));
        using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
        using var compatibilityAudit = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath, ct));

        var sourcePath = Full(repoRoot, authority.RootElement.GetProperty("sourcePath").GetString()!);
        if (!File.Exists(sourcePath)) return Blocked("SOURCE_DOCUMENT_MISSING");
        var rawById = ReadRawParagraphs(sourcePath).ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var rows = new List<RepairRow>();

        foreach (var item in bindings.RootElement.GetProperty("bindings").EnumerateArray())
        {
            var old = new OldBinding(
                item.GetProperty("occurrenceId").GetString()!,
                item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceSpan").GetProperty("start").GetInt32(),
                item.GetProperty("sourceSpan").GetProperty("end").GetInt32(),
                item.GetProperty("exactText").GetString()!,
                item.TryGetProperty("sourceContainerText", out var container) ? container.GetString() ?? "" : "");

            if (!rawById.TryGetValue(old.SourceId, out var source))
            {
                rows.Add(new RepairRow(old, null, "SOURCE_ID_NOT_FOUND", false, null, string.Empty));
                continue;
            }

            var oldSpanValid = IsSpanValid(source.Text, old.Start, old.End);
            var oldSpanText = oldSpanValid ? source.Text[old.Start..old.End] : null;
            if (oldSpanValid && string.Equals(oldSpanText, old.ExactText, StringComparison.Ordinal))
            {
                var changed = !string.Equals(old.SourceContainerText, source.Text, StringComparison.Ordinal);
                rows.Add(new RepairRow(
                    old,
                    new RepairedBinding(old.OccurrenceId, old.SourceId, old.Start, old.End, oldSpanText!, source.Text),
                    changed ? "EXISTING_SPAN_EXACT;REFRESH_SOURCE_CONTAINER_TEXT" : "EXISTING_SPAN_SOURCE_EXACT",
                    changed,
                    oldSpanText,
                    source.Text));
                continue;
            }

            var reviewedCandidates = new[] { old.SourceContainerText, old.ExactText }
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal);
            RepairedBinding? repaired = null;
            string? method = null;
            foreach (var candidate in reviewedCandidates)
            {
                if (TryBindWhitespaceElision(source.Text, candidate, out var whitespaceSpan))
                {
                    repaired = new RepairedBinding(old.OccurrenceId, old.SourceId, whitespaceSpan.Start, whitespaceSpan.End,
                        source.Text[whitespaceSpan.Start..whitespaceSpan.End], source.Text);
                    method = "source-whitespace-elision-map";
                    break;
                }

                if (!StrictGoldOccurrenceMaterializer.TryBindExactSubstring(source.Text, candidate, null, 0,
                        out var span, out var bindingMethod, out _)) continue;
                repaired = new RepairedBinding(old.OccurrenceId, old.SourceId, span.Start, span.End,
                    source.Text[span.Start..span.End], source.Text);
                method = bindingMethod;
                break;
            }

            rows.Add(new RepairRow(old, repaired, repaired is null ? "NO_SOURCE_FAITHFUL_REBIND" : method!,
                repaired is not null, oldSpanText, source.Text));
        }

        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var repairedCount = rows.Count(row => row.Repaired is not null);
        var changedCount = rows.Count(row => row.Changed);
        var unresolvedCount = rows.Count(row => row.Repaired is null);
        var reasonCounts = rows
            .GroupBy(row => row.Reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var changedOccurrenceIds = rows.Where(row => row.Changed).Select(row => row.Old.OccurrenceId).ToArray();
        var unresolvedOccurrenceIds = rows.Where(row => row.Repaired is null).Select(row => row.Old.OccurrenceId).ToArray();
        var payload = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-binding-repair-successor-v1",
            status = unresolvedCount == 0 ? "REPAIRED_SUCCESSOR_CANDIDATE_READY_FOR_REVIEW" : "REPAIR_BLOCKED_UNRESOLVED_BINDINGS",
            documentId = "DOC-0116",
            existingGoldImmutable = true,
            repairedSuccessorArtifact = true,
            predictionImmutable = true,
            predictionFreezeSha256 = Sha256File(predictionPath),
            existingGoldBindingsSha256 = Sha256File(bindingsPath),
            compatibilityAuditSha256 = Sha256File(auditPath),
            sourceSha256 = Sha256File(sourcePath),
            oldBindingCount = rows.Count,
            repairedBindingCount = repairedCount,
            changedBindingCount = changedCount,
            unresolvedBindingCount = unresolvedCount,
            reasonCounts,
            changedOccurrenceIds,
            unresolvedOccurrenceIds,
            repairPolicy = "Only source-faithful spans are accepted; the original binding is never overwritten."
                + " A whitespace-elision map is accepted only when the candidate accounts for the entire raw source paragraph after whitespace removal.",
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            rows,
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var jsonPath = Path.Combine(outputDir, "binding-repair-successor.v1.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine, ct);
        var reportPath = Path.Combine(outputDir, "report.md");
        var report = $"# DOC-0116 Gold binding repair successor\n\n" +
                     $"Status: `{payload.status}`\n\n" +
                     "This is a successor candidate only. The original 120-binding Gold artifact is immutable.\n\n" +
                     $"- Original bindings: **{rows.Count}**\n" +
                     $"- Source-faithful repaired rows: **{repairedCount}**\n" +
                     $"- Changed rows: **{changedCount}**\n" +
                     $"- Unresolved rows: **{unresolvedCount}**\n" +
                     "- Prediction changes: **0**\n" +
                     "- Provider calls: **0**\n\n" +
                     "Reason counts:\n" +
                     string.Join("\n", reasonCounts.OrderBy(item => item.Key).Select(item => $"- `{item.Key}`: **{item.Value}**")) +
                     "\n\n" +
                     (unresolvedCount == 0
                         ? "All rows have a raw-source-faithful successor binding.\n"
                         : "Unresolved occurrence IDs:\n" + string.Join("\n", unresolvedOccurrenceIds.Select(id => $"- `{id}`")) + "\n") +
                     "\nEach row retains the old binding and records the raw-source-faithful successor plus its repair reason. No official scoring was run.\n";
        await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, ct);
        var manifest = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-binding-repair-successor-manifest-v1",
            status = payload.status,
            documentId = "DOC-0116",
            successorSha256 = Sha256File(jsonPath),
            reportSha256 = Sha256File(reportPath),
            originalGoldSha256 = Sha256File(bindingsPath),
            predictionFreezeSha256 = Sha256File(predictionPath),
            compatibilityAuditSha256 = Sha256File(auditPath),
            providerCalls = 0,
            goldReads = 1,
            modelCalls = 0,
            authorityPromotion = "FORBIDDEN_UNTIL_REVIEW_AND_COMPATIBILITY_GATES_PASS",
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, ct);

        Console.WriteLine($"STATUS={payload.status}");
        Console.WriteLine($"ORIGINAL_BINDINGS={rows.Count}");
        Console.WriteLine($"REPAIRED_BINDINGS={repairedCount}");
        Console.WriteLine($"CHANGED_BINDINGS={changedCount}");
        Console.WriteLine($"UNRESOLVED_BINDINGS={unresolvedCount}");
        Console.WriteLine("PREDICTION_MUTATION=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return unresolvedCount == 0 ? 0 : 2;
    }

    private static RawParagraph[] ReadRawParagraphs(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidOperationException("DOCX body missing");
        var rows = new List<RawParagraph>();
        foreach (var walked in ParagraphWalker.Enumerate(body, new ExtractionOptions { IncludeTables = true }))
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
                        case Text value when !value.Ancestors<DeletedRun>().Any(): text.Append(value.Text); break;
                        case TabChar: text.Append('\t'); break;
                        case Break: text.Append(' '); break;
                        case NoBreakHyphen: text.Append('-'); break;
                    }
                }
            }
            rows.Add(new RawParagraph(walked.StableId, text.ToString()));
        }
        return rows.ToArray();
    }

    private static bool IsSpanValid(string text, int start, int end) => start >= 0 && end >= start && end <= text.Length;

    /// <summary>
    /// Repairs a source-faithful binding when the reviewed artifact omitted whitespace
    /// (for example a DOCX tab between a heading label and a page number). This is deliberately
    /// conservative: the candidate must account for the entire source paragraph after removing
    /// whitespace, so it cannot turn a partial fuzzy match into authority.
    /// </summary>
    private static bool TryBindWhitespaceElision(string raw, string candidate, out StrictGoldOccurrenceSpan span)
    {
        var rawWithoutWhitespace = RemoveWhitespace(raw);
        var candidateWithoutWhitespace = RemoveWhitespace(candidate);
        if (rawWithoutWhitespace.Length == 0 ||
            !string.Equals(rawWithoutWhitespace, candidateWithoutWhitespace, StringComparison.Ordinal) ||
            rawWithoutWhitespace.Length == raw.Length)
        {
            span = new StrictGoldOccurrenceSpan(0, 0);
            return false;
        }

        span = new StrictGoldOccurrenceSpan(0, raw.Length);
        return true;
    }

    private static string RemoveWhitespace(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 2;
    }

    private sealed record RawParagraph(string SourceId, string Text);
    private sealed record OldBinding(string OccurrenceId, string SourceId, int Start, int End, string ExactText, string SourceContainerText);
    private sealed record RepairedBinding(string OccurrenceId, string SourceId, int Start, int End, string ExactText, string SourceContainerText);
    private sealed record RepairRow(OldBinding Old, RepairedBinding? Repaired, string Reason, bool Changed, string? OldSpanText, string SourceText);
}
