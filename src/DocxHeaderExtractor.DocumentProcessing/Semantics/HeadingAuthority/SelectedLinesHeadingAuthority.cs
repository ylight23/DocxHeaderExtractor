using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>
/// The heading authority of both lanes (PDF since the V6 promotion, DOCX since its V6 wiring): an <see cref="IHeadingSelector"/> answers
/// which source lines are headings, and this class turns its answer into heading extents for the shared stages. A run of consecutive selected lines on the same page and in the same
/// parser layout block is one heading (the selector is asked to select every line of a heading that runs over several lines); any other
/// selected line starts a heading of its own. Aliases the source does not have are ignored and counted. Placement and hierarchy run on the
/// shared stages with the authorized transport, as before.
/// <para>
/// The heading text of a selected line is the whole line unless the selector gave its heading extent (a DOCX paragraph that opens with its
/// heading and continues with body text): then it is exactly that span of the line's text. The authority takes the selector's span as it is.
/// Three cases are kept apart: no extent is the whole line, as the contract says; a valid extent is used exactly; an extent that does not lie
/// inside its line is rejected, and the line then makes no heading. It is never replaced by the whole line, which would put the body text into
/// the heading again.
/// </para>
/// </summary>
internal sealed class SelectedLinesHeadingAuthority(
    IHeadingSelector selector,
    string pdfPath,
    string apiKey,
    IReadOnlyDictionary<string, string> layoutBlockByAtom,
    IInferenceTransport? placementTransport,
    Action ensureActive)
{
    internal const string AuthorityId = "pdf-v6-selected-lines-heading-authority-v1";
    internal const string Reason = "pdf-v6-selected-lines-v1";

    public async Task<HeadingAuthorityResult> DecideAsync(DocumentSourceSnapshot source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ensureActive();
        var selection = await selector.SelectAsync(pdfPath, apiKey, ct).ConfigureAwait(false);
        ensureActive();
        var extents = selection.HeadingExtents ?? new Dictionary<string, TextOffsetSpan>(StringComparer.Ordinal);
        var byAlias = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        // Selected lines whose extent is invalid, and extents for lines that are not selected or not in the source: none of them reaches a heading.
        var rejected = selection.SelectedAliases.Where(alias => extents.TryGetValue(alias, out var e) && byAlias.TryGetValue(alias, out var atom) && !Fits(e, atom.Text))
            .ToHashSet(StringComparer.Ordinal);
        var extentsRejected = rejected.Count + extents.Keys.Count(alias => !selection.SelectedAliases.Contains(alias) || !byAlias.ContainsKey(alias));
        var usable = rejected.Count == 0 ? selection.SelectedAliases : selection.SelectedAliases.Where(alias => !rejected.Contains(alias)).ToHashSet(StringComparer.Ordinal);
        var decisions = selection.Groups is { } given
            ? FromGroups(source.Atoms, usable, given, layoutBlockByAtom, extents)
            : Group(source.Atoms, usable, layoutBlockByAtom, extents);
        var atoms = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var bound = decisions.Select(decision => ToBound(decision, atoms)).ToArray();
        var unknown = selection.SelectedAliases.Count(alias => !atoms.ContainsKey(alias));
        var atomCount = source.Atoms.Count;

        PipelineExecutionAudit Complete(PipelineExecutionAudit audit, HeadingStructureAssembly assembly) => audit with
        {
            RawModelResponses = selection.ModelAnswers.Concat(assembly.PlacementObservation?.ModelEvidence ?? []).ToArray(),
            SelectionProviderCalls = selection.ProviderCalls,
            Grouping = selection.Grouping,
            HeadingStageSeconds = new Dictionary<string, double>(selection.StageSeconds ?? new Dictionary<string, double>())
            { ["hierarchy"] = assembly.PlacementObservation?.Seconds ?? 0 },
            HeadingAliasStates = selection.AliasStates,
            ModelInputContracts = [selector.SelectorId, EvidenceHeadingPlacement.Version],
            ValidatedStructures = assembly.Hierarchies.Values.ToArray(),
            SemanticLane = new LaneExecutionAudit(
                selection.UnitsComplete == selection.UnitsTotal ? "complete" : "partial",
                selection.UnitsTotal, selection.UnitsComplete, 0, selection.UnitsTotal - selection.UnitsComplete,
                selection.UnitFailures.Count == 0 && unknown == 0 && extentsRejected == 0 ? null
                    : JsonSerializer.Serialize(new { unitFailures = selection.UnitFailures, selectedAliasesNotInSource = unknown, headingExtentsRejected = extentsRejected, atoms = atomCount })),
            SpanLane = new LaneExecutionAudit("selected-lines", decisions.Count, assembly.Validated.Count, 0, decisions.Count - assembly.Validated.Count),
        };
        var p = selection.Placement;
        var evidence = new HeadingPlacementEvidence(p?.DocumentStylesJson ?? "{}", p?.StyleJsonByAlias ?? new Dictionary<string, string>(), p?.MeasurementsNote)
        { Source = new(source.Atoms, selection.AliasStates ?? selection.SelectedAliases.ToDictionary(a => a,
            _ => new HeadingAliasState("SELECT", "SELECT", null, "READY_FOR_BINDING")), source.SourceSha256)
            { LocationByAlias = source.Evidence.ToDictionary(e => e.SourceAlias, e => e.LocationFacts) } };
        return new HeadingAuthorityResult(decisions, bound, placementTransport, Complete, evidence);
    }

    /// <summary>
    /// The selector's own division of the selected lines into headings. A group keeps only aliases the source has and the selector selected, in
    /// source order; a selected line that no group names becomes a heading of its own, so no selected line is ever dropped. Headings come out in
    /// document order.
    /// </summary>
    internal static IReadOnlyList<HeadingExtentDecision> FromGroups(
        IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlySet<string> selected, IReadOnlyList<IReadOnlyList<string>> groups,
        IReadOnlyDictionary<string, string> layoutBlockByAtom, IReadOnlyDictionary<string, TextOffsetSpan>? extents = null)
    {
        var byAlias = atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<SemanticSourceAtom>>();
        foreach (var group in groups)
        {
            var parts = group.Where(alias => selected.Contains(alias) && byAlias.ContainsKey(alias) && used.Add(alias))
                .Select(alias => byAlias[alias]).OrderBy(atom => atom.Ordinal).ToList();
            if (parts.Count > 0) result.Add(parts);
        }
        foreach (var atom in atoms.OrderBy(atom => atom.Ordinal).Where(atom => selected.Contains(atom.Alias) && !used.Contains(atom.Alias)))
            result.Add([atom]);
        return result.OrderBy(parts => parts[0].Ordinal).Select(parts => Decision(parts, extents)).ToArray();
    }

    /// <summary>Consecutive selected atoms on the same page and in the same layout block form one heading, in source order. The parser keys
    /// layout blocks by source id.</summary>
    internal static IReadOnlyList<HeadingExtentDecision> Group(
        IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlySet<string> selected, IReadOnlyDictionary<string, string> layoutBlockByAtom,
        IReadOnlyDictionary<string, TextOffsetSpan>? extents = null)
    {
        var groups = new List<List<SemanticSourceAtom>>();
        SemanticSourceAtom? previous = null;
        foreach (var atom in atoms.OrderBy(atom => atom.Ordinal))
        {
            if (!selected.Contains(atom.Alias)) { previous = null; continue; }
            var continues = previous is not null && previous.Page == atom.Page
                && layoutBlockByAtom.TryGetValue(previous.SourceId, out var a) && layoutBlockByAtom.TryGetValue(atom.SourceId, out var b) && a == b;
            if (continues) groups[^1].Add(atom); else groups.Add([atom]);
            previous = atom;
        }
        return groups.Select(group => Decision(group, extents)).ToArray();
    }

    /// <summary>One heading over its lines: each part is the line's heading extent when the selector gave one, else the whole line. The callers pass
    /// only lines whose extent lies inside them (a line with an invalid extent is left out before this point), so an extent is applied or absent,
    /// never replaced. The decision's span is its first part's.</summary>
    private static HeadingExtentDecision Decision(IReadOnlyList<SemanticSourceAtom> lines, IReadOnlyDictionary<string, TextOffsetSpan>? extents)
    {
        var parts = lines.Select(atom =>
        {
            var span = extents is not null && extents.TryGetValue(atom.Alias, out var e) ? e : new TextOffsetSpan(0, atom.Text.Length);
            if (!Fits(span, atom.Text)) throw new InvalidOperationException("HEADING_EXTENT_OUTSIDE_LINE:" + atom.Alias);
            return new CanonicalSemanticBoundPart(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text[span.Start..span.End], span.Start, span.End);
        }).ToArray();
        return new HeadingExtentDecision(parts[0].SourceId, Reason, new TextOffsetSpan(parts[0].Start, parts[0].End), "ESTABLISHES_STRUCTURE", parts);
    }

    private static bool Fits(TextOffsetSpan span, string text) => span.Start >= 0 && span.End > span.Start && span.End <= text.Length;

    private static CanonicalSemanticBoundHeading ToBound(HeadingExtentDecision decision, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        var parts = decision.Parts ?? [];
        var primary = atoms[parts[0].Alias];
        return new CanonicalSemanticBoundHeading(primary.Alias, primary.SourceId, primary.Ordinal, string.Join(" ", parts.Select(value => value.Text)), "ESTABLISHES_STRUCTURE", "heading", "document_body", [], parts[0].Start, parts[0].End) { Parts = parts };
    }
}

/// <summary>What an authority decided, plus what the shared stages need to continue.</summary>
/// <param name="Decisions">Exact heading extents the authority selected.</param>
/// <param name="BoundHeadings">The same headings as bound semantic claims, input to placement and hierarchy.</param>
/// <param name="PlacementTransport">Transport for the bounded placement follow-up; null when none ran.</param>
/// <param name="PlacementEvidence">Source context, upstream states and optional style measurements for placement.</param>
/// <param name="CompleteAudit">
/// Adds the authority's own audit facts (raw responses, contracts, lane telemetry) to the route audit
/// once the shared stages have run.
/// </param>
internal sealed record HeadingAuthorityResult(
    IReadOnlyList<HeadingExtentDecision> Decisions,
    IReadOnlyList<CanonicalSemanticBoundHeading> BoundHeadings,
    IInferenceTransport? PlacementTransport,
    Func<PipelineExecutionAudit, HeadingStructureAssembly, PipelineExecutionAudit> CompleteAudit,
    HeadingPlacementEvidence? PlacementEvidence = null);
