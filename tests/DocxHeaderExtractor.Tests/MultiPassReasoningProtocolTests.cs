using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99 capability-amplification (multi-pass reasoning) protocol tests. All model-free: they
/// exercise prompt construction, schema shape, and pure combination/dedupe logic directly, with no
/// provider call and no Gold access -- proving the Gold firewall holds structurally (these prompts
/// physically cannot reference Gold because no Gold value is ever passed into them).
/// </summary>
public sealed class MultiPassReasoningProtocolTests
{
    private static readonly string[] GoldLeakTokens =
        ["gold", "expected", "you missed", "should have found", "ground truth", "answer key"];

    [Fact]
    public void OmissionReviewPrompt_NeverMentionsGoldOrExpectedCounts()
    {
        var lowered = OmissionReviewPrompt.System.ToLowerInvariant();
        foreach (var token in GoldLeakTokens)
            Assert.DoesNotContain(token, lowered);
    }

    [Fact]
    public void OmissionReviewPrompt_BuildUser_CarriesOnlyPacketAndInventory_NoGoldParameterExists()
    {
        var packetJson = """{"occurrences":[{"i":0,"text":"Chapter 1"}]}""";
        var priorHeadings = new[] { new CeilingHeadingProposal(0, 0, 9, "CHAPTER") };
        var inventoryJson = OmissionReviewPrompt.BuildInventoryJson(priorHeadings);

        var user = OmissionReviewPrompt.BuildUser(packetJson, inventoryJson, "test-route");

        Assert.Contains("\"occurrences\"", user);
        Assert.Contains("\"inventory\"", user);
        Assert.Contains("CHAPTER", user);
        var lowered = user.ToLowerInvariant();
        foreach (var token in GoldLeakTokens)
            Assert.DoesNotContain(token, lowered);
    }

    [Fact]
    public void OmissionReviewResponseParser_ParsesNewAndSpanCorrectionMarkers()
    {
        var raw = """
        {"items":[
          {"i":0,"start":50,"end":80,"role":"SECTION","marker":"NEW"},
          {"i":0,"start":12,"end":40,"role":"ARTICLE","marker":"SPAN_CORRECTION","correctsProposalIndex":0}
        ]}
        """;

        var parsed = OmissionReviewResponseParser.Parse(raw);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal(OmissionReviewMarker.New, parsed.Items[0].Marker);
        Assert.Null(parsed.Items[0].CorrectsProposalIndex);
        Assert.Equal(OmissionReviewMarker.SpanCorrection, parsed.Items[1].Marker);
        Assert.Equal(0, parsed.Items[1].CorrectsProposalIndex);
    }

    [Fact]
    public void OmissionReviewResponseParser_RejectsUnknownMarker()
    {
        var raw = """{"items":[{"i":0,"start":1,"end":5,"role":"SECTION","marker":"MAYBE"}]}""";
        Assert.Throws<FormatException>(() => OmissionReviewResponseParser.Parse(raw));
    }

