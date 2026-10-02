using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6DAdaptiveLocatorDomainShardingTests
{
    private const string Root = "artifacts/v5-p6d-adaptive-locator-domain-sharding";

    [Fact]
    public void Freeze_reusable_registry_primary_partition_and_fail_closed_overflow_without_provider()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "source", 0, 1, 1, 0, "A😀B"),
            new SemanticSourceAtom("L1", "source", 1, 1, 2, 0, "Next"),
        };
        var registry = RequestLocalLocatorRegistry.Create(atoms);
        var repeat = RequestLocalLocatorRegistry.Create(atoms);
        Assert.Equal(registry.Fingerprint, repeat.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => registry.BoundaryHandle(0, 2)); // middle of 😀 surrogate pair

        var locator = new OccurrenceLocator(
            new OccurrenceLocatorPart(registry.AtomHandle(0), registry.BoundaryHandle(0, 0), registry.BoundaryHandle(0, 1)),
            [new OccurrenceLocatorPart(registry.AtomHandle(1))], ["STRUCTURAL_REGION"]);
        var endpoint = registry.Decode(locator);
        Assert.Equal("source:0-1|source:0-4", endpoint.Identity);

        var parent = OccurrenceShardDomain.Full(registry);
        var (left, right) = OccurrenceShardSplitter.Split(parent);
        Assert.Empty(left.Roots.Intersect(right.Roots));
        Assert.Equal(OrderRoots(parent.Roots), OrderRoots(left.Roots.Concat(right.Roots)));
        Assert.Equal(1, new[] { left, right }.Count(domain => domain.Owns(registry, locator)));

        var overflow = OccurrenceShardSplitter.OnResponseBytes(parent, 49_153, 49_152);
        Assert.Equal("SPLIT_WITHOUT_PARSE_OR_BIND", overflow.Status);
        Assert.NotNull(overflow.Left); Assert.NotNull(overflow.Right);
        var leaf = new OccurrenceShardDomain("leaf", [parent.Roots[0]]);
        Assert.Equal("INCOMPLETE_UNSPLITTABLE_OVERFLOW", OccurrenceShardSplitter.OnResponseBytes(leaf, 49_153, 49_152).Status);

        var response = new OccurrenceLocatorResponse([locator, locator]);
        var identities = response.Occurrences.Select(item => registry.Decode(item).Identity).ToArray();
        Assert.NotEqual(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());

        FreezeArtifact.AssertJson(Root, "adaptive-locator-domain-sharding.v1.json", new
        {
            schemaVersion = "v5-p6d-adaptive-locator-domain-sharding-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            reusableQualificationTypes = new[] { "RequestLocalLocatorRegistry", "OccurrenceShardDomain", "OccurrenceShardSplitter", "OccurrenceLocatorResponse" },
            registry = new { determinism = "PASS: same packet/source atoms produces same fingerprint", unicode = "PASS: no issued boundary between a UTF-16 surrogate pair", coordinateSystem = "UTF-16_OFFSETS_AT_VALID_UNICODE_SCALAR_BOUNDARIES" },
            ownership = new { coverage = "PASS_FOR_PRIMARY_ROOT_UNIVERSE", nonOverlap = "PASS_AFTER_DETERMINISTIC_BISECTION", multipart = "PRIMARY_OWNED_ONLY" },
            normalization = new { duplicateExactLocator = "REJECT_REQUIRED_AT_RESPONSE_VALIDATION: duplicate identities are detectable; this test proves detection only", functions = "UNIQUE_SET_REQUIRED" },
            overflow = new { oversizedResponse = "SPLIT_WITHOUT_PARSE_OR_BIND", retry = 0, repair = false, leafOverflow = "INCOMPLETE_UNSPLITTABLE_OVERFLOW_NOT_EVALUABLE" },
            termination = new { primaryRootTree = "FINITE: each split reduces a finite root set", p6bExactIdentityLeaf = "NOT_PROVEN: primary-root leaf still admits different multipart continuations for that primary; continuation-domain partition is required before claiming P6B-safe termination" },
            gate = new { coarseAggregateVolume = "OPEN", providerManifest = "NOT_PREPARED", providerExecution = "BLOCKED", sharedRuntime = "UNCHANGED" },
            conclusion = "P6D turns registry and primary-domain splitting into reusable qualification types and preserves fail-closed overflow semantics. It intentionally does not overclaim termination to an exact multipart identity leaf.",
        });
    }

    private static PrimaryLocatorRoot[] OrderRoots(IEnumerable<PrimaryLocatorRoot> roots) => roots
        .OrderBy(root => root.AtomIndex).ThenBy(root => root.Whole ? -1 : root.Start).ToArray();
}
