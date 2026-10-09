using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7UpstreamCaptureReadinessTests
{
    // Fabricated test-only transport fixtures, never persisted as a production/provider baseline.
    private static PdfSourceBuildResult Source() => PdfSourceAdapter.BuildWithDetails([
        new PdfLine(1, 10, 12, "Title <&> 😀", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 0, Top: 10),
        new PdfLine(1, 30, 12, "Continuation", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 20, Top: 30),
        new PdfLine(1, 50, 12, "Body", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 40, Top: 50)], new string('a', 64));
    private static SemanticEvidencePack Pack(PdfSourceBuildResult s) => new("synthetic:PACK_001", 1, s.Snapshot.Evidence, s.Snapshot.Evidence);
    private static string Functions(params string[] labels) => JsonSerializer.Serialize(new {
        decisions = labels.Select((function, i) => new { occurrence = "O" + (i + 1), function }) });
    private static string Anchors(string primary, string anchor) => JsonSerializer.Serialize(new {
        decisions = new[] { new { primary, anchor } } });
    private static P7RawCapture Capture(PdfSourceBuildResult s, InterpretationRequest request, P7CaptureArm arm,
        string decision, P7ExperimentMode mode = P7ExperimentMode.SharedF1Root, params string[] parents)
    {
        using var payload = JsonDocument.Parse(decision);
        var raw = arm == P7CaptureArm.Control ? decision : JsonSerializer.Serialize(new {
            protocolVersion = PdfInterpretationProtocol.Version, stage = PdfInterpretationProtocol.Name(request.Stage),
            sourceSha256 = s.Snapshot.SourceSha256, evidenceStoreSha256 = request.EvidenceStore.StoreSha256,
            stageDecision = payload.RootElement, analysis = request.DecisionSubjects.Select(id => new {
                subject = id, references = new[] { new { occurrence = id, sourceAlias = request.VisibleAliasByOccurrence[id], fields = new[] { "page" } } },
                assertions = Array.Empty<object>(), interpretation = "Synthetic test explanation, not semantic authority." }) });
        var system = arm == P7CaptureArm.Control ? request.ControlSystemPrompt : request.SystemPrompt;
        var user = arm == P7CaptureArm.Control ? request.ControlUserMessage : request.UserMessage;
        var body = new OpenRouterQwen37InferenceRequestComposer().Build(system, user, 32768);
        var response = Encoding.UTF8.GetBytes(raw); var sse = Encoding.UTF8.GetBytes("test-only-sse");
        var observation = JsonSerializer.SerializeToUtf8Bytes(new { Content = raw, FinishReason = "stop", RetryCount = 0, RawSse = "test-only-sse", Usage = (object?)null });
        var identity = new P7CaptureIdentity("PDF-" + s.Snapshot.SourceSha256, s.Snapshot.SourceSha256, s.Snapshot.SourceAliasUniverseHash,
            Pack(s).PackId, SpatialCanonical.Hash(SpatialCanonical.Bytes(Pack(s))), P7UpstreamCaptureReadiness.IssueHash(request),
            request.Stage, arm, mode, SpatialCanonical.Hash(body), Hash(system), Hash(user), parents);
        var freeze = SpatialCanonical.Bytes(new { status = "RAW_FROZEN_BEFORE_PARSE", hashes = new[] {
            new { file = "provider-body.json", sha256 = SpatialCanonical.Hash(body) },
            new { file = "response.txt", sha256 = SpatialCanonical.Hash(response) },
            new { file = "response.sse", sha256 = SpatialCanonical.Hash(sse) } } });
        var receipt = new P7CaptureReceipt(identity, "PROVIDER_RAW", "RAW_FROZEN_BEFORE_PARSE", "stop", 1, 0, false, false, null,
            SpatialCanonical.Hash(response), SpatialCanonical.Hash(sse), SpatialCanonical.Hash(observation), SpatialCanonical.Hash(freeze),
            SpatialCanonical.Hash(SpatialCanonical.Bytes(payload.RootElement)), new string('b', 64));
        return new(body, response, sse, observation, freeze, receipt);
    }
    private static string Hash(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
    private static P7ValidatedCapture Validate(PdfSourceBuildResult s, InterpretationRequest request, P7RawCapture raw) =>
        P7UpstreamCaptureReadiness.Validate(raw, raw.Receipt.Identity, request, s.Snapshot, s.Details);

    [Fact] public void Capture_receipt_schema_round_trips_named_stages_arms_and_modes()
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        var bytes = SpatialCanonical.Bytes(raw.Receipt);
        var parsed = P7UpstreamCaptureReadiness.ParseReceipt(bytes);
        Assert.Equal(bytes, SpatialCanonical.Bytes(parsed));
        Assert.Contains("\"mode\":\"SharedF1Root\"", Encoding.UTF8.GetString(bytes));
    }
    [Fact] public void Published_schema_property_sets_match_the_capture_DTO_not_a_stale_template()
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        using var actual = JsonDocument.Parse(SpatialCanonical.Bytes(raw.Receipt));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("scripts/P7UpstreamCaptureReadiness/capture-receipt.schema.v1.json")));
        void EqualProperties(JsonElement value, JsonElement contract) {
            var names = value.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(names, contract.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).Order());
            Assert.Equal(names, contract.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
            Assert.False(contract.GetProperty("additionalProperties").GetBoolean());
        }
        EqualProperties(actual.RootElement, schema.RootElement);
        EqualProperties(actual.RootElement.GetProperty("identity"), schema.RootElement.GetProperty("$defs").GetProperty("identity"));
    }
    [Theory] [InlineData("EXTRA")] [InlineData("MISSING")] [InlineData("DUPLICATE")]
    [InlineData("NUMERIC_ENUM")]
    public void Receipt_unknown_missing_duplicate_or_numeric_protocol_fields_fail_closed(string kind)
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        var text = Encoding.UTF8.GetString(SpatialCanonical.Bytes(raw.Receipt));
        text = kind switch { "EXTRA" => "{\"extra\":true," + text[1..], "MISSING" => text.Replace("\"origin\":\"PROVIDER_RAW\",", ""),
            "DUPLICATE" => "{\"origin\":\"PROVIDER_RAW\"," + text[1..], _ => text.Replace("\"arm\":\"Control\"", "\"arm\":\"0\"") };
        Assert.ThrowsAny<Exception>(() => P7UpstreamCaptureReadiness.ParseReceipt(Encoding.UTF8.GetBytes(text)));
    }
    [Theory] [InlineData("DOCUMENT")] [InlineData("SOURCE")]
    [InlineData("UNIVERSE")] [InlineData("BODY")]
    public void Receipt_cannot_bind_a_different_frozen_request(string kind)
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        var expected = raw.Receipt.Identity;
        expected = kind switch { "DOCUMENT" => expected with { Document = "foreign" }, "SOURCE" => expected with { SourceSha256 = new string('c', 64) },
            "UNIVERSE" => expected with { SourceUniverseSha256 = new string('c', 64) }, _ => expected with { ProviderBodySha256 = new string('c', 64) } };
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.Validate(raw, expected, f1, s.Snapshot, s.Details));
    }

    [Theory] [InlineData(false, 1314, 14, 1286)] [InlineData(true, 2614, 28, 2572)]
    public void Worst_case_counts_are_caps_not_authorization_or_Gold_estimates(bool both, int total, int g2a, int h2c)
    {
        var r = P7UpstreamCaptureReadiness.Bounds(7, 643, both);
        Assert.Equal(14, r.F1); Assert.Equal(g2a, r.G2AUpper); Assert.Equal(h2c, r.H2CUpper);
        Assert.Equal(total, r.TotalUpper); Assert.Equal((long)total * 32768, r.CompletionTokenCeilingTotal);
        Assert.Equal("UNKNOWN_UNTIL_VALID_UPSTREAM", r.ActualDownstreamCounts);
    }
    [Fact] public void Missing_capture_is_not_an_empty_ledger()
    {
        var s = Source(); var request = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, request, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        Assert.Equal("P7_CAPTURE_MISSING_NO_DOWNSTREAM", Assert.Throws<InvalidOperationException>(() =>
            P7UpstreamCaptureReadiness.Validate(null, raw.Receipt.Identity, request, s.Snapshot, s.Details)).Message);
    }
    [Theory] [InlineData("SYNTHETIC")] [InlineData("GOLD_DERIVED")]
    public void Non_provider_origins_cannot_supply_baseline_decisions(string origin)
    {
        var s = Source(); var request = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, request, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        Assert.Throws<InvalidOperationException>(() => Validate(s, request, raw with { Receipt = raw.Receipt with { Origin = origin } }));
    }
    [Theory] [InlineData("FAILED")] [InlineData("INVALID")] [InlineData("INCOMPLETE_RAW_CAPTURE")]
    public void Failed_invalid_or_partial_statuses_are_quarantined(string status)
    {
        var s = Source(); var request = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, request, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        Assert.Throws<InvalidOperationException>(() => Validate(s, request, raw with { Receipt = raw.Receipt with { Status = status } }));
    }
    [Theory] [InlineData("LENGTH")] [InlineData("RETRY")] [InlineData("REPAIR")] [InlineData("FALLBACK")]
    [InlineData("FAILURE")]
    public void Non_primary_or_non_stop_results_cannot_fan_out(string kind)
    {
        var s = Source(); var request = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, request, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        var r = raw.Receipt;
        r = kind switch { "LENGTH" => r with { FinishReason = "length" }, "RETRY" => r with { HttpAttempts = 2, RetryCount = 1 },
            "REPAIR" => r with { Repair = true }, "FALLBACK" => r with { Fallback = true }, _ => r with { FailureClass = "timeout" } };
        Assert.Throws<InvalidOperationException>(() => Validate(s, request, raw with { Receipt = r }));
    }
    [Theory] [InlineData("RESPONSE")] [InlineData("SSE")] [InlineData("BODY")]
    [InlineData("OBSERVATION")] [InlineData("FREEZE")] [InlineData("PARSED")]
    public void Raw_or_parsed_hash_drift_is_rejected(string kind)
    {
        var s = Source(); var request = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var raw = Capture(s, request, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER"));
        var bad = Encoding.UTF8.GetBytes("changed");
        raw = kind switch { "RESPONSE" => raw with { Response = bad }, "SSE" => raw with { Sse = bad },
            "BODY" => raw with { ProviderBody = bad }, "OBSERVATION" => raw with { Observation = bad },
            "FREEZE" => raw with { RawFreeze = bad }, _ => raw with { Receipt = raw.Receipt with { ParsedDecisionSha256 = new string('0', 64) } } };
        Assert.Throws<InvalidOperationException>(() => Validate(s, request, raw));
    }
    [Fact] public void Validated_all_OTHER_is_a_real_empty_downstream_not_a_failure()
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        var r = Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "OTHER")));
        Assert.Null(P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, r, s.Snapshot, s.Details, Pack(s)));
        Assert.Equal("NOT_VERIFIED_PROTOCOL_ONLY", r.SemanticStatus);
    }
    [Fact] public void Controlled_replay_and_natural_chain_use_different_frozen_upstreams_without_Gold()
    {
        var s = Source(); var pack = Pack(s); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var c = Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("ESTABLISHES_STRUCTURE", "OTHER", "OTHER")));
        var b = Validate(s, f1, Capture(s, f1, P7CaptureArm.B, Functions("OTHER", "ESTABLISHES_STRUCTURE", "OTHER")));
        var controlled = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, c, s.Snapshot, s.Details, pack)!;
        var natural = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.NaturalEndToEnd, P7CaptureArm.B, b, s.Snapshot, s.Details, pack)!;
        Assert.Equal(new[] { "O1" }, controlled.Request.DecisionSubjects);
        Assert.Equal(new[] { "O2" }, natural.Request.DecisionSubjects);
        Assert.Equal(new[] { c.CaptureSha256 }, controlled.ParentCaptureSha256);
        Assert.Equal(new[] { b.CaptureSha256 }, natural.ParentCaptureSha256);
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, b, s.Snapshot, s.Details, pack));
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.NaturalEndToEnd, P7CaptureArm.B, c, s.Snapshot, s.Details, pack));
        var ng = Validate(s, natural.Request, Capture(s, natural.Request, P7CaptureArm.B,
            Anchors("O2", "HAS_STRUCTURAL_EXTENT"), P7ExperimentMode.NaturalEndToEnd, b.CaptureSha256));
        var h2 = Assert.Single(P7UpstreamCaptureReadiness.H2C(P7ExperimentMode.NaturalEndToEnd, P7CaptureArm.B, b, ng, s.Snapshot, s.Details, pack));
        Assert.Equal(new[] { "O2" }, h2.Request.DecisionSubjects);
        Assert.Equal(new[] { b.CaptureSha256, ng.CaptureSha256 }, h2.ParentCaptureSha256);
    }
    [Fact] public void H2C_controlled_uses_Control_G2A_and_NO_yields_no_calls()
    {
        var s = Source(); var pack = Pack(s); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var c = Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("OTHER", "OTHER", "ESTABLISHES_STRUCTURE")));
        var gen = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.Control, c, s.Snapshot, s.Details, pack)!;
        var no = Validate(s, gen.Request, Capture(s, gen.Request, P7CaptureArm.Control, Anchors("O3", "NO_STRUCTURAL_EXTENT"), gen.Mode, c.CaptureSha256));
        Assert.Empty(P7UpstreamCaptureReadiness.H2C(gen.Mode, P7CaptureArm.B, c, no, s.Snapshot, s.Details, pack));
        var yes = Validate(s, gen.Request, Capture(s, gen.Request, P7CaptureArm.Control, Anchors("O3", "HAS_STRUCTURAL_EXTENT"), gen.Mode, c.CaptureSha256));
        var h = Assert.Single(P7UpstreamCaptureReadiness.H2C(gen.Mode, P7CaptureArm.B, c, yes, s.Snapshot, s.Details, pack));
        Assert.Equal(new[] { "O3" }, h.Request.DecisionSubjects); // terminal anchor retained
    }
    [Fact] public void Different_source_pack_or_parent_lineage_cannot_be_joined()
    {
        var s = Source(); var pack = Pack(s); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var c = Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("ESTABLISHES_STRUCTURE", "OTHER", "OTHER")));
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, c, s.Snapshot, s.Details, pack with { PackId = "different" }));
        var gen = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.Control, c, s.Snapshot, s.Details, pack)!;
        var wrongParent = Validate(s, gen.Request, Capture(s, gen.Request, P7CaptureArm.Control, Anchors("O1", "HAS_STRUCTURAL_EXTENT"), gen.Mode, new string('c', 64)));
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.H2C(gen.Mode, P7CaptureArm.B, c, wrongParent, s.Snapshot, s.Details, pack));
    }
    [Fact] public void Partial_or_invalid_total_ledger_cannot_supply_upstream()
    {
        var s = Source(); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, Pack(s));
        Assert.Throws<InvalidOperationException>(() => Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("ESTABLISHES_STRUCTURE"))));
        Assert.Throws<InvalidOperationException>(() => Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("HEADING", "OTHER", "OTHER"))));
    }
    [Fact] public void Validated_capture_does_not_force_stop_when_continuation_has_F1_OTHER()
    {
        var s = Source(); var pack = Pack(s); var f1 = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var c = Validate(s, f1, Capture(s, f1, P7CaptureArm.Control, Functions("ESTABLISHES_STRUCTURE", "OTHER", "OTHER")));
        var gen = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.Control, c, s.Snapshot, s.Details, pack)!;
        var g = Validate(s, gen.Request, Capture(s, gen.Request, P7CaptureArm.Control, Anchors("O1", "HAS_STRUCTURAL_EXTENT"), gen.Mode, c.CaptureSha256));
        var h = Assert.Single(P7UpstreamCaptureReadiness.H2C(gen.Mode, P7CaptureArm.Control, c, g, s.Snapshot, s.Details, pack));
        var decision = "{\"decisions\":[{\"anchor\":\"O1\",\"headingMembers\":[\"O1\",\"O2\"],\"endOccurrence\":\"O2\",\"firstOutsideOccurrence\":\"O3\",\"firstOutsideRole\":\"BODY_CONTENT\"}]}";
        var validated = Validate(s, h.Request, Capture(s, h.Request, P7CaptureArm.Control, decision, h.Mode, c.CaptureSha256, g.CaptureSha256));
        Assert.Equal(2, validated.StageDecision.GetProperty("decisions")[0].GetProperty("headingMembers").GetArrayLength());
        Assert.Equal("NOT_VERIFIED_PROTOCOL_ONLY", validated.SemanticStatus);
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.H2C(P7ExperimentMode.NaturalEndToEnd, P7CaptureArm.Control, c, g, s.Snapshot, s.Details, pack));
    }
    [Fact] public void Readiness_manifest_is_conditional_and_no_legacy_capture_is_silently_adopted()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.upstream-capture-readiness.v1.json"));
        Assert.Equal("6e7ef07e466f81b6e7c98ef949c8fe4820d5baeb194ddae1abfd7fe22a56cea2", SpatialCanonical.Hash(bytes));
        using var json = JsonDocument.Parse(bytes); var r = json.RootElement;
        Assert.Equal("CONDITIONALLY_FROZEN_CAPTURE_AND_GENERATION_CONTRACTS", r.GetProperty("status").GetString());
        Assert.Equal(14, r.GetProperty("rootCaptureBindings").GetArrayLength());
        var reuse = r.GetProperty("reuseInventory");
        Assert.True(reuse.GetProperty("f1").GetProperty("exactBodySystemUserMatch").GetBoolean());
        Assert.True(reuse.GetProperty("g2a").GetProperty("exactReconstructedBodyMatch").GetBoolean());
        Assert.False(reuse.GetProperty("reusePolicyAmendmentApproved").GetBoolean());
        Assert.False(reuse.GetProperty("fullCurrentQualificationEligibility").GetBoolean());
        Assert.False(r.GetProperty("downstreamActualRequestBytesFrozen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("sizing").GetProperty("downstreamPayloadBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("budget").GetProperty("approvedSpend").ValueKind);
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.Equal("LOCKED", r.GetProperty("providerExecution").GetString());
        Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
    }
}
