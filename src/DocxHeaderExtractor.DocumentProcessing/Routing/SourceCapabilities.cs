namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>Facts available before normal authority route selection.</summary>
public sealed record SourceCapabilities(bool HasDocx, bool HasPdf, bool AnalystAvailable);
