using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6ECanonicalLocatorContinuationDomainTests
{
    private const string Root = "artifacts/v5-p6e-canonical-locator-continuation-domain";

    [Fact]
    public void Freeze_exact_locator_identity_partition_for_current_v33_representable_universe_without_provider()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "source-0", 0, 1, 1, 0, "AB"),
            new SemanticSourceAtom("L1", "source-1", 1, 1, 2, 0, "CD"),
        };
        var registry = RequestLocalLocatorRegistry.Create(atoms);
        var parts = Enumerable.Range(0, atoms.Length).SelectMany(atom => Parts(registry, atoms[atom], atom)).ToArray();
        var locators = new List<OccurrenceLocator>();
        foreach (var primary in parts.Where(item => item.Atom == "A0"))
        {
            locators.Add(new OccurrenceLocator(primary, [], ["STRUCTURAL_REGION"]));
            foreach (var continuation in parts.Where(item => item.Atom == "A1"))
                locators.Add(new OccurrenceLocator(primary, [continuation], ["STRUCTURAL_REGION"]));
        }
        locators.AddRange(parts.Where(item => item.Atom == "A1").Select(primary => new OccurrenceLocator(primary, [], ["STRUCTURAL_REGION"])));
        var keys = locators.Select(locator => new { Locator = locator, Key = registry.CanonicalKey(locator) })
            .OrderBy(item => item.Key.EndpointIdentity, StringComparer.Ordinal).ToArray();
        Assert.Equal(keys.Length, keys.Select(item => item.Key).Distinct().Count());

        var leaves = Split(keys.Select(item => item.Key).ToArray());
        Assert.All(leaves, leaf => Assert.Single(leaf));
        Assert.Equal(keys.Select(item => item.Key).OrderBy(key => key.EndpointIdentity), leaves.SelectMany(item => item).OrderBy(key => key.EndpointIdentity));
        var maxBytes = keys.Max(item => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { occurrences = new[] { item.Locator } })));
        Assert.True(maxBytes < 49_152);

        FreezeArtifact.AssertJson(Root, "canonical-locator-continuation-domain.v1.json", new
        {
            schemaVersion = "v5-p6e-canonical-locator-continuation-domain-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            canonicalKey = "RequestLocalLocatorRegistry.CanonicalKey(locator) = decoded BoundClaimEndpoint.Identity",
            refinementOrder = new[] { "primary-root", "primary-end", "multipart-part-count", "additional-atom", "additional-start", "additional-end" },
            representationUniverse = new { maxSourceParts = V5SemanticDecisionResponseBoundsV3.HistoricalMaxSourceParts, meaning = "FINITE_OVER_CURRENT_V3_3_REPRESENTABLE_UNIVERSE_NOT_A_UNIVERSAL_HEADING_SEMANTIC_LIMIT" },
            proofFixture = new { locators = keys.Length, exactCoverage = true, overlap = 0, terminalLeaves = leaves.Count, terminalIdentity = "ONE_BoundClaimEndpoint.Identity", maxSingleOccurrenceResponseUtf8Bytes = maxBytes },
            terminalBound = "P6B singleton response bound applies once the full locator tuple is singleton.",
            gate = new { continuationTermination = "PROVEN_FOR_FINITE_CURRENT_REPRESENTABLE_LOCATOR_UNIVERSE", practicalCoarseSchedule = "NOT_EVALUATED", providerManifest = "BLOCKED", sharedRuntime = "UNCHANGED" },
        });
    }

    private static IEnumerable<OccurrenceLocatorPart> Parts(RequestLocalLocatorRegistry registry, SemanticSourceAtom atom, int index)
    {
        yield return new OccurrenceLocatorPart(registry.AtomHandle(index));
        for (var from = 0; from < atom.Text.Length; from++)
        for (var to = from + 1; to <= atom.Text.Length; to++)
            if (!(from == 0 && to == atom.Text.Length)) yield return new OccurrenceLocatorPart(registry.AtomHandle(index), registry.BoundaryHandle(index, from), registry.BoundaryHandle(index, to));
    }

    private static List<IReadOnlyList<CanonicalLocatorKey>> Split(IReadOnlyList<CanonicalLocatorKey> keys)
    {
        if (keys.Count <= 1) return [keys];
        var midpoint = keys.Count / 2;
        var result = Split(keys.Take(midpoint).ToArray());
        result.AddRange(Split(keys.Skip(midpoint).ToArray()));
        return result;
    }
}
