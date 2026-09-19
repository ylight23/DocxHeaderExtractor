using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfP3aSourceStageTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceUniverse =
        "eval/a99-closed-loop/pdf-gold-doc0252/source-universe.v1.json";
    private const string Manifest =
        "eval/a99-closed-loop/pdf-canary-072/experiment-manifest.v1.json";
    private const string ExpectedManifestHash =
        "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    [Fact]
    public void Source_universe_builder_preserves_DOC0252_aliases_order_text_and_hash_inputs()
    {
        var universe = Build();
        using var frozen = JsonDocument.Parse(Read(SourceUniverse));
        var rows = frozen.RootElement.GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal(1013, universe.Aliases.Count);
        Assert.Equal(rows.Length, universe.Aliases.Count);
        Assert.Equal(
            frozen.RootElement.GetProperty("sourceSha256").GetString(),
            universe.SourceSha256);

        foreach (var (alias, row) in universe.Aliases.Zip(rows))
        {
            Assert.Equal(row.GetProperty("sourceAlias").GetString(), alias.Alias);
            Assert.Equal(row.GetProperty("sourceId").GetString(), alias.SourceId);
            Assert.Equal(row.GetProperty("ordinal").GetInt32(), alias.SourceOrdinal);
            Assert.Equal(row.GetProperty("verbatimText").GetString(), alias.Text);
            Assert.Equal(row.GetProperty("page").GetInt32(), alias.SourceAnchor?.Page);
        }
    }

    [Fact]
    public void Source_universe_builder_is_deterministic_and_keeps_provider_out_of_the_stage()
    {
        var first = Build();
        var second = Build();

        Assert.Equal(first.SourceUniverseSha256, second.SourceUniverseSha256);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(
            first.Aliases.Select(alias => (alias.Alias, alias.SourceId, alias.SourceOrdinal, alias.Text)),
            second.Aliases.Select(alias => (alias.Alias, alias.SourceId, alias.SourceOrdinal, alias.Text)));
        Assert.Equal(0, first.Contexts.Count(context => context.Value.Source.ObservedEvidence
            .Any(evidence => evidence.Contains("provider", StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void Proposal_binder_accepts_a_valid_source_bound_heading()
    {
        var universe = Build();
        var alias = universe.Aliases[0];
        var decision = new PdfBlockDecision(
            alias.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "test",
            new TextOffsetSpan(0, alias.Text.Length),
            SemanticRole: PdfSemanticRole.SectionHeading);

        var bound = PdfSemanticProposalBinder.BindAndValidate(universe, [decision]);

        var heading = Assert.Single(bound);
        Assert.Equal(alias.SourceId, heading.SourceId);
        Assert.Equal(alias.Text, universe.Contexts[alias.SourceId].Source.RawText);
    }

    [Fact]
    public void Proposal_binder_rejects_unknown_source_and_invalid_source_span()
    {
        var universe = Build();
        var alias = universe.Aliases[0];
        var unknown = new PdfBlockDecision(
            "unknown-source",
            PdfBlockRole.HeadingTopic,
            1,
            "test",
            new TextOffsetSpan(0, 1));
        var invalidSpan = new PdfBlockDecision(
            alias.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "test",
            new TextOffsetSpan(0, alias.Text.Length + 1));

        Assert.Empty(PdfSemanticProposalBinder.BindAndValidate(universe, [unknown]));
        Assert.Empty(PdfSemanticProposalBinder.BindAndValidate(universe, [invalidSpan]));
    }

    [Fact]
    public void Proposal_binding_does_not_change_the_frozen_manifest_identity()
    {
        using var manifest = JsonDocument.Parse(Read(Manifest));
        Assert.Equal(ExpectedManifestHash, manifest.RootElement.GetProperty("manifestHash").GetString());
    }

    [Fact]
    public void Production_input_created_by_the_universe_is_stable()
    {
        var first = Build().CreateProductionInput("DOC-0252");
        var second = Build().CreateProductionInput("DOC-0252");

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(1013, first.SourceCatalog.Units.Count);
        Assert.NotNull(first.SourceEvidence);
        Assert.Equal(1013, first.SourceEvidence!.Count);
    }

    private static PdfCanonicalSourceUniverse Build() =>
        PdfCanonicalSourceUniverseBuilder.Build(Path(Pdf));

    private static string Read(string relativePath) => File.ReadAllText(Path(relativePath));

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(RepositoryRoot(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
