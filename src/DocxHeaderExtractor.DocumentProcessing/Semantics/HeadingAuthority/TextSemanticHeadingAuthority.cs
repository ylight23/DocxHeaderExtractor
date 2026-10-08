using DocxHeaderExtractor.Core.Semantics.Canonical;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>
/// The canonical-text heading authority the DOCX route uses today. One text-model pass answers
/// heading, role and relation for each source alias; the model supplies no coordinates. It is not
/// the qualified F1 → anchor → extent chain, and DOCX does not use that chain until it has its own
/// qualification evidence - replacing this class is the whole change that promotion needs.
/// </summary>
internal sealed class TextSemanticHeadingAuthority(
    IInferenceTransport? transport,
    CanonicalSemanticExperiment? experiment = null) : IHeadingAuthority
{
    public async Task<HeadingAuthorityResult> DecideAsync(
        DocumentSourceSnapshot source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var evidence = source.Evidence;
        var input = new CanonicalSemanticTextProductionInput(
            source.Catalog,
            null,
            source.SourceSha256,
            source.SourceSha256,
            source.DocumentId,
            evidence,
            evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray())
        {
            OwnedAliases = null,
        };

        CanonicalSemanticTextProductionResult result;
        CanonicalSemanticEngine.CanonicalTextInferenceModel? model = null;
        if (transport is null)
        {
            // No model, no semantic claims: the harness does not declare headings from style,
            // outline level or numbering on its own. Same as the PDF lane.
            result = CanonicalSemanticTextProductionEntryPoint.Run(input with { SemanticProposals = [] });
        }
        else
        {
            model = new CanonicalSemanticEngine.CanonicalTextInferenceModel(
                transport,
                SemanticCoordinateContract.DocxAliasSpan,
                SemanticEvidencePackingPolicies.FixedOwnedCount120,
                experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticTextProductionEntryPoint.RunAsync(
                input, model,
                requestId: $"{source.SourceKind}:{source.DocumentId}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var bound = result.TextPipeline.BoundHeadings;
        var decisions = bound.Select(item => new HeadingExtentDecision(
            item.SourceId,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticFunction: item.SemanticRole)).ToArray();
        var count = source.Occurrences.Count;

        PipelineExecutionAudit Complete(PipelineExecutionAudit audit, HeadingStructureAssembly assembly) => audit with
        {
            RawAnalystResponses = model?.RawResponses ?? [],
            ModelInputContracts = model is null ? [] : [model.Contract.ProtocolVersion],
            SourceStageTraces = source.OccurrenceContexts.Values.Select(context =>
            {
                var selected = assembly.Validated.Any(item => item.SourceId == context.SourceId);
                return new HeadingSourceStageTrace(
                    context.SourceId,
                    context.StructuralScope,
                    selected ? "canonical-semantic" : "not-heading",
                    "source-grounded",
                    selected ? "valid" : "not-selected",
                    null);
            }).ToArray(),
            ValidatedStructures = assembly.Hierarchies.Values.ToArray(),
            HierarchyFacts = [],
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.ConflictNormalization.NormalizedProposals,
                    source.Aliases,
                    result.ConflictNormalization.Conflicts),
                0,
                0,
                bound.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new LaneExecutionAudit("complete", count, assembly.Validated.Count, 0, 0),
            SpanLane = new LaneExecutionAudit(
                "canonical-binder", bound.Count, bound.Count, 0, result.TextPipeline.BindingFailureCount),
            BatchTelemetry = new HeadingAuthorityBatchTelemetry(
                count,
                count,
                result.TextModelCalls == 0 ? 0 : 1,
                result.TextModelCalls,
                0,
                0,
                0,
                bound.Count,
                0,
                0,
                0,
                result.TextPipeline.Graph.Occurrences.Count,
                0,
                result.TextModelCalls,
                model?.RawResponses.Count ?? 0,
                0),
        };

        return new HeadingAuthorityResult(decisions, bound, transport, Complete);
    }
}
