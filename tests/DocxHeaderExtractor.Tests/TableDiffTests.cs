using DocxHeaderExtractor.DocumentProcessing.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class TableDiffTests
{
    static string Cell(string text, string properties = "") =>
        $"<w:tc>{properties}<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p></w:tc>";

    static string Table(params string[][] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
        string.Concat(rows.Select(r => "<w:tr>" + string.Concat(r.Select(c => Cell(c))) + "</w:tr>")) + "</w:tbl>";

    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static ComparisonResult Compare(string templateBody, string documentBody)
    {
        var template = RawDocxFixtureTests.Build(templateBody);
        var document = RawDocxFixtureTests.Build(documentBody);
        try { return DocxComparer.Compare(template, document); }
        finally
        {
            File.Delete(template);
            File.Delete(document);
        }
    }

    static readonly string[] R1 = ["Hạng mục xây tường", "Đơn giá 100", "Số lượng 2"];
    static readonly string[] R2 = ["Hạng mục lát sàn", "Đơn giá 250", "Số lượng 7"];
    static readonly string[] R3 = ["Hạng mục sơn cửa", "Đơn giá 80", "Số lượng 12"];
    static readonly string[] R4 = ["Hạng mục lắp đèn", "Đơn giá 40", "Số lượng 30"];

    [Fact]
    public void Identical_tables_have_no_changes()
    {
        Assert.Empty(Compare(P("Bảng giá") + Table(R1, R2), P("Bảng giá") + Table(R1, R2)).Changes);
    }

    [Fact]
    public void A_word_changed_in_a_cell_is_reported_on_that_cell_with_spans()
    {
        var result = Compare(Table(R1, R2), Table(R1, ["Hạng mục lát sàn", "Đơn giá 260", "Số lượng 7"]));

        var change = Assert.Single(result.Changes);
        Assert.Equal("text_modified", change.Kind);
        Assert.Equal("body[1]/tbl[1]/tr[2]/tc[2]/p[1]", change.Template!.Location);
        Assert.Equal("250", change.Template.Span!.Text);
        Assert.Equal("260", change.Document!.Span!.Text);
    }

    [Fact]
    public void An_added_row_is_one_row_change_not_many_paragraph_changes()
    {
        var result = Compare(Table(R1, R2), Table(R1, R4, R2));

        var change = Assert.Single(result.Changes);
        Assert.Equal("row_only_in_document", change.Kind);
        Assert.Equal("body[1]/tbl[1]/tr[2]", change.Document!.Location);
        Assert.Equal(3, change.DocumentGroup!.Count);
    }

    [Fact]
    public void A_removed_row_is_reported_on_the_template_side()
    {
        var result = Compare(Table(R1, R2, R3), Table(R1, R3));

        var change = Assert.Single(result.Changes);
        Assert.Equal("row_only_in_template", change.Kind);
        Assert.Equal("body[1]/tbl[1]/tr[2]", change.Template!.Location);
    }

    [Fact]
    public void Swapped_rows_are_reported_as_moved_without_text_changes()
    {
        var result = Compare(Table(R1, R2, R3), Table(R1, R3, R2));

        var moved = Assert.Single(result.Changes);
        Assert.Equal("row_moved", moved.Kind);
        Assert.False(moved.Uncertain);
        Assert.NotEqual(moved.Template!.Location, moved.Document!.Location);
    }

    [Fact]
    public void A_column_added_to_every_row_is_reported_as_a_column()
    {
        string[] Two(string[] row) => row[..2];
        var result = Compare(Table(Two(R1), Two(R2), Two(R3)),
            Table([.. Two(R1), "Ghi chú một"], [.. Two(R2), "Ghi chú hai"], [.. Two(R3), "Ghi chú ba"]));

        var column = Assert.Single(result.Changes, c => c.Kind == "column_only_in_document");
        Assert.Equal("body[1]/tbl[1]#col[3]", column.Document!.Location);
        Assert.Equal(3, result.Changes.Count(c => c.Kind == "cell_only_in_document"));
        Assert.DoesNotContain(result.Changes, c => c.Kind.StartsWith("row_", StringComparison.Ordinal));
    }

    const string LongText = "Nhà thầu phải nộp bảo đảm thực hiện hợp đồng trong vòng hai mươi tám ngày kể từ ngày nhận được thông báo trúng thầu, " +
        "theo mẫu quy định tại hồ sơ mời thầu và do một ngân hàng có uy tín phát hành, có hiệu lực đến khi hoàn thành nghĩa vụ bảo hành.";

    [Fact]
    public void A_long_added_row_still_pairs_the_table()
    {
        var result = Compare(Table(R1, R2, R3), Table(R1, [LongText, LongText, LongText], R2, R3));

        var change = Assert.Single(result.Changes);
        Assert.Equal("row_only_in_document", change.Kind);
        Assert.Equal("body[1]/tbl[1]/tr[2]", change.Document!.Location);
    }

    [Fact]
    public void A_long_added_column_still_pairs_every_row()
    {
        string[] Two(string[] row) => row[..2];
        var result = Compare(Table(Two(R1), Two(R2), Two(R3)),
            Table([.. Two(R1), LongText], [.. Two(R2), LongText], [.. Two(R3), LongText]));

        Assert.Single(result.Changes, c => c.Kind == "column_only_in_document");
        Assert.DoesNotContain(result.Changes, c => c.Kind.StartsWith("row_", StringComparison.Ordinal) || c.Kind.StartsWith("table_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removed_table_is_one_table_change()
    {
        var result = Compare(P("Mở đầu") + Table(R1, R2) + P("Kết"), P("Mở đầu") + P("Kết"));

        var change = Assert.Single(result.Changes);
        Assert.Equal("table_only_in_template", change.Kind);
        Assert.Equal("body[1]/tbl[1]", change.Template!.Location);
        Assert.Equal(6, change.TemplateGroup!.Count);
    }

    [Fact]
    public void Changes_in_a_table_with_merged_cells_are_uncertain_and_say_why()
    {
        string Merged(string second) =>
            "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
            "<w:tr>" + Cell("Tiêu đề gộp hai cột", "<w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr>") + "</w:tr>" +
            "<w:tr>" + Cell("Cột một") + Cell(second) + "</w:tr></w:tbl>";
        var template = RawDocxFixtureTests.Build(Merged("Giá 100"));
        var document = RawDocxFixtureTests.Build(Merged("Giá 120"));
        try
        {
            var result = DocxComparer.Compare(template, document);
            Assert.Contains("merged_table_cells", result.TemplateUnsupported);
            var change = Assert.Single(result.Changes);
            Assert.True(change.Uncertain);
            Assert.Contains("merged_cells_present", change.Note);
        }
        finally
        {
            File.Delete(template);
            File.Delete(document);
        }
    }

    [Fact]
    public void Body_paragraphs_around_a_table_are_still_compared()
    {
        var result = Compare(P("Điều 1 trước bảng.") + Table(R1) + P("Điều 2 sau bảng."),
            P("Điều 1 trước bảng.") + Table(R1) + P("Điều 2 sau bảng đã sửa."));

        var change = Assert.Single(result.Changes);
        Assert.Equal("body[1]/p[2]", change.Template!.Location);
        Assert.Equal("text_inserted", change.Kind);
    }
}
