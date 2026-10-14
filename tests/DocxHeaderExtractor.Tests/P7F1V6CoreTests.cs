using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D1: V6 core contracts (instruction pin, interpretation, decision passes, evidence proof, resolution). Offline.</summary>
public sealed class P7F1V6CoreTests
{
    const string Req = "D094-V6-C1";
    const string Sha = "src-sha";
    static readonly string[] SetIds = ["aaaa", "bbbb", "cccc"];
    static readonly Dictionary<string, string> AliasById = new(StringComparer.Ordinal) { ["aaaa"] = "L0000:S0", ["bbbb"] = "L0001:S0", ["cccc"] = "L0002:S0" };
    static readonly HashSet<string> Shown = ["L0000:S0", "L0001:S0", "L0002:S0"];
    static readonly List<P7F1V6Tools.Record> NoTranscript = new();

    static P7F1V6Core.DecisionPass Parse(string name, string response, IReadOnlyList<P7F1V6Tools.Record>? transcript = null, IReadOnlySet<string>? shown = null) =>
        P7F1V6Core.ParseDecisions(name, response, Req, SetIds, shown ?? Shown, AliasById, Sha, transcript ?? NoTranscript);

    static string Interp(string status, string text, string? questions = null) =>
        $"{{\"protocolVersion\":\"P7_F1_V6_INTERPRET_V1\",\"requestId\":\"{Req}\",\"status\":\"{status}\",\"interpretation\":\"{text}\"{(questions is null ? "" : $",\"openQuestions\":[{questions}]")}}}";

    static string Dec(string status, string decisions) =>
        $"{{\"protocolVersion\":\"P7_F1_V6_DECISIONS_V1\",\"requestId\":\"{Req}\",\"status\":\"{status}\",\"decisions\":[{decisions}]}}";

    static string D(string id, string value, string refs = "[\"L0000:S0\"]") =>
        $"{{\"id\":\"{id}\",\"decision\":\"{value}\",\"evidenceRefs\":{refs}}}";

    static P7F1V6Tools.Record Rec(string ev, string req, string src, string status, params string[] aliases) =>
        new(ev, req, "get_page_text", src, "u", "s", status, null, "h", aliases.ToHashSet(StringComparer.Ordinal), "v");

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
        Assert.Equal("SUBJECT_NOT_DECIDED:cccc", Parse("p", Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT"))).FailureCode);
        var partial = Parse("pack", Dec("INCOMPLETE", D("aaaa", "NOT_SELECT")));
        Assert.Null(partial.FailureCode);
        var res = P7F1V6Core.Finalise(SetIds, [partial]);
        Assert.Equal("NOT_SELECTED", res.Finals.Single(f => f.Id == "aaaa").State);
        Assert.Equal("MISSING", res.Finals.Single(f => f.Id == "bbbb").State);   // omitted: unobserved, not negative
        Assert.False(P7F1V6Core.Status(res).CoverageComplete);
    }

    [Fact] public void Evidence_must_have_been_shown_and_a_decision_needs_evidence()
    {
        Assert.Equal("EVIDENCE_REQUIRED", Parse("p", Dec("COMPLETE", D("aaaa", "SELECT", "[]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"))).FailureCode);
        // An alias that exists in the document but was never shown in this request is rejected.
        var notShown = Dec("COMPLETE", D("aaaa", "SELECT", "[\"L9999:S0\"]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        Assert.Equal("EVIDENCE_NOT_SHOWN", Parse("p", notShown).FailureCode);
        Assert.Equal("ID_NOT_IN_REQUEST", Parse("p", Dec("INCOMPLETE", D("zzzz", "SELECT"))).FailureCode);
        Assert.Equal("ID_DECIDED_TWICE", Parse("p", Dec("INCOMPLETE", D("aaaa", "NOT_SELECT") + "," + D("aaaa", "SELECT"))).FailureCode);
    }

    [Fact] public void A_tool_evidence_id_is_accepted_only_when_the_transcript_proves_it()
    {
        string Resp(string r) => Dec("COMPLETE", D("aaaa", "SELECT", $"[\"{r}\"]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        Assert.Equal("EVIDENCE_ID_NOT_IN_TRANSCRIPT", Parse("p", Resp("E1.2")).FailureCode);
        Assert.Null(Parse("p", Resp("E1.1"), [Rec("E1.1", Req, Sha, "OK", "L0000:S0")]).FailureCode);
        Assert.Equal("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE", Parse("p", Resp("E1.1"), [Rec("E1.1", "OTHER-REQ", Sha, "OK", "L0000:S0")]).FailureCode);
        Assert.Equal("EVIDENCE_ID_WRONG_REQUEST_OR_SOURCE", Parse("p", Resp("E1.1"), [Rec("E1.1", Req, "other-source", "OK", "L0000:S0")]).FailureCode);
        Assert.Equal("EVIDENCE_ID_NOT_OK", Parse("p", Resp("E1.1"), [Rec("E1.1", Req, Sha, "ERROR", "L0000:S0")]).FailureCode);
        Assert.Equal("EVIDENCE_DOES_NOT_CONTAIN_SUBJECT", Parse("p", Resp("E1.1"), [Rec("E1.1", Req, Sha, "OK", "L0009:S0")]).FailureCode);
    }

    [Fact] public void Duplicate_evidence_ids_are_rejected_and_one_record_is_never_chosen()
    {
        var dup = new[] { Rec("E1.1", Req, Sha, "OK", "L0000:S0"), Rec("E1.1", Req, Sha, "OK", "L0000:S0") };
        var resp = Dec("COMPLETE", D("aaaa", "SELECT", "[\"E1.1\"]") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        Assert.Equal("EVIDENCE_ID_DUPLICATE", Parse("p", resp, dup).FailureCode);
    }

    [Fact] public void A_later_reconcile_overrides_earlier_decisions_and_resolution_needs_a_completed_reconcile()
    {
        var pack = Parse("pack-1", Dec("INCOMPLETE", D("aaaa", "NOT_SELECT")));
        var reconcile = Parse("reconcile", Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "UNRESOLVED", "[\"L0001:S0\"]") + "," + D("cccc", "NOT_SELECT")));
        var res = P7F1V6Core.Finalise(SetIds, [pack, reconcile]);
        Assert.Equal("SELECTED", res.Finals.Single(f => f.Id == "aaaa").State);
        Assert.Equal("reconcile", res.Finals.Single(f => f.Id == "aaaa").Source);
        var st = P7F1V6Core.Status(res);
        Assert.True(st.CoverageComplete);
        Assert.False(st.ResolvedComplete);                       // UNRESOLVED is not resolved
        Assert.Equal("PARTIAL_UNRESOLVED", st.Assessment);
    }

    [Fact] public void Decisions_without_a_completed_reconcile_are_UNRECONCILED_not_RESOLVED()
    {
        var pack = Parse("pack-1", Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT")));
        var st = P7F1V6Core.Status(P7F1V6Core.Finalise(SetIds, [pack]));
        Assert.True(st.CoverageComplete);
        Assert.False(st.ResolvedComplete);
        Assert.Equal("UNRECONCILED", st.Assessment);
    }

    [Fact] public void A_failed_reconcile_after_complete_packs_spoils_the_document()
    {
        var pack = Parse("pack-1", Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT")));
        var failed = Parse("reconcile", "{ not json");
        Assert.Equal("RESPONSE_NOT_JSON", failed.FailureCode);
        var res = P7F1V6Core.Finalise(SetIds, [pack, failed]);
        Assert.Equal("SELECTED", res.Finals.Single(f => f.Id == "aaaa").State);   // pack decisions survive as decisions
        Assert.Contains("reconcile:RESPONSE_NOT_JSON", res.FailedPasses);
        var st = P7F1V6Core.Status(res);
        Assert.False(st.CoverageComplete);
        Assert.False(st.ResolvedComplete);
        Assert.Equal("PASS_FAILED", st.Assessment);
    }

    [Fact] public void The_same_contract_accepts_the_same_response_for_two_different_instructions()
    {
        // Condition 1: the decision contract does not read the instruction. Different verbatim instructions have different hashes
        // and the same response validates identically.
        Assert.NotEqual(P7F1V6Core.InstructionSha256("Trích xuất các heading chính"), P7F1V6Core.InstructionSha256("Chỉ lấy tiêu đề các điều trong nghị định"));
        var response = Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT"));
        var a = Parse("p", response);
        var b = Parse("p", response);
        Assert.Equal(a.FailureCode, b.FailureCode);
        Assert.Equal(a.Decisions.Keys.Order(), b.Decisions.Keys.Order());
    }

    [Fact] public void Observed_and_evaluated_scopes_are_reported_apart_and_an_evaluation_without_observation_is_flagged()
    {
        var observed = new HashSet<string>(StringComparer.Ordinal) { "L0000:S0", "L0001:S0" };
        var pass = Parse("p", Dec("COMPLETE", D("aaaa", "SELECT") + "," + D("bbbb", "NOT_SELECT") + "," + D("cccc", "NOT_SELECT", "[\"L0000:S0\"]")), shown: Shown);
        var res = P7F1V6Core.Finalise(SetIds, [pass]);
        var rep = P7F1V6Core.Scope(["L0000:S0", "L0001:S0", "L0002:S0", "L0003:S0"], observed, res.Finals, AliasById);
        Assert.Equal(4, rep.InScope);
        Assert.Equal(2, rep.Observed);
        Assert.Equal(3, rep.Evaluated);
        Assert.Equal(0, rep.ObservedNotEvaluated);
        Assert.Equal(1, rep.EvaluatedNotObserved);   // cccc (L0002) was judged; it was not among the observed aliases: reported
    }

    [Fact] public void Overview_on_the_real_RFC_document_is_observations_only_and_finds_its_running_header()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path("todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf"));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var tools = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var json = JsonSerializer.SerializeToElement(P7F1V6Core.Overview(tools.AllLines), P7F1QEvidenceTools.WireJson);
        Assert.True(json.GetProperty("pageCount").GetInt32() >= 10);
        Assert.Contains(json.GetProperty("repeatGroups").EnumerateArray(), g => g.GetProperty("key").GetString()!.Contains("rfc", StringComparison.OrdinalIgnoreCase) && g.GetProperty("pages").GetArrayLength() >= 5);
        var text = json.GetRawText();
        foreach (var banned in new[] { "heading\"", "\"label", "ESTABLISHES", "REPRESENTS", "TOC" }) Assert.DoesNotContain(banned, text);
    }
}
