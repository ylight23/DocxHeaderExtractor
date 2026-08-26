using System.Security.Cryptography;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Logical identity for frozen benchmark manifests. The sole supported normalization is CRLF to LF;
/// every other byte is preserved, and unsupported text encodings fail closed rather than widening the
/// authority contract into generic JSON canonicalization.
/// </summary>
internal static class N0BenchmarkManifestIdentity
{
    internal static string ComputeCanonicalN0ManifestSha256(string path) =>
        ComputeCanonicalN0ManifestSha256(File.ReadAllBytes(path));

    internal static string ComputeCanonicalN0ManifestSha256(ReadOnlySpan<byte> raw)
    {
        if (raw.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new InvalidDataException("Benchmark manifest canonical hash does not support a UTF-8 BOM.");

        using var canonical = new MemoryStream(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != (byte)'\r')
            {
                canonical.WriteByte(raw[index]);
                continue;
            }

            if (index + 1 >= raw.Length || raw[index + 1] != (byte)'\n')
                throw new InvalidDataException("Benchmark manifest canonical hash does not support a lone CR byte.");
            canonical.WriteByte((byte)'\n');
            index++;
        }

        return Convert.ToHexString(SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)))).ToLowerInvariant();
    }
}

public sealed class N0BenchmarkManifestIdentityTests
{
    private const string HistoricalHash = "529fecc53341c12e06fd34a873c544acddc6d96388670c4da80c7700030a01a7";

    [Fact]
    public void N0ManifestCanonicalHashMatchesHistoricalAuthorityAcrossLineEndings()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var raw = File.ReadAllBytes(Path.Combine(root, "keys", "benchmark-n0", "manifest.json"));
        var lf = ToLf(raw);
        var crlf = ToCrLf(lf);

        Assert.Equal(HistoricalHash, N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(lf));
        Assert.Equal(HistoricalHash, N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(crlf));
        Assert.NotEqual(ComputeRawSha256(lf), ComputeRawSha256(crlf));
    }

    [Fact]
    public void SemanticByteMutationChangesCanonicalHash()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var bytes = File.ReadAllBytes(Path.Combine(root, "keys", "benchmark-n0", "manifest.json"));
        var mutated = bytes.ToArray();
        var index = Array.IndexOf(mutated, (byte)'N');
        Assert.True(index >= 0);
        mutated[index] = (byte)'X';

        Assert.NotEqual(
            N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(bytes),
            N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(mutated));
    }

    [Fact]
    public void UnsupportedBomAndLoneCrFailClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(new byte[] { 0xef, 0xbb, 0xbf, (byte)'{' }));
        Assert.Throws<InvalidDataException>(() =>
            N0BenchmarkManifestIdentity.ComputeCanonicalN0ManifestSha256(new byte[] { (byte)'{', (byte)'\r', (byte)'}' }));
    }

    private static byte[] ToLf(byte[] bytes)
    {
        using var output = new MemoryStream(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'\n') index++;
            output.WriteByte(bytes[index]);
        }
        return output.ToArray();
    }

    private static byte[] ToCrLf(byte[] bytes)
    {
        using var output = new MemoryStream(bytes.Length * 2);
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') output.WriteByte((byte)'\r');
            output.WriteByte(value);
        }
        return output.ToArray();
    }

    private static string ComputeRawSha256(ReadOnlySpan<byte> raw) =>
        Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
}
