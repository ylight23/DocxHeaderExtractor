using System.Net;
using System.Text.Json;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

/// <summary>The reading-accuracy measurement: the gold file, the comparison, and a run over 023 with a scripted vision model. No network.</summary>
[Trait("Category", "Slow")]
public sealed class ScanTextEvaluationTests : IDisposable
{
    const string Docx = "todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx";
    const string Gold = "eval/scan-text/023_heading_lines.v1.json";
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-text-eval-" + Guid.NewGuid().ToString("N"));

    public ScanTextEvaluationTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_gold_of_023_holds_its_112_heading_lines_with_boxes()
    {
        var gold = ScanTextEvaluation.ReadGold(TestRepository.Path(Gold));

        Assert.Equal(112, gold.Count);
        Assert.Equal(Enumerable.Range(1, 112), gold.Select(g => g.N));
        Assert.Equal("Chương III", gold[57].Text);
        Assert.All(gold, g => Assert.True(g.Box.Width > 0 && g.Box.Height > 0 && g.Page is >= 1 and <= 34));
    }

    [Fact]
    public void Lines_are_compared_after_nfc_and_white_space_only()
    {
        var decomposed = "Sổ gốc";   // "Sổ gốc" with combining marks
        Assert.Equal("Sổ gốc", ScanTextEvaluation.Norm(" " + decomposed + "  "));
        Assert.Equal(1, ScanTextEvaluation.Distance("sổ", "số"));
        Assert.Equal(1, ScanTextEvaluation.Distance("Chương III", "Chương II"));
        var lines = new[]
        {
            new ScanTextEvaluation.LineResult(1, 1, "Chương III", new Dictionary<string, string?> { ["a"] = "Chương  III", ["b"] = "Chương II" }),
            new ScanTextEvaluation.LineResult(2, 1, "Sổ", new Dictionary<string, string?> { ["a"] = "Số", ["b"] = null }),
        };
        var a = ScanTextEvaluation.Score("a", lines);
        var b = ScanTextEvaluation.Score("b", lines);
        Assert.Equal((1, 2), (a.Exact, a.Lines));
        Assert.Equal(1.0 / 12, a.CharacterErrorRate, 6);
        Assert.Equal(0, b.Exact);
        Assert.Equal(3.0 / 12, b.CharacterErrorRate, 6);   // a missing reading counts every gold letter as an error
    }

    [Fact]
    public void A_reply_keeps_only_the_ids_asked_once()
    {
        var reply = JsonSerializer.Serialize(new
        {
            protocolVersion = ScanTextEvaluation.PromptVersion,
            lines = new object[] { new { id = "T001", text = "Mục 1" }, new { id = "T001", text = "again" }, new { id = "T009", text = "not asked" } },
        });
        var parsed = ScanTextEvaluation.Parse(reply, ["T001", "T002"], out var problem);
        Assert.Null(problem);
        Assert.Equal(new Dictionary<string, string> { ["T001"] = "Mục 1" }, parsed);
        Assert.Null(ScanTextEvaluation.Parse("{}", ["T001"], out problem));
        Assert.Equal("PROTOCOL_VERSION_INVALID", problem);
    }

