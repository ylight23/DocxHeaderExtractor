using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Raw store for real runs (Gate S1, D3.3). One directory per run, created fresh. Each item is written to a temporary file in that
/// directory, flushed to disk, then moved into place with no-overwrite semantics, and read back from disk to confirm its hash. Names
/// are the SHA-256 of the content, validated as 64 lowercase hex characters, so no name can carry a path. Reparse points (symbolic
/// links and junctions) are refused for the root, the run directory and every file. On Windows the run directory is restricted to the
/// current user with inheritance removed. Nothing here sends data; it stores bytes.
/// </summary>
internal sealed class P7F1V6SafeRawStore
{
    static readonly Regex Sha256Name = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    readonly string runDirectory;

    P7F1V6SafeRawStore(string runDirectory) { this.runDirectory = runDirectory; }

    public string RunDirectory => runDirectory;

    /// <summary>Creates a new run directory under root. Fails if the root is missing, is a reparse point, or the run name exists.</summary>
    public static P7F1V6SafeRawStore CreateRun(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("RAW_ROOT_MISSING");
        RefuseReparse(full, "RAW_ROOT_IS_REPARSE_POINT");
        var run = Path.Combine(full, "run-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(run) || File.Exists(run)) throw new IOException("RUN_DIRECTORY_EXISTS");
        Directory.CreateDirectory(run);
        RestrictToCurrentUser(run);
        return new P7F1V6SafeRawStore(run);
    }

    /// <summary>Writes bytes under their hash. Returns the hash only after the file on disk has been read back and matches it.</summary>
    public string Put(byte[] bytes)
    {
        var sha = Hash(bytes);
        var final = PathFor(sha);
        if (File.Exists(final)) return ReadVerified(sha) is not null ? sha : throw new IOException("STORED_HASH_MISMATCH");
        var temp = Path.Combine(runDirectory, sha + "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        try { File.Move(temp, final, overwrite: false); }
        catch (IOException) when (File.Exists(final)) { File.Delete(temp); }   // another thread stored the same bytes first
        return ReadVerified(sha) is not null ? sha : throw new IOException("STORED_HASH_MISMATCH");
    }

    /// <summary>Reads an item from disk and returns it only if its hash is the name it was stored under. Null when absent or altered.</summary>
    public byte[]? ReadVerified(string sha)
    {
        if (!Sha256Name.IsMatch(sha)) throw new ArgumentException("INVALID_STORE_NAME");
        var path = PathFor(sha);
        if (!File.Exists(path)) return null;
        RefuseReparse(path, "RAW_ITEM_IS_REPARSE_POINT");
        var bytes = File.ReadAllBytes(path);
        return Hash(bytes) == sha ? bytes : null;
    }

    /// <summary>Checks every stored item on disk again. Returns the names that fail; an empty list means the run store is intact.</summary>
    public IReadOnlyList<string> VerifyOnDisk()
    {
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(runDirectory, "*.bin"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!Sha256Name.IsMatch(name)) { bad.Add(name); continue; }
            RefuseReparse(file, "RAW_ITEM_IS_REPARSE_POINT");
            if (Hash(File.ReadAllBytes(file)) != name) bad.Add(name);
        }
        return bad;
    }

    string PathFor(string sha) => Path.Combine(runDirectory, sha + ".bin");

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static void RefuseReparse(string path, string code)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException(code);
    }

    /// <summary>Restricts a raw root to the current user, as each run directory is (D4.5a). The operator prepares the root once.</summary>
    [SupportedOSPlatform("windows")]
    public static void RestrictRootToCurrentUser(string root) => RestrictToCurrentUser(Path.GetFullPath(root));

    /// <summary>Every rule on the directory, inherited ones included, must be an Allow for the current user, with inheritance cut off.
    /// Returns what is wrong; empty when the root is private to the current user.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> RootAccessFailures(string root)
    {
        var failures = new List<string>();
        var security = new DirectoryInfo(root).GetAccessControl();
        if (!security.AreAccessRulesProtected) failures.Add("ACL_INHERITS_FROM_PARENT");
        var user = WindowsIdentity.GetCurrent().User!.Value;
        foreach (FileSystemAccessRule r in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.IdentityReference.Value != user || r.AccessControlType != AccessControlType.Allow)
                failures.Add("ACL_OTHER_PRINCIPAL:" + r.IdentityReference.Value + ":" + r.AccessControlType);
        return failures;
    }

    [SupportedOSPlatform("windows")]
    static void RestrictToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;   // on other systems the owner-only mode must be set by the operator; see the S1 record
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            security.RemoveAccessRule(rule);
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>The access rules a verifier would read: only the current user, with inheritance removed. Empty on non-Windows systems.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> AccessRulesOf(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
        return security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value + ":" + r.AccessControlType).ToArray();
    }
}

/// <summary>Data policy for the qualification (Gate S2). Proposed values, pending approval. Redaction never replaces the original source identity.</summary>
internal static class P7F1V6DataPolicy
{
    /// <summary>Documents that may be sent: the public heading corpus only, by repository-relative path prefix.</summary>
    public static readonly string[] AllowedSourcePrefixes = ["todo10_8/"];
    public const int RawRetentionDays = 30;          // proposed; the reviewer approves the value
    public const string RedactionPolicy = "NO_REDACTION_IN_THIS_PHASE";

    public static bool IsAllowedSource(string repositoryRelativePath)
    {
        var p = repositoryRelativePath.Replace('\\', '/');
        if (p.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p)) return false;
        return AllowedSourcePrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The identity of a source as it is recorded. A redacted copy is never recorded as the original.</summary>
    public static string SourceIdentity(string originalSha256, string? redactedSha256) =>
        redactedSha256 is null ? "ORIGINAL:" + originalSha256 : "REDACTED_COPY:" + redactedSha256 + ":OF:" + originalSha256;
}
