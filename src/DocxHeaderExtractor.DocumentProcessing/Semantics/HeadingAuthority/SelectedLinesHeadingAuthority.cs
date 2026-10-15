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
/// heading and continues with body text): then it is exactly that span of the line's text. The authority takes the selector's span as it is;
/// the shared validator checks it against the source, and a span that does not lie inside the line is not applied but counted.
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
        var decisions = selection.Groups is { } given
            ? FromGroups(source.Atoms, selection.SelectedAliases, given, layoutBlockByAtom, extents)
            : Group(source.Atoms, selection.SelectedAliases, layoutBlockByAtom, extents);
        var extentsNotInLine = extents.Count(kv => source.Atoms.FirstOrDefault(atom => atom.Alias == kv.Key) is { } atom && !Fits(kv.Value, atom.Text));
        var atoms = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var bound = decisions.Select(decision => ToBound(decision, atoms)).ToArray();
        var unknown = selection.SelectedAliases.Count(alias => !atoms.ContainsKey(alias));
        var atomCount = source.Atoms.Count;

        PipelineExecutionAudit Complete(PipelineExecutionAudit audit, HeadingStructureAssembly assembly) => audit with
        {
            RawModelResponses = selection.ModelAnswers,
            ModelInputContracts = [selector.SelectorId],
            ValidatedStructures = assembly.Hierarchies.Values.ToArray(),
            SemanticLane = new LaneExecutionAudit(
                selection.UnitsComplete == selection.UnitsTotal ? "complete" : "partial",
                selection.UnitsTotal, selection.UnitsComplete, 0, selection.UnitsTotal - selection.UnitsComplete,
                selection.UnitFailures.Count == 0 && unknown == 0 && extentsNotInLine == 0 ? null
                    : JsonSerializer.Serialize(new { unitFailures = selection.UnitFailures, selectedAliasesNotInSource = unknown, headingExtentsNotInLine = extentsNotInLine, atoms = atomCount })),
            SpanLane = new LaneExecutionAudit("selected-lines", decisions.Count, assembly.Validated.Count, 0, decisions.Count - assembly.Validated.Count),
        };
        var evidence = selection.Placement is { } p ? new HeadingPlacementEvidence(p.DocumentStylesJson, p.StyleJsonByAlias, p.MeasurementsNote) : null;
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

    /// <summary>One heading over its lines: each part is the line's heading extent when the selector gave one that lies inside the line, else the
    /// whole line. The decision's span is its first part's.</summary>
    private static HeadingExtentDecision Decision(IReadOnlyList<SemanticSourceAtom> lines, IReadOnlyDictionary<string, TextOffsetSpan>? extents)
    {
        var parts = lines.Select(atom =>
        {
            var span = extents is not null && extents.TryGetValue(atom.Alias, out var e) && Fits(e, atom.Text) ? e : new TextOffsetSpan(0, atom.Text.Length);
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
/// <param name="PlacementEvidence">Optional measurements for the placement pass (the PDF lane's style table); null keeps the packet unchanged.</param>
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
