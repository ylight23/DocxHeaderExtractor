namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Public manifest fingerprint without exposing the analyst's mutable prompt text.</summary>
public static class PdfStagePromptProfile
{
    public static string SemanticPromptSha256 => PdfBlockAnalyst.PromptProfileSha256;
}
