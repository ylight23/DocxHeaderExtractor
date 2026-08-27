using System.Globalization;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record PartialKeyPackageOptions(
    string OutputDirectory,
    int MaxHeadings = 30,
    int StartAt = 0,
    bool DistributedSample = true);

public sealed record PartialKeyPackageResult(
    string File,
    string Directory,
    string DraftKeyPath,
    string ReviewCsvPath,
    string OutlineJsonPath,
    int SelectedHeadings,
    int TotalHeadings,
    string SampleStrategy,
    TextLayoutLineProbeReport LineProbe);

/// <summary>
/// Builds a small human-review package for documents that pass internal gates but still lack a real
/// answer key. The generated key is deliberately marked partial_human and must be reviewed before it
/// becomes calibration data.
/// </summary>
public sealed class PartialKeyPackage(PipelineOptions options)
{
    public async Task<PartialKeyPackageResult> RunAsync(
        string inputPath,
        PartialKeyPackageOptions packageOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(packageOptions);
        if (packageOptions.MaxHeadings <= 0)
            throw new ArgumentOutOfRangeException(nameof(packageOptions.MaxHeadings), "MaxHeadings must be positive.");
        if (packageOptions.StartAt < 0)
            throw new ArgumentOutOfRangeException(nameof(packageOptions.StartAt), "StartAt must be non-negative.");

        using var pipeline = new AuthorityExtractionPipeline(options);
        var outline = await pipeline.RunAsync(inputPath, ct);

        return await RunAsync(inputPath, outline, packageOptions, ct);
    }

    public async Task<PartialKeyPackageResult> RunAsync(
        string inputPath,
        DocumentOutline outline,
        PartialKeyPackageOptions packageOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(packageOptions);
        if (packageOptions.MaxHeadings <= 0)
            throw new ArgumentOutOfRangeException(nameof(packageOptions.MaxHeadings), "MaxHeadings must be positive.");
        if (packageOptions.StartAt < 0)
            throw new ArgumentOutOfRangeException(nameof(packageOptions.StartAt), "StartAt must be non-negative.");

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var safeStem = SafePathSegment(stem);
        var packageDir = Path.Combine(Path.GetFullPath(packageOptions.OutputDirectory), safeStem);
        Directory.CreateDirectory(packageDir);

        var outlineJsonPath = Path.Combine(packageDir, $"{safeStem}.current-outline.json");
        var reviewCsvPath = Path.Combine(packageDir, $"{safeStem}.partial-review.csv");
        var draftKeyPath = Path.Combine(packageDir, $"{safeStem}.partial.key");

        var ordered = outline.Headings
            .OrderBy(h => h.Index)
            .ThenBy(h => h.Text, StringComparer.Ordinal)
            .ToList();
        var selected = SelectSample(ordered, packageOptions);
        var sampleStrategy = packageOptions.DistributedSample
            ? $"distributed_even:{selected.Count}/{ordered.Count}:start={packageOptions.StartAt}"
            : $"contiguous_first:{selected.Count}/{ordered.Count}:start={packageOptions.StartAt}";
        var lineProbe = AnalyzeLines(inputPath);

        await File.WriteAllTextAsync(outlineJsonPath, OutlineFormatter.Format(outline, OutlineFormat.Json), Utf8NoBom(), ct);
        await File.WriteAllTextAsync(reviewCsvPath, WriteReviewCsv(inputPath, outline, selected, sampleStrategy, lineProbe), Utf8NoBom(), ct);
        await File.WriteAllTextAsync(draftKeyPath, WriteDraftKey(inputPath, selected, sampleStrategy, lineProbe), Utf8NoBom(), ct);

        return new PartialKeyPackageResult(
            inputPath,
            packageDir,
            draftKeyPath,
            reviewCsvPath,
            outlineJsonPath,
            selected.Count,
            outline.Headings.Count,
            sampleStrategy,
            lineProbe);
    }

