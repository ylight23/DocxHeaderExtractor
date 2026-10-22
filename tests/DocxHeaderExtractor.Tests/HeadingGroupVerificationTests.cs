using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
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
            Assert.Contains("JSON object", body["messages"]![0]!["content"]!.Text());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = FakeOpenRouter.VerificationResponse(body) }, "stop"));
        }, autoVerify: false);
        var budget = new AgentBudget(10, 100);
        using var model = new AgentModel(handler, "sk-test", budget);
        var result = GroupVerifier.Verify(source, new AgentGrouping.Result([new[] { A(1), A(2) }], [], [], 0),
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
                var repair = JsonNode.Parse(request.Body)!["messages"]!.AsArray().Last()!["content"]!.Text();
                Assert.Contains(diagnostic, repair);
                Assert.Contains("must not be resubmitted", repair);
            }
            return new ModelTurn(response.ToJsonString(), [], "stop");
        };
        var input = EightCandidates();
        var result = GroupVerifier.Verify(Source(lineCount: 16), input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
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
    [InlineData("ACCEPT")]
    [InlineData("MEMBERSHIP_DISPUTE")]
    [InlineData("persistent-omission")]
    public void Coverage_repair_identifies_missing_later_group_members_without_using_currentGroups_as_authority(string outcome)
    {
        // Synthetic shape replay of the reported four-alias/two-group failure; not a model replay or Gold.
        string[][] groups = [[A(1), A(2)], [A(3), A(4)]];
        var candidates = new[] { new GroupVerifier.Candidate("V41", groups),
            new GroupVerifier.Candidate("V42", [[A(6)]]) };
        var calls = 0;
        AgentUnit.Model model = (unit, _, request) =>
        {
            Assert.Equal("VERIFY-006", unit);
            var packet = Payload(request);
            calls++;
            if (calls > 1)
            {
                Assert.Equal("V41", Assert.Single(packet["candidates"]!.AsArray())!["run"]!.GetValue<string>());
                var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
                var feedback = messages.Last()!["content"]!.Text();
                var begin = feedback.IndexOf('{');
                var end = feedback.IndexOf("}. Previously accepted", StringComparison.Ordinal);
                var diagnostics = JsonNode.Parse(feedback[begin..(end + 1)])!;
                Assert.Equal(new[] { "V41" }, diagnostics["pendingCandidates"]!.Deserialize<string[]>());
                var coverage = Assert.Single(diagnostics["membershipCoverage"]!.AsArray())!;
                Assert.Equal("V41", coverage["run"]!.GetValue<string>());
                Assert.Equal(new[] { A(1), A(2), A(3), A(4) }, coverage["expectedAliases"]!.Deserialize<string[]>());
                Assert.Equal(new[] { A(1), A(2) }, coverage["observedAliases"]!.Deserialize<string[]>());
                Assert.Equal(new[] { A(3), A(4) }, coverage["missingAliases"]!.Deserialize<string[]>());
                Assert.Empty(coverage["unexpectedAliases"]!.AsArray());
                Assert.Empty(coverage["duplicateAliases"]!.AsArray());
                Assert.False(coverage["sequenceMatches"]!.GetValue<bool>());
                Assert.Contains("across ALL proposedGroups", feedback);
                Assert.Contains("currentGroups does not supply membership", feedback);
                Assert.Contains("coverage repair never requires HEADING or ACCEPT", feedback);
            }
            var response = JsonNode.Parse(Verdicts(packet,
                c => calls > 1 && outcome == "MEMBERSHIP_DISPUTE" ? "MEMBERSHIP_DISPUTE" : "ACCEPT",
                membershipStatus: a => calls > 1 && outcome == "MEMBERSHIP_DISPUTE" && (a == A(3) || a == A(4)) ? "UNRESOLVED" : "HEADING"))!;
            var target = response["verdicts"]!.AsArray().Single(r => r!["run"]!.GetValue<string>() == "V41")!;
            target["currentGroups"] = JsonSerializer.SerializeToNode(groups); // Full graph never substitutes for membership.
            if (calls == 1 || outcome == "persistent-omission")
            {
                var membership = target["membership"]!.AsArray();
                membership.RemoveAt(3); membership.RemoveAt(2);
            }
            return new ModelTurn(response.ToJsonString(), [], "stop");
        };
        var result = GroupVerifier.Ask(Source(), candidates, new Dictionary<string, TextOffsetSpan>(),
            new Dictionary<string, string>(), model, new AgentBudget(20, 100), CancellationToken.None,
            0, Instruction, "VERIFY-006", 0);
        Assert.Equal(outcome == "persistent-omission" ? GroupVerifier.MaxPosts : 2, calls);
        Assert.Equal("ACCEPT", result.Verdicts.Single(v => v.Run == "V42").Decision);
        if (outcome == "persistent-omission")
        {
            Assert.Single(result.Verdicts);
            Assert.Contains("MEMBERSHIP_COVERAGE_INVALID", result.Failure);
            Assert.All(result.CoverageDiagnostics.Where(d => d.Run == "V41"), d => Assert.False(d.SequenceMatches));
        }
        else
        {
            Assert.Null(result.Failure);
            var verdict = result.Verdicts.Single(v => v.Run == "V41");
            Assert.Equal(outcome, verdict.Decision);
            Assert.Equal(new[] { A(1), A(2), A(3), A(4) }, verdict.Membership.Select(m => m.Alias));
            Assert.All(verdict.Membership.Skip(2), m => Assert.Equal(outcome == "ACCEPT" ? "HEADING" : "UNRESOLVED", m.Status));
            Assert.True(result.CoverageDiagnostics.Last().SequenceMatches);
        }
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
        var result = GroupVerifier.Verify(Source(lineCount: 16), input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
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
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
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
        var repair = JsonNode.Parse(requests[1])!["messages"]!.AsArray().Last()!["content"]!.Text();
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
        var result = GroupVerifier.Verify(source, new AgentGrouping.Result([new[] { A(2), A(3) }], [], [], 0),
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
        var result = GroupVerifier.Verify(Source(lineCount: 16), EightCandidates(), new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(7, result.Groups.Count);
        Assert.Equal(new[] { A(7) }, result.UnresolvedAliases);
        Assert.Equal(7, result.Observation.VerifiedRuns);
        Assert.Equal("HEADING", result.Memberships[A(1)]);
        Assert.False(result.Memberships.ContainsKey(A(7)));
        Assert.Empty(result.Disputes);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(mode == "invalid" ? GroupVerifier.MaxPosts : mode == "transport" ? 2 + GroupVerifier.TransportRetries : 1, calls);

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
            if (requests.Count <= GroupVerifier.TransportRetries) throw new ReplyFailure(failure);
            return new ModelTurn(Verdicts(Payload(request), _ => "ACCEPT"), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(retry ? 1 + GroupVerifier.TransportRetries : 1, requests.Count);
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
        AgentGrouping.Result Run() => GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
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
        Assert.Equal(mode == "exhausted" ? 1 + GroupVerifier.TransportRetries : 1, calls);
    }

    [Fact]
    public void Real_stream_failure_is_retried_without_reading_partial_content_as_a_verdict()
    {
        using var handler = new FakeOpenRouter((body, number) => (HttpStatusCode.OK,
            number == 1 ? "data: {\"error\":{\"message\":\"temporary\"}}\n\ndata: [DONE]\n\n"
                : FakeOpenRouter.Stream(new { content = FakeOpenRouter.VerificationResponse(body) }, "stop")), autoVerify: false);
        var budget = new AgentBudget(10, 100);
        using var model = new AgentModel(handler, "sk-test", budget);
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
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
            return new ModelTurn("{\"protocolVersion\":\"" + GroupVerifier.Version + "\",\"verdicts\":[]}", [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Equal(GroupVerifier.MaxCalls, calls);
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
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }], [], [], 0),
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
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1), A(2), A(3) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100), 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Empty(result.Disputes);
        Assert.Empty(result.Memberships);
        Assert.Equal(GroupVerifier.MaxPosts, calls);
        Assert.Contains(result.Failures, f => f.Contains("PROTOCOL:VERIFICATION_INVALID", StringComparison.Ordinal));
        if (mode is "missing" or "duplicate" or "out-of-order" or "unknown-alias")
        {
            var coverage = Assert.Single(result.Observation.CoverageDiagnostics!, d => d.Round == GroupVerifier.MaxPosts);
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
                    // Generous on purpose: in a loaded full run the thread pool can start the second job seconds late. Serialized calls
                    // still fail, because the first call then waits for a second one that never starts.
                    Assert.True(firstTwo.Wait(TimeSpan.FromSeconds(60)), "Independent regroup calls were serialized.");
                }
                return new ModelTurn(JsonSerializer.Serialize(new
                {
                    protocolVersion = AgentGrouping.GroupVersion,
                    runs = packet["runs"]!.AsArray().Select(r => new
                    {
                        run = r!["run"]!.GetValue<string>(),
                        headings = unit.Contains("CYCLE-2", StringComparison.Ordinal)
                            ? new[] { r["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray() }
                            : r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }).ToArray()
                    })
                }), [], "stop");
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var result = GroupVerifier.Verify(source, input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new AgentBudget(100, 100), 2, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Failures);
        Assert.Equal(secondCycle ? 8 : 4, groupCalls);
        Assert.Equal(groupCalls, result.Observation.BoundaryRepairs!.Count);
        Assert.All(result.Observation.BoundaryRepairs, r => Assert.Equal("CHANGED", r.Result));
        Assert.Equal(2, maximum);
        Assert.Equal(secondCycle ? expected : expected.SelectMany(g => g.Select(a => new[] { a })).ToArray(), result.Groups.Select(g => g.ToArray()));
        Assert.Equal(secondCycle ? 8 : 4, result.Observation.BoundaryDisputes);
        Assert.Equal(4, result.Observation.VerifiedRuns);
        Assert.Contains(result.Provenance, p => p.Decision == "REGROUP" && p.Membership == "HEADING");
        Assert.Contains(result.Provenance, p => p.UnitId.StartsWith("VERIFY-REVERIFY", StringComparison.Ordinal) && p.Decision == "ACCEPT");
        Assert.Equal(secondCycle ? 2 : 1, result.Provenance.Max(p => p.BoundaryRepairCycle));
        Assert.Equal(secondCycle ? 12 : 8, result.Observation.Partitions!.Count);
        Assert.All(result.Observation.Partitions.GroupBy(p => p.CandidateKey), trajectory =>
        {
            Assert.Equal(secondCycle ? 3 : 2, trajectory.Count());
            Assert.All(trajectory.Skip(1), p => Assert.True(p.ChangedFromInput));
        });
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
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                    headings = verificationCalls == 1 ? new[] { new[] { aliases[0] }, aliases[1..] }
                        : new[] { aliases[..2], new[] { aliases[2] } } })
            }), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([aliases], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(100, 100),
            1, CancellationToken.None, 0, Instruction, allowMembershipReview: false);
        Assert.Equal(protocolRepair ? 4 : 2, groupCalls);
        Assert.Equal(3, verificationCalls);
        Assert.All(result.Provenance.Where(p => p.Stage == "verification-final" && p.BoundaryRepairCycle == 2),
            p => Assert.Equal(terminal, p.Decision));
        Assert.Equal(new[] { 0, 1, 2 }, result.Provenance.Where(p => p.Stage == "verification-final").Select(p => p.BoundaryRepairCycle).Distinct());
        if (terminal == "ACCEPT")
        {
            Assert.Equal(2, result.Groups.Count);
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

    [Theory]
    [InlineData(0, false, 2)]
    [InlineData(0, true, 2)]
    [InlineData(1, false, 2)]
    [InlineData(0, false, 3)]
    public void Unchanged_valid_model_repair_recovers_only_after_fresh_verifier_accepts_and_keeps_independent_headings(int fromCycle, bool protocolRepair, int acceptAtCall)
    {
        var calls = 0; var groupCalls = 0;
        string[][] rejected = [[A(1)], [A(2)]];
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                calls++;
                return new ModelTurn(Verdicts(packet, c => calls >= acceptAtCall || c["proposedGroups"]![0]![0]!.GetValue<string>() == A(5) ? "ACCEPT" : "REGROUP"), [], "stop");
            }
            if (++groupCalls == 1 && protocolRepair) return new ModelTurn("{}", [], "stop");
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(), headings = rejected }) }), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([.. rejected, new[] { A(5) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(50, 100),
            1, CancellationToken.None, 0, Instruction, boundaryRepairCycle: fromCycle);
        Assert.Equal(acceptAtCall, calls);
        Assert.Equal(acceptAtCall - 1 + (protocolRepair ? 1 : 0), groupCalls);
        Assert.Empty(result.UnresolvedAliases);
        Assert.Equal(JsonSerializer.Serialize(new[] { new[] { A(1) }, new[] { A(2) }, new[] { A(5) } }), JsonSerializer.Serialize(result.Groups));
        Assert.Equal("HEADING", result.Memberships[A(1)]);
        Assert.Empty(result.Failures);
        Assert.Equal(acceptAtCall - 1, result.Observation.BoundaryRepairs!.Count);
        Assert.All(result.Observation.BoundaryRepairs, repair =>
        {
            Assert.Null(repair.FailureClass);
            Assert.Equal("NO_PROGRESS", repair.Result);
            Assert.False(repair.Changed);
            Assert.InRange(repair.FromCycle, fromCycle, fromCycle + acceptAtCall - 2);
            Assert.Equal(repair.FromCycle + 1, repair.ToCycle);
            Assert.Equal(JsonSerializer.Serialize(repair.InputGroups), JsonSerializer.Serialize(repair.OutputGroups));
        });
        Assert.Equal(acceptAtCall + 1, result.Observation.Partitions!.Count); // Two initial candidates plus fresh bounded verification.
        Assert.Equal(fromCycle + acceptAtCall - 1, result.Observation.Partitions.Last().BoundaryRepairCycle);
        Assert.Contains(result.Provenance, p => p.Stage == "boundary-repair-result" && p.Decision == "NO_PROGRESS"
            && p.BoundaryRepairCycle == fromCycle + 1);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("disputed")]
    [InlineData("protocol-failure")]
    [InlineData("scope-changed")]
    [InlineData("no-model-division")]
    public void No_progress_observation_excludes_non_model_divisions_failures_disputes_and_scope_changes(string mode)
    {
        string[][] groups = [[A(1)], [A(2)]];
        var input = new GroupVerifier.Candidate("V9", groups);
        var result = new AgentGrouping.Result(groups, [], [], 1)
        {
            Observation = new(ModelDividedRuns: 1),
            Provenance = groups.SelectMany(g => g).Select(a => new HeadingAliasProvenance(a, "grouping", "G-001", "R1", "DIVIDED", null, [], [])).ToArray()
        };
        result = mode switch
        {
            "fallback" => result with { Observation = new(FallbackRuns: 1) },
            "disputed" => result with { Disputes = [new("R1", input.Aliases, "Model dispute")] },
            "protocol-failure" => result with { Failures = ["PROTOCOL:REPLY_NOT_JSON"] },
            "scope-changed" => result with { Groups = [new[] { A(1) }] },
            _ => result with { Provenance = [], Observation = new() }
        };
        var observation = GroupVerifier.ObserveBoundaryRepair("VERIFY-REGROUP-V9", "VERIFY-002", input, 0, result);
        Assert.Equal(mode == "disputed" ? "DISPUTED" : "FAILED", observation.Result);
        Assert.NotEqual("NO_PROGRESS", observation.Result);
        if (mode == "scope-changed") Assert.Null(observation.Changed);
    }

    [Fact]
    public void Cancellation_after_unchanged_grouping_does_not_return_a_no_progress_result()
    {
        using var cancellation = new CancellationTokenSource();
        var verificationCalls = 0; var groupingCalls = 0;
        string[][] groups = [[A(1)], [A(2)]];
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                verificationCalls++;
                return new ModelTurn(Verdicts(packet, _ => "REGROUP"), [], "stop");
            }
            groupingCalls++;
            cancellation.Cancel();
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(), headings = groups }) }), [], "stop");
        };
        Assert.ThrowsAny<OperationCanceledException>(() => GroupVerifier.Verify(Source(), new AgentGrouping.Result(groups, [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(50, 100),
            1, cancellation.Token, 0, Instruction));
        Assert.Equal(1, verificationCalls);
        Assert.Equal(1, groupingCalls);
    }

    [Fact]
    public void Unchanged_layout_fallback_after_protocol_failure_still_uses_the_existing_verifier_path()
    {
        var verificationCalls = 0; var groupingCalls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
                return new ModelTurn(Verdicts(packet, _ => ++verificationCalls == 1 ? "REGROUP" : "ACCEPT"), [], "stop");
            groupingCalls++;
            return new ModelTurn("{}", [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }, new[] { A(2) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(50, 100),
            1, CancellationToken.None, 0, Instruction);
        Assert.Equal(2, verificationCalls);
        Assert.Equal(1 + AgentGrouping.MaxRepairs, groupingCalls);   // a repeated refused reply is still repaired to the limit
        Assert.Equal(2, result.Groups.Count);
        Assert.Empty(result.UnresolvedAliases);
        Assert.DoesNotContain(result.Failures, f => f.Contains("BOUNDARY_REPAIR_NO_PROGRESS", StringComparison.Ordinal));
        var repair = Assert.Single(result.Observation.BoundaryRepairs!);
        Assert.Equal("FAILED", repair.Result);
        Assert.False(repair.Changed); // Equality alone is deliberately insufficient.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Partition_trajectory_compares_ordered_boundaries_and_joins_aliases_despite_changed_candidate_IDs(bool firstChange, bool secondChange)
    {
        string[][] original = [[A(3), A(4)], [A(5)]];
        string[][] revised = [[A(3)], [A(4), A(5)]];
        var groupCalls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
                return new ModelTurn(Verdicts(packet, c => c["proposedGroups"]![0]![0]!.GetValue<string>() == A(1) ? "ACCEPT" : "REGROUP"), [], "stop");
            var output = ++groupCalls == 2 && secondChange ? new[] { new[] { A(3), A(4), A(5) } } : firstChange ? revised : original;
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(), headings = output }) }), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([new[] { A(1) }, .. original], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(100, 100),
            1, CancellationToken.None, 0, Instruction);
        var trajectory = result.Observation.Partitions!.Where(p => p.CandidateAliases.Contains(A(3))).ToArray();
        var expectedCycles = new[] { 0, 1, 2 };
        Assert.Equal(expectedCycles, trajectory.Select(p => p.BoundaryRepairCycle));
        Assert.Equal(expectedCycles.Select(c => c == 0 ? "V2" : "V1"), trajectory.Select(p => p.CandidateId));
        Assert.Single(trajectory.Select(p => p.CandidateKey).Distinct());
        Assert.All(trajectory, p => Assert.Equal(new[] { A(3), A(4), A(5) }, p.CandidateAliases));
        Assert.Null(trajectory[0].InputGroups);
        Assert.Null(trajectory[0].ChangedFromInput);
        Assert.Equal(firstChange, trajectory[1].ChangedFromInput);
        Assert.Equal(secondChange, trajectory[2].ChangedFromInput);
        for (var i = 1; i < trajectory.Length; i++)
            Assert.Equal(JsonSerializer.Serialize(trajectory[i - 1].OutputGroups), JsonSerializer.Serialize(trajectory[i].InputGroups));
        Assert.Equal(2, groupCalls);
        Assert.DoesNotContain(result.Groups, g => g.Contains(A(3)));
        Assert.Equal(new[] { A(3), A(4), A(5) }, result.UnresolvedAliases);
        var terminalRepair = result.Observation.BoundaryRepairs!.Last();
        Assert.Equal(firstChange && secondChange ? "CHANGED" : "NO_PROGRESS", terminalRepair.Result);
        Assert.Equal(firstChange && secondChange, terminalRepair.Changed);
        Assert.All(result.Observation.VerifierEvidence!, e =>
        {
            Assert.Empty(e.Requests);
            Assert.False(e.UsedScanCrop);
            Assert.Equal(e.InitialObservedAliases, e.ObservedAliases);
        });
        Assert.Equal(new[] { A(3), A(4), A(5) }, result.UnresolvedAliases);
        var changedScope = GroupVerifier.ObservePartition("SURVIVORS", "u", new("new-id", [[A(3)], [A(5)]]), 2, original);
        Assert.Null(changedScope.ChangedFromInput);
        Assert.NotEqual(trajectory[0].CandidateKey, changedScope.CandidateKey);
    }

    [Fact]
    public void Evidence_metadata_tracks_shared_scope_extra_context_failed_crop_and_safe_tool_names()
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) => ++calls == 1
            ? new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = GroupVerifier.Version,
                requests = new object[] {
                    new { name = "get_source_context", arguments = (object)new { alias = A(28), before = 1, after = 1 } },
                    new { name = "get_scan_crop", arguments = (object)new { aliases = new[] { A(1) } } },
                    new { name = "private-source-name", arguments = (object)new { } } } }), [], "stop")
            : new ModelTurn(Verdicts(Payload(request), _ => "ACCEPT"), [], "stop");
        var result = GroupVerifier.Verify(Source(lineCount: 40, sourceTexts: Enumerable.Repeat("PRIVATE_SOURCE_TEXT_CANARY", 40).ToArray()),
            new AgentGrouping.Result([new[] { A(1) }, new[] { A(3) }], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(20, 100),
            1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Failures);
        var observation = Assert.Single(result.Observation.VerifierEvidence!);
        Assert.Equal(2, observation.CandidateAliases.Count); // Tools belong to this shared conversation, not one candidate.
        Assert.DoesNotContain(A(28), observation.InitialObservedAliases);
        Assert.Contains(A(28), observation.ObservedAliases);
        Assert.Equal(new[] { "get_source_context", "get_scan_crop", "UNREGISTERED" }, observation.Requests.Select(r => r.ToolName));
        Assert.Contains(A(28), observation.Requests[0].ReturnedAliases);
        Assert.Equal("UNAVAILABLE", observation.Requests[1].Status);
        Assert.Empty(observation.Requests[1].ReturnedAliases);
        Assert.False(observation.UsedScanCrop);
        var safe = JsonSerializer.Serialize(result.Observation);
        Assert.DoesNotContain("private-source-name", safe);
        Assert.DoesNotContain("Independent scripted source-based judgement", safe);
        Assert.DoesNotContain("PRIVATE_SOURCE_TEXT_CANARY", safe);
        Assert.DoesNotContain("base64", safe);
    }

    [Fact]
    public void Membership_refinement_does_not_reset_the_semantic_boundary_cycle_limit()
    {
        var groupCalls = 0;
        var verificationCalls = 0;
        // Two multi-alias survivor scopes can still make valid different proposals at cycle 2.
        // Singleton REGROUP is now necessarily self-contradictory and belongs to protocol-repair tests.
        var aliases = new[] { A(1), A(2), A(3), A(4), A(5) };
        AgentUnit.Model model = (_, _, request) =>
        {
            var packet = Payload(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                var decision = ++verificationCalls == 3 ? "MEMBERSHIP_DISPUTE" : "REGROUP";
                return new ModelTurn(Verdicts(packet, _ => decision, membershipStatus:
                    a => decision == "MEMBERSHIP_DISPUTE" && a == A(3) ? "NOT_HEADING" : "HEADING"), [], "stop");
            }
            Assert.Equal("GROUP", packet["stage"]!.GetValue<string>());
            groupCalls++;
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                    headings = groupCalls == 1 ? new[] { new[] { aliases[0] }, aliases[1..] }
                        : new[] { aliases[..3], aliases[3..] } }) }), [], "stop");
        };
        var result = GroupVerifier.Verify(Source(), new AgentGrouping.Result([aliases], [], [], 0),
            new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(100, 100),
            1, CancellationToken.None, 0, Instruction, allowMembershipReview: false);
        Assert.Equal(2, groupCalls);
        Assert.Equal(4, verificationCalls);
        Assert.Equal("NOT_HEADING", result.Memberships[A(3)]);
        Assert.Empty(result.Groups);
        Assert.Equal(aliases.Order(), result.UnresolvedAliases.Order());
        Assert.Equal(2, result.Provenance.Max(p => p.BoundaryRepairCycle));
        Assert.DoesNotContain(result.Provenance.Where(p => p.Stage == "grouping" && p.BoundaryRepairCycle == 2), p => p.Decision == "FALLBACK");
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
        protocolVersion = GroupVerifier.Version,
        verdicts = packet["candidates"]!.AsArray().Select(c =>
        {
            var decision = decide(c!);
            return new
            {
                run = c!["run"]!.GetValue<string>(), decision, reason = "Independent scripted source-based judgement.",
                currentGroups = decision == "REGROUP" ? c["proposedGroups"]!.Deserialize<string[][]>() : null,
                suggestedGroups = decision == "REGROUP" ? GroundedVerifierFixture.Different(c["proposedGroups"]!.Deserialize<string[][]>()!) : null,
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
    public void Production_keeps_a_CSharp_validated_group_without_semantic_verifier_veto(string mode)
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
        var result = AgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Equal(new[] { "selection", "grouping" }, result.StageSeconds!.Keys);
        Assert.All(result.StageSeconds.Values, seconds => Assert.True(seconds >= 0));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(1), A(2) }));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(3), A(4) }));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(10), A(11), A(12) }));
        Assert.Equal(0, verificationCalls);
        Assert.Equal(0, result.Grouping!.FallbackRuns);
        Assert.Equal(0, selectionReviews);
        Assert.Contains(A(10), result.SelectedAliases);
        Assert.Empty(result.UnitFailures);
        Assert.Equal(1, groupCalls);
        Assert.Equal(0, result.Grouping.VerificationChecks);
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
            var response = mode == "missing-verdict" ? "{\"protocolVersion\":\"" + GroupVerifier.Version + "\",\"verdicts\":[]}"
                : Verdicts(Payload(request), _ => mode == "unresolved" ? "UNRESOLVED" : "ACCEPT", mode == "unknown-reference");
            return new ModelTurn(response, [], "stop");
        };
        var result = GroupVerifier.Verify(source, input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { A(10), A(11), A(12) }, result.UnresolvedAliases);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, result.Observation.FallbackRuns);
        Assert.InRange(calls, 0, GroupVerifier.MaxPosts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verifier_can_request_a_hashed_scan_crop_and_read_only_context_before_deciding(bool exhaustBudgetAfterEvidence)
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
                if (calls == 1) return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = GroupVerifier.Version,
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
            var result = GroupVerifier.Verify(source, new AgentGrouping.Result([new[] { A(10), A(11), A(12) }], [], [], 0),
                new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model,
                new AgentBudget(exhaustBudgetAfterEvidence ? 1 : 10, 100), 1, CancellationToken.None, 0, Instruction);
            Assert.Equal(exhaustBudgetAfterEvidence ? 1 : 2, calls);
            Assert.Contains(result.Evidence, e => e.Contains("cropSha256", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Evidence, e => e.Contains("data:image", StringComparison.Ordinal));
            Assert.Empty(result.Groups);
            var observations = result.Observation.VerifierEvidence!;
            Assert.All(observations, o => Assert.Equal(!exhaustBudgetAfterEvidence, o.UsedScanCrop));
            Assert.All(observations, o => Assert.Contains(o.Requests, t => t.ToolName == "get_scan_crop"
                && t.ScanCropReturned && t.Status == "OK" && t.ReturnedAliases.Contains(A(10))));
            Assert.DoesNotContain("data:image", JsonSerializer.Serialize(observations));
            Assert.DoesNotContain("cropSha256", JsonSerializer.Serialize(observations));
        }
        finally { Directory.Delete(directory, true); }
    }
}
