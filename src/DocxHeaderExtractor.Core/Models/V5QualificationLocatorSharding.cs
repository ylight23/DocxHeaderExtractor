using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Qualification-only opaque locator registry. It is not wired into the live runtime.</summary>
public sealed class RequestLocalLocatorRegistry
{
    private readonly IReadOnlyList<SemanticSourceAtom> _atoms;
    private readonly Dictionary<string, (int Atom, int Offset)> _boundaries;
    private readonly Dictionary<(int Atom, int Offset), string> _handles;

    private RequestLocalLocatorRegistry(IReadOnlyList<SemanticSourceAtom> atoms,
        Dictionary<string, (int Atom, int Offset)> boundaries, Dictionary<(int Atom, int Offset), string> handles)
    { _atoms = atoms; _boundaries = boundaries; _handles = handles; }

    public string Fingerprint { get; private init; } = string.Empty;
    public int AtomCount => _atoms.Count;
    public IReadOnlyList<SemanticSourceAtom> Atoms => _atoms;

    public static RequestLocalLocatorRegistry Create(IReadOnlyList<SemanticSourceAtom> atoms)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        var boundaries = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var handles = new Dictionary<(int, int), string>(); var ordinal = 0;
        foreach (var (atom, index) in atoms.Select((atom, index) => (atom, index)))
        for (var offset = 0; offset <= atom.Text.Length; offset++)
            if (IsScalarBoundary(atom.Text, offset))
            {
                var handle = $"H{ordinal++}"; boundaries.Add(handle, (index, offset)); handles.Add((index, offset), handle);
            }
        var canonical = JsonSerializer.Serialize(new { atoms = atoms.Select((atom, index) => new { handle = $"A{index}", atom.Alias, atom.SourceId, atom.Ordinal, atom.Page, atom.Row, atom.Segment, atom.Text }),
            boundaries = boundaries.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new { handle = item.Key, atom = item.Value.Item1, offset = item.Value.Item2 }) });
        return new RequestLocalLocatorRegistry(atoms, boundaries, handles) { Fingerprint = Hashing.Sha256(canonical) };
    }

    public string AtomHandle(int atom) => atom >= 0 && atom < _atoms.Count ? $"A{atom}" : throw new InvalidOperationException("locator-atom-not-issued");
    public string BoundaryHandle(int atom, int offset) => _handles.TryGetValue((atom, offset), out var handle) ? handle : throw new InvalidOperationException("locator-boundary-not-issued");
    public bool TryBoundary(string handle, out int atom, out int offset)
    { if (_boundaries.TryGetValue(handle, out var value)) { atom = value.Atom; offset = value.Offset; return true; } atom = offset = -1; return false; }
    public IReadOnlyList<string> BoundaryHandles(int atom) => _boundaries.Where(item => item.Value.Atom == atom).Select(item => item.Key).ToArray();

    public BoundClaimEndpoint Decode(OccurrenceLocator locator)
    {
        var parts = new[] { locator.Primary }.Concat(locator.AdditionalParts ?? []).Select(DecodePart).ToArray();
        var binding = SemanticSourcePartBinder.Bind(_atoms, new SemanticSourcePartsProposal(parts));
        if (!binding.IsBound) throw new InvalidOperationException($"locator-binding-invalid:{binding.Status}");
        return new BoundClaimEndpoint(binding.Parts);
    }
    public CanonicalLocatorKey CanonicalKey(OccurrenceLocator locator) => new(Decode(locator).Identity);

    public PrimaryLocatorRoot RootOf(OccurrenceLocator locator)
    {
        var atom = ParseAtom(locator.Primary.Atom);
        if (locator.Primary.From is null && locator.Primary.To is null) return new PrimaryLocatorRoot(atom, null, true);
        if (locator.Primary.From is null || locator.Primary.To is null || !TryBoundary(locator.Primary.From, out var fromAtom, out var from) || fromAtom != atom)
            throw new InvalidOperationException("locator-primary-root-invalid");
        return new PrimaryLocatorRoot(atom, from, false);
    }

    private SemanticSourcePart DecodePart(OccurrenceLocatorPart part)
    {
        var atomIndex = ParseAtom(part.Atom); var atom = _atoms[atomIndex];
        if (part.From is null && part.To is null) return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias);
        if (part.From is null || part.To is null || !TryBoundary(part.From, out var fromAtom, out var from) || !TryBoundary(part.To, out var toAtom, out var to) || fromAtom != atomIndex || toAtom != atomIndex)
            throw new InvalidOperationException("locator-boundary-not-issued-for-atom");
        if (from >= to) throw new InvalidOperationException("locator-boundary-order-invalid");
        if (from == 0 && to == atom.Text.Length) throw new InvalidOperationException("locator-whole-must-omit-boundaries");
        var value = atom.Text[from..to]; var occurrence = 0;
        for (var at = atom.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = atom.Text.IndexOf(value, at + 1)) { occurrence++; if (at == from) return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, value, occurrence); }
        throw new InvalidOperationException("locator-selection-not-resolvable");
    }

    private int ParseAtom(string handle) => handle.StartsWith('A') && int.TryParse(handle[1..], out var atom) && atom >= 0 && atom < _atoms.Count
        ? atom : throw new InvalidOperationException("locator-atom-not-issued");
    private static bool IsScalarBoundary(string text, int offset) => offset == 0 || offset == text.Length || !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
}

