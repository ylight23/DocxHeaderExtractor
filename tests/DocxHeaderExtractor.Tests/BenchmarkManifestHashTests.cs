using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class BenchmarkManifestHashTests
{
    private const string HistoricalManifestSha256 = "529fecc53341c12e06fd34a873c544acddc6d96388670c4da80c7700030a01a7";

    [Fact]
    public void RepositoryManifestMatchesHistoricalCanonicalAuthority()
    {
        var path = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "keys", "benchmark-n0", "manifest.json");
        Assert.Equal(HistoricalManifestSha256, BenchmarkManifestHash.ComputeCanonicalSha256(path));
    }

    [Fact]
    public void LfAndCrlfBytesShareCanonicalHashButNotRawHash()
    {
        var lf = Encoding.UTF8.GetBytes("{\n  \"documents\": [\n    \"003\"\n  ]\n}\n");
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(lf).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.NotEqual(RawHash(lf), RawHash(crlf));
        Assert.Equal(BenchmarkManifestHash.ComputeCanonicalSha256(lf), BenchmarkManifestHash.ComputeCanonicalSha256(crlf));
    }

    [Fact]
    public void SemanticByteMutationChangesCanonicalHash()
    {
        var original = Encoding.UTF8.GetBytes("{\n  \"version\": 1\n}\n");
        var mutated = original.ToArray();
        mutated[mutated.AsSpan().IndexOf((byte)'1')] = (byte)'2';

        Assert.NotEqual(BenchmarkManifestHash.ComputeCanonicalSha256(original), BenchmarkManifestHash.ComputeCanonicalSha256(mutated));
    }

    [Fact]
    public void LoneCrIsRejectedInsteadOfSilentlyNormalized()
    {
        Assert.Throws<InvalidDataException>(() => BenchmarkManifestHash.ComputeCanonicalSha256(Encoding.UTF8.GetBytes("{\r\n}\r")));
    }

    private static string RawHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
