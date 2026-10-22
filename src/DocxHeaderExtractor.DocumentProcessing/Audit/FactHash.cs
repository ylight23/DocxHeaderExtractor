using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.DocumentProcessing.Audit;

public static class FactHash
{
    public static string OfText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
