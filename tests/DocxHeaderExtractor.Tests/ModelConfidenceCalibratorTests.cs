using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class ModelConfidenceCalibratorTests
{
    [Fact]
    public void One_model_pass_is_80_percent()
    {
        Assert.Equal(0.80, ModelConfidenceCalibrator.FromPasses(
            builtInStyle: false, twoPass: false, passA: true, passB: false));
    }

    [Fact]
    public void Two_agreeing_passes_are_85_percent()
    {
        Assert.Equal(0.85, ModelConfidenceCalibrator.FromPasses(
            builtInStyle: false, twoPass: true, passA: true, passB: true));
    }

    [Fact]
    public void Two_disagreeing_passes_stay_at_75_percent()
    {
        Assert.Equal(0.75, ModelConfidenceCalibrator.FromPasses(
            builtInStyle: false, twoPass: true, passA: true, passB: false));
    }

    [Fact]
    public void Critic_confirmation_is_85_percent()
    {
        Assert.Equal(0.85, ModelConfidenceCalibrator.CriticConfirmed);
    }
}
