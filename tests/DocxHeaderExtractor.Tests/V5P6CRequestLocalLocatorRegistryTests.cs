using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6C replaces P6B's parseable A#:B# coordinate encoding with an opaque, request-local handle
/// registry.  This is still qualification-only: it proves authority isolation, not a provider lane.
/// </summary>
public sealed class V5P6CRequestLocalLocatorRegistryTests
{
    private const string Root = "artifacts/v5-p6c-request-local-locator-registry";

    [Fact]
    public void Freeze_opaque_issued_boundary_registry_and_shard_domain_rejection_without_provider()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "source", 0, 1, 1, 0, "First Heading; Second Heading"),
            new SemanticSourceAtom("L1", "source", 1, 1, 2, 0, "Continuation"),
        };
        var registry = HarnessIssuedLocatorRegistry.Issue(atoms);
        var endpoint = Bind(atoms,
            new SemanticSourcePart("L0", CanonicalSemanticSelectionMode.VerbatimText, "First Heading"),
            new SemanticSourcePart("L1", CanonicalSemanticSelectionMode.WholeAlias));
        var locator = registry.Encode(endpoint, ["STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"]);
        Assert.Equal(endpoint.Identity, registry.Decode(locator).Identity);

        var serialized = JsonSerializer.Serialize(locator);
        Assert.DoesNotContain("First Heading", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("verbatimText", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("context", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("H", locator.Primary.From, StringComparison.Ordinal);
        Assert.StartsWith("H", locator.Primary.To, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => registry.Decode(locator with
        {
            Primary = locator.Primary with { From = "H999" },
        }));
        Assert.Throws<InvalidOperationException>(() => registry.Decode(locator with
        {
            Primary = locator.Primary with { From = registry.HandleFor(1, 0) },
        }));
        Assert.Throws<InvalidOperationException>(() => registry.Decode(new IssuedLocatorOccurrence(
            new IssuedLocatorPart("A0", registry.HandleFor(0, 0), registry.HandleFor(0, atoms[0].Text.Length)), [], ["STRUCTURAL_REGION"])));

        var permittedFrom = new HashSet<string>([registry.HandleFor(0, 0)], StringComparer.Ordinal);
        var permittedTo = registry.HandlesForAtom(0).ToHashSet(StringComparer.Ordinal);
        var shard = registry.ForPrimaryDomain("S1", permittedFrom, permittedTo, ["A0"]);
        Assert.Equal(endpoint.Identity, shard.Decode(locator).Identity);
        Assert.Throws<InvalidOperationException>(() => shard.Decode(locator with
        {
            Primary = locator.Primary with { From = registry.HandleFor(0, 1) },
        }));

        FreezeArtifact.AssertJson(Root, "request-local-locator-registry.v1.json", new
        {
            schemaVersion = "v5-p6c-request-local-locator-registry-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            correction = new
            {
                p6bBoundaryAuthority = "RANGE_VALIDATED_COORDINATE_ENCODING_NOT_ISSUED_REGISTRY",
                p6cBoundaryAuthority = "OPAQUE_REQUEST_LOCAL_REGISTRY_LOOKUP",
            },
            registry = new
            {
                atoms = "A# maps to one owned source atom in this request",
                boundaries = "H# maps to exactly one (A#, offset) pair issued by this request",
                decoder = "lookup only; no numeric offset is parsed from a provider response",
                unknownHandle = "REJECT_BEFORE_BIND",
                crossAtomHandle = "REJECT_BEFORE_BIND",
            },
            shardDomain = new
            {
                owner = "primary atom plus an issued primary-from handle domain; whole atoms have an explicit owner set",
                strictLocator = "from must be in the shard's allowedFrom registry subset and to must be in its allowedTo subset",
                issuedButOutOfDomain = "REJECT_BEFORE_BIND",
                multipart = "decoded through the existing exact binder after primary-domain validation; it does not change primary shard ownership",
            },
            retainedP6BProperties = new
            {
                sourceTextCopy = "ABSENT",
                contextEcho = "ABSENT",
                wholeAtomCanonicalForm = "atom only; full-span boundary pair is rejected",
                strictAndMultipartIdentity = "LOSSLESS_ROUND_TRIP_TO_BoundClaimEndpoint.Identity",
                unaryFunctions = "UNIQUE_SUBSET_OF_DOCUMENT_IDENTITY_STRUCTURAL_REGION_NAVIGATION_REPRESENTATION",
            },
            remainingGate = new
            {
                coarseShardAggregateVolume = "NOT_ESTABLISHED",
                providerManifest = "NOT_PREPARED",
                providerExecution = "BLOCKED",
                sharedRuntime = "UNCHANGED",
            },
            conclusion = "P6C makes boundary authority real: only a request-issued opaque handle can select a boundary, and a shard can reject even issued handles outside its domain before binding. Aggregate shard sizing remains a separate proof.",
        });
    }

    private static BoundClaimEndpoint Bind(IReadOnlyList<SemanticSourceAtom> atoms, params SemanticSourcePart[] parts)
    {
        var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
        Assert.True(binding.IsBound, binding.Reason);
        return new BoundClaimEndpoint(binding.Parts);
    }

    private sealed record IssuedLocatorPart(
        [property: JsonPropertyName("atom")] string Atom,
        [property: JsonPropertyName("from")] string? From = null,
        [property: JsonPropertyName("to")] string? To = null);

    private sealed record IssuedLocatorOccurrence(
        [property: JsonPropertyName("primary")] IssuedLocatorPart Primary,
        [property: JsonPropertyName("additionalParts")] IReadOnlyList<IssuedLocatorPart> AdditionalParts,
        [property: JsonPropertyName("functions")] IReadOnlyList<string> Functions);

    private sealed class HarnessIssuedLocatorRegistry
    {
        private readonly IReadOnlyList<SemanticSourceAtom> _atoms;
        private readonly IReadOnlyDictionary<string, int> _atomIndices;
        private readonly IReadOnlyDictionary<string, Boundary> _boundaries;
        private readonly IReadOnlyDictionary<(int AtomIndex, int Offset), string> _handles;
        private readonly HashSet<string>? _allowedFrom;
        private readonly HashSet<string>? _allowedTo;
        private readonly HashSet<string>? _allowedWholeAtoms;

        private HarnessIssuedLocatorRegistry(IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlyDictionary<string, Boundary> boundaries,
            IReadOnlyDictionary<(int AtomIndex, int Offset), string> handles, HashSet<string>? allowedFrom = null,
            HashSet<string>? allowedTo = null, HashSet<string>? allowedWholeAtoms = null)
        {
            _atoms = atoms;
            _atomIndices = atoms.Select((_, index) => index).ToDictionary(index => $"A{index}", index => index, StringComparer.Ordinal);
            _boundaries = boundaries;
            _handles = handles;
            _allowedFrom = allowedFrom;
            _allowedTo = allowedTo;
            _allowedWholeAtoms = allowedWholeAtoms;
        }

        public static HarnessIssuedLocatorRegistry Issue(IReadOnlyList<SemanticSourceAtom> atoms)
        {
            var boundaries = new Dictionary<string, Boundary>(StringComparer.Ordinal);
            var handles = new Dictionary<(int AtomIndex, int Offset), string>();
            var ordinal = 0;
            foreach (var (atom, atomIndex) in atoms.Select((atom, index) => (atom, index)))
            for (var offset = 0; offset <= atom.Text.Length; offset++)
            {
                var handle = $"H{ordinal++}";
                boundaries.Add(handle, new Boundary(atomIndex, offset));
                handles.Add((atomIndex, offset), handle);
            }
            return new HarnessIssuedLocatorRegistry(atoms, boundaries, handles);
        }

        public string HandleFor(int atomIndex, int offset) => _handles.TryGetValue((atomIndex, offset), out var handle)
            ? handle : throw new InvalidOperationException("requested-boundary-not-issued");

        public IEnumerable<string> HandlesForAtom(int atomIndex) => _boundaries.Where(pair => pair.Value.AtomIndex == atomIndex).Select(pair => pair.Key);

        public HarnessIssuedLocatorRegistry ForPrimaryDomain(string shardId, HashSet<string> allowedFrom,
            HashSet<string> allowedTo, IReadOnlyList<string> allowedWholeAtoms)
        {
            Assert.False(string.IsNullOrWhiteSpace(shardId));
            Assert.All(allowedFrom.Concat(allowedTo), handle => Assert.True(_boundaries.ContainsKey(handle)));
            return new HarnessIssuedLocatorRegistry(_atoms, _boundaries, _handles, allowedFrom, allowedTo,
                allowedWholeAtoms.ToHashSet(StringComparer.Ordinal));
        }

        public IssuedLocatorOccurrence Encode(BoundClaimEndpoint endpoint, IReadOnlyList<string> functions)
        {
            ValidateFunctions(functions);
            var parts = endpoint.Parts.Select(part => EncodePart(part)).ToArray();
            return new IssuedLocatorOccurrence(parts[0], parts.Skip(1).ToArray(), functions.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }

        public BoundClaimEndpoint Decode(IssuedLocatorOccurrence occurrence)
        {
            ValidateFunctions(occurrence.Functions);
            var primary = DecodePart(occurrence.Primary, isPrimary: true);
            var additional = (occurrence.AdditionalParts ?? []).Select(part => DecodePart(part, isPrimary: false)).ToArray();
            var binding = SemanticSourcePartBinder.Bind(_atoms, new SemanticSourcePartsProposal([primary, .. additional]));
            if (!binding.IsBound) throw new InvalidOperationException($"issued-locator-binding-invalid:{binding.Status}");
            return new BoundClaimEndpoint(binding.Parts);
        }

        private IssuedLocatorPart EncodePart(BoundSourcePart part)
        {
            var atomIndex = _atoms.Select((atom, index) => (atom, index)).Single(item => item.atom.Alias == part.Alias).index;
            return part.Start == 0 && part.End == _atoms[atomIndex].Text.Length
                ? new IssuedLocatorPart($"A{atomIndex}")
                : new IssuedLocatorPart($"A{atomIndex}", HandleFor(atomIndex, part.Start), HandleFor(atomIndex, part.End));
        }

        private SemanticSourcePart DecodePart(IssuedLocatorPart part, bool isPrimary)
        {
            if (!_atomIndices.TryGetValue(part.Atom, out var atomIndex)) throw new InvalidOperationException("issued-locator-atom-unknown");
            var atom = _atoms[atomIndex];
            if (part.From is null && part.To is null)
            {
                if (isPrimary && _allowedWholeAtoms is not null && !_allowedWholeAtoms.Contains(part.Atom))
                    throw new InvalidOperationException("issued-locator-whole-atom-outside-shard-domain");
                return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias);
            }
            if (part.From is null || part.To is null) throw new InvalidOperationException("issued-locator-boundary-pair-incomplete");
            if (!_boundaries.TryGetValue(part.From, out var from) || !_boundaries.TryGetValue(part.To, out var to))
                throw new InvalidOperationException("issued-locator-boundary-unknown");
            if (from.AtomIndex != atomIndex || to.AtomIndex != atomIndex) throw new InvalidOperationException("issued-locator-boundary-atom-mismatch");
            if (isPrimary && _allowedFrom is not null && !_allowedFrom.Contains(part.From)) throw new InvalidOperationException("issued-locator-from-outside-shard-domain");
            if (isPrimary && _allowedTo is not null && !_allowedTo.Contains(part.To)) throw new InvalidOperationException("issued-locator-to-outside-shard-domain");
            if (from.Offset >= to.Offset) throw new InvalidOperationException("issued-locator-boundary-order-invalid");
            if (from.Offset == 0 && to.Offset == atom.Text.Length) throw new InvalidOperationException("issued-locator-whole-atom-must-omit-boundaries");
            var selection = atom.Text[from.Offset..to.Offset];
            return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, selection,
                OccurrenceAt(atom.Text, selection, from.Offset));
        }

        private static int OccurrenceAt(string source, string selection, int start)
        {
            var occurrence = 0;
            for (var at = source.IndexOf(selection, StringComparison.Ordinal); at >= 0; at = source.IndexOf(selection, at + 1))
            {
                occurrence++;
                if (at == start) return occurrence;
            }
            throw new InvalidOperationException("issued-locator-boundary-does-not-resolve-to-selection");
        }

        private static void ValidateFunctions(IReadOnlyList<string> functions)
        {
            var allowed = new HashSet<string>(["DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"], StringComparer.Ordinal);
            if (functions is null || functions.Count == 0 || functions.Any(function => !allowed.Contains(function)) || functions.Distinct(StringComparer.Ordinal).Count() != functions.Count)
                throw new InvalidOperationException("issued-locator-functions-invalid");
        }

        private sealed record Boundary(int AtomIndex, int Offset);
    }
}
