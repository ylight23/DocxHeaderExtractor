using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Repair;

/// <summary>
/// Repair-facing boundary for obtaining an outline from the current authority pipeline.
/// Repair code depends on this capability instead of a concrete production orchestrator.
/// </summary>
public interface IRepairOutlineRunner : IDisposable
{
    Task<DocumentOutline> RunAsync(string inputPath, CancellationToken cancellationToken = default);
}
