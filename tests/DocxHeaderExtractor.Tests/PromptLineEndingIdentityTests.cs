using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A prompt is bytes on the wire, so the same commit must send the same bytes on every machine.
/// <para>
/// A raw string literal keeps whatever line endings the source file has, and Git gives a Windows
/// checkout CRLF where a Linux one gets LF. The discovery prompt was therefore 2,330 characters in
/// one checkout and 2,301 in another, from identical code - which made every frozen prompt hash a
/// property of the machine that ran it, and meant two people could run what they believed was the
/// same experiment and send different requests.
/// </para>
/// <para>
/// A clean worktree found it; no suite did, because the payload hashes that were pinned are built
/// at runtime as JSON and were never affected.
/// </para>
/// </summary>
public sealed class PromptLineEndingIdentityTests
{
    [Fact]
    public void No_prompt_carries_a_carriage_return()
    {
        Assert.DoesNotContain('\r', CanonicalSemanticEngine.SystemPrompt);
        Assert.DoesNotContain('\r', HeadingParentResolver.PlacementPrompt);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\nb\nc")]
    [InlineData("a\rb\rc")]
    public void Every_line_ending_convention_normalizes_to_the_same_bytes(string input)
    {
        // Proves the property rather than pinning one platform's answer: whichever convention the
        // file arrives with, the prompt that leaves is identical.
        Assert.Equal("a\nb\nc", CanonicalSemanticEngine.NormalizePromptLineEndings(input));
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        var once = CanonicalSemanticEngine.NormalizePromptLineEndings(CanonicalSemanticEngine.SystemPrompt);

        Assert.Equal(CanonicalSemanticEngine.SystemPrompt, once);
        Assert.Equal(once, CanonicalSemanticEngine.NormalizePromptLineEndings(once));
    }

}
