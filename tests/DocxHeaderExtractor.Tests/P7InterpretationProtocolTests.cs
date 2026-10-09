using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7InterpretationProtocolTests
{
    [Theory]
    [InlineData("F1")]
    [InlineData("G2A")]
    [InlineData("H2C")]
    public void Separate_stage_decisions_use_existing_strict_parsers(string stageName)
    {
        var stage = Enum.Parse<InterpretationStage>(stageName);
        var source = Fixture();
        var request = Request(stage, source);
        var result = PdfInterpretationProtocol.Validate(Response(request).ToJsonString(), request, source.Snapshot, source.Details);
        Assert.Equal("VERIFIED_FACTS", result.EvidenceStatus);
        Assert.Equal("UNVERIFIABLE_ASSERTION", result.InterpretationStatus);
        Assert.Equal("NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW", result.SemanticStatus);
        Assert.Equal(request.DecisionSubjects.Count, result.Analysis.GetArrayLength());
        Assert.DoesNotContain("SAME_VISUAL_ROW", request.UserMessage);
        Assert.DoesNotContain("DISTINCT_HORIZONTAL_REGIONS", request.UserMessage);
    }

    [Fact]
    public void Raw_store_and_requests_are_deterministic_and_do_not_modify_source_or_D1()
    {
        var source = Fixture();
        var originalAtoms = JsonSerializer.Serialize(source.Snapshot.Atoms);
        var d1 = PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details).CanonicalBytes();
        var original = Request(InterpretationStage.G2A, source);
        var reversed = Request(InterpretationStage.G2A, source with
        {
            Snapshot = source.Snapshot with { Atoms = source.Snapshot.Atoms.Reverse().ToArray() },
            Details = source.Details with { Blocks = source.Details.Blocks.Reverse().ToArray() }
        });
        Assert.Equal(original.UserMessage, reversed.UserMessage);
        Assert.Equal(original.SystemPromptSha256, reversed.SystemPromptSha256);
        Assert.Equal(original.EvidenceStore.CanonicalBytes(), reversed.EvidenceStore.CanonicalBytes());
        Assert.Equal(originalAtoms, JsonSerializer.Serialize(source.Snapshot.Atoms));
        Assert.Equal(d1, PdfSpatialEvidenceLedgerBuilder.Build(source.Snapshot, source.Details).CanonicalBytes());
        Assert.Throws<NotSupportedException>(() => ((IList<SourceEvidenceEntry>)original.EvidenceStore.Entries).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SourceEvidenceField>)original.EvidenceStore.Entries[0].Fields).Clear());
    }

    [Fact]
    public void Control_projection_is_exactly_the_existing_protocol_request_not_an_approximation()
    {
        var source = Fixture();
        var owned = source.Snapshot.Atoms;
        var f1 = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, [],
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>());
        var request = Request(InterpretationStage.F1, source);
        Assert.Equal(f1.UserMessage, request.ControlUserMessage);
        Assert.Equal(f1.SystemPrompt, request.ControlSystemPrompt);
        var ids = f1.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id);
        var g2a = Request(InterpretationStage.G2A, source);
        Assert.Equal(HeadingAnchorProtocolV1.ComposeUserMessage(owned, ids, [("O2", owned[1])]), g2a.ControlUserMessage);
        Assert.Equal(HeadingAnchorProtocolV1.SystemPrompt, g2a.ControlSystemPrompt);
        var h2c = Request(InterpretationStage.H2C, source);
        Assert.Equal(HeadingExtentProtocolV2.ComposeUserMessage("O1", owned.Select(atom => atom.Alias).ToArray(), ids,
            owned.ToDictionary(atom => atom.Alias), source.Snapshot.Evidence.ToDictionary(value => value.SourceAlias)), h2c.ControlUserMessage);
        Assert.Equal(HeadingExtentProtocolV2.SystemPrompt, h2c.ControlSystemPrompt);
    }

    [Theory]
    [InlineData("unknown", "interpretation-reference-source-mismatch")]
    [InlineData("alias", "interpretation-reference-source-mismatch")]
    [InlineData("outside", "interpretation-reference-source-mismatch")]
    [InlineData("field", "interpretation-field-not-available")]
    [InlineData("missingGlyphs", "interpretation-field-not-available")]
    [InlineData("value", "interpretation-assertion-value-mismatch")]
    [InlineData("unreferenced", "interpretation-assertion-unreferenced-or-duplicate")]
    [InlineData("duplicateField", "interpretation-field-duplicate")]
    [InlineData("duplicateReference", "interpretation-reference-duplicate")]
    [InlineData("identity", "interpretation-response-identity-mismatch")]
    [InlineData("stage", "interpretation-response-identity-mismatch")]
    [InlineData("extra", "interpretation-schema-invalid")]
    [InlineData("explanation", "interpretation-explanation-length")]
    public void Invented_misbound_missing_or_falsified_evidence_fails_closed(string corruption, string error)
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        var response = Response(request);
        var row = response["analysis"]![0]!;
        var reference = row["references"]![0]!;
        var assertion = row["assertions"]![0]!;
        switch (corruption)
        {
            case "unknown": reference["occurrence"] = "O999"; break;
            case "alias": reference["sourceAlias"] = "invented"; break;
            case "outside": reference["occurrence"] = "O4"; reference["sourceAlias"] = source.Snapshot.Atoms[3].Alias; break;
            case "field": reference["fields"]![0] = "sameHeading"; break;
            case "missingGlyphs": reference["fields"]![0] = "glyphs"; break;
            case "value": assertion["value"] = 99; break;
            case "unreferenced": assertion["field"] = "bbox"; break;
            case "duplicateField": ((JsonArray)reference["fields"]!).Add("page"); break;
            case "duplicateReference": ((JsonArray)row["references"]!).Add(reference.DeepClone()); break;
            case "identity": response["sourceSha256"] = new string('b', 64); break;
            case "stage": response["stage"] = "F1"; break;
            case "extra": response["confidence"] = 1; break;
            case "explanation": row["interpretation"] = new string('a', 1025); break;
        }
        Assert.Equal(error, Assert.ThrowsAny<InvalidOperationException>(() =>
            PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details)).Message);
    }

    [Fact]
    public void Missing_bbox_is_unavailable_not_a_false_observation_and_no_font_height_fallback()
    {
        var source = Fixture();
        var details = source.Details with { Blocks = source.Details.Blocks.Select(block => block with
        { Lines = block.Lines.Select(line => line with { Top = null, Bottom = null }).ToArray() }).ToArray() };
        var request = Request(InterpretationStage.G2A, source with { Details = details });
        var bbox = request.EvidenceStore.Entries[1].Fields.Single(field => field.Name == "bbox");
        Assert.Equal("NOT_AVAILABLE", bbox.Availability);
        Assert.Null(bbox.Value);
        var response = Response(request);
        response["analysis"]![0]!["references"]![0]!["fields"]![0] = "bbox";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, details));
    }

    [Fact]
    public void Evidence_validity_does_not_certify_arbitrary_semantic_interpretation()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        var response = Response(request);
        response["analysis"]![0]!["interpretation"] = "These supplied rows are definitely a heading because I say so.";
        var result = PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details);
        Assert.Equal("NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW", result.SemanticStatus);
        Assert.Equal("NO_STRUCTURAL_EXTENT", result.StageDecision.GetProperty("decisions")[0].GetProperty("anchor").GetString());
    }

    [Fact]
    public void Unicode_text_assertion_can_copy_a_referenced_field_but_cannot_rewrite_it()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        var response = Response(request);
        var row = response["analysis"]![0]!;
        row["references"]![0]!["fields"]![0] = "text";
        row["assertions"]![0]!["field"] = "text";
        row["assertions"]![0]!["value"] = source.Snapshot.Atoms[1].Text;
        PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details);
        row["assertions"]![0]!["value"] = "rewritten source";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details));
        Assert.Contains("Exact copied text field values are permitted inside assertions", request.SystemPrompt);
    }

    [Fact]
    public void Missing_duplicate_or_context_only_analysis_subjects_cannot_complete_the_ledger()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.F1, source);
        var response = Response(request);
        response["analysis"]![1]!["subject"] = "O1";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details));
        response = Response(request);
        ((JsonArray)response["analysis"]!).RemoveAt(0);
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(response.ToJsonString(), request, source.Snapshot, source.Details));
        var anchor = Request(InterpretationStage.G2A, source);
        response = Response(anchor);
        response["analysis"]![0]!["subject"] = "O1";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(response.ToJsonString(), anchor, source.Snapshot, source.Details));
    }

    [Fact]
    public void Stage_dimension_leakage_and_invalid_boundary_are_rejected_even_with_real_references()
    {
        var source = Fixture();
        var g2a = Request(InterpretationStage.G2A, source);
        var invalid = Response(g2a);
        invalid["stageDecision"]!["decisions"]![0]!["headingMembers"] = new JsonArray("O2");
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(invalid.ToJsonString(), g2a, source.Snapshot, source.Details));
        var h2c = Request(InterpretationStage.H2C, source);
        invalid = Response(h2c);
        invalid["stageDecision"]!["decisions"]![0]!["firstOutsideOccurrence"] = "O3";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(invalid.ToJsonString(), h2c, source.Snapshot, source.Details));
        invalid = Response(h2c);
        invalid["stageDecision"]!["decisions"]![0]!["firstOutsideRole"] = "UNREGISTERED_ROLE";
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(invalid.ToJsonString(), h2c, source.Snapshot, source.Details));
    }

    [Fact]
    public void Duplicate_JSON_keys_byte_overflow_and_stale_parser_facts_are_rejected()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        var raw = Response(request).ToJsonString();
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(
            raw.Insert(1, "\"stage\":\"G2A\","), request, source.Snapshot, source.Details));
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Validate(
            new string(' ', PdfInterpretationProtocol.ResponseUtf8ByteCap) + raw, request, source.Snapshot, source.Details));
        var changed = source.Details with { Blocks = source.Details.Blocks.Select(block => block with
        { Lines = block.Lines.Select(line => line with { Top = line.Top + 1 }).ToArray() }).ToArray() };
        Assert.Equal("interpretation-store-source-mismatch", Assert.ThrowsAny<InvalidOperationException>(() =>
            PdfInterpretationProtocol.Validate(raw, request, source.Snapshot, changed)).Message);
    }

    [Fact]
    public void Scope_and_anchor_copy_are_explicit_terminal_contract_is_preserved()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        Assert.Equal(["O1", "O2", "O3"], request.VisibleAliasByOccurrence.Keys);
        Assert.Equal(["O2"], request.DecisionSubjects);
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Compose(InterpretationStage.F1,
            source.Snapshot, source.Details, source.Snapshot.Atoms, ["O2"]));
        Assert.ThrowsAny<InvalidOperationException>(() => PdfInterpretationProtocol.Compose(InterpretationStage.G2A,
            source.Snapshot, source.Details, source.Snapshot.Atoms, ["O2", "O2"]));
        var terminal = PdfInterpretationProtocol.Compose(InterpretationStage.H2C, source.Snapshot, source.Details,
            source.Snapshot.Atoms, anchor: "O4");
        var response = Response(terminal);
        response["stageDecision"] = JsonNode.Parse("{\"decisions\":[{\"anchor\":\"O4\",\"headingMembers\":[\"O4\"],\"endOccurrence\":\"O4\",\"firstOutsideOccurrence\":null,\"firstOutsideRole\":\"NO_VISIBLE_SUCCESSOR\"}]}");
        PdfInterpretationProtocol.Validate(response.ToJsonString(), terminal, source.Snapshot, source.Details);
    }

    private static InterpretationRequest Request(InterpretationStage stage, PdfSourceBuildResult source) =>
        PdfInterpretationProtocol.Compose(stage, source.Snapshot, source.Details, source.Snapshot.Atoms,
            primaryIds: stage == InterpretationStage.G2A ? ["O2"] : null,
            anchor: stage == InterpretationStage.H2C ? "O1" : null);

    [Fact]
    public void Assessment_separates_physical_fact_verification_from_unverifiable_interpretation()
    {
        var source = Fixture();
        var request = Request(InterpretationStage.G2A, source);
        var response = Response(request);
        var report = InterpretationEvidenceAssessor.Assess(response.ToJsonString(), request, source.Snapshot, source.Details);
        Assert.Equal(EvidenceAssessmentStatus.VERIFIED_FACTS, report.PhysicalEvidenceStatus);
        Assert.Equal(EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION, report.InterpretationStatus);
        Assert.Equal("NOT_EVALUATED", report.SemanticDecisionStatus);
        response["analysis"]![0]!["references"]![0]!["occurrence"] = "invented";
        report = InterpretationEvidenceAssessor.Assess(response.ToJsonString(), request, source.Snapshot, source.Details);
        Assert.Equal("REJECTED", report.ContractStatus);
        Assert.Equal(EvidenceAssessmentStatus.INVALID_REFERENCE, report.PhysicalEvidenceStatus);
        response = Response(request);
        response["analysis"]![0]!["references"]![0]!["fields"]![0] = "glyphs";
        report = InterpretationEvidenceAssessor.Assess(response.ToJsonString(), request, source.Snapshot, source.Details);
        Assert.Equal("REJECTED", report.ContractStatus);
        Assert.Equal(EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION, report.PhysicalEvidenceStatus);
        report = InterpretationEvidenceAssessor.Assess("{}", request, source.Snapshot, source.Details);
        Assert.Null(report.PhysicalEvidenceStatus); // invalid protocol is a separate dimension
    }

    [Fact]
    public void F1_OTHER_does_not_prune_a_valid_H2C_extent_or_turn_disagreement_into_authority()
    {
        var source = Fixture();
        var functionRequest = Request(InterpretationStage.F1, source);
        var functions = PdfInterpretationProtocol.Validate(Response(functionRequest).ToJsonString(), functionRequest, source.Snapshot, source.Details);
        Assert.All(functions.StageDecision.GetProperty("decisions").EnumerateArray(), row => Assert.Equal("OTHER", row.GetProperty("function").GetString()));
        var request = Request(InterpretationStage.H2C, source);
        var response = Response(request);
        var decision = response["stageDecision"]!["decisions"]![0]!;
        decision["headingMembers"] = new JsonArray("O1", "O2");
        decision["endOccurrence"] = "O2";
        decision["firstOutsideOccurrence"] = "O3";
        response["analysis"]![0]!["interpretation"] = "The second occurrence appears to continue the literal heading, despite a separate upstream function judgment.";
        var raw = response.ToJsonString();
        var result = PdfInterpretationProtocol.Validate(raw, request, source.Snapshot, source.Details);
        Assert.Equal(["O1", "O2"], result.StageDecision.GetProperty("decisions")[0].GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(raw, response.ToJsonString());
        Assert.Equal("NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW", result.SemanticStatus);
    }

    private static JsonObject Response(InterpretationRequest request)
    {
        object payload = request.Stage switch
        {
            InterpretationStage.F1 => new { decisions = request.DecisionSubjects.Select(id => new { occurrence = id, function = "OTHER" }).ToArray() },
            InterpretationStage.G2A => new { decisions = request.DecisionSubjects.Select(id => new { primary = id, anchor = "NO_STRUCTURAL_EXTENT" }).ToArray() },
            _ => new { decisions = new[] { new { anchor = request.DecisionSubjects[0], headingMembers = new[] { request.DecisionSubjects[0] },
                endOccurrence = request.DecisionSubjects[0], firstOutsideOccurrence = "O2", firstOutsideRole = "BODY_CONTENT" } } }
        };
        var rows = request.DecisionSubjects.Select(id =>
        {
            var alias = request.VisibleAliasByOccurrence[id];
            var page = request.EvidenceStore.Entries.Single(entry => entry.SourceAlias == alias).Fields.Single(field => field.Name == "page");
            return new { subject = id, references = new[] { new { occurrence = id, sourceAlias = alias, fields = new[] { "page" } } },
                assertions = new[] { new { occurrence = id, sourceAlias = alias, field = "page", value = page.Value } },
                interpretation = "Decision explanation requiring independent semantic review." };
        }).ToArray();
        return JsonNode.Parse(JsonSerializer.Serialize(new { protocolVersion = PdfInterpretationProtocol.Version,
            stage = PdfInterpretationProtocol.Name(request.Stage), sourceSha256 = request.EvidenceStore.SourceSha256,
            evidenceStoreSha256 = request.EvidenceStore.StoreSha256, stageDecision = payload, analysis = rows }))!.AsObject();
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("G2A")]
    [InlineData("H2C")]
    public void Evidence_only_A_and_interpretation_B_share_exact_source_projection_and_semantic_rules(string stageName)
    {
        var source = Fixture();
        var b = Request(Enum.Parse<InterpretationStage>(stageName), source);
        var a = PdfEvidenceOnlyProtocol.Compose(b);
        using var aUser = JsonDocument.Parse(a.UserMessage);
        using var bUser = JsonDocument.Parse(b.UserMessage);
        foreach (var field in new[] { "sourceEvidence", "stageInput", "decisionSubjects", "sourceSha256", "sourceAliasUniverseSha256", "evidenceStoreSha256" })
            Assert.Equal(bUser.RootElement.GetProperty(field).GetRawText(), aUser.RootElement.GetProperty(field).GetRawText());
        var commonPrefix = b.SystemPrompt[..b.SystemPrompt.IndexOf("This is a versioned qualification interpretation treatment", StringComparison.Ordinal)];
        Assert.StartsWith(commonPrefix, a.SystemPrompt);
        var response = Response(b);
        response["protocolVersion"] = PdfEvidenceOnlyProtocol.Version;
        response.Remove("analysis");
        var raw = response.ToJsonString();
        PdfEvidenceOnlyProtocol.Validate(raw, a, source.Snapshot, source.Details);
        response["analysis"] = new JsonArray();
        Assert.ThrowsAny<InvalidOperationException>(() => PdfEvidenceOnlyProtocol.Validate(response.ToJsonString(), a, source.Snapshot, source.Details));
        Assert.ThrowsAny<InvalidOperationException>(() => PdfEvidenceOnlyProtocol.Validate(raw.Insert(1, "\"stage\":\"F1\","), a, source.Snapshot, source.Details));
    }

    private static PdfSourceBuildResult Fixture() => PdfSourceAdapter.BuildWithDetails([
        Line(0, "Heading"), Line(20, "Vietnamese Unicode: Giấy mời <&>"), Line(40, "Body"), Line(60, "Next")], new string('a', 64));
    private static PdfLine Line(double left, string text) => new(1, 15, 12, text, 1, "", 0,
        left, left + 10, "Times-Bold", "", Bottom: 10, Top: 20);
}
