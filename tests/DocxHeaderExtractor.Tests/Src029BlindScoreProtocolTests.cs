namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC029_BLIND_SCORE_PROTOCOL_V1, committed before the engine's blind proposals are opened: which engine
/// and which Gold are joined, how a hypothesis matches a claim, what each metric counts and how residuals
/// are classified. The scorer is pinned by hash, so its rules cannot be tuned after the reveal. The
/// proposals are pinned by their git blob, taken from git, so pinning them does not read them.
/// </summary>
public sealed class Src029BlindScoreProtocolTests
{
    [Fact]
    public void Freeze_the_score_protocol()
    {
        // The proposals file as committed at dc98144 (git rev-parse dc98144:<path>).
        const string proposalGitBlob = "cb911b3d1f92e06b81d194eae1d26c4df2ee9ce1";
        Assert.Equal(proposalGitBlob, Src029BlindScoreTests.GitBlob(TestRepository.Path(Src029BlindScoreTests.Proposals)));

        FreezeArtifact.AssertJson(Src029BlindScoreTests.Root, "SRC-029.score-protocol.v1.json", new
        {
            artifactKind = "a99_blind_score_protocol",
            study = "SRC029_BLIND_SCORE_PROTOCOL_V1",
            frozenBeforeReveal = true,
            modelProviderVlmCalls = 0,
            engineBaseline = new
            {
                engineCommit = "73eff5a",
                proposalArtifactCommit = "dc98144",
                proposalPath = Src029BlindScoreTests.Proposals,
                proposalGitBlob,
                preregistration = new
                {
                    path = $"{Src029BlindScoreTests.Root}/SRC-029.preregistration.v1.json",
                    sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Src029BlindScoreTests.Root}/SRC-029.preregistration.v1.json")),
                },
            },
            gold = new
            {
                goldCommit = "30059d4",
                corpusRegistryCommit = "60b4d3e",
                authoredGoldPath = Src029BlindScoreTests.GoldPath,
                authoredGoldSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src029BlindScoreTests.GoldPath)),
                registryGoldSha256 = CanonicalGoldRegistry.Entry("SRC-029").GoldSha256,
                sourceSha256 = CanonicalGoldRegistry.Entry("SRC-029").SourceSha256,
                goldClaims = CanonicalGoldRegistry.Entry("SRC-029").MaterializedSemanticClaims,
            },
            scorer = new
            {
                path = Src029BlindScoreTests.ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src029BlindScoreTests.ScorerFile)),
                output = $"{Src029BlindScoreTests.Root}/SRC-029.blind-score.v1.json",
            },
            matching = new[]
            {
                "canonical identity = ordered source-part tuple (alias:start-end per part) from the production binder (GENERIC_MULTIPART_BINDER_V2) over the PDF's atom universe",
                "an engine hypothesis is its aliases bound as WHOLE_ALIAS parts in the order the engine listed them; one that does not bind is reported as unbindable and matches nothing (not reordered or repaired)",
                "a hypothesis matches a Gold claim only when the identities are equal; multipart claims match as one claim",
                "partial VERBATIM_TEXT spans are respected: a whole-atom hypothesis over an atom whose Gold part is a partial span is not an exact match. The engine proposes whole atoms only, so a Gold claim with a VERBATIM_TEXT part cannot be matched exactly. This is reported (composite.goldClaimsWithVerbatimPart) and not excused",
                "a hypothesis that shares characters with a claim without an equal identity is a claim-level miss (PARTIAL_*) and a part-level diagnostic, never a true positive",
                "occurrences the engine did not list are FALSE (the proposal artifact lists every TRUE and NEEDS_REVIEW hypothesis and every set-apart FALSE one)",
            },
            predictionStates = new[] { "TRUE", "FALSE", "NEEDS_REVIEW" },
            goldBuckets = new
            {
                rule = "each Gold claim is in exactly one bucket, taken in this order",
                order = new[]
                {
                    "EXACT_TRUE / EXACT_NEEDS_REVIEW / EXACT_FALSE - a hypothesis with the claim's identity, by its state",
                    "PARTIAL_TRUE - no exact hypothesis; some overlapping hypothesis is TRUE",
                    "PARTIAL_NEEDS_REVIEW - no exact or overlapping TRUE; some overlapping hypothesis is NEEDS_REVIEW",
                    "PARTIAL_FALSE - only overlapping FALSE hypotheses",
                    "UNPROPOSED - no hypothesis touches the claim (engine FALSE)",
                },
            },
            metrics = new
            {
                truePositives = "EXACT_TRUE claims",
                falsePositives = "TRUE hypotheses whose identity is no Gold claim's (split: touching Gold / disjoint from Gold)",
                falseNegatives = "Gold claims not EXACT_TRUE (374 - TP)",
                precision = "TP / TRUE hypotheses",
                recall = "TP / Gold claims",
                f1 = "harmonic mean of precision and recall",
                needsReview = "never counted as a heading in precision, recall or F1",
                reviewCaptureRate = "EXACT_NEEDS_REVIEW / (Gold claims - TP); a variant including PARTIAL_NEEDS_REVIEW is reported as a diagnostic only",
                goldPredictedNeedsReview = "EXACT_NEEDS_REVIEW",
                goldPredictedFalse = "EXACT_FALSE + UNPROPOSED",
                goldPartialOnly = "PARTIAL_TRUE + PARTIAL_NEEDS_REVIEW + PARTIAL_FALSE (claim-level misses)",
                nonGoldPredictedNeedsReview = "NEEDS_REVIEW hypotheses whose identity is no Gold claim's; reviewNoiseOutsideGold = those sharing no character with any Gold claim",
                nonGoldPredictedTrue = "the false positives",
                exactCompositeMatch = "exact matches of multipart Gold claims",
                partLevelMismatch = "per residual claim: Gold parts covered by any overlapping hypothesis / Gold parts",
                axisDisagreements = "on EXACT_TRUE pairs (primary) and EXACT_NEEDS_REVIEW pairs (secondary): semanticFunctions and occurrenceRoles compared as sets, primaryFunction, scope, titleRelation, repeatStatus and informationType as exact values (null equals null)",
            },
            diagnosticBuckets = new
            {
                rule = "after reveal, every residual (Gold claim not EXACT_TRUE, non-Gold TRUE, non-Gold NEEDS_REVIEW) gets exactly one bucket with a written reason, in a separate classification artifact; the raw score is not edited",
                A = "semantics expressible in the ontology; the difference is a reviewer meaning decision the engine's evidence cannot settle",
                B_known = "a recurrence of a pre-registered gap: B1 TOC opener with repeated text, B2 TOC sequence continuity evidence, B3 DOCX bold run-in lead, B4 outline level on list item - the reason must name which",
                B_new = "a new generic evidence or harness gap: fixable by a rule that names no document, text, page or total",
                C = "ontology gap: the occurrence's function cannot be expressed in OCCURRENCE_SEMANTIC_AXES_V2",
                D = "document-specific exception: no generic rule could decide it; the reason must name the document-specific fact",
            },
            forbiddenAfterReveal = new[]
            {
                "changing the scorer (its hash is checked before scoring)",
                "changing the Gold, the engine or the proposals",
                "rewriting the committed raw score",
            },
        });
    }
}
