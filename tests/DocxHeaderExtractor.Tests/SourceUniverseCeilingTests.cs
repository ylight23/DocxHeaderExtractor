using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SEMANTIC_RECALL_CEILING = SOURCE_OCCURRENCE_UNIVERSE, not HEURISTIC_CANDIDATE_SET.
/// <para>
/// A candidate label is an attention hint. The load-bearing consequence is that declining to flag a
/// paragraph must not remove it from what the model is shown — otherwise the heuristic caps recall
/// silently, which is the same failure class as a heuristic veto at the output boundary, moved to
/// the front of the pipeline where it is even harder to see.
/// </para>
/// <para>
/// The fixture is chosen deliberately. On many documents in this corpus every non-empty paragraph
/// ends up a candidate, so they cannot test this invariant at all: with no non-candidates, "non-
/// candidates survive" is vacuously true. This file has all three roles present.
/// </para>
/// </summary>
public sealed class SourceUniverseCeilingTests
{
    [Fact]
    public void ParagraphsTheHeuristicDeclinedToFlagAreStillShownToTheModel()
    {
        var state = State();
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        var universe = DocxAuthorityPipeline.BuildForAudit(state, mode).ModelContexts;

        var nonCandidates = state.Paragraphs
            .Where(p => p.Role is not (ParagraphRole.HeadingCandidate or ParagraphRole.Empty))
            .ToArray();

        var roles = string.Join(" ", state.Paragraphs.GroupBy(p => p.Role)
            .OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
        Assert.True(nonCandidates.Length > 0,
            $"Fixture cannot exercise the invariant - it has no non-candidates. Roles: {roles}");

        Assert.All(nonCandidates, p => Assert.Contains(p.Source.SourceId, universe.Keys));
    }

    [Fact]
    public void TheCandidateHintStillDistinguishesSomething()
    {
        // The other direction. If this ever fails the label has become a constant, and any code
        // reading it as a filter is reading noise.
        var state = State();

        var candidates = state.Paragraphs.Count(p => p.Role is ParagraphRole.HeadingCandidate);
        var nonEmpty = state.Paragraphs.Count(p => p.Role is not ParagraphRole.Empty);

        Assert.InRange(candidates, 1, nonEmpty - 1);
    }

    private static DocxPolicyState State()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        var docx = Path.Combine(dir!.FullName, "todo10_8", "heading_corpus_95_word",
            "02_hop_dong_mua_sam", "026_WB_RFB_Goods_One_Envelope_2017.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        return new DocxPolicyState(source, features, derived, policy.Paragraphs, policy.StyleTrust);
    }
}
