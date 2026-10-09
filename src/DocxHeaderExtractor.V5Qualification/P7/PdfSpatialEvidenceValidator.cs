using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>Verifies ledger integrity and source assertions only, never semantic truth.</summary>
internal static class PdfSpatialEvidenceValidator
{
    public static IReadOnlyList<SpatialEvidenceValidationIssue> Validate(
        DocumentSourceSnapshot source, PdfSourceDetails details, PdfSpatialEvidenceLedger ledger,
        IReadOnlySet<string> issuedAliases, IReadOnlyList<SpatialEvidenceReference> references,
        IReadOnlyList<SpatialRelationQuery>? queries = null)
    {
        // Recompute the expected universe from trusted parser inputs, not from submitted fact IDs,
        // self-declared verification status, measurements, or the ledger's own claimed hash.
        var expected = PdfSpatialEvidenceLedgerBuilder.Build(source, details, queries);
        if (!ledger.CanonicalBytes().SequenceEqual(expected.CanonicalBytes()))
            return [new("LEDGER_DOES_NOT_MATCH_PARSER_SNAPSHOT")];
        var issues = new List<SpatialEvidenceValidationIssue>();
        var universe = expected.Observations.Select(observation => observation.Alias).ToHashSet(StringComparer.Ordinal);
        if (issuedAliases.Any(alias => !universe.Contains(alias))) issues.Add(new("ISSUED_ALIAS_OUTSIDE_SOURCE_UNIVERSE"));
        var facts = expected.Facts.ToDictionary(fact => fact.FactId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!seen.Add(reference.FactId)) { issues.Add(new("DUPLICATE_FACT_REFERENCE", reference.FactId)); continue; }
            if (!facts.TryGetValue(reference.FactId, out var fact)) { issues.Add(new("UNKNOWN_FACT_REFERENCE", reference.FactId)); continue; }
            if (!reference.Subjects.SequenceEqual(fact.Subjects, StringComparer.Ordinal)) issues.Add(new("FACT_SUBJECTS_MISMATCH", reference.FactId));
            if (fact.Subjects.Any(alias => !issuedAliases.Contains(alias))) issues.Add(new("FACT_OUTSIDE_ISSUED_SUBJECTS", reference.FactId));
            if (fact.Availability != "OBSERVED") issues.Add(new("FACT_NOT_AVAILABLE", reference.FactId));
            if (reference.AssertedValue is { } asserted &&
                (fact.Value is not { } value || !JsonElement.DeepEquals(asserted, value)))
                issues.Add(new("FACT_VALUE_MISMATCH", reference.FactId));
        }
        return Array.AsReadOnly(issues.ToArray());
    }
}
