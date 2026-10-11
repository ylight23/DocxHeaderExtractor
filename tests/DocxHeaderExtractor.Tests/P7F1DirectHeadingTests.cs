using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #7 (P7_F1_DIRECT_HEADING_V1): contract, binder and source-enrichment tests. Offline only.</summary>
public sealed class P7F1DirectHeadingTests
{
    static readonly P7F1DirectProtocol.Subject[] Subjects =
    [
        new("O1", "L0001:S0", 3, "CHAPTER I"), new("O2", "L0002:S0", 3, "GENERAL PROVISIONS"),
        new("O3", "L0003:S0", 3, "Article 1. Scope"), new("O4", "L0004:S0", 3, "This Decree applies to agencies."),
    ];
    static readonly F1QIssuedOccurrence[] Issued = Subjects.Select(s => new F1QIssuedOccurrence(s.Occurrence, s.SourceAlias, s.Page, s.Text)).ToArray();
    static readonly string Scope = P7F1DirectProtocol.IssuedSetSha256("D001-PACK_001-C1", "001", Subjects);
    static readonly HashSet<string> Citable = new(Subjects.Select(s => s.SourceAlias).Append("L0005:S0"), StringComparer.Ordinal);

    static string Response(string headings = "[]", string unresolved = "[]", string assessment = "COMPLETE", string? scope = null, string requestId = "D001-PACK_001-C1") =>
        $$"""{"protocolVersion":"P7_F1_DIRECT_HEADING_V1","requestId":"{{requestId}}","issuedSetSha256":"{{scope ?? Scope}}","assessment":"{{assessment}}","headings":{{headings}},"unresolved":{{unresolved}}}""";
    static string H(string occ, string alias, string refs = null!, string why = "Bold centred chapter label opening the chapter") =>
        $$"""{"occurrence":"{{occ}}","sourceAlias":"{{alias}}","evidenceRefs":{{refs ?? $"[\"{alias}\"]"}},"justification":"{{why}}"}""";
    static F1QValidation V(string text) => P7F1DirectProtocol.Validate(text, "D001-PACK_001-C1", Scope, Issued, Citable);

    [Fact] public void An_all_negative_complete_response_is_accepted_and_omitted_ids_are_not_rows()
    {
        var v = V(Response());
        Assert.True(v.StrictAccepted); Assert.Empty(v.Rows);
    }

    [Fact] public void Several_fragments_of_one_heading_are_selected_independently_and_unresolved_is_distinct()
    {
        var v = V(Response($"[{H("O1", "L0001:S0")},{H("O2", "L0002:S0")}]", """[{"occurrence":"O3","sourceAlias":"L0003:S0","missingEvidence":["cross-page context"]}]"""));
        Assert.True(v.StrictAccepted);
        Assert.Equal(["HEADING_MEMBER", "HEADING_MEMBER", "UNRESOLVED"], v.Rows.Select(r => r.Decision!.Assessment).ToArray());
        Assert.All(v.Rows, r => Assert.Null(r.Decision!.Function)); // never E/R/O
    }

