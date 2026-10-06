namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Wire limits of the promoted PDF authority route. These are production policy, not
/// qualification-adapter implementation details; qualification reuses them to preserve parity.
/// </summary>
internal static class PdfQualifiedInferencePolicy
{
    internal const int CompletionTokenCeiling = 32_768;
    internal const int ResponseUtf8ByteCap = 49_152;
}
