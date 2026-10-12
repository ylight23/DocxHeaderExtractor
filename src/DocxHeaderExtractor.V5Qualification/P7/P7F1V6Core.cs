using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V6 core (Issue #9, D1 offline). Task-conditioned contracts with no fixed heading taxonomy:
/// <list type="bullet">
/// <item>originalUserInstruction: verbatim text, pinned by SHA-256; code never interprets it.</item>
/// <item>Interpretation: free text plus open questions. Status values are technical only.</item>
/// <item>Decision passes: each id of a request's set is decided explicitly (SELECT, NOT_SELECT or UNRESOLVED). A COMPLETE pass
/// decides every id. An absent id is MISSING, never NOT_SELECT. Only an explicit NOT_SELECT is a negative.</item>
/// <item>Evidence: a reference is valid only if the model was shown that alias (in a request payload or a tool result of this
/// run). A tool evidence id is valid only if exactly one transcript record holds it, for the same request and source, with
/// status OK, and that record returned the decided subject's alias.</item>
/// <item>Resolution: any failed pass makes the document PASS_FAILED. A document is RESOLVED only after a COMPLETE reconcile
/// pass that decides every id, with no id MISSING or UNRESOLVED.</item>
/// </list>
/// </summary>
internal static class P7F1V6Core
{
    public const string Version = "P7_F1_V6_TASK_CONDITIONED_CORE_V2";
    public const string InterpretVersion = "P7_F1_V6_INTERPRET_V1";
    public const string DecisionVersion = "P7_F1_V6_DECISIONS_V1";
    public const int MaxInterpretationChars = 1000, MaxOpenQuestions = 5, MaxEvidenceRefs = 8, MaxNoteChars = 300;
    public static readonly string[] TechnicalStatuses = ["COMPLETE", "INCOMPLETE", "CLARIFICATION_REQUIRED", "NOT_ANSWERABLE"];
    public static readonly string[] Decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];

    static readonly Regex ToolEvidence = new(@"^E\d+\.\d+$", RegexOptions.CultureInvariant);
    static readonly Regex Numbered = new(@"^\s*(\d+(\.\d+)*|[IVX]+|[a-z])[\.\)]?\s", RegexOptions.CultureInvariant);
    static readonly Regex DotLeader = new(@"(?:\s*[.·…]){3,}", RegexOptions.CultureInvariant);

    /// <summary>SHA-256 of the verbatim instruction bytes (no trimming, no normalisation).</summary>
    public static string InstructionSha256(string originalUserInstruction) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(originalUserInstruction)));

    // ---------------- Interpretation pass ----------------

    public sealed record Interpretation(string Status, string Text, IReadOnlyList<string> OpenQuestions);

    /// <summary>Strict shape check only. The meaning of the text is never parsed by code.</summary>
    public static (Interpretation? Value, string? FailureCode) ParseInterpretation(string response, string requestId)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); } catch (JsonException) { return (null, "RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "ROOT_NOT_OBJECT");
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            string[] required = ["protocolVersion", "requestId", "status", "interpretation"];
            string[] allowed = ["protocolVersion", "requestId", "status", "interpretation", "openQuestions"];
            if (required.Any(k => !names.Contains(k, StringComparer.Ordinal)) || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))) return (null, "ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != InterpretVersion) return (null, "PROTOCOL_VERSION_INVALID");
            if (Str(root, "requestId") != requestId) return (null, "REQUEST_ID_MISMATCH");
            var status = Str(root, "status");
            if (status is null || !TechnicalStatuses.Contains(status, StringComparer.Ordinal)) return (null, "STATUS_INVALID");
            var text = Str(root, "interpretation");
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInterpretationChars) return (null, "INTERPRETATION_INVALID");
            var questions = new List<string>();
            if (root.TryGetProperty("openQuestions", out var q))
            {
                if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() > MaxOpenQuestions || q.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
                    return (null, "OPEN_QUESTIONS_INVALID");
                questions.AddRange(q.EnumerateArray().Select(x => x.GetString()!));
            }
            if (status == "CLARIFICATION_REQUIRED" && questions.Count == 0) return (null, "CLARIFICATION_WITHOUT_QUESTION");
            return (new Interpretation(status, text, questions), null);
        }
    }

    // ---------------- Decision passes ----------------

    public sealed record Decision(string Id, string Value, IReadOnlyList<string> EvidenceRefs, string? Note);

    /// <summary>A parsed or failed pass. Name identifies it (for example "pack-1" or "reconcile"); FailureCode is null when it parsed.</summary>
    public sealed record DecisionPass(string Name, string? Status, IReadOnlyDictionary<string, Decision> Decisions, string? FailureCode);

    /// <summary>
    /// Parses one decision pass for a request whose set of ids is <paramref name="setIds"/>. <paramref name="shownAliases"/> is
    /// every alias the model was shown in this request (its payload and the results of this run's tool calls).
    /// </summary>
    public static DecisionPass ParseDecisions(string name, string response, string requestId, IReadOnlyCollection<string> setIds,
        IReadOnlySet<string> shownAliases, IReadOnlyDictionary<string, string> aliasById, string expectedSourceSha256,
        IReadOnlyList<P7F1V6Tools.Record> transcript, IReadOnlySet<string>? incompleteAliases = null)
    {
        var set = setIds.ToHashSet(StringComparer.Ordinal);
        var incomplete = incompleteAliases ?? new HashSet<string>(StringComparer.Ordinal);
        DecisionPass Fail(string code) => new(name, null, new Dictionary<string, Decision>(), code);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); } catch (JsonException) { return Fail("RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("ROOT_NOT_OBJECT");
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            string[] required = ["protocolVersion", "requestId", "status", "decisions"];
            string[] allowed = ["protocolVersion", "requestId", "status", "decisions", "clarification", "reason"];
            if (required.Any(k => !names.Contains(k, StringComparer.Ordinal)) || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))) return Fail("ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != DecisionVersion) return Fail("PROTOCOL_VERSION_INVALID");
            if (Str(root, "requestId") != requestId) return Fail("REQUEST_ID_MISMATCH");
            var status = Str(root, "status");
            if (status is null || !TechnicalStatuses.Contains(status, StringComparer.Ordinal)) return Fail("STATUS_INVALID");
            if (status == "CLARIFICATION_REQUIRED" && string.IsNullOrWhiteSpace(Str(root, "clarification"))) return Fail("CLARIFICATION_MISSING");
            if (status == "NOT_ANSWERABLE" && string.IsNullOrWhiteSpace(Str(root, "reason"))) return Fail("REASON_MISSING");
            var list = root.GetProperty("decisions");
            if (list.ValueKind != JsonValueKind.Array) return Fail("DECISIONS_NOT_ARRAY");
            var result = new Dictionary<string, Decision>(StringComparer.Ordinal);
            foreach (var d in list.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) return Fail("DECISION_NOT_OBJECT");
                var dn = d.EnumerateObject().Select(p => p.Name).ToArray();
                string[] dKeys = ["id", "decision", "evidenceRefs", "note"];
                if (dn.Any(n => !dKeys.Contains(n, StringComparer.Ordinal)) || new[] { "id", "decision" }.Any(k => !dn.Contains(k, StringComparer.Ordinal)))
                    return Fail("DECISION_KEYS_INVALID");
                var id = Str(d, "id") ?? "";
                if (!set.Contains(id)) return Fail("ID_NOT_IN_REQUEST");
                if (result.ContainsKey(id)) return Fail("ID_DECIDED_TWICE");
                var value = Str(d, "decision");
                if (value is null || !Decisions.Contains(value, StringComparer.Ordinal)) return Fail("DECISION_VALUE_INVALID");
                var refs = new List<string>();
                if (d.TryGetProperty("evidenceRefs", out var er))
                {
                    if (er.ValueKind != JsonValueKind.Array || er.GetArrayLength() > MaxEvidenceRefs || er.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        return Fail("EVIDENCE_REFS_INVALID");
                    refs.AddRange(er.EnumerateArray().Select(x => x.GetString()!));
                }
                if (value is "SELECT" or "NOT_SELECT")
                {
                    if (refs.Count == 0) return Fail("EVIDENCE_REQUIRED");
                    var subjectAlias = aliasById.TryGetValue(id, out var sa) ? sa : null;
                    // A decision may not rest on source text that was not read to the end: a partial view is not evidence.
                    if (subjectAlias is not null && incomplete.Contains(subjectAlias)) return Fail("SUBJECT_EVIDENCE_INCOMPLETE");
                    foreach (var r in refs)
                    {
                        if (ToolEvidence.IsMatch(r))
                        {
                            // Exactly one transcript record must hold this id: duplicates are rejected, never resolved by choice.
                            var recs = transcript.Where(t => t.EvidenceId == r).ToArray();
                            if (recs.Length == 0) return Fail("EVIDENCE_ID_NOT_IN_TRANSCRIPT");
                            if (recs.Length > 1) return Fail("EVIDENCE_ID_DUPLICATE");
                            var rec = recs[0];
                            if (rec.RequestId != requestId || rec.SourceSha256 != expectedSourceSha256) return Fail("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE");
                            if (rec.Status != "OK") return Fail("EVIDENCE_ID_NOT_OK");
                            if (subjectAlias is null || !rec.ReturnedAliases.Contains(subjectAlias)) return Fail("EVIDENCE_DOES_NOT_CONTAIN_SUBJECT");
                            continue;
                        }
                        // A source alias, or an id of this request, must have been SHOWN to the model in this run.
                        var alias = aliasById.TryGetValue(r, out var ra) ? ra : r;
                        if (!shownAliases.Contains(alias)) return Fail("EVIDENCE_NOT_SHOWN");
                        if (incomplete.Contains(alias)) return Fail("EVIDENCE_INCOMPLETE");
                    }
                }
                var note = Str(d, "note");
                if (note is not null && note.Length > MaxNoteChars) return Fail("NOTE_TOO_LONG");
                result[id] = new Decision(id, value, refs, note);
            }
            if (status == "COMPLETE")
            {
                var undecided = set.Where(id => !result.ContainsKey(id)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (undecided.Length > 0) return Fail("SUBJECT_NOT_DECIDED:" + undecided[0]);
            }
            return new DecisionPass(name, status, result, null);
        }
    }

    // ---------------- Resolution (binder side) ----------------

    public sealed record FinalDecision(string Id, string State, string? Source);

    /// <summary>All passes of one run, with the failed ones kept. Nothing is dropped.</summary>
    public sealed record Resolution(IReadOnlyList<FinalDecision> Finals, IReadOnlyList<string> FailedPasses, bool ReconcileCompleted);

    /// <summary>
    /// Final state per id: the decision of the LAST parsed pass that decided it (a reconcile overrides earlier passes). An id that
    /// no pass decided is MISSING. A failed pass is recorded in FailedPasses; it is never skipped silently. A reconcile counts as
    /// completed only when it parsed, was COMPLETE and decided every id.
    /// </summary>
    public static Resolution Finalise(IReadOnlyList<string> ids, IReadOnlyList<DecisionPass> passesInOrder)
    {
        var final = new Dictionary<string, (string State, string Source)>(StringComparer.Ordinal);
        foreach (var p in passesInOrder)
        {
            if (p.FailureCode is not null) continue;
            foreach (var d in p.Decisions.Values)
                final[d.Id] = (d.Value switch { "SELECT" => "SELECTED", "NOT_SELECT" => "NOT_SELECTED", _ => "UNRESOLVED" }, p.Name);
        }
        var finals = ids.Select(id => final.TryGetValue(id, out var f) ? new FinalDecision(id, f.State, f.Source) : new FinalDecision(id, "MISSING", null)).ToArray();
        var failed = passesInOrder.Where(p => p.FailureCode is not null).Select(p => $"{p.Name}:{p.FailureCode}").ToArray();
        var reconcileCompleted = passesInOrder.Any(p => p.Name == "reconcile" && p.FailureCode is null && p.Status == "COMPLETE"
            && ids.All(id => p.Decisions.ContainsKey(id)));
        return new Resolution(finals, failed, reconcileCompleted);
    }

    /// <summary>
    /// CoverageComplete and ResolvedComplete are DECISION facts. EvidenceComplete is an EVIDENCE fact: no evaluated line was
    /// left only partly read. A RESOLVED decision set over incompletely read evidence is reported as EVIDENCE_INCOMPLETE.
    /// </summary>
    public sealed record DocumentStatus(bool CoverageComplete, bool ResolvedComplete, bool EvidenceComplete, int Selected, int NotSelected,
        int Unresolved, int Missing, int FailedPasses, string Assessment);

    /// <summary>
    /// Separate facts, never merged. CoverageComplete: no pass failed and no id is MISSING. ResolvedComplete: coverage holds, every
    /// id is SELECTED or NOT_SELECTED, and a COMPLETE reconcile decided every id. Assessment: PASS_FAILED if any pass failed;
    /// INCOMPLETE if an id is missing; PARTIAL_UNRESOLVED if an id is unresolved; UNRECONCILED if the decisions are resolved but
    /// no reconcile completed; RESOLVED otherwise.
    /// </summary>
    public static DocumentStatus Status(Resolution r, int incompleteEvidenceLines = 0)
    {
        var f = r.Finals;
        int sel = f.Count(x => x.State == "SELECTED"), nsel = f.Count(x => x.State == "NOT_SELECTED");
        int unr = f.Count(x => x.State == "UNRESOLVED"), mis = f.Count(x => x.State == "MISSING");
        int failed = r.FailedPasses.Count;
        bool coverage = failed == 0 && mis == 0;
        bool evidence = incompleteEvidenceLines == 0;
        bool resolved = coverage && unr == 0 && r.ReconcileCompleted && evidence;
        var assessment = failed > 0 ? "PASS_FAILED" : mis > 0 ? "INCOMPLETE" : unr > 0 ? "PARTIAL_UNRESOLVED"
            : !r.ReconcileCompleted ? "UNRECONCILED" : !evidence ? "EVIDENCE_INCOMPLETE" : "RESOLVED";
        return new DocumentStatus(coverage, resolved, evidence, sel, nsel, unr, mis, failed, assessment);
    }

    // ---------------- Scope ----------------

    /// <summary>Observed (shown to the model) and evaluated (has an explicit state), and the differences between them.</summary>
    public sealed record ScopeReport(int InScope, int Observed, int Evaluated, int ObservedNotEvaluated, int EvaluatedNotObserved);

    public static ScopeReport Scope(IReadOnlyCollection<string> inScopeAliases, IReadOnlySet<string> observedAliases,
        IReadOnlyList<FinalDecision> finals, IReadOnlyDictionary<string, string> aliasById)
    {
        var evaluated = finals.Where(f => f.State != "MISSING").Select(f => aliasById.TryGetValue(f.Id, out var a) ? a : f.Id).ToHashSet(StringComparer.Ordinal);
        return new ScopeReport(inScopeAliases.Count, observedAliases.Count, evaluated.Count,
            observedAliases.Count(a => !evaluated.Contains(a)), evaluated.Count(a => !observedAliases.Contains(a)));
    }

    // ---------------- Document overview (observations only) ----------------

    /// <summary>Whole-document evidence from parser geometry and text. Counts and groupings, never a heading label.</summary>
    public static object Overview(IReadOnlyList<F1QSourceLine> lines, int maxRepeatGroups = 25)
    {
        var pages = lines.Where(l => l.Page is not null).GroupBy(l => l.Page!.Value).OrderBy(g => g.Key).Select(g => new
        {
            page = g.Key, lines = g.Count(),
            numbered = g.Count(l => Numbered.IsMatch(l.Text)),
            dotLeader = g.Count(l => DotLeader.IsMatch(l.Text)),
        }).ToArray();
        var repeats = lines.Where(l => l.Page is not null && P7F1QEvidenceTools.RepeatKey(l.Text).Length > 0)
            .GroupBy(l => P7F1QEvidenceTools.RepeatKey(l.Text)).Where(g => g.Select(l => l.Page).Distinct().Count() >= 2)
            .OrderByDescending(g => g.Select(l => l.Page).Distinct().Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(maxRepeatGroups).Select(g => new
            {
                key = g.Key.Length > 60 ? g.Key[..60] : g.Key,
                pages = g.Select(l => l.Page!.Value).Distinct().Order().ToArray(),
                aliases = g.Take(12).Select(l => l.Alias).ToArray(),
            }).ToArray();
        return new { version = Version, pageCount = pages.Length, lineCount = lines.Count, pages, repeatGroups = repeats,
            basis = "PARSER_OBSERVATIONS_ONLY: counts, repeats, numbering and dot leaders. No heading label is produced." };
    }

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
