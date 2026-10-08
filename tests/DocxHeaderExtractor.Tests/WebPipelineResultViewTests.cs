using System.Text.RegularExpressions;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Guards the checked-in Web result view against silently dropping source-grounded heading
/// evidence or reviving the legacy progress renderer. These are source contract assertions;
/// browser interaction and PDF provider execution require separate integration coverage.
/// </summary>
public sealed class WebPipelineResultViewTests
{
    private static string WebHtml() => File.ReadAllText(Path.Combine(
        TestRepository.Root(), "src", "DocxHeaderExtractor.Web", "wwwroot", "index.html"));

    [Fact]
    public void Heading_result_exposes_source_evidence_without_inventing_parentage()
    {
        var html = WebHtml();
        Assert.Contains("const headingEvidence = h =>", html);
        foreach (var property in new[]
        {
            "h.stableId", "h.sourceId", "h.headingSpan.start", "h.headingSpan.end",
            "h.hierarchyResolution", "h.boundarySource", "h.confidenceBasis",
            "h.originalText", "h.inlineBody",
        })
            Assert.Contains(property, html);

        Assert.Contains("fields.map(([key, value])", html);
        Assert.Contains("esc(key)", html);
        Assert.Contains("esc(String(value))", html);
        Assert.Contains("esc(h.text)", html);
        Assert.Contains("không suy diễn quan hệ cha–con", html);
        Assert.DoesNotContain("h.parentId", html);
    }

    [Fact]
    public void Result_view_uses_only_the_contract_aware_harness_progress_renderer()
    {
        var html = WebHtml();
        Assert.DoesNotContain("const STAGES =", html);
        Assert.DoesNotContain("stageIndex", html);
        Assert.Equal(1, Regex.Matches(html, @"function renderStages\(").Count);
        Assert.Equal(1, Regex.Matches(html, @"function updateDevState\(").Count);
        Assert.Contains("renderHarnessStages();", html);
        Assert.Contains("outline = evt.outline", html);
        Assert.Contains("render(evt.outline, evt.stats, evt.review, evt.agent)", html);
    }
}
