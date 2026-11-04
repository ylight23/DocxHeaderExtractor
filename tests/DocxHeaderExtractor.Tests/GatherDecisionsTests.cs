using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>The reviewer's decisions on uncertain sections: merge into a chosen heading, add a new heading, skip, or leave undecided (not written).</summary>
public sealed class GatherDecisionsTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord H(int index, string text, int level) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    // Target: CTXH > "Công tác tiếp nhận và hỗ trợ từ thiện" (already has "Tiếp đón 66 lượt."), then "Tổng đài:".
    // Source: CTXH > "Tiếp nhận và hỗ trợ từ thiện" (near name) and "Hoạt động phối hợp" (near "Các hoạt động phối hợp khác").
    static (GatherHeadingPlan Plan, string Target, string Source) Build()
    {
        var target = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Công tác tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 66 lượt.")
            + P("Các hoạt động phối hợp khác") + P("Phối hợp trao quà.") + P("Tổng đài:") + P("Gọi 100 cuộc."));
        var source = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 66 lượt.") + P("Tiếp đón 50 lượt.")
            + P("Hoạt động phối hợp") + P("Khám sức khỏe học đường."));
        var plan = GatherByHeading.Build(target,
            [H(0, "Công tác xã hội:", 1), H(1, "Công tác tiếp nhận và hỗ trợ từ thiện", 2), H(3, "Các hoạt động phối hợp khác", 2), H(5, "Tổng đài:", 1)],
            [new GatherSourceFile("T8", source, [H(0, "Công tác xã hội:", 1), H(1, "Tiếp nhận và hỗ trợ từ thiện", 2), H(4, "Hoạt động phối hợp", 2)])],
            addMissingHeadings: true);
        return (plan, target, source);
    }

    [Fact]
    public void Merge_writes_into_the_chosen_heading_without_the_lines_it_already_has_and_new_goes_to_the_end_of_the_parent()
    {
        var (plan, target, source) = Build();
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            Assert.Equal(["Tiếp nhận và hỗ trợ từ thiện", "Hoạt động phối hợp"], plan.Uncertain.Select(u => u.SourceHeading));
            Assert.Equal("Công tác xã hội:", plan.Uncertain[1].NewHeadingParent);

            var result = GatherDecisions.Apply(GatherByHeading.DecidedByChain(plan.Sections), plan.Uncertain,
                [new GatherDecision(0, "MERGE", 0), new GatherDecision(1, "NEW")]);

            var merge = result.Outcomes.Single(o => o.Index == 0);
            Assert.Equal(1, merge.Written);
            Assert.Equal(["Tiếp đón 66 lượt."], merge.AlreadyThere);
            var merged = result.Sections.Single(s => s.Heading == "Công tác tiếp nhận và hỗ trợ từ thiện");
            var fact = Assert.Single(merged.Run.Facts);
            Assert.Equal("Tiếp đón 50 lượt.", fact.Render());
            Assert.Contains(fact.Flags, f => f.StartsWith("USER_DECISION: gộp từ", StringComparison.Ordinal));
            Assert.Empty(result.Undecided);

            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
            Assert.Empty(GatherWriteback.Apply(target, output, result.Sections.Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList(),
                DateTime.UtcNow, true, hash).SkippedHeadings);
            using var document = WordprocessingDocument.Open(output, false);
            var text = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            int At(string start) => text.FindIndex(t => t.StartsWith(start, StringComparison.Ordinal));
            Assert.True(At("- Tiếp đón 50 lượt.") > At("Tiếp đón 66 lượt.") && At("- Tiếp đón 50 lượt.") < At("Các hoạt động phối hợp khác"));
            // The new heading closes the CTXH subtree: after its last child's content, before "Tổng đài:".
            Assert.True(At("Hoạt động phối hợp") > At("Phối hợp trao quà.") && At("Hoạt động phối hợp") < At("Tổng đài:"));
            Assert.Equal(At("Hoạt động phối hợp") + 1, At("- Khám sức khỏe học đường."));
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }

    [Fact]
    public void Skipped_and_undecided_sections_are_not_written_and_bad_decisions_are_reported()
    {
        var (plan, target, source) = Build();
        try
        {
            var decided = GatherByHeading.DecidedByChain(plan.Sections);

            var result = GatherDecisions.Apply(decided, plan.Uncertain,
                [new GatherDecision(0, "SKIP"), new GatherDecision(7, "NEW"), new GatherDecision(0, "NEW")]);

            Assert.Equal(decided.Count, result.Sections.Count);
            Assert.All(result.Sections, s => Assert.Empty(s.Run.Facts));
            Assert.Equal([1], result.Undecided);
            Assert.Equal(2, result.Outcomes.Count(o => o.Error == "INVALID_INDEX"));
            Assert.Equal("SKIP", Assert.Single(result.Outcomes, o => o.Error is null).Decision);

            var badCandidate = GatherDecisions.Apply(decided, plan.Uncertain, [new GatherDecision(0, "MERGE", 9)]);
            Assert.Equal("INVALID_CANDIDATE", Assert.Single(badCandidate.Outcomes).Error);
        }
        finally { File.Delete(target); File.Delete(source); }
    }
}
