using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectPrefetchCanary;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.11h (offline): the program-read evidence (prefetch) put in the request, the CONTROL arm that equals the D4.11g AUTO
/// request byte for byte, three identical repetitions, and the plan. Scripted models and fake HTTP only. No network.</summary>
public sealed class P7F1V6DirectD411hTests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d411h-0000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d411h-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD411hTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Lazy<P7F1V6Universe> U = new(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), P7F1V6DirectD45.Document, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f));

    static string L(int i) => U.Value.Lines[i].Alias;
    static P7F1V6AgentTools NewTools() => new(U.Value.Tools);
    static IReadOnlyList<string> Decide(string segment) => P7F1V6DirectCanary410.Scope(U.Value, segment).Decide;

    static JsonElement PayloadOf(P7F1V6Protocol.PreparedRequest r)
    {
        using var d = JsonDocument.Parse(r.Body);
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static IEnumerable<string> PropertyNames(JsonNode? n) => n switch
    {
        JsonObject o => o.SelectMany(kv => PropertyNames(kv.Value).Prepend(kv.Key)),
        JsonArray a => a.SelectMany(PropertyNames),
        _ => Enumerable.Empty<string>(),
    };

    // ================= the prefetch read =================

    [Theory]
    [InlineData("SEG-001")]
    [InlineData("SEG-008")]
    [InlineData("SEG-009")]
    public void Prefetch_ListsExactlyTheTargetsWhoseTextOccursElsewhere_WithTheFirstTwoOtherOccurrencesInReadingOrder(string segment)
    {
        var decide = Decide(segment);
        var tools = NewTools();
        var r = tools.Prefetch(decide);
        var lines = U.Value.Lines;
        // Independent computation from the parser lines and the published normalization.
        var expected = new List<(int At, int[] Others)>();
        foreach (var a in decide)
        {
            var at = lines.ToList().FindIndex(l => l.Alias == a);
            var key = P7F1QEvidenceTools.RepeatKey(lines[at].Text);
            var others = key.Length == 0 ? Array.Empty<int>() : Enumerable.Range(0, lines.Count).Where(i => i != at && P7F1QEvidenceTools.RepeatKey(lines[i].Text) == key).ToArray();
            if (others.Length > 0) expected.Add((at, others));
        }
        Assert.Equal(expected.Count, r.Listed);
        Assert.Equal(decide.Count - expected.Count, r.WithoutOther);
        Assert.Equal(0, r.Omitted);
        Assert.True(r.Observations["complete"]!.GetValue<bool>());
        var listed = r.Observations["targets"]!.AsArray();
        Assert.Equal(expected.Select(e => lines[e.At].Alias), listed.Select(t => t!["alias"]!.GetValue<string>()));
        for (int k = 0; k < expected.Count; k++)
        {
            var t = listed[k]!.AsObject();
            Assert.Equal(expected[k].Others.Length, t["otherOccurrences"]!.GetValue<int>());
            Assert.Equal(expected[k].Others.Select(i => lines[i].Page).Distinct().Count(), t["pagesWithOtherOccurrences"]!.GetValue<int>());
            var shown = t["shown"]!.AsArray();
            Assert.Equal(expected[k].Others.Take(2).Select(i => lines[i].Alias), shown.Select(x => x!["alias"]!.GetValue<string>()));
            foreach (var (x, j) in shown.Zip(expected[k].Others.Take(2)))
            {
                Assert.Equal(lines[j].Page, x!["page"]!.GetValue<int>());
                Assert.Equal(j, x["readingOrder"]!.GetValue<int>());
                Assert.Equal(j - expected[k].At, x["distanceFromTarget"]!.GetValue<int>());
                Assert.Equal(lines[j].Text == lines[expected[k].At].Text, x["exactTextMatch"]!.GetValue<bool>());
                Assert.Equal(lines[j].Text != lines[expected[k].At].Text, x["text"] is not null);     // the other text is shown only when it differs
                if (j + 1 < lines.Count) Assert.Equal(lines[j + 1].Alias, x["followingLine"]!["alias"]!.GetValue<string>());
                Assert.NotNull(x["layout"]);
                Assert.NotNull(x["delta"]);
            }
        }
        Assert.Empty(tools.Ledger);                                 // not a model call: nothing goes in the tool-call ledger
        Assert.Equal(r.Bytes, Encoding.UTF8.GetByteCount(r.Observations.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
    }

    [Fact]
    public void Prefetch_OnSeg001_CoversTheContentsLinesAndTheRunningLines_AndNotTheTwoWithoutAnotherOccurrence()
    {
        var r = NewTools().Prefetch(Decide("SEG-001"));
        var listed = r.Observations["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(46, r.Listed);
        Assert.Equal(2, r.WithoutOther);
        Assert.DoesNotContain("L0059:S0", listed);                  // "4.6. Server Push": no other line with this text
        Assert.DoesNotContain("L0075:S0", listed);                  // "7.2. Frame Definitions" (ligature): no other line with this text
        Assert.Contains("L0042:S0", listed);                        // a contents line whose text also opens a later line
        Assert.Contains("L0062:S0", listed);                        // a running line repeated on every page is listed by the same rule
    }

    [Fact]
    public void Prefetch_IsDeterministic_AndUsesNothingButTheParser()
    {
        var a = NewTools().Prefetch(Decide("SEG-001"));
        var b = NewTools().Prefetch(Decide("SEG-001"));
        Assert.Equal(a.Observations.ToJsonString(), b.Observations.ToJsonString());
        Assert.Equal(a.ShownAliases, b.ShownAliases);
        Assert.InRange(a.Bytes, 1, P7F1V6AgentTools.PrefetchMaxBytes);
    }

    static readonly Regex KindOfLine = new(@"\b(toc|contents?|table of contents|headers?|footers?|page numbers?|dot leaders?|index|indexes|entry|entries|navigation|furniture|metadata|letterhead|titles?|subtitle|caption|chapter|section|heading|headings)\b|mục lục",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Prefetch_NamesNoKindOfLine_InItsFieldsOrItsOwnTexts()
    {
        var r = NewTools().Prefetch(Decide("SEG-001"));
        var own = r.Observations.DeepClone().AsObject();
        // The words of the lines themselves are the document's; the object's own field names and its fixed texts are the program's.
        Assert.DoesNotContain(PropertyNames(own), n => KindOfLine.IsMatch(n));
        Assert.False(KindOfLine.IsMatch(own["readBy"]!.GetValue<string>()), own["readBy"]!.GetValue<string>());
        Assert.False(KindOfLine.IsMatch(own["normalization"]!.GetValue<string>()));
    }

    [Fact]
    public void Prefetch_OverTheByteCap_CutsToOneOccurrencePerTarget_ThenByWholeTargets_AndSaysSo()
    {
        // 40 distinct long texts, each occurring 3 times: every target is listed and each shown occurrence is large.
        var box = JsonDocument.Parse("{\"left\":72.0,\"right\":540.0,\"bottom\":700.0,\"top\":712.0}").RootElement;
        var font = JsonDocument.Parse("{\"fontSize\":10.0,\"fontName\":\"F1\",\"boldRatio\":0.0,\"italicRatio\":0.0}").RootElement;
        var rows = new List<F1QSourceLine>();
        var n = 0;
        for (int rep = 0; rep < 3; rep++)
            for (int k = 0; k < 40; k++)
                rows.Add(new F1QSourceLine($"L{n:0000}:S0", n++, 1 + rep, "x" + new string((char)('a' + k % 26), 3) + k.ToString("00") + " " + new string('y', 20), box, font));
        // Make every text differ from its twin so the other text is shown, and make the following line long.
        var tools = new P7F1V6AgentTools(new P7F1QEvidenceTools("s", "u", "e", rows, new Dictionary<string, string> { ["O1"] = "L0000:S0" }, 2));
        var targets = rows.Take(40).Select(l => l.Alias).ToList();
        var r = tools.Prefetch(targets);
        Assert.Equal(40, r.Listed + r.Omitted);
        Assert.True(r.Bytes <= P7F1V6AgentTools.PrefetchMaxBytes);
        Assert.Equal(r.Omitted == 0, r.Observations["complete"]!.GetValue<bool>());
        if (r.Omitted > 0) Assert.Equal(r.Omitted, r.Observations["omittedTargets"]!.GetValue<int>());
        Assert.InRange(r.PerTarget, 1, 2);

        // Force the cut: long texts on both sides.
        var big = new List<F1QSourceLine>();
        for (int rep = 0; rep < 3; rep++)
            for (int k = 0; k < 60; k++)
                big.Add(new F1QSourceLine($"L{big.Count:0000}:S0", big.Count, 1 + rep, "t" + k.ToString("000") + (rep == 0 ? "" : " ") + new string('z', 0), box, font));
        // Repeat keys ignore digits, so build twins by identical text instead; the following line carries 100 characters.
        var twins = new List<F1QSourceLine>();
        for (int rep = 0; rep < 4; rep++)
            for (int k = 0; k < 60; k++)
            {
                twins.Add(new F1QSourceLine($"L{twins.Count:0000}:S0", twins.Count, 1 + rep, "name " + new string((char)('a' + k % 26), 1 + k % 7) + new string((char)('a' + (k / 26) % 26), 3), box, font));
                twins.Add(new F1QSourceLine($"L{twins.Count:0000}:S0", twins.Count, 1 + rep, new string('w', 300), box, font));
            }
        var bigTools = new P7F1V6AgentTools(new P7F1QEvidenceTools("s", "u", "e", twins, new Dictionary<string, string> { ["O1"] = "L0000:S0" }, 2));
        var bigTargets = twins.Where(l => l.Text.StartsWith("name ")).Take(60).Select(l => l.Alias).ToList();
        var cut = bigTools.Prefetch(bigTargets);
        Assert.True(cut.Bytes <= P7F1V6AgentTools.PrefetchMaxBytes, cut.Bytes.ToString());
        Assert.Equal(bigTargets.Count - cut.WithoutOther, cut.Listed + cut.Omitted);
        if (cut.Omitted > 0)
        {
            Assert.False(cut.Observations["complete"]!.GetValue<bool>());
            Assert.Equal(cut.Omitted, cut.Observations["omittedTargets"]!.GetValue<int>());
            Assert.Equal(1, cut.PerTarget);                            // the per-target cut comes first
        }
    }

    // ================= the arms =================

    [Theory]
    [InlineData("SEG-001")]
    [InlineData("SEG-008")]
    public void Control_IsTheD411gAutoRequest_ByteForByte(string segment)
    {
        var control = P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, U.Value, segment);
        Assert.Equal(C.D411gAutoFirstRequests[segment], control.Sha256);
        Assert.Equal(A.AutoPromptSha256, A.Sha(A.AutoPrompt));
    }

    [Theory]
    [InlineData("SEG-001")]
    [InlineData("SEG-008")]
    public void Prefetch_IsTheControlRequestPlusOneLineAndAnObservationsObjectAtTheEnd(string segment)
    {
        var control = P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, U.Value, segment);
        var prefetch = P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Prefetch, U.Value, segment);
        using var cb = JsonDocument.Parse(control.Body);
        using var pb = JsonDocument.Parse(prefetch.Body);
        // Everything but the user message and the system message is the same.
        foreach (var name in cb.RootElement.EnumerateObject().Where(p => p.Name != "messages"))
            Assert.Equal(name.Value.GetRawText(), pb.RootElement.GetProperty(name.Name).GetRawText());
        Assert.Equal(cb.RootElement.EnumerateObject().Select(p => p.Name), pb.RootElement.EnumerateObject().Select(p => p.Name));
        // System prompt: one added line, nothing else.
        var cs = A.AutoPrompt.Split('\n'); var ps = A.PrefetchPrompt.Split('\n');
        Assert.Equal(cs.Length + 1, ps.Length);
        Assert.Single(ps.Except(cs));
        Assert.Equal(cs, ps.Where(l => cs.Contains(l)));
        Assert.StartsWith("Observations: the request ends with observations", ps.Single(l => !cs.Contains(l)));
        Assert.Equal(A.PrefetchPromptSha256, A.Sha(A.PrefetchPrompt));
        // User message: every property before observations is identical; observations is last.
        var cp = PayloadOf(control); var pp = PayloadOf(prefetch);
        var names = pp.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal("observations", names[^1]);
        Assert.Equal(cp.EnumerateObject().Select(p => p.Name), names.Take(names.Length - 1));
        foreach (var p in cp.EnumerateObject()) Assert.Equal(p.Value.GetRawText(), pp.GetProperty(p.Name).GetRawText());
        Assert.False(cp.TryGetProperty("observations", out _));
        Assert.True(prefetch.Bytes > control.Bytes);
        Assert.True(prefetch.Bytes < C.MaxRequestBytes);
    }

    [Fact]
    public void PrefetchPrompt_StatesWhatTheObservationsAre_AndNamesNoKindOfLine()
    {
        var p = A.PrefetchPrompt;
        Assert.Contains("read from the parser by the program and not requested by you", p);
        Assert.Contains("They are facts about the document, not labels or decisions; what they mean for the instruction is for you to judge", p);
        Assert.Contains("You may cite the aliases they show in evidenceRefs", p);
        Assert.Contains("When observations.complete is true, a target that is not listed has no other occurrence with the same text", p);
        Assert.Contains("You may still call the tools", p);
        Assert.DoesNotMatch(new Regex(@"\b(toc|contents|table of contents|headers?|footers?|page numbers?|dot leaders?|index|indexes|entry|entries|navigation|furniture|metadata|letterhead|titles?|subtitle|caption|chapter|section)\b|mục lục",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), p);
    }

    // ================= running a unit =================

    static string Final(IEnumerable<string> targets, Func<string, string[]?>? selectWith = null) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(targets.Select(a =>
        {
            var refs = selectWith?.Invoke(a);
            var o = new JsonObject { ["alias"] = a, ["decision"] = refs is null ? "NOT_SELECT" : "SELECT" };
            if (refs is not null) o["evidenceRefs"] = new JsonArray(refs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            return (JsonNode)o;
        }).ToArray()),
    }.ToJsonString();

    static string[] TargetsIn(P7F1V6Protocol.PreparedRequest r) => PayloadOf(r).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();

    static A.ModelTurn Answer(string content) => new(content, Array.Empty<A.ToolCall>(), "stop");

    static A.AgentUnitOutcome Run(A.Arm arm, A.AgentModel model, string segment = "SEG-001", P7F1V6DirectV2.Budget? budget = null)
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(U.Value, segment);
        return A.RunUnit(arm, P7F1V6DirectD45.Instruction, U.Value, segment, decide, ctx, model, budget ?? new P7F1V6DirectV2.Budget(100, 100, 2_000_000));
    }

    [Fact]
    public void AnAnswerMayCiteALineOnlyTheObservationsShow_AndItIsReported()
    {
        var obs = NewTools().Prefetch(Decide("SEG-001"));
        var first = obs.Observations["targets"]![0]!.AsObject();
        var target = first["alias"]!.GetValue<string>();
        var other = first["shown"]![0]!["alias"]!.GetValue<string>();
        Assert.DoesNotContain(other, P7F1V6DirectCanary410.Scope(U.Value, "SEG-001").Context.Select(i => L(i)));        // not in the window: only the observations show it
        string? seenRequest = null;
        var outcome = Run(A.Arm.Prefetch, (unit, turn, req) =>
        {
            seenRequest = req.Body;
            return Answer(Final(TargetsIn(req), a => a == target ? new[] { other } : null));
        });
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Contains("\"observations\"", PayloadOf(P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Prefetch, U.Value, "SEG-001")).ToString());
        Assert.Equal(new[] { target }, outcome.Evidence.EvidenceUsedFromObservations);
        Assert.Equal(obs.Listed, outcome.Evidence.ObservationTargetsListed);
        Assert.Equal(obs.Bytes, outcome.Evidence.ObservationBytes);
        Assert.True(outcome.Evidence.ObservationsComplete);
        Assert.Equal(0, outcome.Evidence.ToolsDispatched);                      // the observations are not a model tool call
        Assert.Equal(0, outcome.Evidence.ModelToolCallsEmitted);
        Assert.NotNull(seenRequest);
    }

    [Fact]
    public void ALineThatNeitherTheRequestNorTheObservationsShow_IsStillRefused_AndTheControlCannotCiteObservations()
    {
        var obs = NewTools().Prefetch(Decide("SEG-001"));
        var first = obs.Observations["targets"]![0]!.AsObject();
        var target = first["alias"]!.GetValue<string>();
        var shownByObservations = first["shown"]![0]!["alias"]!.GetValue<string>();
        var shownSomewhere = obs.ShownAliases.ToHashSet();
        var stranger = Enumerable.Range(0, U.Value.Lines.Count).Select(L).First(a => !shownSomewhere.Contains(a)
            && !P7F1V6DirectCanary410.Scope(U.Value, "SEG-001").Context.Select(i => L(i)).Contains(a));
        var refused = Run(A.Arm.Prefetch, (unit, turn, req) => Answer(Final(TargetsIn(req), a => a == target ? new[] { stranger } : null)));
        Assert.Equal("PROTOCOL:EVIDENCE_NOT_SHOWN", refused.Unit.Failure);
        // CONTROL has no observations: the same alias is not shown there, so citing it is refused too.
        var control = Run(A.Arm.Auto, (unit, turn, req) => Answer(Final(TargetsIn(req), a => a == target ? new[] { shownByObservations } : null)));
        Assert.Equal("PROTOCOL:EVIDENCE_NOT_SHOWN", control.Unit.Failure);
        Assert.Equal(0, control.Evidence.ObservationTargetsListed);
        Assert.Empty(control.Evidence.EvidenceUsedFromObservations);
    }

    [Fact]
    public void Prefetch_StillAllowsTools_AndTheirResultsAreReportedApart()
    {
        var outcome = Run(A.Arm.Prefetch, (unit, turn, req) => turn == 1
            ? new A.ModelTurn(null, new[] { new A.ToolCall("c1", P7F1V6AgentTools.DocumentOverview, "{}") }, "tool_calls")
            : Answer(Final(TargetsIn(req))));
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Equal(1, outcome.Evidence.ToolSucceeded);
        Assert.True(outcome.Evidence.ObservationTargetsListed > 0);
    }

    // ================= three identical repetitions =================

    [Fact]
    public void RunArms_GoesRepetitionBySegmentByArm_WithIdenticalRequestsAndRepetitionInTheLedgerId()
    {
        var seen = new List<(string Arm, string Unit, string Sha)>();
        A.AgentModel Model(string arm) => (unit, turn, req) => { seen.Add((arm, unit, req.Sha256)); return Answer(Final(TargetsIn(req))); };
        var budgets = C.Arms.ToDictionary(a => a.Arm, _ => C.NewArmBudget());
        var state = C.RunArms(U.Value, C.Arms.ToDictionary(a => a.Arm, a => Model(a.Arm)), budgets);

        Assert.Equal(new[] { "CONTROL", "PREFETCH" }, state.Select(s => s.Arm));
        Assert.All(state, s => { Assert.Equal(6, s.Units.Count); Assert.Equal(6, s.Budget.Calls); Assert.All(s.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status)); });
        // Order: repetition, segment, arm.
        Assert.Equal(new[]
        {
            "CONTROL SEG-001#r1", "PREFETCH SEG-001#r1", "CONTROL SEG-008#r1", "PREFETCH SEG-008#r1",
            "CONTROL SEG-001#r2", "PREFETCH SEG-001#r2", "CONTROL SEG-008#r2", "PREFETCH SEG-008#r2",
            "CONTROL SEG-001#r3", "PREFETCH SEG-001#r3", "CONTROL SEG-008#r3", "PREFETCH SEG-008#r3",
        }, seen.Select(x => x.Arm + " " + x.Unit));
        // The repetitions are the same request, byte for byte; the arms differ.
        foreach (var arm in new[] { "CONTROL", "PREFETCH" })
            foreach (var seg in new[] { "SEG-001", "SEG-008" })
                Assert.Single(seen.Where(x => x.Arm == arm && x.Unit.StartsWith(seg)).Select(x => x.Sha).Distinct());
        Assert.NotEqual(seen.First(x => x.Arm == "CONTROL").Sha, seen.First(x => x.Arm == "PREFETCH").Sha);
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, state[0].Units.Select(u => u.Rep));
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("pages")]
    public void EvenIfEveryUnitCallsToolsOnEveryTurn_NoUnitIsStoppedByTheCallLimitOrTheReserve(string scenario)
    {
        var pages = U.Value.Lines.Select(l => l.Page).Distinct().ToArray();
        A.ModelTurn Model(int turn, P7F1V6Protocol.PreparedRequest req) => turn <= A.MaxToolTurns
            ? new A.ModelTurn(null, (scenario == "overview"
                ? new[] { new A.ToolCall($"c{turn}", P7F1V6AgentTools.DocumentOverview, "{}") }
                : Enumerable.Range(0, 8).Select(k => new A.ToolCall($"c{turn}_{k}", P7F1V6AgentTools.PageLayout,
                    JsonSerializer.Serialize(new { page = pages[(turn * 8 + k) % pages.Length], offset = 0, limit = 40 }))).ToArray()), "tool_calls")
            : Answer(Final(TargetsIn(req)));
        var budgets = C.Arms.ToDictionary(a => a.Arm, _ => C.NewArmBudget());
        var state = C.RunArms(U.Value, C.Arms.ToDictionary(a => a.Arm, a => (A.AgentModel)((unit, turn, req) => Model(turn, req))), budgets);
        foreach (var s in state)
        {
            Assert.Equal(24, s.Budget.Calls);                                              // 6 units x 4 posts: the most a unit can use
            Assert.Empty(s.Budget.Refusals);
            Assert.Null(s.Budget.HaltReason);
            Assert.True(s.Budget.ReservedUsd <= C.CeilingUsdPerArm, s.Budget.ReservedUsd.ToString());
            Assert.All(s.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status));
        }
    }

    // ================= plan and execute =================

    static readonly Lazy<byte[]> PlanBytes = new(() => C.Serialize(C.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));

    [Fact]
    public void Plan_PinsTheArms_TheControlIdentity_TheMeasuredObservations_AndTheBudget()
    {
        using var doc = JsonDocument.Parse(PlanBytes.Value);
        var p = doc.RootElement;
        Assert.Equal(C.PlanArtifact, p.GetProperty("artifact").GetString());
        Assert.False(p.GetProperty("approvalToSend").GetBoolean());
        Assert.Equal(48, p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(1.30, p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble(), 9);
        Assert.Contains("raisedFrom", p.GetProperty("budget").ToString());
        Assert.Equal(0, p.GetProperty("retries").GetInt32());
        Assert.Equal(3, p.GetProperty("repetitions").GetProperty("perArmAndSegment").GetInt32());
        var arms = p.GetProperty("arms").EnumerateArray().ToList();
        Assert.Equal(new[] { "CONTROL", "PREFETCH" }, arms.Select(a => a.GetProperty("arm").GetString()));
        Assert.All(arms, a =>
        {
            var b = a.GetProperty("budget");
            Assert.Equal(24, b.GetProperty("maxCalls").GetInt32());
            Assert.True(b.GetProperty("worstCaseReserveUsd").GetDouble() <= b.GetProperty("ceilingUsd").GetDouble());
            Assert.Equal(2, a.GetProperty("firstRequests").GetArrayLength());
        });
        Assert.Equal(A.AutoPromptSha256, arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(A.PrefetchPromptSha256, arms[1].GetProperty("promptSha256").GetString());
        // CONTROL first requests are the D4.11g AUTO ones.
        var controlFirst = arms[0].GetProperty("firstRequests").EnumerateArray().ToDictionary(f => f.GetProperty("unitId").GetString()!, f => f.GetProperty("sha256").GetString()!);
        Assert.Equal(C.D411gAutoFirstRequests, controlFirst);
        // Measured observations per segment.
        var measured = p.GetProperty("prefetch").GetProperty("measuredPerSegment").EnumerateArray().ToDictionary(m => m.GetProperty("segment").GetString()!);
        Assert.Equal(46, measured["SEG-001"].GetProperty("listed").GetInt32());
        Assert.Equal(0, measured["SEG-001"].GetProperty("omitted").GetInt32());
        Assert.Equal(NewTools().Prefetch(Decide("SEG-008")).Bytes, measured["SEG-008"].GetProperty("observationBytes").GetInt32());
        Assert.Contains("only criterion", p.GetProperty("prefetch").GetProperty("rule").GetString());
        Assert.Contains("indication, not a significance test", string.Join("|", p.GetProperty("readingRules").EnumerateArray().Select(x => x.GetString())));
        Assert.Contains("Gold and earlier answers are never used", p.GetProperty("prefetch").GetProperty("notSharedWithAnyAnswer").GetString());
        var frozen = p.GetProperty("frozen");
        Assert.Equal(P7F1V6DirectD45.Document, frozen.GetProperty("document").GetString());
        Assert.Equal(U.Value.UniverseSha256, frozen.GetProperty("universeSha256").GetString());
        Assert.False(frozen.GetProperty("provider").GetProperty("allowFallbacks").GetBoolean());
        Assert.Equal(new[] { "alibaba/us-east-1" }, frozen.GetProperty("provider").GetProperty("ignore").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Plan_Verify_RefusesADriftedObservationRule_AnotherSha_AndAnotherCommit()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(SHA256.HashData(PlanBytes.Value));
        Assert.Empty(C.Verify(TestRepository.Root(), plan, sha, "TEST_COMMIT"));
        Assert.Contains("PLAN_SHA_NOT_CONFIRMED", C.Verify(TestRepository.Root(), plan, new string('0', 64), "TEST_COMMIT"));
        Assert.Contains("CODE_COMMIT_DIFFERS", C.Verify(TestRepository.Root(), plan, sha, "OTHER"));
        var node = JsonNode.Parse(PlanBytes.Value)!.AsObject();
        node["prefetch"]!["rule"] = "A target is listed if it looks like a contents line";
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        var drift = Path.Combine(scratch, "drift.json");
        File.WriteAllBytes(drift, bytes);
        Assert.Contains("PINNED_VALUES_DIFFER", C.Verify(TestRepository.Root(), drift, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT"));
    }

    sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "text/event-stream") };
        }
    }

    static string Sse(string requestBody, string content)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        var sb = new StringBuilder();
        void Data(object o) => sb.Append("data: ").Append(JsonSerializer.Serialize(o)).Append("\n\n");
        var head = new { id = "gen-mock", provider = "Alibaba", model = "qwen/qwen3.7-flash" };
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { role = "assistant", reasoning = "thinking" } } } });
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { content } } } });
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = "stop", native_finish_reason = "stop" } } });
        Data(new { head.id, head.provider, head.model, choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 },
                prompt_tokens_details = new { cached_tokens = 0, cache_write_tokens = 0 } } });
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    [Fact]
    public void Execute_SendsNothingWithoutAuthorization_ThenRunsTwelvePostsWithRepetitionsInTheLedger_Once()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(SHA256.HashData(PlanBytes.Value));
        var root = SafeRoot("safe");
        var http = new Handler(body =>
        {
            using var d = JsonDocument.Parse(body);
            using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            return Sse(body, Final(p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!)));
        });
        Assert.StartsWith("AUTHORIZATION_REFUSED:", Assert.Throws<InvalidOperationException>(() =>
            C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", Path.Combine(scratch, "none.json"), Key, http, root)).Message, StringComparison.Ordinal);
        Assert.Empty(http.Bodies);

        var auth = Path.Combine(scratch, "auth.json");
        File.WriteAllBytes(auth, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(plan, root), "TEST_USER", "TEST approval",
            "TEST: allow", P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking, DateTime.UtcNow));
        var arms = C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root);

        Assert.Equal(12, http.Bodies.Count);
        Assert.Equal(new[] { "CONTROL", "PREFETCH" }, arms.Select(a => a.Arm));
        Assert.All(arms, a =>
        {
            Assert.Equal(6, a.Ledger.Count);
            Assert.Equal(new[] { "SEG-001#r1", "SEG-008#r1", "SEG-001#r2", "SEG-008#r2", "SEG-001#r3", "SEG-008#r3" }, a.Ledger.Select(l => l.UnitId));
            Assert.All(a.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status));
            Assert.All(a.Ledger, l => Assert.Equal("PASSED_AUDIT_TO_PARSER", l.Verdict));
        });
        Assert.Equal(0, http.Bodies.Count(b => b.Contains("\"response_format\"")));              // every first turn is without it
        Assert.Equal(6, http.Bodies.Count(b => b.Contains("\\\"observations\\\":{")));                      // only the PREFETCH requests carry observations
        Assert.NotEqual(arms[0].SafeRunDirectory, arms[1].SafeRunDirectory);
        Assert.DoesNotContain(http.Bodies, b => b.Contains(Key, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root));
        Assert.Equal(12, http.Bodies.Count);
    }
}
