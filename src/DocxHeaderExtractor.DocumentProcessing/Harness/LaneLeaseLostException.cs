namespace DocxHeaderExtractor.DocumentProcessing.Harness;

internal sealed class LaneLeaseLostException : InvalidOperationException
{
    public LaneLeaseLostException()
        : base("The PDF execution lease is no longer active; late provider work was quarantined.")
    {
    }
}
