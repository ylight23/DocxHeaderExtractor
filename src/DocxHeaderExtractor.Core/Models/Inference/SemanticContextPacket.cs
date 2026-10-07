namespace DocxHeaderExtractor.Core.Models;

/// <summary>Evidence layers packed for semantic reasoning; none of these layers grants binding authority.</summary>
public sealed record SemanticContextPacket(
    IReadOnlyList<string> TargetEvidence,
    IReadOnlyList<string> LocalContext,
    IReadOnlyList<string> GlobalContext);
