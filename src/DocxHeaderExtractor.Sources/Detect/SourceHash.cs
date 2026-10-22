using System.Security.Cryptography;

namespace DocxHeaderExtractor.Sources.Detect;

public static class SourceHash
{
    public static string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
