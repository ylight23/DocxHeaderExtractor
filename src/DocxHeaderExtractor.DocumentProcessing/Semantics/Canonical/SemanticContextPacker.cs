using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

public static class SemanticContextPacker
{
    public static SemanticContextPacket Pack(
        IEnumerable<string> targetEvidence,
        IEnumerable<string> localContext,
        IEnumerable<string> globalContext)
    {
        ArgumentNullException.ThrowIfNull(targetEvidence);
        ArgumentNullException.ThrowIfNull(localContext);
        ArgumentNullException.ThrowIfNull(globalContext);
        return new(
            targetEvidence.Where(item => item is not null).ToArray(),
            localContext.Where(item => item is not null).ToArray(),
            globalContext.Where(item => item is not null).ToArray());
    }
}
