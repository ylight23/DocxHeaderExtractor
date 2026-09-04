using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class BlindSourcePacketBuilder
{
    public static BlindSourcePacket Create(
        SourceDocument source,
        string sourceDocumentSha256,
        DateTimeOffset? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(sourceDocumentSha256))
            throw new ArgumentException("Source SHA-256 is required.", nameof(sourceDocumentSha256));

        // The packet intentionally omits parser fields whose names or values encode a heading
        // decision (outline/numbering levels). Reviewers receive source text and raw observations,
        // never a prediction-shaped answer.
        return new BlindSourcePacket
        {
            DocumentId = source.DocumentId,
            FileName = source.FileName,
            SourceKind = source.SourceKind,
            SourceDocumentSha256 = sourceDocumentSha256,
            Occurrences = source.Paragraphs.Select(paragraph => new BlindSourceOccurrence
            {
                SourceId = paragraph.SourceId,
                SourceOrdinal = paragraph.SourceOrdinal,
                RawText = paragraph.Text,
                FullSpan = new Accuracy99Span(0, paragraph.Text.Length),
                Style = new BlindSourceStyleFacts
                {
                    StyleId = paragraph.Style.StyleId,
                    StyleName = paragraph.Style.StyleName,
                    Bold = paragraph.Style.Bold,
                    Italic = paragraph.Style.Italic,
                    Underline = paragraph.Style.Underline,
                    AllCaps = paragraph.Style.AllCaps,
                    FontSizePt = paragraph.Style.FontSizePt,
                    Alignment = paragraph.Style.Alignment,
                },
                Numbering = new BlindSourceNumberingFacts
                {
                    NumberingId = paragraph.Numbering.NumberingId,
                    NumberLabel = paragraph.Numbering.NumberLabel,
                    NumberingFormat = paragraph.Numbering.NumberingFormat,
                },
                Layout = new BlindSourceLayoutFacts
                {
                    InContentControl = paragraph.Layout.InContentControl,
                    KeepNext = paragraph.Layout.KeepNext,
                    PageBreakBefore = paragraph.Layout.PageBreakBefore,
                    TableDepth = paragraph.Layout.TableDepth,
                    SectionIndex = paragraph.Layout.SectionIndex,
                },
            }).ToArray(),
        };
    }

    public static BlindSourcePacket CreateFromFile(SourceDocument source) =>
        Create(source, HumanGoldValidator.ComputeSha256(source.SourcePath));
}

public static class BlindSourcePacketLeakageValidator
{
    private static readonly HashSet<string> ForbiddenPropertyNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "candidate", "candidates", "prediction", "predictions", "predictedLevel",
            "predictedParent", "confidence", "rank", "selected", "rejected", "modelAnswer",
            "validatedHeading", "validatedHeadings", "accuracyResult", "accuracyResults",
            "goldLabel", "goldLabels", "parentSourceId", "headingSpan", "level",
        };

    public static IReadOnlyList<string> FindLeaks(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ["json-empty"];
        using var document = JsonDocument.Parse(json);
        var leaks = new List<string>();
        Walk(document.RootElement, "$", leaks);
        return leaks;
    }

    public static void EnsureClean(string json)
    {
        var leaks = FindLeaks(json);
        if (leaks.Count > 0)
            throw new InvalidDataException($"blind-source-packet-leakage: {string.Join(", ", leaks)}");
    }

    private static void Walk(JsonElement element, string path, ICollection<string> leaks)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenPropertyNames.Contains(property.Name))
                    leaks.Add($"{path}.{property.Name}");
                Walk(property.Value, $"{path}.{property.Name}", leaks);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                Walk(item, $"{path}[{index++}]", leaks);
        }
    }
}
