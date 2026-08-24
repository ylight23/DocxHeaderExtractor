using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfSemanticRecoveryArtifactEvaluatorTests
{
    [Fact]
    public void ReplaysFrozenArtifactWithoutChangingRoutingOrTreatingSemanticFnAsTransport()
    {
        const string recovery = """
            {
              "usesGold": false,
              "report": {
                "EligibleUnresolvedBlocks": 2,
                "HeadingRoleProposals": 1,
                "CanonicalUniqueProposals": 1,
                "ValidatorAccepted": 1,
                "Decisions": [
                  {
                    "Id":"b35/line0", "SourceBlockId":"b35", "SourceLineIndex":0, "Page":3,
                    "SourceText":"Eurostat-OECD PPP Program", "Role":"BodySentence",
                    "CanonicalSpan":null, "ValidationStatus":"not-heading", "Reason":""
                  },
                  {
                    "Id":"b50/line0", "SourceBlockId":"b50", "SourceLineIndex":0, "Page":4,
                    "SourceText":"Western Asia", "Role":"HeadingTopic", "CanonicalSpan":"westernasia",
                    "ValidationStatus":"accepted", "Reason":""
                  }
                ]
              }
            }
            """;
        const string baseline = """
            [{
              "occurrence": {
                "GoldOccurrencesResolved":17,
                "Entries":[
                  {"Gold":"Eurostat-OECD PPP Program","ExpectedPdfPage":3,"Status":"candidate_occurrence_not_mapped_to_gold_anchor"},
                  {"Gold":"Western Asia","ExpectedPdfPage":4,"Status":"candidate_occurrence_not_mapped_to_gold_anchor"}
                ]
              }
            }]
            """;
        var key = AnswerKey.Parse("@body[1]/p[26] 2 # Eurostat-OECD PPP Program\n@body[1]/p[35] 2 # Western Asia");

        var result = PdfSemanticRecoveryArtifactEvaluator.Evaluate(recovery, baseline, key);

        Assert.True(result.EvaluationOnly);
        Assert.Equal(17, result.BaselineCorrectOccurrence);
        Assert.Equal(1, result.GoldCorrect);
        Assert.Equal(0, result.FalsePositive);
        Assert.Equal(1, result.NetCorrectOccurrenceGain);
        Assert.Equal(18, result.CombinedCorrectOccurrence);
        Assert.Equal(2, result.EligibleGoldOpportunities);
        Assert.Equal(0.5, result.GoldOpportunityRecall);
        Assert.Contains(result.Items, item => item.Id == "b35/line0" && item.Outcome == "semantic_false_negative");
        Assert.Contains(result.Items, item => item.Id == "b50/line0" && item.Outcome == "validated_true_recovery");
    }

    [Fact]
    public void RejectsAnArtifactThatClaimsGoldWasUsedAtRuntime()
    {
        const string recovery = """{"usesGold":true,"report":{}}""";
        const string baseline = """{"occurrence":{"GoldOccurrencesResolved":0,"Entries":[]}}""";

        Assert.Throws<InvalidOperationException>(() =>
            PdfSemanticRecoveryArtifactEvaluator.Evaluate(recovery, baseline, AnswerKey.Parse("")));
    }

    [Fact]
    public void ClassifiesUnknownCanonicalValidatorAndNonGoldOutcomesSeparately()
    {
        const string recovery = """
            {"usesGold":false,"report":{"EligibleUnresolvedBlocks":4,"HeadingRoleProposals":2,"CanonicalUniqueProposals":0,"ValidatorAccepted":0,"Decisions":[
              {"Id":"unknown","SourceBlockId":"unknown","SourceLineIndex":0,"Page":1,"SourceText":"Uncertain text","Role":"Uncertain","CanonicalSpan":null,"ValidationStatus":"unresolved","Reason":"missing-model-decision"},
              {"Id":"canonical","SourceBlockId":"canonical","SourceLineIndex":0,"Page":1,"SourceText":"Candidate title","Role":"HeadingTopic","CanonicalSpan":null,"ValidationStatus":"unresolved","Reason":"missing-pointer-span"},
              {"Id":"validator","SourceBlockId":"validator","SourceLineIndex":0,"Page":1,"SourceText":"Candidate title two","Role":"HeadingTopic","CanonicalSpan":"candidatetitletwo","ValidationStatus":"rejected","Reason":"scope"},
              {"Id":"non-gold","SourceBlockId":"non-gold","SourceLineIndex":0,"Page":1,"SourceText":"Body sentence","Role":"BodySentence","CanonicalSpan":null,"ValidationStatus":"not-heading","Reason":""}
            ]}}
            """;
        const string baseline = """{"occurrence":{"GoldOccurrencesResolved":0,"Entries":[]}}""";

        var result = PdfSemanticRecoveryArtifactEvaluator.Evaluate(recovery, baseline, AnswerKey.Parse(""));

        Assert.Equal("model_unknown", result.Items.Single(item => item.Id == "unknown").Outcome);
        Assert.Equal("canonical_unresolved", result.Items.Single(item => item.Id == "canonical").Outcome);
        Assert.Equal("validator_rejected", result.Items.Single(item => item.Id == "validator").Outcome);
        Assert.Equal("non_gold_eligible", result.Items.Single(item => item.Id == "non-gold").Outcome);
    }
}
