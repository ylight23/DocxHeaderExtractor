using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Pipeline;

public static class PdfHierarchyFactHash
{
    public static string OfText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// M8.1a evaluation-input artifact (schemaVersion 2). It carries only source-observed facts and is
/// deliberately gold-free: nothing here is derived from an answer key, and no evaluation type is
/// referenced. Offline evaluators may refuse any artifact that declares otherwise.
/// <para>
/// This is a separate artifact kind from the <c>pdf-stage-eval</c> route audit. That older payload
/// keeps its original shape so previously frozen runs stay byte-comparable.
/// </para>
/// </summary>
public static class PdfHierarchyFactsArtifact
{
    public const int SchemaVersion = 2;
    public const string ArtifactKind = "pdf_hierarchy_facts";

    /// <summary>
    /// Canonical order is source order with a deterministic id tie-break, so a row's fingerprint
    /// depends on the fact set rather than on enumeration order.
    /// </summary>
    public static IReadOnlyList<PdfHierarchyFactAudit> Canonicalize(IReadOnlyList<PdfHierarchyFactAudit> facts) =>
        facts.OrderBy(fact => fact.SourceOrder)
            .ThenBy(fact => fact.FactId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Fingerprint of one document's occurrence set. Two runs that observed the same occurrences at
    /// the same spans agree; a differing fingerprint is an observable run difference, not a defect.
    /// </summary>
    public static string OccurrenceFingerprint(IReadOnlyList<PdfHierarchyFactAudit> facts) =>
        PdfHierarchyFactHash.OfText(string.Join("\n", Canonicalize(facts).Select(fact => string.Join("|",
            fact.FactId,
            fact.SourceOrder.ToString(CultureInfo.InvariantCulture),
            fact.Page.ToString(CultureInfo.InvariantCulture),
            $"{fact.HeadingSpan.Start}-{fact.HeadingSpan.End}",
            fact.SourceBlockTextSha256))));

    public static PdfHierarchyFactsRow BuildRow(
        string file,
        string sourceDocumentSha256,
        IReadOnlyList<PdfHierarchyFactAudit> facts)
    {
        var ordered = Canonicalize(facts);
        var counters = new PdfHierarchyFactsCounters(
            ordered.Count,
            ordered.Count(fact => fact.MarkerPath is not null),
            ordered.Count(fact => fact.ResolvedLevel is not null),
            ordered.Count(fact => fact.MarkerPrefixParentCandidate is not null),
            ordered.Count(fact => fact.ParentResolution == "relationship_unresolved"),
            // M8.1a observes evidence only. A conflict needs a resolver that can disagree with
            // itself, so reporting 0 here would be a measurement claim this stage cannot make.
            "not_measured");
        return new PdfHierarchyFactsRow(
            file,
            sourceDocumentSha256,
            OccurrenceFingerprint(ordered),
            counters,
            ordered.Select(PdfHierarchyFactItem.From).ToArray());
    }
}

public sealed record PdfHierarchyFactsArtifactEnvelope(
    [property: JsonPropertyName("generation")] PdfHierarchyFactsGeneration Generation,
    [property: JsonPropertyName("rows")] IReadOnlyList<PdfHierarchyFactsRow> Rows)
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion => PdfHierarchyFactsArtifact.SchemaVersion;

    [JsonPropertyName("artifactKind")]
    public string ArtifactKind => PdfHierarchyFactsArtifact.ArtifactKind;

    /// <summary>
    /// Structurally true, not self-declared: the producing command accepts no key path and never
    /// constructs an answer key.
    /// </summary>
    [JsonPropertyName("usesGold")]
    public bool UsesGold => false;
}

/// <summary>Frozen harness generation. Documents may be produced in separate runs.</summary>
public sealed record PdfHierarchyFactsGeneration(
    [property: JsonPropertyName("codeRevision")] string? CodeRevision,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("promptSha256")] string PromptSha256,
    [property: JsonPropertyName("routeConfigSha256")] string RouteConfigSha256);

public sealed record PdfHierarchyFactsRow(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sourceDocumentSha256")] string SourceDocumentSha256,
    [property: JsonPropertyName("occurrenceFingerprint")] string OccurrenceFingerprint,
    [property: JsonPropertyName("counters")] PdfHierarchyFactsCounters Counters,
    [property: JsonPropertyName("items")] IReadOnlyList<PdfHierarchyFactItem> Items);

public sealed record PdfHierarchyFactsCounters(
    [property: JsonPropertyName("validatedHeadings")] int ValidatedHeadings,
    [property: JsonPropertyName("markerPathFacts")] int MarkerPathFacts,
    [property: JsonPropertyName("deterministicLevelResolved")] int DeterministicLevelResolved,
    [property: JsonPropertyName("deterministicParentResolved")] int DeterministicParentResolved,
    [property: JsonPropertyName("unresolvedRelationships")] int UnresolvedRelationships,
    [property: JsonPropertyName("conflicts")] string Conflicts);

public sealed record PdfHierarchyFactItem(
    [property: JsonPropertyName("factId")] string FactId,
    [property: JsonPropertyName("sourceFactId")] string SourceFactId,
    [property: JsonPropertyName("sourceOrder")] int SourceOrder,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceBlockText")] string SourceBlockText,
    [property: JsonPropertyName("sourceBlockTextSha256")] string SourceBlockTextSha256,
    [property: JsonPropertyName("headingSpan")] PdfHierarchyFactSpan HeadingSpan,
    [property: JsonPropertyName("headingText")] string HeadingText,
    [property: JsonPropertyName("lineIds")] IReadOnlyList<string> LineIds,
    [property: JsonPropertyName("geometry")] PdfSourceGeometry Geometry,
    [property: JsonPropertyName("structuralScope")] string StructuralScope,
    [property: JsonPropertyName("documentRegime")] string DocumentRegime,
    [property: JsonPropertyName("markerFamily")] string? MarkerFamily,
    [property: JsonPropertyName("markerDepth")] int? MarkerDepth,
    [property: JsonPropertyName("markerPath")] string? MarkerPath,
    [property: JsonPropertyName("previousValidatedId")] string? PreviousValidatedId,
    [property: JsonPropertyName("markerPrefixParentCandidate")] string? MarkerPrefixParentCandidate,
    [property: JsonPropertyName("resolvedLevel")] int? ResolvedLevel,
    [property: JsonPropertyName("parentResolution")] string ParentResolution,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence)
{
    public static PdfHierarchyFactItem From(PdfHierarchyFactAudit fact) => new(
        fact.FactId,
        fact.Id,
        fact.SourceOrder,
        fact.Page,
        fact.SourceBlockText,
        fact.SourceBlockTextSha256,
        new PdfHierarchyFactSpan(fact.HeadingSpan.Start, fact.HeadingSpan.End),
        fact.HeadingText,
        fact.LineIds,
        fact.Geometry,
        fact.StructuralScope,
        fact.DocumentRegime,
        fact.MarkerFamily,
        fact.MarkerDepth,
        fact.MarkerPath,
        fact.PreviousValidatedId,
        fact.MarkerPrefixParentCandidate,
        fact.ResolvedLevel,
        fact.ParentResolution,
        fact.Evidence);
}

/// <summary>End is exclusive, matching <c>TextOffsetSpan</c> as the validator enforces it.</summary>
public sealed record PdfHierarchyFactSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);
