using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Detect;

namespace DocxHeaderExtractor.Tests;

/// <summary>The format is read from the bytes. A PDF may carry junk before its header; anything else is not a PDF.</summary>
public sealed class UploadedSourceDetectorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-detect-" + Guid.NewGuid().ToString("N"));

    public UploadedSourceDetectorTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_pdf_with_its_header_at_the_start_is_a_pdf()
    {
        var path = Write("plain.pdf", "%PDF-1.4\n%rest"u8.ToArray());

        Assert.Equal(SourceType.Pdf, SourceDetector.Detect(path));
    }

    [Fact]
    public void A_pdf_with_junk_before_its_header_within_1024_bytes_is_a_pdf()
    {
        var junk = new byte[37];
        Array.Fill(junk, (byte)'\n');
        var path = Write("junk.pdf", junk.Concat("%PDF-1.7\n%rest"u8.ToArray()).ToArray());

        Assert.Equal(SourceType.Pdf, SourceDetector.Detect(path));
    }

    [Fact]
    public void A_file_without_the_pdf_header_in_its_first_1024_bytes_is_unknown()
    {
        var path = Write("text.pdf", new byte[1100].Select((_, i) => (byte)'a').Concat("%PDF-1.4"u8.ToArray()).ToArray());

        Assert.Equal(SourceType.Unknown, SourceDetector.Detect(path));
    }

    [Fact]
    public void A_short_or_empty_file_is_unknown()
    {
        Assert.Equal(SourceType.Unknown, SourceDetector.Detect(Write("empty.pdf", [])));
        Assert.Equal(SourceType.Unknown, SourceDetector.Detect(Write("short.pdf", "%PD"u8.ToArray())));
    }
}
