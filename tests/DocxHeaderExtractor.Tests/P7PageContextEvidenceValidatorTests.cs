using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PageContextEvidenceValidatorTests
{
    private sealed record Case(PdfSourceBuildResult Source, InterpretationRequest Request,
        PdfPageEvidenceContext Context, string[] Subjects, PageContextPolicy Policy);
    private static Case Fixture(InterpretationStage stage = InterpretationStage.H2C, bool missing = false)
    {
        PdfLine Line(int page, double y, string text) => new(page, y, 12, text, 0, "", 0, 10, 90,
            "Times", "", Bottom: y - 10, Top: y);
        var anchor = Line(1, 650, "Anchor");
        var source = PdfSourceAdapter.BuildWithDetails([Line(1, 700, "Prefix"),
            missing ? anchor with { Bottom = null, Top = null } : anchor,
            Line(1, 610, "Outside"), Line(2, 700, "Adjacent")], new string('a', 64));
        var owned = source.Snapshot.Atoms.Where(x => x.Page == 1).ToArray();
        var request = PdfInterpretationProtocol.Compose(stage, source.Snapshot, source.Details, owned,
            primaryIds: stage == InterpretationStage.G2A ? ["O2"] : null,
            anchor: stage == InterpretationStage.H2C ? "O2" : null);
        var subjects = request.DecisionSubjects.Select(id => request.Owned.Single(x => x.Id == id).Atom.Alias).ToArray();
        var policy = new PageContextPolicy(PageContextScope.SubjectAndAdjacentPages, AdjacentPageRadius: 1);
        return new(source, request, PdfPageEvidenceContextBuilder.Build(request.EvidenceStore, subjects, policy), subjects, policy);
    }
    private static JsonObject Response(Case c, string? alias = null, string field = "bbox")
    {
        alias ??= c.Source.Snapshot.Atoms.Single(x => x.Text == "Prefix").Alias;
        var entry = c.Request.EvidenceStore.Entries.Single(x => x.SourceAlias == alias);
        var value = entry.Fields.Single(x => x.Name == field).Value;
        var decisions = c.Request.Stage switch
        {
            InterpretationStage.F1 => new JsonObject { ["decisions"] = new JsonArray(c.Request.DecisionSubjects.Select(id =>
                (JsonNode)new JsonObject { ["occurrence"] = id, ["function"] = "OTHER" }).ToArray()) },
            InterpretationStage.G2A => JsonNode.Parse("{\"decisions\":[{\"primary\":\"O2\",\"anchor\":\"HAS_STRUCTURAL_EXTENT\"}]}")!,
            _ => JsonNode.Parse("{\"decisions\":[{\"anchor\":\"O2\",\"headingMembers\":[\"O2\"],\"endOccurrence\":\"O2\",\"firstOutsideOccurrence\":\"O3\",\"firstOutsideRole\":\"BODY_CONTENT\"}]}")!
        };
        return new JsonObject
        {
            ["sourceSha256"] = c.Request.EvidenceStore.SourceSha256,
            ["evidenceStoreSha256"] = c.Request.EvidenceStore.StoreSha256,
            ["contextSha256"] = c.Context.ContextSha256, ["stageDecision"] = decisions,
            ["analysis"] = new JsonArray(c.Request.DecisionSubjects.Select(id => (JsonNode)new JsonObject
            {
                ["subject"] = id,
                ["references"] = new JsonArray(new JsonObject
                {
                    ["sourceAlias"] = alias, ["sourceIdSha256"] = entry.SourceIdSha256,
                    ["page"] = entry.Fields.Single(x => x.Name == "page").Value!.Value.GetInt32(),
                    ["spanStart"] = entry.SpanStart, ["spanEnd"] = entry.SpanEnd,
                    ["fields"] = new JsonArray(JsonValue.Create(field))
                }),
                ["assertions"] = new JsonArray(new JsonObject { ["sourceAlias"] = alias, ["field"] = field,
                    ["value"] = value is null ? null : JsonNode.Parse(value.Value.GetRawText()) }),
                ["interpretation"] = "These observations suggest a semantic relation; this claim remains unverified."
            }).ToArray())
        };
    }
    private static PageContextEvidenceValidationResult Validate(Case c, JsonObject response) =>
        PdfPageContextEvidenceValidator.Validate(response.ToJsonString(), c.Request, c.Context, c.Subjects, c.Policy,
            c.Source.Snapshot, c.Source.Details);
    private static JsonNode Reference(JsonObject response) => response["analysis"]![0]!["references"]![0]!;
    private static JsonNode Assertion(JsonObject response) => response["analysis"]![0]!["assertions"]![0]!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Expanded_prefix_and_adjacent_references_are_facts_not_semantic_verification(int stageValue)
    {
        var stage = (InterpretationStage)stageValue;
        var c = Fixture(stage); var before = c.Context.CanonicalBytes(); var requestHash = c.Request.UserMessageSha256;
        foreach (var text in new[] { "Prefix", "Adjacent" })
        {
            var alias = c.Source.Snapshot.Atoms.Single(x => x.Text == text).Alias;
            var result = Validate(c, Response(c, alias));
            Assert.True(result.Accepted); Assert.All(result.Facts, x => Assert.Equal(PageContextFactStatus.FACT_VERIFIED, x.Status));
            Assert.Equal("UNVERIFIABLE_ASSERTION", result.InterpretationStatus);
            Assert.Equal("NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW", result.SemanticStatus);
        }
        Assert.Equal(before, c.Context.CanonicalBytes()); Assert.Equal(requestHash, c.Request.UserMessageSha256);
    }

    [Theory]
    [InlineData("sourceSha256")]
    [InlineData("evidenceStoreSha256")]
    [InlineData("contextSha256")]
    public void Stale_or_cross_snapshot_identity_is_contradicted(string property)
    {
        var c = Fixture(); var response = Response(c); response[property] = new string('f', 64);
        var result = Validate(c, response); Assert.False(result.Accepted);
        Assert.Equal(PageContextFactStatus.FACT_CONTRADICTED, Assert.Single(result.Facts).Status);
    }

    [Theory]
    [InlineData("sourceAlias")]
    [InlineData("sourceIdSha256")]
    [InlineData("page")]
    [InlineData("spanStart")]
    [InlineData("spanEnd")]
    public void Reference_identity_page_and_exact_span_cannot_be_forged(string property)
    {
        var c = Fixture(); var response = Response(c);
        Reference(response)[property] = property is "sourceAlias" or "sourceIdSha256" ? JsonValue.Create("foreign") : JsonValue.Create(999);
        var result = Validate(c, response); Assert.False(result.Accepted);
        Assert.Contains(result.Facts, x => x.Status == PageContextFactStatus.FACT_CONTRADICTED);
    }

    [Fact]
    public void Modified_geometry_and_contradictory_copies_fail_closed()
    {
        var c = Fixture(); var response = Response(c); Assertion(response)["value"]!["left"] = -999;
        var result = Validate(c, response); Assert.False(result.Accepted);
        Assert.Contains(result.Facts, x => x.Code == "copied-value-contradicted");
        // A second conflicting copy is rejected, not resolved by first/last wins.
        response["analysis"]![0]!["assertions"]!.AsArray().Add(Assertion(response).DeepClone());
        Assert.Throws<InvalidOperationException>(() => Validate(c, response));
    }

    [Fact]
    public void Unavailable_geometry_is_not_false_or_verified()
    {
        var c = Fixture(missing: true); var alias = c.Source.Snapshot.Atoms.Single(x => x.Text == "Anchor").Alias;
        var result = Validate(c, Response(c, alias)); Assert.False(result.Accepted);
        Assert.All(result.Facts, x => Assert.Equal(PageContextFactStatus.FACT_NOT_VERIFIABLE, x.Status));
    }

    [Fact]
    public void Missing_glyphs_unknown_fields_and_semantic_predicates_remain_unverified_or_contradicted()
    {
        var c = Fixture(); var result = Validate(c, Response(c, field: "glyphs"));
        Assert.False(result.Accepted); Assert.All(result.Facts, x => Assert.Equal(PageContextFactStatus.FACT_NOT_VERIFIABLE, x.Status));
        var response = Response(c); Reference(response)["fields"]![0] = "SAME_HEADING";
        Assertion(response)["field"] = "SAME_HEADING"; Assertion(response)["value"] = true;
        result = Validate(c, response); Assert.False(result.Accepted);
        Assert.Contains(result.Facts, x => x.Status == PageContextFactStatus.FACT_CONTRADICTED);
    }

    [Theory]
    [InlineData("headingMembers", "O1")]
    [InlineData("endOccurrence", "O1")]
    [InlineData("firstOutsideOccurrence", "O1")]
    [InlineData("headingMembers", "O4")]
    [InlineData("endOccurrence", "O4")]
    [InlineData("firstOutsideOccurrence", "O4")]
    public void Prefix_and_adjacent_context_can_never_expand_h2_decision_universe(string property, string id)
    {
        var c = Fixture(); var response = Response(c); var decision = response["stageDecision"]!["decisions"]![0]!;
        decision[property] = property == "headingMembers" ? new JsonArray(JsonValue.Create(id)) : JsonValue.Create(id);
        Assert.ThrowsAny<Exception>(() => Validate(c, response));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Other_stage_decisions_cannot_select_context_only_ids(int stageValue)
    {
        var stage = (InterpretationStage)stageValue;
        var c = Fixture(stage); var response = Response(c);
        response["stageDecision"]!["decisions"]![0]![stage == InterpretationStage.F1 ? "occurrence" : "primary"] = "O4";
        Assert.ThrowsAny<Exception>(() => Validate(c, response));
    }

    [Fact]
    public void Forged_context_and_original_scope_are_rejected_before_decisions()
    {
        var c = Fixture(); var response = Response(c);
        var forged = PdfPageEvidenceContextBuilder.Build(c.Request.EvidenceStore, c.Subjects, new(PageContextScope.SubjectPages));
        Assert.Throws<InvalidOperationException>(() => Validate(c with { Context = forged }, response));
        var map = c.Request.VisibleAliasByOccurrence.ToDictionary(x => x.Key, x => x.Value);
        map.Add("O1", c.Request.Owned[0].Atom.Alias);
        Assert.Throws<InvalidOperationException>(() => Validate(c with { Request = c.Request with { VisibleAliasByOccurrence = map } }, response));
        var firstPage = c.Context.Pages[0]; var observations = firstPage.Observations.ToArray();
        observations[0] = observations[0] with { Selectable = true };
        var pages = c.Context.Pages.ToArray(); pages[0] = firstPage with { Observations = observations };
        var tampered = new PdfPageEvidenceContext(c.Request.EvidenceStore, c.Policy, c.Subjects, [], pages);
        Assert.Throws<InvalidOperationException>(() => Validate(c with { Context = tampered }, response));
    }

    [Fact]
    public void Strict_schema_duplicate_properties_and_response_cap_reject()
    {
        var c = Fixture(); var response = Response(c); response["confidence"] = 1;
        Assert.Throws<InvalidOperationException>(() => Validate(c, response));
        var raw = Response(c).ToJsonString(); var duplicate = raw.Insert(1, "\"contextSha256\":\"fake\",");
        Assert.Throws<InvalidOperationException>(() => PdfPageContextEvidenceValidator.Validate(duplicate, c.Request, c.Context,
            c.Subjects, c.Policy, c.Source.Snapshot, c.Source.Details));
        Assert.Throws<InvalidOperationException>(() => PdfPageContextEvidenceValidator.Validate(new string('x', 262_145),
            c.Request, c.Context, c.Subjects, c.Policy, c.Source.Snapshot, c.Source.Details));
    }

    [Fact]
    public void Cross_document_store_and_mutated_context_measurement_fail_before_analysis()
    {
        var c = Fixture(); var response = Response(c);
        var differentDocument = PdfSourceAdapter.BuildWithDetails(c.Source.Details.Blocks.SelectMany(x => x.Lines).ToArray(), new string('f', 64));
        var foreignStore = PdfSourceEvidenceStore.Build(differentDocument.Snapshot, differentDocument.Details);
        Assert.Throws<InvalidOperationException>(() => Validate(c with { Request = c.Request with { EvidenceStore = foreignStore } }, response));
        var pages = c.Context.Pages.ToArray(); var observations = pages[0].Observations.ToArray();
        var fields = observations[0].Fields.ToArray(); var index = Array.FindIndex(fields, x => x.Name == "bbox");
        var bbox = JsonNode.Parse(fields[index].Value!.Value.GetRawText())!; bbox["left"] = -999;
        fields[index] = fields[index] with { Value = System.Text.Json.JsonSerializer.SerializeToElement(bbox) };
        observations[0] = observations[0] with { Fields = fields };
        pages[0] = pages[0] with { Observations = observations };
        var forged = new PdfPageEvidenceContext(c.Request.EvidenceStore, c.Policy, c.Subjects, [], pages);
        Assert.Throws<InvalidOperationException>(() => Validate(c with { Context = forged }, response));
    }

    [Fact]
    public void Repeated_validation_is_deterministic_and_no_f1_label_veto_is_added()
    {
        var c = Fixture(); var response = Response(c);
        // No F1 decisions are consumed by this verifier: a valid H2-C extent remains intact.
        var first = Validate(c, response); var second = Validate(c, response);
        Assert.Equal(first.StageDecision.GetRawText(), second.StageDecision.GetRawText());
        Assert.Equal(first.Facts, second.Facts);
        Assert.Equal("O2", first.StageDecision.GetProperty("decisions")[0].GetProperty("endOccurrence").GetString());
    }
}
