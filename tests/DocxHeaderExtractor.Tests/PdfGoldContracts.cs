using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One human-adjudicated heading occurrence in a PDF.
/// <para>
/// Addressed the way the model addresses one: by source alias plus, when the heading is part of an
/// occurrence rather than all of it, the exact text. Never by offsets - coordinates belong to the
/// harness, and a Gold carrying them would assert something no reviewer looked at.
/// </para>
/// <para>
/// The text is the PDF's VerbatimText, the declared glyph projection the model is shown and the
/// binder binds against. Not DisplayText, and not punctuation-normalised: those disagree exactly
/// where a PDF's reconstruction is hard, and Gold written against the wrong one would fail to bind
/// for reasons that have nothing to do with the model.
/// </para>
/// </summary>
public sealed record PdfGoldHeading(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("semanticRole")] string SemanticRole)
{
    /// <summary>Required for VERBATIM_TEXT, forbidden for WHOLE_ALIAS.</summary>
    [JsonPropertyName("verbatimText")] public string? VerbatimText { get; init; }

    /// <summary>1-based, only when the text occurs more than once inside its own occurrence.</summary>
    [JsonPropertyName("occurrence")] public int? Occurrence { get; init; }

    [JsonPropertyName("leftExactContext")] public string? LeftExactContext { get; init; }
    [JsonPropertyName("rightExactContext")] public string? RightExactContext { get; init; }

    /// <summary>Only where a reviewer actually adjudicated the relation. Absent is not "root".</summary>
    [JsonPropertyName("parentSourceAlias")] public string? ParentSourceAlias { get; init; }
}

/// <summary>A Gold document: what was reviewed, against which source, and on whose authority.</summary>
public sealed record PdfGoldDocument(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("headings")] IReadOnlyList<PdfGoldHeading> Headings)
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_pdf_semantic_gold";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-pdf-semantic-gold-v1";

    /// <summary>The authoritative count this review must reconcile with, when one exists.</summary>
    [JsonPropertyName("semanticHeadingTotal")] public int? SemanticHeadingTotal { get; init; }

    [JsonPropertyName("finalAuthority")] public string FinalAuthority { get; init; } = "UNREVIEWED";

    /// <summary>What may be scored from this Gold. A count alone scores neither membership nor relations.</summary>
    [JsonPropertyName("capabilities")] public PdfGoldCapabilities Capabilities { get; init; } = new();

    /// <summary>Provider calls spent producing this Gold. Must be zero.</summary>
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record PdfGoldCapabilities
{
    [JsonPropertyName("semanticEvaluable")] public bool SemanticEvaluable { get; init; }
    [JsonPropertyName("occurrenceEvaluable")] public bool OccurrenceEvaluable { get; init; }
    [JsonPropertyName("hierarchyEvaluable")] public bool HierarchyEvaluable { get; init; }
}

public sealed record PdfGoldIssue(string Code, string SourceAlias, string Detail);

/// <summary>
/// Binds a Gold document to the PDF catalog it claims to describe.
/// <para>
/// Every failure here is a Gold defect, never a model one. It runs before any measurement, because
/// a Gold row that does not bind cannot be a miss - it is an assertion about an occurrence that
/// does not exist, and counting it as a false negative would blame the model for the reviewer.
/// </para>
/// </summary>
public static class PdfGoldValidator
{
    public const string AliasOutsideUniverse = "ALIAS_OUTSIDE_SOURCE_UNIVERSE";
    public const string WholeAliasCarriesText = "WHOLE_ALIAS_CARRIES_TEXT";
    public const string VerbatimTextMissing = "VERBATIM_TEXT_MISSING";
    public const string TextNotInSource = "TEXT_NOT_FOUND_IN_SOURCE_OCCURRENCE";
    public const string AmbiguousBinding = "AMBIGUOUS_BINDING";
    public const string DisambiguatorUnresolved = "DISAMBIGUATOR_DOES_NOT_RESOLVE";
    public const string DuplicateRow = "DUPLICATE_GOLD_ROW";
    public const string ParentOutsideUniverse = "PARENT_ALIAS_OUTSIDE_SOURCE_UNIVERSE";
    public const string TotalDisagreesWithAuthority = "ROW_COUNT_DISAGREES_WITH_AUTHORITATIVE_TOTAL";

