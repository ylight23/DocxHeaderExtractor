using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The measured regression, pinned to the document it was measured on.
/// <para>
/// On this file the model proposed three day headings — "DAY 2/3/4" — with valid aliases, exact
/// verbatim text and successful binding. Each sits alone in a one-cell table inside an open
/// appendix, so the parser scoped it <c>appendix_table</c>, the domain policy read that as a table
/// title, and <see cref="PdfProposalValidator"/> deleted it before hierarchy resolution.
/// The route audit then reported 23 validated structures against 20 emitted headings with no
/// rejection recorded anywhere, which is what made the loss hard to find: the harness had overruled
/// the model on what a heading means, silently.
/// </para>
/// <para>
/// This drives the real document through the real context builder and stops at that seam, so it
/// needs no provider call and cannot drift with model behaviour. It asserts both halves of the
/// split: the scope evidence still exists, and it no longer removes anything.
/// </para>
/// </summary>
public sealed class TableScopedHeadingSurvivalTests
{
    private static readonly string[] DayHeadingSourceIds =
    [
        "body[1]/tbl[2]/tr[1]/tc[1]/p[1]",
        "body[1]/tbl[3]/tr[1]/tc[1]/p[1]",
        "body[1]/tbl[4]/tr[1]/tc[1]/p[1]",
    ];

    [Fact]
    public void ATableScopedHeadingTheModelProposedReachesValidatedAuthority()
    {
        var contexts = ModelContexts();
        var decisions = DayHeadingSourceIds
            .Select(id => new PdfBlockDecision(id, PdfBlockRole.HeadingTopic, 1, "canonical-vnext-semantic-contract",
                new TextOffsetSpan(0, contexts[id].Source.RawText.Length)))
            .ToArray();

        var validated = PdfProposalValidator.Validate(contexts, decisions);

        Assert.Equal(DayHeadingSourceIds, validated.Select(item => item.SourceId).ToArray());
    }

    [Fact]
    public void TheHeuristicThatUsedToDeleteThemIsStillRecorded()
    {
        var contexts = ModelContexts();

        Assert.All(DayHeadingSourceIds, id =>
        {
            // The chain that removed them: the paragraph is inside a table, an appendix was open,
            // so the scope became appendix_table, which the domain policy classifies as a table
            // title, which proposes outline exclusion. Every link still holds — none of it decides.
            Assert.Equal("appendix_table", contexts[id].Source.StructuralScope);
            Assert.Equal(PdfDomainRole.TableTitle, contexts[id].Source.DomainRole);
            Assert.True(contexts[id].Source.DomainEvidence.ProposesOutlineExclusion);
            Assert.Equal($"domain-role-disagreement:{PdfDomainRole.TableTitle}",
                PdfProposalValidator.SemanticDisagreementOf(contexts[id]));
        });
    }

    [Fact]
    public void TheThreeHeadingsAreTheDayHeadingsAndNotSomeOtherTableText()
    {
        // Anchors the fixture: if the document or its source ids ever change, this fails loudly
        // rather than letting the two tests above pass on unrelated paragraphs.
        var contexts = ModelContexts();

        Assert.Collection(DayHeadingSourceIds.Select(id => contexts[id].Source.RawText),
            text => Assert.StartsWith("DAY 2", text, StringComparison.Ordinal),
            text => Assert.StartsWith("DAY 3", text, StringComparison.Ordinal),
            text => Assert.StartsWith("DAY 4", text, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, PdfCandidateContext> ModelContexts()
    {
        var state = State();
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        return DocxAuthorityPipeline.BuildForAudit(state, mode).ModelContexts;
    }

    private static DocxPolicyState State()
    {
        var docx = Path.Combine(RepositoryRoot(),
            "todo10_8", "heading_corpus_95_word", "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        return new DocxPolicyState(source, features, derived, policy.Paragraphs, policy.StyleTrust);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
