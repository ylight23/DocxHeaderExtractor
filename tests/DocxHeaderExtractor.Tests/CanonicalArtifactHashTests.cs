using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The identity rule for frozen artifacts, stated as a property rather than as a list of values.
/// <para>
/// The defect this closes: <c>predictionSha256</c> was taken over the file in the working tree, so
/// on Windows with <c>core.autocrlf=true</c> it digested the CRLF rendering of an LF blob. The
/// recorded values therefore described bytes that exist nowhere in the repository, and the audits
/// verifying them could only pass on that one checkout configuration. Pinning new numbers alone
/// would not have fixed it - the same audit would fail again the moment it ran somewhere else.
/// </para>
/// </summary>
public sealed class CanonicalArtifactHashTests
{
    private const string Json = "{\n  \"a\": 1,\n  \"b\": [2, 3]\n}\n";

    [Fact]
    public void The_same_document_hashes_the_same_under_every_line_ending_convention()
    {
        // The property that makes the audit portable. Whichever way the file arrives - a Linux
        // checkout, a Windows checkout, an old Mac convention - it is the same artifact.
        var lf = CanonicalArtifactHash.OfText(Json);
        var crlf = CanonicalArtifactHash.OfText(Json.Replace("\n", "\r\n"));
        var cr = CanonicalArtifactHash.OfText(Json.Replace("\n", "\r"));

        Assert.Equal(lf, crlf);
        Assert.Equal(lf, cr);
    }

    [Fact]
    public void The_canonical_hash_is_the_hash_of_the_LF_bytes()
    {
        // Not an arbitrary new number: it is the digest of the content as the repository stores it,
        // which is what "the artifact" has meant all along.
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json))),
            CanonicalArtifactHash.OfText(Json.Replace("\n", "\r\n")));
    }

    [Fact]
    public void A_byte_order_mark_does_not_change_an_artifact_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-bom-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.Equal(CanonicalArtifactHash.OfText(Json), CanonicalArtifactHash.OfTextFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Content_changes_still_change_the_hash()
    {
        // Normalization must not be so eager that it stops distinguishing documents.
        Assert.NotEqual(
            CanonicalArtifactHash.OfText(Json),
            CanonicalArtifactHash.OfText(Json.Replace("\"a\": 1", "\"a\": 2")));
        // Whitespace inside the document is content, not a line-ending convention.
        Assert.NotEqual(
            CanonicalArtifactHash.OfText(Json),
            CanonicalArtifactHash.OfText(Json.Replace("  \"a\"", "    \"a\"")));
    }

    [Fact]
    public void A_source_document_keeps_a_byte_exact_identity()
    {
        // The line this rule must not cross. A DOCX is the source itself; folding bytes inside one
        // would not make it portable, it would corrupt it.
        var path = Path.Combine(Path.GetTempPath(), $"dhx-bin-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] bytes = [0x50, 0x4B, 0x03, 0x04, 0x0D, 0x0A, 0x00, 0x0D, 0x00];
            File.WriteAllBytes(path, bytes);
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                CanonicalArtifactHash.OfBytes(path));
            Assert.NotEqual(CanonicalArtifactHash.OfBytes(path), CanonicalArtifactHash.OfTextFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_verified_freeze_declares_the_rule_its_hashes_follow()
    {
        // Without the declaration the field reads as a raw-byte digest, and the next person to
        // regenerate one would produce a value this audit rejects for reasons it never states.
        string[] freezes =
        [
            "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/freeze.v1.json",
            "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/freeze.v1.json",
            "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/freeze.v2.json",
            "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/freeze.v1.json",
            "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r3/whole-alias/freeze.v1.json",
        ];

        Assert.All(freezes, relative =>
        {
            var path = Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            using var freeze = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                CanonicalArtifactHash.Contract,
                freeze.RootElement.GetProperty(CanonicalArtifactHash.ContractField).GetString());
        });
    }

    [Fact]
    public void The_frozen_DOC_0205_prediction_is_pinned_to_its_committed_content()
    {
        // The one concrete value, tied to the artifact rather than to the machine. If a checkout
        // ever hands this file CRLF again, this still holds - which is the whole point.
        var path = Path.Combine(RepositoryRoot(),
            "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/prediction.v1.json"
                .Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(
            "a6d65ce827401124c686eb4a3c450f9cda12898a0372ce23f7fb464401c6c164",
            CanonicalArtifactHash.OfTextFile(path));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
