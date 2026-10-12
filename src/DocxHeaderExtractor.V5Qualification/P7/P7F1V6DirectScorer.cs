using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Exact scoring for DIRECT-F1 (D4.3). Two metrics, kept apart:
///   - atom membership F1: which source atoms contribute to some heading (Gold atoms = the union of every claim's parts);
///   - heading occurrence F1: which headings, each identified by its ordered list of atoms. Three atoms selected for one three-line heading
///     are one occurrence, not three.
/// Gold claims come in two shapes: a single sourceAlias, or sourceParts[]. Both are read. A part that selects only some of an alias's text
/// (VERBATIM_TEXT), or a claim whose selection mode is not stated, cannot be matched by whole-atom decisions; heading occurrence F1 is then
/// reported as not comparable, with the reason, and only atom membership F1 is given. An unknown claim shape is a controlled refusal.
/// The scorer refuses unless the registry names the authority with a USER final approval, the Gold bytes match goldSha256, the Gold path is
/// inside the authority folder, the run's source and universe match the Gold, the run's coverage is complete with nothing UNRESOLVED, every
/// predicted and Gold atom is in the pinned universe, and the prediction is self-consistent. It never creates, edits or approves Gold.
/// </summary>
internal static class P7F1V6DirectScorer
{
    /// <summary>Universe is rebuilt from the pinned source by the run's parser and evidence store (<see cref="P7F1V6Universe.Rebuild"/>); a
    /// caller cannot declare it. Decisions is the run's final value per alias (SELECT, NOT_SELECT or UNRESOLVED). The scorer derives
    /// coverage, UNRESOLVED and the selected atoms from these two itself. Headings are the run's accepted groups.
    /// RunStatus, Finalization and Assurance are the run's own outcome (<see cref="P7F1V6DirectV2.RunOutcome"/>): they decide whether a
    /// score is the official end-to-end score or a provisional diagnostic.</summary>
    public sealed record RunEvidence(P7F1V6Universe Universe, IReadOnlyDictionary<string, string> Decisions, IReadOnlyList<IReadOnlyList<string>> Headings,
        string RunStatus, string Finalization, string Assurance)
    {
        public static RunEvidence From(P7F1V6Universe universe, P7F1V6DirectV2.RunOutcome outcome) =>
            new(universe, outcome.Decisions, outcome.Headings, outcome.Status, outcome.Finalization, outcome.Assurance);
    }

    public sealed record Counts(int TruePositive, int FalsePositive, int FalseNegative, string F1Exact, double F1);

    /// <summary>Official is true only for a run that is COMPLETE with finalization DONE (assurance FINAL_SELECTION_REVIEWED). Every other
    /// scorable run is labelled PROVISIONAL_DIAGNOSTIC with its status and finalization, and must not be reported as end-to-end F1.
    /// Even an official score claims only FINAL_SELECTION_REVIEWED: finalization does not re-examine aliases every segment rejected.</summary>
    public sealed record Result(bool Scored, string Status, string? Reason, Counts? AtomMembership, Counts? HeadingOccurrence,
        string? HeadingOccurrenceNotComparable, IReadOnlyList<string> Notes, bool Official = false, string? Label = null);

    public const string OfficialLabel = "END_TO_END:FINAL_SELECTION_REVIEWED";

    static Result Refuse(string reason) => new(false, "REFUSED", reason, null, null, null, Array.Empty<string>());

    sealed record GoldClaim(IReadOnlyList<string> Aliases, IReadOnlyList<string?> Modes)
    {
        public string Key => string.Join("|", Aliases);
    }

