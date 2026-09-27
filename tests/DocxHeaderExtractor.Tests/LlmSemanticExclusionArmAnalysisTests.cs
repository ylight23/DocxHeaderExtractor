using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// LLM_SEMANTIC_EXCLUSION_ARM_V1 against its baseline LLM_SEMANTIC_PILOT_V1: the deltas the arm was authorized to
/// measure. Both sides are committed raw scores and committed raw provider responses; nothing is re-scored, and the
/// clause is not revised in the light of what it did.
/// <para>
/// A contradiction is counted where the model gives an occurrence a semanticRole the clause excludes - page furniture,
/// a note, an ordinary object caption, a table header row, an index entry - and still answers isHeading true. The
/// clause did not instruct the model about that pairing; it names the exclusion by function, so the count is an
/// outcome, not an instruction followed.
/// </para>
/// </summary>
public sealed class LlmSemanticExclusionArmAnalysisTests
{
    private const string Arm = LlmSemanticExclusionArmV1Tests.Root;
    private const string Pilot = LlmSemanticPilotV1Tests.Root;

    /// <summary>Roles whose primary function the clause excludes; a proposal carrying one and isHeading true contradicts it.</summary>
    private static readonly string[] ExcludedRoles =
    [
        "running-header", "running-footer", "page-header", "page-footer", "page-number", "footnote", "source-note",
        "table-header", "table-caption", "figure-caption", "figure-label", "caption", "index-entry", "toc-entry",
        "table-of-contents-entry", "signature-label", "signature-value",
    ];

    private sealed record Reply(string Document, string Role, bool IsHeading);

