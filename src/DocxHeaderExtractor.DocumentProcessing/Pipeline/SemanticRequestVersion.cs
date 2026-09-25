namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Which model-visible semantic request a run sends: its system prompt and the shape of its evidence.
/// <para>
/// A request is part of the contract the model answered under, so it is versioned rather than edited:
/// a run's artifacts name the version, and a historical run replays under the version it was sent in.
/// <see cref="Unspecified"/> is the zero value so that a default or deserialized value never selects
/// a version by accident; composing a request under it fails closed.
/// </para>
/// </summary>
internal enum SemanticRequestVersion
{
    Unspecified = 0,

    /// <summary>
    /// Before REMOVE_MODEL_VISIBLE_ATTENTION_V1: evidence carried the harness's candidate heuristic as
    /// an "attention" flag. Historical replay only - see <see cref="HistoricalContracts.AttentionLegacyV1"/>.
    /// </summary>
    V1_ATTENTION_LEGACY = 1,

    /// <summary>
    /// Production: source facts only; no attention, candidate or salience label of the harness's own,
    /// and no pre-interpreted location either - page, page band, recurrence and hyperlink anchors in
    /// place of the scope label and the contents flag (A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1).
    /// </summary>
    V2_ATTENTION_FREE = 2,
}

internal static class SemanticRequestVersions
{
    /// <summary>What a run sends unless a historical profile explicitly selects otherwise.</summary>
    public const SemanticRequestVersion ProductionDefault = SemanticRequestVersion.V2_ATTENTION_FREE;

    /// <summary>The version, or a refusal: an unknown version is never mapped to a known one.</summary>
    public static SemanticRequestVersion Require(SemanticRequestVersion version) => version switch
    {
        SemanticRequestVersion.V1_ATTENTION_LEGACY or SemanticRequestVersion.V2_ATTENTION_FREE => version,
        _ => throw new InvalidOperationException($"SEMANTIC_REQUEST_VERSION_UNKNOWN:{version}"),
    };
}
