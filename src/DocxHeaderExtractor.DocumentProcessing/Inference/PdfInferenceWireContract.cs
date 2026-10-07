namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>
/// Wire limits of the promoted PDF authority route. These are wire contract, not
/// qualification-adapter implementation details; qualification reuses them to preserve parity.
/// </summary>
internal static class PdfInferenceWireContract
{
    internal const int CompletionTokenCeiling = 32_768;
    internal const int ResponseUtf8ByteCap = 49_152;
}
