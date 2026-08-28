using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class CandidatePolicyExtractionTests
{
    [Fact]
    public void Application_policy_matches_legacy_classifier_decision()
    {
        var options = new ExtractionOptions();
        var features = EmptyFeatures();
        var legacy = Paragraph("1. Scope", bold: true);
        var routed = Paragraph("1. Scope", bold: true);

        HeadingHeuristics.Classify(legacy, options);
        var decision = new HeadingCandidatePolicy().Apply(
            new CandidatePolicyInput(routed, features, options));

        Assert.Equal(legacy.IsCandidate, decision.IsCandidate);
        Assert.Equal(legacy.Score, decision.Score);
        Assert.Equal(legacy.Role, decision.Role);
        Assert.Equal(legacy.GuessedLevel, decision.GuessedLevel);
    }

    [Fact]
    public void Candidate_decision_contains_only_candidate_stage_fields()
    {
        var names = typeof(CandidateDecision).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            ["GuessedLevel", "IsCandidate", "Role", "Score"],
            names.OrderBy(name => name));
        Assert.DoesNotContain("ValidatedLevel", names);
        Assert.DoesNotContain("Parent", names);
        Assert.DoesNotContain("ModelProposal", names);
    }

    [Fact]
    public void Policy_does_not_mutate_source_document()
    {
        var source = new SourceDocument
        {
            DocumentId = "policy.docx",
            FileName = "policy.docx",
            SourcePath = "policy.docx",
            SourceKind = "docx",
            Paragraphs = [new SourceParagraph
            {
                SourceId = "p0",
                SourceOrdinal = 0,
                Text = "Heading",
                Style = new SourceStyleFacts { FontSizePt = 12 },
                Numbering = new SourceNumberingFacts(),
                Layout = new SourceLayoutFacts(),
            }],
        };
        var before = source.Paragraphs[0];
        var paragraph = Paragraph("Heading");

        _ = new HeadingCandidatePolicy().Apply(
            new CandidatePolicyInput(paragraph, new DocumentFeatureDeriver().Derive(source), new ExtractionOptions()));

        Assert.Equal(before, source.Paragraphs[0]);
    }

    private static DerivedDocumentFeatures EmptyFeatures() =>
        new DocumentFeatureDeriver().Derive(new SourceDocument
        {
            DocumentId = "empty.docx",
            FileName = "empty.docx",
            SourcePath = "empty.docx",
            SourceKind = "docx",
            Paragraphs = [],
        });

    private static SlimParagraph Paragraph(string text, bool bold = false) => new()
    {
        Index = 0,
        StableId = "p0",
        Text = text,
        Bold = bold,
        FontSizePt = 12,
        BodyFontSizePt = 11,
    };
}
