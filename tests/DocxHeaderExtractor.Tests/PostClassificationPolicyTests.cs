using System.Collections.Frozen;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PostClassificationPolicyTests
{
    [Fact]
    public void Matches_the_characterized_legacy_postprocess_rules()
    {
        var policy = new PostClassificationPolicy();
        var cases = new[]
        {
            (Role: ParagraphRole.Normal, Score: 0.20, Next: (string?)null, Previous: (ParagraphRole?)null, Toc: true),
            (Role: ParagraphRole.HeadingCandidate, Score: 0.50, Next: new string('x', 201), Previous: (ParagraphRole?)ParagraphRole.StyledHeading, Toc: false),
            (Role: ParagraphRole.HeadingCandidate, Score: 0.98, Next: new string('x', 201), Previous: (ParagraphRole?)ParagraphRole.StyledHeading, Toc: false),
            (Role: ParagraphRole.Normal, Score: 0.20, Next: new string('x', 201), Previous: (ParagraphRole?)ParagraphRole.StyledHeading, Toc: false),
        };

        foreach (var testCase in cases)
        {
            var candidate = new CandidateDecision(true, testCase.Score, testCase.Role, 2);
            var actual = policy.Decide(Input(candidate, testCase.Toc, testCase.Next, testCase.Previous));
            var expected = LegacyDecision(candidate, testCase.Toc, testCase.Next, testCase.Previous);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Uses_source_identity_when_duplicate_text_is_present()
    {
        var policy = new PostClassificationPolicy();
        var unchanged = policy.Decide(Input(
            new CandidateDecision(false, 0.2, ParagraphRole.Normal, null), toc: false));
        var promoted = policy.Decide(Input(
            new CandidateDecision(false, 0.2, ParagraphRole.Normal, null), toc: true));

        Assert.Equal(ParagraphRole.Normal, unchanged.Role);
        Assert.Equal(ParagraphRole.HeadingCandidate, promoted.Role);
        Assert.Equal(0.80, promoted.Score);
    }

    [Fact]
    public void Applies_context_in_execution_order_after_toc_promotion()
    {
        var result = new PostClassificationPolicy().Decide(Input(
            new CandidateDecision(false, 0.2, ParagraphRole.Normal, null),
            toc: true,
            next: new string('x', 201),
            previous: ParagraphRole.StyledHeading));

        Assert.Equal(ParagraphRole.HeadingCandidate, result.Role);
        Assert.Equal(0.95, result.Score, precision: 10);
    }

    [Fact]
    public void Leaves_non_toc_non_heading_unchanged()
    {
        var candidate = new CandidateDecision(false, 0.2, ParagraphRole.Normal, null);
        var result = new PostClassificationPolicy().Decide(Input(
            candidate, toc: false, next: new string('x', 201), previous: ParagraphRole.StyledHeading));

        Assert.Equal(new PostClassificationDecision(ParagraphRole.Normal, 0.2, null), result);
    }

    [Fact]
    public void Does_not_mutate_source_or_toc_facts()
    {
        var source = Source("source-1", "Same text");
        var toc = new TocStructuralFeatures(new[] { "other-source" }.ToFrozenSet(StringComparer.Ordinal));
        var beforeSource = source with { };
        var beforeToc = toc with { };

        _ = new PostClassificationPolicy().Decide(new PostClassificationInput(
            source,
            new CandidateDecision(false, 0.2, ParagraphRole.Normal, null),
            toc,
            null,
            null));

        Assert.Equal(beforeSource, source);
        Assert.Equal(beforeToc, toc);
    }

    [Fact]
    public void Has_no_dependency_on_extraction_or_model_layers()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "DocxHeaderExtractor.Core",
            "Application", "Policy", "PostClassificationPolicy.cs");
        var text = File.ReadAllText(path);

        foreach (var forbidden in new[]
        {
            "OpenXml", "PdfFirstValidatedFallback",
            "ModelProposal", "ValidatedHeading", "Hierarchy", "provider", "HeadingHeuristics",
        })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    private static PostClassificationInput Input(
        CandidateDecision candidate,
        bool toc,
        string? next = null,
        ParagraphRole? previous = null) => new(
        Source("source-1", "Same text"),
        candidate,
        new TocStructuralFeatures((toc ? new[] { "source-1" } : Array.Empty<string>())
            .ToFrozenSet(StringComparer.Ordinal)),
        next,
        previous);

    private static PostClassificationDecision LegacyDecision(
        CandidateDecision candidate,
        bool toc,
        string? next,
        ParagraphRole? previous)
    {
        var role = candidate.Role;
        var score = candidate.Score;
        if (toc && role is ParagraphRole.Normal or ParagraphRole.HeadingCandidate)
        {
            role = ParagraphRole.HeadingCandidate;
            score = Math.Max(score, 0.80);
        }

        if (role != ParagraphRole.HeadingCandidate)
            return new PostClassificationDecision(role, score, candidate.GuessedLevel);
        if (next is { Length: > 200 }) score = Math.Min(1, score + 0.10);
        if (previous is ParagraphRole.StyledHeading) score = Math.Min(1, score + 0.05);
        return new PostClassificationDecision(role, score, candidate.GuessedLevel);
    }

    private static SourceParagraph Source(string id, string text) => new()
    {
        SourceId = id,
        SourceOrdinal = 0,
        Text = text,
        Style = new SourceStyleFacts(),
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
