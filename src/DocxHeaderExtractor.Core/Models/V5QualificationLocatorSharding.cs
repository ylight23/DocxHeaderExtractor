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
    public LocatorDirectory Directory() => new(_atoms.Select((atom, index) => new LocatorDirectoryAtom($"A{index}", atom.Text,
        _boundaries.Where(pair => pair.Value.Atom == index).Select(pair => new LocatorDirectoryBoundary(pair.Key, pair.Value.Offset)).ToArray())).ToArray(), Fingerprint);

    public BoundClaimEndpoint Decode(OccurrenceLocator locator)
    {
        var parts = new[] { locator.Primary }.Concat(locator.AdditionalParts ?? []).Select(DecodePart).ToArray();
        var binding = SemanticSourcePartBinder.Bind(_atoms, new SemanticSourcePartsProposal(parts));
        if (!binding.IsBound) throw new InvalidOperationException($"locator-binding-invalid:{binding.Status}");
        return new BoundClaimEndpoint(binding.Parts);
    }
    public CanonicalLocatorKey CanonicalKey(OccurrenceLocator locator) => new(Decode(locator).Identity);

    public OccurrenceLocatorResponseResult Parse(JsonElement payload, int rawUtf8Bytes, int responseCap, IReadOnlySet<int> ownedAtomIndices)
        => OccurrenceLocatorResponseParser.Parse(payload, rawUtf8Bytes, responseCap, this, ownedAtomIndices);

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

    private int ParseAtom(string handle) => TryParseAtom(handle, _atoms.Count, out var atom) && AtomHandle(atom) == handle
        ? atom : throw new InvalidOperationException("locator-atom-not-issued");
    private static bool TryParseAtom(string handle, int count, out int atom)
    {
        atom = -1;
        return handle.StartsWith('A') && handle.Length > 1 && int.TryParse(handle.AsSpan(1), out atom) && atom >= 0 && atom < count;
    }
    private static bool IsScalarBoundary(string text, int offset) => offset == 0 || offset == text.Length || !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
}

public sealed record OccurrenceLocatorPart([property: JsonPropertyName("atom")] string Atom, [property: JsonPropertyName("from")] string? From = null, [property: JsonPropertyName("to")] string? To = null);
public sealed record OccurrenceLocator([property: JsonPropertyName("primary")] OccurrenceLocatorPart Primary, [property: JsonPropertyName("additionalParts")] IReadOnlyList<OccurrenceLocatorPart> AdditionalParts, [property: JsonPropertyName("functions")] IReadOnlyList<string> Functions);
public sealed record OccurrenceLocatorResponse([property: JsonPropertyName("occurrences")] IReadOnlyList<OccurrenceLocator> Occurrences);
public sealed record PrimaryLocatorRoot(int AtomIndex, int? Start, bool Whole);
public sealed record CanonicalLocatorKey(string EndpointIdentity);
public sealed record LocatorDirectoryBoundary(string Handle, int Utf16Offset);
public sealed record LocatorDirectoryAtom(string Atom, string Text, IReadOnlyList<LocatorDirectoryBoundary> Boundaries);
public sealed record LocatorDirectory(IReadOnlyList<LocatorDirectoryAtom> Atoms, string RegistryFingerprint);
public sealed record QuarantinedOccurrence(int Ordinal, string Reason);
public sealed record OccurrenceLocatorResponseResult(OccurrenceLocatorResponse Response, IReadOnlyList<QuarantinedOccurrence> Quarantined, int CanonicalUtf8Bytes);

