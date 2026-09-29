using System.Text;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Resolves a structured claim into coordinates, or refuses it.
/// <para>
/// The model names atoms and quotes their text. Every offset here is derived from the atom catalog
/// and nothing the model wrote is used as a coordinate, so the split of authority survives a claim
/// that spans several atoms: meaning is still the model's, position is still the harness's.
/// </para>
/// <para>
/// Refusals are explicit and nothing is repaired on the way through. Text that does not occur is
/// not corrected and parts that arrive out of order are not sorted - each is reported for what it
/// is, because a binder that quietly fixes a proposal makes the model look more accurate than it was.
/// </para>
/// <para>
/// GENERIC_MULTIPART_BINDER_V2: a claim's identity is the ordered tuple of the parts it names, not a
/// run of adjacent rows. A title whose lines are interleaved with another column's rows, or that
/// continues on the next page, binds when each part is named explicitly; the locality is recorded,
/// not used to refuse. The binder validates the coordinates it is given - known atoms, exact text,
/// source order, no overlap - and never discovers a continuation by proximity: a claim is exactly
/// the parts it names. Whether distant parts form one heading is a semantic question, answered by
/// the model or the Gold, and scored against Gold, not refused here.
/// </para>
/// </summary>
public static class SemanticSourcePartBinder
{
    public static SemanticSourcePartsBinding Bind(
        IReadOnlyList<SemanticSourceAtom> atoms,
        SemanticSourcePartsProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(proposal);

        if (proposal.SourceParts is null || proposal.SourceParts.Count == 0)
            return Refuse(SemanticSourcePartsStatus.NoParts, "a claim must name at least one source part");

        var byAlias = new Dictionary<string, SemanticSourceAtom>(StringComparer.Ordinal);
        foreach (var atom in atoms) byAlias.TryAdd(atom.Alias, atom);

        var bound = new List<BoundSourcePart>(proposal.SourceParts.Count);
        SemanticSourceAtom? previousAtom = null;
        var previousEnd = 0;

        foreach (var part in proposal.SourceParts)
        {
            if (!byAlias.TryGetValue(part.SourceAlias ?? string.Empty, out var atom))
                return Refuse(SemanticSourcePartsStatus.UnknownAlias, $"'{part.SourceAlias}' is not an atom in this source");

            var resolved = Resolve(atom, part);
            if (resolved.Status != SemanticSourcePartsStatus.Bound)
                return Refuse(resolved.Status, resolved.Reason);

            var (start, end) = (resolved.Start, resolved.End);
            var locality = previousAtom is null
                ? SemanticSourceLocality.SameSegment
                : Locality(previousAtom, atom);

            if (previousAtom is not null)
            {
                var sameAtom = string.Equals(previousAtom.Alias, atom.Alias, StringComparison.Ordinal);
                var previousStart = bound[^1].Start;

                if (sameAtom)
                {
                    if (start == previousStart && end == previousEnd)
                        return Refuse(SemanticSourcePartsStatus.DuplicatePart,
                            $"'{atom.Alias}' is selected twice at the same place");
                    if (start < previousStart)
                        return Refuse(SemanticSourcePartsStatus.OutOfSourceOrder,
                            $"'{atom.Alias}' selects text that comes before the part above it");
                    if (start < previousEnd)
                        return Refuse(SemanticSourcePartsStatus.OverlappingParts,
                            $"'{atom.Alias}' selects text the part above it already covers");
                }
                else if (atom.Ordinal <= previousAtom.Ordinal)
                {
                    return Refuse(SemanticSourcePartsStatus.OutOfSourceOrder,
                        $"'{atom.Alias}' does not come after '{previousAtom.Alias}' in the source");
                }
            }

            bound.Add(new BoundSourcePart(
                atom.Alias, atom.SourceId, atom.Ordinal, start, end, atom.Text[start..end], locality));
            previousAtom = atom;
            previousEnd = end;
        }

        return new SemanticSourcePartsBinding(SemanticSourcePartsStatus.Bound, bound);
    }

    /// <summary>
    /// How the second of two consecutive parts sits relative to the first - recorded on the binding,
    /// never a reason to refuse it.
    /// <para>
    /// Adjacency in the source, not membership of a parser block. Nothing about what the text means is
    /// consulted - a bullet and its item are adjacent whether or not they turn out to be one heading,
    /// and that question belongs to the model.
    /// </para>
    /// </summary>
    public static SemanticSourceLocality Locality(SemanticSourceAtom previous, SemanticSourceAtom next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (previous.Alias == next.Alias) return SemanticSourceLocality.SameSegment;
        if (previous.Page != next.Page) return SemanticSourceLocality.CrossPage;
        if (previous.Row == next.Row && next.Segment == previous.Segment + 1)
            return SemanticSourceLocality.SameRowNextSegment;
        if (next.Row == previous.Row + 1) return SemanticSourceLocality.NextRowCompatible;
        return SemanticSourceLocality.SamePageNonAdjacent;
    }

