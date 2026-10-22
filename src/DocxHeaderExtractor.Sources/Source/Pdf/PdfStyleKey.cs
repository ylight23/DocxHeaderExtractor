using MessagePack;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>Parser appearance identity for layout grouping; it never classifies heading membership.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfStyleKey([property: Key(0)] double FontSizeBucket, [property: Key(1)] string FontName, [property: Key(2)] string FillColorKey)
{
    public static PdfStyleKey StyleOf(PdfLine line, double fontSizeBucket = 0.5)
    {
        var bucket = fontSizeBucket <= 0 ? line.FontSize : Math.Round(line.FontSize / fontSizeBucket) * fontSizeBucket;
        return new PdfStyleKey(bucket, line.FontName, line.FillColorKey);
    }
}
