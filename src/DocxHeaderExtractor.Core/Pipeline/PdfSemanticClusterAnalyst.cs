using System.Text.Json;
using DocxHeaderExtractor.Core.Llm;

namespace DocxHeaderExtractor.Core.Pipeline;

internal enum PdfSemanticClusterRole
{
    HeadingTopic,
    BodySentence,
    TableOrChartLabel,
    Uncertain,
}

internal sealed record PdfSemanticClusterSample(
    string Id,
    PdfStyleKey Style,
    int Lines,
    int Pages,
    int Characters,
    IReadOnlyList<string> Examples);

internal sealed record PdfSemanticClusterDecision(
    string Id,
    PdfSemanticClusterRole Role,
    double Confidence,
    string Reason);

internal sealed record PdfSemanticClusterAnalysis(
    IReadOnlyList<PdfSemanticClusterSample> Samples,
    IReadOnlyList<PdfSemanticClusterDecision> Decisions)
{
    public IReadOnlySet<PdfStyleKey> HeadingStyles => Decisions
        .Where(d => d.Role == PdfSemanticClusterRole.HeadingTopic && d.Confidence >= 0.65)
        .Join(Samples, d => d.Id, s => s.Id, (_, s) => s.Style)
        .ToHashSet();
}

/// <summary>
/// LLM analyst for PDF style clusters. Deterministic code first learns visual clusters and selects
/// examples; the model only answers the semantic question "is this cluster a topic label or body/table
/// text?". The output is advisory and must still be gated by a route-specific extractor.
/// </summary>
internal static class PdfSemanticClusterAnalyst
{
    private const string SystemPrompt =
        "You classify PDF visual style clusters for document outline extraction.\n" +
        "A cluster is a repeated visual style learned by deterministic code from font size, font name, and color.\n" +
        "For each cluster, decide its semantic role using the examples only:\n" +
        "- heading_topic: noun phrase/topic label that opens a section/page/subject.\n" +
        "- body_sentence: prose sentence or claim, usually with a finite verb.\n" +
        "- table_or_chart_label: table/chart metric, short data label, column/row label, formula/figure text.\n" +
        "- uncertain: evidence is mixed or insufficient.\n" +
        "Do not choose heading_topic just because text is large/bold/uppercase. Prefer heading_topic when examples are parallel topic phrases, not sentences.\n" +
        "Return strict JSON only: {\"clusters\":[{\"id\":\"c1\",\"role\":\"heading_topic|body_sentence|table_or_chart_label|uncertain\",\"confidence\":0.0,\"reason\":\"short\"}]}";

    public static IReadOnlyList<PdfSemanticClusterSample> BuildSamples(
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfLine> lines,
        IReadOnlySet<PdfLine>? excludedLines = null,
        int maxClusters = 8,
        int maxExamplesPerCluster = 10)
    {
        var byStyle = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) &&
                        (excludedLines is null || !excludedLines.Contains(l)))
            .GroupBy(l => PdfStyleClusterProfile.StyleOf(l))
            .ToDictionary(g => g.Key, g => g.ToList());

        var id = 1;
        return profile.Clusters
            .Where(c => profile.CandidateStyles.Contains(c.Style))
            .OrderByDescending(c => c.Characters)
            .Take(maxClusters)
            .Select(c => new PdfSemanticClusterSample(
                $"c{id++}",
                c.Style,
                c.Lines,
                c.Pages,
                c.Characters,
                RepresentativeExamples(byStyle.TryGetValue(c.Style, out var group) ? group : [], maxExamplesPerCluster)))
            .Where(s => s.Examples.Count > 0)
            .ToList();
    }

    public static async Task<PdfSemanticClusterAnalysis> AnalyzeAsync(
        IHeaderClassifier classifier,
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfLine> lines,
        CancellationToken ct = default)
    {
        var annotations = PdfLineBlockFilter.Analyze(lines);
        var excluded = annotations
            .Where(a => a.ExcludeFromSemanticSamples)
            .Select(a => a.Line)
            .ToHashSet();
        var samples = BuildSamples(profile, lines, excluded);
        if (samples.Count == 0) return new PdfSemanticClusterAnalysis(samples, []);

        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(SystemPrompt, BuildUserPrompt(samples, BodyExamples(profile, lines, excluded)), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PdfSemanticClusterAnalysis(samples, []);
        }

        return new PdfSemanticClusterAnalysis(samples, ParseDecisions(raw, samples));
    }

    internal static string BuildUserPrompt(
        IReadOnlyList<PdfSemanticClusterSample> samples,
        IReadOnlyList<string>? bodyExamples = null)
    {
        var payload = samples.Select(s => new
        {
            id = s.Id,
            style = new
            {
                font_size = s.Style.FontSizeBucket,
                font = s.Style.FontName,
                color = s.Style.FillColorKey,
            },
            stats = new { lines = s.Lines, pages = s.Pages, chars = s.Characters },
            examples = s.Examples,
        });
        return JsonSerializer.Serialize(new { body_examples = bodyExamples ?? [], clusters = payload });
    }

    internal static IReadOnlyList<PdfSemanticClusterDecision> ParseDecisions(
        string raw,
        IReadOnlyList<PdfSemanticClusterSample> samples)
    {
        var allowed = samples.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<PdfSemanticClusterDecision>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("clusters", out var clusters) ||
                clusters.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in clusters.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!allowed.Contains(id)) continue;

                var roleText = item.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
                var role = ParseRole(roleText);
                var confidence = item.TryGetProperty("confidence", out var confProp) &&
                                 confProp.TryGetDouble(out var c)
                    ? Math.Clamp(c, 0, 1)
                    : 0;
                var reason = item.TryGetProperty("reason", out var reasonProp)
                    ? (reasonProp.GetString() ?? "")
                    : "";
                result.Add(new PdfSemanticClusterDecision(id, role, confidence, reason));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static IReadOnlyList<string> RepresentativeExamples(IReadOnlyList<PdfLine> lines, int take)
    {
        var cleaned = lines
            .Select(l => PdfTextUtilities.Readable(l.Text))
            .Where(t => t.Length is >= 3 and <= 180)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleaned.Count <= take) return cleaned;

        var step = (cleaned.Count - 1) / (double)(take - 1);
        return Enumerable.Range(0, take)
            .Select(i => cleaned[(int)Math.Round(i * step)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BodyExamples(
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfLine> lines,
        IReadOnlySet<PdfLine>? excludedLines) =>
        RepresentativeExamples(
            lines.Where(l => PdfStyleClusterProfile.StyleOf(l) == profile.BodyStyle &&
                             (excludedLines is null || !excludedLines.Contains(l))).ToList(),
            take: 8);

    private static PdfSemanticClusterRole ParseRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "heading_topic" or "heading" or "topic" => PdfSemanticClusterRole.HeadingTopic,
            "body_sentence" or "body" or "prose" => PdfSemanticClusterRole.BodySentence,
            "table_or_chart_label" or "table_label" or "chart_label" or "table" or "chart" =>
                PdfSemanticClusterRole.TableOrChartLabel,
            _ => PdfSemanticClusterRole.Uncertain,
        };

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
