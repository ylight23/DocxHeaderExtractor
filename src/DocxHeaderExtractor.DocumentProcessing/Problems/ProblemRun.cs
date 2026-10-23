namespace DocxHeaderExtractor.DocumentProcessing.Problems;

/// <summary>One run of a problem: the shared envelope and the problem's own output. The output is null when the model was disabled, since
/// there was no proposal to check.</summary>
public sealed record ProblemRun<TOutput>(RunEnvelope Envelope, TOutput? Output) where TOutput : class;
