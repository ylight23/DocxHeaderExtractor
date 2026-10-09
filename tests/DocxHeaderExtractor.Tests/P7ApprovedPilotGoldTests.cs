using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7ApprovedPilotGoldTests
{
    // Invented text, including a surrogate pair: no source excerpt or model response fixture.
    private static readonly PilotSourceAtom[] Source = [new("A", "id-a", 0, 1, "Tiêu đề 😀"),
        new("B", "id-b", 1, 1, "Continuation"), new("C", "id-c", 2, 1, "Body"), new("D", "id-d", 3, 2, "Unknown")];
    private static readonly EvaluationScope Scope = new(P7EvaluationUniverse.Version, "pdf", "universe", "snapshot", [1], P7EvaluationUniverse.CrossingPolicy);
    private static PilotUserApproval Approval => new(true, true, "proposal-hash", []);
    private static JsonElement Proposal() => JsonSerializer.SerializeToElement(new
    {
        status = "PROPOSAL_NOT_APPROVED_GOLD", modelPredictionsUsed = false,
        proposedUnits = new[] { new { document = "DOC", unitId = "H1", primaryAlias = "A", primaryOrdinal = 0,
            parts = Source.Take(2).Select(a => new { alias = a.Alias, sourceId = a.SourceId, ordinal = a.Ordinal, text = a.Text }),
            reviewedFirstOutside = new { alias = "C", sourceId = "id-c", ordinal = 2, page = 1 }, documentEndAttested = false } },
        worksheetRows = Source.Take(3).Select(a => new { document = "DOC", alias = a.Alias, sourceId = a.SourceId,
            ordinal = a.Ordinal, page = a.Page, textForReview = a.Text, semanticFunction = a.Alias == "C" ? "OTHER" : "ESTABLISHES_STRUCTURE",
            headingMembership = a.Alias != "C", isDistinctAnchor = a.Alias == "A", unitId = a.Alias == "C" ? null : "H1", unitRole = "STRUCTURAL_HEADING" })
    });
    private static PilotApprovedDocument Build(JsonElement? proposal = null, PilotUserApproval? approval = null) =>
        P7ApprovedPilotGold.Build(proposal ?? Proposal(), "proposal-hash", approval ?? Approval, "DOC", Scope, Source);
    private static JsonElement Edit(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(Proposal().GetRawText())!.AsObject(); edit(json);
        return JsonSerializer.SerializeToElement(json);
    }

    [Fact] public void Full_atom_spans_use_utf16_not_codepoints_or_utf8()
    {
        var gold = Build(); var span = gold.Units[0].Parts[0];
        Assert.Equal(0, span.Start); Assert.Equal(Source[0].Text.Length, span.Length);
        Assert.Equal(Source[0].Text.EnumerateRunes().Count() + 1, span.Length);
        Assert.Equal("DOTNET_UTF16_CODE_UNITS", P7ApprovedPilotGold.OffsetConvention);
    }
    [Fact] public void All_selected_rows_adjudicated_and_outside_truth_stays_null()
    {
        var gold = Build(); Assert.Equal(3, gold.Annotations.Count(a => a.Status == "ADJUDICATED"));
        var outside = gold.Annotations.Single(a => a.Alias == "D"); Assert.Equal("OUT_OF_EVALUATION_SCOPE", outside.Status);
        Assert.Null(outside.SemanticFunction); Assert.Null(outside.HeadingMembership); Assert.Null(outside.ExtentParts);
        Assert.Equal(2, gold.ReviewedRows.Count(r => r.HeadingMembership)); Assert.Single(gold.ReviewedRows, r => r.IsDistinctAnchor);
    }
    [Theory] [InlineData(false, true)] [InlineData(true, false)]
    public void Missing_explicit_approval_never_creates_gold(bool user, bool scope) =>
        Assert.Throws<InvalidOperationException>(() => Build(approval: Approval with { UserApproved = user, ScopeApproved = scope }));
    [Fact] public void Wrong_proposal_hash_blocks_gold() =>
        Assert.Throws<InvalidOperationException>(() => Build(approval: Approval with { ProposalSha256 = "other" }));
    [Fact] public void Full_scope_partition_is_required() =>
        Assert.Throws<InvalidOperationException>(() => Build(Edit(j => j["worksheetRows"]!.AsArray().RemoveAt(2))));
    [Fact] public void No_source_normalization_is_allowed() =>
        Assert.Throws<InvalidOperationException>(() => Build(Edit(j => j["worksheetRows"]![0]!["textForReview"] = "Tiêu đề")));
    [Fact] public void Forged_part_source_identity_fails() =>
        Assert.Throws<InvalidOperationException>(() => Build(Edit(j => j["proposedUnits"]![0]!["parts"]![0]!["sourceId"] = "foreign-document")));
    [Fact] public void Exit_must_be_the_reviewed_immediate_successor() =>
        Assert.Throws<InvalidOperationException>(() => Build(Edit(j => j["proposedUnits"]![0]!["reviewedFirstOutside"]!["alias"] = "D")));
    [Fact] public void Pending_row_requires_user_policy_resolution()
    {
        var proposal = Edit(j => { var r = j["worksheetRows"]![2]!; r["semanticFunction"] = null; r["headingMembership"] = null; r["isDistinctAnchor"] = null; });
        Assert.Throws<InvalidOperationException>(() => Build(proposal));
        var gold = Build(proposal, Approval with { Resolutions = [new("DOC", "C", "OTHER", false, false)] });
        Assert.Equal("OTHER", gold.ReviewedRows.Single(r => r.Alias == "C").SemanticFunction);
    }
    [Fact] public void Foreign_resolution_cannot_resolve_local_pending_row()
    {
        var proposal = Edit(j => j["worksheetRows"]![2]!["semanticFunction"] = null);
        Assert.Throws<InvalidOperationException>(() => Build(proposal, Approval with { Resolutions = [new("OTHER-DOC", "C", "OTHER", false, false)] }));
    }
    [Fact] public void Overlap_is_not_allowed_in_this_approved_gold_partition() =>
        Assert.Throws<InvalidOperationException>(() => Build(Edit(j => j["proposedUnits"]!.AsArray().Add(j["proposedUnits"]![0]!.DeepClone()))));
    [Fact] public void Build_is_deterministic_and_does_not_mutate_proposal()
    {
        var p = Proposal(); var before = p.GetRawText();
        Assert.Equal(JsonSerializer.Serialize(Build(p)), JsonSerializer.Serialize(Build(p)));
        Assert.Equal(before, p.GetRawText());
    }
    [Fact] public void Semantic_labels_do_not_implicitly_cut_extent()
    {
        var gold = Build(Edit(j => j["worksheetRows"]![1]!["semanticFunction"] = "OTHER"));
        Assert.Equal(2, gold.Units[0].Parts.Count); Assert.True(gold.ReviewedRows.Single(r => r.Alias == "B").HeadingMembership);
    }
    [Fact] public void Reviewer_role_is_optional_not_a_gold_contract_field()
    {
        var p = Edit(j => { foreach (var row in j["worksheetRows"]!.AsArray()) row!.AsObject().Remove("unitRole"); });
        var gold = Build(p);
        Assert.Equal(P7ApprovedPilotGold.ScoringProjection(Build()), P7ApprovedPilotGold.ScoringProjection(gold));
        Assert.All(gold.ReviewerInterpretations, r => Assert.Null(r.ProposalRoleNote));
        using var json = JsonDocument.Parse(P7ApprovedPilotGold.ScoringProjection(gold));
        Assert.All(json.RootElement.GetProperty("reviewedRows").EnumerateArray(), r => Assert.False(r.TryGetProperty("unitRole", out _)));
    }
    [Fact] public void Arbitrary_reviewer_vocabulary_and_alternatives_cannot_change_gold_or_score()
    {
        var a = Build();
        var b = Build(Edit(j => { foreach (var row in j["worksheetRows"]!.AsArray()) {
            row!["unitRole"] = "UNDEFINED_ALTERNATIVE_ROLE_NOT_AN_ENUM"; row["sourceReviewNote"] = "May be another role under another task.";
        } }));
        Assert.Equal(P7ApprovedPilotGold.ScoringProjection(a), P7ApprovedPilotGold.ScoringProjection(b));
        var atoms = Source.Select(s => new ReviewOccurrence(s.Alias, s.Page, s.Text.Length)).ToArray();
        PilotPrediction[] predictions = [new("synthetic", "A", a.Units[0].Parts)];
        Assert.Equal(JsonSerializer.Serialize(P7PilotScorer.Score(Scope, atoms, a.Annotations, a.Units, predictions)),
            JsonSerializer.Serialize(P7PilotScorer.Score(Scope, atoms, b.Annotations, b.Units, predictions)));
        Assert.Equal("UNDEFINED_ALTERNATIVE_ROLE_NOT_AN_ENUM", b.ReviewerInterpretations[0].ProposalRoleNote);
    }
    [Fact] public void Pending_alternative_role_note_does_not_override_approved_other_decision()
    {
        var b = Build(Edit(j => j["worksheetRows"]![2]!["unitRole"] = "BRANDING_OR_SUPERTITLE"));
        Assert.Equal("OTHER", b.ReviewedRows.Single(r => r.Alias == "C").SemanticFunction);
        Assert.False(b.ReviewedRows.Single(r => r.Alias == "C").HeadingMembership);
        Assert.Equal("BRANDING_OR_SUPERTITLE", b.ReviewerInterpretations.Single(r => r.Alias == "C").ProposalRoleNote);
    }
    [Fact] public void Role_free_receipt_requires_exact_v1_v2_semantic_parity()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.approved-pilot-gold-dry-run.v3.json"));
        Assert.Equal("34aac4fff3c12bdf59eb3407f7d55b3b9dd3a6a04d8c3fcd83ce50e1cccb425d", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var doc = JsonDocument.Parse(bytes);
        var r = doc.RootElement;
        Assert.Equal(5, r.GetProperty("roleFreeGoldScoringProjectionParityDocuments").GetInt32());
        Assert.False(r.GetProperty("reviewerRolesInGoldDecisionRows").GetBoolean());
        Assert.False(r.GetProperty("roleTaxonomyRequired").GetBoolean());
        Assert.False(r.GetProperty("reviewerInterpretationsScored").GetBoolean());
        Assert.Equal(34, r.GetProperty("sourceReviewRoleNoteValues").GetInt32());
        Assert.Equal(0, r.GetProperty("pending").GetInt32()); Assert.True(r.GetProperty("previousGoldPreserved").GetBoolean());
        Assert.All(r.GetProperty("documents").EnumerateArray(), d => Assert.True(d.GetProperty("v1V2ScoringProjectionByteIdentical").GetBoolean()));
    }
    [Fact] public void Other_label_is_bound_to_task_policy_not_a_universal_negative()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.evaluation-policy.v1.json")));
        Assert.Equal("FREE_TEXT_NOT_REQUIRED_NOT_SCORED_NOT_MODEL_OUTPUT_TAXONOMY", doc.RootElement.GetProperty("reviewerRoleVocabulary").GetString());
        Assert.Equal("NEGATIVE_ONLY_FOR_THIS_TASK_AND_APPROVED_SCOPE_NOT_FOR_SUPERTITLE_OR_ADMINISTRATIVE_REGION_TASKS",
            doc.RootElement.GetProperty("otherScope").GetString());
    }
    [Fact] public void Real_receipt_keeps_provider_and_requests_locked()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.approved-pilot-gold-dry-run.v2.json"));
        Assert.Equal("c4b962554bb6e54c957e444f75fc17210413112eda1303d0419990062e638f9e", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var json = JsonDocument.Parse(bytes);
        var r = json.RootElement; Assert.Equal(213, r.GetProperty("adjudicatedRows").GetInt32());
        Assert.Equal(15, r.GetProperty("oracleExact").GetInt32()); Assert.Equal(0, r.GetProperty("oracleNotEvaluable").GetInt32());
        Assert.Equal("NOT_FROZEN", r.GetProperty("requestManifestStatus").GetString());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32()); Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
        Assert.False(r.GetProperty("actualModelAccuracyScored").GetBoolean());
    }
}