    [Theory]
    [InlineData("dup", "OCCURRENCE_LISTED_TWICE")]
    [InlineData("both", "OCCURRENCE_LISTED_TWICE")]
    [InlineData("foreign-id", "OCCURRENCE_NOT_ISSUED")]
    [InlineData("other-doc-alias", "SOURCE_ALIAS_MISMATCH")]
    [InlineData("scope", "ISSUED_SET_SHA_MISMATCH")]
    [InlineData("request", "REQUEST_ID_MISMATCH")]
    [InlineData("ref-not-citable", "EVIDENCE_REF_NOT_CITABLE")]
    [InlineData("ref-not-about-subject", "EVIDENCE_NOT_ABOUT_SUBJECT")]
    [InlineData("no-refs", "EVIDENCE_REFS_INVALID")]
    [InlineData("blank-justification", "JUSTIFICATION_INVALID")]
    [InlineData("extra-key", "HEADING_KEYS_INVALID")]
    [InlineData("incomplete", "MODEL_DECLARED_INCOMPLETE")]
    [InlineData("unresolved-empty", "MISSING_EVIDENCE_INVALID")]
    [InlineData("blank", "RESPONSE_NOT_JSON")]
    [InlineData("root-extra", "ROOT_KEYS_INVALID")]
    public void Contract_violations_fail_closed(string kind, string code)
    {
        var text = kind switch
        {
            "dup" => Response($"[{H("O1", "L0001:S0")},{H("O1", "L0001:S0")}]"),
            "both" => Response($"[{H("O1", "L0001:S0")}]", """[{"occurrence":"O1","sourceAlias":"L0001:S0","missingEvidence":["x"]}]"""),
            "foreign-id" => Response($"[{H("O9", "L0001:S0")}]"),
            "other-doc-alias" => Response($"[{H("O1", "L0777:S0")}]"),
            "scope" => Response(scope: new string('0', 64)),
            "request" => Response(requestId: "D002-PACK_001-C1"),
            "ref-not-citable" => Response($"[{H("O1", "L0001:S0", "[\"L0001:S0\",\"L9999:S0\"]")}]"),
            "ref-not-about-subject" => Response($"[{H("O1", "L0001:S0", "[\"L0002:S0\"]")}]"),
            "no-refs" => Response($"[{H("O1", "L0001:S0", "[]")}]"),
            "blank-justification" => Response($"[{H("O1", "L0001:S0", why: " ")}]"),
            "extra-key" => Response("""[{"occurrence":"O1","sourceAlias":"L0001:S0","evidenceRefs":["L0001:S0"],"justification":"x","function":"ESTABLISHES_STRUCTURE"}]"""),
            "incomplete" => Response(assessment: "INCOMPLETE"),
            "unresolved-empty" => Response(unresolved: """[{"occurrence":"O3","sourceAlias":"L0003:S0","missingEvidence":[]}]"""),
            "blank" => "",
            "root-extra" => Response().TrimEnd('}') + ""","headingsLevels":[]}""",
            _ => throw new ArgumentException(kind),
        };
        var v = V(text);
        Assert.False(v.StrictAccepted); Assert.Equal(code, v.FailureCode);
    }

    [Fact] public void A_tool_evidence_id_passes_the_runner_check_and_is_left_for_the_binder()
    {
        var v = V(Response($"[{H("O1", "L0001:S0", "[\"E1.1\"]")}]"));
        Assert.True(v.StrictAccepted); Assert.Contains("E1.1", v.UnresolvedEvidenceRefs);
    }

    [Fact] public void A_fenced_response_is_accepted_only_through_the_normalized_view()
    {
        var fenced = "```json\n" + Response() + "\n```";
        Assert.Equal("RESPONSE_NOT_JSON", V(fenced).FailureCode);
        var n = P7F1QFenceNormalization.Normalize(fenced);
        Assert.True(n.Applied); Assert.True(V(n.Normalized).StrictAccepted);
    }

    [Fact] public void The_scope_hash_binds_request_document_order_and_text()
    {
        Assert.NotEqual(Scope, P7F1DirectProtocol.IssuedSetSha256("D001-PACK_001-C1", "002", Subjects));
        Assert.NotEqual(Scope, P7F1DirectProtocol.IssuedSetSha256("D001-PACK_001-C1", "001", Subjects.Reverse().ToArray()));
        Assert.NotEqual(Scope, P7F1DirectProtocol.IssuedSetSha256("D001-PACK_001-C1", "001", [Subjects[0] with { Text = "CHAPTER 1" }, .. Subjects[1..]]));
    }

    [Fact] public void The_direct_prompt_asks_only_for_membership_and_the_frozen_V2_prompt_is_unchanged()
    {
        var prompt = P7F1DirectProtocol.SystemPrompt();
        foreach (var banned in new[] { "ESTABLISHES_STRUCTURE", "REPRESENTS_STRUCTURE", "semanticKind", "hierarchy\":", "\"function\"" })
            Assert.DoesNotContain(banned, prompt);
        Assert.Contains("unresolved", prompt); Assert.Contains("No page images are supplied", prompt);
        Assert.DoesNotContain('\r', prompt);
        using var v3 = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.f1q.execution-plan.v3.json")));
        Assert.Equal(v3.RootElement.GetProperty("requests")[0].GetProperty("systemPromptSha256").GetString(),
            SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QProtocolV2.SystemPrompt())));
    }

    // ---------------- Binder ----------------

    static JsonElement PlanRequest(string? scopeOverride = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        handle = "D001-PACK_001-C1|DIRECT", requestId = "D001-PACK_001-C1", document = "001", issuedSetSha256 = scopeOverride ?? Scope,
        issued = Subjects.Select(s => new { occurrence = s.Occurrence, sourceAlias = s.SourceAlias, page = s.Page, textSha256 = P7F1DirectProtocol.TextSha256(s.Text) }),
        contextAliases = new[] { "L0005:S0" },
    })).RootElement;
    static P7F1DirectBinder.SourceFact Fact(string alias) => Subjects.FirstOrDefault(s => s.SourceAlias == alias) is { } s
        ? new(s.Page, s.Text, null) : new(3, "context", null);

    static string Raw(params (string Status, string? Code, string? Response, (string Id, string[] Aliases)[] Tools)[] attempts)
    {
        var dir = Path.Combine(Path.GetTempPath(), "p7f1direct-" + Guid.NewGuid().ToString("N"));
        foreach (var (a, i) in attempts.Select((a, i) => (a, i + 1)))
        {
            var d = Path.Combine(dir, $"attempt-{i}"); Directory.CreateDirectory(Path.Combine(d, "turn-1"));
            File.WriteAllText(Path.Combine(d, "request-receipt.json"), JsonSerializer.Serialize(new { status = a.Status, failureCode = a.Code }));
            if (a.Response is not null) File.WriteAllText(Path.Combine(d, "response.txt"), a.Response);
            foreach (var (t, n) in a.Tools.Select((t, n) => (t, n + 1)))
                File.WriteAllText(Path.Combine(d, "turn-1", $"tool-{n:D2}-{t.Id}.json"), JsonSerializer.Serialize(new { evidenceId = t.Id, status = "OK", returnedAliases = t.Aliases }));
        }
        return dir;
    }

    [Fact] public void The_binder_binds_every_selection_to_parser_text_even_a_wrong_one_and_exposes_negatives_only_when_complete()
    {
        // O4 is body text: a semantically wrong pick must still be bound, never vetoed.
        var dir = Raw(("ACCEPTED", null, Response($"[{H("O1", "L0001:S0")},{H("O4", "L0004:S0")}]", """[{"occurrence":"O3","sourceAlias":"L0003:S0","missingEvidence":["x"]}]"""), []));
        var b = P7F1DirectBinder.Bind(PlanRequest(), dir, Fact);
        Assert.Equal("ACCEPTED_COMPLETE", b["status"]!.GetValue<string>());
        Assert.Equal(["L0001:S0", "L0004:S0"], b["selected"]!.AsArray().Select(x => x!["sourceAlias"]!.GetValue<string>()).ToArray());
        Assert.Equal("This Decree applies to agencies.", b["selected"]![1]!["text"]!.GetValue<string>());
        Assert.Equal(["L0002:S0"], b["negativeEligible"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal("L0003:S0", b["unresolved"]![0]!["sourceAlias"]!.GetValue<string>());
    }

    [Fact] public void Tool_evidence_must_exist_in_the_raw_transcript_and_cover_the_subject()
    {
        var ok = P7F1DirectBinder.Bind(PlanRequest(), Raw(("ACCEPTED", null, Response($"[{H("O1", "L0001:S0", "[\"E1.1\"]")}]"), [("E1.1", ["L0001:S0", "L0000:S0"])])), Fact);
        Assert.Equal("ACCEPTED_COMPLETE", ok["status"]!.GetValue<string>());
        var missing = P7F1DirectBinder.Bind(PlanRequest(), Raw(("ACCEPTED", null, Response($"[{H("O1", "L0001:S0", "[\"E1.2\"]")}]"), [("E1.1", ["L0001:S0"])])), Fact);
        Assert.Equal("EVIDENCE_ID_NOT_IN_TRANSCRIPT:E1.2", missing["failureCode"]!.GetValue<string>());
        var notAbout = P7F1DirectBinder.Bind(PlanRequest(), Raw(("ACCEPTED", null, Response($"[{H("O1", "L0001:S0", "[\"E1.1\"]")}]"), [("E1.1", ["L0002:S0"])])), Fact);
        Assert.Equal("EVIDENCE_NOT_ABOUT_SUBJECT:O1", notAbout["failureCode"]!.GetValue<string>());
    }

    [Fact] public void Missing_failed_and_incomplete_requests_never_yield_negatives()
    {
        Assert.Equal("MISSING", P7F1DirectBinder.Bind(PlanRequest(), Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid()), Fact)["status"]!.GetValue<string>());
        var failed = P7F1DirectBinder.Bind(PlanRequest(), Raw(("CONTRACT_FAILED", "SOURCE_ALIAS_MISMATCH", "{}", []), ("CONTRACT_FAILED", "RESPONSE_NOT_JSON", "x", [])), Fact);
        Assert.Equal("CONTRACT_FAILED", failed["status"]!.GetValue<string>()); Assert.Null(failed["negativeEligible"]);
        var incomplete = P7F1DirectBinder.Bind(PlanRequest(), Raw(("CONTRACT_FAILED", "MODEL_DECLARED_INCOMPLETE", Response(assessment: "INCOMPLETE"), [])), Fact);
        Assert.Equal("INCOMPLETE", incomplete["status"]!.GetValue<string>()); Assert.Null(incomplete["negativeEligible"]);
    }

    [Fact] public void A_tampered_plan_scope_or_source_identity_fails_closed()
    {
        var dir = Raw(("ACCEPTED", null, Response(), []));
        Assert.Equal("PLAN_SCOPE_HASH_MISMATCH", P7F1DirectBinder.Bind(PlanRequest(new string('1', 64)), dir, Fact)["failureCode"]!.GetValue<string>());
        var drift = P7F1DirectBinder.Bind(PlanRequest(), dir, a => a == "L0002:S0" ? new(3, "GENERAL PROVISION", null) : Fact(a));
        Assert.Equal("PLAN_SOURCE_IDENTITY_MISMATCH:L0002:S0", drift["failureCode"]!.GetValue<string>());
    }

    // ---------------- Source enrichment on real development documents ----------------

    static (P7F1SourceEnrichment E, P7F1QEvidenceTools T) Enrich(string pdf)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(pdf));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        // The tools need a non-empty issued scope; enrichment reads only whole-document lines.
        var tools = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return (new P7F1SourceEnrichment(tools, parsed.Snapshot, parsed.Details), tools);
    }
    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, P7F1QEvidenceTools.WireJson);
    static readonly Lazy<(P7F1SourceEnrichment E, P7F1QEvidenceTools T)> Doc049 = new(() => Enrich("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/049_IDA_Financial_Statements_September_2024.pdf"));
    static readonly Lazy<(P7F1SourceEnrichment E, P7F1QEvidenceTools T)> Doc044 = new(() => Enrich("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/044_IDA_Financial_Statements_June_2024.pdf"));

    [Fact] public void A_financial_statement_row_reports_numeric_peers_and_repetition_without_asserting_a_table()
    {
        var e = J(Doc049.Value.E.Of("L0974:S0")); // "Market borrowings, at fair value", balance sheet p29
        Assert.Equal(2, e.GetProperty("row").GetProperty("num").GetInt32());
        Assert.True(e.GetProperty("tbl").GetProperty("self").GetBoolean());
        Assert.True(e.GetProperty("rep").GetInt32() >= 2);
        Assert.Equal("UNRESOLVED", e.GetProperty("nav").GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("cap").ValueKind); // captionless statement: absence of a caption asserts nothing
    }

    [Fact] public void A_row_under_a_caption_gets_a_candidate_never_an_owner_and_a_caption_line_marks_itself()
    {
        var row = J(Doc049.Value.E.Of("L1899:S0")); // "Total Investments", under "Table J2: ..." on p52
        var cap = row.GetProperty("cap");
        Assert.Equal("ABOVE", cap.GetProperty("rel").GetString());
        Assert.StartsWith("Table J2", cap.GetProperty("text").GetString());
        Assert.False(cap.TryGetProperty("owner", out _));
        var self = J(Doc044.Value.E.Of("L3351:S0")); // "Table B1: Subscriptions and contributions paid-in"
        Assert.Equal("SELF", self.GetProperty("cap").GetProperty("rel").GetString());
    }

    [Fact] public void A_list_of_tables_entry_exposes_its_page_number_and_only_a_candidate_target()
    {
        var t = Doc044.Value.T; var e = Doc044.Value.E;
        var entry = t.LinesOnPage(4).First(l => l.Text.StartsWith("Table 1", StringComparison.Ordinal) || l.Text.StartsWith("Table1", StringComparison.Ordinal));
        var nav = J(e.Of(entry.Alias)).GetProperty("nav");
        Assert.Equal(JsonValueKind.Number, nav.GetProperty("pageNo").ValueKind);
        Assert.Contains(nav.GetProperty("target").GetString(), new[] { "SAME_TEXT_ON_REFERENCED_PAGE_CANDIDATE", "UNRESOLVED", "NOT_AVAILABLE" });
    }

    [Fact] public void Enrichment_is_deterministic_never_labels_and_never_asserts_continuity_or_ownership()
    {
        var (e, t) = Doc049.Value;
        foreach (var page in new[] { 1, 29, 39, 51, 52 })
            foreach (var line in t.LinesOnPage(page))
            {
                var text = JsonSerializer.Serialize(e.Of(line.Alias), P7F1QEvidenceTools.WireJson);
                Assert.Equal(text, JsonSerializer.Serialize(e.Of(line.Alias), P7F1QEvidenceTools.WireJson));
                foreach (var banned in new[] { "ESTABLISHES", "REPRESENTS", "heading", "isTable", "owner\"" }) Assert.DoesNotContain(banned, text);
                var j = JsonDocument.Parse(text).RootElement;
                if (j.TryGetProperty("prev", out var prev) && prev.ValueKind == JsonValueKind.Object) Assert.Equal("UNRESOLVED", prev.GetProperty("continuity").GetString());
                if (j.TryGetProperty("cap", out var cap) && cap.ValueKind == JsonValueKind.Object && cap.GetProperty("rel").GetString() == "PREV_PAGE")
                    Assert.Equal("UNRESOLVED", cap.GetProperty("st").GetString());
            }
    }
}
