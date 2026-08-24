namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Resolves an optional visual proposal against the text-model proposal without granting visual
/// evidence source authority. A visual answer may corroborate or lower a heading proposal, but it
/// may never create one from a body/noise proposal.
/// </summary>
internal static class PdfProposalConflictResolver
{
    public static PdfProposalResolutionResult Resolve(
        IReadOnlyList<PdfBlockDecision> modelDecisions,
        IReadOnlyList<PdfVisualBlockDecision> visualDecisions,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        var visualById = visualDecisions
            .GroupBy(decision => decision.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resolved = new List<PdfBlockDecision>(modelDecisions.Count);
        var audit = new List<PdfProposalResolutionAudit>(modelDecisions.Count);

        foreach (var model in modelDecisions)
        {
            if (!visualById.TryGetValue(model.Id, out var visual))
            {
                if (IsMarkerOnly(contexts, model.Id) && model.Role == PdfBlockRole.HeadingTopic)
                {
                    var unresolved = model with { Role = PdfBlockRole.Uncertain, Reason = "marker-only-needs-visual" };
                    resolved.Add(unresolved);
                    audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), null, unresolved.Role.ToString(), "marker-only-needs-visual"));
                    continue;
                }
                resolved.Add(model);
                audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), null, model.Role.ToString(), "no-visual-proposal"));
                continue;
            }

            if (visual.Role == PdfBlockRole.Uncertain)
            {
                if (IsMarkerOnly(contexts, model.Id) && model.Role == PdfBlockRole.HeadingTopic)
                {
                    var unresolved = model with { Role = PdfBlockRole.Uncertain, Reason = "marker-only-needs-visual" };
                    resolved.Add(unresolved);
                    audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), unresolved.Role.ToString(), "marker-only-needs-visual"));
                    continue;
                }
                resolved.Add(model);
                audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), model.Role.ToString(), "visual-unresolved"));
                continue;
            }

            if (model.Role == PdfBlockRole.HeadingTopic && visual.Role != PdfBlockRole.HeadingTopic)
            {
                var facts = contexts.TryGetValue(model.Id, out var context) ? context.Source : null;
                if (facts?.ObservedEvidence.Contains("marker_only_source") == true)
                {
                    var unresolved = model with { Role = PdfBlockRole.Uncertain, Reason = "marker-only-needs-visual" };
                    resolved.Add(unresolved);
                    audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), unresolved.Role.ToString(), "marker-only-needs-visual"));
                    continue;
                }
                var markerBacked = facts?.StructuralScope == "document_body" &&
                    facts.DomainRole is not PdfDomainRole.AmendmentAnnotation and not PdfDomainRole.EditorialInstruction &&
                    facts.ObservedEvidence.Any(evidence => evidence.StartsWith("marker:", StringComparison.Ordinal));
                var tableCorroborated = visual.Role == PdfBlockRole.TableOrChartLabel &&
                    (facts?.StructuralScope == "table" || facts?.ObservedEvidence.Contains("table_like") == true);
                if (markerBacked && !tableCorroborated)
                {
                    resolved.Add(model);
                    audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), model.Role.ToString(), "marker-semantic-retained-over-visual-conflict"));
                    continue;
                }
                var lowered = model with
                {
                    Role = PdfBlockRole.Uncertain,
                    Reason = $"visual-conflict:{visual.Role.ToString().ToLowerInvariant()}",
                };
                resolved.Add(lowered);
                audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), lowered.Role.ToString(), "conflict-lowered-to-unresolved"));
                continue;
            }

            if (model.Role != PdfBlockRole.HeadingTopic && visual.Role == PdfBlockRole.HeadingTopic)
            {
                var escalated = model with { Role = PdfBlockRole.Uncertain, Reason = "visual-heading-disagreement" };
                resolved.Add(escalated);
                audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), escalated.Role.ToString(), "visual-heading-escalated-unresolved"));
                continue;
            }

            resolved.Add(model);
            var status = model.Role == PdfBlockRole.HeadingTopic && visual.Role == PdfBlockRole.HeadingTopic
                ? "visual-corroborated"
                : "visual-agrees-or-nonheading";
            audit.Add(new PdfProposalResolutionAudit(model.Id, model.Role.ToString(), visual.Role.ToString(), model.Role.ToString(), status));
        }

        return new PdfProposalResolutionResult(resolved, audit);
    }

    private static bool IsMarkerOnly(IReadOnlyDictionary<string, PdfCandidateContext> contexts, string id) =>
        contexts.TryGetValue(id, out var context) && context.Source.ObservedEvidence.Contains("marker_only_source");
}

internal sealed record PdfProposalResolutionResult(
    IReadOnlyList<PdfBlockDecision> Decisions,
    IReadOnlyList<PdfProposalResolutionAudit> Audit);

public sealed record PdfProposalResolutionAudit(
    string Id,
    string ModelRole,
    string? VisualRole,
    string ResolvedRole,
    string Resolution);
