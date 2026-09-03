using System.Reflection;
using System.Reflection.Emit;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M12-A3 locks. A run records the revision its binary was built from, and the binary is the
/// authority for that - not whoever launched it.
/// <para>
/// A missing revision is null, never invented and never inferred from a product version that merely
/// looks like one. An artifact with a null revision is still a valid artifact; it is simply not
/// eligible as release acceptance evidence, which is a documentation contract rather than a runtime
/// refusal.
/// </para>
/// </summary>
public sealed class BuildProvenanceTests
{
    [Fact]
    public void EmbeddedRevisionIsReadFromTheAssembly()
    {
        var assembly = AssemblyWith("1.4.0+abc1234def5678");

        Assert.Equal("abc1234def5678", BuildProvenance.SourceRevisionOf(assembly));
    }

    /// <summary>
    /// Only the segment after the plus is the revision. Returning the whole informational version
    /// would put a product version into a field whose contract says "commit".
    /// </summary>
    [Fact]
    public void ProductVersionWithoutARevisionSegmentYieldsNull()
    {
        Assert.Null(BuildProvenance.SourceRevisionOf(AssemblyWith("1.0.0")));
        Assert.Null(BuildProvenance.SourceRevisionOf(AssemblyWith("1.0.0+")));
        Assert.Null(BuildProvenance.SourceRevisionOf(AssemblyWith("   ")));
    }

    [Fact]
    public void EmbeddedRevisionTakesPrecedenceOverTheEnvironmentFallback()
    {
        var assembly = AssemblyWith("1.0.0+embedded0000");

        Assert.Equal("embedded0000",
            BuildProvenance.ResolveCodeRevision(assembly, "operator-supplied-value"));
    }

    [Fact]
    public void EnvironmentRevisionIsUsedOnlyWhenNothingIsEmbedded()
    {
        var assembly = AssemblyWith("1.0.0");

        Assert.Equal("from-environment",
            BuildProvenance.ResolveCodeRevision(assembly, "from-environment"));
    }

    [Fact]
    public void MissingRevisionStaysNullRatherThanInvented()
    {
        var assembly = AssemblyWith("1.0.0");

        Assert.Null(BuildProvenance.ResolveCodeRevision(assembly, null));
        Assert.Null(BuildProvenance.ResolveCodeRevision(assembly, "   "));
    }

    /// <summary>The same build must always report the same revision.</summary>
    [Fact]
    public void TheSameAssemblyReportsAStableRevision()
    {
        var assembly = AssemblyWith("2.1.0+stable9999");

        Assert.Equal(
            BuildProvenance.ResolveCodeRevision(assembly, null),
            BuildProvenance.ResolveCodeRevision(assembly, null));
    }

    /// <summary>
    /// The release-acceptance contract, locked where it belongs - at the acceptance boundary rather
    /// than in the pipeline. An ordinary run may carry a null revision; evidence offered for release
    /// may not.
    /// </summary>
    [Fact]
    public void ReleaseAcceptanceRejectsAnArtifactWithoutARevision()
    {
        Assert.False(IsReleaseEvidence(null));
        Assert.False(IsReleaseEvidence("  "));
        Assert.True(IsReleaseEvidence("047b16976c141c4139ff08c1a894b5cc6f41f3ae"));
    }

    private static bool IsReleaseEvidence(string? codeRevision) =>
        !string.IsNullOrWhiteSpace(codeRevision);

    private static Assembly AssemblyWith(string informationalVersion)
    {
        var name = new AssemblyName($"provenance-fixture-{Guid.NewGuid():N}");
        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run,
        [
            new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                [informationalVersion]),
        ]);
        return builder;
    }
}