/// <summary>Candidate-local locator validation with response-wide byte guards and post-bind canonical dedupe.</summary>
public static class OccurrenceLocatorResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(CanonicalJson.Options);
    private static readonly HashSet<string> AllowedFunctions = new(["DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"], StringComparer.Ordinal);

    public static OccurrenceLocatorResponseResult Parse(JsonElement payload, int rawUtf8Bytes, int responseCap,
        RequestLocalLocatorRegistry registry, IReadOnlySet<int> ownedAtomIndices)
    {
        if (rawUtf8Bytes < 0 || responseCap < 1 || rawUtf8Bytes > responseCap)
            throw new InvalidOperationException("occurrence-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1 || payload.EnumerateObject().Any(property => !property.NameEquals("occurrences")) ||
            !payload.TryGetProperty("occurrences", out var occurrences) || occurrences.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("occurrence-response-root-invalid");

        var byIdentity = new Dictionary<string, (BoundClaimEndpoint Endpoint, HashSet<string> Functions)>(StringComparer.Ordinal);
        var quarantined = new List<QuarantinedOccurrence>(); var ordinal = 0;
        foreach (var element in occurrences.EnumerateArray())
        {
            try
            {
                var locator = ReadCandidate(element);
                ValidateFunctions(locator.Functions);
                var primaryIndex = ParseAtom(locator.Primary.Atom, registry);
                if (!ownedAtomIndices.Contains(primaryIndex)) throw new InvalidOperationException("primary-atom-not-owned-by-shard");
                var parts = new[] { locator.Primary }.Concat(locator.AdditionalParts).ToArray();
                var previous = primaryIndex;
                foreach (var part in parts.Skip(1))
                {
                    var index = ParseAtom(part.Atom, registry);
                    if (!ownedAtomIndices.Contains(index)) throw new InvalidOperationException("additional-atom-not-owned");
                    if (index <= previous) throw new InvalidOperationException("additional-atoms-not-strictly-increasing");
                    previous = index;
                }
                var endpoint = registry.Decode(locator);
                if (!byIdentity.TryGetValue(endpoint.Identity, out var value)) value = (endpoint, new HashSet<string>(StringComparer.Ordinal));
                value.Functions.UnionWith(locator.Functions);
                byIdentity[endpoint.Identity] = value;
            }
            catch (InvalidOperationException exception) { quarantined.Add(new QuarantinedOccurrence(ordinal, exception.Message)); }
            catch (JsonException) { quarantined.Add(new QuarantinedOccurrence(ordinal, "candidate-json-shape-invalid")); }
            ordinal++;
        }

        var normalized = byIdentity.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var parts = pair.Value.Endpoint.Parts.Select(part => EncodeBoundPart(registry, part)).ToArray();
            return new OccurrenceLocator(parts[0], parts.Skip(1).ToArray(), pair.Value.Functions.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }).ToArray();
        var response = new OccurrenceLocatorResponse(normalized);
        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length;
        if (canonicalBytes > responseCap) throw new InvalidOperationException("occurrence-canonical-response-byte-cap-exceeded");
        return new OccurrenceLocatorResponseResult(response, quarantined, canonicalBytes);
    }

    private static OccurrenceLocator ReadCandidate(JsonElement element)
    {
        RequireObjectKeys(element, "primary", "additionalParts", "functions");
        if (!element.TryGetProperty("primary", out var primary) || !element.TryGetProperty("additionalParts", out var additional) || additional.ValueKind != JsonValueKind.Array ||
            !element.TryGetProperty("functions", out var functions) || functions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("candidate-fields-invalid");
        var extra = additional.EnumerateArray().Select(ReadPart).ToArray();
        var names = functions.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidOperationException("function-name-invalid")).ToArray();
        return new OccurrenceLocator(ReadPart(primary), extra, names);
    }

    private static OccurrenceLocatorPart ReadPart(JsonElement element)
    {
        var properties = element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().ToArray() : [];
        if (element.ValueKind != JsonValueKind.Object || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length || properties.Any(property => property.Name is not ("atom" or "from" or "to")) || !element.TryGetProperty("atom", out var atom) || atom.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("locator-part-fields-invalid");
        string? from = null; string? to = null;
        if (element.TryGetProperty("from", out var fromElement)) from = fromElement.ValueKind == JsonValueKind.String ? fromElement.GetString() : throw new InvalidOperationException("locator-from-invalid");
        if (element.TryGetProperty("to", out var toElement)) to = toElement.ValueKind == JsonValueKind.String ? toElement.GetString() : throw new InvalidOperationException("locator-to-invalid");
        if ((from is null) != (to is null)) throw new InvalidOperationException("locator-boundary-pair-incomplete");
        return new OccurrenceLocatorPart(atom.GetString()!, from, to);
    }

    private static void RequireObjectKeys(JsonElement element, params string[] keys)
    {
        var properties = element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().ToArray() : [];
        if (element.ValueKind != JsonValueKind.Object || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length || properties.Any(property => !keys.Contains(property.Name, StringComparer.Ordinal)) || keys.Any(key => !element.TryGetProperty(key, out _)))
            throw new InvalidOperationException("candidate-fields-invalid");
    }

    private static void ValidateFunctions(IReadOnlyList<string> functions)
    {
        if (functions.Count == 0 || functions.Any(function => !AllowedFunctions.Contains(function)) || functions.Distinct(StringComparer.Ordinal).Count() != functions.Count)
            throw new InvalidOperationException("semantic-functions-invalid");
    }

    private static int ParseAtom(string handle, RequestLocalLocatorRegistry registry) =>
        handle.StartsWith('A') && handle.Length > 1 && int.TryParse(handle.AsSpan(1), out var index) && index >= 0 && index < registry.AtomCount && registry.AtomHandle(index) == handle
            ? index : throw new InvalidOperationException("locator-atom-unknown");

    private static OccurrenceLocatorPart EncodeBoundPart(RequestLocalLocatorRegistry registry, BoundSourcePart part)
    {
        var index = registry.Atoms.Select((atom, index) => (atom, index)).Single(item => item.atom.Alias == part.Alias).index;
        var atom = registry.Atoms[index];
        return part.Start == 0 && part.End == atom.Text.Length
            ? new OccurrenceLocatorPart(registry.AtomHandle(index))
            : new OccurrenceLocatorPart(registry.AtomHandle(index), registry.BoundaryHandle(index, part.Start), registry.BoundaryHandle(index, part.End));
    }
}

