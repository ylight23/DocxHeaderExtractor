using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

/// <summary>The selection audit is audit only: it reads the selection's own answers and never adds or removes a heading.</summary>
public sealed class SelectionAuditTests
{
    [Fact]
    public void Selected_lines_and_their_reasons_are_read_from_the_selection_answers()
    {
        var answers = new[]
        {
            "{\"unit\":\"SEG-001\",\"decisions\":{\"A1\":\"SELECT\",\"A2\":\"NOT_SELECT\",\"A3\":\"SELECT\"},\"reasons\":{\"A1\":\"names an article\",\"A3\":\"numbered\"}}",
            "{\"grouping\":\"{}\"}",
            "not json",
        };

        var selected = SelectionAudit.SelectedWithReasons(answers);

        Assert.Equal(new[] { ("A1", "names an article"), ("A3", "numbered") }, selected);
    }

    [Fact]
    public void Without_headings_the_audit_makes_no_call()
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, _) => { calls++; throw new InvalidOperationException("must not call"); };

        var entries = SelectionAudit.Run(null!, [], [], model, 4, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Equal(0, calls);
    }
}
