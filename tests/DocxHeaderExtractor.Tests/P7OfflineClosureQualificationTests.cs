using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7OfflineClosureQualificationTests
{
    private const string Recipe = "baf4c9bdf98d46737320a539c96090a60d4454b3a01d478f10f9c49a6ba859d1";
    // All captures in this file are fabricated protocol fixtures, not observations of a provider.
    private static PdfSourceBuildResult Source() => PdfSourceAdapter.BuildWithDetails([
        new PdfLine(1, 10, 12, "Tiêu đề <&> 😀", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 0, Top: 10),
        new PdfLine(1, 30, 12, "Dòng tiếp", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 20, Top: 30),
        new PdfLine(1, 50, 12, "Nội dung", 1, "", 0, 20, 180, "Times-Bold", "", Bottom: 40, Top: 50)], new string('a', 64));
    private static SemanticEvidencePack Pack(PdfSourceBuildResult s) => new("synthetic:PACK_001", 1, s.Snapshot.Evidence, s.Snapshot.Evidence);
    private static string Functions(string primary = "O1") => JsonSerializer.Serialize(new {
        decisions = Enumerable.Range(1, 3).Select(i => new { occurrence = "O" + i,
            function = "O" + i == primary ? "ESTABLISHES_STRUCTURE" : "OTHER" }) });
    private static string Anchors(string primary) => JsonSerializer.Serialize(new {
        decisions = new[] { new { primary, anchor = "HAS_STRUCTURAL_EXTENT" } } });
    private static byte[] Body(P7GeneratedStage stage) => Body(stage.Request, stage.Arm);
    private static byte[] Body(InterpretationRequest request, P7CaptureArm arm) =>
        new OpenRouterQwen37InferenceRequestComposer().Build(
            arm == P7CaptureArm.Control ? request.ControlSystemPrompt : request.SystemPrompt,
            arm == P7CaptureArm.Control ? request.ControlUserMessage : request.UserMessage, 32768);
    private static P7RawCapture Raw(PdfSourceBuildResult s, InterpretationRequest request, P7CaptureArm arm,
        string decision, P7ExperimentMode mode = P7ExperimentMode.SharedF1Root, params string[] parents)
    {
        using var d = JsonDocument.Parse(decision);
        var raw = arm == P7CaptureArm.Control ? decision : JsonSerializer.Serialize(new {
            protocolVersion = PdfInterpretationProtocol.Version, stage = PdfInterpretationProtocol.Name(request.Stage),
            sourceSha256 = s.Snapshot.SourceSha256, evidenceStoreSha256 = request.EvidenceStore.StoreSha256,
            stageDecision = d.RootElement, analysis = request.DecisionSubjects.Select(id => new {
                subject = id, references = new[] { new { occurrence = id, sourceAlias = request.VisibleAliasByOccurrence[id], fields = new[] { "page" } } },
                assertions = Array.Empty<object>(), interpretation = "Protocol fixture; no semantic accuracy claim." }) });
        var body = Body(request, arm); var response = Encoding.UTF8.GetBytes(raw);
        var sse = Encoding.UTF8.GetBytes("synthetic-protocol-sse");
        var observation = JsonSerializer.SerializeToUtf8Bytes(new { Content = raw, FinishReason = "stop", RetryCount = 0, RawSse = Encoding.UTF8.GetString(sse) });
        using var b = JsonDocument.Parse(body); var messages = b.RootElement.GetProperty("messages");
        var identity = new P7CaptureIdentity("PDF-" + s.Snapshot.SourceSha256, s.Snapshot.SourceSha256, s.Snapshot.SourceAliasUniverseHash,
            Pack(s).PackId, SpatialCanonical.Hash(SpatialCanonical.Bytes(Pack(s))), P7UpstreamCaptureReadiness.IssueHash(request),
            request.Stage, arm, mode, SpatialCanonical.Hash(body), Hash(messages[0].GetProperty("content").GetString()!),
            Hash(messages[1].GetProperty("content").GetString()!), parents);
        var freeze = SpatialCanonical.Bytes(new { status = "RAW_FROZEN_BEFORE_PARSE", hashes = new[] {
            new { file = "provider-body.json", sha256 = SpatialCanonical.Hash(body) },
            new { file = "response.txt", sha256 = SpatialCanonical.Hash(response) },
            new { file = "response.sse", sha256 = SpatialCanonical.Hash(sse) } } });
        // PROVIDER_RAW here exercises the validator's success branch only; it is not a real receipt.
        return new(body, response, sse, observation, freeze, new(identity, "PROVIDER_RAW", "RAW_FROZEN_BEFORE_PARSE", "stop", 1, 0,
            false, false, null, SpatialCanonical.Hash(response), SpatialCanonical.Hash(sse), SpatialCanonical.Hash(observation),
            SpatialCanonical.Hash(freeze), SpatialCanonical.Hash(SpatialCanonical.Bytes(d.RootElement)), new string('b', 64)));
    }
    private static P7ValidatedCapture Validate(PdfSourceBuildResult s, InterpretationRequest request, P7RawCapture raw) =>
        P7UpstreamCaptureReadiness.Validate(raw, raw.Receipt.Identity, request, s.Snapshot, s.Details);
    private static string Hash(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
    private static P7ProtocolFixtureBinding Binding(PdfSourceBuildResult s, P7GeneratedStage stage) =>
        P7OfflineClosureQualification.FingerprintProtocolFixture(stage, s.Snapshot, Pack(s), Body(stage), Recipe);

    [Theory] [InlineData("ControlledDownstream", "Control")]
    [InlineData("ControlledDownstream", "B")]
    [InlineData("NaturalEndToEnd", "Control")]
    [InlineData("NaturalEndToEnd", "B")]
    public void Valid_fixtures_generate_deterministic_bodies_and_bindings(string modeName, string armName)
    {
        var mode = Enum.Parse<P7ExperimentMode>(modeName); var arm = Enum.Parse<P7CaptureArm>(armName);
        var s = Source(); var pack = Pack(s); var f = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var upstreamArm = mode == P7ExperimentMode.ControlledDownstream ? P7CaptureArm.Control : arm;
        var f1 = Validate(s, f, Raw(s, f, upstreamArm, Functions()));
        P7GeneratedStage G(P7CaptureArm target) => P7UpstreamCaptureReadiness.G2A(mode, target, f1, s.Snapshot, s.Details, pack)!;
        var g = G(arm); var again = G(arm);
        Assert.Equal(Body(g), Body(again)); Assert.Equal(SpatialCanonical.Bytes(Binding(s, g)), SpatialCanonical.Bytes(Binding(s, again)));
        var upstreamG = G(upstreamArm);
        var g2a = Validate(s, upstreamG.Request, Raw(s, upstreamG.Request, upstreamArm, Anchors("O1"), mode, f1.CaptureSha256));
        P7GeneratedStage H() => Assert.Single(P7UpstreamCaptureReadiness.H2C(mode, arm, f1, g2a, s.Snapshot, s.Details, pack));
        var h = H(); var hAgain = H();
        Assert.Equal(Body(h), Body(hAgain)); Assert.Equal(SpatialCanonical.Bytes(Binding(s, h)), SpatialCanonical.Bytes(Binding(s, hAgain)));
        Assert.Equal(new[] { f1.CaptureSha256, g2a.CaptureSha256 }, Binding(s, h).ParentCaptureSha256);
        Assert.False(Binding(s, h).SemanticAccuracyEligible); Assert.False(Binding(s, h).ExecutionAuthorized);
        Assert.Equal("PROTOCOL_TEST_FIXTURE_ONLY", Binding(s, h).Origin);
    }

    [Theory] [InlineData("MISSING")] [InlineData("INVALID_LEDGER")] [InlineData("FAILED")]
    [InlineData("WRONG_SOURCE")] [InlineData("WRONG_LINEAGE")] [InlineData("WRONG_ARM")]
    public void Invalid_or_missing_upstream_never_selects_an_alternative(string failure)
    {
        var s = Source(); var pack = Pack(s); var f = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var raw = Raw(s, f, P7CaptureArm.Control, Functions());
        if (failure == "MISSING") {
            Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.Validate(null, raw.Receipt.Identity, f, s.Snapshot, s.Details)); return;
        }
        if (failure is "INVALID_LEDGER" or "FAILED") {
            var bad = failure == "INVALID_LEDGER" ? Raw(s, f, P7CaptureArm.Control, "{\"decisions\":[]}") :
                raw with { Receipt = raw.Receipt with { Status = "FAILED" } };
            Assert.ThrowsAny<Exception>(() => Validate(s, f, bad)); return;
        }
        var f1 = Validate(s, f, raw);
        if (failure == "WRONG_SOURCE") {
            Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream,
                P7CaptureArm.B, f1, s.Snapshot with { SourceSha256 = new string('c', 64) }, s.Details, pack)); return;
        }
        if (failure == "WRONG_ARM") {
            Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.NaturalEndToEnd,
                P7CaptureArm.B, f1, s.Snapshot, s.Details, pack)); return;
        }
        var gen = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.Control, f1, s.Snapshot, s.Details, pack)!;
        var wrong = Validate(s, gen.Request, Raw(s, gen.Request, P7CaptureArm.Control, Anchors("O1"), gen.Mode, new string('c', 64)));
        Assert.Throws<InvalidOperationException>(() => P7UpstreamCaptureReadiness.H2C(gen.Mode, P7CaptureArm.B, f1, wrong, s.Snapshot, s.Details, pack));
    }

    [Fact] public void Changed_raw_upstream_changes_dependency_even_when_decisions_and_body_match()
    {
        var s = Source(); var pack = Pack(s); var f = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, pack);
        var first = Validate(s, f, Raw(s, f, P7CaptureArm.Control, Functions()));
        var second = Validate(s, f, Raw(s, f, P7CaptureArm.Control, " \n" + Functions()));
        Assert.Equal(first.StageDecision.GetRawText(), second.StageDecision.GetRawText());
        Assert.NotEqual(first.CaptureSha256, second.CaptureSha256);
        var g1 = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, first, s.Snapshot, s.Details, pack)!;
        var g2 = P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B, second, s.Snapshot, s.Details, pack)!;
        Assert.Equal(Body(g1), Body(g2)); Assert.NotEqual(SpatialCanonical.Bytes(Binding(s, g1)), SpatialCanonical.Bytes(Binding(s, g2)));
        Assert.Equal(second.CaptureSha256, Assert.Single(Binding(s, g2).ParentCaptureSha256));
    }

    [Fact] public void Changed_F1_decision_changes_downstream_issued_subjects_without_Gold()
    {
        var s = Source(); var p = Pack(s); var f = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, p);
        P7GeneratedStage G(string primary) => P7UpstreamCaptureReadiness.G2A(P7ExperimentMode.ControlledDownstream, P7CaptureArm.B,
            Validate(s, f, Raw(s, f, P7CaptureArm.Control, Functions(primary))), s.Snapshot, s.Details, p)!;
        var g1 = G("O1"); var g2 = G("O2");
        Assert.Equal(new[] { "O1" }, g1.Request.DecisionSubjects); Assert.Equal(new[] { "O2" }, g2.Request.DecisionSubjects);
        Assert.NotEqual(Binding(s, g1).ProviderBodySha256, Binding(s, g2).ProviderBodySha256);
        Assert.NotEqual(Binding(s, g1).ParentCaptureSha256[0], Binding(s, g2).ParentCaptureSha256[0]);
    }

    [Fact] public void Same_provider_bytes_in_different_modes_do_not_share_manifest_or_metrics()
    {
        var s = Source(); var p = Pack(s); var f = P7PilotRequestPreflight.F1(s.Snapshot, s.Details, p);
        var f1 = Validate(s, f, Raw(s, f, P7CaptureArm.Control, Functions()));
        P7GeneratedStage G(P7ExperimentMode mode) => P7UpstreamCaptureReadiness.G2A(mode, P7CaptureArm.Control, f1, s.Snapshot, s.Details, p)!;
        var controlled = G(P7ExperimentMode.ControlledDownstream); var natural = G(P7ExperimentMode.NaturalEndToEnd);
        Assert.Equal(Body(controlled), Body(natural));
        Assert.NotEqual(SpatialCanonical.Bytes(Binding(s, controlled)), SpatialCanonical.Bytes(Binding(s, natural)));
        Assert.Equal("CONTROLLED_STAGE_ISOLATED", Binding(s, controlled).MetricNamespace);
        Assert.Equal("NATURAL_END_TO_END", Binding(s, natural).MetricNamespace);
    }

    [Fact] public void Generation_has_no_Gold_sidecar_or_fallback_parameter_and_no_file_reads()
    {
        foreach (var method in typeof(P7UpstreamCaptureReadiness).GetMethods().Where(m => m.Name is "G2A" or "H2C"))
            Assert.DoesNotContain(method.GetParameters(), p => new[] { "gold", "sidecar", "fallback", "alternative" }.Any(v => p.Name!.Contains(v, StringComparison.OrdinalIgnoreCase)));
        foreach (var file in new[] { "P7UpstreamCaptureReadiness.cs", "P7PilotRequestPreflight.cs" }) {
            var source = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.V5Qualification/P7/" + file));
            Assert.DoesNotContain("File.", source); Assert.DoesNotContain("P7PilotGoldReader", source);
            Assert.DoesNotContain("P7ApprovedPilotGold", source);
        }
    }

    [Theory] [InlineData("MISSING")] [InlineData("DUPLICATE")] [InlineData("UNKNOWN")]
    public void Offline_acceptance_cannot_pass_incomplete_duplicate_or_unknown_checks(string variant)
    {
        var checks = P7OfflineClosureQualification.RequiredChecks.ToList();
        if (variant == "MISSING") checks.RemoveAt(0);
        else checks[0] = variant == "DUPLICATE" ? checks[1] : "EXECUTION_FROZEN";
        Assert.Throws<InvalidOperationException>(() => P7OfflineClosureQualification.Qualify(checks));
    }

    [Fact] public void Offline_PASS_keeps_execution_usage_promotion_and_historical_adoption_closed()
    {
        var result = P7OfflineClosureQualification.Qualify(P7OfflineClosureQualification.RequiredChecks);
        Assert.Equal("PASS", result.D23OfflineFreeze);
        Assert.Equal("DEFERRED_UNTIL_EXECUTION", result.DownstreamBodyMaterialization);
        Assert.Equal("PENDING", result.ProviderUsageCalibration); Assert.Equal("BLOCKED", result.ExactTokenizerMapping);
        Assert.Equal("LOCKED", result.D3ProviderExecution); Assert.Equal("LOCKED", result.ProductionPromotion);
        Assert.Equal("NOT_MEASURED", result.SemanticAccuracy); Assert.False(result.HistoricalStatesRewritten);
        Assert.Equal("HISTORICAL_DIAGNOSTIC_NOT_ADOPTED", result.HistoricalCaptureStatus);
    }

    [Fact] public void Acceptance_V2_contract_matches_code_and_preserves_pinned_historical_state()
    {
        using var contract = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("scripts/P7OfflineClosureQualification/acceptance-contract.v2.json")));
        var c = contract.RootElement;
        Assert.Equal(P7OfflineClosureQualification.Version, c.GetProperty("version").GetString());
        Assert.Equal(P7OfflineClosureQualification.RequiredChecks, c.GetProperty("requiredChecks").EnumerateArray().Select(v => v.GetString()!));
        var prior = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.upstream-capture-readiness.v1.json"));
        Assert.Equal(c.GetProperty("historicalReadinessReceiptSha256").GetString(), SpatialCanonical.Hash(prior));
        using var historical = JsonDocument.Parse(prior);
        Assert.Equal(c.GetProperty("historicalReadinessStatus").GetString(), historical.RootElement.GetProperty("status").GetString());
        Assert.Equal(c.GetProperty("historicalD23").GetString(), historical.RootElement.GetProperty("d23").GetString());
        Assert.False(c.GetProperty("historicalStatesRewritten").GetBoolean());
    }
}
