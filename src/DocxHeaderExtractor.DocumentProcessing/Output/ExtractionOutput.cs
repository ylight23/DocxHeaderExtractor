using System.Security.Cryptography;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output.Final;
using DocxHeaderExtractor.DocumentProcessing.Output.Outline;
using DocxHeaderExtractor.DocumentProcessing.Output.Product;
using DocxHeaderExtractor.DocumentProcessing.Output.Sections;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Output;

/// <summary>
/// What every lane returns once its headings are decided: the canonical document (catalog, structure, sections, chunks) and the outline
/// with its product output. The projections are the shared ones, so a caller receives the same contract whichever format it uploaded;
/// a lane passes only the values that are its own (identity, provenance, counts, route).
/// </summary>
internal static class ExtractionOutput
{
    /// <summary>The fields of the result that differ by lane. <paramref name="ProductSha256"/> names the empty product of a run with no
    /// audit; null hashes the file when that product is needed.</summary>
    internal sealed record Lane(
        string Path,
        DocumentIdentity Identity,
        DocumentExtractionProvenance Provenance,
        string? ProductSha256,
        int ParagraphCount,
        int SourceCount,
        string Route,
        string? Model,
        bool SentDataExternally);

    internal static RunResult Build(
        Lane lane,
        LaneResult authority,
        ValidatedStructure structure,
        IReadOnlySet<string> emittedIds,
        DocumentSourceCatalog catalog,
        PipelineOptions options,
        long started)
    {
        var audit = authority.Audit;
        DocumentProductOutput product;
        if (audit is not null)
        {
            var final = BuildFinalStructure(lane.Path, audit, authority.Structure, authority.ProjectionContext);
            product = ProductProjector.Serialize(final, OutputPolicy.Decide(final));
        }
        else product = new DocumentProductOutput(lane.ProductSha256 ?? FileSha256(lane.Path), []);

        var headings = OutlineProjection.Project(structure, emittedIds, authority.ProjectionContext, catalog);
        var sections = SectionProjection.Project(structure, catalog);
        var chunks = ChunkProjection.Project(
            sections, catalog, structure, new DocumentChunkingPolicy(Math.Max(1, options.Chunking.TokenBudget)));
        var result = new DocumentExtractionResult(lane.Identity, catalog, structure, sections, chunks, lane.Provenance);
        var outline = new DocumentOutline
        {
            File = System.IO.Path.GetFileName(lane.Path),
            ParagraphCount = lane.ParagraphCount,
            SourceCount = lane.SourceCount,
            Headings = headings,
            ProductOutput = product,
            ElapsedMs = Environment.TickCount64 - started,
            Model = lane.Model,
            DeterministicRoute = lane.Route,
            RouteAudit = audit,
            Provenance = BuildProvenance(audit, lane.SentDataExternally),
        };
        return new RunResult(result, outline) { HeadingPipeline = authority };
    }

    internal static CanonicalFinalStructure BuildFinalStructure(string path, RunAudit audit,
        ValidatedStructure structure, ProjectionContext? projectionContext = null)
    {
        // Materializes only facts already validated upstream; resolves no identity, hierarchy or provider work.
        return FinalProjection.Project(
            FileSha256(path),
            audit.ValidatedStructures,
            audit.HierarchyFacts,
            GroundingProjection.Project(structure, projectionContext));
    }

    internal static OutlineRunProvenance BuildProvenance(RunAudit? audit, bool sentDataExternally)
    {
        if (audit is null)
            return new OutlineRunProvenance("deterministic-ooxml", false, []);

        var passes = new List<OutlinePass>();
        if (audit.SemanticLane is { Scheduled: > 0, Completed: > 0 })
            passes.Add(new("semantic-role", audit.SemanticLane.Completed, audit.SemanticLane.Scheduled, sentDataExternally));
        if (audit.SpanLane is { Scheduled: > 0, Completed: > 0 })
            passes.Add(new("heading-span", audit.SpanLane.Completed, audit.SpanLane.Scheduled, sentDataExternally));
        passes.Add(new("source-facts", 1, audit.SourceBlocksAvailable, false));
        passes.Add(new("proposal-validation", 1, audit.BlockDecisions.Count, false));
        passes.Add(new("deterministic-hierarchy", 1, audit.ValidatedStructures.Count, false));
        passes.Add(new("output-policy", 1, audit.ValidatedStructures.Count, false));
        return new OutlineRunProvenance(
            audit.SemanticLane is not null || audit.SpanLane is not null ? "configured-route" : "deterministic-ooxml",
            sentDataExternally,
            passes);
    }

    internal static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
