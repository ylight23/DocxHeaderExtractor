using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.12 (offline): the two ways V6 can cut a document into segments. The default stays the fixed 48-line cut; the layout cut is the F1Q arm-B rule.</summary>
public sealed class P7F1V6SegmentationTests
{
    static P7F1V6Universe Universe(string folder, string file)
    {
        var rel = $"todo10_8/heading_corpus_100/{folder}/{file}";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        return P7F1V6Universe.Rebuild(TestRepository.Root(), rel, sha, out var why) ?? throw new InvalidOperationException(why);
    }

    [Theory]
    [InlineData("07_system_generated", "095_RFC9114_HTTP_3.pdf")]
    [InlineData("06_dich_song_ngu", "089_ND_195-2013_Luat_Xuat_ban_EN.pdf")]
    public void BothPolicies_PartitionTheDocument_AndNeverExceed48(string folder, string file)
    {
        var u = Universe(folder, file);
        Assert.Equal(P7F1V6Universe.FixedSegmentation, u.Segmentation);
        Assert.Equal(u.Lines.Count, u.LayoutBlocks.Count);
        foreach (var policy in new[] { P7F1V6Universe.FixedSegmentation, P7F1V6Universe.LayoutSegmentation, P7F1V6Universe.LayoutSegmentation96 })
        {
            var v = u.WithSegmentation(policy);
            var next = 0;
            foreach (var (a, b) in v.Cores) { Assert.Equal(next, a); Assert.InRange(b - a, 1, P7F1V6Universe.MaxCore(policy)); next = b; }
            Assert.Equal(u.Lines.Count, next);
            // Every line is decided in exactly one segment, whatever the policy.
            var decided = Enumerable.Range(0, v.Cores.Count).SelectMany(n => P7F1V6DirectCanary410.Scope(v, $"SEG-{n:000}").Decide).ToList();
            Assert.Equal(u.Aliases, decided);
        }
    }

