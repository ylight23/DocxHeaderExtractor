namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Test/evaluation-only companion lookup. Production routing must never scan the evaluation corpus.
/// </summary>
internal static class EvaluationCorpusPdfResolver
{
    public static string? Find(string inputPath)
    {
        var corpusRoot = Path.Combine(
            PdfExtractorQualityBenchmarkProbe.RepositoryRoot(),
            "todo10_8", "heading_corpus_100");
        if (!Directory.Exists(corpusRoot)) return null;

        var fileName = Path.GetFileNameWithoutExtension(inputPath) + ".pdf";
        return Directory.EnumerateFiles(corpusRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }
}
