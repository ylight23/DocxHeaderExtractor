namespace DocxHeaderExtractor.Core.Models;

public sealed record CanonicalSemanticBindingValidation(
    bool IsValid,
    IReadOnlyList<string> Errors);
