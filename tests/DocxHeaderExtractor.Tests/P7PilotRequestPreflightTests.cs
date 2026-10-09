using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PilotRequestPreflightTests
{
    private static PdfSourceBuildResult Source() => PdfSourceAdapter.BuildWithDetails([
        Line(1, 10, "Tiêu đề <&> 😀"), Line(1, 30, "Continuation"), Line(1, 50, "Body"),
        Line(2, 10, "Unreviewed source context"), Line(2, 30, "More context")], new string('a', 64));
    private static PdfLine Line(int page, double top, string text) => new(page, top, 12, text, 1, "", 0,
        20, 180, "Times-Bold", "", Bottom: top - 10, Top: top);
    private static SemanticEvidencePack Pack(PdfSourceBuildResult source) => new("synthetic:PACK_001", 1,
        source.Snapshot.Evidence.Take(3).ToArray(), source.Snapshot.Evidence.ToArray());
    private static string Functions(params string[] values) => JsonSerializer.Serialize(new {
        decisions = values.Select((function, i) => new { occurrence = "O" + (i + 1), function }) });
    private static string Anchors(params (string Id, string Value)[] values) => JsonSerializer.Serialize(new {
        decisions = values.Select(v => new { primary = v.Id, anchor = v.Value }) });

    [Fact] public void Selected_packs_are_unclipped_production_P05_and_cover_each_selected_page_atom_once()
    {
        var source = Source();
        var selected = P7PilotRequestPreflight.SelectPacks(source.Snapshot, source.Details, [1]);
        var original = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(source.Snapshot.Evidence, source.Details.LayoutBlockByAtom);
        Assert.All(selected, p => Assert.Contains(original, x => x.PackId == p.PackId && SpatialCanonical.Bytes(x).SequenceEqual(SpatialCanonical.Bytes(p))));
        var owned = selected.SelectMany(p => p.Owned).Select(a => a.SourceAlias).ToArray();
        Assert.Equal(owned.Length, owned.Distinct().Count());
        Assert.All(source.Snapshot.Atoms.Where(a => a.Page == 1), a => Assert.Contains(a.Alias, owned));
        Assert.Contains(selected.SelectMany(p => p.Owned), a => source.Snapshot.Atoms.Single(x => x.Alias == a.SourceAlias).Page == 2);
    }
    [Theory] [InlineData("EMPTY")] [InlineData("DUPLICATE")] [InlineData("FOREIGN")]
    public void Invalid_or_ambiguous_page_scope_is_rejected(string kind)
    {
        var source = Source(); int[] pages = kind switch { "EMPTY" => [], "DUPLICATE" => [1, 1], _ => [999] };
        Assert.Throws<InvalidOperationException>(() => P7PilotRequestPreflight.SelectPacks(source.Snapshot, source.Details, pages));
    }
    [Fact] public void Control_is_exact_production_F1_and_B_preserves_stage_input_and_issued_universe()
    {
        var source = Source(); var pack = Pack(source);
        var owned = source.Snapshot.Atoms.Take(3).ToArray(); var context = source.Snapshot.Atoms.Skip(3).Select(a => (a.Page, a.Text)).ToArray();
        var control = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context,
            PdfReadOnlyCorrespondenceBuilder.Build(owned, source.Snapshot.Atoms));
        var request = P7PilotRequestPreflight.F1(source.Snapshot, source.Details, pack);
        Assert.Equal(control.SystemPrompt, request.ControlSystemPrompt); Assert.Equal(control.UserMessage, request.ControlUserMessage);
        P7PilotRequestPreflight.ValidateProjection(request);
        Assert.Equal(new[] { "O1", "O2", "O3" }, request.DecisionSubjects);
        Assert.DoesNotContain("O4", request.VisibleAliasByOccurrence.Keys);
        Assert.Contains("Unreviewed source context", request.ControlUserMessage);
        var composer = new OpenRouterQwen37InferenceRequestComposer();
        Assert.Equal(composer.Build(control.SystemPrompt, control.UserMessage, 32768), composer.Build(request.ControlSystemPrompt, request.ControlUserMessage, 32768));
        using var body = JsonDocument.Parse(composer.Build(request.SystemPrompt, request.UserMessage, 32768));
        Assert.Equal(request.SystemPrompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(request.UserMessage, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Contains("\\u003C", request.ControlUserMessage);
    }
    [Fact] public void Request_generation_has_no_Gold_or_interpretation_sidecar_input()
    {
        var forbidden = new[] { "PilotApprovedDocument", "PilotScoringInput", "EvaluationAnnotation", "EvaluationScope", "PilotGoldUnit", "PilotReviewerInterpretation" };
        foreach (var method in typeof(P7PilotRequestPreflight).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            Assert.All(method.GetParameters(), p => Assert.DoesNotContain(p.ParameterType.Name, forbidden));
        var source = Source(); var request = P7PilotRequestPreflight.F1(source.Snapshot, source.Details, Pack(source));
        using var user = JsonDocument.Parse(request.UserMessage);
        var actual = user.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "decisionSubjects", "evidenceStoreSha256", "interpretationCharacterCap", "protocolVersion", "sourceAliasUniverseSha256", "sourceEvidence", "sourceSha256", "stage", "stageInput" }.Order(), actual);
        Assert.DoesNotContain("unitRole", request.UserMessage); Assert.DoesNotContain("reviewedRows", request.UserMessage);
    }
    [Fact] public void All_OTHER_control_F1_suppresses_G2A_without_inventing_an_empty_call()
    {
        var source = Source();
        Assert.Null(P7PilotRequestPreflight.G2A(source.Snapshot, source.Details, Pack(source), Functions("OTHER", "OTHER", "OTHER"), "stop"));
        Assert.Throws<InvalidOperationException>(() => P7PilotRequestPreflight.H2C(source.Snapshot, source.Details, Pack(source),
            Functions("OTHER", "OTHER", "OTHER"), "stop", Anchors(("O1", "HAS_STRUCTURAL_EXTENT")), "stop"));
    }
    [Theory] [InlineData("length")] [InlineData("error")]
    public void Non_stop_F1_cannot_generate_downstream(string finish)
    {
        var source = Source();
        Assert.Throws<InvalidOperationException>(() => P7PilotRequestPreflight.G2A(source.Snapshot, source.Details, Pack(source), Functions("OTHER", "ESTABLISHES_STRUCTURE", "OTHER"), finish));
    }
    [Fact] public void Partial_F1_cannot_choose_a_downstream_subset()
    {
        var source = Source();
        Assert.Throws<InvalidOperationException>(() => P7PilotRequestPreflight.G2A(source.Snapshot, source.Details, Pack(source), Functions("ESTABLISHES_STRUCTURE"), "stop"));
    }
    [Fact] public void G2A_subjects_come_from_control_F1_not_geometry_or_treatment_interpretation()
    {
        var source = Source(); var request = P7PilotRequestPreflight.G2A(source.Snapshot, source.Details, Pack(source),
            Functions("OTHER", "ESTABLISHES_STRUCTURE", "OTHER"), "stop")!;
        Assert.Equal(new[] { "O2" }, request.DecisionSubjects);
        Assert.Equal(new[] { "O1", "O2", "O3" }, request.VisibleAliasByOccurrence.Keys);
        P7PilotRequestPreflight.ValidateProjection(request);
        using var user = JsonDocument.Parse(request.UserMessage);
        Assert.All(user.RootElement.GetProperty("sourceEvidence").EnumerateArray(), r =>
            Assert.Equal(r.GetProperty("occurrence").GetString() == "O2", r.GetProperty("selectable").GetBoolean()));
    }
    [Theory] [InlineData("UNKNOWN_PRIMARY")] [InlineData("MISSING_PRIMARY")] [InlineData("NON_STOP")]
    public void Invalid_G2A_cannot_spawn_H2C(string failure)
    {
        var source = Source(); var raw = failure switch { "UNKNOWN_PRIMARY" => Anchors(("O1", "HAS_STRUCTURAL_EXTENT")),
            "MISSING_PRIMARY" => Anchors(), _ => Anchors(("O2", "HAS_STRUCTURAL_EXTENT")) };
        Assert.Throws<InvalidOperationException>(() => P7PilotRequestPreflight.H2C(source.Snapshot, source.Details, Pack(source),
            Functions("OTHER", "ESTABLISHES_STRUCTURE", "OTHER"), "stop", raw, failure == "NON_STOP" ? "length" : "stop"));
    }
    [Fact] public void NO_anchor_yields_no_H2C_and_terminal_HAS_remains_issued()
    {
        var source = Source(); var f1 = Functions("OTHER", "OTHER", "ESTABLISHES_STRUCTURE");
        Assert.Empty(P7PilotRequestPreflight.H2C(source.Snapshot, source.Details, Pack(source), f1, "stop", Anchors(("O3", "NO_STRUCTURAL_EXTENT")), "stop"));
        var request = Assert.Single(P7PilotRequestPreflight.H2C(source.Snapshot, source.Details, Pack(source), f1, "stop", Anchors(("O3", "HAS_STRUCTURAL_EXTENT")), "stop"));
        P7PilotRequestPreflight.ValidateProjection(request);
        Assert.Equal(new[] { "O3" }, request.DecisionSubjects); Assert.Equal(new[] { "O3" }, request.VisibleAliasByOccurrence.Keys);
        using var payload = JsonDocument.Parse("{\"decisions\":[{\"anchor\":\"O3\",\"headingMembers\":[\"O3\"],\"endOccurrence\":\"O3\",\"firstOutsideOccurrence\":null,\"firstOutsideRole\":\"NO_VISIBLE_SUCCESSOR\"}]}");
        PdfInterpretationProtocol.ValidateStage(payload.RootElement, request, source.Snapshot);
    }
    [Fact] public void Downstream_recipe_does_not_force_stop_at_F1_OTHER_or_select_context_prefix()
    {
        var source = Source();
        var request = Assert.Single(P7PilotRequestPreflight.H2C(source.Snapshot, source.Details, Pack(source),
            Functions("ESTABLISHES_STRUCTURE", "OTHER", "OTHER"), "stop", Anchors(("O1", "HAS_STRUCTURAL_EXTENT")), "stop"));
        using var valid = JsonDocument.Parse("{\"decisions\":[{\"anchor\":\"O1\",\"headingMembers\":[\"O1\",\"O2\"],\"endOccurrence\":\"O2\",\"firstOutsideOccurrence\":\"O3\",\"firstOutsideRole\":\"BODY_CONTENT\"}]}");
        PdfInterpretationProtocol.ValidateStage(valid.RootElement, request, source.Snapshot);
        using var invalid = JsonDocument.Parse(valid.RootElement.GetRawText().Replace("\"O1\",\"O2\"", "\"O1\",\"O4\""));
        Assert.Throws<InvalidOperationException>(() => PdfInterpretationProtocol.ValidateStage(invalid.RootElement, request, source.Snapshot));
    }

    [Fact] public void Pilot_receipt_freezes_F1_only_and_does_not_claim_unknown_downstream_calls_are_zero()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.pilot-f1-request-freeze.v1.json"));
        Assert.Equal("2a5e4491270fd7ec92f536bfa8ac1e9812c8440f9fbb3e0a58a99d06e2965ae1", SpatialCanonical.Hash(bytes));
        using var json = JsonDocument.Parse(bytes); var r = json.RootElement;
        Assert.Equal(14, r.GetProperty("f1FrozenRequests").GetInt32()); Assert.Equal(7, r.GetProperty("selectedPacks").GetInt32());
        Assert.Equal(213, r.GetProperty("inScopeOccurrences").GetInt32()); Assert.Equal(643, r.GetProperty("issuedOwnedOccurrences").GetInt32());
        Assert.Equal(5, r.GetProperty("sourceDocuments").GetInt32()); Assert.Equal(6, r.GetProperty("evaluationPages").GetInt32());
        Assert.False(r.GetProperty("downstreamRequestCountKnown").GetBoolean());
        Assert.False(r.GetProperty("downstreamRequestBytesFrozen").GetBoolean());
        Assert.False(r.GetProperty("allStagesReadyForAuthorization").GetBoolean());
        Assert.Equal(0, r.GetProperty("g2aFrozenRequests").GetInt32()); Assert.Equal(0, r.GetProperty("h2cFrozenRequests").GetInt32());
        Assert.True(r.GetProperty("noGoldDecisionsOrSidecarsLoaded").GetBoolean());
        Assert.True(r.GetProperty("noProviderResponsesLoaded").GetBoolean());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32()); Assert.Equal(0, r.GetProperty("authorizedProviderCalls").GetInt32());
        Assert.Equal("LOCKED", r.GetProperty("providerExecution").GetString());
        Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
        var requests = r.GetProperty("requests").EnumerateArray().ToArray(); Assert.Equal(14, requests.Length);
        Assert.Equal(14, requests.Select(c => c.GetProperty("callHandle").GetString()).Distinct().Count());
        foreach (var group in requests.GroupBy(c => c.GetProperty("document").GetString() + "|" + c.GetProperty("pack").GetString()))
        {
            Assert.Equal(new[] { "B", "CONTROL" }, group.Select(c => c.GetProperty("arm").GetString()!).Order());
            Assert.Single(group.Select(c => c.GetProperty("issuedUniverseSha256").GetString()).Distinct());
            Assert.Single(group.Select(c => c.GetProperty("decisionSubjects").GetRawText()).Distinct());
        }
        Assert.All(r.GetProperty("documents").EnumerateArray().SelectMany(d => d.GetProperty("packs").EnumerateArray()), p =>
        {
            Assert.Equal(JsonValueKind.Null, p.GetProperty("expectedDownstreamCallCount").ValueKind);
            Assert.Equal("WAITING_FROZEN_CONTROL_F1_LEDGER", p.GetProperty("g2a").GetString());
            Assert.Equal("WAITING_FROZEN_CONTROL_G2A_LEDGER", p.GetProperty("h2c").GetString());
        });
    }
}
