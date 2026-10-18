using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingGroupVerificationTests
{
    const string JsonModeProviderError = "{\"error\":{\"message\":\"'messages' must contain the word 'json' in some form, to use 'response_format' of type 'json_object'.\"}}";

    [Fact]
    public void Production_verifier_satisfies_the_providers_JSON_mode_message_requirement_through_the_real_transport()
    {
        var source = Source();
        using var handler = new FakeOpenRouter((body, _) =>
        {
            // Simulate Alibaba's HTTP validation rather than auto-accepting any verifier request.
            var mentionsJson = body["messages"]!.AsArray().Any(m =>
                m!["content"]!.ToString().Contains("json", StringComparison.OrdinalIgnoreCase));
            if (!mentionsJson) return (HttpStatusCode.BadRequest, JsonModeProviderError);
            Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
            Assert.Contains("JSON object", body["messages"]![0]!["content"]!.GetValue<string>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = FakeOpenRouter.VerificationResponse(body) }, "stop"));
        }, autoVerify: false);
        var budget = new AgentBudget(10, 100);
        using var model = new AgentModel(handler, "sk-test", budget);
        var result = AgentGroupVerification.Verify(source, new AgentGrouping.Result([new[] { A(1), A(2) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model.Turn, budget, 1, CancellationToken.None, 0, "Extract structural headings.");
        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Single(result.Groups);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(1, result.Observation.VerifiedRuns);
    }

    [Fact]
    public void Provider_JSON_mode_rejection_reports_the_actionable_cause_without_returning_raw_provider_text()
    {
        using var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.BadRequest,
            JsonModeProviderError.Replace("some form", "some form sk-test private provider text", StringComparison.Ordinal)), autoVerify: false);
        using var model = new AgentModel(handler, "sk-test", new AgentBudget(10, 100));
        var request = AgentGrouping.BuildRequest("Return an object.", new JsonArray(), "Use the declared schema.");
        var error = Assert.Throws<ReplyFailure>(() => model.Turn("VERIFY-001", 1, request));
        Assert.Equal("TRANSPORT:CLIENT_ERROR:HTTP_400:JSON_MODE_REQUIRES_JSON_MESSAGE", error.Code);
        Assert.DoesNotContain("sk-test", error.Message);
        Assert.DoesNotContain("private provider text", error.Message);
    }

    static string A(int n) => $"P032-L{n:000}";

    static AgentGrouping.Result EightCandidates() => new(Enumerable.Range(0, 8).Select(i => new[] { A(1 + 2 * i) }).ToArray(), [], [], 0);

    [Theory]
    [InlineData("missing", "RUN_MISSING")]
    [InlineData("duplicate", "RUN_DUPLICATE")]
    [InlineData("fields", "VERDICT_REQUIRED_FIELDS_MISSING")]
    [InlineData("reason-type", "VERDICT_FIELD_TYPES_INVALID")]
    [InlineData("membership-type", "VERDICT_FIELD_TYPES_INVALID")]
    [InlineData("member-fields", "MEMBERSHIP_REQUIRED_FIELDS_MISSING")]
    [InlineData("member-type", "MEMBERSHIP_FIELD_TYPES_INVALID")]
    [InlineData("evidence", "EVIDENCE_REFS_INVALID")]
    [InlineData("membership", "MEMBERSHIP_COVERAGE_INVALID")]
    [InlineData("status", "MEMBERSHIP_STATUS_INVALID")]
    [InlineData("inconsistent", "MEMBERSHIP_DECISION_INCONSISTENT")]
    public void One_bad_verdict_keeps_seven_valid_candidates_and_repairs_only_the_bad_one(string mode, string diagnostic)
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            var response = JsonNode.Parse(Verdicts(packet, _ => "ACCEPT"))!;
            var verdicts = response["verdicts"]!.AsArray();
            if (++calls == 1)
            {
                var bad = verdicts[3]!;
                switch (mode)
                {
                    case "missing": verdicts.RemoveAt(3); break;
                    case "duplicate": verdicts.Add(bad.DeepClone()); break;
                    case "fields": bad.AsObject().Remove("reason"); break;
                    case "reason-type": bad["reason"] = 12; break;
                    case "membership-type": bad["membership"] = new JsonObject(); break;
                    case "member-fields": bad["membership"]![0]!.AsObject().Remove("evidenceRefs"); break;
                    case "member-type": bad["membership"]![0]!["status"] = true; break;
                    case "evidence": bad["evidenceRefs"]![0] = "UNKNOWN"; break;
                    case "membership": bad["membership"]!.AsArray().Clear(); break;
                    case "status": bad["membership"]![0]!["status"] = "BODY"; break;
                    case "inconsistent": bad["membership"]![0]!["status"] = "NOT_HEADING"; break;
                }
            }
            else
            {
                Assert.Equal("V4", Assert.Single(packet["candidates"]!.AsArray())!["run"]!.GetValue<string>());
                var repair = JsonNode.Parse(request.Body)!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
                Assert.Contains(diagnostic, repair);
                Assert.Contains("must not be resubmitted", repair);
            }
            return new ModelTurn(response.ToJsonString(), [], "stop");
        };
        var input = EightCandidates();
        var result = AgentGroupVerification.Verify(Source(lineCount: 16), input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(2, calls);
        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Equal(input.Groups, result.Groups);
        Assert.Equal(8, result.Observation.VerifiedRuns);
        Assert.Contains(result.Evidence, e => e.Contains(diagnostic, StringComparison.Ordinal));
        Assert.Contains(result.Provenance, p => p.Alias == A(7) && p.CandidateId == "V4" && p.Round == 1
            && p.FailureCodes.Any(c => c == diagnostic || c.StartsWith(diagnostic + ":", StringComparison.Ordinal)));
        Assert.Contains(result.Provenance, p => p.Alias == A(7) && p.Round == 2 && p.Decision == "ACCEPT" && p.Membership == "HEADING");
        Assert.DoesNotContain(result.Provenance, p => p.Alias != A(7)
            && p.FailureCodes.Any(c => c == diagnostic || c.StartsWith(diagnostic + ":", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("verdict")]
    [InlineData("membership")]
    [InlineData("both")]
    public void Unknown_verdict_and_membership_fields_are_audited_without_changing_semantic_authority_or_repairing(string mode)
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            calls++;
            var response = JsonNode.Parse(Verdicts(Payload(request), _ => "ACCEPT"))!;
            foreach (var verdict in response["verdicts"]!.AsArray())
            {
                if (mode is "verdict" or "both") verdict!["confidence"] = 0.9;
                if (mode is "membership" or "both")
                    foreach (var member in verdict!["membership"]!.AsArray()) member!["statusOverride"] = "NOT_HEADING";
            }
            return new ModelTurn(response.ToJsonString(), [], "stop");
        };
        var input = EightCandidates();
        var result = AgentGroupVerification.Verify(Source(lineCount: 16), input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(1, calls);
        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Equal(input.Groups, result.Groups);
        Assert.All(result.Memberships.Values, status => Assert.Equal("HEADING", status));
        Assert.Equal(mode == "both" ? 16 : 8, result.Observation.FieldDiagnostics!.Count);
        Assert.All(result.Observation.FieldDiagnostics, d => Assert.True(d.ProtocolAccepted));
        var protocol = result.Evidence.Select(e => JsonNode.Parse(e)!).Single(e => e["stage"]?.GetValue<string>() == "verification-protocol");
        var diagnostics = protocol["fieldDiagnostics"]!.AsArray();
        Assert.Equal(mode == "both" ? 16 : 8, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Empty(d!["missing"]!.AsArray());
            Assert.Empty(d["invalidTypes"]!.AsArray());
            Assert.Single(d["unexpected"]!.AsArray());
        });
    }

    [Fact]
    public void Refused_fields_report_missing_unexpected_and_actual_types_in_audit_and_repair()
    {
        var requests = new List<string>();
        AgentUnit.Model model = (_, _, request) =>
        {
            requests.Add(request.Body);
            var response = JsonNode.Parse(Verdicts(Payload(request), _ => "ACCEPT"))!;
            var verdict = response["verdicts"]![0]!.AsObject();
            verdict.Remove("reason");
            verdict["confidence"] = 0.9;
            verdict["membership"] = "not an array";
            return new ModelTurn(response.ToJsonString(), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100),
            1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { A(1) }, result.UnresolvedAliases);
        Assert.All(result.Observation.FieldDiagnostics!, d => Assert.False(d.ProtocolAccepted));
        var protocol = result.Evidence.Select(e => JsonNode.Parse(e)!).First(e => e["stage"]?.GetValue<string>() == "verification-protocol");
        var diagnostic = Assert.Single(protocol["fieldDiagnostics"]!.AsArray())!;
        Assert.Equal("V1", diagnostic["run"]!.GetValue<string>());
        Assert.Equal("reason", Assert.Single(diagnostic["missing"]!.AsArray())!.GetValue<string>());
        Assert.Equal("confidence", Assert.Single(diagnostic["unexpected"]!.AsArray())!.GetValue<string>());
        var type = Assert.Single(diagnostic["invalidTypes"]!.AsArray())!;
        Assert.Equal("membership", type["field"]!.GetValue<string>());
        Assert.Equal("Array", type["expected"]!.GetValue<string>());
        Assert.Equal("String", type["actual"]!.GetValue<string>());
        var repair = JsonNode.Parse(requests[1])!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("\"missing\":[\"reason\"]", repair);
        Assert.Contains("\"unexpected\":[\"confidence\"]", repair);
        Assert.Contains("\"expected\":\"Array\"", repair);
    }

    [Fact]
    public void Appendix_cover_and_book_title_false_merge_can_be_split_only_by_model_regroup_and_independent_acceptance()
    {
        // A protocol regression for the reported source functions, not a claim that a scripted model proves live semantic accuracy.
        var source = Source(sourceTexts: ["II. MẪU SỔ CHỨNG THỰC", "1. Bìa sổ", "a) Sổ Chứng thực bản sao từ bản chính", "Nội dung biểu mẫu"]);
        var groupingCalls = 0;
        AgentUnit.Model model = (unit, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                Assert.Contains(packet["context"]!.AsArray(), line => line!["text"]?.GetValue<string>() == "II. MẪU SỔ CHỨNG THỰC");
                return new ModelTurn(Verdicts(packet, _ => unit.Contains("REVERIFY", StringComparison.Ordinal) ? "ACCEPT" : "REGROUP"), [], "stop");
            }
            groupingCalls++;
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(run => new
                {
                    run = run!["run"]!.GetValue<string>(), headings = new[] { new[] { A(2) }, new[] { A(3) } }
                })
            }), [], "stop");
        };
        var result = AgentGroupVerification.Verify(source, new AgentGrouping.Result([new[] { A(2), A(3) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100),
            1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Equal(1, groupingCalls);
        Assert.Equal(new[] { new[] { A(2) }, new[] { A(3) } }, result.Groups.Select(g => g.ToArray()));
        Assert.Equal(1, result.Observation.BoundaryDisputes);
        Assert.Equal(1, result.Observation.VerifiedRuns);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("transport")]
    [InlineData("budget")]
    public void Later_failure_never_discards_accepted_candidates_or_turns_unknown_membership_into_body(string mode)
    {
        var calls = 0;
        var budget = new AgentBudget(mode == "budget" ? 1 : 20, 100);
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (++calls > 1)
            {
                Assert.Equal("V4", Assert.Single(packet["candidates"]!.AsArray())!["run"]!.GetValue<string>());
                if (mode == "transport") throw new ReplyFailure("TRANSPORT:STREAM_ERROR");
                // Even a repeated/revised verdict for a frozen candidate must not overwrite it.
                var response = JsonNode.Parse(Verdicts(packet, _ => "ACCEPT"))!;
                response["verdicts"]![0]!["membership"]!.AsArray().Clear();
                var stale = JsonNode.Parse(Verdicts(PayloadOfFrozenCandidate(), _ => "MEMBERSHIP_DISPUTE"))!["verdicts"]![0]!.DeepClone();
                response["verdicts"]!.AsArray().Add(stale);
                return new ModelTurn(response.ToJsonString(), [], "stop");
            }
            var first = JsonNode.Parse(Verdicts(packet, _ => "ACCEPT"))!;
            first["verdicts"]!.AsArray().RemoveAt(3);
            return new ModelTurn(first.ToJsonString(), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(lineCount: 16), EightCandidates(), new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(7, result.Groups.Count);
        Assert.Equal(new[] { A(7) }, result.UnresolvedAliases);
        Assert.Equal(7, result.Observation.VerifiedRuns);
        Assert.Equal("HEADING", result.Memberships[A(1)]);
        Assert.False(result.Memberships.ContainsKey(A(7)));
        Assert.Empty(result.Disputes);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(mode == "invalid" ? AgentGroupVerification.MaxPosts : mode == "transport" ? 2 + AgentGroupVerification.TransportRetries : 1, calls);

        static JsonObject PayloadOfFrozenCandidate() => new() { ["candidates"] = new JsonArray(new JsonObject
        { ["run"] = "V1", ["proposedGroups"] = new JsonArray(new JsonArray(A(1))) }) };
    }

    [Theory]
    [InlineData("TRANSPORT:STREAM_ERROR", true)]
    [InlineData("TRANSPORT:STREAM_INCOMPLETE", true)]
    [InlineData("TRANSPORT:TRANSPORT_ERROR", true)]
    [InlineData("TRANSPORT:RATE_LIMITED_NO_RETRY", true)]
    [InlineData("TRANSPORT:SERVER_ERROR_NO_RETRY", true)]
    [InlineData("TRANSPORT:CLIENT_ERROR", false)]
    [InlineData("TRANSPORT:PAYMENT_REQUIRED", false)]
    [InlineData("AUDIT:USAGE_MISSING", false)]
    [InlineData("ENDPOINT_OR_MODEL_UNEXPECTED", false)]
    public void Transport_retries_are_bounded_allowlisted_audited_and_send_identical_bytes(string failure, bool retry)
    {
        var requests = new List<PreparedRequest>();
        var attempts = new List<int>();
        var budget = new AgentBudget(20, 100);
        AgentUnit.Model model = (_, attempt, request) =>
        {
            requests.Add(request); attempts.Add(attempt);
            if (requests.Count <= AgentGroupVerification.TransportRetries) throw new ReplyFailure(failure);
            return new ModelTurn(Verdicts(Payload(request), _ => "ACCEPT"), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(retry ? 1 + AgentGroupVerification.TransportRetries : 1, requests.Count);
        Assert.Equal(requests.Count, budget.Calls);
        Assert.Equal(Enumerable.Range(1, requests.Count), attempts);
        Assert.All(requests, r => Assert.Equal(requests[0], r));
        Assert.Equal(retry ? 1 : 0, result.Groups.Count);
        Assert.Equal(retry ? 0 : 1, result.UnresolvedAliases.Count);
        Assert.Contains(result.Evidence, e => e.Contains(failure, StringComparison.Ordinal) && e.Contains("transportRetry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("budget")]
    [InlineData("exhausted")]
    public void Retry_obeys_cancellation_budget_and_exhaustion(string mode)
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var budget = new AgentBudget(mode == "budget" ? 1 : 20, 100);
        AgentUnit.Model model = (_, _, _) =>
        {
            calls++;
            if (mode == "cancel") cts.Cancel();
            throw new ReplyFailure("TRANSPORT:STREAM_ERROR");
        };
        AgentGrouping.Result Run() => AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, budget, 1, cts.Token, mode == "cancel" ? 10000 : 0, Instruction);
        if (mode == "cancel") Assert.ThrowsAny<OperationCanceledException>(() => Run());
        else
        {
            var result = Run();
            Assert.Empty(result.Groups);
            Assert.Empty(result.Memberships);
            Assert.Equal(new[] { A(1) }, result.UnresolvedAliases);
            Assert.Contains(result.Failures, f => f.Contains(mode == "budget" ? "BUDGET:CALL_LIMIT" : "CALL:TRANSPORT:STREAM_ERROR", StringComparison.Ordinal));
        }
        Assert.Equal(mode == "exhausted" ? 1 + AgentGroupVerification.TransportRetries : 1, calls);
    }

    [Fact]
    public void Real_stream_failure_is_retried_without_reading_partial_content_as_a_verdict()
    {
        using var handler = new FakeOpenRouter((body, number) => (HttpStatusCode.OK,
            number == 1 ? "data: {\"error\":{\"message\":\"temporary\"}}\n\ndata: [DONE]\n\n"
                : FakeOpenRouter.Stream(new { content = FakeOpenRouter.VerificationResponse(body) }, "stop")), autoVerify: false);
        var budget = new AgentBudget(10, 100);
        using var model = new AgentModel(handler, "sk-test", budget);
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model.Turn, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(2, handler.Posts);
        Assert.Equal(2, budget.Calls);
        Assert.Single(result.Groups);
        Assert.Empty(result.Failures);
        Assert.Contains(result.Evidence, e => e.Contains("TRANSPORT:STREAM_ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void Transport_retry_limit_is_shared_across_JSON_rounds_and_does_not_reset_on_repair()
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            if (++calls % 2 == 1) throw new ReplyFailure("TRANSPORT:STREAM_ERROR");
            return new ModelTurn("{\"protocolVersion\":\"" + AgentGroupVerification.Version + "\",\"verdicts\":[]}", [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(AgentGroupVerification.MaxCalls, calls);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { A(1) }, result.UnresolvedAliases);
        Assert.Equal(3, result.Evidence.Count(e => e.Contains("\"transportRetry\":true", StringComparison.Ordinal)));
        Assert.Equal(3, result.Evidence.Count(e => e.Contains("\"stage\":\"verification-protocol\"", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("version", "PROTOCOL_VERSION_INVALID")]
    [InlineData("bytes", "REPLY_OVER_CAP")]
    [InlineData("finish", "FINISH_REASON_INVALID")]
    [InlineData("json", "JSON_OR_ENVELOPE_INVALID")]
    [InlineData("envelope", "VERDICTS_ENVELOPE_INVALID")]
    public void Invalid_envelope_cannot_release_individual_verdicts_and_reports_the_specific_gate(string mode, string diagnostic)
    {
        AgentUnit.Model model = (_, _, request) =>
        {
            var response = JsonNode.Parse(Verdicts(Payload(request), _ => "ACCEPT"))!;
            if (mode == "version") response["protocolVersion"] = "UNKNOWN";
            if (mode == "envelope") response["extra"] = true;
            var content = mode == "json" ? "{" : response.ToJsonString();
            if (mode == "bytes") content += new string(' ', VisibleResponseCapBytes);
            return new ModelTurn(content, [], mode == "finish" ? "length" : "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Empty(result.Memberships);
        Assert.Equal(new[] { A(1) }, result.UnresolvedAliases);
        Assert.Contains(result.Failures, f => f.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("out-of-order")]
    [InlineData("unknown-alias")]
    [InlineData("unknown-status")]
    [InlineData("unknown-evidence")]
    [InlineData("regroup-with-non-heading")]
    [InlineData("dispute-with-all-heading")]
    public void Per_alias_membership_requires_complete_ordered_grounded_and_consistent_structured_claims(string mode)
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            calls++;
            var root = JsonNode.Parse(Verdicts(Payload(request), _ => "ACCEPT"))!;
            var verdict = root["verdicts"]![0]!;
            var membership = verdict["membership"]!.AsArray();
            switch (mode)
            {
                case "missing": membership.RemoveAt(0); break;
                case "duplicate": membership[1]!["alias"] = membership[0]!["alias"]!.GetValue<string>(); break;
                case "out-of-order":
                    var first = membership[0]!.DeepClone(); membership[0] = membership[1]!.DeepClone(); membership[1] = first; break;
                case "unknown-alias": membership[0]!["alias"] = "UNKNOWN"; break;
                case "unknown-status": membership[0]!["status"] = "BODY"; break;
                case "unknown-evidence": membership[0]!["evidenceRefs"]![0] = "UNKNOWN"; break;
                case "regroup-with-non-heading": verdict["decision"] = "REGROUP"; membership[0]!["status"] = "NOT_HEADING"; break;
                case "dispute-with-all-heading": verdict["decision"] = "MEMBERSHIP_DISPUTE"; break;
            }
            return new ModelTurn(root.ToJsonString(), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([new[] { A(1), A(2), A(3) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Empty(result.Disputes);
        Assert.Empty(result.Memberships);
        Assert.Equal(AgentGroupVerification.MaxPosts, calls);
        Assert.Contains(result.Failures, f => f.Contains("PROTOCOL:VERIFICATION_INVALID", StringComparison.Ordinal));
        if (mode is "missing" or "duplicate" or "out-of-order" or "unknown-alias")
        {
            var coverage = Assert.Single(result.Observation.CoverageDiagnostics!, d => d.Round == AgentGroupVerification.MaxPosts);
            Assert.Equal(new[] { A(1), A(2), A(3) }, coverage.ExpectedAliases);
            Assert.False(coverage.SequenceMatches);
            Assert.Equal(mode == "missing" ? new[] { A(2), A(3) }
                : mode == "duplicate" ? new[] { A(1), A(1), A(3) }
                : mode == "out-of-order" ? new[] { A(2), A(1), A(3) } : new[] { "UNKNOWN", A(2), A(3) }, coverage.ObservedAliases);
            Assert.Equal(mode == "out-of-order" ? Array.Empty<string>() : new[] { mode == "duplicate" ? A(2) : A(1) }, coverage.MissingAliases);
            Assert.Equal(mode == "duplicate" ? new[] { A(1) } : Array.Empty<string>(), coverage.DuplicateAliases);
            Assert.Equal(mode == "unknown-alias" ? new[] { "UNKNOWN" } : Array.Empty<string>(), coverage.UnexpectedAliases);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Independent_boundary_repairs_overlap_without_exceeding_jobs_and_keep_output_order(bool secondCycle)
    {
        var source = Source();
        var expected = new[] { new[] { A(1), A(2) }, new[] { A(5), A(6) }, new[] { A(9), A(10) }, new[] { A(13), A(14) } };
        var input = new AgentGrouping.Result(expected, [], [], 0);
        using var firstTwo = new CountdownEvent(2);
        var active = 0; var maximum = 0; var groupCalls = 0;
        AgentUnit.Model model = (unit, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
                return new ModelTurn(Verdicts(packet, _ => unit.Contains("REVERIFY", StringComparison.Ordinal)
                    && (!secondCycle || unit.Contains("CYCLE-2", StringComparison.Ordinal)) ? "ACCEPT" : "REGROUP"), [], "stop");
            var current = Interlocked.Increment(ref active);
            int seen;
            do { seen = Volatile.Read(ref maximum); } while (current > seen && Interlocked.CompareExchange(ref maximum, current, seen) != seen);
            try
            {
                if (Interlocked.Increment(ref groupCalls) <= 2)
                {
                    firstTwo.Signal();
                    Assert.True(firstTwo.Wait(TimeSpan.FromSeconds(5)), "Independent regroup calls were serialized.");
                }
                return new ModelTurn(JsonSerializer.Serialize(new
                {
                    protocolVersion = AgentGrouping.GroupVersion,
                    runs = packet["runs"]!.AsArray().Select(r => new
                    {
                        run = r!["run"]!.GetValue<string>(),
                        headings = new[] { r["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray() }
                    })
                }), [], "stop");
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var result = AgentGroupVerification.Verify(source, input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new AgentBudget(100, 100), 2, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Failures);
        Assert.Equal(secondCycle ? 8 : 4, groupCalls);
        Assert.Equal(2, maximum);
        Assert.Equal(expected, result.Groups.Select(g => g.ToArray()));
        Assert.Equal(secondCycle ? 8 : 4, result.Observation.BoundaryDisputes);
        Assert.Equal(4, result.Observation.VerifiedRuns);
        Assert.Contains(result.Provenance, p => p.Decision == "REGROUP" && p.Membership == "HEADING");
        Assert.Contains(result.Provenance, p => p.UnitId.StartsWith("VERIFY-REVERIFY", StringComparison.Ordinal) && p.Decision == "ACCEPT");
        Assert.Equal(secondCycle ? 2 : 1, result.Provenance.Max(p => p.BoundaryRepairCycle));
    }

    [Theory]
    [InlineData("ACCEPT", false)]
    [InlineData("ACCEPT", true)]
    [InlineData("REGROUP", false)]
    [InlineData("MEMBERSHIP_DISPUTE", false)]
    [InlineData("UNRESOLVED", false)]
    public void Second_semantic_boundary_repair_is_bounded_and_never_overrides_changed_membership(string terminal, bool protocolRepair)
    {
        var groupCalls = 0;
        var verificationCalls = 0;
        var aliases = new[] { A(1), A(2), A(3) };
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                var decision = ++verificationCalls <= 2 ? "REGROUP" : terminal;
                return new ModelTurn(Verdicts(packet, _ => decision), [], "stop");
            }
            Assert.Equal("GROUP", packet["stage"]!.GetValue<string>());
            groupCalls++;
            if (protocolRepair && groupCalls % 2 == 1) return new ModelTurn("{}", [], "stop");
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(), headings = new[] { aliases } })
            }), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([aliases], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(100, 100),
            1, CancellationToken.None, 0, Instruction, allowMembershipReview: false);
        Assert.Equal(protocolRepair ? 4 : 2, groupCalls);
        Assert.Equal(3, verificationCalls);
        Assert.All(result.Provenance.Where(p => p.Stage == "verification-final" && p.BoundaryRepairCycle == 2),
            p => Assert.Equal(terminal, p.Decision));
        Assert.Equal(new[] { 0, 1, 2 }, result.Provenance.Where(p => p.Stage == "verification-final").Select(p => p.BoundaryRepairCycle).Distinct());
        if (terminal == "ACCEPT")
        {
            Assert.Single(result.Groups);
            Assert.Empty(result.UnresolvedAliases);
            Assert.Empty(result.Failures);
        }
        else
        {
            Assert.Empty(result.Groups);
            Assert.Equal(aliases, result.UnresolvedAliases);
            Assert.All(aliases, a => Assert.Equal(terminal == "MEMBERSHIP_DISPUTE" ? "NOT_HEADING" : terminal == "UNRESOLVED" ? "UNRESOLVED" : "HEADING", result.Memberships[a]));
        }
    }

    [Fact]
    public void Membership_refinement_does_not_reset_the_semantic_boundary_cycle_limit()
    {
        var groupCalls = 0;
        var verificationCalls = 0;
        var aliases = new[] { A(1), A(2), A(3) };
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                var decision = ++verificationCalls == 3 ? "MEMBERSHIP_DISPUTE" : "REGROUP";
                return new ModelTurn(Verdicts(packet, _ => decision, membershipStatus:
                    a => decision == "MEMBERSHIP_DISPUTE" && a == A(2) ? "NOT_HEADING" : "HEADING"), [], "stop");
            }
            Assert.Equal("GROUP", packet["stage"]!.GetValue<string>());
            groupCalls++;
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(), headings = new[] { aliases } }) }), [], "stop");
        };
        var result = AgentGroupVerification.Verify(Source(), new AgentGrouping.Result([aliases], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(100, 100),
            1, CancellationToken.None, 0, Instruction, allowMembershipReview: false);
        Assert.Equal(2, groupCalls);
        Assert.Equal(4, verificationCalls);
        Assert.Equal("NOT_HEADING", result.Memberships[A(2)]);
        Assert.Empty(result.Groups);
        Assert.Equal(aliases.Order(), result.UnresolvedAliases.Order());
        Assert.Equal(2, result.Provenance.Max(p => p.BoundaryRepairCycle));
        Assert.DoesNotContain(result.Provenance.Where(p => p.Stage == "grouping" && p.BoundaryRepairCycle == 2), p => p.Decision == "FALLBACK");
    }
    [Fact]
    public void Many_candidates_can_use_both_boundary_cycles_without_borrowing_verifier_reserve()
    {
        const int candidateCount = 24, lineCount = candidateCount * 3;
        var source = Source(lineCount: lineCount);
        var input = new AgentGrouping.Result(Enumerable.Range(0, candidateCount)
            .Select(i => new[] { A(3 * i + 1), A(3 * i + 2) }).ToArray(), [], [], 0);
        var reservedPosts = HeadingAgentSelector.SemanticBoundaryGroupingPostsReserveForLines(lineCount);
        Assert.Equal(2 * 2 * (lineCount / 2) * 6, reservedPosts);
        // This second ledger admits GROUP requests only: verification cannot subsidize them.
        var groupingOnly = new AgentBudget(reservedPosts,
            reservedPosts * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes));
        var documentBudget = HeadingAgentSelector.BudgetFor(source);
        var units = source.Cores.Count;
        var selectionPosts = units * (1 + MaxToolTurns + AgentUnit.TransportRetries + 2 * 4);
        var verificationPosts = 2 * (1 + AgentGroupVerification.MaxBoundaryRepairCycles)
            * ((lineCount + AgentGroupVerification.BatchSize - 1) / AgentGroupVerification.BatchSize) * AgentGroupVerification.MaxCalls;
        Assert.Equal(selectionPosts + HeadingAgentSelector.GroupingPostsReserveForLines(lineCount)
            + units * AgentSelectionReview.MaxPosts + verificationPosts + reservedPosts, documentBudget.MaxCalls);
        for (var pass = 0; pass < 2; pass++)
        {
            var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            AgentUnit.Model model = (unit, _, request) =>
            {
                var packet = Payload(request);
                if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
                    return new ModelTurn(Verdicts(packet, _ => unit.Contains("CYCLE-2", StringComparison.Ordinal)
                        ? "ACCEPT" : "REGROUP"), [], "stop");
                Assert.Null(groupingOnly.Admit(request));
                var attempt = attempts.AddOrUpdate(unit, 1, (_, n) => n + 1);
                if (attempt <= AgentUnit.TransportRetries) throw new ReplyFailure("TRANSPORT:STREAM_ERROR");
                if (attempt <= AgentUnit.TransportRetries + AgentGrouping.MaxRepairs)
                    return new ModelTurn("{}", [], "stop");
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                    runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                        headings = new[] { r["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray() } }) }), [], "stop");
            };
            var result = AgentGroupVerification.Verify(source, input, new Dictionary<string, TextOffsetSpan>(),
                new Dictionary<string, string>(), model, documentBudget, 4, CancellationToken.None, 0, Instruction);
            Assert.Empty(result.Failures);
            Assert.Empty(result.UnresolvedAliases);
            Assert.Equal(candidateCount, result.Groups.Count);
            Assert.Equal(candidateCount * 2, attempts.Count);
            Assert.All(attempts.Values, n => Assert.Equal(6, n));
            Assert.Equal(2, result.Provenance.Max(p => p.BoundaryRepairCycle));
        }
        Assert.Equal(2 * candidateCount * 2 * 6, groupingOnly.Calls);
        Assert.Null(groupingOnly.HaltReason);
        Assert.Null(documentBudget.HaltReason);
        Assert.InRange(groupingOnly.ReservedUsd, 0, groupingOnly.CeilingUsd);
    }

    // Source functions copied from the inspected stored page 32, not a heading Gold or a production phrase filter.
    static ScanAgentSource Source(string? entry = null, string? imageSha = null, int lineCount = 14, string[]? sourceTexts = null)
    {
        string[] texts = sourceTexts ?? ["Chương II", "QUY ĐỊNH CHUNG", "Điều 3. Tên điều", "tiếp nối tên điều",
            "nội dung", "nội dung", "nội dung", "nội dung", "c) Sổ Chứng thực chữ ký người dịch",
            "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", "Độc lập - Tự do - Hạnh phúc", "SỔ CHỨNG THỰC CHỮ KÝ NGƯỜI DỊCH",
            "d) Sổ Chứng thực hợp đồng, giao dịch", "nội dung"];
        var lines = texts.Concat(Enumerable.Repeat("context", Math.Max(0, lineCount - texts.Length))).Select((t, i) => new OcrLine($"P032-ocr-original-L{i + 1:000}", t,
            new PixelBox(30, 20 + i * 30, 340, 20), 95, 95, false, [])).ToArray();
        var view = new OcrView(new OcrViewSpec("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
            [new OcrPage(32, imageSha ?? new string('a', 64), 420, 500, new string('b', 64), lines)]);
        return new ScanAgentSource(new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedPdf", 1,
            new string('d', 64), [view], []), evidenceEntry: entry);
    }

    static JsonObject Payload(PreparedRequest request)
    {
        var content = JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!;
        return JsonNode.Parse(content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>())!.AsObject();
    }

    static string Verdicts(JsonObject packet, Func<JsonNode, string> decide, bool invalidRefs = false, Func<string, string>? membershipStatus = null) => JsonSerializer.Serialize(new
    {
        protocolVersion = AgentGroupVerification.Version,
        verdicts = packet["candidates"]!.AsArray().Select(c =>
        {
            var decision = decide(c!);
            return new
            {
                run = c!["run"]!.GetValue<string>(), decision, reason = "Independent scripted source-based judgement.",
                evidenceRefs = new[] { invalidRefs ? "UNKNOWN" : c["proposedGroups"]![0]![0]!.GetValue<string>() },
                membership = c["proposedGroups"]!.AsArray().SelectMany(g => g!.AsArray()).Select(a => new
                {
                    alias = a!.GetValue<string>(),
                    status = membershipStatus?.Invoke(a.GetValue<string>()) ?? (decision == "MEMBERSHIP_DISPUTE" ? "NOT_HEADING" : decision == "UNRESOLVED" ? "UNRESOLVED" : "HEADING"),
                    evidenceRefs = new[] { a.GetValue<string>() }
                })
            };
        })
    });

    [Theory]
    [InlineData("boundary")]
    [InlineData("membership")]
    [InlineData("repeated-boundary")]
    public void Accepted_but_wrong_form_group_is_checked_without_a_grouping_dispute(string mode)
    {
        var source = Source();
        var groupCalls = 0; var selectionReviews = 0; var verificationCalls = 0;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var packet = Payload(request);
            var stage = packet["stage"]!.GetValue<string>();
            if (stage == "GROUP_VERIFICATION")
            {
                verificationCalls++;
                Assert.True(packet["candidates"]![0]!["targetFacts"]!.AsArray().Count > 0);
                var firstPass = !unit.Contains("REVERIFY", StringComparison.Ordinal) && !unit.StartsWith("REVIEW-", StringComparison.Ordinal);
                return new ModelTurn(Verdicts(packet, c =>
                    c["proposedGroups"]!.ToJsonString().Contains(A(10), StringComparison.Ordinal) &&
                    (firstPass || mode == "repeated-boundary")
                        ? mode == "membership" ? "MEMBERSHIP_DISPUTE" : "REGROUP" : "ACCEPT"), [], "stop");
            }
            if (stage == "GROUP")
            {
                groupCalls++;
                var runs = packet["runs"]!.AsArray().Select(r =>
                {
                    var aliases = r!["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray();
                    var headings = aliases.Contains(A(1)) ? new[] { new[] { A(1), A(2) }, new[] { A(3), A(4) } }
                        : unit == "GROUP-001" ? new[] { new[] { A(9) }, new[] { A(10), A(11), A(12) }, new[] { A(13) } }
                        : aliases.Select(a => new[] { a }).ToArray();
                    return new { run = r["run"]!.GetValue<string>(), headings };
                });
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs }), [], "stop");
            }
            var reviewing = packet["selectionReview"] is not null;
            if (reviewing) selectionReviews++;
            var decisions = packet["targets"]!.AsArray().Select(t =>
            {
                var alias = t!["alias"]!.GetValue<string>();
                var select = new[] { A(1), A(2), A(3), A(4), A(9), A(13) }.Contains(alias) || !reviewing && new[] { A(10), A(11), A(12) }.Contains(alias);
                return new { alias, decision = select ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias }, reason = "Scripted membership judgement." };
            });
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = SelectScanVersion, status = "COMPLETE", decisions }), [], "stop");
        };
        var result = HeadingAgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Equal(new[] { "selection", "grouping", "verification", "review" }, result.StageSeconds!.Keys);
        Assert.All(result.StageSeconds.Values, seconds => Assert.True(seconds >= 0));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(1), A(2) }));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(3), A(4) }));
        Assert.DoesNotContain(result.Groups!, g => g.Contains(A(10)) && g.Contains(A(12)));
        Assert.True(verificationCalls >= 2);
        Assert.Equal(0, result.Grouping!.FallbackRuns);
        Assert.Equal(mode == "membership" ? 1 : 0, selectionReviews);
        if (mode == "membership") Assert.DoesNotContain(A(10), result.SelectedAliases);
        if (mode == "repeated-boundary")
        {
            Assert.Equal(5, result.Grouping.UnresolvedAliases);
            Assert.Contains(result.UnitFailures, f => f.Contains("UNRESOLVED", StringComparison.Ordinal));
            Assert.Equal(0, result.UnitsComplete);
        }
        else Assert.Empty(result.UnitFailures);
        Assert.Equal(mode == "membership" ? 1 : mode == "repeated-boundary" ? 3 : 2, groupCalls);
    }

    [Theory]
    [InlineData("unknown-reference")]
    [InlineData("missing-verdict")]
    [InlineData("unresolved")]
    [InlineData("budget")]
    public void Unverifiable_groups_are_withheld_and_never_claimed_as_layout_fallback(string mode)
    {
        var source = Source();
        var input = new AgentGrouping.Result([new[] { A(10), A(11), A(12) }], [], [], 0);
        var budget = new AgentBudget(20, 100);
        if (mode == "budget") budget.Halt("TEST");
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            calls++;
            var response = mode == "missing-verdict" ? "{\"protocolVersion\":\"" + AgentGroupVerification.Version + "\",\"verdicts\":[]}"
                : Verdicts(Payload(request), _ => mode == "unresolved" ? "UNRESOLVED" : "ACCEPT", mode == "unknown-reference");
            return new ModelTurn(response, [], "stop");
        };
        var result = AgentGroupVerification.Verify(source, input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { A(10), A(11), A(12) }, result.UnresolvedAliases);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, result.Observation.FallbackRuns);
        Assert.InRange(calls, 0, AgentGroupVerification.MaxPosts);
    }

    [Fact]
    public void Verifier_can_request_a_hashed_scan_crop_and_read_only_context_before_deciding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "pages"));
        try
        {
            var png = TestPng.WithBlack(420, 500, 255, (40, 300, 80, 10));
            File.WriteAllBytes(Path.Combine(directory, "pages", "page-032.png"), png);
            var source = Source(directory, Convert.ToHexStringLower(SHA256.HashData(png)));
            var calls = 0;
            AgentUnit.Model model = (_, _, request) =>
            {
                calls++;
                if (calls == 1) return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                    requests = new object[] { new { name = "get_scan_crop", arguments = new { aliases = new[] { A(9), A(10), A(11), A(12), A(13) } } },
                        new { name = "get_source_context", arguments = new { alias = A(10), before = 2, after = 4 } } } }), [], "stop");
                var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
                var parts = messages.Last()!["content"]!.AsArray();
                Assert.Contains(parts, p => p!["type"]!.GetValue<string>() == "image_url");
                var url = parts.Single(p => p!["type"]!.GetValue<string>() == "image_url")!["image_url"]!["url"]!.GetValue<string>();
                var raster = PngRaster.TryDecode(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]))!;
                Assert.Contains((byte)0, raster.Grey);
                Assert.Contains((byte)255, raster.Grey);
                Assert.Contains("cropSha256", parts[0]!["text"]!.GetValue<string>());
                return new ModelTurn(Verdicts(Payload(request), _ => "UNRESOLVED"), [], "stop");
            };
            var result = AgentGroupVerification.Verify(source, new AgentGrouping.Result([new[] { A(10), A(11), A(12) }], [], [], 0),
                new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(10, 100), 1, CancellationToken.None, 0, Instruction);
            Assert.Equal(2, calls);
            Assert.Contains(result.Evidence, e => e.Contains("cropSha256", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Evidence, e => e.Contains("data:image", StringComparison.Ordinal));
            Assert.Empty(result.Groups);
        }
        finally { Directory.Delete(directory, true); }
    }
}
