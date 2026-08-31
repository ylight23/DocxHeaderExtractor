namespace DocxHeaderExtractor.Core.Models;

/// <summary>Role assigned to a source paragraph by deterministic policy.</summary>
public enum ParagraphRole
{
    Normal = 0,
    StyledHeading = 1,
    HeadingCandidate = 2,
    Empty = 3,
}
