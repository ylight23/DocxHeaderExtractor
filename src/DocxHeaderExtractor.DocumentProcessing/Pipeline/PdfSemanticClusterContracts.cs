namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The style-cluster vocabulary the PDF grounding and checkpoint stages speak.
/// <para>
/// These lived beside <c>PdfSemanticClusterAnalyst</c>, the model stage that once produced them.
/// That stage is gone - the canonical semantic engine owns this question now - but the types are
/// not: <c>PdfStageCheckpoint</c> still reads and persists them, and the checkpoint lifecycle is
/// retained for the later timeout/detached-work hardening phase. Moving them here separates a
/// contract that is still used from an implementation that no longer exists.
/// </para>
/// </summary>
internal enum PdfSemanticClusterRole
{
    HeadingTopic,
    BodySentence,
    TableOrChartLabel,
    Uncertain,
}

/// <summary>One visual style cluster, with the examples that characterise it.</summary>
internal sealed record PdfSemanticClusterSample(
    string Id,
    PdfStyleKey Style,
    int Lines,
    int Pages,
    int Characters,
    IReadOnlyList<string> Examples);

/// <summary>What a cluster was judged to be, and why. Advisory; never an output decision.</summary>
internal sealed record PdfSemanticClusterDecision(
    string Id,
    PdfSemanticClusterRole Role,
    double Confidence,
    string Reason);
