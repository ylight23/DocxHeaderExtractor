namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Resolves the source-document stem a versioned evaluation key belongs to.
/// <para>
/// A versioned key keeps its source stem and adds <c>.v{N}-{label}</c>, where the label names what
/// the generation is: <c>-regenerated-docx</c> for a stable-id rebase, <c>-occurrence-reviewed</c>
/// for a reviewed occurrence repair, and whatever a later review calls itself. Recognising the shape
/// rather than one fixed label matters: a resolver that only accepts one name pushes people to
/// rename data so it resolves, and then the file name stops describing the file.
/// </para>
/// <para>
/// Aliasing deliberately does not choose between generations. Two keys that alias to the same stem
/// stay two matches so the caller reports an ambiguous key, because silently preferring the newest,
/// the first, or whatever the filesystem returns is how a superseded generation gets measured
/// without anyone noticing.
/// </para>
/// </summary>
public static class EvaluationKeyAlias
{
    public static bool TryGetSourceStem(string keyStem, out string sourceStem)
    {
        sourceStem = "";
        if (string.IsNullOrWhiteSpace(keyStem)) return false;

        var versionStart = keyStem.LastIndexOf(".v", StringComparison.OrdinalIgnoreCase);
        if (versionStart <= 0) return false;

        var suffix = keyStem[(versionStart + 2)..];
        var labelStart = suffix.IndexOf('-');
        if (labelStart <= 0 || labelStart == suffix.Length - 1) return false;
        if (!int.TryParse(suffix[..labelStart], out _)) return false;

        sourceStem = keyStem[..versionStart];
        return true;
    }
}
