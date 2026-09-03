using System.Security.Cryptography;

using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// Computes the stable identity of a text benchmark manifest. Git may materialize the same
/// UTF-8 JSON with LF or CRLF; only that proven serialization difference is normalized.
/// </summary>
public static class BenchmarkManifestHash
{
    public const string AlgorithmVersion = "crlf-to-lf-v1";

    public static string ComputeCanonicalSha256(string path) =>
        ComputeCanonicalSha256(File.ReadAllBytes(path));

    public static string ComputeCanonicalSha256(ReadOnlySpan<byte> bytes)
    {
        using var canonical = new MemoryStream(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            {
                if (i + 1 >= bytes.Length || bytes[i + 1] != (byte)'\n')
                    throw new InvalidDataException("Benchmark manifest contains a lone CR; canonical hashing is not defined for it.");

                canonical.WriteByte((byte)'\n');
                i++;
                continue;
            }

            canonical.WriteByte(bytes[i]);
        }

        return Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant();
    }
}