    private static (SemanticSourcePartsStatus Status, int Start, int End, string? Reason) Resolve(
        SemanticSourceAtom atom, SemanticSourcePart part)
    {
        if (string.Equals(part.SelectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal))
        {
            return part.VerbatimText is not null
                ? (SemanticSourcePartsStatus.UnexpectedVerbatimText, 0, 0,
                    $"'{atom.Alias}' selects the whole atom and must not also quote text")
                : (SemanticSourcePartsStatus.Bound, 0, atom.Text.Length, null);
        }

        if (!string.Equals(part.SelectionMode, CanonicalSemanticSelectionMode.VerbatimText, StringComparison.Ordinal))
            return (SemanticSourcePartsStatus.UnknownSelectionMode, 0, 0, $"'{part.SelectionMode}' is not a selection mode");

        if (string.IsNullOrEmpty(part.VerbatimText))
            return (SemanticSourcePartsStatus.MissingVerbatimText, 0, 0, $"'{atom.Alias}' selects part of an atom without saying which");

        var hits = new List<int>();
        for (var at = atom.Text.IndexOf(part.VerbatimText, StringComparison.Ordinal);
             at >= 0;
             at = atom.Text.IndexOf(part.VerbatimText, at + 1, StringComparison.Ordinal))
        {
            hits.Add(at);
        }

        if (hits.Count == 0)
            return (SemanticSourcePartsStatus.TextNotInAtom, 0, 0, $"'{atom.Alias}' does not contain that text");

        if (hits.Count > 1)
        {
            if (part.Occurrence is { } wanted)
            {
                if (wanted < 1 || wanted > hits.Count)
                    return (SemanticSourcePartsStatus.AmbiguousSelection, 0, 0,
                        $"'{atom.Alias}' has {hits.Count} occurrences of that text, not {wanted}");
                return (SemanticSourcePartsStatus.Bound, hits[wanted - 1], hits[wanted - 1] + part.VerbatimText.Length, null);
            }

            var byContext = hits
                .Where(at => Matches(atom.Text, at, part))
                .ToArray();
            if (byContext.Length != 1)
                return (SemanticSourcePartsStatus.AmbiguousSelection, 0, 0,
                    $"'{atom.Alias}' contains that text {hits.Count} times and nothing says which");
            return (SemanticSourcePartsStatus.Bound, byContext[0], byContext[0] + part.VerbatimText.Length, null);
        }

        return Matches(atom.Text, hits[0], part)
            ? (SemanticSourcePartsStatus.Bound, hits[0], hits[0] + part.VerbatimText.Length, null)
            : (SemanticSourcePartsStatus.TextNotInAtom, 0, 0,
                $"'{atom.Alias}' contains that text but not in the context given");
    }

    private static bool Matches(string text, int at, SemanticSourcePart part)
    {
        if (part.LeftExactContext is { Length: > 0 } left)
        {
            if (at < left.Length) return false;
            if (!text.AsSpan(at - left.Length, left.Length).SequenceEqual(left)) return false;
        }

        if (part.RightExactContext is { Length: > 0 } right)
        {
            var after = at + (part.VerbatimText?.Length ?? 0);
            if (after + right.Length > text.Length) return false;
            if (!text.AsSpan(after, right.Length).SequenceEqual(right)) return false;
        }

        return true;
    }

    private static SemanticSourcePartsBinding Refuse(SemanticSourcePartsStatus status, string? reason) =>
        new(status, [], reason);
}

/// <summary>
/// How a bound claim reads, which is not what it is.
/// <para>
/// Identity is the tuple of coordinates; this is a rendering of it. They are kept apart on purpose:
/// a projection that replaced identity would make two different selections compare equal because
/// they happened to read the same, and a heading split across a line break would lose the record of
/// where it actually came from.
/// </para>
/// <para>
/// The rules are few and deliberately shy. A hyphen at a row break is kept rather than removed,
/// because nothing in the geometry distinguishes a word broken across lines from a compound that
/// was already hyphenated, and keeping it is the choice that can be undone.
/// </para>
/// </summary>
public static class SemanticSourceProjection
{
    /// <summary>Characters that end a part with a break rather than with a word.</summary>
    public const string ContinuationHyphens = "-‐‑­";

    public static string Render(IReadOnlyList<BoundSourcePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return string.Empty;

        var text = new StringBuilder(parts[0].Text);
        for (var index = 1; index < parts.Count; index++)
        {
            var previous = parts[index - 1].Text;
            var current = parts[index].Text;

            // No separator where the source already has one, where the earlier part ends on a
            // break hyphen, or where the later part opens with punctuation that belongs to the
            // word before it. A single space otherwise - never more, and never a guess at one.
            var joined =
                previous.Length == 0 || current.Length == 0 ||
                char.IsWhiteSpace(previous[^1]) || char.IsWhiteSpace(current[0]) ||
                ContinuationHyphens.Contains(previous[^1]) ||
                IsTrailingPunctuation(current[0]);

            if (!joined) text.Append(' ');
            text.Append(current);
        }

        return text.ToString();
    }

    private static bool IsTrailingPunctuation(char character) => character is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']';
}
