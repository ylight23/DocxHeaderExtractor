using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>How an issued candidate's extent was derived from source structure. Never a semantic label.</summary>
public enum V5CandidateExtentKind
{
    WHOLE,
    STRICT_PREFIX,
    STRICT_SUFFIX,
    MULTIPART,
}

/// <summary>
/// The bounded, deterministic extent family the harness issues. The binder's own language (any
/// increasing sequence of owned atoms, each whole or any scalar-boundary substring) is combinatorial,
/// so it can never be enumerated; this policy names exactly which source-derived extents become
/// selectable, and coverage of that choice is measured rather than assumed.
/// </summary>
public sealed record V5CandidatePolicyV1(int MaxMultipartParts, bool StrictTokenTrim, int MaxRelationsPerCandidate)
{
    public const string Version = "v5-candidate-extent-policy-1";

    /// <summary>
    /// WHOLE every owned atom; STRICT_PREFIX / STRICT_SUFFIX drop one trailing / leading
    /// whitespace-delimited token; MULTIPART 2..3 owned atoms with consecutive source ordinals on one
    /// page, each whole, plus the variant whose last part is its STRICT_PREFIX.
    /// </summary>
    public static V5CandidatePolicyV1 Default { get; } = new(MaxMultipartParts: 3, StrictTokenTrim: true, MaxRelationsPerCandidate: 8);
}

