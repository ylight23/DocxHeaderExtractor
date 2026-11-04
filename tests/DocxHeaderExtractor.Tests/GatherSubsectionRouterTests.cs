using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Tests;

public sealed class GatherSubsectionRouterTests
{
    static SourcedPart Part(string location, string role, string text) => new("T9|" + location, "T9", location, role, text, 0, text.Length);
    static GatheredFact Fact(string label, string body) => new([Part("p[89]", "label", label), Part("p[90]", "text", body)], []);

    [Fact]
    public void Removes_only_a_uniquely_matched_heading_label_before_comparing_direct_content()
    {
        var fact = Fact("Duy trì hoạt động", "Đã hỗ trợ người bệnh.");
        var target = new GatherSubsectionTarget("Duy trì hoạt động", "p[75]", "p[80]", ["Đã hỗ trợ người bệnh."]);
        var result = GatherSubsectionRouter.Route([fact], [target]);
        Assert.Empty(result.ByLocation["p[75]"]);
        var duplicate = Assert.Single(result.AlreadyInTarget);
        Assert.Equal(fact, duplicate.SourceFact);
        Assert.Equal("T9", duplicate.SourceFact.Parts[1].SourceId);
        Assert.Equal("Đã hỗ trợ người bệnh.", duplicate.ComparedText);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Nonduplicate_routes_to_the_unique_subsection_and_preserves_source_label()
    {
        var fact = Fact("Duy trì hoạt động", "Hỗ trợ bổ sung.");
        var target = new GatherSubsectionTarget("Duy trì hoạt động", "p[75]", "p[80]", ["Nội dung cũ."]);
        var routed = GatherSubsectionRouter.Route([fact], [target]);
        var item = Assert.Single(routed.ByLocation["p[75]"]);
        Assert.Equal("Hỗ trợ bổ sung.", item.Render());
        Assert.Equal("p[89]", Assert.Single(item.AlsoFrom).Location);
        Assert.Equal("p[90]", Assert.Single(item.Parts).Location);
    }

    [Fact]
    public void Unmatched_or_ambiguous_heading_and_unlabelled_fact_are_not_written_or_discarded()
    {
        var fact = Fact("Duy trì hoạt động", "Nội dung");
        var unlabelled = new GatheredFact([Part("p[91]", "text", "Không có nhãn")], []);
        var same = new[] { new GatherSubsectionTarget("Duy trì hoạt động", "p[1]", "p[2]", []),
            new GatherSubsectionTarget("Duy trì hoạt động", "p[3]", null, []) };
        var result = GatherSubsectionRouter.Route([fact, unlabelled], same);
        Assert.Equal(2, result.Unresolved.Count);
        Assert.All(result.ByLocation.Values, Assert.Empty);
        Assert.Empty(result.AlreadyInTarget);
        Assert.Throws<InvalidOperationException>(() => GatherSubsectionRouter.BuildSections(result, same,
            new GatherRun([], [], [], [], [], [], 1)));
        Assert.Single(GatherSubsectionRouter.Route([fact], [same[0] with { Heading = "Tên khác" }]).Unresolved);
    }

    [Fact]
    public void Similar_or_partial_text_is_not_silently_removed_as_a_duplicate()
    {
        var fact = Fact("Mục A", "Hỗ trợ 100 người");
        var target = new GatherSubsectionTarget("Mục A", "p[1]", null, ["Hỗ trợ 100 người và gia đình", "Hỗ trợ 101 người"]);
        var result = GatherSubsectionRouter.Route([fact], [target]);
        Assert.Single(result.ByLocation["p[1]"]);
        Assert.Empty(result.AlreadyInTarget);
    }

    [Fact]
    public void Explicit_verified_leaf_keeps_a_different_label_and_deduplicates_only_whole_content()
    {
        var fact = Fact("Nhãn dữ liệu", "Nội dung mới");
        var target = new GatherSubsectionTarget("Mục đích", "p[1]", null, []);
        Assert.Single(GatherSubsectionRouter.Route([fact], [target]).Unresolved);
        var routed = GatherSubsectionRouter.Route([fact], [target], singleTargetVerified: true);
        Assert.Equal("Nhãn dữ liệu: Nội dung mới", Assert.Single(routed.ByLocation["p[1]"]).Render());
        Assert.Throws<ArgumentException>(() => GatherSubsectionRouter.Route([fact], [], singleTargetVerified: true));
    }

    [Fact]
    public void September_donation_fact_is_detected_as_already_in_the_matching_october_subsection()
    {
        // The monthly reports are the owner's real documents and are never committed: on a clone without them there is nothing to check.
        const string folder = "todo10_8/standard-docx-v3/05_bien_ban_hop/";
        var septemberPath = TestRepository.Path(folder + "Báo cáo tháng 9 phương hướng tháng 10-2026 Phòng CTXH&TT.docx");
        var octoberPath = TestRepository.Path(folder + "Báo cáo tháng 10 phương hướng tháng 11-2026 Phòng CTXH&TT.docx");
        if (!File.Exists(septemberPath) || !File.Exists(octoberPath)) return;
        var september = GatherUnits.Read(septemberPath, "T9");
        var october = GatherUnits.Read(octoberPath, "T10");
        var label = september.Units.Single(u => u.Location == "body[1]/p[89]");
        var value = september.Units.Single(u => u.Location == "body[1]/p[90]");
        var heading = october.Units.Single(u => u.Location == "body[1]/p[75]");
        var existing = october.Units.Single(u => u.Location == "body[1]/p[76]");
        var fact = new GatheredFact([new(label.Id, label.SourceId, label.Location, "label", label.Text, 0, label.Text.Length),
            new(value.Id, value.SourceId, value.Location, "text", value.Text, 0, value.Text.Length)], []);
        var result = GatherSubsectionRouter.Route([fact], [new(heading.Text, heading.Location, "body[1]/p[82]", [existing.Text])]);
        var duplicate = Assert.Single(result.AlreadyInTarget);
        Assert.Equal(value.Text, duplicate.ComparedText);
        Assert.Empty(result.ByLocation[heading.Location]);
        Assert.Equal(value.Id, duplicate.SourceFact.Parts[1].UnitId);
    }

    [Fact]
    public void Routed_content_is_written_before_the_next_subheading_in_a_copy()
    {
        static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";
        var path = RawDocxFixtureTests.Build(P("Mục cha") + P("Mục A") + P("Nội dung A") + P("Mục B") + P("Nội dung B"));
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            var fact = Fact("Mục A", "Thông tin nguồn");
            var target = new GatherSubsectionTarget("Mục A", "body[1]/p[2]", "body[1]/p[4]", ["Nội dung A"]);
            var routed = GatherSubsectionRouter.Route([fact], [target]);
            var run = new GatherRun([fact], [], [], [], [], [], 1);
            var sections = GatherSubsectionRouter.BuildSections(routed, [target], run);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var result = GatherWriteback.Apply(path, output, sections, DateTime.UtcNow, true, hash);
            Assert.Empty(result.SkippedHeadings);
            using var doc = WordprocessingDocument.Open(output, false);
            var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            var inserted = paragraphs.FindIndex(p => p.Contains("Thông tin nguồn", StringComparison.Ordinal));
            Assert.True(inserted > paragraphs.IndexOf("Nội dung A"));
            Assert.True(inserted < paragraphs.IndexOf("Mục B"));
            Assert.Contains("p[90]", paragraphs[inserted], StringComparison.Ordinal);
            Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally { File.Delete(path); File.Delete(output); }
    }
}
