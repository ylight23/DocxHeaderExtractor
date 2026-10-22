using System.Text.Encodings.Web;
using System.Text.Json;
using MessagePack;
using DocxHeaderExtractor.Contracts.Scan;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// Reads a stored scan evidence file (MessagePack) and writes it as indented JSON, so a scan's evidence can be read while debugging.
/// The file is the one the scan writes beside its page images; this reads it and changes nothing.
/// </summary>
public static class ScanEvidenceJson
{
    /// <summary>The evidence in <paramref name="evidenceFile"/> as indented JSON text.</summary>
    public static string Read(string evidenceFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFile);
        var evidence = MessagePackSerializer.Deserialize<ScanOcrEvidence>(File.ReadAllBytes(evidenceFile), ScanOcrPreflight.EvidenceBinary);
        return JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
