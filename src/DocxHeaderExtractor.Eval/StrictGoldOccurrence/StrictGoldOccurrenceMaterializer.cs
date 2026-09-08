using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.StrictGoldOccurrence;

/// <summary>
/// Evaluation-only binding of frozen semantic Gold to parser-owned source coordinates.
/// Production predictions, model output, and current extraction output are deliberately absent
/// from this API.
/// </summary>
public static class StrictGoldOccurrenceMaterializer
{
    private sealed record DocumentSpec(string Id, string SourcePath, string ReferencePath);
    private sealed record GoldHeading(string SourceId, string ApprovedText, string Role, int? Level, string? Parent, StrictGoldOccurrenceSpan? ExistingSpan);
    private sealed record HistoricalEntry(string SourceId, string Text, int Level, int LineNumber);
    private sealed record TextMatch(StrictGoldOccurrenceSpan Span, string Method, string ComparisonRule);

    private static readonly DocumentSpec[] Specs =
    [
        new("DOC-0001", "bench/01-style-chuan.docx", "bench/01-style-chuan.key"),
        new("DOC-0205", "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx", "keys/legal-human/025_ND_47-2020_Chia_se_du_lieu_so.key"),
        new("DOC-0252", "todo10_8/heading_corpus_95_word/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.docx", "keys/format-driven-human/072_ICP_TAG_Minutes_Mar_2025.key"),
        new("DOC-0256", "todo10_8/heading_corpus_95_word/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.docx", "keys/rebased/m712/076_ICP_IACG08_Minutes_2023.key"),
        new("DOC-0258", "todo10_8/heading_corpus_95_word/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx", "keys/tagged-pdf-coverage/078_ICP_IACG07_Minutes_May_2023.key"),
        new("DOC-0264", "todo10_8/heading_corpus_95_word/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.docx", "eval/harness-lift/review-packets-v2/DOC-0264.v2.json"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex KeyLine = new(
        @"^\s*@?(?<source>\S+)\s+(?<level>\d+)\s+#\s*(?<text>.*)$",
        RegexOptions.Compiled);


    public static IReadOnlyList<string> DocumentIds => Specs.Select(x => x.Id).ToArray();

    /// <summary>Evaluation corpus mapping shared by the occurrence and retention runners.</summary>
    public static IReadOnlyDictionary<string, string> SourcePaths =>
        Specs.ToDictionary(item => item.Id, item => item.SourcePath, StringComparer.Ordinal);

    public static StrictGoldOccurrenceMaterializationReport MaterializeAll(string repoRoot, out IReadOnlyList<StrictGoldOccurrenceArtifact> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var built = Specs.Select(spec => MaterializeDocument(repoRoot, spec)).ToArray();
        artifacts = built;
        var summaries = built.Select(Summarize).ToArray();
        var expected = summaries.Sum(x => x.Expected);
        var materialized = summaries.Sum(x => x.Materialized);
        return new StrictGoldOccurrenceMaterializationReport
        {
            Status = materialized == expected && summaries.All(x => x.Status == "PASS") ? "PASS" : "BLOCKED",
            ExpectedDocuments = Specs.Length,
            MaterializedDocuments = summaries.Count(x => x.Status == "PASS"),
            ExpectedOccurrences = expected,
            MaterializedOccurrences = materialized,
            PerDocument = summaries,
        };
    }

    public static void WriteAll(string repoRoot, string? outputRoot = null)
    {
        var report = MaterializeAll(repoRoot, out var artifacts);
        var root = outputRoot ?? Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1");
        Directory.CreateDirectory(root);
        foreach (var artifact in artifacts)
        {
            var path = Path.Combine(root, $"{artifact.DocumentId}.occurrence-gold-v1.json");
            File.WriteAllText(path, JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        }

        var artifactRoot = Path.GetDirectoryName(root)!;
        File.WriteAllText(
            Path.Combine(artifactRoot, "strict-gold-occurrence-materialization.v1.json"),
            JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(artifactRoot, "strict-gold-capability-matrix.v5.json"),
            JsonSerializer.Serialize(BuildCapabilityMatrix(repoRoot, report), JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));

        Console.WriteLine($"strict gold occurrence materialization: {report.MaterializedOccurrences}/{report.ExpectedOccurrences}; status={report.Status}");
        foreach (var summary in report.PerDocument)
            Console.WriteLine($"{summary.DocumentId}: {summary.Materialized}/{summary.Expected} status={summary.Status} ambiguous={summary.Ambiguous} missing={summary.Missing}");
    }

    /// <summary>Shared deterministic matcher used by the materializer and its focused tests.</summary>
    public static bool TryBindExactSubstring(
        string rawText,
        string reviewedText,
        ISet<StrictGoldOccurrenceSpan>? usedSpans,
        int minimumStart,
        out StrictGoldOccurrenceSpan span,
        out string method,
        out string comparisonRule)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(reviewedText);
        usedSpans ??= new HashSet<StrictGoldOccurrenceSpan>();
        var exact = FindExact(rawText, reviewedText, usedSpans, minimumStart);
        var normalized = FindNormalized(rawText, reviewedText, usedSpans, minimumStart);
        // Prefer the earliest reversible source match. An ASCII-normalized historical key can
        // otherwise bind a later literal-hyphen body mention ahead of an earlier en-dash heading.
        if (exact is not null && (normalized is null || exact.Start <= normalized.Start))
        {
            span = exact;
            method = "exact-raw-text-substring";
            comparisonRule = "ordinal";
            return true;
        }

        if (normalized is not null)
        {
            span = normalized;
            method = "reversible-normalized-text-map";
            comparisonRule = "NFC; smart-quote-to-ASCII; NBSP-to-space; whitespace-run-collapse; reversible-offset-map";
            return true;
        }

        span = new StrictGoldOccurrenceSpan(0, 0);
        method = "unbound";
        comparisonRule = "none";
        return false;
    }

    /// <summary>
    /// Binds only when the reviewed text has exactly one source-backed occurrence. Callers that
    /// have an approved ordering or source-location discriminator should use
    /// <see cref="TryBindExactSubstring"/> with its consumed-span set instead.
    /// </summary>
    public static bool TryBindUniqueExactSubstring(
        string rawText,
        string reviewedText,
        out StrictGoldOccurrenceSpan span,
        out string method,
        out string comparisonRule)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(reviewedText);

        var exactMatches = FindExactMatches(rawText, reviewedText);
        if (exactMatches.Count == 1)
        {
            span = exactMatches[0];
            method = "exact-raw-text-substring";
            comparisonRule = "ordinal;unique-source-match";
            return true;
        }

        var normalizedMatches = FindNormalizedMatches(rawText, reviewedText);
        if (normalizedMatches.Count == 1)
        {
            span = normalizedMatches[0];
            method = "reversible-normalized-text-map";
            comparisonRule = "NFC;smart-quote-to-ASCII;NBSP-to-space;whitespace-run-collapse;reversible-offset-map;unique-source-match";
            return true;
        }

        span = new StrictGoldOccurrenceSpan(0, 0);
        method = "ambiguous-or-unbound";
        comparisonRule = "none";
        return false;
    }

    private static StrictGoldOccurrenceArtifact MaterializeDocument(string repoRoot, DocumentSpec spec)
    {
        var v4Path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-v4", $"{spec.Id}.strict-gold-v4.json");
        var sourcePath = Path.Combine(repoRoot, spec.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var referencePath = Path.Combine(repoRoot, spec.ReferencePath.Replace('/', Path.DirectorySeparatorChar));
        var errors = new List<string>();

        if (!File.Exists(v4Path)) return Blocked(spec, v4Path, sourcePath, "strict-gold-v4-missing");
        if (!File.Exists(sourcePath)) return Blocked(spec, v4Path, sourcePath, "source-document-missing");
        if (!File.Exists(referencePath)) return Blocked(spec, v4Path, sourcePath, "source-reference-missing");

        using var v4 = JsonDocument.Parse(File.ReadAllText(v4Path));
        var v4Root = v4.RootElement;
        var expected = v4Root.GetProperty("semanticHeadingTotal").GetInt32();
        var documentGroupId = v4Root.GetProperty("documentGroupId").GetString() ?? "";
        var expectedSourceSha = v4Root.GetProperty("sourceSha256").GetString() ?? "";
        var sourceSha = Sha256File(sourcePath);
        var v4Sha = Sha256File(v4Path);
        if (!string.Equals(sourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            errors.Add($"source-sha-mismatch:expected={expectedSourceSha}:actual={sourceSha}");

        SourceDocument source;
        try
        {
            source = new OpenXmlDocumentSource().Read(sourcePath);
        }
        catch (Exception ex)
        {
            errors.Add($"source-read-failed:{ex.GetType().Name}");
            return BuildArtifact(spec, documentGroupId, sourceSha, v4Path, v4Sha, expected, [], errors);
        }

        IReadOnlyList<StrictGoldOccurrenceBinding> bindings;
        if (spec.Id == "DOC-0264")
        {
            // The live canonical Gold records a semantic total only. Its reviewed heading list
            // is not materialized, so this document is intentionally non-evaluable for exact
            // occurrence/span scoring. Never manufacture rows from legal-marker heuristics.
            bindings = [];
            errors.Add("exact-approved-heading-list-not-materialized");
        }
        else
            bindings = MaterializeKeyBackedDocument(spec, source, v4Root, referencePath, Sha256File(referencePath), v4Path, v4Sha, repoRoot, errors);

        if (bindings.Count != expected)
            errors.Add($"materialized-count-mismatch:expected={expected}:actual={bindings.Count}");
        var distinctIds = bindings.Select(x => x.HeadingOccurrenceId).Distinct(StringComparer.Ordinal).Count();
        if (distinctIds != bindings.Count) errors.Add("duplicate-heading-occurrence-id");
        if (errors.Count > 0)
            return BuildArtifact(spec, documentGroupId, sourceSha, v4Path, v4Sha, expected, bindings, errors);

        return BuildArtifact(spec, documentGroupId, sourceSha, v4Path, v4Sha, expected, bindings, []);
    }

    private static IReadOnlyList<StrictGoldOccurrenceBinding> MaterializeKeyBackedDocument(
        DocumentSpec spec,
        SourceDocument source,
        JsonElement v4Root,
        string referencePath,
        string referenceSha,
        string v4Path,
        string v4Sha,
        string repoRoot,
        ICollection<string> errors)
    {
        var references = ParseKey(referencePath);
        var rows = v4Root.GetProperty("headings").EnumerateArray().Select(ParseHeading).ToArray();
        var bindings = new List<StrictGoldOccurrenceBinding>();
        var usedReferences = new HashSet<int>();
        var usedSpans = new Dictionary<string, HashSet<StrictGoldOccurrenceSpan>>(StringComparer.Ordinal);
        var cursors = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (row, ordinal) in rows.Select((value, index) => (value, index)))
        {
            var sourceHint = ResolveSource(source, row.SourceId);
            var reference = FindReference(references, row, sourceHint, usedReferences);
            SourceParagraph? sourceParagraph = null;
            TextMatch? match = null;

            // Historical source IDs are useful hints, but regenerated DOCX files can move a
            // reviewed occurrence to another paragraph. Try the hinted paragraph first, then
            // the historical hint, then source-backed text matches in parser order. A failed
            // binding on one candidate must not prevent an exact binding on the next one.
            foreach (var candidateSource in ResolveSourceCandidates(source, row, reference, sourceHint))
            {
                var candidateSpans = usedSpans.GetValueOrDefault(candidateSource.SourceId);
                var minimumStart = cursors.GetValueOrDefault(candidateSource.SourceId);
                var candidateMatch = BindHeading(candidateSource, row, reference, candidateSpans, minimumStart);
                if (candidateMatch is null) continue;

                sourceParagraph = candidateSource;
                match = candidateMatch;
                break;
            }

            if (sourceParagraph is null)
            {
                errors.Add($"missing-source:{ordinal}:{row.SourceId}:{row.ApprovedText}");
                continue;
            }
            if (!usedSpans.TryGetValue(sourceParagraph.SourceId, out var spans))
            {
                spans = new HashSet<StrictGoldOccurrenceSpan>();
                usedSpans[sourceParagraph.SourceId] = spans;
            }
            if (match is null)
            {
                errors.Add($"missing-raw-substring:{ordinal}:{sourceParagraph.SourceId}:{reference?.Text ?? row.ApprovedText}");
                continue;
            }

            if (reference is not null) usedReferences.Add(reference.LineNumber);

            spans.Add(match.Span);
            cursors[sourceParagraph.SourceId] = match.Span.End;
            var sourceReference = sourceParagraph.SourceId;
            bindings.Add(new StrictGoldOccurrenceBinding
            {
                HeadingOrdinal = ordinal,
                HeadingOccurrenceId = OccurrenceId(spec.Id, sourceReference, match.Span),
                SourceId = sourceReference,
                HeadingSpan = match.Span,
                RawSourceText = sourceParagraph.Text.Substring(match.Span.Start, match.Span.End - match.Span.Start),
                ApprovedHeadingText = row.ApprovedText,
                SemanticRole = row.Role,
                Level = row.Level ?? reference?.Level,
                ParentHeadingOccurrenceId = null,
                BindingMethod = match.Method + (reference is null ? ";committed-v4-span" : ";historical-key-bridge"),
                ComparisonRule = match.ComparisonRule,
                SourceReferencePath = reference is null
                    ? RelativePath(repoRoot, v4Path) + "#heading-span"
                    : RelativePath(repoRoot, referencePath, sourceParagraph.SourceId, reference!.LineNumber),
                SourceReferenceSha256 = reference is null ? v4Sha : referenceSha,
                ExactRawSubstringVerified = true,
            });
        }
        return bindings;
    }

    private static TextMatch? BindHeading(
        SourceParagraph sourceParagraph,
        GoldHeading row,
        HistoricalEntry? reference,
        ISet<StrictGoldOccurrenceSpan>? usedSpans,
        int minimumStart)
    {
        if (reference is null)
        {
            if (row.ExistingSpan is { } existingSpan && existingSpan.IsValidFor(sourceParagraph.Text) &&
                TextEquivalent(sourceParagraph.Text.Substring(existingSpan.Start, existingSpan.End - existingSpan.Start), row.ApprovedText))
                return new TextMatch(existingSpan, "committed-v4-source-span", "ordinal");

            return null;
        }

        var reviewedCandidates = new[] { reference.Text, row.ApprovedText };
        foreach (var candidate in reviewedCandidates.Distinct(StringComparer.Ordinal))
        {
            if (TryBindExactSubstring(sourceParagraph.Text, candidate, usedSpans, minimumStart, out var span, out var method, out var rule))
                return new TextMatch(span, method, rule);
        }

        return null;
    }

    private static StrictGoldOccurrenceArtifact BuildArtifact(
        DocumentSpec spec,
        string groupId,
        string sourceSha,
        string v4Path,
        string v4Sha,
        int expected,
        IReadOnlyList<StrictGoldOccurrenceBinding> bindings,
        IReadOnlyList<string> errors)
    {
        var passed = errors.Count == 0 && bindings.Count == expected;
        var parent = bindings.Count > 0 && bindings.All(x => x.ParentHeadingOccurrenceId is not null);
        return new StrictGoldOccurrenceArtifact
        {
            Status = passed ? "PASS" : "BLOCKED",
            DocumentId = spec.Id,
            DocumentGroupId = groupId,
            SourceSha256 = sourceSha,
            StrictGoldArtifactPath = RelativePath(v4Path),
            StrictGoldArtifactSha256 = v4Sha,
            SemanticHeadingTotal = expected,
            MaterializedOccurrenceCount = bindings.Count,
            Bindings = bindings,
            Discrepancies = errors,
            ExactApprovedHeadingListMaterialized = passed,
            Capabilities = new StrictGoldOccurrenceCapabilities
            {
                OccurrenceEvaluable = passed,
                CharacterSpanEvaluable = passed,
                RoleEvaluable = passed,
                LevelEvaluable = passed,
                ParentEvaluable = parent,
                HierarchyEvaluable = parent,
            },
        };
    }

    private static StrictGoldOccurrenceArtifact Blocked(DocumentSpec spec, string v4Path, string sourcePath, string error) =>
        BuildArtifact(spec, "", File.Exists(sourcePath) ? Sha256File(sourcePath) : "", v4Path,
            File.Exists(v4Path) ? Sha256File(v4Path) : "", 0, [], [error]);

    private static StrictGoldOccurrenceDocumentSummary Summarize(StrictGoldOccurrenceArtifact artifact) => new()
    {
        DocumentId = artifact.DocumentId,
        Expected = artifact.SemanticHeadingTotal,
        Materialized = artifact.MaterializedOccurrenceCount,
        Ambiguous = artifact.Discrepancies.Count(x => x.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)),
        Missing = artifact.Discrepancies.Count(x => x.Contains("missing", StringComparison.OrdinalIgnoreCase)),
        OccurrenceEvaluable = artifact.Capabilities.OccurrenceEvaluable,
        CharacterSpanEvaluable = artifact.Capabilities.CharacterSpanEvaluable,
        ParentEvaluable = artifact.Capabilities.ParentEvaluable,
        HierarchyEvaluable = artifact.Capabilities.HierarchyEvaluable,
        Status = artifact.Status,
        Discrepancies = artifact.Discrepancies,
    };

    private static object BuildCapabilityMatrix(string repoRoot, StrictGoldOccurrenceMaterializationReport report)
    {
        var exhaustive = report.PerDocument.Count;
        var occurrence = report.PerDocument.Count(x => x.OccurrenceEvaluable);
        var spans = report.PerDocument.Count(x => x.CharacterSpanEvaluable);
        return new
        {
            artifactKind = "a99_strict_gold_capability_matrix",
            schemaVersion = "a99-strict-gold-capability-matrix-v5",
            policyAuthority = "USER_PROMOTED_STRICT_GOLD_V4",
            capabilityRevision = "V5_EVALUATION_CAPABILITY_ONLY",
            activeStrictGoldDocuments = exhaustive,
            activeStrictGoldTotal = exhaustive,
            exhaustiveSemanticDocuments = exhaustive,
            occurrenceEvaluableExhaustive = $"{occurrence}/{exhaustive}",
            characterSpanEvaluableExhaustive = $"{spans}/{exhaustive}",
            expectedGoldOccurrences = report.ExpectedOccurrences,
            materializedGoldOccurrences = report.MaterializedOccurrences,
            baselineReady = report.Status == "PASS",
            providerCalls = 0,
            holdoutTouched = false,
            documents = report.PerDocument,
            source = "strict-gold-occurrence-materialization.v1.json",
            generatedAtRevision = "EVALUATION_RUN",
        };
    }

    private static GoldHeading ParseHeading(JsonElement element)
    {
        StrictGoldOccurrenceSpan? span = null;
        if (element.TryGetProperty("headingSpan", out var spanElement) &&
            spanElement.TryGetProperty("start", out var start) && start.TryGetInt32(out var s) &&
            spanElement.TryGetProperty("end", out var end) && end.TryGetInt32(out var e))
            span = new StrictGoldOccurrenceSpan(s, e);
        return new GoldHeading(
            element.GetProperty("sourceId").GetString() ?? "",
            element.GetProperty("exactText").GetString() ?? "",
            element.GetProperty("role").GetString() ?? "heading",
            element.TryGetProperty("level", out var level) && level.TryGetInt32(out var l) ? l : null,
            element.TryGetProperty("parentHeadingOccurrenceId", out var parent) ? parent.GetString() : null,
            span);
    }

    private static IReadOnlyList<HistoricalEntry> ParseKey(string path) =>
        File.ReadLines(path).Select((line, index) => (LineText: line, LineNumber: index + 1))
            .Select(x => (Match: KeyLine.Match(x.LineText), Line: x.LineNumber))
            .Where(x => x.Match.Success)
            .Select(x => new HistoricalEntry(
                x.Match.Groups["source"].Value,
                x.Match.Groups["text"].Value.Trim(),
                int.Parse(x.Match.Groups["level"].Value),
                x.Line))
            .ToArray();

    private static HistoricalEntry? FindReference(
        IReadOnlyList<HistoricalEntry> references,
        GoldHeading row,
        SourceParagraph? paragraph,
        ISet<int> used)
    {
        return references.FirstOrDefault(reference =>
            !used.Contains(reference.LineNumber) &&
            (paragraph is null || SourceReferenceMatches(reference.SourceId, paragraph)) &&
            TextEquivalent(reference.Text, row.ApprovedText) &&
            (row.Level is null || row.Level == reference.Level))
            ?? references.FirstOrDefault(reference =>
                !used.Contains(reference.LineNumber) &&
                TextEquivalent(reference.Text, row.ApprovedText) &&
                (row.Level is null || row.Level == reference.Level));
    }

    private static bool SourceReferenceMatches(string reference, SourceParagraph paragraph)
    {
        if (string.Equals(reference, paragraph.SourceId, StringComparison.Ordinal)) return true;
        if (int.TryParse(reference, out var ordinal)) return ordinal == paragraph.SourceOrdinal;
        var match = Regex.Match(reference, @"paragraph\[(?<ordinal>\d+)\]", RegexOptions.IgnoreCase);
        return match.Success && int.Parse(match.Groups["ordinal"].Value) == paragraph.SourceOrdinal;
    }

    private static SourceParagraph? ResolveSource(SourceDocument source, string id)
    {
        var direct = source.Paragraphs.FirstOrDefault(x => string.Equals(x.SourceId, id, StringComparison.Ordinal));
        if (direct is not null) return direct;
        if (int.TryParse(id, out var ordinal)) return source.Paragraphs.FirstOrDefault(x => x.SourceOrdinal == ordinal);
        var match = Regex.Match(id, @"paragraph\[(?<ordinal>\d+)\]", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["ordinal"].Value, out var parsed)
            ? source.Paragraphs.FirstOrDefault(x => x.SourceOrdinal == parsed)
            : null;
    }

    private static IReadOnlyList<SourceParagraph> ResolveSourceCandidates(
        SourceDocument source,
        GoldHeading row,
        HistoricalEntry? reference,
        SourceParagraph? sourceHint)
    {
        var reviewed = reference?.Text ?? row.ApprovedText;
        var referenceHint = reference is null
            ? null
            : source.Paragraphs.FirstOrDefault(x => SourceReferenceMatches(reference.SourceId, x));

        var candidates = new List<SourceParagraph>();
        Add(sourceHint);
        Add(referenceHint);

        // PDF-converted DOCX keys describe the historical packet occurrence, not necessarily the
        // regenerated DOCX paragraph ID. Text is still an exact reviewed bridge, so search source
        // facts deterministically and let the per-source cursor disambiguate repeats later.
        foreach (var paragraph in source.Paragraphs.Where(x => HasBindableText(x.Text, reviewed, null)))
            Add(paragraph);

        return candidates;

        void Add(SourceParagraph? paragraph)
        {
            if (paragraph is not null && candidates.All(x => !string.Equals(x.SourceId, paragraph.SourceId, StringComparison.Ordinal)))
                candidates.Add(paragraph);
        }
    }

    private static bool HasBindableText(string rawText, string reviewedText, StrictGoldOccurrenceSpan? existingSpan)
    {
        if (existingSpan is { } span && span.IsValidFor(rawText) &&
            TextEquivalent(rawText.Substring(span.Start, span.End - span.Start), reviewedText)) return true;
        return TryBindExactSubstring(rawText, reviewedText, null, 0, out _, out _, out _);
    }

    private static bool TextEquivalent(string left, string right) =>
        string.Equals(NormalizeReference(left), NormalizeReference(right), StringComparison.Ordinal) ||
        string.Equals(NormalizeReference(RepairMojibake(left)), NormalizeReference(right), StringComparison.Ordinal) ||
        string.Equals(NormalizeReference(left), NormalizeReference(RepairMojibake(right)), StringComparison.Ordinal);

    private static string NormalizeReference(string value) =>
        NormalizeForBinding(value).Text;

    private static string RepairMojibake(string value)
    {
        var current = value;
        for (var pass = 0; pass < 2 && SuspiciousCount(current) > 0; pass++)
        {
            try
            {
                // Historical JSON was sometimes decoded through Windows-1252, so characters such
                // as U+2018/U+2039 represent bytes 0x91/0x8B rather than Unicode punctuation.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var repaired = Encoding.UTF8.GetString(Encoding.GetEncoding(1252).GetBytes(current));
                if (SuspiciousCount(repaired) >= SuspiciousCount(current)) break;
                current = repaired;
            }
            catch (ArgumentException)
            {
                break;
            }
        }

        return current;
    }

    private static int SuspiciousCount(string value) =>
        value.Count(c => c is 'Ã' or 'Â' or 'Æ' or 'Ä' or 'Ð' or '�' or 'â') +
        CountOccurrences(value, "á»");

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        for (var index = value.IndexOf(token, StringComparison.Ordinal); index >= 0;)
        {
            count++;
            index = value.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static StrictGoldOccurrenceSpan? FindExact(string raw, string reviewed, ISet<StrictGoldOccurrenceSpan> used, int minimumStart)
    {
        var cursor = Math.Max(0, minimumStart);
        while (cursor <= raw.Length - reviewed.Length)
        {
            var index = raw.IndexOf(reviewed, cursor, StringComparison.Ordinal);
            if (index < 0) return null;
            var span = new StrictGoldOccurrenceSpan(index, index + reviewed.Length);
            if (!used.Any(existing => Overlaps(existing, span))) return span;
            cursor = index + 1;
        }
        return null;
    }

    private static IReadOnlyList<StrictGoldOccurrenceSpan> FindExactMatches(string raw, string reviewed)
    {
        var matches = new List<StrictGoldOccurrenceSpan>();
        var cursor = 0;
        while (cursor <= raw.Length - reviewed.Length)
        {
            var index = raw.IndexOf(reviewed, cursor, StringComparison.Ordinal);
            if (index < 0) break;
            matches.Add(new StrictGoldOccurrenceSpan(index, index + reviewed.Length));
            cursor = index + Math.Max(1, reviewed.Length);
        }
        return matches;
    }

    private static StrictGoldOccurrenceSpan? FindNormalized(string raw, string reviewed, ISet<StrictGoldOccurrenceSpan> used, int minimumStart)
    {
        var candidates = new[] { reviewed, RepairMojibake(reviewed) }.Distinct(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var normalizedRaw = NormalizeForBinding(raw);
            var normalizedCandidate = NormalizeForBinding(candidate).Text;
            var cursor = 0;
            while (cursor < normalizedRaw.Text.Length)
            {
                var index = normalizedRaw.Text.IndexOf(normalizedCandidate, cursor, StringComparison.Ordinal);
                if (index < 0) break;
                var start = normalizedRaw.OriginalIndices[index];
                var last = index + normalizedCandidate.Length - 1;
                var end = normalizedRaw.OriginalIndices[last] + 1;
                var span = new StrictGoldOccurrenceSpan(start, end);
                if (start >= minimumStart && !used.Any(existing => Overlaps(existing, span))) return span;
                cursor = index + 1;
            }
        }
        return null;
    }

    private static IReadOnlyList<StrictGoldOccurrenceSpan> FindNormalizedMatches(string raw, string reviewed)
    {
        var result = new HashSet<StrictGoldOccurrenceSpan>();
        foreach (var candidate in new[] { reviewed, RepairMojibake(reviewed) }.Distinct(StringComparer.Ordinal))
        {
            var normalizedRaw = NormalizeForBinding(raw);
            var normalizedCandidate = NormalizeForBinding(candidate).Text;
            if (normalizedCandidate.Length == 0) continue;
            var cursor = 0;
            while (cursor < normalizedRaw.Text.Length)
            {
                var index = normalizedRaw.Text.IndexOf(normalizedCandidate, cursor, StringComparison.Ordinal);
                if (index < 0) break;
                var last = index + normalizedCandidate.Length - 1;
                result.Add(new StrictGoldOccurrenceSpan(
                    normalizedRaw.OriginalIndices[index],
                    normalizedRaw.OriginalIndices[last] + 1));
                cursor = index + 1;
            }
        }
        return result.OrderBy(item => item.Start).ThenBy(item => item.End).ToArray();
    }

    private static (string Text, IReadOnlyList<int> OriginalIndices) NormalizeForBinding(string value)
    {
        var text = new StringBuilder();
        var indices = new List<int>();
        var pendingSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var normalizedChar = value[i].ToString().Normalize(NormalizationForm.FormC);
            var c = normalizedChar.Length == 0 ? value[i] : normalizedChar[0];
            c = c switch
            {
                '\u00A0' or '\u2007' or '\u202F' => ' ',
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                '\u2013' or '\u2014' => '-',
                _ => c,
            };
            if (char.IsWhiteSpace(c))
            {
                if (i > 0 && value[i - 1] == '.')
                {
                    pendingSpace = false;
                    continue;
                }
                if (text.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                text.Append(' ');
                indices.Add(i);
                pendingSpace = false;
            }
            text.Append(c);
            indices.Add(i);
        }
        return (text.ToString(), indices);
    }

    private static List<(int Index, string Text)> AllExact(string raw, string value)
    {
        var result = new List<(int, string)>();
        var cursor = 0;
        while (cursor < raw.Length)
        {
            var index = raw.IndexOf(value, cursor, StringComparison.Ordinal);
            if (index < 0) break;
            result.Add((index, value));
            cursor = index + value.Length;
        }
        return result;
    }

    private static bool Overlaps(StrictGoldOccurrenceSpan left, StrictGoldOccurrenceSpan right) =>
        left.Start < right.End && right.Start < left.End;

    private static string OccurrenceId(string documentId, string sourceId, StrictGoldOccurrenceSpan span) =>
        $"{documentId}:{sourceId}@{span.Start}:{span.End}";

    private static string RelativePath(string repoRoot, string path, string? sourceId = null, int? line = null)
    {
        var normalized = Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        return sourceId is null ? normalized : $"{normalized}#key-line={line}:{sourceId}";
    }

    private static string RelativePath(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        var marker = normalized.IndexOf("/eval/", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? normalized[(marker + 1)..] : normalized;
    }

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256FileOrEmpty(string path) => File.Exists(path) ? Sha256File(path) : "";
}
