using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V6 core (Issue #9, design D0.v2; offline D1 only). Task-conditioned contracts with no fixed heading taxonomy:
/// <list type="bullet">
/// <item>originalUserInstruction: verbatim text, pinned by SHA-256, never interpreted by code.</item>
/// <item>modelTaskInterpretation: Qwen's free text plus open questions. Status values are technical only.</item>
/// <item>Decision passes: every id in a request's set is decided explicitly (SELECT, NOT_SELECT or UNRESOLVED). A COMPLETE
/// pass must decide every id; an id that is absent is MISSING, never NOT_SELECT. Only an explicit NOT_SELECT is a negative.</item>
/// <item>Reconcile pass: the last pass that decides an id is its final decision. C# never chooses between decisions.</item>
/// <item>Document overview: observations only (page map, repeated lines, numbering, dot leaders). No heading label is produced.</item>
/// </list>
/// The code path does not branch on the instruction text, so the same contracts serve any request.
/// </summary>
internal static class P7F1V6Core
{
    public const string Version = "P7_F1_V6_TASK_CONDITIONED_CORE_V1";
    public const string InterpretVersion = "P7_F1_V6_INTERPRET_V1";
    public const string DecisionVersion = "P7_F1_V6_DECISIONS_V1";
    public const int MaxInterpretationChars = 1000, MaxOpenQuestions = 5, MaxEvidenceRefs = 8, MaxNoteChars = 300;
    public static readonly string[] TechnicalStatuses = ["COMPLETE", "INCOMPLETE", "CLARIFICATION_REQUIRED", "NOT_ANSWERABLE"];
    public static readonly string[] Decisions = ["SELECT", "NOT_SELECT", "UNRESOLVED"];

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

    public sealed record DecisionPass(string Status, IReadOnlyDictionary<string, Decision> Decisions, string? FailureCode);

    /// <summary>
    /// Parses one decision pass for a request whose set of ids is <paramref name="setIds"/>. A COMPLETE pass must decide every
    /// id. Non-COMPLETE passes may decide a subset; the rest stay undecided (MISSING), never NOT_SELECT.
    /// </summary>
    public static DecisionPass ParseDecisions(string response, string requestId, IReadOnlyCollection<string> setIds, IReadOnlySet<string> citable,
        IReadOnlyDictionary<string, string> aliasById, string expectedSourceSha256, IReadOnlyList<P7F1V6Tools.Record> transcript)
    {
        var set = setIds.ToHashSet(StringComparer.Ordinal);
        var byEvidence = transcript.GroupBy(t => t.EvidenceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        DecisionPass Fail(string code) => new(null!, new Dictionary<string, Decision>(), code);
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
                // SELECT and NOT_SELECT need a source-grounded reason; a tool evidence id is checked against the transcript by the binder.
                if (value is "SELECT" or "NOT_SELECT")
                {
                    if (refs.Count == 0) return Fail("EVIDENCE_REQUIRED");
                    var subjectAlias = aliasById.TryGetValue(id, out var sa) ? sa : null;
                    foreach (var r in refs)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(r, @"^E\d+\.\d+$"))
                        {
                            // A tool evidence id counts only if the transcript proves it: issued in this request, from this
                            // source, returned successfully, and it returned the subject's own alias.
                            if (!byEvidence.TryGetValue(r, out var rec)) return Fail("EVIDENCE_ID_NOT_IN_TRANSCRIPT");
                            if (rec.RequestId != requestId || rec.SourceSha256 != expectedSourceSha256) return Fail("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE");
                            if (rec.Status != "OK") return Fail("EVIDENCE_ID_NOT_OK");
                            if (subjectAlias is null || !rec.ReturnedAliases.Contains(subjectAlias)) return Fail("EVIDENCE_DOES_NOT_CONTAIN_SUBJECT");
                            continue;
                        }
                        if (!citable.Contains(r) && !set.Contains(r)) return Fail("EVIDENCE_REF_NOT_CITABLE");
                    }
                }
                var note = Str(d, "note");
                if (note is not null && note.Length > MaxNoteChars) return Fail("NOTE_TOO_LONG");
                result[id] = new Decision(id, value, refs, note);
            }
            if (status == "COMPLETE")
            {
                var undecided = set.Where(id => !result.ContainsKey(id)).ToArray();
                if (undecided.Length > 0) return Fail("SUBJECT_NOT_DECIDED:" + undecided[0]);
            }
            return new DecisionPass(status, result, null);
        }
    }

    // ---------------- Binder-side final decisions ----------------

    public sealed record FinalDecision(string Id, string State, string? Source);

    /// <summary>
    /// Final state per id: the decision of the LAST pass that decided it (reconcile overrides earlier passes). An id no pass
    /// decided is MISSING. Only an explicit NOT_SELECT is a negative. UNRESOLVED stays unresolved.
    /// </summary>
    public static IReadOnlyList<FinalDecision> Finalise(IReadOnlyList<string> ids, IReadOnlyList<(string Pass, DecisionPass Result)> passesInOrder)
    {
        var final = new Dictionary<string, (string State, string Source)>(StringComparer.Ordinal);
        foreach (var (pass, r) in passesInOrder)
        {
            if (r.FailureCode is not null) continue;
            foreach (var d in r.Decisions.Values)
                final[d.Id] = (d.Value switch { "SELECT" => "SELECTED", "NOT_SELECT" => "NOT_SELECTED", _ => "UNRESOLVED" }, pass);
        }
        return ids.Select(id => final.TryGetValue(id, out var f) ? new FinalDecision(id, f.State, f.Source) : new FinalDecision(id, "MISSING", null)).ToArray();
    }

    public sealed record DocumentStatus(bool CoverageComplete, bool ResolvedComplete, int Selected, int NotSelected, int Unresolved, int Missing,
        string Assessment);

    /// <summary>
    /// Four separate facts, never merged into one flag. CoverageComplete: every required id has an explicit state (none
    /// MISSING; UNRESOLVED counts as explicit). ResolvedComplete: every id is SELECTED or NOT_SELECTED. UNRESOLVED is neither
    /// missing nor resolved. Assessment is a conclusion about the request scope, and it is PARTIAL whenever any id is unresolved
    /// or missing. Evidence coverage (what was observed) is reported by the tools, see P7F1V6Tools.ScopeLedger.
    /// </summary>
    public static DocumentStatus Status(IReadOnlyList<FinalDecision> finals)
    {
        int sel = finals.Count(f => f.State == "SELECTED"), nsel = finals.Count(f => f.State == "NOT_SELECTED");
        int unr = finals.Count(f => f.State == "UNRESOLVED"), mis = finals.Count(f => f.State == "MISSING");
        var assessment = mis > 0 ? "INCOMPLETE" : unr > 0 ? "PARTIAL_UNRESOLVED" : "RESOLVED";
        return new DocumentStatus(CoverageComplete: mis == 0, ResolvedComplete: mis == 0 && unr == 0, sel, nsel, unr, mis, assessment);
    }

    // ---------------- Document overview (observations only) ----------------

    /// <summary>
    /// Whole-document evidence built from parser geometry and text, for Qwen to query. It contains counts and groupings,
    /// never a heading label. Repeated groups show lines that recur on several pages (running furniture candidates, navigation
    /// repeats); numbered and dot-leader lines are counted per page.
    /// </summary>
    public static object Overview(IReadOnlyList<F1QSourceLine> lines, int maxRepeatGroups = 25)
    {
        var pages = lines.Where(l => l.Page is not null).GroupBy(l => l.Page!.Value).OrderBy(g => g.Key).Select(g => new
        {
            page = g.Key,
            lines = g.Count(),
            numbered = g.Count(l => Numbered.IsMatch(l.Text)),
            dotLeader = g.Count(l => DotLeader.IsMatch(l.Text)),
        }).ToArray();
        var repeats = lines.Where(l => l.Page is not null && RepeatKey(l.Text).Length > 0)
            .GroupBy(l => RepeatKey(l.Text)).Where(g => g.Select(l => l.Page).Distinct().Count() >= 2)
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

    /// <summary>
    /// Observed scope (what the tools returned to the model) against evaluated scope (what a decision was made on). An id
    /// decided without any observation is a violation: it is reported, not silently accepted.
    /// </summary>
    public sealed record ScopeReport(int InScope, int Observed, int Evaluated, int ObservedNotEvaluated, int EvaluatedNotObserved);

    public static ScopeReport Scope(IReadOnlyCollection<string> inScopeAliases, IReadOnlySet<string> observedAliases, IReadOnlyList<FinalDecision> finals, IReadOnlyDictionary<string, string> aliasById)
    {
        var evaluated = finals.Where(f => f.State != "MISSING").Select(f => aliasById.TryGetValue(f.Id, out var a) ? a : f.Id).ToHashSet(StringComparer.Ordinal);
        return new ScopeReport(inScopeAliases.Count, observedAliases.Count, evaluated.Count,
            observedAliases.Count(a => !evaluated.Contains(a)), evaluated.Count(a => !observedAliases.Contains(a)));
    }

    /// <summary>Same normalisation as the repeat index used by the evidence tools.</summary>
    static string RepeatKey(string text) => P7F1QEvidenceTools.RepeatKey(text);

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
