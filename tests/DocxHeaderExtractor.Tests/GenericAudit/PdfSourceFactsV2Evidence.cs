using System.Reflection;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests.GenericAudit;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.2's evidence layer over a chosen PDF_SOURCE_FACTS version, without touching V1.2's frozen
/// files. It repeats only <c>V1_2.SourceEvidenceProfile.FromPdf</c>'s mapping from lines to occurrences, reading
/// the lines under <paramref name="facts"/>, and hands them to V1.2's own profile builder - the one implementation
/// of body typography, repetition and pointers - so the engine logic is V1.2's, unchanged. Under
/// PDF_SOURCE_FACTS_V1 it reproduces V1.2's committed runs exactly (<see cref="PdfSourceFactsV2EvidenceTests"/>).
/// </summary>
internal static class PdfSourceFactsV2Evidence
{
    public const string EngineId = "GENERIC_AUDIT_ENGINE_V1.2 over PDF_SOURCE_FACTS_V2";

    private static readonly MethodInfo Build = typeof(V12.SourceEvidenceProfile)
        .GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("V1.2's profile builder is missing");

    public static V12.SourceEvidenceProfile FromPdf(string path, PdfSourceFactsVersion facts)
    {
        // The atom universe does not depend on the facts version (PDF_SOURCE_FACTS_V2_AUDIT).
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(path, facts).Atoms;
        Dictionary<string, PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
            lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3, facts)
                .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var extents = lines.Values.GroupBy(l => l.Page).ToDictionary(g => g.Key, g => (Min: g.Min(l => l.Y), Max: g.Max(l => l.Y)));
        var rows = atoms.GroupBy(a => (a.Page, a.Row)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Segment).ToArray());
        var occurrences = atoms.Select((a, i) =>
        {
            var l = lines[a.SourceId];
            var (min, max) = extents[a.Page];
            var row = rows[(a.Page, a.Row)];
            var band = max > min ? (max - l.Y) / (max - min) : 0.5;
            var text = a.Text.Trim();
            var lead = l.LeadingBoldPrefix.Length > 0 ? V12.LexicalShape.ExactLead(text, l.LeadingBoldPrefix) : null;
            // A lead is only a lead when something that is not bold follows it on the line.
            if (lead is not null && text[lead.Length..].Trim().Length == 0) lead = null;
            return new V12.SourceOccurrence(
                a.Alias, a.SourceId, i, text, "PDF", a.Page, Math.Round(l.FontSize, 1), l.BoldRatio >= 0.5 && lead is null, l.ItalicRatio >= 0.5,
                a.Text.Any(char.IsLetter) && a.Text.Where(char.IsLetter).All(char.IsUpper), false, null, null, null, 0, false,
                row.Length, Array.IndexOf(row, a), row.Any(o => o != a && V12.LexicalShape.IsFiguresOnly(o.Text.Trim()) && o.Text.Any(char.IsDigit)), band)
            {
                Row = a.Row,
                Left = l.Left,
                Right = l.Right,
                Y = l.Y,
                BoldLead = lead,
            };
        }).ToArray();
        return (V12.SourceEvidenceProfile)Build.Invoke(null, ["PDF", occurrences, extents.Count])!;
    }
}