public sealed record OwnedAtomShardDomain(string ShardId, IReadOnlyList<int> OwnedAtomIndices)
{
    public static OwnedAtomShardDomain Full(int ownedAtomCount) => new("root", Enumerable.Range(0, ownedAtomCount).ToArray());
    public bool Owns(OccurrenceLocator locator) => locator.Primary.Atom.StartsWith('A') && int.TryParse(locator.Primary.Atom.AsSpan(1), out var index) &&
        locator.Primary.Atom == $"A{index}" && OwnedAtomIndices.Contains(index);
}

public sealed record OwnedAtomSplitResult(string Status, OwnedAtomShardDomain? Left = null, OwnedAtomShardDomain? Right = null);
public static class OwnedAtomShardSplitter
{
    public static (OwnedAtomShardDomain Left, OwnedAtomShardDomain Right) Split(OwnedAtomShardDomain parent)
    {
        if (parent.OwnedAtomIndices.Count < 2) throw new InvalidOperationException("owned-atom-shard-unsplittable");
        var midpoint = parent.OwnedAtomIndices.Count / 2;
        return (new(parent.ShardId + ".0", parent.OwnedAtomIndices.Take(midpoint).ToArray()), new(parent.ShardId + ".1", parent.OwnedAtomIndices.Skip(midpoint).ToArray()));
    }
    public static OwnedAtomSplitResult OnUnsafeResponse(OwnedAtomShardDomain domain, string? finishReason, int responseBytes, int responseCap)
    {
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) || responseBytes > responseCap)
            return domain.OwnedAtomIndices.Count == 1 ? new("INCOMPLETE_UNSPLITTABLE_SEMANTIC_OVERFLOW") : ChildResult(domain);
        return new("READY_FOR_PARSE");
    }
    public static long MaximumTreeNodes(int leaves) => checked(2L * leaves - 1);
    private static OwnedAtomSplitResult ChildResult(OwnedAtomShardDomain domain) { var children = Split(domain); return new("SPLIT_WITHOUT_PARSE_BIND_REPAIR_OR_RETRY", children.Left, children.Right); }
}

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