    [Fact]
    public void ExtractorB_And_ExtractorA_Prompts_AreGenuinelyDifferentInstructionText()
    {
        Assert.NotEqual(CeilingSemanticPrompt.System, ExhaustiveCoverageSemanticPrompt.System);
        Assert.NotEqual(CeilingSemanticPrompt.ProtocolVersion, ExhaustiveCoverageSemanticPrompt.ProtocolVersion);

        // Same closed role vocabulary and output shape -- only the discovery framing differs.
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(CeilingSemanticPrompt.Schema()),
            System.Text.Json.JsonSerializer.Serialize(ExhaustiveCoverageSemanticPrompt.Schema()));
    }

    [Fact]
    public void ExhaustiveCoveragePrompt_NeverMentionsGoldOrDocumentSpecificWording()
    {
        var lowered = ExhaustiveCoverageSemanticPrompt.System.ToLowerInvariant();
        foreach (var token in GoldLeakTokens)
            Assert.DoesNotContain(token, lowered);
        Assert.DoesNotContain("doc-0205", lowered);
        Assert.DoesNotContain("doc-0258", lowered);
    }

    [Fact]
    public void UnionExtractors_DedupesByCanonicalLocalSpan()
    {
        var extractorA = new[]
        {
            new CeilingHeadingProposal(0, 0, 10, "CHAPTER"),
            new CeilingHeadingProposal(0, 20, 30, "SECTION"),
        };
        var extractorB = new[]
        {
            new CeilingHeadingProposal(0, 0, 10, "CHAPTER"), // exact duplicate of extractorA[0]
            new CeilingHeadingProposal(0, 40, 55, "ARTICLE"), // genuinely new find
        };

        var union = MultiPassProposalCombiner.UnionExtractors(extractorA, extractorB);

        Assert.Equal(3, union.Count);
        Assert.Contains(union, h => h is { Start: 0, End: 10, Role: "CHAPTER" });
        Assert.Contains(union, h => h is { Start: 20, End: 30, Role: "SECTION" });
        Assert.Contains(union, h => h is { Start: 40, End: 55, Role: "ARTICLE" });
    }

    [Fact]
    public void ApplyOmissionReview_AddsNewItems_AndSubstitutesSpanCorrections()
    {
        var passA = new[]
        {
            new CeilingHeadingProposal(0, 0, 10, "CHAPTER"),
            new CeilingHeadingProposal(0, 20, 30, "SECTION"),
        };
        var review = new OmissionReviewResponse(
        [
            new OmissionReviewProposal(0, 45, 60, "ARTICLE", OmissionReviewMarker.New),
            new OmissionReviewProposal(0, 0, 12, "CHAPTER", OmissionReviewMarker.SpanCorrection, 0),
        ]);

        var combined = MultiPassProposalCombiner.ApplyOmissionReview(passA, review);

        Assert.Equal(3, combined.Count);
        Assert.DoesNotContain(combined, h => h is { Start: 0, End: 10 }); // corrected-away original span gone
        Assert.Contains(combined, h => h is { Start: 0, End: 12, Role: "CHAPTER" }); // corrected span present
        Assert.Contains(combined, h => h is { Start: 20, End: 30, Role: "SECTION" }); // untouched item survives
        Assert.Contains(combined, h => h is { Start: 45, End: 60, Role: "ARTICLE" }); // new item added
    }

    [Fact]
    public void VerifierPrompt_Schema_UsesClosedDecisionEnum()
    {
        Assert.Equal(["KEEP", "REJECT", "CORRECT_SPAN"], VerifierDecision.AllowedDecisions);
        Assert.True(VerifierDecision.IsAllowed("KEEP"));
        Assert.True(VerifierDecision.IsAllowed("REJECT"));
        Assert.True(VerifierDecision.IsAllowed("CORRECT_SPAN"));
        Assert.False(VerifierDecision.IsAllowed("MAYBE"));
        Assert.False(VerifierDecision.IsAllowed(null));
    }

    [Fact]
    public void VerifierResponseParser_RejectsUnknownDecision()
    {
        var raw = """{"decisions":[{"id":"H0001","decision":"MAYBE"}]}""";
        Assert.Throws<FormatException>(() => VerifierResponseParser.Parse(raw));
    }

    [Fact]
    public void VerifierResponseParser_RequiresBoundsForCorrectSpan()
    {
        var raw = """{"decisions":[{"id":"H0001","decision":"CORRECT_SPAN"}]}""";
        Assert.Throws<FormatException>(() => VerifierResponseParser.Parse(raw));
    }

    [Fact]
    public void ApplyVerifierDecisions_RejectDropsCandidate_KeepAndCorrectSpanSurvive()
    {
        var candidates = new Dictionary<string, CeilingHeadingProposal>
        {
            ["H0001"] = new CeilingHeadingProposal(0, 0, 10, "CHAPTER"),
            ["H0002"] = new CeilingHeadingProposal(0, 20, 30, "SECTION"),
            ["H0003"] = new CeilingHeadingProposal(0, 40, 50, "ARTICLE"),
        };
        var verifier = new VerifierResponse(
        [
            new VerifierProposalDecision("H0001", VerifierDecision.Keep),
            new VerifierProposalDecision("H0002", VerifierDecision.Reject),
            new VerifierProposalDecision("H0003", VerifierDecision.CorrectSpan, 41, 52),
        ]);

        var surviving = MultiPassProposalCombiner.ApplyVerifierDecisions(candidates, verifier);

        Assert.Equal(2, surviving.Count);
        Assert.Contains(surviving, h => h is { Start: 0, End: 10, Role: "CHAPTER" });
        Assert.DoesNotContain(surviving, h => h.Role == "SECTION");
        Assert.Contains(surviving, h => h is { Start: 41, End: 52, Role: "ARTICLE" });
    }

    [Fact]
    public void ApplyVerifierDecisions_MissingDecision_DefaultsToKeep_NeverSilentlyDropped()
    {
        var candidates = new Dictionary<string, CeilingHeadingProposal>
        {
            ["H0001"] = new CeilingHeadingProposal(0, 0, 10, "CHAPTER"),
        };
        var verifier = new VerifierResponse([]); // critic returned nothing for this id (e.g. truncated)

        var surviving = MultiPassProposalCombiner.ApplyVerifierDecisions(candidates, verifier);

        Assert.Single(surviving);
    }

    /// <summary>
    /// Proves the critic's KEEP decision is advisory only and can never itself constitute
    /// acceptance: the hard validator still independently checks every surviving proposal after
    /// verifier decisions are applied, and rejects a KEEP-decided proposal whose span is invalid
    /// against the actual source text.
    /// </summary>
    [Fact]
    public void VerifierKeepDecision_CannotBypassHardValidator_InvalidSpanStillRejected()
    {
        var candidates = new Dictionary<string, CeilingHeadingProposal>
        {
            // The model/critic both "KEEP" this, but its span is nonsensical (end before start) --
            // a shape the hard validator, not the critic, is responsible for catching.
            ["H0001"] = new CeilingHeadingProposal(0, 100, 5, "CHAPTER"),
        };
        var verifier = new VerifierResponse([new VerifierProposalDecision("H0001", VerifierDecision.Keep)]);

        var surviving = MultiPassProposalCombiner.ApplyVerifierDecisions(candidates, verifier);
        Assert.Single(surviving); // the combiner itself does not validate spans

        // The independently-owned binder is the actual authority: an inverted span cannot ever
        // produce a valid global binding, regardless of the critic's KEEP.
        var occurrence = new ReasoningSourceOccurrence
        {
            SourceOccurrenceId = "occ-0",
            SourceId = "para-0",
            SourceOrdinal = 0,
            RawText = "some short source text",
            SourceSpan = new DocxHeaderExtractor.Core.Models.StructuralSpan(0, 23),
            CandidateHint = new CandidateHint(false, 0, []),
        };
        var packet = CeilingPacketBuilder.Build([occurrence], new HashSet<string>(StringComparer.Ordinal) { "occ-0" });
        var bound = CeilingProposalBinder.Bind(surviving, packet, new HashSet<string>(StringComparer.Ordinal) { "occ-0" });

        Assert.Empty(bound); // validator/binder authority rejects it despite the critic's KEEP
    }

    [Fact]
    public void AssignCandidateIds_ProducesStableSequentialIds()
    {
        var candidates = new[]
        {
            new CeilingHeadingProposal(0, 0, 10, "CHAPTER"),
            new CeilingHeadingProposal(0, 20, 30, "SECTION"),
        };
        var withIds = MultiPassProposalCombiner.AssignCandidateIds(candidates);
        Assert.Equal(2, withIds.Count);
        Assert.True(withIds.ContainsKey("H0001"));
        Assert.True(withIds.ContainsKey("H0002"));
    }
}