    /// <summary>Reads one claim in either shape. Returns null for a shape this scorer does not know.</summary>
    static GoldClaim? ReadClaim(JsonElement c)
    {
        if (c.TryGetProperty("sourceParts", out var ps) && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() > 0)
        {
            var aliases = new List<string>(); var modes = new List<string?>();
            foreach (var p in ps.EnumerateArray())
            {
                if (!p.TryGetProperty("sourceAlias", out var a) || a.ValueKind != JsonValueKind.String) return null;
                aliases.Add(a.GetString()!);
                modes.Add(p.TryGetProperty("selectionMode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
            }
            return new GoldClaim(aliases, modes);
        }
        if (c.TryGetProperty("sourceAlias", out var s) && s.ValueKind == JsonValueKind.String)
            return new GoldClaim(new[] { s.GetString()! },
                new[] { c.TryGetProperty("selectionMode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null });
        return null;
    }

    static Counts Count(int tp, int fp, int fn)
    {
        var denom = 2L * tp + fp + fn;
        var num = 2L * tp;
        return new Counts(tp, fp, fn, $"{num}/{denom}", denom == 0 ? 0 : (double)num / denom);
    }

    public static Result Score(string repoRoot, string registryRelativePath, string authorityId, RunEvidence run)
    {
        try { return ScoreChecked(repoRoot, registryRelativePath, authorityId, run); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A Gold or registry this scorer cannot read is reported, never thrown at the caller.
            return Refuse("GOLD_OR_REGISTRY_UNREADABLE:" + e.GetType().Name);
        }
    }

    static Result ScoreChecked(string repoRoot, string registryRelativePath, string authorityId, RunEvidence run)
    {
        var registryPath = Path.Combine(repoRoot, registryRelativePath);
        if (!File.Exists(registryPath)) return Refuse("REGISTRY_MISSING");
        using var reg = JsonDocument.Parse(File.ReadAllBytes(registryPath));
        var authority = reg.RootElement.GetProperty("authorities").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("authorityId").GetString() == authorityId);
        if (authority.ValueKind != JsonValueKind.Object) return Refuse("NO_APPROVED_GOLD_FOR_AUTHORITY");
        if (authority.GetProperty("approvalAuthority").GetString() != "USER" || !authority.GetProperty("userFinalApproval").GetBoolean())
            return Refuse("REGISTRY_APPROVAL_NOT_USER_FINAL");
        if (!authority.GetProperty("semanticClaimsEvaluable").GetBoolean()) return Refuse("SEMANTIC_CLAIMS_NOT_EVALUABLE");
        if (!authority.GetProperty("occurrenceEvaluable").GetBoolean()) return Refuse("OCCURRENCE_NOT_EVALUABLE");
        if (run.Universe.SourceSha256 != authority.GetProperty("sourceSha256").GetString()) return Refuse("SOURCE_SHA_MISMATCH");

        var goldRel = authority.GetProperty("canonicalGoldPath").GetString() ?? "";
        var goldPath = Path.GetFullPath(Path.Combine(repoRoot, goldRel));
        var goldRoot = Path.GetFullPath(Path.Combine(repoRoot, "eval", "a99-closed-loop", "gold-current", "documents")) + Path.DirectorySeparatorChar;
        if (!goldPath.StartsWith(goldRoot, StringComparison.OrdinalIgnoreCase)) return Refuse("GOLD_PATH_OUTSIDE_AUTHORITY_FOLDER");
        if (!File.Exists(goldPath)) return Refuse("GOLD_FILE_MISSING");
        var goldBytes = File.ReadAllBytes(goldPath);
        if (Convert.ToHexStringLower(SHA256.HashData(goldBytes)) != authority.GetProperty("goldSha256").GetString()) return Refuse("GOLD_SHA_MISMATCH");

        using var gold = JsonDocument.Parse(goldBytes);
        var g = gold.RootElement;
        if (g.GetProperty("authorityId").GetString() != authorityId) return Refuse("GOLD_AUTHORITY_DIFFERS");
        if (g.GetProperty("source").GetProperty("sourcePath").GetString() != run.Universe.DocumentRelativePath) return Refuse("GOLD_SOURCE_PATH_DIFFERS");
        if (!g.GetProperty("approval").GetProperty("userFinalApproval").GetBoolean()) return Refuse("GOLD_APPROVAL_NOT_USER_FINAL");
        var semantic = g.GetProperty("semantic");
        if (!semantic.GetProperty("headingSetExhaustive").GetBoolean()) return Refuse("GOLD_NOT_EXHAUSTIVE");

        var claims = new List<GoldClaim>();
        foreach (var c in semantic.GetProperty("claims").EnumerateArray())
        {
            var claim = ReadClaim(c);
            if (claim is null) return Refuse("GOLD_CLAIM_SHAPE_UNKNOWN");
            claims.Add(claim);
        }
        if (claims.Count == 0) return Refuse("GOLD_EMPTY");
        if (claims.Count != semantic.GetProperty("approvedSemanticTotal").GetInt32()) return Refuse("CLAIM_COUNT_MISMATCH");
        if (claims.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != claims.Count) return Refuse("GOLD_DUPLICATE_CLAIM");

        var occurrence = g.GetProperty("occurrence");
        if (!occurrence.GetProperty("occurrenceEvaluable").GetBoolean()) return Refuse("OCCURRENCE_NOT_EVALUABLE");
        if (occurrence.GetProperty("sourceUniverseSha256").GetString() != run.Universe.UniverseSha256) return Refuse("UNIVERSE_SHA_MISMATCH");
        var occurrenceKeys = new List<string>();
        foreach (var c in occurrence.GetProperty("claims").EnumerateArray())
        {
            var claim = ReadClaim(c);
            if (claim is null) return Refuse("GOLD_OCCURRENCE_SHAPE_UNKNOWN");
            occurrenceKeys.Add(claim.Key);
        }
        if (occurrenceKeys.Distinct(StringComparer.Ordinal).Count() != occurrenceKeys.Count) return Refuse("GOLD_OCCURRENCE_DUPLICATE_CLAIM");
        if (!occurrenceKeys.ToHashSet(StringComparer.Ordinal).SetEquals(claims.Select(c => c.Key))) return Refuse("UNIT_MISMATCH_OCCURRENCE_VS_SEMANTIC");

        // The run, against the rebuilt universe: every alias decided, nothing outside, nothing UNRESOLVED.
        var universe = run.Universe.Aliases.ToHashSet(StringComparer.Ordinal);
        if (run.Decisions.Values.Any(v => v is not ("SELECT" or "NOT_SELECT" or "UNRESOLVED"))) return Refuse("DECISION_VALUE_INVALID");
        var missing = run.Universe.Aliases.Count(a => !run.Decisions.ContainsKey(a));
        var unexpected = run.Decisions.Keys.Count(a => !universe.Contains(a));
        if (missing > 0 || unexpected > 0) return Refuse($"COVERAGE_INCOMPLETE:missing={missing},unexpected={unexpected}");
        var unresolved = run.Decisions.Values.Count(v => v == "UNRESOLVED");
        if (unresolved > 0) return Refuse($"RUN_HAS_UNRESOLVED:{unresolved}");
        var goldAtoms = claims.SelectMany(c => c.Aliases).ToHashSet(StringComparer.Ordinal);
        if (!goldAtoms.All(universe.Contains)) return Refuse("GOLD_OUTSIDE_UNIVERSE");
        var selected = run.Decisions.Where(kv => kv.Value == "SELECT").Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var outside = selected.Concat(run.Headings.SelectMany(h => h)).Where(a => !universe.Contains(a)).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (outside.Count > 0) return Refuse("PREDICTED_OUTSIDE_UNIVERSE:" + string.Join(",", outside.Take(10)) + (outside.Count > 10 ? ",..." : ""));

        // The prediction must be self-consistent: every heading part selected, every selected atom in exactly one heading.
        var partCount = run.Headings.SelectMany(h => h).GroupBy(a => a, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        if (partCount.Values.Any(n => n > 1) || partCount.Keys.Any(a => !selected.Contains(a)) || selected.Any(a => !partCount.ContainsKey(a)))
            return Refuse("PREDICTION_INCONSISTENT");

        var notes = new List<string>();
        var atomTp = selected.Count(goldAtoms.Contains);
        var atoms = Count(atomTp, selected.Count - atomTp, goldAtoms.Count - atomTp);
        if (claims.Sum(c => c.Aliases.Count) != goldAtoms.Count) notes.Add("SOME_GOLD_ATOMS_BELONG_TO_MORE_THAN_ONE_CLAIM");

        // Heading occurrence: exact ordered atom lists. Comparable only when every Gold part selects its whole atom.
        string? notComparable = null;
        var modes = claims.SelectMany(c => c.Modes).ToList();
        var verbatim = modes.Count(m => m == "VERBATIM_TEXT");
        var unstated = modes.Count(m => m is null);
        var other = modes.Count(m => m is not null and not "WHOLE_ALIAS" and not "VERBATIM_TEXT");
        if (verbatim > 0) notComparable = $"VERBATIM_TEXT_PARTS:{verbatim}";
        else if (unstated > 0) notComparable = $"SELECTION_MODE_NOT_STATED:{unstated}";
        else if (other > 0) notComparable = $"SELECTION_MODE_UNKNOWN:{other}";
        if (verbatim > 0) notes.Add("ATOM_MEMBERSHIP_COUNTS_A_VERBATIM_TEXT_PART_AS_ITS_WHOLE_ATOM");

        Counts? headings = null;
        if (notComparable is null)
        {
            var goldKeys = claims.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var predKeys = run.Headings.Select(h => string.Join("|", h)).ToHashSet(StringComparer.Ordinal);
            var tp = predKeys.Count(goldKeys.Contains);
            headings = Count(tp, predKeys.Count - tp, goldKeys.Count - tp);
        }
        var official = run.RunStatus == "COMPLETE" && run.Finalization == "DONE" && run.Assurance == P7F1V6DirectV2.AssuranceFinalSelectionReviewed;
        if (official)
            return new Result(true, "SCORED", null, atoms, headings, notComparable, notes, true, OfficialLabel);
        notes.Add("NOT_END_TO_END: run did not reach FINAL_SELECTION_REVIEWED; report only as a provisional diagnostic");
        return new Result(true, "SCORED_PROVISIONAL", null, atoms, headings, notComparable, notes, false,
            $"PROVISIONAL_DIAGNOSTIC:{run.RunStatus}/{run.Finalization}/{run.Assurance}");
    }
}
