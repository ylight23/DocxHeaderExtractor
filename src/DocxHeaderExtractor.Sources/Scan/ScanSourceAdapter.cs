using System.Text.Json;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>The scan's declared reading is the source text for binding and every downstream projection.
/// OCR originals and other readings stay in its source anchor; image pixels are never PDF coordinates.</summary>
internal static class ScanSourceAdapter
{
    internal static DocumentSourceSnapshot Build(ScanDocumentSelection prepared)
    {
        var lines = prepared.Lines;
        if (lines.Select(l => l.Alias).Distinct(StringComparer.Ordinal).Count() != lines.Count
            || lines.Select(l => l.LineId).Distinct(StringComparer.Ordinal).Count() != lines.Count)
            throw new InvalidOperationException("SCAN_SOURCE_ID_DUPLICATE");
        foreach (var l in lines)
            if (!l.Readings.TryGetValue(l.ReadingViewId, out var text) || text != l.Text)
                throw new InvalidOperationException("SCAN_SOURCE_READING_MISMATCH:" + l.Alias);
        var atoms = lines.Select((l, i) => new SemanticSourceAtom(l.Alias, l.LineId, i, l.Page, i, 0, l.Text)).ToArray();
        var catalog = new DocumentSourceCatalog(lines.Select((l, i) => new DocumentSourceUnit(l.LineId, i, l.Text,
            new SourceAnchor
            {
                SourceType = "scan", Page = l.Page, RenderLineIds = [l.LineId],
                Scan = new ScanLineAnchor(l.Box, l.ImageWidth, l.ImageHeight, l.ReadingViewId, l.PrimaryReading, l.Readings, l.TranscriptionStatus, l.TranscriptionText),
            }, new StructuralSpan(0, l.Text.Length))));
        var contexts = lines.Select((l, i) => new OccurrenceContext(l.LineId, l.Text, "document_body", ["scan_reader"],
            lines.Take(i).TakeLast(3).Select(p => p.Text).ToArray(), lines.Skip(i + 1).Take(3).Select(p => p.Text).ToArray()))
            .ToDictionary(c => c.SourceId, StringComparer.Ordinal);
        var evidence = lines.Select((l, i) => new CanonicalSemanticSourceEvidence(l.Alias, l.LineId, i, l.Text, "document_body",
            ["scan-derived-reading"], JsonSerializer.Deserialize<JsonElement>(l.FactsJson), new { }, Array.Empty<object>(),
            new[] { "scan-derived-reading" }, contexts[l.LineId].PreviousOccurrences, contexts[l.LineId].NextOccurrences)
            { LocationFacts = new { l.Page, l.Box, unit = "image-pixels-top-left" } }).ToArray();
        return new DocumentSourceSnapshot(atoms,
            lines.Select((l, i) => new DocumentOccurrence(l.LineId, l.Alias, i, l.Text, "scan")).ToArray(), evidence,
            TesseractOcr.Sha256(JsonSerializer.Serialize(lines.Select(l => new { l.Alias, l.Text, l.ReadingViewId }))),
            TesseractOcr.Sha256(JsonSerializer.Serialize(lines)), prepared.SourceSha256, contexts, catalog,
            atoms.Select(a => new SemanticSourceAlias(a.Alias, a.SourceId, a.Ordinal, a.Text, new StructuralSpan(0, a.Text.Length))).ToArray(),
            atoms.ToDictionary(a => a.SourceId, a => a.Ordinal, StringComparer.Ordinal))
        { SourceKind = "scan", DocumentId = prepared.SourceSha256[..16] };
    }
}
