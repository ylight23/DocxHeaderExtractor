using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// How a frozen JSON artifact is identified, and why it is not the raw bytes.
/// <para>
/// <c>predictionSha256</c> and <c>resultSha256</c> used to be taken over the file as it sat in the
/// working tree. On Windows with <c>core.autocrlf=true</c> that is the CRLF rendering; the blob in
/// the repository is LF. So the recorded values described bytes that exist nowhere in the
/// repository, and the audits verifying them passed on one checkout configuration and failed on
/// every other - the same defect as the prompt that was 2,330 characters in one checkout and 2,301
/// in another, moved from the provider request to the evaluation authority.
/// </para>
/// <para>
/// A prediction or result file is a JSON document, not a forensic capture of provider transport.
/// Its line endings carry no meaning, so they must not be part of its identity: the artifact is
/// hashed as canonical text - UTF-8, no BOM, every CRLF and lone CR folded to LF. Freezes that use
/// this declare <see cref="Contract"/> so the field cannot be mistaken for a raw-byte digest.
/// </para>
/// <para>
/// This is deliberately not applied to source documents. A DOCX or a PDF is the thing itself, and
/// <c>sourceSha256</c> stays byte-exact: normalizing a binary would not make it portable, it would
/// corrupt it.
/// </para>
/// </summary>
internal static class CanonicalArtifactHash
{
    /// <summary>The canonicalization a freeze declares when its hashes follow this rule.</summary>
    public const string Contract = "utf8-lf-v1";

    /// <summary>The field a freeze carries to declare it.</summary>
    public const string ContractField = "artifactHashCanonicalization";

    public static string OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var canonical = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexStringLower(SHA256.HashData(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(canonical)));
    }

    /// <summary>Reads as text, which also drops a byte-order mark if one is present.</summary>
    public static string OfTextFile(string path) => OfText(File.ReadAllText(path));

    /// <summary>Byte-exact identity, for a source document rather than an artifact about one.</summary>
    public static string OfBytes(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
