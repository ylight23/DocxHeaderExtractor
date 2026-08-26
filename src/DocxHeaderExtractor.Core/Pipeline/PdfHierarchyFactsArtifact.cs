using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

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
    public const int SchemaVersion = 4;
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
        IReadOnlyList<PdfHierarchyFactAudit> facts,
        IReadOnlyList<PdfValidatedStructure>? validatedStructures = null,
        IReadOnlyList<PdfCanonicalGrounding>? canonicalGroundings = null,
        string? semanticLaneStatus = null,
        SemanticLaneOptions? semanticLaneOptions = null,
        string? spanLaneStatus = null)
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
            ordered.Select(PdfHierarchyFactItem.From).ToArray(),
            // Carried so a downstream projection can be replayed from the frozen artifact alone
            // rather than from a live route.
            validatedStructures ?? [],
            canonicalGroundings ?? [])
        {
            // The producing route's own lane status, copied verbatim. Without it an artifact with no
            // validated structures cannot be told apart from one whose semantic lane degraded, and
            // those are different operational events with the same cardinality.
            SemanticLaneStatus = semanticLaneStatus,
            // Separate from the semantic lane on purpose: a heading needs a resolved span to
            // validate, so this lane can fail while the role lane completes.
            SpanLaneStatus = spanLaneStatus,
            // The thresholds the lane was actually given. Without them a partial_timeout says the run
            // degraded but not whether the service was slow or the policy was tight, and those call
            // for opposite responses.
            SemanticLaneTimeouts = semanticLaneOptions is null ? null : new PdfSemanticLaneTimeouts(
                (int)semanticLaneOptions.RequestTimeout.TotalSeconds,
                (int)semanticLaneOptions.BatchTimeout.TotalSeconds,
                (int)semanticLaneOptions.LaneDeadline.TotalSeconds),
        };
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

/// <summary>
/// One document's frozen facts. <see cref="SemanticLaneStatus"/> is null on artifacts produced before
/// it existed, and that is deliberately not read as "the run was complete": absent provenance is
/// absent, not reassuring.
/// </summary>
public sealed record PdfHierarchyFactsRow(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sourceDocumentSha256")] string SourceDocumentSha256,
    [property: JsonPropertyName("occurrenceFingerprint")] string OccurrenceFingerprint,
    [property: JsonPropertyName("counters")] PdfHierarchyFactsCounters Counters,
    [property: JsonPropertyName("items")] IReadOnlyList<PdfHierarchyFactItem> Items,
    [property: JsonPropertyName("validatedStructures")] IReadOnlyList<PdfValidatedStructure> ValidatedStructures,
    [property: JsonPropertyName("canonicalGroundings")] IReadOnlyList<PdfCanonicalGrounding> CanonicalGroundings)
{
    /// <summary>
    /// The producing route's semantic lane status, copied rather than recomputed. Null means the
    /// artifact predates this field - unknown provenance, not a completed run.
    /// </summary>
    [JsonPropertyName("semanticLaneStatus")]
    public string? SemanticLaneStatus { get; init; }

    /// <summary>
    /// The span-resolution lane's own outcome. Null on artifacts that predate the field - unknown,
    /// never "complete". Reported beside <see cref="SemanticLaneStatus"/> rather than merged into it,
    /// because that field already means something and a silent widening would break every reader that
    /// relies on it.
    /// </summary>
    [JsonPropertyName("spanLaneStatus")]
    public string? SpanLaneStatus { get; init; }

    /// <summary>
    /// The thresholds the semantic lane ran under, copied from the options it was handed. Null on
    /// artifacts that predate the field - unknown, not "the defaults".
    /// </summary>
    [JsonPropertyName("semanticLaneTimeouts")]
    public PdfSemanticLaneTimeouts? SemanticLaneTimeouts { get; init; }
}

/// <summary>
/// The lane's configured thresholds in seconds, in the producer's own terms. Deliberately not a
/// policy summary: no "aggressive"/"relaxed" classification is derived, because the route computes
/// no such fact.
/// </summary>
public sealed record PdfSemanticLaneTimeouts(
    [property: JsonPropertyName("requestSeconds")] int RequestSeconds,
    [property: JsonPropertyName("batchSeconds")] int BatchSeconds,
    [property: JsonPropertyName("laneDeadlineSeconds")] int LaneDeadlineSeconds);

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
    [property: JsonPropertyName("markerComponents")] IReadOnlyList<int> MarkerComponents,
    [property: JsonPropertyName("previousValidatedId")] string? PreviousValidatedId,
    [property: JsonPropertyName("markerPrefixParentCandidate")] string? MarkerPrefixParentCandidate,
    [property: JsonPropertyName("resolvedLevel")] int? ResolvedLevel,
    [property: JsonPropertyName("parentResolution")] string ParentResolution,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// Reverses <see cref="From"/> so a frozen row can be replayed through
    /// <see cref="PdfFinalStructureProjection"/> without re-running extraction. Lossy in exactly one
    /// field: <see cref="PdfHierarchyFactAudit.MarkerIsPath"/> is not carried by the artifact and is
    /// defaulted to <c>false</c> here - the projection never reads it, so the replay is faithful for
    /// every field M9.1/M9.2 actually consume.
    /// </summary>
    public PdfHierarchyFactAudit ToFactAudit() => new(
        SourceFactId, SourceOrder, Page, StructuralScope, DocumentRegime, MarkerFamily, MarkerDepth,
        MarkerIsPath: false, MarkerPath, PreviousValidatedId, MarkerPrefixParentCandidate, ResolvedLevel,
        ParentResolution, Evidence)
    {
        FactId = FactId,
        SourceBlockText = SourceBlockText,
        SourceBlockTextSha256 = SourceBlockTextSha256,
        HeadingSpan = new TextOffsetSpan(HeadingSpan.Start, HeadingSpan.End),
        HeadingText = HeadingText,
        MarkerComponents = MarkerComponents,
        LineIds = LineIds,
        Geometry = Geometry,
    };

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
        fact.MarkerComponents,
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
