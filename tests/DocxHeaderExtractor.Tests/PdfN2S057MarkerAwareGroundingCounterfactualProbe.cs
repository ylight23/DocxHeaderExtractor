using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Of the 14 `GROUNDING_VALIDATOR_REJECTION` targets from
/// <see cref="PdfN2S057RepresentationAuditProbe"/>, 10 fail `LooksGroundableText`'s
/// <c>&gt;= 2 periods</c> check purely because their own structural marker is three-level decimal
/// (<c>10.0.1</c>, <c>24.1.1</c>, ...) - the marker's own punctuation, not prose or window noise, trips
/// the heuristic. This tests the narrow candidate fix directly: using the project's existing marker
/// authority (<see cref="SourceFactsBuilder.ParseMarkerText"/> - never a new regex invented in the
/// grounder) to strip the recognized marker before counting punctuation, and only that.
/// <para>
/// Because <see cref="PdfBlockGrounder.Ground"/> only reaches the text-shape check for a candidate the
/// analyst already called <c>HeadingTopic</c> at confidence &gt;= 0.65, the population this change can
/// affect is exactly "already-heading-classified candidates with a multi-level marker" - never a
/// body-classified candidate. The negative control below measures that population's size across both
/// canonical checkpoints available (003, 057) as a bounded scope check, not a corpus-wide guarantee.
/// </para>
/// </summary>
public sealed class PdfN2S057MarkerAwareGroundingCounterfactualProbe
{
    [Fact]
    public void WriteCounterfactual()
    {
        var output = Environment.GetEnvironmentVariable("N2S_MARKER_AWARE_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedCounterfactualReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-marker-aware-grounding.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        using var audit = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-representation-audit.v1.json")));
        var markerDepthTargets = audit.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("Owner").GetString() == "GROUNDING_VALIDATOR_REJECTION" && !r.GetProperty("requiredOnlyGroundable").GetBoolean())
            .Select(r => (StableId: r.GetProperty("StableId").GetString()!, RequiredOnlyText: r.GetProperty("requiredOnlyText").GetString()!))
            .ToArray();

        var rows = markerDepthTargets.Select(t =>
        {
            var marker = ParseMarkerTextVerbatimCopy(t.RequiredOnlyText);
            var titleOnly = marker is null ? t.RequiredOnlyText : t.RequiredOnlyText[marker.Value.Raw.Length..].TrimStart();
            return new
            {
                t.StableId,
                requiredOnlyText = t.RequiredOnlyText,
                markerRecognized = marker is not null,
                markerRaw = marker?.Raw,
                markerKind = marker?.Kind,
                markerDepth = marker?.Depth,
                titleOnlyText = titleOnly,
                titleOnlyGroundable = LooksGroundableTextExcludingMarker(titleOnly),
            };
        }).ToArray();

        // Negative-control scope: candidates the analyst already called HeadingTopic (confidence
        // >= 0.65, since that is the only population this text-shape check ever reaches) whose
        // required text carries a >=3-level decimal marker, across both canonical checkpoints. This is
        // not a corpus-wide guarantee - it is the bounded evidence actually available offline.
        var negativeControl = new[] { "003", "057" }.Select(stem =>
        {
            var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", $"{stem}-n2-s.jsonl");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var roleBlocks = File.ReadLines(checkpointPath)
                .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
                .Where(e => e is not null).Cast<CheckpointEntry>()
                .Where(e => e.Lane == "semantic")
                .SelectMany(e => e.Payload.Blocks)
                .ToArray();

            var docx = stem == "003"
                ? Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "003_Luat_Doanh_nghiep_59-2020-QH14.docx")
                : Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh", "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
            var textById = snapshot.CandidateBlocks.ToDictionary(b => b.Id, b => b.Text, StringComparer.Ordinal);

            var headingClassified = roleBlocks.Where(b => string.Equals(b.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase) && b.Confidence >= 0.65);
            var withMultiLevelMarker = headingClassified
                .Where(b => textById.TryGetValue(b.Id, out var text) && (ParseMarkerTextVerbatimCopy(text)?.Depth ?? 0) >= 3)
                .Select(b => b.Id)
                .ToArray();

            return new
            {
                stem,
                headingClassifiedCandidates = headingClassified.Count(),
                withThreeLevelOrDeeperMarker = withMultiLevelMarker.Length,
                candidateIds = withMultiLevelMarker,
            };
        }).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_057_marker_aware_grounding_counterfactual",
            usesModel = false,
            fix = "Strip a marker recognized by SourceFactsBuilder.ParseMarkerText (the project's existing marker authority) before LooksGroundableText's punctuation count, rather than counting the marker's own punctuation as prose evidence.",
            targets = rows,
            wouldNowPass = rows.Count(r => r.titleOnlyGroundable),
            outOf = rows.Length,
            negativeControlScope = negativeControl,
            negativeControlNote = "This counts, it does not adjudicate. Every listed candidate ID is already HeadingTopic-classified by the analyst; whether letting its multi-level-marker text now pass the shape gate is correct or a new false positive requires the same silver/gold check as the FP audit - not attempted here.",
        };
    }

    /// <summary>
    /// Verbatim copy of SourceFactsBuilder.ParseMarkerText's decimal branch - not accessible from
    /// here (private). Only the decimal/dotted-decimal case is reproduced, since that is the only
    /// marker family involved in the 10 marker-depth targets.
    /// </summary>
    private static readonly Regex DecimalMarker = new(
        @"^\s*(?<value>\d{1,3}(?:\.\d{1,3}){0,4})\s*[\.)](?=\s*\S)", RegexOptions.Compiled);

    private static (string Raw, string Kind, int Depth)? ParseMarkerTextVerbatimCopy(string text)
    {
        var m = DecimalMarker.Match(text);
        if (!m.Success) return null;
        var value = m.Groups["value"].Value;
        var components = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return (m.Value.Trim(), components.Length == 1 ? "Decimal" : "DecimalDotted", components.Length);
    }

    private static bool LooksGroundableTextExcludingMarker(string titleOnlyText)
    {
        var t = PdfTextUtilities.HeadingReadable(titleOnlyText);
        if (t.Length is < 3 or > 180) return false;
        if (!t.Any(char.IsLetter)) return false;
        if (t.Count(c => c is '.' or ';') >= 2) return false;
        if (t.Length >= 80 && t.EndsWith('.')) return false;
        return true;
    }

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
