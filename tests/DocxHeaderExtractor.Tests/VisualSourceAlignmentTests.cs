using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class VisualSourceAlignmentTests
{
    [Fact]
    public void EveryOrdinaryOccurrenceGetsAnAliasIndependentOfCandidateHint()
    {
        var occurrences = new[]
        {
            Occurrence("body/p1", 0, "ordinary paragraph with enough rendered text to anchor deterministically", false),
            Occurrence("body/p2", 1, "ordinary paragraph with enough rendered text to anchor deterministically", true),
        };

        var manifest = VisualSourceAlignmentBuilder.Build(occurrences, [
            "ordinary paragraph with enough rendered text to anchor deterministically"
        ]);

        Assert.True(manifest.GatePass);
        Assert.Equal(2, manifest.MappedOccurrences);
        Assert.Equal(1d, manifest.SourceAliasCoverage);
        Assert.Equal(2, manifest.Occurrences.Count);
        Assert.All(manifest.Occurrences, row => Assert.NotEmpty(row.VisualAliases));
    }

    [Fact]
    public void ALongOccurrenceGetsFullPageBoundaryIntervalsAndOneCanonicalIdentity()
    {
        var first = string.Concat(Enumerable.Repeat("first page content ", 8));
        var second = string.Concat(Enumerable.Repeat("second page content ", 8));
        var raw = first + "|" + second;
        var occurrence = Occurrence("body/long", 0, raw, false);

        var manifest = VisualSourceAlignmentBuilder.Build([occurrence], [first, second]);

        Assert.True(manifest.GatePass);
        var aliases = Assert.Single(manifest.Occurrences).VisualAliases;
        Assert.Equal(2, aliases.Count);
        Assert.Equal(0, manifest.Occurrences[0].UnmappedCharacterCount);
        Assert.Equal(new[] { 1, 2 }, manifest.Occurrences[0].PageIndices);
        Assert.Equal(1, manifest.Aliases.Select(x => x.SourceOccurrenceId).Distinct().Count());
        Assert.Equal(0, manifest.Aliases[0].VisibleStartCharacter);
        Assert.Equal(raw.Length, manifest.Aliases[^1].VisibleEndCharacter);
    }

    [Fact]
    public void AliasRoundTripsToExactCanonicalOccurrenceSpan()
    {
        const string text = "Repeated source paragraph with a stable exact span for visual alignment";
        var occurrence = Occurrence("body/repeated", 3, text, false);
        var manifest = VisualSourceAlignmentBuilder.Build([occurrence], [text]);
        var alias = Assert.Single(manifest.Aliases);

        Assert.Equal(text, occurrence.RawText[alias.VisibleStartCharacter..alias.VisibleEndCharacter]);
        Assert.Equal(alias.VisibleStartCharacter, alias.OwnedStartCharacter);
        Assert.Equal(alias.VisibleEndCharacter, alias.OwnedEndCharacter);
        Assert.Equal("MAPPED", alias.MappingStatus);
        Assert.True(manifest.RoundTripValid);
    }

    [Fact]
    public void GoldCannotInfluenceThePreInferenceManifest()
    {
        var occurrence = Occurrence("body/no-gold", 0, "source content is the only authority in this alignment", false);
        var manifest = VisualSourceAlignmentBuilder.Build([occurrence], [occurrence.RawText]);

        Assert.False(manifest.GoldUsed);
        Assert.DoesNotContain("gold", string.Join('|', manifest.Aliases.Select(x => x.Alias)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidVisualAliasIsRejectedWithoutFuzzyFallback()
    {
        var occurrence = Occurrence("body/bind", 0, "source occurrence with an exact visual alias", true);
        var manifest = VisualSourceAlignmentBuilder.Build([occurrence], [occurrence.RawText]);
        var alias = Assert.Single(manifest.Aliases);
        var packet = CeilingPacketBuilder.Build([occurrence],
            new HashSet<string>([occurrence.SourceOccurrenceId], StringComparer.Ordinal),
            aliases: new Dictionary<string, string>(StringComparer.Ordinal) { [occurrence.SourceOccurrenceId] = alias.Alias });

        Assert.Null(CeilingProposalBinder.ResolveBinding(0, packet.Bindings,
            new HashSet<string>([occurrence.SourceOccurrenceId], StringComparer.Ordinal), "P99-O999"));
        Assert.NotNull(CeilingProposalBinder.ResolveBinding(0, packet.Bindings,
            new HashSet<string>([occurrence.SourceOccurrenceId], StringComparer.Ordinal), alias.Alias));
    }

    private static ReasoningSourceOccurrence Occurrence(string sourceId, int ordinal, string text, bool candidate) => new()
    {
        SourceOccurrenceId = $"DOC:{sourceId}:{ordinal}:{text.Length}",
        SourceId = sourceId,
        SourceOrdinal = ordinal,
        RawText = text,
        SourceSpan = new StructuralSpan(0, text.Length),
        CandidateHint = new CandidateHint(candidate, candidate ? 1 : 0, []),
    };
}
