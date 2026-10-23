namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>
/// Reads a scanned document into text lines, with no decision about what the lines are: the lines are cut from the page images and the
/// agent's model transcribes each one. Any problem reads a scan through this; the heading selection runs on the same reading.
/// </summary>
public interface IScanReader
{
    /// <param name="apiKey">The provider key the transcription calls the model with. Null transcribes nothing, so no line has text.</param>
    Task<ScanReading> ReadAsync(string path, string? apiKey, CancellationToken ct);
}