    public static IReadOnlyList<PdfGoldIssue> Validate(
        PdfGoldDocument gold,
        DocumentSourceCatalog catalog,
        IReadOnlyList<SemanticSourceAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(aliases);

        var textById = catalog.Units.ToDictionary(unit => unit.SourceId, unit => unit.Text, StringComparer.Ordinal);
        var textByAlias = aliases
            .Where(alias => textById.ContainsKey(alias.SourceId))
            .ToDictionary(alias => alias.Alias, alias => textById[alias.SourceId], StringComparer.Ordinal);

        var issues = new List<PdfGoldIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in gold.Headings)
        {
            if (!textByAlias.TryGetValue(row.SourceAlias, out var sourceText))
            {
                issues.Add(new(AliasOutsideUniverse, row.SourceAlias,
                    "Gold names an alias this document's source catalog does not contain."));
                continue;
            }

            if (row.ParentSourceAlias is { Length: > 0 } parent && !textByAlias.ContainsKey(parent))
                issues.Add(new(ParentOutsideUniverse, row.SourceAlias, $"parent {parent} is not a source alias."));

            var key = $"{row.SourceAlias}|{row.SelectionMode}|{row.VerbatimText}|{row.Occurrence}";
            if (!seen.Add(key))
                issues.Add(new(DuplicateRow, row.SourceAlias, "The same occurrence is claimed twice."));

            if (string.Equals(row.SelectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(row.VerbatimText))
                    issues.Add(new(WholeAliasCarriesText, row.SourceAlias,
                        "WHOLE_ALIAS already addresses the whole occurrence; text would be a second claim."));
                continue;
            }

            if (string.IsNullOrEmpty(row.VerbatimText))
            {
                issues.Add(new(VerbatimTextMissing, row.SourceAlias, "VERBATIM_TEXT needs the exact text."));
                continue;
            }

            var hits = Occurrences(sourceText, row.VerbatimText);
            if (hits.Count == 0)
            {
                issues.Add(new(TextNotInSource, row.SourceAlias,
                    $"{Excerpt(row.VerbatimText)} is not an exact substring of the source occurrence."));
                continue;
            }

            if (hits.Count == 1) continue;

            if (row.Occurrence is null && row.LeftExactContext is null && row.RightExactContext is null)
            {
                issues.Add(new(AmbiguousBinding, row.SourceAlias,
                    $"{Excerpt(row.VerbatimText)} occurs {hits.Count} times here and nothing says which."));
                continue;
            }

            if (Resolve(sourceText, row, hits) is null)
                issues.Add(new(DisambiguatorUnresolved, row.SourceAlias,
                    "The disambiguator matches no single occurrence of the text."));
        }

        if (gold.SemanticHeadingTotal is { } total && gold.Headings.Count != total)
        {
            issues.Add(new(TotalDisagreesWithAuthority, gold.DocumentId,
                $"review materialised {gold.Headings.Count} rows against an authoritative total of {total}."));
        }

        return issues;
    }

    /// <summary>The single start offset a row addresses, or null when it does not resolve to one.</summary>
    public static int? Resolve(string sourceText, PdfGoldHeading row, IReadOnlyList<int> hits)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Occurrence is { } ordinal)
            return ordinal >= 1 && ordinal <= hits.Count ? hits[ordinal - 1] : null;

        var text = row.VerbatimText ?? string.Empty;
        var matches = hits.Where(start =>
            (row.LeftExactContext is null ||
             (start >= row.LeftExactContext.Length &&
              sourceText.AsSpan(start - row.LeftExactContext.Length, row.LeftExactContext.Length)
                  .SequenceEqual(row.LeftExactContext))) &&
            (row.RightExactContext is null ||
             (start + text.Length + row.RightExactContext.Length <= sourceText.Length &&
              sourceText.AsSpan(start + text.Length, row.RightExactContext.Length)
                  .SequenceEqual(row.RightExactContext)))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static IReadOnlyList<int> Occurrences(string haystack, string needle)
    {
        var found = new List<int>();
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
            found.Add(at);
        return found;
    }

    private static string Excerpt(string value) => value.Length <= 48 ? value : value[..48] + "...";
}
