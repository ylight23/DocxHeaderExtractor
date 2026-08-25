using System.Reflection;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// What revision the running binary was built from, taken from the binary itself.
/// <para>
/// The authority for "which code ran" belongs to the build artifact, not to whoever launched it. An
/// operator-supplied value can be wrong - honestly or otherwise - and an artifact that trusted it
/// would be claiming a revision the binary never contained. So an embedded revision always wins, and
/// an environment value can only fill a gap.
/// </para>
/// <para>
/// The SDK writes <c>AssemblyInformationalVersion</c> as <c>{Version}+{SourceRevisionId}</c> when the
/// build can see the repository. Only the part after the <c>+</c> is the revision: the whole string
/// is a product version that happens to end in a SHA, and returning it would put "1.0.0+" into a
/// field that means "commit". A build made without repository access carries no <c>+</c> segment and
/// yields null rather than a guess.
/// </para>
/// </summary>
public static class BuildProvenance
{
    /// <summary>The source revision embedded in an assembly, or null when the build did not record one.</summary>
    public static string? SourceRevisionOf(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return null;

        var plus = informational.IndexOf('+');
        if (plus < 0 || plus == informational.Length - 1) return null;
        var revision = informational[(plus + 1)..].Trim();
        return revision.Length == 0 ? null : revision;
    }

    /// <summary>
    /// The revision to record for a run. The embedded value is authoritative; the environment is a
    /// fallback for builds that carry none, never an override for builds that do.
    /// </summary>
    public static string? ResolveCodeRevision(Assembly assembly, string? environmentRevision) =>
        SourceRevisionOf(assembly) ??
        (string.IsNullOrWhiteSpace(environmentRevision) ? null : environmentRevision.Trim());
}
