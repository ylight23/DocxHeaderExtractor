using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A committed freeze artifact, compared in an ordinary run and rewritten only when asked.
/// <para>
/// These files are authority: frozen arm comparisons and preflight reports name them, and other
/// tests hash them. They were also being rewritten by the ordinary suite on every run, which made
/// the test that reads them a test of what this machine had just produced - and, worse, left the
/// working tree dirty after a green run.
/// </para>
/// <para>
/// The dirt was not a content difference. <c>git diff</c> was empty, because the clean filter
/// normalizes both sides. But an artifact written with LF no longer matches the CRLF that checkout
/// would produce on Windows, so Git reports it modified to avoid overwriting local work - and
/// <c>git checkout</c> and <c>git bisect</c> abort. That happened here: two commits under
/// investigation could not be checked out, and the run that looked like it measured them had
/// actually re-measured the commit already in the tree.
/// </para>
/// <para>
/// So an ordinary run compares and writes nothing. Regenerating is an explicit, separate act:
/// <c>A99_FREEZE_UPDATE=1 dotnet test --filter ...</c>. The comparison is over LF-normalized text
/// for the same reason the prompt is normalized where it is built - what the artifact says must not
/// depend on how it was checked out - while the bytes written in update mode are canonical, so
/// regenerating twice produces the same file.
/// </para>
/// </summary>
internal static class FreezeArtifact
{
    /// <summary>Set to 1 or true to rewrite artifacts instead of comparing against them.</summary>
    public const string UpdateVariable = "A99_FREEZE_UPDATE";

    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true" or "TRUE";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes with the freeze options, then compares or rewrites.</summary>
    public static void AssertJson(string directory, string name, object value) =>
        AssertText(directory, name, JsonSerializer.Serialize(value, Json));

    public static void AssertText(string directory, string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        var path = Path.Combine(TestRepository.Root(), directory.Replace('/', Path.DirectorySeparatorChar), name);
        var canonical = content.ReplaceLineEndings("\n");

        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // UTF-8 with no BOM and LF, explicitly - not whatever this platform defaults to.
            File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(canonical));
            return;
        }

        Assert.True(File.Exists(path),
            $"missing freeze artifact {directory}/{name}; regenerate with {UpdateVariable}=1");
        var committed = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (string.Equals(committed, canonical, StringComparison.Ordinal)) return;

        Assert.Fail(
            $"""
             {directory}/{name} no longer matches what this run produces.
             This is an authority artifact, so the difference is a finding, not a file to refresh:
             establish why it moved first, then regenerate with {UpdateVariable}=1.
             committed sha256 {Sha256(committed)} ({committed.Length} chars)
             produced  sha256 {Sha256(canonical)} ({canonical.Length} chars)
             """);
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
