namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

/// <summary>
/// Which model-visible semantic request a run sends: its system prompt and the shape of its evidence.
/// <para>
/// A request is part of the contract the model answered under, so it is versioned rather than edited:
/// a run's artifacts name the version. Each lane sends exactly one version.
/// <see cref="Unspecified"/> is the zero value so that a default or deserialized value never selects
/// a version by accident; composing a request under it fails closed.
/// </para>
/// </summary>
internal enum SemanticRequestVersion
{
    Unspecified = 0,

    /// <summary>
    /// The DOCX lane: source facts only; no salience label of the harness's own,
    /// and no pre-interpreted location either - page, page band, recurrence and hyperlink anchors in
    /// place of the scope label and the contents flag (A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1).
    /// </summary>
    V2_ATTENTION_FREE = 2,

}

internal static class SemanticRequestVersions
{
    /// <summary>What the DOCX lane sends.</summary>
    public const SemanticRequestVersion Docx = SemanticRequestVersion.V2_ATTENTION_FREE;

    /// <summary>The version, or a refusal: an unknown version is never mapped to a known one.</summary>
    public static SemanticRequestVersion Require(SemanticRequestVersion version) => version switch
    {
        SemanticRequestVersion.V2_ATTENTION_FREE => version,
        _ => throw new InvalidOperationException($"SEMANTIC_REQUEST_VERSION_UNKNOWN:{version}"),
    };
}
