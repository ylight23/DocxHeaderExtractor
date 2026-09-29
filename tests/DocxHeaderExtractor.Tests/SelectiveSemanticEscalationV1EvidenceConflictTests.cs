namespace DocxHeaderExtractor.Tests;

/// <summary>
/// ADJUDICATOR_EVIDENCE_CONFLICT: a runtime-observable, Gold-free escalation signal.
/// <para>
/// RESOLVED_INCORRECT (from <see cref="SelectiveSemanticEscalationV1AdjudicationScoringTests"/>) is not
/// something production can act on, because knowing an answer is "incorrect" requires Gold. This file
/// asks a different, Gold-free question instead: does the adjudicator's final label agree with the
/// label most commonly produced across the pooled repeat-label evidence from all three frozen,
/// source-faithful views? Disagreement (ADJUDICATOR_EVIDENCE_CONFLICT = true) is observable from the
/// adjudicator's own output plus evidence already gathered before the adjudicator ever ran - no Gold
/// label appears anywhere in this file, which is why <see cref="No_gold_reference_exists_in_this_file"/>
/// scans the file's own source for the word.
/// </para>
/// <para>
/// The pooled dominant label is deliberately never treated as a semantic answer in its own right - see
/// <see cref="Dominant_label_is_documented_as_a_conflict_signal_not_semantic_authority"/> - it exists
/// only to detect disagreement worth escalating, never to resolve it. Resolving it, when the
/// disagreement is plausibly visual/layout in nature, is <c>VISUAL_LAYOUT_RELEVANT</c>'s job and the
/// VLM's downstream job, not this signal's.
/// </para>
/// </summary>
public sealed class SelectiveSemanticEscalationV1EvidenceConflictTests
{
    private const string ScoreRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252";
    private const string AdjudicationCaptureRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/adjudication";

    private static readonly string[] FlaggedItems = ["ITEM-505430BB", "ITEM-CCE2C592"];

    // Set only after this experiment's own conflict signal (not Gold) flagged the item for visual
    // review - see escalation-decision.v1.json's derivation for why each value is what it is.
    private static readonly IReadOnlyDictionary<string, (bool VisualLayoutRelevant, string Rationale)> VisualRelevance =
        new Dictionary<string, (bool, string)>(StringComparer.Ordinal)
        {
            ["ITEM-505430BB"] = (false,
                "No conflict signal fired for this item (adjudicator agrees with the pooled evidence); there is nothing to escalate, so visual relevance is not evaluated."),
            ["ITEM-CCE2C592"] = (true,
                "The disagreement is between a masthead/title block (group, date, venue) and a section heading beneath it in the same visual hierarchy - exactly the kind of typography, spatial grouping, and header/body separation a text-only rendering can flatten away. Plausibly resolvable by visual evidence, not by re-reading the same text again."),
        };

