using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;
using T = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectOwnerTrial;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.11j (offline): the minimal contact redaction, the owner-supplied document loader and the owner-document trial plan. A small PDF of
/// the public corpus stands in for the owner's file. Scripted models and fake HTTP only. No network.</summary>
public sealed class P7F1V6DirectD411jTests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d411j-0000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d411j-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD411jTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A real PDF, outside the repository's source gate: a copy in the scratch directory, as an owner-supplied file would be.
    string StandIn()
    {
        var src = Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100", "06_dich_song_ngu", "089_ND_195-2013_Luat_Xuat_ban_EN.pdf");
        var copy = Path.Combine(scratch, "owner file.pdf");
        if (!File.Exists(copy)) File.Copy(src, copy);
        return copy;
    }

    // ================= redaction =================

    [Theory]
    [InlineData("tel: 0912503028; email:", "tel: [PHONE]; email:", 1)]
    [InlineData("lqtuan@mst.gov.vn.", "[EMAIL].", 1)]
    [InlineData("Liên hệ 091 250 3028 hoặc +84 912 503 028 hoặc 091-250-3028 hoặc 024.3943.1234", "Liên hệ [PHONE] hoặc [PHONE] hoặc [PHONE] hoặc [PHONE]", 4)]
    [InlineData("a.b-c+d@sub.example.org và x@y.vn", "[EMAIL] và [EMAIL]", 2)]
    [InlineData("Thời gian: 14 giờ 00 phút, ngày 23 tháng 9 năm 2026", "Thời gian: 14 giờ 00 phút, ngày 23 tháng 9 năm 2026", 0)]
    [InlineData("Số: 113/GM-BKHCN, 13:30 – 14:00, page 2024, Article 0.5", "Số: 113/GM-BKHCN, 13:30 – 14:00, page 2024, Article 0.5", 0)]
    [InlineData("", "", 0)]
    public void Redaction_ReplacesOnlyPhonesAndEmails(string text, string expected, int spans)
    {
        Assert.Equal(expected, P7F1V6Redaction.Apply(text, out var n));
        Assert.Equal(spans, n);
        Assert.Equal(expected, P7F1V6Redaction.Apply(text));
    }

    [Fact]
    public void Redaction_IsIdempotent_AndLeavesPlaceholdersAlone()
    {
        var once = P7F1V6Redaction.Apply("0912503028 lqtuan@mst.gov.vn");
        Assert.Equal("[PHONE] [EMAIL]", once);
        Assert.Equal(once, P7F1V6Redaction.Apply(once, out var n));
        Assert.Equal(0, n);
    }

    // ================= the loader =================

    [Fact]
    public void OwnerLoader_RefusesAnotherFileAndAMissingFile_AndNeverUsesThePublicSourceGate()
    {
        var path = StandIn();
        var sha = T.Sha256Of(path);
        Assert.Null(P7F1V6Universe.RebuildOwnerSupplied(path, new string('0', 64), null, out var why1));
        Assert.Equal("OWNER_DOCUMENT_SHA_DIFFERS", why1);
        Assert.Null(P7F1V6Universe.RebuildOwnerSupplied(Path.Combine(scratch, "none.pdf"), sha, null, out var why2));
        Assert.Equal("OWNER_DOCUMENT_MISSING", why2);
        Assert.Null(P7F1V6Universe.RebuildOwnerSupplied(Path.Combine(scratch, "x.docx"), sha, null, out var why3));
        Assert.Equal("UNIVERSE_REBUILD_PDF_ONLY", why3);
        // The public source gate would refuse this path (outside todo10_8/); the owner path loads it because the plan pins the hash.
        Assert.False(P7F1V6DataPolicy.IsAllowedSource("owner file.pdf"));
        var u = P7F1V6Universe.RebuildOwnerSupplied(path, sha, null, out var ok);
        Assert.NotNull(u);
        Assert.Equal("OK", ok);
        Assert.Equal(sha, u!.SourceSha256);
        Assert.StartsWith("owner-supplied:", u.DocumentRelativePath);
    }

    [Fact]
    public void RedactionReachesEveryPlaceTextCanLeave_RequestsToolResultsAndTheObservations()
    {
        var path = StandIn();
        var sha = T.Sha256Of(path);
        // A redaction that marks every line makes any leak of unredacted text visible.
        var marked = P7F1V6Universe.RebuildOwnerSupplied(path, sha, t => "§" + t, out _)!;
        Assert.All(marked.Lines, l => Assert.StartsWith("§", l.Text));
        var tools = new P7F1V6AgentTools(marked.Tools);
        var segment = "SEG-000";
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(marked, segment);
        var request = C.FirstRequest(A.Arm.Prefetch, marked, segment).Body;
        using var body = JsonDocument.Parse(request);
        using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        foreach (var line in payload.RootElement.GetProperty("targets").EnumerateArray().Concat(payload.RootElement.GetProperty("context").EnumerateArray()))
            Assert.StartsWith("§", line.GetProperty("text").GetString());
        // Tool results.
        var r = tools.Execute("E1.01", P7F1V6AgentTools.SourceContext, JsonSerializer.Serialize(new { alias = decide[3], before = 2, after = 2 }));
        using var res = JsonDocument.Parse(r.Content);
        foreach (var l in res.RootElement.GetProperty("result").GetProperty("lines").EnumerateArray()) Assert.StartsWith("§", l.GetProperty("text").GetString());
        var span = tools.Execute("E1.02", P7F1V6AgentTools.SourceSpan, JsonSerializer.Serialize(new { aliases = new[] { decide[0] } }));
        Assert.Contains("§", Encoding.UTF8.GetString(span.Content));
        var first = marked.Lines[0].Text[1..Math.Min(marked.Lines[0].Text.Length, 6)];
        var search = tools.Execute("E1.03", P7F1V6AgentTools.SearchText, JsonSerializer.Serialize(new { query = first, mode = "literal", offset = 0, limit = 5 }));
        var matches = JsonDocument.Parse(search.Content).RootElement.GetProperty("result").GetProperty("matches");
        Assert.True(matches.GetArrayLength() > 0);
        foreach (var m in matches.EnumerateArray()) Assert.StartsWith("§", m.GetProperty("text").GetString());
        // Prefetch observations: the other occurrence's text and the following line.
        var pf = tools.Prefetch(Enumerable.Range(0, marked.Lines.Count).Select(i => marked.Lines[i].Alias).Take(48).ToList());
        foreach (var t in pf.Observations["targets"]!.AsArray())
            foreach (var s in t!["shown"]!.AsArray())
            {
                if (s!["text"] is { } txt) Assert.StartsWith("§", txt.GetValue<string>());
                if (s["followingLine"] is JsonObject f) Assert.StartsWith("§", f["text"]!.GetValue<string>());
            }
    }

    [Fact]
    public void TheTrialUniverse_IsTheDocumentAsParsed_NothingIsRedacted()
    {
        var path = StandIn();
        var sha = T.Sha256Of(path);
        var asIs = P7F1V6Universe.RebuildOwnerSupplied(path, sha, null, out _)!;
        var trial = T.Load(path, sha);
        Assert.Equal(asIs.Lines.Select(l => l.Text), trial.Lines.Select(l => l.Text));
        Assert.Equal(asIs.UniverseSha256, trial.UniverseSha256);
    }

    // ================= the plan =================

    static readonly Dictionary<string, byte[]> PlanCache = new();

    byte[] Plan()
    {
        var path = StandIn();
        if (!PlanCache.TryGetValue(path, out var b)) PlanCache[path] = b = T.Serialize(T.PinnedValues(path, "TEST_COMMIT"));
        return b;
    }

    [Fact]
    public void Plan_PinsTheFileNotThePath_TheRedaction_TheSegments_TheRequestsAndTheDerivedBudget()
    {
        var path = StandIn();
        using var doc = JsonDocument.Parse(T.Serialize(T.PinnedValues(path, "TEST_COMMIT")));
        var p = doc.RootElement;
        var sha = T.Sha256Of(path);
        Assert.Equal(sha, p.GetProperty("sourceSha256").GetString());
        Assert.Equal("OWNER-DOC-" + sha[..8], p.GetProperty("authorityId").GetString());
        Assert.DoesNotContain(scratch, p.ToString());                                               // the path is not in the plan
        Assert.False(p.GetProperty("ownerSupplied").GetProperty("publicCorpus").GetBoolean());
        Assert.Contains("NOT part of the repository's public corpus", p.GetProperty("documentPublicity").GetString());
        Assert.StartsWith("none:", p.GetProperty("ownerSupplied").GetProperty("redaction").GetString());
        Assert.Contains("nothing is redacted", p.GetProperty("documentPublicity").GetString());
        Assert.StartsWith("none: there is no Gold", p.GetProperty("goldForScorerOnly").GetString());
        Assert.Equal(3, p.GetProperty("segments").GetArrayLength() == 0 ? 0 : 3);
        var u = T.Load(path, sha);
        var segs = p.GetProperty("segments").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(T.SegmentsOf(u), segs);
        Assert.Equal(C.Reps, p.GetProperty("repetitions").GetProperty("perArmAndSegment").GetInt32());
        var arms = p.GetProperty("arms").EnumerateArray().ToList();
        Assert.Equal(new[] { "CONTROL", "PREFETCH" }, arms.Select(a => a.GetProperty("arm").GetString()));
        var units = segs.Length * C.Reps;
        Assert.All(arms, a =>
        {
            var b = a.GetProperty("budget");
            Assert.Equal(units + Math.Max(3, units / 5), b.GetProperty("maxCalls").GetInt32());
            var sum = a.GetProperty("firstRequests").EnumerateArray().Sum(f => P7F1V6Direct.AttemptUsd(f.GetProperty("bytes").GetInt32())) * C.Reps;
            var spare = (b.GetProperty("maxCalls").GetInt32() - units) * P7F1V6Direct.AttemptUsd(131072);
            Assert.Equal(Math.Ceiling((sum + spare) * 1.1 * 100) / 100, b.GetProperty("ceilingUsd").GetDouble(), 6);
        });
        Assert.Equal(A.AutoPromptRangeSha256, arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(A.PrefetchPromptRangeSha256, arms[1].GetProperty("promptSha256").GetString());
        Assert.StartsWith("None.", p.GetProperty("rawPublication").GetString());
        Assert.Equal(0, p.GetProperty("retries").GetInt32());
        // The plan holds no line text of the document.
        var plain = p.ToString();
        foreach (var l in u.Lines.Where(l => l.Text.Length > 25).Take(40)) Assert.DoesNotContain(l.Text, plain);
    }

    [Fact]
    public void Plan_Verify_RefusesAnotherSha_AnotherCommit_AnotherFile_AndADriftedValue()
    {
        var path = StandIn();
        var planBytes = Plan();
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, planBytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        Assert.Empty(T.Verify(plan, sha, "TEST_COMMIT", path));
        Assert.Contains("PLAN_SHA_NOT_CONFIRMED", T.Verify(plan, new string('0', 64), "TEST_COMMIT", path));
        Assert.Contains("CODE_COMMIT_DIFFERS", T.Verify(plan, sha, "OTHER", path));
        // Another file with the same name: refused before anything is parsed.
        var other = Path.Combine(scratch, "other.pdf");
        File.Copy(Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100", "07_system_generated", "095_RFC9114_HTTP_3.pdf"), other);
        Assert.Contains("OWNER_DOCUMENT_SHA_DIFFERS", T.Verify(plan, sha, "TEST_COMMIT", other));
        Assert.Equal(new[] { "OWNER_DOCUMENT_MISSING" }, T.Verify(plan, sha, "TEST_COMMIT", Path.Combine(scratch, "gone.pdf")));
        var node = JsonNode.Parse(planBytes)!.AsObject();
        node["ownerSupplied"]!["redaction"] = "something else";
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        var drift = Path.Combine(scratch, "drift.json");
        File.WriteAllBytes(drift, bytes);
        Assert.Contains("PINNED_VALUES_DIFFER", T.Verify(drift, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
    }

    [Fact]
    public void TheAuthorizationDraft_StatesThatTheDocumentIsNotPublic_AndThatNothingIsRedacted()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, Plan());
        var root = Path.Combine(scratch, "safe-draft");
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        using var draft = JsonDocument.Parse(P7F1V6DirectAuthorization.Draft(plan, root));
        var pub = draft.RootElement.GetProperty("dataSharing").GetProperty("documentPublicity").GetString()!;
        Assert.Contains("NOT part of the repository's public corpus", pub);
        Assert.Contains("nothing is redacted", pub);
        Assert.Equal("PENDING_USER_APPROVAL", draft.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void LayoutPlan_PinsThePolicyAndTheCores_TheFixedPlanHasNoSuchField_AndVerifyFollowsThePlan()
    {
        var path = StandIn();
        var fixedPlan = Plan();
        using (var f = JsonDocument.Parse(fixedPlan)) Assert.False(f.RootElement.TryGetProperty("segmentation", out _));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation));
        using var doc = JsonDocument.Parse(bytes);
        var seg = doc.RootElement.GetProperty("segmentation");
        Assert.Equal(P7F1V6Universe.LayoutSegmentation, seg.GetProperty("policy").GetString());
        var u = T.Load(path, T.Sha256Of(path), P7F1V6Universe.LayoutSegmentation);
        Assert.Equal(u.Cores.Count, seg.GetProperty("cores").GetArrayLength());
        Assert.Equal(T.SegmentsOf(u), doc.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(fixedPlan)), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var plan = Path.Combine(scratch, "layout-plan.json");
        File.WriteAllBytes(plan, bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Empty(T.Verify(plan, sha, "TEST_COMMIT", path));
        // The policy named in the plan decides what Verify rebuilds: a plan edited back to the fixed policy no longer matches its pinned values.
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["segmentation"]!["policy"] = P7F1V6Universe.FixedSegmentation;
        var edited = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        var editedPath = Path.Combine(scratch, "layout-edited.json");
        File.WriteAllBytes(editedPath, edited);
        Assert.Contains("PINNED_VALUES_DIFFER", T.Verify(editedPath, Convert.ToHexStringLower(SHA256.HashData(edited)), "TEST_COMMIT", path));
    }

    // ================= running =================

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
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { content } } } });
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = "stop", native_finish_reason = "stop" } } });
        Data(new { head.id, head.provider, head.model, choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 } } });
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    [Fact]
    public void Execute_SendsNothingWithoutAuthorization_ThenRunsEveryPlannedFirstPost_Once_AndNoRawLeavesTheSafeStore()
    {
        var path = StandIn();
        var planBytes = Plan();
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, planBytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        using var doc = JsonDocument.Parse(planBytes);
        var segs = doc.RootElement.GetProperty("segments").GetArrayLength();
        var root = Path.Combine(scratch, "safe");
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        var http = new Handler(body =>
        {
            using var d = JsonDocument.Parse(body);
            using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var targets = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!);
            return Sse(body, new JsonObject
            {
                ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
                ["decisions"] = new JsonArray(targets.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
            }.ToJsonString());
        });
        Assert.StartsWith("AUTHORIZATION_REFUSED:", Assert.Throws<InvalidOperationException>(() =>
            T.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", Path.Combine(scratch, "none.json"), Key, http, root, path)).Message, StringComparison.Ordinal);
        Assert.Empty(http.Bodies);

        var auth = Path.Combine(scratch, "auth.json");
        File.WriteAllBytes(auth, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(plan, root), "TEST_USER", "TEST approval",
            "TEST: allow", P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking, DateTime.UtcNow));
        // The wrong file is refused before the marker is written, so the plan stays usable.
        var wrong = Path.Combine(scratch, "wrong.pdf");
        File.Copy(Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100", "07_system_generated", "095_RFC9114_HTTP_3.pdf"), wrong);
        Assert.Contains("OWNER_DOCUMENT_SHA_DIFFERS", Assert.Throws<InvalidOperationException>(() =>
            T.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root, wrong)).Message);
        Assert.Empty(http.Bodies);
        Assert.False(File.Exists(P7F1V6DirectD45.ConsumedMarker(root, sha)));

        var arms = T.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root, path);
        Assert.Equal(segs * C.Reps * 2, http.Bodies.Count);
        Assert.All(arms, a => { Assert.Equal(segs * C.Reps, a.Ledger.Count); Assert.All(a.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status)); });
        Assert.DoesNotContain(http.Bodies, b => b.Contains(Key, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => T.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root, path));
        Assert.Equal(segs * C.Reps * 2, http.Bodies.Count);
    }
}
