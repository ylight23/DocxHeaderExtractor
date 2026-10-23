namespace DocxHeaderExtractor.Entities;

/// <summary>
/// The entity types the first entity problem looks for. These are defaults chosen for the trial, not a product schema: a change here changes
/// the prompt the model reads and the checker that keeps its answers.
/// </summary>
public static class EntityTypes
{
    public static readonly string[] All = ["ORGANIZATION", "PERSON", "DATE", "LEGAL_REFERENCE", "AMOUNT"];
}
