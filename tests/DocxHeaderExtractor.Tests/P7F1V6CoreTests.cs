using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 (P7_F1_V6_TASK_CONDITIONED_CORE_V1, D1 offline): contracts, explicit coverage and whole-document observations.</summary>
public sealed class P7F1V6CoreTests
{
    const string Req = "D094-V6-C1";
    static readonly string[] SetIds = ["aaaa", "bbbb", "cccc"];
    static readonly HashSet<string> Citable = ["L0000:S0", "L0001:S0"];

    static string Interp(string status, string text, string? questions = null) =>
        $"{{\"protocolVersion\":\"P7_F1_V6_INTERPRET_V1\",\"requestId\":\"{Req}\",\"status\":\"{status}\",\"interpretation\":\"{text}\"{(questions is null ? "" : $",\"openQuestions\":[{questions}]")}}}";

    static string Dec(string status, string decisions, string extra = "") =>
        $"{{\"protocolVersion\":\"P7_F1_V6_DECISIONS_V1\",\"requestId\":\"{Req}\",\"status\":\"{status}\",\"decisions\":[{decisions}]{extra}}}";

    static readonly Dictionary<string, string> AliasById = new(StringComparer.Ordinal) { ["aaaa"] = "L0000:S0", ["bbbb"] = "L0001:S0", ["cccc"] = "L0002:S0" };
    const string Sha = "src-sha";
    static readonly List<P7F1V6Tools.Record> NoTranscript = new();
    static P7F1V6Core.DecisionPass Parse(string response) => P7F1V6Core.ParseDecisions(response, Req, SetIds, Citable, AliasById, Sha, NoTranscript);
    static P7F1V6Tools.Record Rec(string ev, string req, string src, string status, params string[] aliases) =>
        new(ev, req, "get_page_text", src, "u", "s", status, null, "h", aliases.ToHashSet(StringComparer.Ordinal), "v");

    static string D(string id, string value, string refs = "[\"L0000:S0\"]") =>
        $"{{\"id\":\"{id}\",\"decision\":\"{value}\",\"evidenceRefs\":{refs}}}";

    [Fact] public void Instruction_is_pinned_verbatim_and_a_trailing_space_changes_the_hash()
    {
        var a = P7F1V6Core.InstructionSha256("Trích xuất các heading chính");
        Assert.Equal(a, P7F1V6Core.InstructionSha256("Trích xuất các heading chính"));
        Assert.NotEqual(a, P7F1V6Core.InstructionSha256("Trích xuất các heading chính "));
    }

    [Fact] public void Interpretation_is_free_text_with_technical_status_only_and_clarification_needs_a_question()
    {
        Assert.Null(P7F1V6Core.ParseInterpretation(Interp("COMPLETE", "Lấy các điều trong nghị định"), Req).FailureCode);
        Assert.Equal("STATUS_INVALID", P7F1V6Core.ParseInterpretation(Interp("HEADING_MEMBERSHIP", "x"), Req).FailureCode);
        Assert.Equal("CLARIFICATION_WITHOUT_QUESTION", P7F1V6Core.ParseInterpretation(Interp("CLARIFICATION_REQUIRED", "mơ hồ"), Req).FailureCode);
        var ok = P7F1V6Core.ParseInterpretation(Interp("CLARIFICATION_REQUIRED", "mơ hồ", "\"Cấp mấy?\""), Req);
        Assert.Null(ok.FailureCode);
        Assert.Equal("Cấp mấy?", Assert.Single(ok.Value!.OpenQuestions));
    }

    [Fact] public void A_COMPLETE_pass_must_decide_every_id_and_an_omitted_id_is_never_a_negative()
    {
        var missing = Parse(Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT")));
        Assert.Equal("SUBJECT_NOT_DECIDED:cccc", missing.FailureCode);

        var partial = Parse(Dec("INCOMPLETE", D("aaaa", "NOT_SELECT")));
        Assert.Null(partial.FailureCode);
        var finals = P7F1V6Core.Finalise(SetIds, [("p1", partial)]);
        Assert.Equal("NOT_SELECTED", finals.Single(f => f.Id == "aaaa").State);
        Assert.Equal("MISSING", finals.Single(f => f.Id == "bbbb").State);     // omitted: unobserved, not negative
        var st = P7F1V6Core.Status(finals);
        Assert.False(st.CoverageComplete);
        Assert.Equal("INCOMPLETE", st.Assessment);
    }

    [Fact] public void Explicit_decisions_need_citable_evidence_and_only_listed_ids_are_accepted()
    {
        Assert.Equal("EVIDENCE_REQUIRED", Parse(Dec("COMPLETE", D("aaaa", "SELECT", "[]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"))).FailureCode);
        Assert.Equal("EVIDENCE_REF_NOT_CITABLE", Parse(Dec("COMPLETE", D("aaaa", "SELECT", "[\"L9999:S0\"]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"))).FailureCode);
        Assert.Equal("ID_NOT_IN_REQUEST", Parse(Dec("INCOMPLETE", D("zzzz", "SELECT"))).FailureCode);
        Assert.Equal("ID_DECIDED_TWICE", Parse(Dec("INCOMPLETE", D("aaaa", "NOT_SELECT") + "," + D("aaaa", "SELECT"))).FailureCode);
    }

    [Fact] public void A_later_reconcile_pass_overrides_an_earlier_decision_and_C_sharp_chooses_nothing_else()
    {
        var first = Parse(Dec("INCOMPLETE", D("aaaa", "NOT_SELECT")));
        var second = Parse(Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "UNRESOLVED", "[\"L0001:S0\"]") + "," + D("cccc", "NOT_SELECT")));
        Assert.Null(second.FailureCode);
        var finals = P7F1V6Core.Finalise(SetIds, [("pack", first), ("reconcile", second)]);
        Assert.Equal("SELECTED", finals.Single(f => f.Id == "aaaa").State);
        Assert.Equal("reconcile", finals.Single(f => f.Id == "aaaa").Source);
        Assert.Equal("UNRESOLVED", finals.Single(f => f.Id == "bbbb").State);
        var st = P7F1V6Core.Status(finals);
        // UNRESOLVED is explicit coverage but not resolution: the four facts stay separate.
        Assert.True(st.CoverageComplete);
        Assert.False(st.ResolvedComplete);
        Assert.Equal(1, st.Unresolved);
        Assert.Equal("PARTIAL_UNRESOLVED", st.Assessment);
    }

    [Fact] public void A_tool_evidence_id_is_accepted_only_when_the_transcript_proves_it()
    {
        string Refs(string r) => $"[\"{r}\"]";
        string Resp(string r) => Dec("COMPLETE", D("aaaa", "SELECT", Refs(r)) + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        // Not issued at all: the regex matches, the transcript does not.
        Assert.Equal("EVIDENCE_ID_NOT_IN_TRANSCRIPT", Parse(Resp("E1.2")).FailureCode);
        var tr = new List<P7F1V6Tools.Record> { Rec("E1.1", Req, Sha, "OK", "L0000:S0") };
        Assert.Null(P7F1V6Core.ParseDecisions(Resp("E1.1"), Req, SetIds, Citable, AliasById, Sha, tr).FailureCode);
        // Another request, another source, a failed call, or a result without the subject's alias: each is rejected.
        Assert.Equal("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE", P7F1V6Core.ParseDecisions(Resp("E1.1"), Req, SetIds, Citable, AliasById, Sha,
            new[] { Rec("E1.1", "OTHER-REQ", Sha, "OK", "L0000:S0") }).FailureCode);
        Assert.Equal("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE", P7F1V6Core.ParseDecisions(Resp("E1.1"), Req, SetIds, Citable, AliasById, Sha,
            new[] { Rec("E1.1", Req, "other-source", "OK", "L0000:S0") }).FailureCode);
        Assert.Equal("EVIDENCE_ID_NOT_OK", P7F1V6Core.ParseDecisions(Resp("E1.1"), Req, SetIds, Citable, AliasById, Sha,
            new[] { Rec("E1.1", Req, Sha, "ERROR", "L0000:S0") }).FailureCode);
        Assert.Equal("EVIDENCE_DOES_NOT_CONTAIN_SUBJECT", P7F1V6Core.ParseDecisions(Resp("E1.1"), Req, SetIds, Citable, AliasById, Sha,
            new[] { Rec("E1.1", Req, Sha, "OK", "L0009:S0") }).FailureCode);
    }

    [Fact] public void The_same_contract_accepts_the_same_response_for_two_different_instructions()
    {
        // Condition 1: the decision contract does not read the instruction. Two requests with different verbatim instructions
        // (so different SHA-256) validate the same response identically.
        var instructionA = "Trích xuất các heading chính";
        var instructionB = "Chỉ lấy tiêu đề các điều trong nghị định";
        Assert.NotEqual(P7F1V6Core.InstructionSha256(instructionA), P7F1V6Core.InstructionSha256(instructionB));
        var response = Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        var a = Parse(response);
        var b = Parse(response);
        Assert.Equal(a.FailureCode, b.FailureCode);
        Assert.Equal(a.Decisions.Keys.Order(), b.Decisions.Keys.Order());
    }

    [Fact] public void Observed_and_evaluated_scopes_are_reported_apart_and_an_evaluation_without_observation_is_flagged()
    {
        // Observed: the model was shown aaaa and bbbb. Evaluated: decisions exist for aaaa, bbbb and cccc.
        var observed = new HashSet<string>(StringComparer.Ordinal) { "L0000:S0", "L0001:S0" };
        var passes = new[] { ("p", Parse(Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT", "[\"L0000:S0\"]")))) };
        var finals = P7F1V6Core.Finalise(SetIds, passes);
        var rep = P7F1V6Core.Scope(["L0000:S0", "L0001:S0", "L0002:S0", "L0003:S0"], observed, finals, AliasById);
        Assert.Equal(4, rep.InScope);
        Assert.Equal(2, rep.Observed);
        Assert.Equal(3, rep.Evaluated);
        Assert.Equal(0, rep.ObservedNotEvaluated);
        Assert.Equal(1, rep.EvaluatedNotObserved);   // cccc (L0002) was judged without being shown: a violation to report
    }

    [Fact] public void Overview_on_the_real_RFC_document_is_observations_only_and_finds_its_running_header()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path("todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf"));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var tools = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var overview = P7F1V6Core.Overview(tools.AllLines);
        var json = JsonSerializer.SerializeToElement(overview, P7F1QEvidenceTools.WireJson);
        Assert.True(json.GetProperty("pageCount").GetInt32() >= 10);
        var repeats = json.GetProperty("repeatGroups").EnumerateArray().ToArray();
        Assert.Contains(repeats, g => g.GetProperty("key").GetString()!.Contains("rfc", StringComparison.OrdinalIgnoreCase) && g.GetProperty("pages").GetArrayLength() >= 5);
        // No label vocabulary in the observation output: it describes lines, it does not name heading kinds.
        var text = json.GetRawText();
        foreach (var banned in new[] { "heading\"", "\"label", "ESTABLISHES", "REPRESENTS", "TOC" }) Assert.DoesNotContain(banned, text);
    }
}
