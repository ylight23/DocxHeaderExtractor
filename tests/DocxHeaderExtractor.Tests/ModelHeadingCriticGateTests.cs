using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class ModelHeadingCriticGateTests
{
    [Fact]
    public void Model_only_low_evidence_unnumbered_heading_is_sent_to_critic()
    {
        var paragraph = new SlimParagraph
        {
            Index = 11,
            Text = "Đơn vị A gửi đơn vị B",
            Score = 0.60,
            Role = ParagraphRole.HeadingCandidate,
        };
        var heading = new HeadingRecord
        {
            Index = 11,
            Text = paragraph.Text,
            Level = 1,
            Source = HeadingSource.Model,
            Confidence = 0.60,
        };

        Assert.True(ModelHeadingCriticGate.NeedsCritique(heading, paragraph));
    }

    [Theory]
    [InlineData("I. TÌNH HÌNH TRÊN KHÔNG")]
    [InlineData("2. Vùng biển miền Trung")]
    public void Valid_visible_numbering_is_not_weakened_by_low_score(string text)
    {
        var paragraph = new SlimParagraph { Index = 1, Text = text, Score = 0.60 };
        var heading = new HeadingRecord
        {
            Index = 1,
            Text = text,
            Level = 1,
            Source = HeadingSource.Model,
            Confidence = 0.60,
        };

        Assert.False(ModelHeadingCriticGate.NeedsCritique(heading, paragraph));
    }

    [Fact]
    public void Built_in_heading_style_is_not_sent_to_critic()
    {
        var paragraph = new SlimParagraph
        {
            Index = 1,
            Text = "Phạm vi áp dụng",
            Score = 0.60,
            HasBuiltInHeadingStyle = true,
        };
        var heading = new HeadingRecord
        {
            Index = 1,
            Text = paragraph.Text,
            Level = 1,
            Source = HeadingSource.Model,
            Confidence = 0.60,
        };

        Assert.False(ModelHeadingCriticGate.NeedsCritique(heading, paragraph));
    }

    [Fact]
    public void Weak_evidence_threshold_is_configurable()
    {
        var paragraph = new SlimParagraph
        {
            Index = 7,
            Text = "Kết quả thực hiện",
            Score = 0.68,
            Role = ParagraphRole.HeadingCandidate,
        };
        var heading = new HeadingRecord
        {
            Index = 7,
            Text = paragraph.Text,
            Level = 1,
            Source = HeadingSource.Model,
            Confidence = 0.68,
        };

        Assert.True(ModelHeadingCriticGate.NeedsCritique(heading, paragraph, weakEvidenceThreshold: 0.70));
        Assert.False(ModelHeadingCriticGate.NeedsCritique(heading, paragraph, weakEvidenceThreshold: 0.60));
    }
}
