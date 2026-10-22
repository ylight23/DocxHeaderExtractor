using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Contracts.Authority;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

public static class HierarchyFactHash
{
    public static string OfText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
