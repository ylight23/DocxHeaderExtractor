using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline regression of observed D4.5 contract failures. No provider or network call.
/// Missing negative citations are allowed; missing/extra aliases and oversized groups remain fatal.
/// </summary>
public sealed class P7F1V6DirectContractFixTests
{
    static readonly string[] Aliases = Enumerable.Range(0, 12).Select(i => $"L{i:0000}:S0").ToArray();
    static readonly Dictionary<string, int> Shown = Aliases.Select((alias, index) => (alias, index))
        .ToDictionary(x => x.alias, x => x.index, StringComparer.Ordinal);

    static string Reply(IEnumerable<object> decisions, IEnumerable<object>? headings = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "COMPLETE",
            decisions,
            headings = headings ?? Array.Empty<object>()
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotSelect_AcceptsOmittedOrEmptyEvidenceRefs(bool omitted)
    {
        var negative = omitted
            ? (object)new { alias = Aliases[1], decision = "NOT_SELECT" }
            : new { alias = Aliases[1], decision = "NOT_SELECT", evidenceRefs = Array.Empty<string>() };
        var text = Reply(
            new object[] { new { alias = Aliases[0], decision = "SELECT", evidenceRefs = new[] { Aliases[0] } }, negative },
            new object[] { new { parts = new[] { Aliases[0] } } });
        var parsed = P7F1V6DirectV2.Parse(text, Shown, Aliases.Take(2).ToArray());
        Assert.True(parsed.Ok, parsed.Failure);
        Assert.Empty(parsed.Decisions.Single(d => d.Alias == Aliases[1]).Refs);
        Assert.Equal("SELECT", parsed.Decisions.Single(d => d.Alias == Aliases[0]).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Select_StillRequiresOneOrMoreShownEvidenceRefs(bool omitted)
    {
        var positive = omitted
            ? (object)new { alias = Aliases[0], decision = "SELECT" }
            : new { alias = Aliases[0], decision = "SELECT", evidenceRefs = Array.Empty<string>() };
        var text = Reply(new object[] { positive }, new object[] { new { parts = new[] { Aliases[0] } } });
        Assert.Equal("EVIDENCE_REFS_INVALID", P7F1V6DirectV2.Parse(text, Shown, new[] { Aliases[0] }).Failure);
    }

    [Fact]
    public void NotSelect_InvalidEvidenceReference_IsNotSilentlyAccepted()
    {
        var text = Reply(new object[] { new { alias = Aliases[0], decision = "NOT_SELECT", evidenceRefs = new[] { "FOREIGN" } } });
        Assert.Equal("EVIDENCE_NOT_SHOWN", P7F1V6DirectV2.Parse(text, Shown, new[] { Aliases[0] }).Failure);
    }

    [Fact]
    public void ContextOnlyAlias_IsRejectedEvenWithOmittedNegativeEvidence()
    {
        // Mirrors SEG-003: fixing negative evidence reveals an independent UNEXPECTED_ALIAS.
        var text = Reply(new object[]
        {
            new { alias = Aliases[0], decision = "NOT_SELECT" },
            new { alias = Aliases[1], decision = "NOT_SELECT" }
        });
        Assert.Equal("UNEXPECTED_ALIAS", P7F1V6DirectV2.Parse(text, Shown, new[] { Aliases[0] }).Failure);
    }

    [Fact]
    public void MissingDecision_IsRejectedEvenWithOmittedNegativeEvidence()
    {
        // Mirrors SEG-008: one alias missing must still make the entire unit INCOMPLETE.
        var text = Reply(new object[] { new { alias = Aliases[0], decision = "NOT_SELECT" } });
        var parsed = P7F1V6DirectV2.Parse(text, Shown, Aliases.Take(2).ToArray());
        Assert.Equal("COVERAGE_INCOMPLETE", parsed.Failure);
        Assert.Equal(Aliases[1], Assert.Single(parsed.Missing));
    }

    [Fact]
    public void GroupExceedingEightParts_StillFails()
    {
        // Mirrors SEG-001; no silent truncation or regrouping by C#.
        var partAliases = Aliases.Take(9).ToArray();
        var decisions = partAliases.Select(a => (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } }).ToArray();
        var text = Reply(decisions, new object[] { new { parts = partAliases } });
        Assert.Equal("HEADING_PARTS_INVALID", P7F1V6DirectV2.Parse(text, Shown, partAliases).Failure);
    }

    [Fact]
    public void Prompt_AndParser_AgreeOnNegativeEvidenceAndDecisionScope()
    {
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, P7F1V6DirectV2.ComputedSystemPromptSha256());
        Assert.Contains("NOT_SELECT may omit evidenceRefs", P7F1V6DirectV2.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("NO decision for any alias in context", P7F1V6DirectV2.SystemPrompt, StringComparison.Ordinal);
    }
}
