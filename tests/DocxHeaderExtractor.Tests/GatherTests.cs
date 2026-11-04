using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

public sealed class GatherTests
{
    static string P(string text, string style = "") =>
        $"<w:p>{(style.Length == 0 ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")}<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static string Row(params string[] cells) =>
        "<w:tr>" + string.Concat(cells.Select(c => $"<w:tc><w:p><w:r><w:t xml:space=\"preserve\">{c}</w:t></w:r></w:p></w:tc>")) + "</w:tr>";

    static string Table(params string[] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" + string.Concat(rows) + "</w:tbl>";

    static HeadingRecord Heading(int index, int? level, string text) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    static GatherUnit Unit(string id, string text, string path = "", string? table = null, string? row = null, string? label = null,
        string? labelId = null, string? header = null, string? headerId = null, bool certain = true, bool merged = false) =>
        new(id, id.Split('|')[0], id, table is null ? "body" : "table", false, false, text, path, "pipeline", certain, table, row, merged,
            label, labelId, header, headerId, id);

    static ProposedFact Fact(params FactPart[] parts) => new(parts);

    static GatherSource Read(string body, IReadOnlyList<HeadingRecord>? outline = null)
    {
        var path = RawDocxFixtureTests.Build(body);
        try { return GatherUnits.Read(path, "src", outline); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_heading_without_a_heading_style_gives_the_path_when_the_pipeline_outline_names_it()
    {
        var source = Read(P("Thông tin chung") + P("- Đơn vị chủ trì: Sở A."), [Heading(0, 1, "Thông tin chung")]);

        var heading = source.Units.Single(u => u.Text == "Thông tin chung");
        Assert.True(heading.IsHeading);
        var bullet = source.Units.Single(u => u.Text.StartsWith("- Đơn vị"));
        Assert.Equal("Thông tin chung", bullet.HeadingPath);
        Assert.Equal("pipeline", bullet.PathSource);
        Assert.True(bullet.PathCertain);
        Assert.True(bullet.IsBullet);
    }

    [Fact]
    public void Information_inside_a_heading_paragraph_is_a_unit_and_can_be_cited()
    {
        var source = Read(P("Thông tin chung: Sở A là đơn vị chủ trì."), [Heading(0, 1, "Thông tin chung")]);

        var unit = Assert.Single(source.Units);
        Assert.True(unit.IsHeading);
        var result = GatherCheck.Check([unit], [Fact(new FactPart(unit.Id, 17, 21, "text"))]);
        Assert.Equal("Sở A", Assert.Single(result.Kept).Parts[0].Text);
    }

    [Fact]
    public void A_heading_of_unknown_level_leaves_the_units_after_it_uncertain_and_the_checker_flags_them()
    {
        var source = Read(P("Thông tin chung") + P("Mục lạ") + P("Dự án triển khai tại Hà Nội."), [
            Heading(0, 1, "Thông tin chung"),
            Heading(1, null, "Mục lạ"),
        ]);

        var after = source.Units.Single(u => u.Text.StartsWith("Dự án"));
        Assert.False(after.PathCertain);
        Assert.Equal("Thông tin chung", after.HeadingPath);
        var result = GatherCheck.Check(source.Units, [Fact(new FactPart(after.Id, 0, 3, "text"))]);
        Assert.Contains("PATH_UNCERTAIN", Assert.Single(result.Kept).Flags);
    }

    [Fact]
    public void A_pipeline_heading_that_does_not_match_the_file_is_a_gap_not_a_silent_path()
    {
        var source = Read(P("Thông tin chung") + P("- Đơn vị A."), [Heading(0, 1, "Thông tin chung"), Heading(9, 1, "Không có")]);

        Assert.Contains(source.Gaps, g => g.Reason == "heading_unmatched:9");
        Assert.Equal("Thông tin chung", source.Units.Single(u => u.Text.StartsWith("- Đơn vị")).HeadingPath);
    }

    [Fact]
    public void A_heading_the_pipeline_places_elsewhere_is_matched_by_its_text_and_reported()
    {
        var source = Read(P("Mở đầu") + P("Thông tin chung") + P("- Đơn vị A."), [Heading(0, 1, "Thông tin chung")]);

        Assert.Contains(source.Gaps, g => g.Reason.StartsWith("heading_located_by_text:", StringComparison.Ordinal));
        Assert.Equal("Thông tin chung", source.Units.Single(u => u.Text.StartsWith("- Đơn vị")).HeadingPath);
        Assert.False(source.Units.Single(u => u.Text.StartsWith("- Đơn vị")).PathCertain);
    }

    [Fact]
    public void Units_after_an_unmatched_heading_are_uncertain_until_the_next_matched_heading()
    {
        var source = Read(P("Thông tin chung") + P("Phần lạ không khớp") + P("Đoạn sau phần lạ") + P("Mục khác") + P("Đoạn cuối"), [
            Heading(0, 1, "Thông tin chung"),
            Heading(1, 2, "Không có trong file"),
            Heading(3, 1, "Mục khác"),
        ]);

        Assert.Contains(source.Gaps, g => g.Reason == "heading_unmatched:1");
        Assert.False(source.Units.Single(u => u.Text == "Phần lạ không khớp").PathCertain);
        Assert.False(source.Units.Single(u => u.Text == "Đoạn sau phần lạ").PathCertain);
        Assert.False(source.Units.Single(u => u.Text == "Mục khác").PathCertain);
        var last = source.Units.Single(u => u.Text == "Đoạn cuối");
        Assert.True(last.PathCertain);
        Assert.Equal("Mục khác", last.HeadingPath);
    }

    [Fact]
    public void A_table_without_a_label_in_its_first_cell_is_flagged_when_the_fact_pairs_across_rows()
    {
        var body = P("Thông tin chung") + Table(Row("Mã", "Tên"), Row("A-01", "Nhà thầu X"));
        var source = Read(body, [Heading(0, 1, "Thông tin chung")]);
        var value = source.Units.Single(u => u.Text == "Nhà thầu X");
        Assert.Equal("A-01", value.RowLabel);
        var header = source.Units.Single(u => u.Text == "Mã");

        var result = GatherCheck.Check(source.Units, [Fact(
            new FactPart(header.Id, 0, 2, "label"), new FactPart(value.Id, 0, 10, "value"))]);

        var flags = Assert.Single(result.Kept).Flags;
        Assert.Contains("LABEL_NOT_ROW_LABEL", flags);
        Assert.Contains("CROSS_ROW_OR_TABLE", flags);
    }

    [Fact]
    public void A_label_and_value_in_two_cells_keep_their_own_ids_and_mark_multiple_units()
    {
        var body = P("Thông tin chung") + Table(Row("Tên dự án", "Xây dựng hệ thống X"));
        var source = Read(body, [Heading(0, 1, "Thông tin chung")]);
        var label = source.Units.Single(u => u.Text == "Tên dự án");
        var value = source.Units.Single(u => u.Text == "Xây dựng hệ thống X");
        Assert.Equal(label.Id, value.LabelUnitId);

        var result = GatherCheck.Check(source.Units, [Fact(
            new FactPart(label.Id, 0, 9, "label"), new FactPart(value.Id, 0, 19, "value"))]);

        var fact = Assert.Single(result.Kept);
        Assert.Contains("MULTIPLE_UNITS", fact.Flags);
        Assert.Equal("Tên dự án: Xây dựng hệ thống X", fact.Render());
    }

    [Fact]
    public void Render_formats_colon_labels_and_separates_multiple_values()
    {
        var fact = new GatheredFact([
            new SourcedPart("A|p1", "A", "body[1]/p[1]", "label", "Kinh phí:"),
            new SourcedPart("A|p2", "A", "body[1]/p[2]", "value", "Lập kế hoạch"),
            new SourcedPart("A|p3", "A", "body[1]/p[3]", "value", "Thực hiện báo cáo"),
        ], []);

        Assert.Equal("Kinh phí: Lập kế hoạch; Thực hiện báo cáo", fact.Render());
    }

    [Fact]
    public void Render_separates_a_label_from_following_text()
    {
        var fact = new GatheredFact([
            new SourcedPart("A|p1", "A", "body[1]/p[1]", "label", "Quy trình:"),
            new SourcedPart("A|p2", "A", "body[1]/p[2]", "text", "Rà soát và hoàn thiện."),
        ], []);

        Assert.Equal("Quy trình: Rà soát và hoàn thiện.", fact.Render());
    }

    [Fact]
    public void A_fact_with_a_span_outside_its_unit_is_refused_as_a_whole_and_others_are_kept()
    {
        var u = Unit("A|body[1]/p[1]", "Hà Nội");

        var result = GatherCheck.Check([u], [
            Fact(new FactPart(u.Id, 0, 2, "text"), new FactPart(u.Id, 0, 99, "text")),
            Fact(new FactPart(u.Id, 0, 2, "text")),
        ]);

        var refusal = Assert.Single(result.Refused);
        Assert.Equal(0, refusal.Index);
        Assert.Equal("SPAN_OUTSIDE_TEXT", refusal.Reason);
        Assert.Equal("Hà", Assert.Single(result.Kept).Parts[0].Text);
    }

    [Fact]
    public void Unknown_units_roles_and_empty_facts_are_refused_with_reasons()
    {
        var u = Unit("A|body[1]/p[1]", "Hà Nội");

        var result = GatherCheck.Check([u], [
            Fact(new FactPart("A|body[1]/p[9]", 0, 2, "text")),
            Fact(new FactPart(u.Id, 0, 2, "maybe")),
            Fact(),
        ]);

        Assert.Equal(["UNKNOWN_UNIT", "ROLE_UNKNOWN", "NO_PARTS"], result.Refused.Select(r => r.Reason));
    }

    [Fact]
    public void The_same_text_from_two_places_is_kept_once_and_both_sources_are_still_named()
    {
        var a = Unit("A|body[1]/p[2]", "Sở A");
        var b = Unit("B|body[1]/p[4]", "Sở A");

        var result = GatherCheck.Check([a, b], [Fact(new FactPart(a.Id, 0, 3, "text")), Fact(new FactPart(b.Id, 0, 3, "text"))]);

        Assert.Single(result.Kept);
        var duplicate = Assert.Single(result.Duplicates);
        Assert.Equal(0, duplicate.KeptIndex);
        Assert.Equal(1, duplicate.DuplicateIndex);
        Assert.Equal("B", Assert.Single(duplicate.Parts).SourceId);
    }

    [Fact]
    public void A_pairing_across_two_sources_is_flagged_and_still_kept()
    {
        var a = Unit("A|body[1]/p[2]", "Tên dự án");
        var b = Unit("B|body[1]/p[4]", "Xây dựng hệ thống X");

        var result = GatherCheck.Check([a, b], [Fact(new FactPart(a.Id, 0, 9, "label"), new FactPart(b.Id, 0, 19, "value"))]);

        Assert.Contains("CROSS_SOURCE", Assert.Single(result.Kept).Flags);
    }

    [Fact]
    public void A_fact_touching_a_merged_cell_is_flagged()
    {
        var merged = Unit("A|body[1]/tbl[1]/tr[1]/tc[1]/p[1]", "Gộp", table: "body[1]/tbl[1]", row: "body[1]/tbl[1]/tr[1]", merged: true);

        var result = GatherCheck.Check([merged], [Fact(new FactPart(merged.Id, 0, 3, "text"))]);

        Assert.Contains("MERGED_CELL", Assert.Single(result.Kept).Flags);
    }

    [Fact]
    public void Batches_split_by_count_and_chars_and_a_long_unit_stands_alone()
    {
        var items = Enumerable.Range(1, 45).Select(i => Unit($"s|{i}", "ngắn")).ToList();
        var long1 = Unit("s|long", new string('x', GatherBatches.MaxChars + 1));
        var batches = GatherBatches.Plan(items.Append(long1));

        var perBatch = GatherBatches.MaxChars / GatherBatches.Cost(items[0]);
        Assert.Equal(perBatch, batches[0].Count);
        Assert.Equal(45 - perBatch, batches[1].Count);
        Assert.Single(batches[2]);
        Assert.Equal("s|long", batches[2][0].Id);
    }

    [Fact]
    public void A_long_heading_counts_toward_the_batch_and_the_cells_of_one_row_stay_together()
    {
        var longHeading = new string('h', GatherBatches.MaxChars - 300);
        var heavy = Unit("s|body[1]/p[1]", "ngắn", path: longHeading);
        var label = Unit("s|body[1]/tbl[1]/tr[2]/tc[1]/p[1]", "Tên dự án", table: "body[1]/tbl[1]", row: "body[1]/tbl[1]/tr[2]");
        var value = Unit("s|body[1]/tbl[1]/tr[2]/tc[2]/p[1]", "Xây dựng hệ thống X", table: "body[1]/tbl[1]", row: "body[1]/tbl[1]/tr[2]",
            label: "Tên dự án", labelId: label.Id, header: "Thông tin", headerId: "s|body[1]/tbl[1]/tr[1]/tc[2]/p[1]");

        var batches = GatherBatches.Plan([heavy, label, value]);

        Assert.Equal(2, batches.Count);
        Assert.Equal(["s|body[1]/p[1]"], batches[0].Select(u => u.Id));
        Assert.Equal([label.Id, value.Id], batches[1].Select(u => u.Id));
    }

    [Fact]
    public void Payload_is_json_with_the_context_and_the_ids_of_the_label_and_header_cells()
    {
        var value = Unit("s|body[1]/tbl[1]/tr[2]/tc[2]/p[1]", "Xây dựng hệ thống X", path: "Mục 1", table: "body[1]/tbl[1]",
            row: "body[1]/tbl[1]/tr[2]", label: "Tên dự án", labelId: "s|body[1]/tbl[1]/tr[2]/tc[1]/p[1]", header: "Thông tin",
            headerId: "s|body[1]/tbl[1]/tr[1]/tc[2]/p[1]");

        using var json = JsonDocument.Parse(GatherCheck.Payload([value]));
        var first = json.RootElement.GetProperty("units")[0];

        Assert.Equal("Tên dự án", first.GetProperty("rowLabel").GetString());
        Assert.Equal("s|body[1]/tbl[1]/tr[2]/tc[1]/p[1]", first.GetProperty("rowLabelUnitId").GetString());
        Assert.Equal("s|body[1]/tbl[1]/tr[1]/tc[2]/p[1]", first.GetProperty("columnHeaderUnitId").GetString());
        Assert.True(first.GetProperty("headingCertain").GetBoolean());
        Assert.Equal("Xây dựng hệ thống X", first.GetProperty("text").GetString());
    }
}