    [Fact]
    public void A_run_reads_every_gold_line_from_both_views_and_transcribes_the_crops_blind_once()
    {
        var path = TestRepository.Path(Docx);
        var tesseract = TesseractOcr.FindTesseract();
        if (tesseract is null || TesseractOcr.FindImageMagick() is null || !File.Exists(path) || !TesseractOcr.Languages(tesseract).Contains("vie")) return;
        var gold = ScanTextEvaluation.ReadGold(TestRepository.Path(Gold)).Where(g => g.Page == 1).ToArray();
        // The scripted model transcribes every crop as its gold text, and must never be shown an OCR reading.
        var handler = new FakeOpenRouter((body, post) =>
        {
            var content = body["messages"]![1]!["content"]!.AsArray();
            Assert.Equal(ScanTextEvaluation.SystemPrompt, body["messages"]![0]!["content"]!.Text());
            var ids = content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()["id ".Length..]).ToArray();
            Assert.Equal(ids.Length, content.Count(c => c!["type"]!.GetValue<string>() == "image_url"));
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
            {
                content = JsonSerializer.Serialize(new
                {
                    protocolVersion = ScanTextEvaluation.PromptVersion,
                    lines = ids.Select(id => new { id, text = gold[int.Parse(id[1..]) - 1].Text }),
                }),
            }, "stop"));
        });
        var evaluation = new ScanTextEvaluation(Path.Combine(dir, "Kho đánh giá"), () => handler);
        var options = new ScanOcrPreflight.Options(MaxPages: 1, Parallelism: 2);

        var report = evaluation.Run(path, gold, "sk-test", ocrOptions: options);

        Assert.Empty(report.Failures);
        var score = report.Scores.ToDictionary(s => s.Method);
        Assert.Equal(gold.Length, score[ScanTextEvaluation.VlmBilevel].Exact);
        Assert.Equal(gold.Length, score[ScanTextEvaluation.VlmEnlarged].Exact);
        Assert.Equal(gold.Length, score[ScanTextEvaluation.VlmEnlargedBilevel].Exact);
        Assert.Equal(gold.Length, score[ScanTextEvaluation.VlmEnlargedBilevelMasked].Exact);
        // Same pixels as the grey 2x crops, far fewer bytes.
        Assert.True(report.CropBytes[ScanTextEvaluation.VlmEnlargedBilevel] < report.CropBytes[ScanTextEvaluation.VlmEnlarged]);
        Assert.True(score[ScanTextEvaluation.Original].Exact >= 1);
        Assert.True(score[ScanTextEvaluation.BestView].Exact >= Math.Max(score[ScanTextEvaluation.Original].Exact, score[ScanTextEvaluation.Upscaled].Exact));
        Assert.All(report.Lines, l => Assert.NotNull(l.Readings[ScanTextEvaluation.Original]));
        var posts = handler.Posts;
        // At least one request per crop kind; enlarged crops are larger, so they may be split to stay under the transport's byte cap.
        Assert.InRange(posts, 7, 7 * gold.Length);
        Assert.Contains(handler.Bodies, b => b["reasoning"]!["max_tokens"]?.GetValue<int>() == 1024);
        Assert.Contains(handler.Bodies, b => b["reasoning"]!["enabled"]!.GetValue<bool>() == false);
        Assert.Equal(gold.Length, score[ScanTextEvaluation.VlmNoReasoning].Exact);
        Assert.All(handler.Bodies, b => Assert.True(System.Text.Encoding.UTF8.GetByteCount(b.ToJsonString()) <= DocxHeaderExtractor.HeadingAgent.AgentBudget.MaxRequestBytes + 4096));

        var again = evaluation.Run(path, gold, "sk-test", ocrOptions: options);

        Assert.Equal(posts, handler.Posts);   // read back from the store
        Assert.Equal(report.Scores, again.Scores);
        Assert.True(File.Exists(Path.Combine(dir, "Kho đánh giá", "eval", report.SourceSha256[..16] + "-scan-text-eval.json")));
    }

    /// <summary>The mean grey (0 black, 1 white) of a band of rows of a PNG, by ImageMagick.</summary>
    static double Mean(string magick, string png, int top, int rows)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(magick) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (Path.GetFileNameWithoutExtension(magick) == "magick" ? new[] { "convert" } : []).Concat([png, "-crop", $"10000x{rows}+0+{top}", "+repage", "-format", "%[fx:mean]", "info:"]))
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return double.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void A_masked_crop_has_no_part_of_the_line_above_and_keeps_all_of_its_own()
    {
        var magick = TesseractOcr.FindImageMagick();
        if (magick is null) return;
        // The line above ends 4 px above the line's box: inside the 8 px margin. Its box also overlaps the line's own box by 2 px.
        var page = Path.Combine(dir, "page.png");
        File.WriteAllBytes(page, TestPng.WithBlack(400, 200, 255, (100, 60, 120, 36), (100, 100, 200, 30)));
        var own = new PixelBox(100, 100, 200, 30);
        var mask = TesseractOcr.BoxesInCrop(own, 8, [new PixelBox(100, 60, 120, 42), own, new PixelBox(100, 170, 50, 20)]);
        Assert.Equal([new PixelBox(100, 60, 120, 42)], mask);

        var plain = Path.Combine(dir, "plain.png");
        var masked = Path.Combine(dir, "masked.png");
        Assert.True(TesseractOcr.CropScaled(magick, page, plain, own, 8, 2.0, bilevel: true));
        Assert.True(TesseractOcr.CropScaled(magick, page, masked, own, 8, 2.0, bilevel: true, mask: mask));

        // The top margin (8 px, 16 at 2x): black from the line above in the plain crop, white in the masked one.
        Assert.True(Mean(magick, plain, 0, 8) < 0.9);
        Assert.Equal(1.0, Mean(magick, masked, 0, 16), 3);
        // The line's own box (rows 16 to 76 at 2x) is untouched.
        Assert.Equal(Mean(magick, plain, 16, 60), Mean(magick, masked, 16, 60), 3);
    }
}
