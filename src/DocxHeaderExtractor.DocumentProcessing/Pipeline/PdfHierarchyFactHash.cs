using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

public static class PdfHierarchyFactHash
{
    public static string OfText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
