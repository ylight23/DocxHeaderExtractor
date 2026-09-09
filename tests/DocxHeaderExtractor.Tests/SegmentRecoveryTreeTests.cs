using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Section 18 regression tests for the per-segment recovery tree: replaces rung-atomic recovery
/// (discard the whole rung on any sibling failure) with an independent per-leaf lifecycle. Each
/// letter below maps to the corresponding bullet in the mission's section 18.
/// </summary>
public sealed class SegmentRecoveryTreeTests
{
    private static SegmentRecoveryTree BuildThreeOccurrenceTree(int minimum = 1) =>
        new("DOC-TEST", occurrenceCharacterLengths: [10_000, 10_000, 10_000], minimumSegmentCharacters: minimum);

    // (A) 3 segments A-success/B-fail/C-success -> only B gets split; A and C never rerun.
    [Fact]
    public void OnlyTheFailingSegmentIsSplit_SuccessfulSiblingsAreNeverRerun()
    {
        var tree = new SegmentRecoveryTree("DOC-TEST", [10_000, 10_000, 10_000]);
        var root = tree.RootSegmentId;
        var (a, bc) = tree.Split(root);
        var (b, c) = tree.Split(bc.SegmentId);

        tree.MarkSuccess(a.SegmentId, "reqA", "respA");
        tree.RecordFailure(b.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
        tree.MarkSuccess(c.SegmentId, "reqC", "respC");

        Assert.Equal(SegmentRecoveryState.Success, tree.Get(a.SegmentId).Status);
        Assert.Equal(SegmentRecoveryState.Success, tree.Get(c.SegmentId).Status);
        Assert.Equal(SegmentRecoveryState.WorkloadSplitRequired, tree.Get(b.SegmentId).Status);

        var (b1, b2) = tree.Split(b.SegmentId);

        // A and C are still frozen SUCCESS with their original hashes -- proof they were never touched.
        Assert.Equal(SegmentRecoveryState.Success, tree.Get(a.SegmentId).Status);
        Assert.Equal("reqA", tree.Get(a.SegmentId).RequestHash);
        Assert.Equal(SegmentRecoveryState.Success, tree.Get(c.SegmentId).Status);
        Assert.Equal("reqC", tree.Get(c.SegmentId).RequestHash);
        // Only B's two children are pending work now.
        var pending = tree.PendingLeaves.Select(n => n.SegmentId).ToHashSet();
        Assert.Equal(new HashSet<string> { b1.SegmentId, b2.SegmentId }, pending);
    }

    // (B) failed B splits into B1/B2 -> coverage(B1 union B2) == coverage(B) exactly.
    [Fact]
    public void SplitChildrenExactlyPartitionTheParentsOwnedRange()
    {
        var tree = BuildThreeOccurrenceTree();
        var root = tree.Get(tree.RootSegmentId);
        var (left, right) = tree.Split(tree.RootSegmentId);

        var parentAtoms = root.Owned;
        var childAtoms = left.Owned.Concat(right.Owned).ToArray();

        // No overlap: total owned characters is conserved exactly.
        Assert.Equal(parentAtoms.Sum(a => a.Length), childAtoms.Sum(a => a.Length));

        // Reassembling per-occurrence ranges from the children reproduces the parent's coverage
        // for every occurrence, with zero gap and zero overlap.
        foreach (var occGroup in childAtoms.GroupBy(a => a.OccurrenceIndex).OrderBy(g => g.Key))
        {
            var ranges = occGroup.OrderBy(a => a.CharStart).ToArray();
            for (var i = 1; i < ranges.Length; i++)
                Assert.Equal(ranges[i - 1].CharEnd, ranges[i].CharStart);
        }
        var byOcc = parentAtoms.ToDictionary(a => a.OccurrenceIndex);
        foreach (var occGroup in childAtoms.GroupBy(a => a.OccurrenceIndex))
        {
            var min = occGroup.Min(a => a.CharStart);
            var max = occGroup.Max(a => a.CharEnd);
            Assert.Equal(byOcc[occGroup.Key].CharStart, min);
            Assert.Equal(byOcc[occGroup.Key].CharEnd, max);
        }
    }

    // (C) a successful leaf survives resume (reused, not recomputed).
    [Fact]
    public void SuccessfulLeafArtifactIsReusedOnResume()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "a99-segment-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var key = new SegmentLeafArtifactKey("DOC-TEST", "seg-abc123", "sourcesha", "configsig", "planhash", "qwen/qwen3.5-9b", "CEILING_NEVER_DISABLED");
            Assert.False(SegmentLeafPersistence.TryLoad(outputRoot, key, out _));

            SegmentLeafPersistence.Save(outputRoot, key, "{\"manifest\":true}", "{\"headings\":[]}", "{\"attempts\":1}", "reqhash1", "resphash1");

            Assert.True(SegmentLeafPersistence.TryLoad(outputRoot, key, out var artifact));
            Assert.NotNull(artifact);
            Assert.Equal("resphash1", artifact!.ResponseHash);
            Assert.Equal("{\"headings\":[]}", artifact.PredictionJson);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    // (D) a response-hash mismatch (surfaced as any key-field mismatch, e.g. a changed
    // configuration signature) forces a rerun of that leaf.
    [Fact]
    public void KeyMismatchForcesRerunInsteadOfReusingStaleArtifact()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "a99-segment-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var original = new SegmentLeafArtifactKey("DOC-TEST", "seg-abc123", "sourcesha", "configsig-v1", "planhash", "qwen/qwen3.5-9b", "CEILING_NEVER_DISABLED");
            SegmentLeafPersistence.Save(outputRoot, original, "{}", "{\"headings\":[1]}", "{}", "reqhash1", "resphash1");

            var changedConfig = original with { ConfigurationSignature = "configsig-v2" };
            Assert.False(SegmentLeafPersistence.TryLoad(outputRoot, changedConfig, out _));

            // The unchanged key still reuses cleanly.
            Assert.True(SegmentLeafPersistence.TryLoad(outputRoot, original, out var artifact));
            Assert.Equal("resphash1", artifact!.ResponseHash);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    // (E) transient failure retries the same shape.
    [Fact]
    public void TransientFailureKeepsTheSameSegmentShapeForRetry()
    {
        var tree = BuildThreeOccurrenceTree();
        var root = tree.RootSegmentId;
        var status = tree.RecordFailure(root, ReasoningCompletionFailureClass.ProviderUnavailable);
        Assert.Equal(SegmentRecoveryState.TransientRetry, status);
        // The node is still a single, un-split leaf -- the same shape is available for retry.
        Assert.Single(tree.Leaves);
        Assert.Equal(root, tree.Leaves[0].SegmentId);
        Assert.Contains(tree.Leaves[0].SegmentId, tree.PendingLeaves.Select(n => n.SegmentId));
    }

    // (F) timeout triggers a split, not an identical retry.
    [Fact]
    public void TimeoutMovesTheSegmentToSplitRequiredNotRetry()
    {
        var tree = BuildThreeOccurrenceTree();
        var status = tree.RecordFailure(tree.RootSegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
        Assert.Equal(SegmentRecoveryState.WorkloadSplitRequired, status);
        Assert.True(tree.CanSplit(tree.Get(tree.RootSegmentId)));
        var (left, right) = tree.Split(tree.RootSegmentId);
        Assert.Equal(SegmentRecoveryState.SplitParent, tree.Get(tree.RootSegmentId).Status);
        Assert.Equal(2, tree.Get(tree.RootSegmentId).ChildSegmentIds.Count);
        Assert.NotEqual(left.SegmentId, right.SegmentId);
    }

    // (G) output-limit triggers a split.
    [Fact]
    public void OutputLimitMovesTheSegmentToSplitRequired()
    {
        var tree = BuildThreeOccurrenceTree();
        var status = tree.RecordFailure(tree.RootSegmentId, ReasoningCompletionFailureClass.ProviderOutputLimit);
        Assert.Equal(SegmentRecoveryState.WorkloadSplitRequired, status);
    }

    // (H) one terminal FAILED leaf prevents document-level scoring (coverage verification fails).
    [Fact]
    public void OneTerminalFailedLeafBlocksCoverageVerification()
    {
        var tree = new SegmentRecoveryTree("DOC-TEST", [5_000, 5_000]);
        var (a, b) = tree.Split(tree.RootSegmentId);
        tree.MarkSuccess(a.SegmentId, "req", "resp");
        tree.MarkFailedTerminal(b.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);

        Assert.True(tree.HasFailedTerminal);
        Assert.False(tree.IsComplete);
        Assert.False(tree.VerifyFullCoverage());
    }

    // (I) all leaves SUCCESS enables union / coverage verification.
    [Fact]
    public void AllLeavesSuccessEnablesFullCoverageVerification()
    {
        var tree = new SegmentRecoveryTree("DOC-TEST", [5_000, 5_000, 5_000]);
        var (ab, c) = tree.Split(tree.RootSegmentId);
        var (a, b) = tree.Split(ab.SegmentId);
        tree.MarkSuccess(a.SegmentId, "r1", "h1");
        tree.MarkSuccess(b.SegmentId, "r2", "h2");
        tree.MarkSuccess(c.SegmentId, "r3", "h3");

        Assert.True(tree.IsComplete);
        Assert.True(tree.VerifyFullCoverage());
    }

    // (J) halo-duplicate headings dedupe correctly, preserving a genuine role disagreement as
    // conflict metadata rather than silently discarding it.
    [Fact]
    public void HaloDuplicateHeadingsDedupeAndConflictingRolesArePreserved()
    {
        var raw = new (string SegmentId, string SourceId, int Start, int End, string Role)[]
        {
            ("seg-A", "para-1", 0, 20, "TITLE"),
            ("seg-B", "para-1", 0, 20, "TITLE"),           // exact halo duplicate -> dedupes to one
            ("seg-B", "para-2", 0, 15, "SECTION_HEADING"),
            ("seg-C", "para-2", 0, 15, "SUBSECTION_HEADING"), // same span, disagreeing role -> conflict preserved
        };

        var unioned = SegmentProposalUnion.Union(raw);

        Assert.Equal(2, unioned.Count);
        var para1 = unioned.Single(p => p.SourceId == "para-1");
        Assert.False(para1.HasRoleConflict);

        var para2 = unioned.Single(p => p.SourceId == "para-2");
        Assert.True(para2.HasRoleConflict);
        Assert.Contains("SECTION_HEADING", para2.ConflictingRoles);
        Assert.Contains("SUBSECTION_HEADING", para2.ConflictingRoles);
    }

    // (K) successful siblings retain their proposal bytes exactly -- no mutation on subsequent
    // splits elsewhere in the tree.
    [Fact]
    public void SuccessfulSiblingProposalBytesAreUnaffectedByLaterSplitsElsewhere()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "a99-segment-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var tree = new SegmentRecoveryTree("DOC-TEST", [5_000, 5_000, 5_000]);
            var (ab, c) = tree.Split(tree.RootSegmentId);
            var (a, b) = tree.Split(ab.SegmentId);

            var keyC = new SegmentLeafArtifactKey("DOC-TEST", c.SegmentId, "srcsha", "cfg", SegmentLeafPersistence.PlanHash(c.Owned), "qwen/qwen3.5-9b", "CEILING_NEVER_DISABLED");
            SegmentLeafPersistence.Save(outputRoot, keyC, "{}", "{\"headings\":[{\"text\":\"Frozen C\"}]}", "{}", "reqC", "respC");
            tree.MarkSuccess(c.SegmentId, "reqC", "respC");

            // Now split and fail-then-split branch A/B repeatedly -- unrelated tree activity.
            tree.RecordFailure(a.SegmentId, ReasoningCompletionFailureClass.ProviderOutputLimit);
            tree.Split(a.SegmentId);
            tree.RecordFailure(b.SegmentId, ReasoningCompletionFailureClass.ProviderTotalTimeout);
            tree.Split(b.SegmentId);

            Assert.True(SegmentLeafPersistence.TryLoad(outputRoot, keyC, out var artifact));
            Assert.Equal("{\"headings\":[{\"text\":\"Frozen C\"}]}", artifact!.PredictionJson);
            Assert.Equal(SegmentRecoveryState.Success, tree.Get(c.SegmentId).Status);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    // (L) Gold cannot be loaded before document freeze -- the firewall test.
    [Fact]
    public void GoldFirewallForbidsReadBeforeFreeze()
    {
        var gate = new DocumentFreezeGate();
        Assert.Throws<InvalidOperationException>(() => gate.GuardGoldRead());

        gate.MarkFrozen();
        var exception = Record.Exception(() => gate.GuardGoldRead());
        Assert.Null(exception);
    }

    [Fact]
    public void MinimumSegmentFloorStopsFurtherSplittingOfATinySingleOccurrenceSegment()
    {
        var tree = new SegmentRecoveryTree("DOC-TEST", [1_000], minimumSegmentCharacters: 500);
        var node = tree.Get(tree.RootSegmentId);
        Assert.True(tree.CanSplit(node));
        var (left, right) = tree.Split(tree.RootSegmentId);
        // Both children are now at/under the floor -- neither can be split again.
        Assert.False(tree.CanSplit(left));
        Assert.False(tree.CanSplit(right));
        Assert.Throws<InvalidOperationException>(() => tree.Split(left.SegmentId));
    }
}
