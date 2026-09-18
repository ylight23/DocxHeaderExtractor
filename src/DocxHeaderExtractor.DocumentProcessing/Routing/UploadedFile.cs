using System.Security.Cryptography;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>
/// The file the user actually uploaded.
/// <para>
/// <see cref="DetectedType"/> is computed here from the bytes on disk. It is never taken from the
/// client, and never inferred from <see cref="OriginalFileName"/>, which exists only so a download
/// can carry the name the user recognises. A caller that could set the type would be able to send
/// a PDF down the DOCX lane by claiming it was one.
/// </para>
/// </summary>
public sealed record UploadedFile
{
    private UploadedFile(string originalFileName, string localPath, SourceType detectedType, string sha256)
    {
        OriginalFileName = originalFileName;
        LocalPath = localPath;
        DetectedType = detectedType;
        Sha256 = sha256;
    }

    /// <summary>Display and download provenance only. Never routing evidence.</summary>
    public string OriginalFileName { get; }

    public string LocalPath { get; }

    public SourceType DetectedType { get; }

    /// <summary>Identity of the bytes that were actually processed.</summary>
    public string Sha256 { get; }

    /// <summary>Reads the file, identifies it by content, and records the hash of what was read.</summary>
    public static UploadedFile FromLocalPath(string localPath, string? originalFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Uploaded file not found.", localPath);

        return new UploadedFile(
            originalFileName ?? Path.GetFileName(localPath),
            localPath,
            UploadedSourceDetector.Detect(localPath),
            Sha256Of(localPath));
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>
/// What the authority boundary is asked for: one file, nothing about what the user wants out of it.
/// <para>
/// Intent is deliberately absent. The same file must always produce the same canonical document,
/// so that asking a different question can never change what the document is held to contain.
/// Intent belongs to <see cref="ProjectionRequest"/>, on the far side of the semantic boundary.
/// </para>
/// </summary>
public sealed record AuthorityExtractionRequest(UploadedFile File);

/// <summary>Raised when the uploaded bytes are not a format this system extracts.</summary>
public sealed class UnsupportedSourceException(UploadedFile file)
    : NotSupportedException($"'{file.OriginalFileName}' was identified as {file.DetectedType} by its content and cannot be extracted.")
{
    public UploadedFile File { get; } = file;
}
