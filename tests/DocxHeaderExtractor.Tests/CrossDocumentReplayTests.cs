using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The acceptance gate for removing the domain-heuristic veto, pinned across every document that
/// has a usable recording.
/// <para>
/// The gate is deliberately one-sided. Removing a veto can only add occurrences, so the thing that
/// would make it unsafe is a heading that used to reach canonical output and no longer does. That
/// is asserted here. Whether a newly surviving occurrence is correct is a question for the gold,
/// not for a test, and is recorded in eval/a99-closed-loop/semantic-veto-removal-v1 instead.
/// </para>
/// <para>
/// The before-set is the counterfactual computed from the same replay, not an older artifact. The
/// recordings on disk were produced by earlier harness versions that differ in more than the veto,
/// so scoring against them would confound this change with those. The counterfactual was validated
/// against DOC-0256, whose real pre-fix run is known: it reproduces 20 canonical and 16 matched
/// exactly.
/// </para>
/// </summary>
public sealed class CrossDocumentReplayTests
{
    /// <summary>The clauses commit 3886a5a removed from <c>IsEligibleHeading</c>.</summary>
    private static readonly string[] OldVetoScopes =
        ["table", "running_page_artifact", "table_of_contents", "code_or_grammar", "reference_list", "index_terms"];

    [Theory]
    [InlineData("DOC-0001")]
    [InlineData("DOC-0116")]
    [InlineData("DOC-0252")]
    [InlineData("DOC-0258")]
    [InlineData("DOC-0256")]
    public async Task NoHeadingThatUsedToReachCanonicalOutputIsLost(string doc)
    {
        var (rows, _) = await ReplayAsync(doc);

        // Every occurrence the old predicate would have kept must still be here. A removed veto
        // cannot subtract, so any loss would mean this change did something it was never meant to.
        var before = rows.Where(row => !row.VetoedBefore).Select(row => row.SourceId).ToHashSet(StringComparer.Ordinal);
        var after = rows.Select(row => row.SourceId).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(before.Except(after));
    }

    [Fact]
    public async Task TheCounterfactualReproducesTheKnownPreFixRunOnDoc0256()
    {
        // Validates the method the other four documents depend on. If this drifts, every
        // before-column in the cross-document table is suspect.
        var (rows, _) = await ReplayAsync("DOC-0256");

        Assert.Equal(23, rows.Count);
        Assert.Equal(20, rows.Count(row => !row.VetoedBefore));
    }

    [Theory]
    [InlineData("DOC-0252", 0)]
    [InlineData("DOC-0256", 3)]
    [InlineData("DOC-0258", 1)]
    [InlineData("DOC-0116", 111)]
    public async Task TheBlastRadiusPerDocumentIsWhatWasMeasured(string doc, int newlySurviving)
    {
        var (rows, _) = await ReplayAsync(doc);

        Assert.Equal(newlySurviving, rows.Count(row => row.VetoedBefore));
    }

    private sealed record Row(string SourceId, string Text, bool VetoedBefore);

    private static async Task<(IReadOnlyList<Row> Rows, int CallsBeyondRecording)> ReplayAsync(string doc)
    {
        var root = TestRepository.Root();
        using var asset = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests",
            "DocxHeaderExtractor.Tests", "Assets", $"{doc}.frozen-responses.json")));
        var srcRel = asset.RootElement.TryGetProperty("sourcePath", out var sp)
            ? sp.GetString()!
            : "todo10_8/heading_corpus_95_word/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.docx";
        var docx = Path.Combine(root, srcRel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var built = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        var state = new DocxPolicyState(source, features, derived, built.Paragraphs, built.StyleTrust);
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());

        using var replay = new FrozenReplyClassifier(asset.RootElement.GetProperty("responses")
            .EnumerateArray().Select(item => item.GetString()!).ToArray());
        var authority = await DocxAuthorityPipeline.RunAsync(state, mode, replay);

        var contexts = DocxAuthorityPipeline.BuildForAudit(state, mode).ModelContexts;
        var rows = HeadingOutlineProjection.Project(authority.Structure, authority.EmittedElementIds)
            .Select(heading =>
            {
                var id = heading.StableId ?? string.Empty;
                contexts.TryGetValue(id, out var context);
                var scope = context?.Source.StructuralScope;
                var vetoed = (scope is not null && Array.IndexOf(OldVetoScopes, scope) >= 0) ||
                    (context?.Source.DomainEvidence.ProposesOutlineExclusion ?? false);
                return new Row(id, heading.Text ?? string.Empty, vetoed);
            })
            .ToArray();
        return (rows, replay.CallsBeyondRecording);
    }

}
