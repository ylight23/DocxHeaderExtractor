using System.Security.Cryptography;

namespace DocxHeaderExtractor.DocumentProcessing.Provenance;

public static class CanonicalSemanticSourceHash
{
    public static string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
