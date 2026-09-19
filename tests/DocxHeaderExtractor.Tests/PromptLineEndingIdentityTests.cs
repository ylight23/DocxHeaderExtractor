using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

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
    /// <summary>The value from the LF checkout every freeze in this repository was taken on.</summary>
    private const string HistoricalSystemPromptSha256 =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";

    [Fact]
    public void No_prompt_carries_a_carriage_return()
    {
        Assert.DoesNotContain('\r', CanonicalSemanticEngine.SystemPrompt);
        Assert.DoesNotContain('\r', CanonicalSemanticEngine.PlacementPrompt);
        Assert.DoesNotContain('\r', CanonicalSemanticEngine.PartialSpanClause);
        Assert.DoesNotContain('\r',
            CanonicalSemanticEngine.SystemPromptFor(CanonicalSemanticExperiment.PartialSpanOnly));
    }

    [Fact]
    public void The_discovery_prompt_is_the_length_every_freeze_was_taken_at()
    {
        Assert.Equal(2301, CanonicalSemanticEngine.SystemPrompt.Length);
    }

    [Fact]
    public void The_discovery_prompt_hashes_to_its_historical_authority()
    {
        // Not "whatever this machine produces". The frozen arm comparisons name this value, and a
        // CRLF checkout used to produce dd858892... instead. The fix had to make every platform
        // converge on the baseline rather than accept a second one.
        Assert.Equal(HistoricalSystemPromptSha256, Sha256(CanonicalSemanticEngine.SystemPrompt));
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

    [Fact]
    public void The_partial_span_arm_is_also_stable_across_checkouts()
    {
        // I8 appends a second raw literal, which carried the same defect and would otherwise have
        // made only the B2 arm platform-dependent - the hardest kind of drift to notice.
        var withClause = CanonicalSemanticEngine.SystemPromptFor(CanonicalSemanticExperiment.PartialSpanOnly);

        Assert.StartsWith(CanonicalSemanticEngine.SystemPrompt, withClause, StringComparison.Ordinal);
        Assert.Equal(
            CanonicalSemanticEngine.SystemPrompt.Length + CanonicalSemanticEngine.PartialSpanClause.Length,
            withClause.Length);
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