    [Fact]
    public void FixedPolicy_IsTheOldCut_ByteForByte()
    {
        var u = Universe("07_system_generated", "095_RFC9114_HTTP_3.pdf");
        for (int n = 0; n < u.Cores.Count; n++)
        {
            var core = n * P7F1V6DirectCanary410.Unit;
            var end = Math.Min(u.Lines.Count, core + P7F1V6DirectCanary410.Unit);
            var from = Math.Max(0, core - P7F1V6DirectCanary410.Overlap);
            var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, $"SEG-{n:000}");
            Assert.Equal(Enumerable.Range(core, end - core).Select(i => u.Lines[i].Alias), decide);
            Assert.Equal(Enumerable.Range(from, Math.Min(u.Lines.Count, end + P7F1V6DirectCanary410.Overlap) - from), ctx);
        }
    }

    [Fact]
    public void LayoutPolicy_NeverCutsInsideALayoutBlock_AndCutsInsideAPageOnlyWhenThePageIsTooBig()
    {
        var u = Universe("07_system_generated", "095_RFC9114_HTTP_3.pdf").WithSegmentation(P7F1V6Universe.LayoutSegmentation);
        for (int k = 1; k < u.Cores.Count; k++)
        {
            var b = u.Cores[k].Start;
            Assert.NotEqual(u.LayoutBlocks[b - 1], u.LayoutBlocks[b]);
            if (u.Lines[b - 1].Page == u.Lines[b].Page)
            {
                var page = u.Lines[b].Page;
                Assert.True(u.Lines.Count(l => l.Page == page) > P7F1V6DirectCanary410.Unit, "a boundary inside a page needs a page over the bound");
            }
        }
    }

    [Fact]
    public void LayoutPolicy_IsDeterministic_AndAnUnknownPolicyIsRefused()
    {
        var u = Universe("06_dich_song_ngu", "089_ND_195-2013_Luat_Xuat_ban_EN.pdf");
        var a = u.WithSegmentation(P7F1V6Universe.LayoutSegmentation).Cores;
        var b = u.WithSegmentation(P7F1V6Universe.LayoutSegmentation).Cores;
        Assert.Equal(a, b);
        Assert.Throws<ArgumentException>(() => u.WithSegmentation("SOMETHING_ELSE"));
    }

    [Fact]
    public void Layout96_NeverCutsInsideABlock_AndIsNoFinerThanLayout48()
    {
        var u = Universe("07_system_generated", "095_RFC9114_HTTP_3.pdf");
        var l48 = u.WithSegmentation(P7F1V6Universe.LayoutSegmentation);
        var l96 = u.WithSegmentation(P7F1V6Universe.LayoutSegmentation96);
        Assert.True(l96.Cores.Count <= l48.Cores.Count);
        for (int k = 1; k < l96.Cores.Count; k++) Assert.NotEqual(l96.LayoutBlocks[l96.Cores[k].Start - 1], l96.LayoutBlocks[l96.Cores[k].Start]);
        Assert.Contains(l96.Cores, c => c.End - c.Start > P7F1V6DirectCanary410.Unit);
    }

    [Fact]
    public void JsonObjectAllTurns_AddsResponseFormatOnToolTurns_OnlyWhenAsked()
    {
        var body = new System.Text.Json.Nodes.JsonArray();
        var auto = System.Text.Json.JsonDocument.Parse(P7F1V6DirectAgent.Build("S", "{}", body, "auto").Body).RootElement;
        var autoJson = System.Text.Json.JsonDocument.Parse(P7F1V6DirectAgent.Build("S", "{}", body, "auto", jsonObjectAllTurns: true).Body).RootElement;
        var none = System.Text.Json.JsonDocument.Parse(P7F1V6DirectAgent.Build("S", "{}", body, "none").Body).RootElement;
        Assert.False(auto.TryGetProperty("response_format", out _));
        Assert.Equal("json_object", autoJson.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(P7F1V6DirectAgent.Build("S", "{}", body, "none").Body, P7F1V6DirectAgent.Build("S", "{}", body, "none", jsonObjectAllTurns: true).Body);
        Assert.Equal("json_object", none.GetProperty("response_format").GetProperty("type").GetString());
    }

    // ---- D4.12 layout facts ----

    [Fact]
    public void LayoutFacts_CoverEveryLine_AreMeasurementsOnly_AndNameNoKindOfLine()
    {
        var u = Universe("07_system_generated", "095_RFC9114_HTTP_3.pdf");
        Assert.Equal(u.Lines.Count, u.LayoutFacts.Count);
        foreach (var l in u.Lines)
        {
            var f = u.LayoutFacts[l.Alias];
            Assert.Subset(new HashSet<string> { "alias", "block", "segments", "leftEdgeSharedBy" }, f.Select(kv => kv.Key).ToHashSet());
            Assert.True(f.ContainsKey("block") && f.ContainsKey("segments"));
            Assert.InRange((int)f["block"]!["place"]!, 1, (int)f["block"]!["lines"]!);
            Assert.InRange((int)f["segments"]!["index"]!, 1, (int)f["segments"]!["count"]!);
        }
        var text = string.Join(" ", u.LayoutFacts.Values.Select(v => v.ToJsonString()));
        var sentence = P7F1V6DirectAgent.GeometryPromptRange.Split((char)10).First(x => x.StartsWith("Layout facts"));
        foreach (var banned in new[] { "table", "heading", "header", "footer", "title", "column", "cell" })
            Assert.DoesNotContain(banned, text + P7F1V6LayoutFacts.Description + sentence, StringComparison.OrdinalIgnoreCase);
        // The block numbers are the reading-order ordinals of the parser's block ids, and the sizes add up to the line count.
        var blocks = u.Lines.Select((l, i) => (int)u.LayoutFacts[l.Alias]["block"]!["number"]!).Distinct().Count();
        Assert.Equal(u.LayoutBlocks.Distinct().Count(), blocks);
    }

    [Fact]
    public void GeometryArm_AddsLayoutFacts_ToPrefetchObservations_AndLeavesTheOtherArmsUnchanged()
    {
        var u = Universe("07_system_generated", "095_RFC9114_HTTP_3.pdf");
        var control = P7F1V6DirectRepeatCanary.FirstRequest(P7F1V6DirectAgent.Arm.Auto, u, "SEG-001");
        var prefetch = P7F1V6DirectRepeatCanary.FirstRequest(P7F1V6DirectAgent.Arm.Prefetch, u, "SEG-001");
        var geometry = P7F1V6DirectRepeatCanary.FirstRequest(P7F1V6DirectAgent.Arm.Geometry, u, "SEG-001");
        Assert.DoesNotContain("layoutFacts", control.Body);
        Assert.DoesNotContain("layoutFacts", prefetch.Body);
        Assert.Contains("layoutFacts", geometry.Body);
        // The earlier arms' prompts and payloads are what they were.
        Assert.Equal(P7F1V6DirectAgent.AutoPromptRangeSha256, P7F1V6DirectAgent.Sha(P7F1V6DirectAgent.AutoPromptRange));
        Assert.Equal(P7F1V6DirectAgent.PrefetchPromptRangeSha256, P7F1V6DirectAgent.Sha(P7F1V6DirectAgent.PrefetchPromptRange));
        Assert.Contains("Layout facts:", P7F1V6DirectAgent.GeometryPromptRange);
        Assert.DoesNotContain("Layout facts:", P7F1V6DirectAgent.PrefetchPromptRange);
        // Every shown line has facts, and none are about lines that are not shown.
        using var body = System.Text.Json.JsonDocument.Parse(geometry.Body);
        using var payload = System.Text.Json.JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var shown = payload.RootElement.GetProperty("targets").EnumerateArray().Concat(payload.RootElement.GetProperty("context").EnumerateArray())
            .Select(x => x.GetProperty("alias").GetString()).ToList();
        var facts = payload.RootElement.GetProperty("observations").GetProperty("layoutFacts").GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("alias").GetString()).ToList();
        Assert.Equal(shown.OrderBy(x => x, StringComparer.Ordinal), facts.OrderBy(x => x, StringComparer.Ordinal));
    }
}
