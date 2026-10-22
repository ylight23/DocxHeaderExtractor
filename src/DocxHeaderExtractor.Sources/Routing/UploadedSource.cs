namespace DocxHeaderExtractor.DocumentProcessing.Routing;

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

    /// <summary>The spec lets a PDF carry some bytes before its header, as long as the header lies within the first 1024 bytes.</summary>
    private const int PdfHeaderWindow = 1024;

    public static SourceType Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return SourceType.Unknown;

        var head = new byte[PdfHeaderWindow];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.Read(head, 0, head.Length);
        }
        if (read < 4) return SourceType.Unknown;

        var header = head.AsSpan(0, 4);
        // Every OOXML package is a ZIP. That is necessary, not sufficient - an .xlsx is also a ZIP -
        // so the package is opened and asked for the part that makes it a word-processing document.
        if (header.SequenceEqual(ZipMagic)) return IsWordPackage(path) ? SourceType.Docx : SourceType.Unknown;
        if (header.SequenceEqual(PdfMagic)) return SourceType.Pdf;
        return head.AsSpan(0, read).IndexOf(PdfMagic) >= 0 ? SourceType.Pdf : SourceType.Unknown;
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