public sealed record OccurrenceLocatorPart([property: JsonPropertyName("atom")] string Atom, [property: JsonPropertyName("from")] string? From = null, [property: JsonPropertyName("to")] string? To = null);
public sealed record OccurrenceLocator([property: JsonPropertyName("primary")] OccurrenceLocatorPart Primary, [property: JsonPropertyName("additionalParts")] IReadOnlyList<OccurrenceLocatorPart> AdditionalParts, [property: JsonPropertyName("functions")] IReadOnlyList<string> Functions);
public sealed record OccurrenceLocatorResponse([property: JsonPropertyName("occurrences")] IReadOnlyList<OccurrenceLocator> Occurrences);
public sealed record PrimaryLocatorRoot(int AtomIndex, int? Start, bool Whole);
public sealed record CanonicalLocatorKey(string EndpointIdentity);

/// <summary>A deterministic primary-root partition. Multipart continuations deliberately do not own a shard.</summary>
public sealed record OccurrenceShardDomain(string ShardId, IReadOnlyList<PrimaryLocatorRoot> Roots)
{
    public bool Owns(RequestLocalLocatorRegistry registry, OccurrenceLocator locator) => Roots.Contains(registry.RootOf(locator));
    public bool IsLeaf => Roots.Count == 1;
    public static OccurrenceShardDomain Full(RequestLocalLocatorRegistry registry)
    {
        var roots = new List<PrimaryLocatorRoot>();
        for (var atom = 0; atom < registry.AtomCount; atom++)
        {
            roots.Add(new PrimaryLocatorRoot(atom, null, true));
            foreach (var handle in registry.BoundaryHandles(atom)) if (registry.TryBoundary(handle, out _, out var offset) && offset < registry.Atoms[atom].Text.Length)
                roots.Add(new PrimaryLocatorRoot(atom, offset, false));
        }
        return new("root", roots.OrderBy(root => root.AtomIndex).ThenBy(root => root.Whole ? -1 : root.Start).ToArray());
    }
}

public static class OccurrenceShardSplitter
{
    public static (OccurrenceShardDomain Left, OccurrenceShardDomain Right) Split(OccurrenceShardDomain parent)
    {
        if (parent.Roots.Count < 2) throw new InvalidOperationException("occurrence-shard-unsplittable");
        var middle = parent.Roots.Count / 2;
        return (new OccurrenceShardDomain(parent.ShardId + ".0", parent.Roots.Take(middle).ToArray()),
            new OccurrenceShardDomain(parent.ShardId + ".1", parent.Roots.Skip(middle).ToArray()));
    }
    public static OverflowDecision OnResponseBytes(OccurrenceShardDomain domain, int responseBytes, int cap)
    {
        if (responseBytes <= cap) return new("ACCEPT", null, null);
        if (domain.IsLeaf) return new("INCOMPLETE_UNSPLITTABLE_OVERFLOW", null, null);
        var children = Split(domain); return new("SPLIT_WITHOUT_PARSE_OR_BIND", children.Left, children.Right);
    }
}
public sealed record OverflowDecision(string Status, OccurrenceShardDomain? Left, OccurrenceShardDomain? Right);
