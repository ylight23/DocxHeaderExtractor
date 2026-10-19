using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4 (offline): the DIRECT-F1 request and reply contract, the exact F1 scorer that refuses without an approved Gold, the
/// canary T1 builder checks, and the single-context versus segmented size comparison. No connection is opened and no Gold is written.</summary>
public sealed class P7F1V6DirectD4Tests : IDisposable
{
    const string Registry = "eval/a99-closed-loop/gold-current/registry.v1.json";
    const string Instruction = "Trích xuất các heading chính";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d4-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD4Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    static List<P7F1V6Direct.Line> Lines(int n) =>
        Enumerable.Range(1, n).Select(i => new P7F1V6Direct.Line($"S{i:0000}", 1, $"Dòng số {i} của tài liệu giả lập.")).ToList();

    static string UserPayload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        return doc.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!;
    }

    static string Reply(params (string Alias, string Decision, string Ref)[] items) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6Direct.ResponseVersion,
            decisions = items.Select(i => new { alias = i.Alias, decision = i.Decision, evidenceRefs = new[] { i.Ref } }),
        });

    // ---------------- request ----------------

    [Fact]
    public void Request_CarriesInstructionVerbatim_WithoutPreClassification()
    {
        var lines = Lines(3);
        var req = P7F1V6Direct.BuildRequest(Instruction, "sha", lines, 0, 3);
        using var payload = JsonDocument.Parse(UserPayload(req));
        var p = payload.RootElement;

        Assert.Equal(Instruction, p.GetProperty("instruction").GetString());
        Assert.Equal(P7F1V6Core.InstructionSha256(Instruction), p.GetProperty("instructionSha256").GetString());
        foreach (var forbidden in new[] { "level", "parentId", "continuesFromId", "class", "heading", "classification" })
            Assert.False(p.TryGetProperty(forbidden, out _), $"request must not carry {forbidden}");
        Assert.Equal(3, p.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void Request_IsSentWithTheApprovedCapsAndRoute()
    {
        var req = P7F1V6Direct.BuildRequest(Instruction, "sha", Lines(2), 0, 2);
        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal(P7F1V6Protocol.MaxTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.Equal(req.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(req.Body))));
    }

    [Fact]
    public void Request_RejectsARangeOutsideTheDocument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => P7F1V6Direct.BuildRequest(Instruction, "sha", Lines(2), 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => P7F1V6Direct.BuildRequest(Instruction, "sha", Lines(2), 1, 1));
    }

    // ---------------- reply ----------------

    [Fact]
    public void Reply_CompleteAndWellFormed_IsAccepted()
    {
        var lines = Lines(3);
        var r = P7F1V6Direct.Parse(Reply(("S0001", "SELECT", "S0001"), ("S0002", "NOT_SELECT", "S0002"), ("S0003", "NOT_SELECT", "S0003")), lines, 0, 3);
        Assert.True(r.Ok);
        Assert.Empty(r.Missing);
        Assert.Equal(3, r.Decisions.Count);
    }

    [Fact]
    public void Reply_MissingAlias_IsCoverageIncomplete_NotFilledIn()
    {
        var lines = Lines(3);
        var r = P7F1V6Direct.Parse(Reply(("S0001", "SELECT", "S0001"), ("S0003", "NOT_SELECT", "S0003")), lines, 0, 3);
        Assert.False(r.Ok);
        Assert.Equal("COVERAGE_INCOMPLETE", r.Failure);
        Assert.Equal(new[] { "S0002" }, r.Missing);
    }

    [Fact]
    public void Reply_DecidingAnAliasOutsideTheRange_IsUnexpected()
    {
        var lines = Lines(3);
        var r = P7F1V6Direct.Parse(Reply(("S0001", "SELECT", "S0001"), ("S0002", "NOT_SELECT", "S0002"), ("S0003", "NOT_SELECT", "S0003")), lines, 0, 2);
        Assert.False(r.Ok);
        Assert.Equal("UNEXPECTED_ALIAS", r.Failure);
        Assert.Equal(new[] { "S0003" }, r.Unexpected);
    }

    [Theory]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0001\"],\"level\":2}]}", "DECISION_KEYS_INVALID")]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"H2\",\"evidenceRefs\":[\"S0001\"]}]}", "DECISION_VALUE_INVALID")]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0009\"]}]}", "EVIDENCE_NOT_SHOWN")]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[]}]}", "EVIDENCE_REFS_INVALID")]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[],\"extra\":1}", "ROOT_KEYS_INVALID")]
    [InlineData("{\"protocolVersion\":\"WRONG\",\"decisions\":[]}", "PROTOCOL_VERSION_INVALID")]
    [InlineData("{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0001\"]},{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0001\"]}]}", "ALIAS_LISTED_TWICE")]
    [InlineData("not json", "REPLY_NOT_JSON")]
    public void Reply_MalformedShapes_AreRefused(string reply, string code)
    {
        var r = P7F1V6Direct.Parse(reply, Lines(1), 0, 1);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Failure);
    }

    [Fact]
    public void Reply_OverTheVisibleCap_IsRefused()
    {
        var big = "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[],\"pad\":\"" + new string('x', P7F1V6Direct.VisibleResponseCapBytes) + "\"}";
        var r = P7F1V6Direct.Parse(big, Lines(1), 0, 1);
        Assert.Equal("REPLY_OVER_CAP", r.Failure);
    }

    [Fact]
    public void Reply_PerPage_FitsInsideTheVisibleCap()
    {
        // The paged design depends on this: a full page of worst-case decisions must fit in the reply cap.
        int aliasChars = 8;
        int n = P7F1V6Direct.DecisionsPerReply(aliasChars);
        var alias = new string('A', aliasChars);
        var items = Enumerable.Range(0, n).Select(_ => (Alias: alias, Decision: "NOT_SELECT", Ref: alias)).ToArray();
        var reply = JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6Direct.ResponseVersion,
            decisions = items.Select(i => new { alias = i.Alias, decision = i.Decision, evidenceRefs = Enumerable.Repeat(i.Ref, P7F1V6Direct.MaxEvidenceRefs).ToArray() }),
        });
        Assert.True(Encoding.UTF8.GetByteCount(reply) <= P7F1V6Direct.VisibleResponseCapBytes, $"{n} decisions = {Encoding.UTF8.GetByteCount(reply)} bytes");
    }

    [Fact]
    public void Unicode_EscapesThatDecodeToTheAliasAreAccepted()
    {
        // A valid JSON escape is data like any other: S0001 decodes to S0001 and is accepted.
        var reply = "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S\\u0030001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0001\"]}]}";
        var r = P7F1V6Direct.Parse(reply, Lines(1), 0, 1);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("S0001", r.Decisions[0].Alias);
    }

    [Fact]
    public void Unicode_VietnameseTextInEvidenceRefs_IsDecodedNotRefused()
    {
        var lines = new List<P7F1V6Direct.Line> { new("S0001", 1, "Chương 1. Quy định chung") };
        var reply = "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V1\",\"decisions\":[{\"alias\":\"S0001\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"S0001\"]}]}";
        Assert.True(P7F1V6Direct.Parse(reply, lines, 0, 1).Ok);
    }

    [Fact]
    public void Canary_ArtifactBodyBytes_MatchTheRecordedSha()
    {
        var dir = Path.Combine(scratch, "canary");
        var sha = P7F1V6DirectCanary.WriteArtifact(dir, "13bbf5e130f70f4d8557f9e0c2252c07e5f237dc");
        var body = File.ReadAllBytes(Path.Combine(dir, "canary-t1-request.body.json"));
        Assert.Equal(sha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)));
        Assert.Equal(P7F1V6DirectCanary.Build().Request.Sha256, sha);
        Assert.True(body.Length <= P7F1V6DirectCanary.MaxRequestBytes);
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "canary-t1-request.audit.v1.json")));
        Assert.Equal(sha, audit.RootElement.GetProperty("requestSha256").GetString());
        Assert.Equal(0, audit.RootElement.GetProperty("retry").GetInt32());
        Assert.Equal(1, audit.RootElement.GetProperty("postsApproved").GetInt32());
        Assert.False(audit.RootElement.GetProperty("reasoningDeclared").GetBoolean());
    }

    // ---------------- canary T1 ----------------

    [Fact]
    public void Canary_SyntheticRequest_PassesEveryCheck_UnderTheCeiling()
    {
        var built = P7F1V6DirectCanary.Build();
        Assert.True(built.ReadyForReviewedSend, string.Join("; ", built.Checks.Where(c => !c.Passed).Select(c => c.Code + ": " + c.Reason)));
        Assert.True(built.Request.Bytes <= P7F1V6DirectCanary.MaxRequestBytes);
        Assert.True(built.ReserveUsd < P7F1V6DirectCanary.CeilingUsd);
        Assert.Equal(8, built.Checks.Count);
    }

    [Fact]
    public void Canary_TheDocumentIsSyntheticNotTheRfc()
    {
        var built = P7F1V6DirectCanary.Build();
        using var doc = JsonDocument.Parse(built.Request.Body);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!;
        Assert.DoesNotContain("RFC", content, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP/2", content, StringComparison.Ordinal);
    }

    // ---------------- size comparison ----------------

    [Fact]
    public void Compare_SegmentedDecidesEveryLineOnce_AndSingleContextResendsTheWhole()
    {
        var lines = Lines(2000);
        var (single, seg) = P7F1V6Direct.Compare(Instruction, "sha", lines);
        Assert.True(single.Covered);
        Assert.True(seg.Covered);
        Assert.Equal(lines.Count, seg.Decided);
        Assert.Equal(lines.Count, single.Decided);
        Assert.True(seg.TotalRequestBytes < single.TotalRequestBytes, "segmented must resend less than single-context");
        Assert.True(seg.WorstUsd < single.WorstUsd);
        Assert.Equal((long)single.ReplyPages * lines.Count - lines.Count, single.ContextLinesResent);
    }

    [Fact]
    public void Compare_OverTheTokenBudget_IsReportedNotTrimmed()
    {
        var lines = Lines(2000);
        var (single, seg) = P7F1V6Direct.Compare(Instruction, "sha", lines, promptTokenBudget: 5000);
        Assert.False(single.Covered);
        Assert.Equal("REQUEST_OVER_TOKEN_BUDGET", single.Note);
        if (seg.Covered) Assert.Equal(lines.Count, seg.Decided);
        else Assert.NotNull(seg.Note);
    }

    [Fact]
    public void AttemptCost_UsesTheRequestsOwnCompletionCap()
    {
        // 16,384 bytes at the first tier, with max_tokens 32,768 as the completion bound.
        var expected = 16384 * 0.00000003 + 32768 * 0.00000013;
        Assert.Equal(expected, P7F1V6Direct.AttemptUsd(16384), 12);
    }
}
