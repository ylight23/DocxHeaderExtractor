using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.4 (offline): the qualification storage path, the source gate, retention and deletion, symlink refusal, the route
/// data-policy gate, the response adapter, and the provider guard. No request is sent anywhere.</summary>
public sealed class P7F1V6GatesD34Tests : IDisposable
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    readonly string root = Path.Combine(Path.GetTempPath(), "dhx-d34-" + Guid.NewGuid().ToString("N"));

    public P7F1V6GatesD34Tests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        // Junctions are removed without recursion before the scratch root is deleted, so a target outside the root is never touched.
        foreach (var d in Directory.Exists(root) ? Directory.EnumerateDirectories(root) : Array.Empty<string>())
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { try { Directory.Delete(d); } catch (IOException) { } }
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    static P7F1V6Hier.Cand[] Four() => new P7F1V6Hier.Cand[]
    {
        new("L0001:S0", 1, "Intro"), new("L0002:S0", 1, "Body"), new("L0003:S0", 1, "Intro"), new("L0004:S0", 2, "Summary"),
    };

    static P7F1V6Hier.Options Opts() => new("Trích xuất các mục có tiêu đề", "src-sha", ShardSize: 2, Context: 1, FanIn: 3, PassUpCap: 8,
        SourceUniverseSha256: "u", ParserVersion: "p", EvidenceStoreVersion: "e");

    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var r = doc.RootElement;
        if (kind == P7F1V6Hier.Kind.Interpretation)
            return "{\"protocolVersion\":\"P7_F1_V6_INTERPRET_V1\",\"requestId\":\"INTERPRET\",\"status\":\"COMPLETE\",\"interpretation\":\"Task\"}";
        if (kind == P7F1V6Hier.Kind.Local)
            return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"items\":[" +
                string.Join(",", r.GetProperty("owned").EnumerateArray().Select(o => $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"UNRESOLVED\"}}")) + "]}";
        var ids = (kind == P7F1V6Hier.Kind.Reconcile ? r.GetProperty("owned") : r.GetProperty("shown")).EnumerateArray()
            .Select(o => $"\"{o.GetProperty("id").GetString()}\"");
        return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"confirmed\":[{string.Join(",", ids)}]}}";
    }

    // ---------------- the qualification storage path ----------------

    [Fact] public void A_qualification_run_without_a_safe_store_is_refused_before_any_work()
    {
        var o = Opts() with { RequireSafeStore = true };
        var ex = Assert.Throws<ArgumentException>(() => P7F1V6Hier.Run(Four(), o, (n, k, p) => Honest(n, k, p)));
        Assert.Contains("SAFE_STORE_REQUIRED", ex.Message);
    }

    [Fact] public void A_qualification_run_with_the_legacy_directory_option_is_refused()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var o = Opts() with { RequireSafeStore = true, SafeStore = store, RawDir = Path.Combine(root, "legacy") };
        var ex = Assert.Throws<ArgumentException>(() => P7F1V6Hier.Run(Four(), o, (n, k, p) => Honest(n, k, p)));
        Assert.Contains("LEGACY_RAW_DIR_REFUSED", ex.Message);
    }

    [Fact] public void Every_stored_item_of_a_qualification_run_is_on_disk_in_the_safe_store_and_verifies()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var o = Opts() with { RequireSafeStore = true, SafeStore = store };
        var run = P7F1V6Hier.Run(Four(), o, (n, k, p) => Honest(n, k, p));
        Assert.DoesNotContain("STOPPED", run.RunStatus, StringComparison.Ordinal);
        Assert.NotEmpty(run.Raw.Items);
        foreach (var sha in run.Raw.Items.Keys)
            Assert.NotNull(store.ReadVerified(sha));
        Assert.Empty(store.VerifyOnDisk());
        Assert.Empty(Directory.EnumerateFiles(store.RunDirectory, "*.tmp"));
    }

    // ---------------- source gate ----------------

    [Fact] public void A_source_is_allowed_only_under_the_corpus_and_only_with_its_pinned_hash()
    {
        var repo = TestRepository.Root();
        var pinned = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(TestRepository.Path(Rfc))));
        Assert.Equal((true, "OK"), P7F1V6SourceGate.Check(repo, Rfc, pinned));
        Assert.Equal("SOURCE_HASH_NOT_PINNED", P7F1V6SourceGate.Check(repo, Rfc, new string('0', 64)).Code);
        Assert.Equal("SOURCE_NOT_IN_ALLOWED_PREFIX", P7F1V6SourceGate.Check(repo, "../" + Rfc, pinned).Code);
        Assert.Equal("SOURCE_MISSING", P7F1V6SourceGate.Check(repo, "todo10_8/absent.pdf", pinned).Code);
    }

    // ---------------- retention and deletion ----------------

    [Fact] public void Only_owned_run_directories_past_retention_are_listed_and_deleted()
    {
        var old = P7F1V6SafeRawStore.CreateRun(root);
        old.Put(Encoding.UTF8.GetBytes("old"));
        var fresh = P7F1V6SafeRawStore.CreateRun(root);
        fresh.Put(Encoding.UTF8.GetBytes("fresh"));
        Directory.SetLastWriteTimeUtc(old.RunDirectory, DateTime.UtcNow.AddDays(-60));
        var expired = P7F1V6Retention.ExpiredRuns(root, DateTime.UtcNow, 30);
        Assert.Equal(new[] { old.RunDirectory }, expired);
        Assert.Equal(1, P7F1V6Retention.DeleteRun(old.RunDirectory));
        Assert.False(Directory.Exists(old.RunDirectory));
        Assert.True(Directory.Exists(fresh.RunDirectory));
    }

    [Fact] public void A_run_directory_holding_foreign_content_or_a_non_run_name_is_never_deleted()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        Directory.CreateDirectory(Path.Combine(store.RunDirectory, "foreign"));
        Assert.Throws<InvalidOperationException>(() => P7F1V6Retention.DeleteRun(store.RunDirectory));
        Assert.True(Directory.Exists(store.RunDirectory));
        var plain = Path.Combine(root, "not-a-run");
        Directory.CreateDirectory(plain);
        Assert.Throws<InvalidOperationException>(() => P7F1V6Retention.DeleteRun(plain));
        Assert.True(Directory.Exists(plain));
    }

    // ---------------- symbolic links (skipped where the machine cannot create them) ----------------

    static bool TryCreateSymlink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Fact] public void A_symbolic_link_in_place_of_the_raw_root_is_refused()
    {
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root, "link");
        if (!TryCreateSymlink(link, real, directory: true)) return;   // this machine cannot create symbolic links: not tested here
        var ex = Assert.Throws<IOException>(() => P7F1V6SafeRawStore.CreateRun(link));
        Assert.Contains("RAW_ROOT_IS_REPARSE_POINT", ex.Message);
    }

    [Fact] public void A_symbolic_link_in_place_of_a_stored_item_is_refused_when_read()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var bytes = Encoding.UTF8.GetBytes("secret-looking-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var outside = Path.Combine(root, "outside.bin");
        File.WriteAllBytes(outside, bytes);
        var item = Path.Combine(store.RunDirectory, sha + ".bin");
        if (!TryCreateSymlink(item, outside, directory: false)) return;   // not tested here
        var ex = Assert.Throws<IOException>(() => store.ReadVerified(sha));
        Assert.Contains("RAW_ITEM_IS_REPARSE_POINT", ex.Message);
    }

    // ---------------- route data policy ----------------

    [Fact] public void The_route_gate_stays_closed_while_any_alibaba_fact_is_unknown()
    {
        var (allowed, blocking) = P7F1V6RoutePolicy.RouteAllowed("alibaba");
        Assert.False(allowed);
        Assert.Contains("alibaba:training on request data", blocking);
        Assert.Contains("alibaba:retention period of request data", blocking);
    }

    [Fact] public void Every_policy_fact_records_its_status_and_no_unverified_fact_claims_a_source()
    {
        Assert.All(P7F1V6RoutePolicy.Facts, f =>
        {
            Assert.Contains(f.Status, new[] { "VERIFIED", "UNKNOWN", "WAIVED_BY_USER" });
            if (f.Status == "VERIFIED") Assert.False(string.IsNullOrEmpty(f.Source));
            if (f.Status == "UNKNOWN") Assert.Null(f.Source);
            if (f.Status == "WAIVED_BY_USER") Assert.Contains("User decision", f.Note);
        });
    }

    // ---------------- response adapter ----------------

    [Fact] public void The_adapter_passes_choices_zero_message_content_unchanged()
    {
        var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"a\\\":1} trailing\"}}]}";
        Assert.Equal(("{\"a\":1} trailing", null), P7F1V6ResponseAdapter.ExtractContent(body));
    }

    [Theory]
    [InlineData("not json", "RESPONSE_NOT_JSON")]
    [InlineData("{\"choices\":[]}", "NO_CHOICES")]
    [InlineData("{\"choices\":[{\"message\":{}}]}", "CONTENT_NOT_TEXT")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}", "CONTENT_NOT_TEXT")]
    [InlineData("{\"choices\":[{}]}", "NO_MESSAGE")]
    public void A_response_without_text_content_is_reported_not_repaired(string body, string code) =>
        Assert.Equal(code, P7F1V6ResponseAdapter.ExtractContent(body).Failure);

    // ---------------- provider guard ----------------

    [Fact] public void The_provider_guard_lists_every_reason_when_nothing_is_in_place()
    {
        var reasons = P7F1V6ProviderGuard.Refusals(null, "pinned", safeStoreInUse: false, c0Summary: null, sourceGateOk: false, sourceGateCode: "SOURCE_MISSING");
        Assert.Contains("NO_AUTHORISATION_RECORD", reasons);
        Assert.Contains("SAFE_STORE_NOT_IN_USE", reasons);
        Assert.Contains("SOURCE_GATE:SOURCE_MISSING", reasons);
        Assert.Contains("C0_REPLAY_NOT_RUN", reasons);
        Assert.Contains("DATA_POLICY_NOT_VERIFIED:alibaba:training on request data", reasons);
    }

    [Fact] public void A_mismatched_pin_retries_and_ceiling_are_each_refused()
    {
        var auth = new P7F1V6ProviderGuard.AuthorisedRun("other-sha", "alibaba", "todo10_8/x.pdf", "src", MaxCalls: 6,
            CeilingUsd: 0.5m, Retries: 2, ApprovedBy: "test");
        var reasons = P7F1V6ProviderGuard.Refusals(auth, "pinned", true, null, true, "OK");
        Assert.Contains("CANDIDATE_SHA_NOT_PINNED", reasons);
        Assert.Contains("CEILING_OUT_OF_RANGE", reasons);
        Assert.Contains("RETRY_OUT_OF_RANGE", reasons);
    }

    [Fact] public void A_directory_junction_in_place_of_the_raw_root_is_refused()
    {
        // A junction is a reparse point like a symbolic link, and needs no administrator right to create.
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root, "junction");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); if (p.ExitCode != 0) return; }
        try
        {
            var ex = Assert.Throws<IOException>(() => P7F1V6SafeRawStore.CreateRun(link));
            Assert.Contains("RAW_ROOT_IS_REPARSE_POINT", ex.Message);
        }
        finally
        {
            // Remove the junction itself, without /S, so that nothing under its target is touched.
            RemoveJunction(link);
            Assert.True(Directory.Exists(real), "the junction target must survive the test");
        }
    }

    static void RemoveJunction(string link)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c rmdir \"{link}\"") { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    [Fact] public void The_zdr_waiver_is_recorded_but_does_not_open_the_route_gate()
    {
        var zdr = P7F1V6RoutePolicy.Facts.Single(f => f.Subject.StartsWith("zero data retention", StringComparison.Ordinal));
        Assert.Equal("WAIVED_BY_USER", zdr.Status);
        var (allowed, blocking) = P7F1V6RoutePolicy.RouteAllowed("alibaba");
        Assert.False(allowed);
        Assert.DoesNotContain(blocking, b => b.Contains("zero data retention", StringComparison.Ordinal));
        Assert.Contains("alibaba:training on request data", blocking);
    }
}
