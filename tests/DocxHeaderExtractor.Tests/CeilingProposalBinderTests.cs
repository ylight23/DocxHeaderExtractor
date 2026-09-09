using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Regression tests for the union/reload bug found in the per-segment recovery campaign: a leaf
/// reached genuine SUCCESS (real headings, real reasoning) but was persisted with
/// boundProposalCount=0 because the model declared the wrong local occurrence index ("i") for
/// every heading. <see cref="CeilingProposalBinder"/> is the single canonical binder now shared by
/// fresh execution and disk reload; these tests exercise it directly with fake/model-free data --
/// no provider call, no Gold involved.
/// </summary>
public sealed class CeilingProposalBinderTests
{
    private static ReasoningSourceOccurrence Occurrence(string sourceOccurrenceId, string sourceId, int ordinal, string text) => new()
    {
        SourceOccurrenceId = sourceOccurrenceId,
        SourceId = sourceId,
        SourceOrdinal = ordinal,
        RawText = text,
        SourceSpan = new StructuralSpan(0, text.Length),
        CandidateHint = new CandidateHint(false, 0, []),
    };

    // Reproduces the exact real-world shape found in the DOC-0264 SUCCESS leaf: a tiny context-only
    // halo occurrence at local index 0 (owned=null) and the single real owned occurrence at local
    // index 1. The model returned "i":0 for every heading despite the text only existing in the
    // owned occurrence -- the old binder silently dropped every one of them.
    [Fact]
    public void SingleOwnedOccurrence_MisindexedHeading_IsReboundInsteadOfDropped()
    {
        var contextOnly = Occurrence("occ-0", "para-header", 0, "Header field text.");
        var owned = Occurrence("occ-1", "para-body", 1, "TITLE\nBody text continues for a while after the title.");
        var visible = new[] { contextOnly, owned };
        var ownedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-1" };

        var packet = CeilingPacketBuilder.Build(visible, ownedSet);

        // The model claims i=0 (the context-only occurrence) for a span that only exists in the
        // owned occurrence's text.
        var headings = new[] { new CeilingHeadingProposal(0, 0, 5, "DOCUMENT_TITLE") };

        var bound = CeilingProposalBinder.Bind(headings, packet, ownedSet);

        Assert.Single(bound);
        Assert.Equal("para-body", bound[0].SourceId);
        Assert.Equal(0, bound[0].Start);
        Assert.Equal(5, bound[0].End);
        Assert.Equal("DOCUMENT_TITLE", bound[0].Role);
    }

    [Fact]
    public void SingleOwnedOccurrence_CorrectlyIndexedHeading_StillBindsNormally()
    {
        var contextOnly = Occurrence("occ-0", "para-header", 0, "Header field text.");
        var owned = Occurrence("occ-1", "para-body", 1, "TITLE\nBody text continues.");
        var visible = new[] { contextOnly, owned };
        var ownedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-1" };

        var packet = CeilingPacketBuilder.Build(visible, ownedSet);
        var headings = new[] { new CeilingHeadingProposal(1, 0, 5, "DOCUMENT_TITLE") };

        var bound = CeilingProposalBinder.Bind(headings, packet, ownedSet);

        Assert.Single(bound);
        Assert.Equal("para-body", bound[0].SourceId);
    }

    // When more than one occurrence is owned in the same request, an out-of-range/wrong index is
    // never guessed -- there is no principled single destination to rebind to.
    [Fact]
    public void MultipleOwnedOccurrences_UnresolvableIndex_IsDroppedNotGuessed()
    {
        var ownedA = Occurrence("occ-0", "para-a", 0, "ARTICLE 1 text");
        var ownedB = Occurrence("occ-1", "para-b", 1, "ARTICLE 2 text");
        var visible = new[] { ownedA, ownedB };
        var ownedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-0", "occ-1" };

        var packet = CeilingPacketBuilder.Build(visible, ownedSet);
        // Index 5 doesn't exist in a 2-occurrence packet.
        var headings = new[] { new CeilingHeadingProposal(5, 0, 5, "ARTICLE") };

        var bound = CeilingProposalBinder.Bind(headings, packet, ownedSet);

        Assert.Empty(bound);
    }

    [Fact]
    public void ContextOnlyOccurrence_HeadingOutsideOwnedWindow_IsStillRejected()
    {
        // Even with the single-owned-occurrence rebind, a span that genuinely does not fit inside
        // the owned occurrence's visible window must still be rejected -- rebinding only resolves
        // *which* occurrence a heading belongs to, never *whether* its span is valid.
        var owned = Occurrence("occ-0", "para-body", 0, "short");
        var visible = new[] { owned };
        var ownedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-0" };

        var packet = CeilingPacketBuilder.Build(visible, ownedSet);
        var headings = new[] { new CeilingHeadingProposal(0, 0, 500, "ARTICLE") }; // end far beyond "short".Length

        var bound = CeilingProposalBinder.Bind(headings, packet, ownedSet);

        Assert.Empty(bound);
    }

    // Section 3: a fresh in-memory bind and a "reload" bind (packet rebuilt independently from the
    // same occurrences/owned-set/windows) must produce byte-equivalent proposals.
    [Fact]
    public void FreshBindAndReconstructedReloadBind_ProduceIdenticalProposals()
    {
        var contextOnly = Occurrence("occ-0", "para-header", 0, "Header field text.");
        var owned = Occurrence("occ-1", "para-body", 1, "TITLE\nCHAPTER ONE\nBody continues.");
        var visible = new ReasoningSourceOccurrence[] { contextOnly, owned };
        var ownedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-1" };
        var headings = new[]
        {
            new CeilingHeadingProposal(0, 0, 5, "DOCUMENT_TITLE"),
            new CeilingHeadingProposal(0, 6, 17, "CHAPTER"),
        };

        // "Fresh" execution-time packet.
        var freshPacket = CeilingPacketBuilder.Build(visible, ownedSet);
        var freshBound = CeilingProposalBinder.Bind(headings, freshPacket, ownedSet);

        // "Reload" independently reconstructs the packet from the same inputs (as
        // LoadRawProposalsFromArtifact does from a persisted leaf's owned/visible atoms) --
        // never from a previous in-memory object reference.
        var reloadOccurrences = new ReasoningSourceOccurrence[] { contextOnly, owned };
        var reloadOwnedSet = new HashSet<string>(StringComparer.Ordinal) { "occ-1" };
        var reloadPacket = CeilingPacketBuilder.Build(reloadOccurrences, reloadOwnedSet);
        var reloadBound = CeilingProposalBinder.Bind(headings, reloadPacket, reloadOwnedSet);

        Assert.Equal(freshBound.Count, reloadBound.Count);
        Assert.True(freshBound.Count > 0);
        for (var i = 0; i < freshBound.Count; i++)
        {
            Assert.Equal(freshBound[i].SourceId, reloadBound[i].SourceId);
            Assert.Equal(freshBound[i].Start, reloadBound[i].Start);
            Assert.Equal(freshBound[i].End, reloadBound[i].End);
            Assert.Equal(freshBound[i].Role, reloadBound[i].Role);
        }
    }
}
