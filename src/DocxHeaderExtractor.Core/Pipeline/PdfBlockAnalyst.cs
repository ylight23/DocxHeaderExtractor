using System.Text.Json;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

internal enum PdfBlockRole
{
    HeadingTopic,
    DocumentTitle,
    BodySentence,
    TableOrChartLabel,
    DecorativeNoise,
    Uncertain,
}

internal sealed record PdfBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Reason,
    IReadOnlyList<string>? EvidenceTags = null,
    SourceTextSpan? HeadingSpan = null);

internal sealed record PdfBlockAnalysis(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyList<PdfBlockDecision> Decisions,
    IReadOnlyList<string> RawResponses)
{
    public IReadOnlySet<string> HeadingBlockIds => Decisions
        .Where(d => d.Role == PdfBlockRole.HeadingTopic && d.Confidence >= 0.65)
        .Select(d => d.Id)
        .ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// LLM analyst for PDF semantic blocks. Deterministic code has already read PDF text, filtered
/// obvious table/repeat/page-number lines, and grouped remaining lines into blocks. The model only
/// classifies each block's semantic role; production routes must still ground and gate any result.
/// </summary>
internal static class PdfBlockAnalyst
{
    private const int MaximumConcurrentSemanticRequests = 4;
    private const string SystemPrompt =
        "You classify candidate PDF text blocks for document outline extraction.\n" +
        "The input can be a lossless PDF-line audit, so it may include page numbers, repeated headers/footers, table labels, and body text.\n" +
        "Classify each supplied block from its text and local context; do not assume it was pre-filtered.\n" +
        "For each block, choose exactly one role:\n" +
        "- heading_topic: a section/page/topic heading, usually a noun phrase or short label that opens content.\n" +
        "- document_title: the title of the document/cover, not a navigational section heading.\n" +
        "- body_sentence: prose sentence/paragraph, usually with a finite verb or full claim.\n" +
        "- table_or_chart_label: table/chart/metric/axis/column/row label, even if short or bold.\n" +
        "- decorative_noise: logo fragments, spaced-out letters, broken cover art, isolated glyphs.\n" +
        "- uncertain: mixed or insufficient evidence.\n" +
        "Do not mark a block heading_topic merely because it is bold/uppercase. Prefer heading_topic for concise topic labels such as 'AVAILABILITY OF INFORMATION'.\n" +
        "Return one compact strict JSON object for every input id. Omit explanations unless needed.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"role\":\"heading_topic|document_title|body_sentence|table_or_chart_label|decorative_noise|uncertain\",\"confidence\":0.0}]}";

    public static async Task<PdfBlockAnalysis> AnalyzeAsync(
        IHeaderClassifier classifier,
        IReadOnlyList<PdfSemanticBlock> blocks,
        CancellationToken ct = default)
    {
        if (blocks.Count == 0) return new PdfBlockAnalysis(blocks, [], []);

        if (blocks.Count > 12)
        {
            using var throttle = new SemaphoreSlim(MaximumConcurrentSemanticRequests);
            var tasks = blocks.Chunk(12).Select(async batch =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    return await AnalyzeAsync(classifier, batch, ct);
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();
            var partials = await Task.WhenAll(tasks);
            return new PdfBlockAnalysis(
                blocks,
                partials.SelectMany(partial => partial.Decisions).ToArray(),
                partials.SelectMany(partial => partial.RawResponses).ToArray());
        }

        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(SystemPrompt, BuildUserPrompt(blocks), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PdfBlockAnalysis(blocks, [], []);
        }

        return new PdfBlockAnalysis(blocks, ParseDecisions(raw, blocks), [raw]);
    }

    internal static string BuildUserPrompt(IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var payload = blocks.Select(b => new
        {
            id = b.Id,
            page = b.Page,
            lines = b.LineCount,
            style = new
            {
                font_size = b.PrimaryStyle.FontSizeBucket,
                font = b.PrimaryStyle.FontName,
                color = b.PrimaryStyle.FillColorKey,
            },
            source_text = b.Text,
            // Canonical text is for deterministic matching after classification, not semantic
            // judgment. Avoid tripling long PDF lines in every analyst request.
            display_text = string.Equals(b.DisplayText, b.Text, StringComparison.Ordinal)
                ? null
                : b.DisplayText,
        });
        return JsonSerializer.Serialize(new { blocks = payload });
    }

    internal static IReadOnlyList<PdfBlockDecision> ParseDecisions(
        string raw,
        IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var allowed = blocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<PdfBlockDecision>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!allowed.Contains(id)) continue;

                var roleText = item.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
                var confidence = item.TryGetProperty("confidence", out var confProp) &&
                                 confProp.TryGetDouble(out var c)
                    ? Math.Clamp(c, 0, 1)
                    : 0;
                var reason = item.TryGetProperty("reason", out var reasonProp)
                    ? reasonProp.GetString() ?? ""
                    : "";
                result.Add(new PdfBlockDecision(id, ParseRole(roleText), confidence, reason));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static PdfBlockRole ParseRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "heading_topic" or "heading" or "topic" => PdfBlockRole.HeadingTopic,
            "document_title" or "cover_title" => PdfBlockRole.DocumentTitle,
            "body_sentence" or "body" or "prose" => PdfBlockRole.BodySentence,
            "table_or_chart_label" or "table_label" or "chart_label" or "table" or "chart" =>
                PdfBlockRole.TableOrChartLabel,
            "decorative_noise" or "decorative" or "noise" or "logo" => PdfBlockRole.DecorativeNoise,
            _ => PdfBlockRole.Uncertain,
        };

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
