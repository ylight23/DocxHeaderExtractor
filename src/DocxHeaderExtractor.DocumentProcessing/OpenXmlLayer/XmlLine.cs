namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>One line in the model-facing native document view.</summary>
public sealed record XmlLine(string Text, int? ParagraphIndex, bool IsCandidate);
