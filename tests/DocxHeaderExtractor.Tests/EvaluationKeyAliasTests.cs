using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.1e-0b locks. A versioned evaluation key must resolve to the document it grades, whatever the
/// generation is called, and aliasing must never be the thing that picks between two generations.
/// </summary>
public sealed class EvaluationKeyAliasTests
{
    [Theory]
    [InlineData("054_IBRD.v2-regenerated-docx", "054_IBRD")]
    [InlineData("054_IBRD.v3-occurrence-reviewed", "054_IBRD")]
    [InlineData("076_ICP.v11-some-future-review-name", "076_ICP")]
    public void AnyVersionedLabelResolvesToItsSourceStem(string keyStem, string expected)
    {
        Assert.True(EvaluationKeyAlias.TryGetSourceStem(keyStem, out var sourceStem));
        Assert.Equal(expected, sourceStem);
    }

    /// <summary>
    /// Two generations of the same document alias to one stem on purpose. The resolver reports both
    /// so the caller fails on the ambiguity; if it picked one, a superseded gold could be measured
    /// silently, which is exactly how the previous 054 generation kept being used after it was
    /// replaced.
    /// </summary>
    [Fact]
    public void TwoGenerationsOfOneDocumentShareTheSameSourceStem()
    {
        Assert.True(EvaluationKeyAlias.TryGetSourceStem("054_IBRD.v2-regenerated-docx", out var older));
        Assert.True(EvaluationKeyAlias.TryGetSourceStem("054_IBRD.v3-occurrence-reviewed", out var newer));

        Assert.Equal(older, newer);
    }

    [Theory]
    [InlineData("054_IBRD")]
    [InlineData("054_IBRD.v2")]
    [InlineData("054_IBRD.v-regenerated-docx")]
    [InlineData("054_IBRD.vX-regenerated-docx")]
    [InlineData("054_IBRD.v2-")]
    [InlineData(".v2-regenerated-docx")]
    [InlineData("")]
    public void UnversionedOrMalformedStemsDoNotAlias(string keyStem)
    {
        Assert.False(EvaluationKeyAlias.TryGetSourceStem(keyStem, out var sourceStem));
        Assert.Equal("", sourceStem);
    }
}