/// <summary>One harness-issued, request-local extent. Its endpoint is frozen at issue time.</summary>
public sealed record V5IssuedCandidateV1(
    string Id,
    string Text,
    int Page,
    V5CandidateExtentKind Kind,
    IReadOnlyList<SemanticSourcePart> Parts,
    BoundClaimEndpoint Endpoint)
{
    /// <summary>alias:start-end per part - the exact-scorer identity of this extent.</summary>
    public string SpanIdentity => string.Join("|", Endpoint.Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
}

/// <summary>
/// One harness-issued, request-local correspondence: candidate <see cref="CandidateId"/> has the same
/// normalized source text as a document occurrence elsewhere. Read-only target; never selectable.
/// </summary>
public sealed record V5IssuedRelationV1(
    string Id,
    string CandidateId,
    string TargetSpanIdentity,
    string TargetText,
    int TargetPage,
    string MatchTier);

/// <summary>
/// Layer 1 of the candidate-authority lane: every selectable extent and every relation a model may
/// name in one request, issued deterministically from source atoms alone - no Gold, no model, no
/// typography heuristics. A model never authors coordinates, multipart ordering or spans; it can only
/// pick an id this universe issued.
/// </summary>
public sealed class V5CandidateUniverseV1
{
    private readonly Dictionary<string, V5IssuedCandidateV1> _candidates;
    private readonly Dictionary<string, V5IssuedRelationV1> _relations;

    private V5CandidateUniverseV1(IReadOnlyList<V5IssuedCandidateV1> candidates, IReadOnlyList<V5IssuedRelationV1> relations,
        int relationsTruncated, string fingerprint)
    {
        Candidates = candidates;
        Relations = relations;
        RelationsTruncated = relationsTruncated;
        Fingerprint = fingerprint;
        _candidates = candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        _relations = relations.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<V5IssuedCandidateV1> Candidates { get; }
    public IReadOnlyList<V5IssuedRelationV1> Relations { get; }

    /// <summary>Correspondence targets dropped by <see cref="V5CandidatePolicyV1.MaxRelationsPerCandidate"/>; reported, never hidden.</summary>
    public int RelationsTruncated { get; }

    public string Fingerprint { get; }

    public bool TryCandidate(string id, out V5IssuedCandidateV1 candidate) => _candidates.TryGetValue(id, out candidate!);
    public bool TryRelation(string id, out V5IssuedRelationV1 relation) => _relations.TryGetValue(id, out relation!);

    /// <param name="ownedAtoms">The only atoms a candidate may contain.</param>
    /// <param name="documentAtoms">The whole source universe, used only for read-only correspondence targets.</param>
    public static V5CandidateUniverseV1 Build(
        IReadOnlyList<SemanticSourceAtom> ownedAtoms, IReadOnlyList<SemanticSourceAtom> documentAtoms, V5CandidatePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(documentAtoms);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxMultipartParts < 1 || policy.MaxRelationsPerCandidate < 0)
            throw new ArgumentOutOfRangeException(nameof(policy));

        var owned = ownedAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        if (owned.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count() != owned.Length)
            throw new InvalidOperationException("candidate-owned-atom-duplicated");

        var hypotheses = new List<(int Primary, V5CandidateExtentKind Kind, SemanticSourcePart[] Parts)>();
        for (var index = 0; index < owned.Length; index++)
        {
            var atom = owned[index];
            hypotheses.Add((index, V5CandidateExtentKind.WHOLE, [Whole(atom)]));
            if (policy.StrictTokenTrim)
            {
                if (StrictPrefix(atom) is { } prefix) hypotheses.Add((index, V5CandidateExtentKind.STRICT_PREFIX, [prefix]));
                if (StrictSuffix(atom) is { } suffix) hypotheses.Add((index, V5CandidateExtentKind.STRICT_SUFFIX, [suffix]));
            }
            for (var length = 2; length <= policy.MaxMultipartParts && index + length <= owned.Length; length++)
            {
                var window = owned.Skip(index).Take(length).ToArray();
                if (!window.Zip(window.Skip(1)).All(pair => pair.Second.Ordinal == pair.First.Ordinal + 1 && pair.Second.Page == pair.First.Page))
                    break;
                hypotheses.Add((index, V5CandidateExtentKind.MULTIPART, window.Select(Whole).ToArray()));
                if (policy.StrictTokenTrim && StrictPrefix(window[^1]) is { } lastPrefix)
                    hypotheses.Add((index, V5CandidateExtentKind.MULTIPART, window[..^1].Select(Whole).Append(lastPrefix).ToArray()));
            }
        }

        var candidates = new List<V5IssuedCandidateV1>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hypothesis in hypotheses)
        {
            // Bound against owned atoms only: a part naming any other atom cannot bind, so a
            // context-only occurrence is structurally impossible inside a candidate.
            var binding = SemanticSourcePartBinder.Bind(owned, hypothesis.Parts);
            if (!binding.IsBound) throw new InvalidOperationException($"candidate-hypothesis-does-not-bind:{binding.Status}");
            var endpoint = new BoundClaimEndpoint(binding.Parts);
            if (!seen.Add(endpoint.Identity)) continue;
            var id = $"C{candidates.Count + 1}";
            candidates.Add(new V5IssuedCandidateV1(id, string.Join(" ", binding.Parts.Select(part => part.Text)),
                owned[hypothesis.Primary].Page, hypothesis.Kind, hypothesis.Parts, endpoint));
        }

        var (relations, truncated) = BuildRelations(candidates, documentAtoms, policy);
        var canonical = JsonSerializer.Serialize(new
        {
            policy = V5CandidatePolicyV1.Version,
            policy.MaxMultipartParts,
            policy.StrictTokenTrim,
            policy.MaxRelationsPerCandidate,
            candidates = candidates.Select(item => new { item.Id, kind = item.Kind.ToString(), identity = item.SpanIdentity }),
            relations = relations.Select(item => new { item.Id, item.CandidateId, item.TargetSpanIdentity, item.MatchTier }),
        });
        return new V5CandidateUniverseV1(candidates, relations, truncated, Hashing.Sha256(canonical));
    }

    private static (IReadOnlyList<V5IssuedRelationV1> Relations, int Truncated) BuildRelations(
        IReadOnlyList<V5IssuedCandidateV1> candidates, IReadOnlyList<SemanticSourceAtom> documentAtoms, V5CandidatePolicyV1 policy)
    {
        if (policy.MaxRelationsPerCandidate == 0) return ([], 0);
        var document = documentAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();

        // Read-only target universe: every whole document occurrence, plus every two-atom window of
        // consecutive source ordinals on one page (a numbered heading split into number + title).
        var targets = new List<(string Identity, string Text, int Page, int Ordinal, HashSet<string> Aliases)>();
        for (var index = 0; index < document.Length; index++)
        {
            var atom = document[index];
            targets.Add(($"{atom.Alias}:0-{atom.Text.Length}", atom.Text, atom.Page, atom.Ordinal, [atom.Alias]));
            if (index + 1 < document.Length && document[index + 1].Ordinal == atom.Ordinal + 1 && document[index + 1].Page == atom.Page)
            {
                var next = document[index + 1];
                targets.Add(($"{atom.Alias}:0-{atom.Text.Length}|{next.Alias}:0-{next.Text.Length}",
                    atom.Text + " " + next.Text, atom.Page, atom.Ordinal, [atom.Alias, next.Alias]));
            }
        }
        var exact = targets.GroupBy(target => Normalize(target.Text), StringComparer.Ordinal).Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var compact = targets.GroupBy(target => Compact(Normalize(target.Text)), StringComparer.Ordinal).Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var ordinalByAlias = document.ToDictionary(atom => atom.Alias, atom => atom.Ordinal, StringComparer.Ordinal);
        var relations = new List<V5IssuedRelationV1>();
        var truncated = 0;
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate.Text);
            if (normalized.Length == 0) continue;
            var own = candidate.Endpoint.Parts.Select(part => part.Alias).ToHashSet(StringComparer.Ordinal);
            var primaryOrdinal = ordinalByAlias[candidate.Endpoint.Parts[0].Alias];
            var tier = "NFKC_WHITESPACE";
            var matches = (exact.TryGetValue(normalized, out var e) ? e : []).Where(target => !target.Aliases.Overlaps(own)).ToArray();
            if (matches.Length == 0)
            {
                tier = "NFKC_WHITESPACE_INSENSITIVE";
                matches = (compact.TryGetValue(Compact(normalized), out var c) ? c : []).Where(target => !target.Aliases.Overlaps(own)).ToArray();
            }
            // Deterministic cap: nearest occurrences in source order first, ties by identity.
            var ordered = matches.OrderBy(target => Math.Abs(target.Ordinal - primaryOrdinal)).ThenBy(target => target.Identity, StringComparer.Ordinal).ToArray();
            truncated += Math.Max(0, ordered.Length - policy.MaxRelationsPerCandidate);
            foreach (var target in ordered.Take(policy.MaxRelationsPerCandidate).OrderBy(target => target.Ordinal).ThenBy(target => target.Identity, StringComparer.Ordinal))
                relations.Add(new V5IssuedRelationV1($"R{relations.Count + 1}", candidate.Id, target.Identity, target.Text, target.Page, tier));
        }
        return (relations, truncated);
    }

    private static SemanticSourcePart Whole(SemanticSourceAtom atom) => new(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias);

    private static SemanticSourcePart? StrictPrefix(SemanticSourceAtom atom)
    {
        var text = atom.Text;
        var end = text.TrimEnd().Length;
        var lastTokenStart = end;
        while (lastTokenStart > 0 && !char.IsWhiteSpace(text[lastTokenStart - 1])) lastTokenStart--;
        var prefixEnd = lastTokenStart;
        while (prefixEnd > 0 && char.IsWhiteSpace(text[prefixEnd - 1])) prefixEnd--;
        var start = text.Length - text.TrimStart().Length;
        if (prefixEnd <= start || prefixEnd >= text.Length) return null;
        return Verbatim(atom, start, prefixEnd);
    }

    private static SemanticSourcePart? StrictSuffix(SemanticSourceAtom atom)
    {
        var text = atom.Text;
        var start = text.Length - text.TrimStart().Length;
        var firstTokenEnd = start;
        while (firstTokenEnd < text.Length && !char.IsWhiteSpace(text[firstTokenEnd])) firstTokenEnd++;
        var suffixStart = firstTokenEnd;
        while (suffixStart < text.Length && char.IsWhiteSpace(text[suffixStart])) suffixStart++;
        var end = text.TrimEnd().Length;
        if (suffixStart >= end) return null;
        return Verbatim(atom, suffixStart, end);
    }

    /// <summary>The binder addresses a substring by value and occurrence; resolve the occurrence that starts at <paramref name="from"/>.</summary>
    private static SemanticSourcePart? Verbatim(SemanticSourceAtom atom, int from, int to)
    {
        if (from < 0 || to <= from || (from == 0 && to == atom.Text.Length)) return null;
        if (char.IsLowSurrogate(atom.Text[from]) || (to < atom.Text.Length && char.IsLowSurrogate(atom.Text[to]))) return null;
        var value = atom.Text[from..to];
        var occurrence = 0;
        for (var at = atom.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = atom.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            occurrence++;
            if (at == from) return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, value, occurrence);
        }
        return null;
    }

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(rune.ToString());
        }
        return builder.ToString().ToUpperInvariant();
    }

    private static string Compact(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
