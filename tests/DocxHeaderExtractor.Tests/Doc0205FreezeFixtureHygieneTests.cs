using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The DOC-0205 freeze fixtures are test authority, so they are version-controlled and pinned here.
/// <para>
/// Doc0205SemanticContractAuditTests hashes these artifacts and compares them against their freeze.
/// That guard only means something if the artifacts are in the repository: an untracked file can
/// change freely, and the test would then pass or fail depending on which machine ran it. One of
/// the three pairs was untracked and the audit failed on any clean checkout - not because anything
/// had drifted, but because the files were not there.
/// </para>
/// <para>
/// These assertions exist so that replacing a freeze authority is a deliberate act. Editing one of
/// these files now breaks this test by name, rather than quietly changing what "unchanged" means.
/// </para>
/// </summary>
public sealed class Doc0205FreezeFixtureHygieneTests
{
    private static readonly (string Directory, string Freeze)[] FrozenRuns =
    [
        ("eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205", "freeze.v1.json"),
        ("eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling", "freeze.v1.json"),
        ("eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2", "freeze.v2.json"),
    ];

    private const string SourceSha =
        "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878";

    [Fact]
    public void Every_frozen_run_the_audit_reads_is_present_in_the_repository()
    {
        Assert.All(FrozenRuns, run =>
        {
            var directory = Path.Combine(RepositoryRoot(), run.Directory.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(directory), $"missing frozen run: {run.Directory}");
            Assert.True(File.Exists(Path.Combine(directory, run.Freeze)), $"missing authority: {run.Freeze}");
        });
    }

    [Fact]
    public void Each_freeze_authority_names_the_same_source_and_was_taken_before_gold()
    {
        // The three runs disagree about the model and the execution mode, which is the point of
        // having three. What they must agree on is the document they were run against, and that
        // none of them saw gold first - otherwise the freeze is not an independent authority.
        Assert.All(FrozenRuns, run =>
        {
            using var freeze = Open(run.Directory, run.Freeze);
            Assert.Equal(SourceSha, freeze.RootElement.GetProperty("sourceSha256").GetString());
            Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        });
    }

    [Fact]
    public void The_recovered_freeze_is_the_authority_it_claims_to_be()
    {
        // Pinned by content, not by presence. This is the run whose artifacts were missing from
        // version control; if the file is ever replaced, the hash below says so by name rather
        // than letting the audit fail somewhere further downstream.
        var freeze = Open(FrozenRuns[0].Directory, FrozenRuns[0].Freeze);
        using (freeze)
        {
            Assert.Equal("qwen/qwen3.5-9b", freeze.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                "87806218db77813629e50fd5ce31a6e38fead71329426d6fce4f2edb37adadd1",
                freeze.RootElement.GetProperty("predictionSha256").GetString());
            Assert.Equal(
                "9fa17ebe58fa322e09f573cb254aa683929ed1010be8979ecfffc09316d61e49",
                freeze.RootElement.GetProperty("resultSha256").GetString());
        }
    }

    [Fact]
    public void The_committed_fixtures_carry_no_credential_or_machine_local_path()
    {
        // Provider-run artifacts are committed here as authority, so what they carry is a
        // deliberate decision rather than whatever the runner happened to write. Checked over the
        // raw text, because a credential can sit inside a generic field as easily as a named one.
        var suspicious = new[]
        {
            "api_key", "apikey", "authorization", "bearer ", "secret", "password",
            "http://", "https://", "C:\\\\Users", "/home/", "/Users/",
        };

        Assert.All(FrozenRuns, run =>
        {
            var directory = Path.Combine(RepositoryRoot(), run.Directory.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var text = File.ReadAllText(file);
                foreach (var needle in suspicious)
                    Assert.False(text.Contains(needle, StringComparison.OrdinalIgnoreCase),
                        $"{Path.GetFileName(file)} contains {needle}");
            }
        });
    }

    private static JsonDocument Open(string directory, string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), directory.Replace('/', Path.DirectorySeparatorChar), file)));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
