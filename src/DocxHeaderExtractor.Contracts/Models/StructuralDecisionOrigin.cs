namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>Who produced a structural decision. The producing lane declares it; nothing infers it.</summary>
public static class StructuralDecisionOrigin
{
    /// <summary>A model claim that bound to the source and passed validation.</summary>
    public const string Model = "model";

    /// <summary>A reviewer's correction applied to the exact source paragraph.</summary>
    public const string HumanCorrection = "human-correction";
}
