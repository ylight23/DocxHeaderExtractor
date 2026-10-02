using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5Z establishes what the source-binding representation can prove about occurrences rooted at
/// one owned atom.  It does not turn a historical Gold observation into a task cardinality rule.
/// </summary>
public sealed class V5P5ZRootedOccurrenceMultiplicityAuthorityTests
{
    private const string Root = "artifacts/v5-p5z-rooted-occurrence-multiplicity";

    [Fact]
    public void Freeze_rooted_occurrence_multiplicity_authority_without_provider()
    {
        var atom = new SemanticSourceAtom("O0", "source", 0, 1, 1, 1, "First Heading; Second Heading");
        var first = SemanticSourcePartBinder.Bind([atom], new SemanticSourcePartsProposal([
            new SemanticSourcePart("O0", CanonicalSemanticSelectionMode.VerbatimText, "First Heading")
        ]));
        var second = SemanticSourcePartBinder.Bind([atom], new SemanticSourcePartsProposal([
            new SemanticSourcePart("O0", CanonicalSemanticSelectionMode.VerbatimText, "Second Heading")
        ]));
        Assert.True(first.IsBound);
        Assert.True(second.IsBound);
        Assert.NotEqual(first.Identity, second.Identity);

        // Bound claim identity preserves both separately.  The materialized projection has no
        // root-level uniqueness check that would collapse the two source-backed occurrences.
        var firstEndpoint = new BoundClaimEndpoint(first.Parts);
        var secondEndpoint = new BoundClaimEndpoint(second.Parts);
        var firstClaim = new BoundSemanticClaim("proposal-1", firstEndpoint, "STRUCTURAL_REGION", "heading", null, ClaimResolutionState.RESOLVED, []);
        var secondClaim = new BoundSemanticClaim("proposal-2", secondEndpoint, "STRUCTURAL_REGION", "heading", null, ClaimResolutionState.RESOLVED, []);
        Assert.NotEqual(firstClaim.Identity, secondClaim.Identity);

        var gold = new[] { GoldPrimaryStats("SRC-089"), GoldPrimaryStats("SRC-095") };
        Assert.Equal(139, gold.Sum(item => item.HeadingCount));
        Assert.All(gold, item => Assert.Equal(0, item.PrimaryAliasCollisions));
        Assert.Equal(24, gold.Sum(item => item.MultipartCount));

        FreezeArtifact.AssertJson(Root, "rooted-occurrence-multiplicity.v1.json", new
        {
            schemaVersion = "v5-p5z-rooted-occurrence-multiplicity-v1",
            providerCalls = 0, goldRead = true, goldMutation = "NONE",
            ownedDecisionSemantics = new
            {
                currentMeaning = "one primary owned source atom selected by ownedIndex; it is not a task-declared one-heading occurrence root.",
                sourceBinder = "permits distinct non-overlapping strict substrings of the same atom as distinct bound source identities.",
                materializedIdentity = "BoundSemanticClaim.Identity retains distinct source spans, even when their primary source alias is the same.",
                taskCardinality = "NOT_DEFINED: no DocumentTaskContract rule limits distinct heading occurrences rooted at an ownedIndex.",
            },
            mechanicalCounterexample = new
            {
                sourceAtom = atom.Text,
                primaryOwnedAlias = atom.Alias,
                first = new { identity = first.Identity, text = first.Parts.Single().Text },
                second = new { identity = second.Identity, text = second.Parts.Single().Text },
                result = "TWO_DISTINCT_EXACT_OCCURRENCES_SAME_PRIMARY_ALIAS_ACCEPTED",
            },
            canonicalGoldObservation = gold,
            interpretation = new
            {
                observedPrimaryAliasCollisions = "NONE_IN_THE_139_HEADING_SRC-089/SRC-095_GOLD_COHORT",
                observedMultipart = "24_GOLD_HEADINGS_USE_MORE_THAN_ONE_SOURCE_PART",
                authorityLimit = "OBSERVATION_ONLY: the absence of collisions in this cohort is not a universal at-most-one-rooted-occurrence task rule.",
            },
            occurrenceOnlyWire = new
            {
                oneRecordPerOwnedIndex = "NOT_AUTHORIZED: it would reject a mechanically valid distinct strict-substring occurrence before a task rule establishes that outcome.",
                occurrencesArray = "REQUIRES_A_TASK_DEFINED_PER_ROOT_BOUND; none exists today.",
                semanticFunctions = "may be a unique subset of the three unary predicates per exact occurrence, but does not supply a root-occurrence cardinality bound.",
            },
            qualificationGate = new
            {
                occurrenceMultiplicityAuthority = "NOT_ESTABLISHED",
                finiteResponseProof = "BLOCKED: source fidelity is retained, but the number of exact occurrences rooted at one ownedIndex has no task-derived finite maximum.",
                providerManifest = "NOT_PREPARED",
                providerExecution = "BLOCKED",
                sharedRuntime = "UNCHANGED",
            },
            conclusion = "P5Z preserves the P5Y separation but cannot freeze one occurrence record per ownedIndex. The current binder and materialized identity deliberately support multiple distinct exact spans in one atom; only an explicit task semantic rule can narrow that space.",
        });
    }

    private static GoldStats GoldPrimaryStats(string documentId)
    {
        CanonicalGoldRegistry.RequireCapability(documentId, GoldCapability.Occurrence);
        CanonicalGoldRegistry.RequireCapability(documentId, GoldCapability.CharacterSpan);
        using var document = CanonicalGoldRegistry.Resolve(documentId);
        var claims = document.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().ToArray();
        var primaries = claims.Select(claim => claim.GetProperty("boundParts")[0].GetProperty("sourceAlias").GetString()!).ToArray();
        return new GoldStats(documentId, claims.Length,
            primaries.GroupBy(alias => alias, StringComparer.Ordinal).Count(group => group.Count() > 1),
            primaries.GroupBy(alias => alias, StringComparer.Ordinal).Max(group => group.Count()),
            claims.Count(claim => claim.GetProperty("boundParts").GetArrayLength() > 1));
    }

    private sealed record GoldStats(string DocumentId, int HeadingCount, int PrimaryAliasCollisions,
        int MaxHeadingsPerObservedPrimaryAlias, int MultipartCount);
}
