using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.3 (offline): the raw store for real runs (Gate S1) and the data policy (Gate S2). Each test uses its own scratch root.</summary>
public sealed class P7F1V6StoreD33Tests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "dhx-d33-" + Guid.NewGuid().ToString("N"));

    public P7F1V6StoreD33Tests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact] public void A_stored_item_reads_back_verified_under_its_hash()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var bytes = Encoding.UTF8.GetBytes("{\"owned\":[\"Trích\"]}");
        var sha = store.Put(bytes);
        Assert.Equal(Sha(bytes), sha);
        Assert.Equal(bytes, store.ReadVerified(sha));
    }

    [Fact] public void Writing_the_same_bytes_twice_gives_one_verified_item()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var bytes = Encoding.UTF8.GetBytes("same");
        Assert.Equal(store.Put(bytes), store.Put(bytes));
        Assert.Empty(Directory.EnumerateFiles(store.RunDirectory, "*.tmp"));
        Assert.Empty(store.VerifyOnDisk());
    }

    [Fact] public void An_item_altered_on_disk_is_not_returned_and_is_listed_by_the_disk_check()
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var sha = store.Put(Encoding.UTF8.GetBytes("original"));
        File.WriteAllBytes(Path.Combine(store.RunDirectory, sha + ".bin"), Encoding.UTF8.GetBytes("altered"));
        Assert.Null(store.ReadVerified(sha));
        Assert.Contains(sha, store.VerifyOnDisk());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void A_name_that_is_not_a_sha256_hex_string_is_refused(string name)
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        Assert.Throws<ArgumentException>(() => store.ReadVerified(name));
    }

    [Fact] public void Each_run_gets_a_new_directory_under_the_root()
    {
        var a = P7F1V6SafeRawStore.CreateRun(root);
        var b = P7F1V6SafeRawStore.CreateRun(root);
        Assert.NotEqual(a.RunDirectory, b.RunDirectory);
        Assert.StartsWith(Path.GetFullPath(root), a.RunDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void A_missing_root_is_refused()
    {
        Assert.Throws<DirectoryNotFoundException>(() => P7F1V6SafeRawStore.CreateRun(Path.Combine(root, "absent")));
    }

    [Fact] public void On_Windows_the_run_directory_grants_access_to_the_current_user_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = P7F1V6SafeRawStore.CreateRun(root);
        var rules = P7F1V6SafeRawStore.AccessRulesOf(store.RunDirectory);
        var me = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Single(rules);
        Assert.Equal(me + ":Allow", rules[0]);
    }

    [Fact] public void Source_identity_of_a_redacted_copy_never_reads_as_the_original()
    {
        var original = Sha(Encoding.UTF8.GetBytes("pdf"));
        var redacted = Sha(Encoding.UTF8.GetBytes("pdf redacted"));
        Assert.StartsWith("ORIGINAL:", P7F1V6DataPolicy.SourceIdentity(original, null), StringComparison.Ordinal);
        var id = P7F1V6DataPolicy.SourceIdentity(original, redacted);
        Assert.StartsWith("REDACTED_COPY:", id, StringComparison.Ordinal);
        Assert.DoesNotContain("ORIGINAL:", id, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf", true)]
    [InlineData("todo10_8/../secrets/key.pdf", false)]
    [InlineData("other/docs/x.pdf", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Users/x/x.pdf", false)]
    public void Only_the_public_heading_corpus_may_be_sent(string path, bool allowed) =>
        Assert.Equal(allowed, P7F1V6DataPolicy.IsAllowedSource(path));
}
