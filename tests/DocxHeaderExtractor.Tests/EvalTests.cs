using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using Xunit;

namespace DocxHeaderExtractor.Tests;

public class AnswerKeyTests
{
    [Fact]
    public void Parses_index_with_and_without_level()
    {
        var key = AnswerKey.Parse("""
            # chú thích bị bỏ qua
            95 1
            96          # không ghi cấp
            101,2
            105:3       # dấu phân cách nào cũng được
            108 1; 109 2
            """);

        Assert.Equal(6, key.Count);
        Assert.Equal(1, key.LevelOf(95));
        Assert.Null(key.LevelOf(96));
        Assert.Equal(2, key.LevelOf(101));
        Assert.Equal(3, key.LevelOf(105));
        Assert.Equal(2, key.LevelOf(109));

        // Chỉ dòng có ghi cấp mới được đem đi chấm cấp.
        Assert.DoesNotContain(96, key.IndexesWithLevel);
    }

    [Fact]
    public void Rejects_garbage_instead_of_silently_dropping_it()
    {
        // Bỏ qua âm thầm sẽ làm đáp án ngắn đi mà không ai biết ⇒ mọi chỉ số đều sai.
        Assert.Throws<FormatException>(() => AnswerKey.Parse("không-phải-số 1"));
    }

    [Fact]
    public void Round_trips_through_write()
    {
        var text = AnswerKey.Write([(7, 1, "PHỤ LỤC A"), (2, 2, "1.1. Phạm vi")], "thử");
        var key = AnswerKey.Parse(text);

        Assert.Equal(2, key.Count);
        Assert.Equal(1, key.LevelOf(7));
        Assert.Equal(2, key.LevelOf(2));
    }
}

public class EvaluatorTests
{
    private static DocumentOutline Outline(params (int Index, int Level)[] headings) => new()
    {
        File = "t.docx",
        ParagraphCount = 100,
        CandidateCount = 10,
        Headings = [.. headings.Select(h => new HeadingRecord
        {
            Index = h.Index,
            Level = h.Level,
            Text = "h" + h.Index,
        })],
    };

    [Fact]
    public void Counts_false_positives_negatives_and_wrong_levels_separately()
    {
        var key = AnswerKey.Parse("1 1\n2 2\n3 3");
        var outline = Outline((1, 1), (2, 9), (4, 1));   // 2 sai cấp, 4 thừa, 3 thiếu

        var s = Evaluator.Score("t", outline, [1, 2, 3, 4], key);

        Assert.Equal([4], s.FalsePositives);
        Assert.Equal([3], s.FalseNegatives);
        Assert.Equal([(2, 9, 2)], s.WrongLevels);

        Assert.Equal(2.0 / 3, s.Precision, 6);   // 1 và 2 nằm trong đáp án
        Assert.Equal(2.0 / 3, s.Recall, 6);
        Assert.Equal(0.5, s.LevelAccuracy, 6);   // chấm 2 dòng, đúng 1
    }

    [Fact]
    public void Candidate_recall_shows_what_the_ooxml_layer_dropped()
    {
        var key = AnswerKey.Parse("1 1\n2 1\n3 1\n4 1");
        var outline = Outline((1, 1), (2, 1));

        // Tầng OpenXML chỉ giữ 1,2,3 ⇒ số 4 mất từ trước khi mô hình được nhìn thấy.
        var s = Evaluator.Score("t", outline, [1, 2, 3], key);

        Assert.Equal(0.75, s.CandidateRecall, 6);
        Assert.Equal(0.5, s.Recall, 6);
    }

    [Fact]
    public void Suite_micro_average_weights_by_paragraph_not_by_document()
    {
        var big = Evaluator.Score("big", Outline([.. Enumerable.Range(1, 10).Select(i => (i, 1))]),
            [.. Enumerable.Range(1, 10)],
            AnswerKey.Parse(string.Join("\n", Enumerable.Range(1, 10).Select(i => $"{i} 1"))));

        var small = Evaluator.Score("small", Outline((1, 1)), [1], AnswerKey.Parse("2 1"));

        var suite = new SuiteScore([big, small]);

        Assert.Equal(10.0 / 11, suite.MicroPrecision, 6);   // gộp đoạn: 10 đúng / 11 trả về
        Assert.Equal(0.5, suite.MacroF1, 6);                // trung bình tài liệu: (1 + 0) / 2
        Assert.Equal(1, suite.Perfect);
    }
}

public class BenchDocumentTests
{
    /// <summary>
    /// Giá trị của bộ test nằm ở chỗ đáp án khớp với file. Sinh ra rồi đọc lại bằng chính
    /// bộ đọc thật để chắc chắn mọi chỉ số trong .key đều trỏ đúng đoạn.
    /// </summary>
    [Fact]
    public void Generated_keys_point_at_the_paragraphs_they_claim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhx-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var doc in BenchDocumentFactory.All())
            {
                var path = BenchDocumentFactory.Write(doc, dir);
                var key = AnswerKey.Load(Path.ChangeExtension(path, ".key"));
                var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

                var expected = doc.Paragraphs.Where(p => p.Level is not null).ToList();
                Assert.Equal(expected.Count, key.Count);

                foreach (var index in key.Indexes)
                {
                    var p = slim.ByIndex(index);
                    Assert.NotNull(p);
                    Assert.Contains(expected, e => e.Text == p!.Text && e.Level == key.LevelOf(index));
                }
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Tầng OpenXML được phép giữ thừa (mô hình sẽ lọc), nhưng KHÔNG được đánh rơi:
    /// đã rơi thì không tầng nào phía sau cứu lại được.
    /// </summary>
    [Fact]
    public void No_true_heading_is_lost_before_the_model_sees_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhx-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var doc in BenchDocumentFactory.All())
            {
                var path = BenchDocumentFactory.Write(doc, dir);
                var key = AnswerKey.Load(Path.ChangeExtension(path, ".key"));

                var slim = new DocxSlimExtractor(new ExtractionOptions { UseLexicalRules = false }).Extract(path);
                var candidates = slim.Candidates.Select(p => p.Index).ToHashSet();

                var lost = key.Indexes.Where(i => !candidates.Contains(i)).ToList();
                Assert.True(lost.Count == 0,
                    $"{doc.Name}: tầng ứng viên đánh rơi các đoạn {string.Join(", ", lost)}");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
