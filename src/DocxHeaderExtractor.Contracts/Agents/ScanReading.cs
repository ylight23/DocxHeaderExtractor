namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>The text lines of a scan in reading order, each with its reading and its place on the page image, and what the reading cost
/// and missed (transcription calls, failed transcriptions, lines left out for having no readable text).</summary>
public sealed record ScanReading(string SourceSha256, IReadOnlyList<ScanSourceLine> Lines, ScanRunDiagnostics Diagnostics);
