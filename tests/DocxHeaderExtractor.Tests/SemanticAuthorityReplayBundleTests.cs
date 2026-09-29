using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticAuthorityReplayBundleTests
{
    [Fact]
    public void BundleRoundTripPreservesFrozenProposalsAndHash()
    {
        var bundle = Bundle([Whole("S0001", "SECTION")]);
        var json = JsonSerializer.Serialize(bundle);
        var restored = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(json)!;

        Assert.Equal(bundle.Proposals, restored.Proposals);
        Assert.Equal(bundle.AliasCatalog.Select(alias =>
            (alias.Alias, alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceSpan)),
            restored.AliasCatalog.Select(alias =>
            (alias.Alias, alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceSpan)));
        Assert.Equal(bundle.ProposalHash, restored.ProposalHash);
        Assert.Equal(bundle.BundleHash, restored.BundleHash);
        Assert.Empty(SemanticAuthorityReplay.Validate(restored));
    }

    [Fact]
    public void CanonicalHashesIgnorePropertyOrderAndRunMetadata()
    {
        var first = SemanticAuthorityReplayHashing.Canonicalize("{\"b\":2,\"a\":1}");
        var second = SemanticAuthorityReplayHashing.Canonicalize("{\"a\":1,\"b\":2}");
        var bundle = Bundle([Whole("S0001", "SECTION")]);
        var changedRun = bundle with { RunId = "another-run", Commit = "different-commit", CreatedAt = DateTimeOffset.UtcNow };

        Assert.Equal(first, second);
        Assert.Equal(bundle.ProposalHash, changedRun.ProposalHash);
        Assert.Equal(bundle.BundleHash, SemanticAuthorityReplayHashing.BundleHash(changedRun));
    }

    [Fact]
    public void ReplayUsesOnlyFrozenAliasesAndMakesNoModelCall()
    {
        var bundle = Bundle([Whole("S0001", "SECTION")]);

        var replay = SemanticAuthorityReplay.Replay(bundle);

        Assert.True(replay.Pipeline.SourceHashVerified);
        Assert.Equal("SECTION", Assert.Single(replay.Pipeline.BoundHeadings).SemanticRole);
        Assert.Single(replay.MaterializedProjection);
    }

    [Fact]
    public void ReplayRejectsSourceAndSourceUniverseMismatches()
    {
        var bundle = Bundle([Whole("S0001", "SECTION")]);

        var source = Assert.Throws<SemanticAuthorityReplayRejectedException>(() =>
            SemanticAuthorityReplay.Replay(bundle, expectedSourceHash: "changed-source"));
        var universe = Assert.Throws<SemanticAuthorityReplayRejectedException>(() =>
            SemanticAuthorityReplay.Replay(bundle, expectedSourceUniverseHash: "changed-universe"));

        Assert.Contains("SOURCE_HASH_MISMATCH", source.Errors);
        Assert.Contains("SOURCE_UNIVERSE_HASH_MISMATCH", universe.Errors);
    }

    [Fact]
    public void ReplayRejectsAliasProposalAndContractMutation()
    {
        var bundle = Bundle([Whole("S0001", "SECTION")]);
        var changedAlias = Alias("S0001", "changed", 1);
        var changedProposal = Whole("S0001", "CHANGED");

        var aliasErrors = SemanticAuthorityReplay.Validate(bundle with { AliasCatalog = [changedAlias] });
        var proposalErrors = SemanticAuthorityReplay.Validate(bundle with { Proposals = [changedProposal] });
        var contractErrors = SemanticAuthorityReplay.Validate(bundle with { SemanticContractHash = "changed-contract" });

        Assert.Contains("ALIAS_CATALOG_HASH_MISMATCH", aliasErrors);
        Assert.Contains("PROPOSAL_HASH_MISMATCH", proposalErrors);
        Assert.Contains("SEMANTIC_CONTRACT_HASH_MISMATCH", contractErrors);
    }

    [Fact]
    public void ProposalHashStaysIdenticalWhenOnlyPostModelBindingChanges()
    {
        var bundle = Bundle(
            [Alias("S0001", "SECTION", 1)],
            [new CanonicalSemanticProposal(
                "S0001", true, "SECTION", SemanticRole: "SECTION",
                SelectionMode: CanonicalSemanticSelectionMode.VerbatimText)]);
        var changedAliases = new[] { Alias("S0001", "DIFFERENT SOURCE TEXT", 1) };

        var original = CanonicalSemanticPipeline.RunAliases(bundle.AliasCatalog, bundle.Proposals, bundle.SourceHash);
        var changed = CanonicalSemanticPipeline.RunAliases(changedAliases, bundle.Proposals, bundle.SourceHash);

        Assert.Equal(bundle.ProposalHash, SemanticAuthorityReplayHashing.ProposalHash(bundle.Proposals));
        Assert.Equal("SECTION", Assert.Single(original.BoundHeadings).Text);
        Assert.Empty(changed.BoundHeadings);
        Assert.Contains(changed.ContractIssues, issue => issue.Code == "NON_VERBATIM_TEXT");
    }

    [Fact]
    public void DuplicateExactTextReplaysAsAmbiguousBinding()
    {
        var bundle = Bundle(
            [Alias("S0001", "A A", 1)],
            [new CanonicalSemanticProposal("S0001", true, "A", SelectionMode: CanonicalSemanticSelectionMode.VerbatimText)]);

        var replay = SemanticAuthorityReplay.Replay(bundle);

        Assert.Contains(replay.Pipeline.ContractIssues, issue => issue.Code == "AMBIGUOUS_BINDING");
        Assert.Empty(replay.Pipeline.BoundHeadings);
    }

    [Fact]
    public void CompositeAliasesAndVerbatimPartsReplayWithoutSourceDocument()
    {
        var bundle = Bundle(
            [Alias("S0001", "Part one", 1), Alias("S0002", "Part two", 2)],
            [new CanonicalSemanticProposal(
                "S0001", true, null,
                VerbatimParts: ["Part one", "Part two"],
                SemanticRole: "SECTION",
                SourceAliases: ["S0001", "S0002"],
                SelectionMode: CanonicalSemanticSelectionMode.VerbatimText)]);

        var replay = SemanticAuthorityReplay.Replay(bundle);
        var heading = Assert.Single(replay.Pipeline.BoundHeadings);

        Assert.Equal(["S0001", "S0002"], heading.Parts.Select(part => part.Alias));
        Assert.Equal("Part onePart two", heading.Text);
    }

    [Fact]
    public void ParserStopsBeforeSourceAwareValidation()
    {
        using var json = JsonDocument.Parse("""
            {"headings":[{"sourceAlias":"S9999","isHeading":true,"verbatimText":"invented","semanticRole":"SECTION"}]}
            """);

        var proposals = CanonicalSemanticProposalParser.Parse(json.RootElement);

        Assert.Single(proposals);
        Assert.Equal("S9999", proposals[0].SourceAlias);
    }

    private static SemanticAuthorityReplayBundle Bundle(
        IReadOnlyList<CanonicalSemanticProposal> proposals) =>
        Bundle([Alias("S0001", "Section", 1)], proposals);

    private static SemanticAuthorityReplayBundle Bundle(
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<CanonicalSemanticProposal>? proposals = null) =>
        SemanticAuthorityReplayBundleFactory.Create(
            "DOC-SYNTHETIC",
            "DOCX",
            "source-hash",
            "source-universe-hash",
            aliases,
            "synthetic-model",
            "offline-test-route",
            "prompt-hash",
            "raw-response-hash",
            proposals ?? [Whole(aliases[0].Alias, "SECTION")],
            runId: "run-a",
            commit: "commit-a",
            createdAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static CanonicalSemanticProposal Whole(string alias, string role) =>
        new(alias, true, null, SemanticRole: role, SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);

    private static SemanticSourceAlias Alias(string alias, string text, int ordinal) =>
        new(alias, "source-" + ordinal, ordinal, text, new StructuralSpan(0, text.Length),
            new SourceAnchor { SourceType = "DOCX", ParagraphIndex = ordinal - 1 });
}
