using DocxHeaderExtractor.DocumentProcessing.Evaluation;

namespace DocxHeaderExtractor.Tests;

public sealed class GoldScoreTests
{
    static string WriteGold(string root, string sha, params string[] sourceIds)
    {
        Directory.CreateDirectory(root);
        var claims = string.Join(",", sourceIds.Select(id => $"{{\"sourceId\":\"{id}\"}}"));
        File.WriteAllText(Path.Combine(root, "DOC-TEST.gold.v1.json"),
            $"{{\"source\":{{\"sourceSha256\":\"{sha}\"}},\"occurrence\":{{\"claims\":[{claims}]}}}}");
        return root;
    }

    [Fact]
    public void Scores_true_false_positives_and_negatives_with_precision_recall_and_f1()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-gold-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteGold(root, "abc", "body[1]/p[1]", "body[1]/p[2]", "body[1]/p[3]", "body[1]/p[4]");
            var score = GoldHeadingScorer.Evaluate(root, "abc", ["body[1]/p[1]", "body[1]/p[2]", "body[1]/p[3]", "body[1]/p[9]"])!;

            Assert.Equal("DOC-TEST.gold.v1.json", score.GoldDocument);
            Assert.Equal(3, score.TruePositive);
            Assert.Equal(1, score.FalsePositive);
            Assert.Equal(1, score.FalseNegative);
            Assert.Equal(0.75, score.Precision!.Value, 6);
            Assert.Equal(0.75, score.Recall!.Value, 6);
            Assert.Equal(0.75, score.F1!.Value, 6);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Returns_null_without_a_root_or_a_matching_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-gold-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteGold(root, "abc", "body[1]/p[1]");
            Assert.Null(GoldHeadingScorer.Evaluate(null, "abc", ["body[1]/p[1]"]));
            Assert.Null(GoldHeadingScorer.Evaluate(root, "other-sha", ["body[1]/p[1]"]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
