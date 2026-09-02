using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfCandidateContractsTests
{
    [Fact]
    public void ContextBuilderKeepsParserTableFactAsSourceScope()
    {
        var line = Line("Net commitments 2025", 700);
        var block = Block("b1", line);
        var annotations = new[]
        {
            new PdfLineBlockAnnotation(line, false, false, true, false, "table-like"),
        };

        var context = PdfCandidateContextBuilder.Build([block], annotations)["b1"];

        Assert.Equal("table", context.Source.StructuralScope);
        Assert.Contains("table_like", context.Source.ObservedEvidence);
        Assert.Contains(context.Source.EvidenceDetails, evidence => evidence.Kind == "table_like" && evidence.Origin == "layout_parser");
        Assert.NotEmpty(context.Source.LineIds);
    }

    [Fact]
    public void ValidatorMakesInvalidModelPointerUnresolved()
    {
        var line = Line("Operating context", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision(
            "b1", PdfBlockRole.HeadingTopic, 0.9, "topic", new TextOffsetSpan(0, 99));

        var trace = Assert.Single(PdfProposalValidator.Trace(context, [decision]));

        Assert.Equal("invalid", trace.SpanStatus);
        Assert.Equal("unresolved", trace.ValidationStatus);
        Assert.False(PdfProposalValidator.IsEligibleHeading(decision, context["b1"]));
    }

    [Fact]
    public void ValidatorDoesNotLetSemanticRoleOverrideTableScope()
    {
        var line = Line("Assets liabilities", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, true, false, "table-like")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "model call");

        var trace = Assert.Single(PdfProposalValidator.Trace(context, [decision]));

        Assert.Equal("unresolved", trace.ValidationStatus);
        Assert.Equal("scope-conflict", trace.Reason);
        Assert.False(PdfProposalValidator.IsEligibleHeading(decision, context["b1"]));
    }

    [Fact]
    public void ValidatorRequiresPointerSpanForHeadingLikeProposal()
    {
        var line = Line("Operating context. The committee met.", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "topic");

        var trace = Assert.Single(PdfProposalValidator.Trace(context, [decision]));

        Assert.Equal("invalid", trace.SpanStatus);
        Assert.Equal("missing-pointer-span", trace.Reason);
        Assert.Equal("unresolved", trace.ValidationStatus);
    }

    [Fact]
    public void ValidatorCreatesSeparateValidatedHeadingOnlyFromGroundedPointer()
    {
        var line = Line("Operating context. The committee met.", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "topic", new TextOffsetSpan(0, 18));

        var validated = Assert.Single(PdfProposalValidator.Validate(context, [decision]));

        Assert.Equal("b1", validated.SourceId);
        Assert.Equal(new TextOffsetSpan(0, 18), validated.HeadingSpan);
        Assert.Equal("source-grounded-pointer-span", validated.ValidationBasis);
    }

    [Fact]
    public void ValidatorRejectsPointerInsideSourceToken()
    {
        var line = Line("Operating context. The committee met.", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "topic", new TextOffsetSpan(0, 5));

        var trace = Assert.Single(PdfProposalValidator.Trace(context, [decision]));

        Assert.Equal("invalid", trace.SpanStatus);
        Assert.Equal("invalid-pointer-boundary", trace.Reason);
        Assert.False(PdfProposalValidator.IsEligibleHeading(decision, context["b1"]));
    }

    [Fact]
    public void ValidatorAcceptsGenericWholeAndPrefixBoundaries()
    {
        var whole = Line("Operating context. The committee met.", 700);
        var prefix = Line("Heading title — body prose", 680);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("whole", whole), Block("prefix", prefix)],
            [new PdfLineBlockAnnotation(whole, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(prefix, false, false, false, false, "semantic-candidate")]);
        var decisions = new[]
        {
            new PdfBlockDecision("whole", PdfBlockRole.HeadingTopic, 0.99, "topic", new TextOffsetSpan(0, whole.Text.Length)),
            new PdfBlockDecision("prefix", PdfBlockRole.HeadingTopic, 0.99, "topic", new TextOffsetSpan(0, "Heading title".Length)),
        };

        var validated = PdfProposalValidator.Validate(contexts, decisions);

        Assert.Equal(2, validated.Count);
    }

    [Fact]
    public void HierarchyResolverUsesArabicMarkerDepthForParent()
    {
        var first = Line("1. Parent", 700);
        var second = Line("1.1. Child", 680);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("b1", first), Block("b2", second)],
            [new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate")]);
        var validated = PdfProposalValidator.Validate(contexts,
        [
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, 9)),
            new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, 10)),
        ]);

        var structures = PdfHierarchyResolver.Resolve(validated, contexts);

        Assert.Equal(1, structures[0].Level);
        Assert.Equal(2, structures[1].Level);
        Assert.Equal("b1", structures[1].ParentId);
        Assert.Equal("marker-resolved", structures[1].ParentResolution);
    }

    [Fact]
    public void ContextBuilderBuildsActiveStackFromLoosePdfLabelMarker()
    {
        var first = Line("Điều 3 Phạm vi điều chỉnh", 700);
        var second = Line("Điều 4 Giải thích từ ngữ", 680);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("b1", first), Block("b2", second)],
            [new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate")]);

        Assert.Contains("marker:loose_labelled", contexts["b1"].Source.ObservedEvidence);
        Assert.Equal("loose_labelled", contexts["b1"].Source.Marker?.Family);
        Assert.Contains(contexts["b2"].ActiveHeadingStack, item => item.StartsWith("b1:", StringComparison.Ordinal));
    }

    [Fact]
    public void HierarchyResolverUsesSpacedPdfArabicPath()
    {
        var first = Line("4 General requirements", 700);
        var second = Line("4 2 2 Calculating the value", 680);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("b1", first), Block("b2", second)],
            [new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate")]);
        var validated = PdfProposalValidator.Validate(contexts,
        [
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, first.Text.Length)),
            new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, second.Text.Length)),
        ]);

        var structures = PdfHierarchyResolver.Resolve(validated, contexts);

        Assert.Equal(3, structures[1].Level);
        Assert.Null(structures[1].ParentId);
        Assert.Equal("unresolved", structures[1].ParentResolution);
    }

    [Fact]
    public void ContextBuilderScopesFormalGrammarAsNonOutlineContent()
    {
        var line = Line("cache-control = 1#cache-directive", 700);
        var context = PdfCandidateContextBuilder.Build(
            [Block("b1", line)], [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "", new TextOffsetSpan(0, line.Text.Length));

        var trace = Assert.Single(PdfProposalValidator.Trace(context, [decision]));

        Assert.Equal("code_or_grammar", context["b1"].Source.StructuralScope);
        Assert.Contains("formal_syntax_shape", context["b1"].Source.ObservedEvidence);
        Assert.Equal("unresolved", trace.ValidationStatus);
        Assert.False(PdfProposalValidator.IsEligibleHeading(decision, context["b1"]));
    }

    [Fact]
    public void ContextBuilderScopesDenseEarlyDotLeaderEntriesAsTableOfContents()
    {
        var first = Line("1. Introduction ........ 3", 700);
        var second = Line("2. Architecture ........ 7", 680);
        var third = Line("3. Security ........ 11", 660);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("b1", first), Block("b2", second), Block("b3", third)],
            [new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(third, false, false, false, false, "semantic-candidate")]);
        var decision = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.99, "", new TextOffsetSpan(0, first.Text.Length));

        Assert.All(contexts.Values, context =>
        {
            Assert.Equal("table_of_contents", context.Source.StructuralScope);
            Assert.Contains("toc_entry_cluster", context.Source.ObservedEvidence);
            Assert.Empty(context.ActiveHeadingStack);
        });
        Assert.False(PdfProposalValidator.IsEligibleHeading(decision, contexts["b1"]));
    }

    [Fact]
    public void RfcStructuralScopesRejectReferencesIndexAndAbnfButKeepAppendixNamespace()
    {
        var introduction = Line("1. Introduction", 700);
        var references = Line("References", 680);
        var referenceEntry = Line("[RFC9110] Fielding, HTTP Semantics", 660);
        var appendix = Line("Appendix A. Collected ABNF", 640);
        var grammar = Line("cache-control = 1#cache-directive", 620);
        var index = Line("Index", 600);
        var indexTerm = Line("cache-control, 12", 580);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("intro", introduction), Block("references", references), Block("reference", referenceEntry),
             Block("appendix", appendix), Block("grammar", grammar), Block("index", index), Block("term", indexTerm)],
            [new PdfLineBlockAnnotation(introduction, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(references, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(referenceEntry, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(appendix, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(grammar, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(index, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(indexTerm, false, false, false, false, "semantic-candidate")]);
        var proposed = new PdfBlockDecision("reference", PdfBlockRole.HeadingTopic, .99, "", new TextOffsetSpan(0, referenceEntry.Text.Length));

        Assert.Equal("reference_list", contexts["reference"].Source.StructuralScope);
        Assert.Equal("appendix", contexts["appendix"].Source.StructuralScope);
        Assert.Equal("code_or_grammar", contexts["grammar"].Source.StructuralScope);
        Assert.Equal("index_terms", contexts["term"].Source.StructuralScope);
        Assert.False(PdfProposalValidator.IsEligibleHeading(proposed, contexts["reference"]));
    }

    [Fact]
    public void RfcProseSectionReferenceIsAParserFactNotAnOutlineNode()
    {
        var prose = Line("See Section 5.6.1 for cache directives.", 700);
        var contexts = PdfCandidateContextBuilder.Build([Block("prose", prose)],
            [new PdfLineBlockAnnotation(prose, false, false, false, false, "semantic-candidate")]);
        var proposed = new PdfBlockDecision("prose", PdfBlockRole.HeadingTopic, .99, "", new TextOffsetSpan(0, prose.Text.Length));

        Assert.Equal(PdfDomainRole.InlineClauseReference, contexts["prose"].Source.DomainRole);
        Assert.False(PdfProposalValidator.IsEligibleHeading(proposed, contexts["prose"]));
    }

    [Fact]
    public void LegalDomainPolicyResolvesMarkerTreeAndRejectsAmendmentAnnotation()
    {
        var part = Line("PHAN I QUY DINH CHUNG", 700);
        var chapter = Line("CHUONG I NHUNG QUY DINH CHUNG", 680);
        var article = Line("DIEU 1. Pham vi dieu chinh", 660);
        var amendment = Line("Khoan 3 Dieu 3 duoc sua doi, bo sung boi Nghi dinh so 14/2022/ND-CP", 640);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("part", part), Block("chapter", chapter), Block("article", article), Block("amendment", amendment)],
            [new PdfLineBlockAnnotation(part, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(chapter, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(article, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(amendment, false, false, false, false, "semantic-candidate")]);
        var decisions = new[]
        {
            new PdfBlockDecision("part", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, part.Text.Length)),
            new PdfBlockDecision("chapter", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, chapter.Text.Length)),
            new PdfBlockDecision("article", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, article.Text.Length)),
            new PdfBlockDecision("amendment", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, amendment.Text.Length)),
        };

        var validated = PdfProposalValidator.Validate(contexts, decisions);
        var structures = PdfHierarchyResolver.Resolve(validated, contexts);

        Assert.Equal(PdfDomainRole.LegalPart, contexts["part"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.AmendmentAnnotation, contexts["amendment"].Source.DomainRole);
        Assert.DoesNotContain(validated, item => item.SourceId == "amendment");
        Assert.Equal("part", structures.Single(item => item.SourceId == "chapter").ParentId);
        Assert.Equal("chapter", structures.Single(item => item.SourceId == "article").ParentId);
    }

    [Fact]
    public void Domain_detector_emits_evidence_without_materializing_structural_authority()
    {
        var article = new PdfSourceFacts(
            "article", "DIEU 1. Pham vi dieu chinh", 1, 1, 0, 100, 100, 90,
            "document_body", [])
        {
            Marker = PdfMarkerFactsParser.Parse("DIEU 1. Pham vi dieu chinh"),
        };
        var amendment = new PdfSourceFacts(
            "amendment", "Khoan 3 Dieu 3 duoc sua doi, bo sung", 1, 1, 0, 80, 100, 70,
            "document_body", []);

        var articleEvidence = DocumentDomainPolicy.Observe(article, "legal");
        var amendmentEvidence = DocumentDomainPolicy.Observe(amendment, "legal");

        Assert.Equal(PdfDomainRole.LegalArticle, articleEvidence.Role);
        Assert.Equal(4, articleEvidence.ProposedLevel);
        Assert.True(articleEvidence.IsStructuralRole);
        Assert.False(articleEvidence.ProposesOutlineExclusion);
        Assert.Equal(PdfDomainRole.AmendmentAnnotation, amendmentEvidence.Role);
        Assert.Null(amendmentEvidence.ProposedLevel);
        Assert.False(amendmentEvidence.IsStructuralRole);
        Assert.True(amendmentEvidence.ProposesOutlineExclusion);
    }

    [Fact]
    public void ProcurementPolicyKeepsStructuralMarkersAndRejectsTemplateFields()
    {
        var partOne = Line("PART 1 - BIDDING PROCEDURES", 700);
        var partTwo = Line("PART 2 - CONDITIONS OF CONTRACT", 680);
        var section = Line("SECTION I - Instructions to Bidders", 660);
        var field = Line("Employer: [insert name]", 640);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("part-1", partOne), Block("part-2", partTwo), Block("section", section), Block("field", field)],
            [new PdfLineBlockAnnotation(partOne, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(partTwo, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(section, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(field, false, false, false, false, "semantic-candidate")]);
        var decisions = new[]
        {
            new PdfBlockDecision("part-1", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, partOne.Text.Length)),
            new PdfBlockDecision("part-2", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, partTwo.Text.Length)),
            new PdfBlockDecision("section", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, section.Text.Length)),
            new PdfBlockDecision("field", PdfBlockRole.HeadingTopic, .9, "", new TextOffsetSpan(0, field.Text.Length)),
        };

        var validated = PdfProposalValidator.Validate(contexts, decisions);
        var structures = PdfHierarchyResolver.Resolve(validated, contexts);

        Assert.Equal(PdfDomainRole.ProcurementSection, contexts["section"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.FormFieldLabel, contexts["field"].Source.DomainRole);
        Assert.DoesNotContain(validated, item => item.SourceId == "field");
        Assert.Equal("part-2", structures.Single(item => item.SourceId == "section").ParentId);
    }

    [Fact]
    public void FinancialAndMeetingPoliciesUseDocumentLocalMarkersWithoutPromotingCaptions()
    {
        var financialSection = Line("Section I: Executive Summary", 700);
        var financialNote = Line("NOTE A - SUMMARY OF SIGNIFICANT ACCOUNTING POLICIES", 680);
        var financialMarker = Line("Notes to Financial Statements", 660);
        var financialContexts = PdfCandidateContextBuilder.Build(
            [Block("financial", financialSection), Block("note", financialNote), Block("financial-marker", financialMarker)],
            [new PdfLineBlockAnnotation(financialSection, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(financialNote, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(financialMarker, false, false, false, false, "semantic-candidate")]);
        var session = Line("Session I: ICP 2021 cycle results", 700);
        var agenda = Line("D1.01 - Global updates", 680);
        var minutes = Line("Minutes of the technical advisory group meeting", 660);
        var caption = Line("Table 1: Selected Financial Data", 640);
        var meetingContexts = PdfCandidateContextBuilder.Build(
            [Block("session", session), Block("agenda", agenda), Block("minutes", minutes), Block("caption", caption)],
            [new PdfLineBlockAnnotation(session, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(agenda, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(minutes, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(caption, false, false, false, false, "semantic-candidate")]);

        Assert.Equal(PdfDomainRole.FinancialSection, financialContexts["financial"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.FinancialNote, financialContexts["note"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.MeetingSession, meetingContexts["session"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.MeetingAgenda, meetingContexts["agenda"].Source.DomainRole);
        Assert.Equal(PdfDomainRole.FigureOrBoxCaption, meetingContexts["caption"].Source.DomainRole);
    }

    [Fact]
    public void ScopeTrackerKeepsQuotedAmendmentStructureOutOfHostOutlineNamespace()
    {
        var trigger = Line("Article 2 is amended as follows:", 700);
        var chapter = Line("\u201cChapter II FOOD SAFETY REQUIREMENTS", 680);
        var article = Line("Article 4. Food manufacturers", 660);
        var close = Line("\u201d", 640);
        var outer = Line("Chapter III IMPLEMENTATION", 620);
        var contexts = PdfCandidateContextBuilder.Build(
            [Block("trigger", trigger), Block("embedded-chapter", chapter), Block("embedded-article", article), Block("close", close), Block("outer", outer)],
            [new PdfLineBlockAnnotation(trigger, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(chapter, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(article, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(close, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(outer, false, false, false, false, "semantic-candidate")]);

        Assert.Equal("embedded_amendment", contexts["embedded-chapter"].Source.StructuralScope);
        Assert.Equal("trigger", contexts["embedded-article"].Source.ScopeHostSourceId);
        Assert.True(contexts["embedded-article"].Source.InsideQuote);
        Assert.Equal("document_body", contexts["outer"].Source.StructuralScope);
    }

    [Fact]
    public void RankerKeepsEveryCandidateAndEscalatesOnlyByFeatureEvidence()
    {
        var marker = Line("Chapter 1 Scope", 700);
        var table = Line("Revenue 2025", 680);
        var blocks = new[] { Block("marker", marker), Block("table", table) };
        var contexts = PdfCandidateContextBuilder.Build(blocks,
        [
            new PdfLineBlockAnnotation(marker, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(table, false, false, true, false, "table-like"),
        ]);

        var ranked = PdfCandidateRanker.Rank(blocks, contexts);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(ModelTier.Deterministic, ranked.Single(item => item.SourceId == "marker").Tier);
        Assert.Equal(ModelTier.Review, ranked.Single(item => item.SourceId == "table").Tier);
        Assert.Contains("table_scope", ranked.Single(item => item.SourceId == "table").NegativeSignals);
    }

    [Fact]
    public void RankerPrefersTightMarkerTitleCompositeOverItsMarkerOnlyFragment()
    {
        var marker = Line("Chapter I", 700);
        var title = Line("GENERAL PROVISIONS", 680);
        var single = Block("single", marker);
        var composite = new PdfSemanticBlock("composite", [marker, title], PdfStyleClusterProfile.StyleOf(marker),
            1, 700, 680, marker.Left, marker.Right, "Chapter I GENERAL PROVISIONS");
        var contexts = PdfCandidateContextBuilder.Build([single, composite],
        [
            new PdfLineBlockAnnotation(marker, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(title, false, false, false, false, "semantic-candidate"),
        ]);

        var ranked = PdfCandidateRanker.Rank([single, composite], contexts);

        Assert.Equal("composite", ranked[0].SourceId);
        Assert.Contains("marker_title_composite", ranked[0].PositiveSignals);
    }

    [Fact]
    public void RankerDefersLongMarkerBodyWindowBehindAtomicHeading()
    {
        var heading = Line("Article 2 Scope", 700);
        var bodyOne = Line("This Act provides for national cyber security.", 680);
        var bodyTwo = Line("It applies to agencies and organizations.", 660);
        var bodyThree = Line("Implementation follows this Article.", 640);
        var atomic = Block("atomic", heading);
        var window = new PdfSemanticBlock("window", [heading, bodyOne, bodyTwo, bodyThree], PdfStyleClusterProfile.StyleOf(heading),
            1, 700, 640, heading.Left, heading.Right, string.Join(" ", new[] { heading.Text, bodyOne.Text, bodyTwo.Text, bodyThree.Text }));
        var contexts = PdfCandidateContextBuilder.Build([atomic, window],
        [
            new PdfLineBlockAnnotation(heading, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(bodyOne, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(bodyTwo, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(bodyThree, false, false, false, false, "semantic-candidate"),
        ]);

        var ranked = PdfCandidateRanker.Rank([window, atomic], contexts);

        Assert.Equal("atomic", ranked[0].SourceId);
        Assert.Contains("long_marker_body_window", ranked.Single(item => item.SourceId == "window").NegativeSignals);
        Assert.Contains("marker_body_boundary", ranked.Single(item => item.SourceId == "window").AmbiguitySignals);
    }

    [Fact]
    public void LossInstrumentationPreservesPerSourceFirstLossAndAggregateCounts()
    {
        var first = Line("Heading title", 700);
        var second = Line("Heading title", 680);
        var unknown = Line("Body sentence", 660);
        var contexts = new Dictionary<string, PdfCandidateContext>(PdfCandidateContextBuilder.Build(
            [Block("b1", first), Block("b2", second), Block("b3", unknown)],
            [new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate"),
             new PdfLineBlockAnnotation(unknown, false, false, false, false, "semantic-candidate")]));
        contexts["b1"] = contexts["b1"] with
        {
            Source = contexts["b1"].Source with { SourceOrdinal = 7 },
        };
        var validSpan = new TextOffsetSpan(0, first.Text.Length);
        var decisions = new[]
        {
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, .9, "topic", validSpan,
                SemanticRole: PdfSemanticRole.TopicHeading, RawRole: "heading_topic",
                SpanResponseStatus: "valid-boundary", ProposedSpan: validSpan),
            new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, .9, "topic",
                new TextOffsetSpan(0, 3), SemanticRole: PdfSemanticRole.TopicHeading, RawRole: "heading_topic",
                SpanResponseStatus: "invalid-boundary", ProposedSpan: new TextOffsetSpan(0, 3)),
            new PdfBlockDecision("b3", PdfBlockRole.Uncertain, .1, "role",
                SemanticRole: PdfSemanticRole.Unknown, RawRole: "invented_role"),
        };

        var traces = PdfProposalValidator.Trace(contexts, decisions);
        var instrumentation = PdfProposalValidator.BuildLossInstrumentation(contexts, decisions, traces);

        Assert.Equal(3, instrumentation.RoleProposalsTotal);
        Assert.Equal(1, instrumentation.UnknownRoleCount);
        Assert.Equal(2, instrumentation.SpanRequested);
        Assert.Equal(1, instrumentation.SpanInvalidBoundary);
        Assert.Equal(1, instrumentation.SpanValidBoundary);
        Assert.Equal(1, instrumentation.ValidatorAccepted);
        Assert.Equal(1, instrumentation.ValidatorRejected);
        Assert.Equal(1, instrumentation.ValidatorRejectedByReason["invalid-pointer-boundary"]);
        Assert.Equal(7, instrumentation.Items.Single(item => item.SourceId == "b1").SourceOrdinal);
        Assert.Equal("INVALID_BOUNDARY", instrumentation.Items.Single(item => item.SourceId == "b2").FirstLoss);
        Assert.Equal(new TextOffsetSpan(0, 3), instrumentation.Items.Single(item => item.SourceId == "b2").ProposedSpan);
        Assert.Equal("ROLE_UNKNOWN", instrumentation.Items.Single(item => item.SourceId == "b3").FirstLoss);
    }

    private static PdfLine Line(string text, double y) => new(
        1, y, 14, text, 0.8, "", 0, 72, 420, "serif", "0.00,0.20,0.40");

    private static PdfSemanticBlock Block(string id, PdfLine line) => new(
        id, [line], PdfStyleClusterProfile.StyleOf(line), line.Page, line.Y, line.Y, line.Left, line.Right, line.Text);
}