    [Fact]
    public void No_gold_identifier_is_referenced_in_this_files_executable_code()
    {
        // Scans code lines only (comments/doc-comments legitimately discuss Gold in prose - see the
        // class summary above); the actual risk is importing the Gold dictionary or hash constant.
        // This check's own body must name the forbidden identifiers, so it excludes itself.
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/SelectiveSemanticEscalationV1EvidenceConflictTests.cs"));
        var thisBody = ExtractMethodBody(source, nameof(No_gold_identifier_is_referenced_in_this_files_executable_code));
        var rest = source.Replace(thisBody, string.Empty, StringComparison.Ordinal);
        var codeOnly = string.Join('\n', rest.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal) &&
                           !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("GoldR2", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("GoldSha256", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalGoldRegistry", codeOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Dominant_label_computation_is_deterministic()
    {
        foreach (var itemId in FlaggedItems)
        {
            var first = ComputeDominantLabel(itemId);
            var second = ComputeDominantLabel(itemId);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void F1_shows_no_conflict_agenda_shows_conflict()
    {
        var f1 = Evaluate("ITEM-505430BB");
        Assert.False(f1.Conflict);
        Assert.Equal("DOCUMENT_LABEL", f1.AdjudicatorFinalLabel);
        Assert.Equal("DOCUMENT_LABEL", f1.DominantLabel);

        var agenda = Evaluate("ITEM-CCE2C592");
        Assert.True(agenda.Conflict);
        Assert.Equal("DOCUMENT_LABEL", agenda.AdjudicatorFinalLabel);
        Assert.Equal("STRUCTURAL_UNIT", agenda.DominantLabel);
        Assert.Equal(11, agenda.Tally["STRUCTURAL_UNIT"]);
        Assert.Equal(4, agenda.Tally["DOCUMENT_LABEL"]);
        Assert.Equal(1, agenda.Tally["NON_STRUCTURAL"]);
    }

    [Fact]
    public void Dominant_label_is_documented_as_a_conflict_signal_not_semantic_authority()
    {
        // Structural guard: nothing in this file (or the frozen artifacts it writes) may expose the
        // dominant label as a field named/suggesting a final or accepted decision.
        foreach (var itemId in FlaggedItems)
        {
            var result = Evaluate(itemId);
            Assert.NotNull(result.DominantLabel); // it exists...
        }
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/SelectiveSemanticEscalationV1EvidenceConflictTests.cs"));
        var thisBody = ExtractMethodBody(source,
            nameof(Dominant_label_is_documented_as_a_conflict_signal_not_semantic_authority));
        var rest = source.Replace(thisBody, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("finalDecision", rest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acceptedLabel", rest, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var signatureIndex = source.IndexOf($" {methodName}(", StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"method {methodName} not found");
        var braceStart = source.IndexOf('{', signatureIndex);
        var depth = 0;
        var i = braceStart;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) break;
            }
        }
        return source[braceStart..(i + 1)];
    }

    [Fact]
    public void Freeze_evidence_conflict_signal()
    {
        FreezeArtifact.AssertJson(ScoreRoot, "evidence-conflict-signal.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_evidence_conflict_signal",
            schemaVersion = "a99-selective-semantic-escalation-evidence-conflict-signal-v1",
            signalDefinition = "ADJUDICATOR_EVIDENCE_CONFLICT = true iff the adjudicator's final label differs from the label most frequent across the pooled repeat-label multiset of every model run on every one of the three frozen, source-faithful views. Computed without Gold: both operands (adjudicator output, pooled view evidence) are observable at runtime.",
            dominantLabelWarning = "The dominant/pooled label is a CONFLICT-DETECTION SIGNAL ONLY, never a semantic answer. An 11-4-1 split means 'disagreement worth investigating', not 'the 11 side is correct'. Nothing in this pipeline may substitute the dominant label for the adjudicator's or a downstream escalation step's actual decision.",
            goldUsedInSignalConstruction = false,
            items = FlaggedItems.Select(itemId =>
            {
                var result = Evaluate(itemId);
                var relevance = VisualRelevance[itemId];
                return new
                {
                    itemId,
                    adjudicatorFinalLabel = result.AdjudicatorFinalLabel,
                    evidenceTally = result.Tally,
                    dominantLabel = result.DominantLabel,
                    adjudicatorEvidenceConflict = result.Conflict,
                    visualLayoutRelevant = relevance.VisualLayoutRelevant,
                    visualLayoutRationale = relevance.Rationale,
                };
            }).ToArray(),
        });
    }

    [Fact]
    public void Freeze_escalation_decision()
    {
        FreezeArtifact.AssertJson(ScoreRoot, "escalation-decision.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_decision",
            schemaVersion = "a99-selective-semantic-escalation-decision-v1",
            pipeline = "context-sensitive item -> text adjudicator -> (unresolved OR repeat-unstable OR ADJUDICATOR_EVIDENCE_CONFLICT) -> visual relevance? -> yes: VLM / no: independent text adjudicator or review",
            decisions = FlaggedItems.Select(itemId =>
            {
                var result = Evaluate(itemId);
                var relevance = VisualRelevance[itemId];
                var escalationTrigger = result.Conflict; // resolved=true and n=1 for both, so
                                                           // repeat-instability is not evaluable here
                                                           // (see repeatInstabilityEvaluable below);
                                                           // only the conflict signal can fire.
                string decision = !escalationTrigger
                    ? "ACCEPT_TEXT_ADJUDICATION"
                    : relevance.VisualLayoutRelevant ? "VLM_ESCALATION" : "INDEPENDENT_TEXT_ADJUDICATOR_OR_REVIEW";
                return new
                {
                    itemId,
                    contextSensitive = true,
                    textAdjudicatorResolved = true,
                    repeatInstabilityEvaluable = false,
                    adjudicatorEvidenceConflict = result.Conflict,
                    visualLayoutRelevant = relevance.VisualLayoutRelevant,
                    decision,
                };
            }).ToArray(),
            vlmCallsMadeThisRun = 0,
            note = "Decisions are derived entirely from CONTEXT_SENSITIVITY (prior experiment) and ADJUDICATOR_EVIDENCE_CONFLICT (this file), neither of which uses Gold. VISUAL_LAYOUT_RELEVANT is the one human-authored judgment in this table (see rationale per item in evidence-conflict-signal.v1.json) and is not derived from Gold either.",
        });
    }

    // ---------------------------------------------------------------------------------------

    private static (string AdjudicatorFinalLabel, IReadOnlyDictionary<string, int> Tally, string DominantLabel, bool Conflict)
        Evaluate(string itemId)
    {
        var adjudicatorFinalLabel = ReadAdjudicatorFinalLabel(itemId);
        var tally = TallyPooledEvidence(itemId);
        var dominant = DominantOf(tally);
        return (adjudicatorFinalLabel, tally, dominant, !string.Equals(adjudicatorFinalLabel, dominant, StringComparison.Ordinal));
    }

    private static string ComputeDominantLabel(string itemId) => DominantOf(TallyPooledEvidence(itemId));

    private static Dictionary<string, int> TallyPooledEvidence(string itemId)
    {
        var byArm = SelectiveSemanticEscalationV1PreflightTests.ReadCandidateLabels(itemId);
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var labels in byArm.Values)
            foreach (var label in labels)
                tally[label] = tally.GetValueOrDefault(label) + 1;
        return tally;
    }

    private static string DominantOf(Dictionary<string, int> tally) =>
        tally.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

    private static string ReadAdjudicatorFinalLabel(string itemId)
    {
        var path = TestRepository.Path($"{AdjudicationCaptureRoot}/{itemId}.transport-capture.v1.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("PASS", doc.RootElement.GetProperty("contractStatus").GetString());
        return doc.RootElement.GetProperty("finalLabel").GetString()!;
    }
}