    private static string WriteDraftKey(
        string inputPath,
        IReadOnlyList<HeadingRecord> headings,
        string sampleStrategy,
        TextLayoutLineProbeReport lineProbe)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Path.GetFileName(inputPath));
        sb.AppendLine("# partial_human draft from current pipeline; REVIEW REQUIRED before calibration.");
        sb.AppendLine("# Delete false headings, fix levels/text comments, then keep this file as a partial key.");
        sb.Append("# sample_strategy: ").AppendLine(sampleStrategy);
        sb.Append("# line_probe: paragraphs=").Append(lineProbe.TextParagraphs.ToString(CultureInfo.InvariantCulture))
          .Append(" hard_lines=").Append(lineProbe.HardLines.ToString(CultureInfo.InvariantCulture))
          .Append(" recovered_lines=").Append(lineProbe.RecoveredLines.ToString(CultureInfo.InvariantCulture))
          .Append(" long_paragraphs=").AppendLine(lineProbe.LongParagraphs.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("# Format: @<stable-id> <level>   # heading text");
        foreach (var h in headings)
        {
            if (!string.IsNullOrWhiteSpace(h.StableId))
                sb.Append('@').Append(h.StableId).Append(' ');
            else
                sb.Append(h.Index.ToString(CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(h.Level?.ToString(CultureInfo.InvariantCulture) ?? "")
              .Append("   # ")
              .AppendLine(NormalizeInline(h.Text));
        }
        return sb.ToString();
    }

    private static string WriteReviewCsv(
        string inputPath,
        DocumentOutline outline,
        IReadOnlyList<HeadingRecord> headings,
        string sampleStrategy,
        TextLayoutLineProbeReport lineProbe)
    {
        var sb = new StringBuilder();
        sb.Append("# file,").AppendLine(Csv(Path.GetFullPath(inputPath)));
        sb.Append("# route,").AppendLine(Csv(outline.DeterministicRoute ?? ""));
        sb.Append("# sample_strategy,").AppendLine(Csv(sampleStrategy));
        sb.Append("# line_probe,").Append(Csv(
            $"paragraphs={lineProbe.TextParagraphs}; hard_lines={lineProbe.HardLines}; recovered_lines={lineProbe.RecoveredLines}; long_paragraphs={lineProbe.LongParagraphs}"))
          .AppendLine();
        sb.Append("# diagnostics,").AppendLine(Csv(outline.Diagnostics is null
            ? ""
            : $"{outline.Diagnostics.Status}: {outline.Diagnostics.Reason}"));
        sb.Append("# candidates,").AppendLine(Csv(outline.Diagnostics is null
            ? ""
            : string.Join("; ", outline.Diagnostics.Candidates.Select(c =>
                $"{c.Route}:{(c.Accepted ? "accepted" : "rejected")}:{c.HeadingCount}"))));
        sb.AppendLine("order,index,stableId,level,text,source,confidence,confidenceBasis,decisionStatus,disputed,reviewAction,reviewLevel,reviewText,notes");
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(h.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(h.StableId ?? "")).Append(',')
              .Append(h.Level?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Csv(NormalizeInline(h.Text))).Append(',')
              .Append(h.Source).Append(',')
              .Append(h.Confidence.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(h.ConfidenceBasis)).Append(',')
              .Append(h.DecisionStatus).Append(',')
              .Append(h.Disputed ? "true" : "false").Append(',')
              .Append("keep").Append(',')
              .Append(h.Level?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Csv(NormalizeInline(h.Text))).Append(',')
              .AppendLine();
        }
        return sb.ToString();
    }

    private static List<HeadingRecord> SelectSample(
        IReadOnlyList<HeadingRecord> ordered,
        PartialKeyPackageOptions options)
    {
        var pool = ordered.Skip(options.StartAt).ToList();
        if (!options.DistributedSample)
            return pool.Take(options.MaxHeadings).ToList();
        if (pool.Count <= options.MaxHeadings)
            return pool;
        if (options.MaxHeadings == 1)
            return [pool[0]];

        var selected = new List<HeadingRecord>();
        var seen = new HashSet<int>();
        var max = pool.Count - 1;
        for (var i = 0; i < options.MaxHeadings; i++)
        {
            var index = (int)Math.Round(i * max / (double)(options.MaxHeadings - 1), MidpointRounding.AwayFromZero);
            if (seen.Add(index))
                selected.Add(pool[index]);
        }

        for (var i = 0; selected.Count < options.MaxHeadings && i < pool.Count; i++)
        {
            if (seen.Add(i))
                selected.Add(pool[i]);
        }

        return selected
            .OrderBy(h => h.Index)
            .ThenBy(h => h.Text, StringComparer.Ordinal)
            .ToList();
    }

    private TextLayoutLineProbeReport AnalyzeLines(string inputPath)
    {
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        try
        {
            var slim = new DocxSlimExtractor(options.Extraction).Extract(conversion.Path);
            return TextLayoutLineProbe.Analyze(slim.Paragraphs);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    private static Encoding Utf8NoBom() => new UTF8Encoding(false);

    private static string NormalizeInline(string text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }

    private static string Csv(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
}