    /// <summary>Every heading entry of every committed response of a run, with the role the model gave it.</summary>
    private static List<Reply> Replies(string root)
    {
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{root}/run.v1.json")));
        var replies = new List<Reply>();
        foreach (var call in run.RootElement.GetProperty("ledger").EnumerateArray())
        {
            var text = call.GetProperty("Response").GetString();
            if (text is null) continue;
            var document = call.GetProperty("DocumentId").GetString()!;
            using var response = JsonDocument.Parse(text);
            if (!response.RootElement.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in headings.EnumerateArray())
                replies.Add(new Reply(document,
                    entry.TryGetProperty("semanticRole", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "(none)",
                    !entry.TryGetProperty("isHeading", out var flag) || flag.ValueKind != JsonValueKind.False));
        }
        return replies;
    }

    private static object Contradictions(IEnumerable<Reply> replies)
    {
        var all = replies.ToArray();
        var contradicting = all.Where(x => x.IsHeading && ExcludedRoles.Contains(x.Role, StringComparer.OrdinalIgnoreCase)).ToArray();
        return new
        {
            entries = all.Length,
            declinedIsHeadingFalse = all.Count(x => !x.IsHeading),
            excludedRoleWithIsHeadingTrue = contradicting.Length,
            byRole = contradicting.GroupBy(x => x.Role).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    private static Dictionary<string, int> FalsePositiveFamilies(JsonElement document, string id, (int From, int To) contents, int indexFrom)
    {
        var pages = LlmSemanticPilotV1AnalysisTests.PageOfAlias(id);
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
        var goldAliases = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        var reviewed = LlmSemanticPilotV1AnalysisTests.ReviewedNonHeadings(id);
        return document.GetProperty("residuals").GetProperty("nonGoldTrue").EnumerateArray().Select(m =>
        {
            var h = m.GetProperty("hypothesis");
            var identity = h.GetProperty("identity").GetString()!;
            return LlmSemanticPilotV1AnalysisTests.FalsePositiveFamily(identity, h.GetProperty("Text").GetString()!,
                h.GetProperty("Evidence")[0].GetString()!.Replace("semanticRole=", ""),
                LlmSemanticPilotV1AnalysisTests.PageOf(pages, identity), contents, indexFrom, goldAliases, reviewed);
        }).GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>The claims each side proposed that the Gold does not hold, by identity, with the role the model gave each.</summary>
    private static Dictionary<string, string> FalsePositiveRoles(JsonElement document) =>
        document.GetProperty("residuals").GetProperty("nonGoldTrue").EnumerateArray().ToDictionary(
            m => m.GetProperty("hypothesis").GetProperty("identity").GetString()!,
            m => m.GetProperty("hypothesis").GetProperty("Evidence")[0].GetString()!.Replace("semanticRole=", ""),
            StringComparer.Ordinal);

    /// <summary>
    /// Which false claims the clause actually removed, which it added, and which it kept while renaming the role. A clause
    /// meant to exclude should drop claims; renaming the role of a claim it keeps changes only how the reply describes it.
    /// </summary>
    private static object Movement(JsonElement baseline, JsonElement arm)
    {
        var before = FalsePositiveRoles(baseline);
        var after = FalsePositiveRoles(arm);
        var kept = before.Keys.Intersect(after.Keys, StringComparer.Ordinal).ToArray();
        var renamed = kept.Where(k => before[k] != after[k]).ToArray();
        return new
        {
            dropped = before.Keys.Except(after.Keys, StringComparer.Ordinal).Count(),
            added = after.Keys.Except(before.Keys, StringComparer.Ordinal).Count(),
            keptWithTheSameRole = kept.Length - renamed.Length,
            keptButRenamed = renamed.Length,
            renamings = renamed.GroupBy(k => $"{before[k]} -> {after[k]}").OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            addedByRole = after.Keys.Except(before.Keys, StringComparer.Ordinal).GroupBy(k => after[k])
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    [Fact]
    public void Freeze_the_arm_analysis()
    {
        using var armScore = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Arm}/score.v1.json")));
        using var pilotScore = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Pilot}/score.v1.json")));
        var armReplies = Replies(Arm);
        var pilotReplies = Replies(Pilot);

        var documents = new List<object>();
        foreach (var (id, contents, indexFrom) in new[] { ("SRC-089", (0, 0), 0), ("SRC-095", (2, 4), 54) })
        {
            JsonElement Doc(JsonDocument d) => d.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == id);
            var armDocument = Doc(armScore);
            var pilotDocument = Doc(pilotScore);
            JsonElement A(string k) => armDocument.GetProperty("headline").GetProperty(k);
            JsonElement B(string k) => pilotDocument.GetProperty("headline").GetProperty(k);

            var armFamilies = FalsePositiveFamilies(armDocument, id, contents, indexFrom);
            var pilotFamilies = FalsePositiveFamilies(pilotDocument, id, contents, indexFrom);
            var families = pilotFamilies.Keys.Concat(armFamilies.Keys).Distinct(StringComparer.Ordinal)
                .OrderByDescending(f => pilotFamilies.GetValueOrDefault(f)).ThenBy(f => f, StringComparer.Ordinal)
                .Select(f => new
                {
                    family = f,
                    baseline = pilotFamilies.GetValueOrDefault(f),
                    arm = armFamilies.GetValueOrDefault(f),
                    removed = pilotFamilies.GetValueOrDefault(f) - armFamilies.GetValueOrDefault(f),
                }).ToArray();

            Dictionary<string, object> Patterns(JsonElement document) => document.GetProperty("byGoldPattern").EnumerateObject()
                .ToDictionary(p => p.Name, p => (object)new { gold = p.Value.GetProperty("gold").GetInt32(), exact = p.Value.GetProperty("exact").GetInt32() });

            documents.Add(new
            {
                documentId = id,
                goldClaims = A("goldClaims").GetInt32(),
                baseline = new
                {
                    requestVersion = "V2_ATTENTION_FREE",
                    truePositives = B("truePositives").GetInt32(), falsePositives = B("falsePositives").GetInt32(), falseNegatives = B("falseNegatives").GetInt32(),
                    precision = B("truePrecision").GetDouble(), recall = B("trueRecall").GetDouble(), f1 = B("f1").GetDouble(),
                    boundProposals = pilotDocument.GetProperty("uniqueBoundProposals").GetInt32(),
                    exactByGoldPattern = Patterns(pilotDocument),
                    contradictions = Contradictions(pilotReplies.Where(x => x.Document == id)),
                },
                arm = new
                {
                    requestVersion = "V3_ATTENTION_FREE_EXCLUSION_CONSISTENCY",
                    truePositives = A("truePositives").GetInt32(), falsePositives = A("falsePositives").GetInt32(), falseNegatives = A("falseNegatives").GetInt32(),
                    precision = A("truePrecision").GetDouble(), recall = A("trueRecall").GetDouble(), f1 = A("f1").GetDouble(),
                    boundProposals = armDocument.GetProperty("uniqueBoundProposals").GetInt32(),
                    exactByGoldPattern = Patterns(armDocument),
                    contradictions = Contradictions(armReplies.Where(x => x.Document == id)),
                },
                delta = new
                {
                    f1 = Math.Round(A("f1").GetDouble() - B("f1").GetDouble(), 4),
                    precision = Math.Round(A("truePrecision").GetDouble() - B("truePrecision").GetDouble(), 4),
                    recall = Math.Round(A("trueRecall").GetDouble() - B("trueRecall").GetDouble(), 4),
                    falsePositivesRemoved = B("falsePositives").GetInt32() - A("falsePositives").GetInt32(),
                    truePositivesLost = B("truePositives").GetInt32() - A("truePositives").GetInt32(),
                },
                falsePositiveFamilies = families,
                falsePositiveMovement = Movement(pilotDocument, armDocument),
            });
        }

        FreezeArtifact.AssertJson(Arm, "analysis.v1.json", new
        {
            artifactKind = "a99_llm_semantic_arm_analysis",
            study = "LLM_SEMANTIC_EXCLUSION_ARM_V1",
            modelProviderVlmCallsInThisAnalysis = 0,
            comparison = new
            {
                variable = "one model-visible clause: V2_ATTENTION_FREE -> V3_ATTENTION_FREE_EXCLUSION_CONSISTENCY",
                baseline = new { study = "LLM_SEMANTIC_PILOT_V1", score = new { path = $"{Pilot}/score.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Pilot}/score.v1.json")), commit = "58854f7" } },
                arm = new { score = new { path = $"{Arm}/score.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Arm}/score.v1.json")), commit = "8ada89f" } },
                everythingElse = "the same cohort, model, source facts, coordinate contract, profile, packing, binder, scorer and Gold; the evidence bytes of all 25 requests are identical (the arm's preflight asserts it)",
            },
            contradictionRule = new
            {
                definition = "a proposal whose semanticRole is one the clause excludes and whose isHeading is not false",
                excludedRoles = ExcludedRoles,
                note = "the clause names the exclusion by function and says nothing about this pairing, so this count measures an outcome",
            },
            documents,
            criteria = new
            {
                stated = "precision up strongly, recall about unchanged, SRC-089 assembly not regressed, contradictory role/isHeading outputs down strongly",
                result = "NOT MET",
                precisionUpStrongly = "no: SRC-095 precision 0.370 -> 0.397 (17 of 160 false claims removed); SRC-089 precision 0.737 -> 0.472",
                recallHeld = "SRC-095 yes (0.9126 unchanged); SRC-089 no (0.778 -> 0.694)",
                assemblyNotRegressed = "no: SRC-089 article headings 23 -> 21 exact, chapter headings 5 -> 4, and bound proposals 38 -> 53",
                contradictionsDownStrongly = "SRC-089 4 -> 0 but by renaming the role of claims it kept, not by dropping them; SRC-095 52 -> 52. See documents[].contradictions and documents[].falsePositiveMovement",
            },
            reading = new[]
            {
                "the clause did not act as an exclusion. On SRC-089 it dropped no false claim at all (0 dropped, 18 added), and on SRC-095 it removed page furniture (29 -> 8 claims) while leaving the two largest families it names untouched: contents entries 87 -> 87 and index entries 26 -> 26",
                "where the contradiction count fell, it fell by renaming rather than by excluding: SRC-089's 4 contradictions became 0 while the same four occurrences stayed proposed as headings with semanticRole 'heading' - the footnote, the two signature lines and the block the model had called a running header. A contract check on the role/isHeading pairing would have read that as a fix",
                "SRC-095's contradiction count did not move at all (52 -> 52); its composition shifted (running-header 10 -> 7, figure-label 1 -> 5, running-footer 0 -> 3)",
                "on SRC-089 the added claims are clause and sub-clause bodies ('1 . The conditions on equipment ...') and two articles proposed as their first line only, and Chapter IV's label and title were claimed separately where the baseline claimed them as one - which is how 5/5 chapter headings became 4/5 and 23/26 articles became 21/26",
                "so the pilot's diagnosis - that the model recognises these families and the contract lets it return them anyway - is not answered by telling the model the rule in prose. Asking it to apply the exclusion moved which label it writes, not which occurrences it claims",
            },
            doNotConclude = new[]
            {
                "that the contract-level fix is refuted: one clause wording, one model, one run per arm, no repeats - the direction of the SRC-089 change could be sampling",
                "that the pilot's diagnosis was wrong: the contradictory outputs it found are a fact of the committed responses, whatever this clause did about them",
            },
            nextStepNotRun = new[]
            {
                "a deterministic post-filter over the committed responses of either arm: it tests the exclusion without asking the model to apply it, and needs no provider call at all",
                "a contract-level invariant would have to refuse the claim, not the label: this arm shows a model can satisfy a role/isHeading consistency check by writing a different role",
            },
        });
    }
}
