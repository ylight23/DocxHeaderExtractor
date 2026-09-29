namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The model-visible request a lane sends. The experiment arms that used to live here - structural
/// ancestors, partial span, masthead metadata, membership before placement - were measured and are
/// gone; what remains is the request version, which the lane sets.
/// </summary>
internal sealed record CanonicalSemanticExperiment
{
    /// <summary>The request with no arm: what every lane sends.</summary>
    public static readonly CanonicalSemanticExperiment Baseline = new();

    /// <summary>The model-visible request version; the lane sets it.</summary>
    public SemanticRequestVersion RequestVersion { get; init; } = SemanticRequestVersions.Docx;
}
