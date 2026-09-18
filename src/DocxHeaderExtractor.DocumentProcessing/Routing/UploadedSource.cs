namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>What the uploaded file actually is, read from its bytes rather than its name.</summary>
public enum SourceType
{
    Unknown,
    Docx,
    Pdf,
}

/// <summary>
/// The one file the caller asked about, plus whether an analyst is available to reason over it.
/// <para>
/// This deliberately describes a single upload rather than a set of capabilities the machine
/// happens to have. The previous contract asked "is there a DOCX? is there a PDF?", which let a
/// file nobody uploaded decide how the uploaded one was processed.
/// </para>
/// </summary>
public sealed record UploadedSource(SourceType Type, bool AnalystAvailable);

/// <summary>
/// Identifies an uploaded file by its content.
/// <para>
/// The extension is a claim by whoever named the file, not evidence. A .docx that is really a PDF,
/// or a .pdf that is really a ZIP, must be routed by what it is - otherwise the first honest error
/// surfaces deep inside a parser that was never given the format it expects.
/// </para>
/// </summary>
public static class UploadedSourceDetector
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46];

    public static SourceType Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return SourceType.Unknown;

        Span<byte> header = stackalloc byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header) < header.Length) return SourceType.Unknown;
        }

        if (header.SequenceEqual(PdfMagic)) return SourceType.Pdf;
        // Every OOXML package is a ZIP. That is necessary, not sufficient - an .xlsx is also a ZIP -
        // so the package is opened and asked for the part that makes it a word-processing document.
        if (!header.SequenceEqual(ZipMagic)) return SourceType.Unknown;
        return IsWordPackage(path) ? SourceType.Docx : SourceType.Unknown;
    }

    private static bool IsWordPackage(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            return archive.GetEntry("word/document.xml") is not null;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
