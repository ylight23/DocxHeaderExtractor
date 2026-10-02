using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6B is a qualification-only locator contract.  Its handles are issued from source atoms by
/// the harness; source text, aliases, contexts, and free numeric coordinates are not provider
/// response fields.  It intentionally does not promote this experimental wire to the runtime.
/// </summary>
public sealed class V5P6BHarnessIssuedSourceLocatorContractTests
{
    private const string Root = "artifacts/v5-p6b-harness-issued-locator";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract =
        DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    [Fact]
    public void Freeze_lossless_harness_locator_contract_and_singleton_shard_bound_without_provider()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "source", 0, 1, 1, 0, "First Heading; Second Heading"),
            new SemanticSourceAtom("L1", "source", 1, 1, 2, 0, "Continuation"),
        };
        var domain = HarnessLocatorDomain.Create(atoms);

        var whole = Bind(atoms, new SemanticSourcePart("L0", CanonicalSemanticSelectionMode.WholeAlias));
        var strict = Bind(atoms, new SemanticSourcePart("L0", CanonicalSemanticSelectionMode.VerbatimText, "First Heading"));
        var multipart = Bind(atoms,
            new SemanticSourcePart("L0", CanonicalSemanticSelectionMode.VerbatimText, "First Heading"),
            new SemanticSourcePart("L1", CanonicalSemanticSelectionMode.WholeAlias));
        AssertRoundTrip(domain, whole, ["STRUCTURAL_REGION"]);
        AssertRoundTrip(domain, strict, ["DOCUMENT_IDENTITY", "STRUCTURAL_REGION"]);
        AssertRoundTrip(domain, multipart, ["NAVIGATION_REPRESENTATION"]);

        var strictLocator = domain.Encode(strict, ["STRUCTURAL_REGION"]);
        var text = JsonSerializer.Serialize(strictLocator);
        Assert.DoesNotContain("First Heading", text, StringComparison.Ordinal);
        Assert.DoesNotContain("verbatimText", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("context", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("A0", strictLocator.Primary.Atom);
        Assert.NotNull(strictLocator.Primary.From);
        Assert.NotNull(strictLocator.Primary.To);

        Assert.Throws<InvalidOperationException>(() => domain.Decode(new LocatorOccurrence(
            new LocatorPart("A0", "A0:B0", $"A0:B{atoms[0].Text.Length}"), [], ["STRUCTURAL_REGION"])));
        Assert.Throws<InvalidOperationException>(() => domain.Decode(new LocatorOccurrence(
            new LocatorPart("A0", "A1:B0", "A1:B1"), [], ["STRUCTURAL_REGION"])));
        Assert.Throws<InvalidOperationException>(() => domain.Decode(new LocatorOccurrence(
            new LocatorPart("A1"), [new LocatorPart("A0")], ["STRUCTURAL_REGION"])));

        var singletonRows = SingletonLocatorResponseBounds();
        Assert.Equal(31, singletonRows.Count);
        Assert.All(singletonRows, row => Assert.True(row.ResponseUtf8Bytes <= ResponseCap));
        var largest = singletonRows.OrderByDescending(row => row.ResponseUtf8Bytes)
            .ThenBy(row => row.DocumentId, StringComparer.Ordinal).ThenBy(row => row.ParentOrdinal).First();

        FreezeArtifact.AssertJson(Root, "harness-issued-source-locator-contract.v1.json", new
        {
            schemaVersion = "v5-p6b-harness-issued-source-locator-contract-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            wire = new
            {
                protocol = "v5-heading-occurrence-locator-qualification-1",
                response = "occurrences[]: primary locator, ordered additional locators, unique unary functions[]",
                locator = "atom handle plus either no boundaries for a whole atom, or harness-issued from/to boundary handles for a strict span",
                forbiddenProviderFields = new[] { "sourceAlias", "sourceId", "sourceOrdinal", "verbatimText", "leftExactContext", "rightExactContext", "freeNumericStart", "freeNumericEnd" },
                relations = "OUT_OF_SCOPE",
                runtime = "UNCHANGED",
            },
            validation = new
            {
                atomHandle = "must be present in the request-local owned atom domain",
                boundaries = "must be harness-issued for that atom; strict form requires from < to; a full-atom boundary pair is refused because whole form is canonical handle-only",
                multipart = "additional parts must be owned, strictly source-ordered, and are passed through the existing exact binder",
                functions = Contract.Predicates.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                functionSet = "unique finite subset; no mutual-exclusion assumption",
            },
            completeness = new
            {
                wholeAtom = "ROUND_TRIP_PASS",
                strictSubstring = "ROUND_TRIP_PASS",
                multipart = "ROUND_TRIP_PASS",
                identityAuthority = "Decode(locator) then SemanticSourcePartBinder.Bind reproduces BoundClaimEndpoint.Identity.",
            },
            singletonExactIdentityShard = new
            {
                policy = "owner is a complete harness-issued locator identity, including ordered continuation locators; a response admits at most that one locator record.",
                semanticRestriction = "NONE: every exact source identity has a deterministic owner shard; no occurrence/count cap is inferred.",
                maxResponseUtf8Bytes = ResponseCap,
                largestFrozen31Response = largest,
                all31Fit = true,
                caveat = "This establishes a finite safe execution leaf, not an operationally acceptable number of provider calls. Coarser source-domain aggregation requires a separate source-domain-handle and aggregate-volume proof.",
            },
            metrics = new
            {
                semanticSelectionQuality = "future Gold occurrence evaluation over locator-selected identities",
                exactBindingQuality = "locator decode/binder acceptance and identity equality, reported separately",
            },
            providerManifest = "NOT_PREPARED: P6B is a local contract proof only; no model behavior or practical shard schedule has been qualified.",
            conclusion = "Harness-issued handles remove copied-text and context-echo representation failures while retaining exact source identity. A singleton exact-identity shard fits the response cap without semantic cardinality, but P6B does not claim that this leaf granularity is operationally viable.",
        });
    }

    private static IReadOnlyList<SingletonBoundRow> SingletonLocatorResponseBounds()
    {
        var rows = new List<SingletonBoundRow>();
        foreach (var (documentId, pdf, expectedPacks) in new[]
        {
            ("SRC-089", SourcePdfCorpus.Src089, 7),
            ("SRC-095", SourcePdfCorpus.Src095, 24),
        })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), documentId, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(expectedPacks, packs.Count);
            foreach (var (pack, index) in packs.Select((pack, index) => (pack, index)))
            {
                var sourceAtoms = pack.Packet.SubjectEvidence.Select((node, ownedIndex) => new SemanticSourceAtom(
                    node.SourceAlias, node.SourceId, node.SourceOrdinal, 0, ownedIndex, 0, node.Text)).ToArray();
                var domain = HarnessLocatorDomain.Create(sourceAtoms);
                var parts = sourceAtoms.Select(atom => atom.Text.Length > 1
                    ? new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, atom.Text[..1], Occurrence: 1)
                    : new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias)).ToArray();
                var endpoint = Bind(sourceAtoms, parts);
                var occurrence = domain.Encode(endpoint, Contract.Predicates.Select(item => item.Name).ToArray());
                var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { occurrences = new[] { occurrence } }));
                rows.Add(new SingletonBoundRow(documentId, index + 1, sourceAtoms.Length, endpoint.Parts.Count, bytes));
            }
        }
        return rows;
    }

    private static BoundClaimEndpoint Bind(IReadOnlyList<SemanticSourceAtom> atoms, params SemanticSourcePart[] parts)
    {
        var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
        Assert.True(binding.IsBound, binding.Reason);
        return new BoundClaimEndpoint(binding.Parts);
    }

    private static void AssertRoundTrip(HarnessLocatorDomain domain, BoundClaimEndpoint endpoint, IReadOnlyList<string> functions)
    {
        var locator = domain.Encode(endpoint, functions);
        var decoded = domain.Decode(locator);
        Assert.Equal(endpoint.Identity, decoded.Identity);
    }

    private sealed record LocatorPart(
        [property: JsonPropertyName("atom")] string Atom,
        [property: JsonPropertyName("from")] string? From = null,
        [property: JsonPropertyName("to")] string? To = null);

    private sealed record LocatorOccurrence(
        [property: JsonPropertyName("primary")] LocatorPart Primary,
        [property: JsonPropertyName("additionalParts")] IReadOnlyList<LocatorPart> AdditionalParts,
        [property: JsonPropertyName("functions")] IReadOnlyList<string> Functions);

    private sealed class HarnessLocatorDomain
    {
        private readonly IReadOnlyList<SemanticSourceAtom> _atoms;
        private readonly IReadOnlyDictionary<string, int> _byHandle;

        private HarnessLocatorDomain(IReadOnlyList<SemanticSourceAtom> atoms)
        {
            _atoms = atoms;
            _byHandle = atoms.Select((_, index) => index).ToDictionary(index => $"A{index}", index => index, StringComparer.Ordinal);
        }

        public static HarnessLocatorDomain Create(IReadOnlyList<SemanticSourceAtom> atoms) => new(atoms);

        public LocatorOccurrence Encode(BoundClaimEndpoint endpoint, IReadOnlyList<string> functions)
        {
            var parts = endpoint.Parts.Select(EncodePart).ToArray();
            ValidateFunctions(functions);
            return new LocatorOccurrence(parts[0], parts.Skip(1).ToArray(), functions.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }

        public BoundClaimEndpoint Decode(LocatorOccurrence occurrence)
        {
            ValidateFunctions(occurrence.Functions);
            var parts = new[] { occurrence.Primary }.Concat(occurrence.AdditionalParts ?? []).Select(DecodePart).ToArray();
            var binding = SemanticSourcePartBinder.Bind(_atoms, new SemanticSourcePartsProposal(parts));
            if (!binding.IsBound) throw new InvalidOperationException($"locator-binding-invalid:{binding.Status}");
            return new BoundClaimEndpoint(binding.Parts);
        }

        private LocatorPart EncodePart(BoundSourcePart part)
        {
            var index = _atoms.Select((atom, index) => (atom, index)).Single(item => item.atom.Alias == part.Alias).index;
            var atom = _atoms[index];
            return part.Start == 0 && part.End == atom.Text.Length
                ? new LocatorPart($"A{index}")
                : new LocatorPart($"A{index}", Boundary(index, part.Start), Boundary(index, part.End));
        }

        private SemanticSourcePart DecodePart(LocatorPart part)
        {
            if (!_byHandle.TryGetValue(part.Atom, out var index)) throw new InvalidOperationException("locator-atom-handle-unknown");
            var atom = _atoms[index];
            if (part.From is null && part.To is null) return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias);
            if (part.From is null || part.To is null) throw new InvalidOperationException("locator-boundary-pair-incomplete");
            var from = ParseBoundary(index, part.From, atom.Text.Length);
            var to = ParseBoundary(index, part.To, atom.Text.Length);
            if (from >= to) throw new InvalidOperationException("locator-boundary-order-invalid");
            if (from == 0 && to == atom.Text.Length) throw new InvalidOperationException("locator-whole-atom-must-omit-boundaries");
            var selection = atom.Text[from..to];
            var occurrence = OccurrenceAt(atom.Text, selection, from);
            return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, selection, occurrence);
        }

        private static string Boundary(int atomIndex, int offset) => $"A{atomIndex}:B{offset}";

        private static int ParseBoundary(int atomIndex, string handle, int length)
        {
            var prefix = $"A{atomIndex}:B";
            if (!handle.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(handle[prefix.Length..], out var offset) || offset < 0 || offset > length)
                throw new InvalidOperationException("locator-boundary-handle-invalid");
            return offset;
        }

        private static int OccurrenceAt(string source, string selection, int start)
        {
            var occurrence = 0;
            for (var at = source.IndexOf(selection, StringComparison.Ordinal); at >= 0; at = source.IndexOf(selection, at + 1))
            {
                occurrence++;
                if (at == start) return occurrence;
            }
            throw new InvalidOperationException("locator-boundary-does-not-resolve-to-selection");
        }

        private static void ValidateFunctions(IReadOnlyList<string> functions)
        {
            var allowed = new HashSet<string>(["DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"], StringComparer.Ordinal);
            if (functions is null || functions.Count == 0 || functions.Any(function => !allowed.Contains(function)) || functions.Distinct(StringComparer.Ordinal).Count() != functions.Count)
                throw new InvalidOperationException("locator-functions-invalid");
        }
    }

    private sealed record SingletonBoundRow(string DocumentId, int ParentOrdinal, int OwnedCount, int LocatorPartCount, int ResponseUtf8Bytes);
}
