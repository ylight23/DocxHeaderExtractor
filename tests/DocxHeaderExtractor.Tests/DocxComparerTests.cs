using DocxHeaderExtractor.DocumentProcessing.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class DocxComparerTests
{
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

    [Fact]
    public void Identical_documents_have_no_changes()
    {
        var result = Compare(P("Điều 1") + P("Nội dung"), P("Điều 1") + P("Nội dung"));
        Assert.Empty(result.Changes);
        Assert.Equal(64, result.TemplateSha256.Length);
    }

    [Fact]
    public void A_paragraph_added_in_the_document_is_reported_with_its_location()
    {
        var result = Compare(P("A") + P("C"), P("A") + P("B") + P("C"));

        var change = Assert.Single(result.Changes);
        Assert.Equal("paragraph_only_in_document", change.Kind);
        Assert.Null(change.Template);
        Assert.Equal("B", change.Document!.Text);
        Assert.Equal("body[1]/p[2]", change.Document.Location);
        Assert.False(change.Uncertain);
    }

    [Fact]
    public void A_changed_word_gives_spans_on_each_sides_raw_text()
    {
        var result = Compare(P("Thanh toán trong 30 ngày."), P("Thanh toán trong 45 ngày."));

        var change = Assert.Single(result.Changes);
        Assert.Equal("text_modified", change.Kind);
        Assert.Equal(new TextSpan(17, 19, "30"), change.Template!.Span);
        Assert.Equal(new TextSpan(17, 19, "45"), change.Document!.Span);
        Assert.Equal("paired_by_similarity", change.MatchStatus);
    }

    [Fact]
    public void A_whitespace_only_difference_is_kept_and_labelled()
    {
        var result = Compare(P("Điều  1"), P("Điều 1"));

        var change = Assert.Single(result.Changes);
        Assert.Equal("matched", change.MatchStatus);
        Assert.Equal("whitespace_or_unicode_only", change.Note);
        Assert.Equal("  ", change.Template!.Span!.Text);
        Assert.Equal(" ", change.Document!.Span!.Text);
    }

    [Fact]
    public void Words_added_inside_a_paragraph_are_an_insertion_point_on_the_template_side()
    {
        var result = Compare(P("X") + P("Bên A thanh toán đúng hạn.") + P("Y"),
            P("X") + P("Bên A thanh toán đúng hạn hàng tháng.") + P("Y"));

        var change = Assert.Single(result.Changes);
        Assert.Equal("text_inserted", change.Kind);
        Assert.Equal("", change.Template!.Span!.Text);
        Assert.Equal(25, change.Template.Span.Start);
        Assert.Equal(" hàng tháng", change.Document!.Span!.Text);
    }

    [Fact]
    public void A_removed_duplicate_is_reported_as_uncertain()
    {
        var result = Compare(P("A") + P("A") + P("B"), P("A") + P("B"));

        var change = Assert.Single(result.Changes);
        Assert.Equal("paragraph_only_in_template", change.Kind);
        Assert.True(change.Uncertain);
    }

    [Fact]
    public void Offsets_are_utf16_units_on_raw_text_with_emoji()
    {
        var result = Compare(P("😀a"), P("😀b"));

        var change = Assert.Single(result.Changes);
        Assert.Equal(new TextSpan(2, 3, "a"), change.Template!.Span);
        Assert.Equal(new TextSpan(2, 3, "b"), change.Document!.Span);
    }

    [Fact]
    public void Unsupported_regions_of_each_side_are_carried_into_the_result()
    {
        var result = Compare(
            "<w:p><w:del w:id=\"1\" w:author=\"t\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:delText>x</w:delText></w:r></w:del><w:r><w:t>A</w:t></w:r></w:p>",
            P("A"));

        Assert.Contains("tracked_changes", result.TemplateUnsupported);
        Assert.Empty(result.DocumentUnsupported);
        Assert.Contains("formatting_not_compared", result.Scope);
    }

    const string Whole = "Điều 1. Quy định chung về hợp đồng. Phạm vi áp dụng của hợp đồng mua bán.";
    const string First = "Điều 1. Quy định chung về hợp đồng.";
    const string Second = "Phạm vi áp dụng của hợp đồng mua bán.";

    [Fact]
    public void One_paragraph_split_into_two_is_reported_as_a_possible_split_with_both_parts()
    {
        var result = Compare(P("Mở đầu") + P(Whole) + P("Kết"), P("Mở đầu") + P(First) + P(Second) + P("Kết"));

        var split = Assert.Single(result.Changes, c => c.Kind == "paragraph_split");
        Assert.True(split.Uncertain);
        Assert.Equal("body[1]/p[2]", split.Template!.Location);
        Assert.Equal(new[] { "body[1]/p[2]", "body[1]/p[3]" }, split.DocumentGroup!.Select(s => s.Location));
        Assert.DoesNotContain(result.Changes, c => c.Kind.StartsWith("paragraph_only", StringComparison.Ordinal));
        Assert.All(result.Changes.Where(c => c.Kind.StartsWith("text_", StringComparison.Ordinal)),
            c => Assert.Contains("possible_split", c.Note));
    }

    [Fact]
    public void Two_paragraphs_merged_into_one_is_reported_as_a_possible_merge()
    {
        var result = Compare(P("Mở đầu") + P(First) + P(Second) + P("Kết"), P("Mở đầu") + P(Whole) + P("Kết"));

        var merge = Assert.Single(result.Changes, c => c.Kind == "paragraph_merged");
        Assert.True(merge.Uncertain);
        Assert.Equal(2, merge.TemplateGroup!.Count);
        Assert.Equal("body[1]/p[2]", merge.Document!.Location);
        Assert.DoesNotContain(result.Changes, c => c.Kind.StartsWith("paragraph_only", StringComparison.Ordinal));
    }

    [Fact]
    public void A_split_with_an_edited_word_maps_the_edit_onto_the_right_part()
    {
        var result = Compare(P(Whole), P(First) + P("Phạm vi áp dụng của hợp đồng mua bán này."));

        Assert.Single(result.Changes, c => c.Kind == "paragraph_split");
        var edit = Assert.Single(result.Changes, c => c.Document?.Span?.Text == " này");
        Assert.Equal("body[1]/p[2]", edit.Document!.Location);
        Assert.Equal("text_inserted", edit.Kind);
    }

    [Fact]
    public void An_unrelated_new_paragraph_next_to_an_unchanged_one_is_not_a_split()
    {
        var result = Compare(P("Mở đầu") + P(Whole) + P("Kết"), P("Mở đầu") + P(Whole + " Thêm.") + P("Bảng giá dịch vụ đính kèm.") + P("Kết"));

        Assert.DoesNotContain(result.Changes, c => c.Kind is "paragraph_split" or "paragraph_merged");
        Assert.Single(result.Changes, c => c.Kind == "paragraph_only_in_document" && c.Document!.Text == "Bảng giá dịch vụ đính kèm.");
    }

    [Fact]
    public void A_repeated_paragraph_added_is_not_taken_for_a_split()
    {
        var result = Compare(P("Ghi chú.") + P(Whole), P("Ghi chú.") + P("Ghi chú.") + P(Whole));

        var change = Assert.Single(result.Changes);
        Assert.Equal("paragraph_only_in_document", change.Kind);
        Assert.True(change.Uncertain);
    }

    const string MinutesBody = "Governing Committee of the fund met on Monday in Kyiv to review the annual budget, the work plan and the report of the trustee for the next year.";
    const string Minutes = "MEETING MINUTES " + MinutesBody;

    static ComparisonResult CompareWith(string templateBody, string documentBody, ComparerOptions options)
    {
        var template = RawDocxFixtureTests.Build(templateBody);
        var document = RawDocxFixtureTests.Build(documentBody);
        try
        {
            return DocxComparer.Compare(Sources.Docx.Changes.DocxVersionReader.Read(template), Sources.Docx.Changes.DocxVersionReader.Read(document), options);
        }
        finally
        {
            File.Delete(template);
            File.Delete(document);
        }
    }

    [Fact]
    public void The_default_rule_detects_a_short_heading_split_and_is_recorded_in_the_result()
    {
        var result = Compare(P(Minutes), P("MEETING MINUTES") + P(MinutesBody));

        Assert.Equal("ExplainsLeftover", result.GroupRule);
        Assert.True(result.FlagRejectedGroups);
        Assert.Single(result.Changes, c => c.Kind == "paragraph_split");
    }

    [Fact]
    public void The_advantage_rule_stays_available_and_its_rejection_is_flagged_on_the_paragraphs_involved()
    {
        var result = CompareWith(P(Minutes), P("MEETING MINUTES") + P(MinutesBody),
            new ComparerOptions(GroupRule.Advantage));

        Assert.Equal("Advantage", result.GroupRule);
        Assert.DoesNotContain(result.Changes, c => c.Kind == "paragraph_split");
        var oneSided = Assert.Single(result.Changes, c => c.Kind == "paragraph_only_in_document");
        Assert.Equal("MEETING MINUTES", oneSided.Document!.Text);
        Assert.True(oneSided.Uncertain);
        Assert.Equal("possible_split_rejected:no_advantage", oneSided.Note);
        Assert.All(result.Changes.Where(c => c.Kind.StartsWith("text_", StringComparison.Ordinal)),
            c => Assert.Contains("possible_split_rejected:no_advantage", c.Note));
    }

    [Fact]
    public void Turning_the_flag_off_changes_only_uncertainty_and_notes_not_the_differences()
    {
        var on = CompareWith(P(Minutes), P("MEETING MINUTES") + P(MinutesBody),
            new ComparerOptions(GroupRule.Advantage, FlagRejectedGroups: true));
        var off = CompareWith(P(Minutes), P("MEETING MINUTES") + P(MinutesBody),
            new ComparerOptions(GroupRule.Advantage, FlagRejectedGroups: false));

        Assert.Equal(off.Changes.Select(c => c with { Uncertain = false, Note = null }), on.Changes.Select(c => c with { Uncertain = false, Note = null }));
        Assert.DoesNotContain(off.Changes, c => c.Note?.Contains("_rejected", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Each_span_is_the_exact_substring_of_its_sides_raw_text()
    {
        var result = Compare(P("Điều 3. Bên B giao hàng trong 10 ngày.") + P("Phụ lục"),
            P("Điều 3. Bên B giao hàng tận nơi trong 7 ngày làm việc.") + P("Phụ lục A"));

        Assert.NotEmpty(result.Changes);
        foreach (var change in result.Changes)
        {
            if (change.Template?.Span is { } t) Assert.Equal(change.Template.Text[t.Start..t.End], t.Text);
            if (change.Document?.Span is { } d) Assert.Equal(change.Document.Text[d.Start..d.End], d.Text);
        }
    }
}
