using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5Y separates the heading-occurrence qualification question from the unqualified relation
/// lane.  It is deliberately a provider-free design gate: no shared runtime or V3.3 execution
/// manifest is created until exact occurrence source representation has a finite proof.
/// </summary>
public sealed class V5P5YHeadingOccurrenceQualificationDesignTests
{
    private const string Root = "artifacts/v5-p5y-heading-occurrence-qualification";

    [Fact]
    public void Freeze_heading_occurrence_qualification_boundary_without_provider_or_gold()
    {
        var contract = DocumentStructureTaskContract.Create();
        var unary = contract.Predicates.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOCUMENT_IDENTITY", "NAVIGATION_REPRESENTATION", "STRUCTURAL_REGION" }, unary);
        Assert.Empty(contract.Relations.Where(item => unary.Contains(item.Name, StringComparer.Ordinal)));

        // The generic contract declares shapes, not a one-of/mutual-exclusion rule.  A future
        // occurrence-only contract must state that rule explicitly instead of deriving it from
        // observed provider output or the heading-only Gold axis.
        var contractProperties = typeof(DocumentTaskContract).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MutuallyExclusivePredicates", contractProperties);
        Assert.DoesNotContain("PredicateCardinality", contractProperties);

        var packet = new V5SemanticDecisionRequestPacketV3(
            Enumerable.Range(0, 96).Select(index =>
            {
                const string text = "Sample";
                var span = new StructuralSpan(0, text.Length);
                return new EvidenceNode($"E{index}", "source", $"O{index}", index, EvidenceModality.TEXT, text,
                    new EvidenceAnchor("source", index, span), new Dictionary<string, string?>());
            }).ToArray(), [], [], [], [], []);
        var composed = V5CompactDecisionComposerV3_3.Compose(contract, packet);
        using var prompt = JsonDocument.Parse(composed.Prompt);
        var responseSchema = prompt.RootElement.GetProperty("responseSchema").GetRawText();

        // V3.3 preserves exact occurrence fidelity by allowing strict substrings and multipart
        // handles. A bare ownedIndex plus predicate flag cannot express either case.
        Assert.Contains("subjectSelection", responseSchema, StringComparison.Ordinal);
        Assert.Contains("additionalSubjectParts", responseSchema, StringComparison.Ordinal);
        Assert.Contains("verbatimText", responseSchema, StringComparison.Ordinal);
        Assert.Equal(6, composed.ResponseBounds.MaxSubjectParts);
        Assert.Equal(318, composed.ResponseBounds.MaxSelectionStringUtf8Bytes);

        FreezeArtifact.AssertJson(Root, "heading-occurrence-qualification-design.v1.json", new
        {
            schemaVersion = "v5-p5y-heading-occurrence-qualification-design-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            scope = new
            {
                axis = "HEADING_OCCURRENCE_QUALIFICATION_ONLY",
                subject = "one owned source occurrence, with source-part fidelity retained",
                permittedUnaryPredicates = unary,
                prohibitedRelations = contract.Relations.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                sharedRuntime = "UNCHANGED",
                relationLane = "NOT_QUALIFIED_NO_GOLD_AXIS_NOT_PROMOTED_TO_RUNTIME",
            },
            multiplicityAuthority = new
            {
                oneOfThree = "NOT_ESTABLISHED: current DocumentTaskContract has no mutual-exclusion declaration.",
                independentFlags = "CANDIDATE_ONLY: a fixed three-flag wire is finite, but needs an explicit task rule and an exact occurrence locator.",
                observedProviderCombinations = "NOT_AUTHORITY_FOR_TASK_SEMANTICS",
            },
            occurrenceFidelity = new
            {
                wholeAtom = "ownedIndex handle is sufficient when the heading is exactly one whole owned atom.",
                strictSubstring = "requires strict verbatimText plus optional occurrence under current V3.3 compact grammar.",
                multipart = "requires primary ownedIndex plus ordered additionalSubjectParts; their selections may be strict substrings.",
                bareOwnedIndexAndFlags = "INSUFFICIENT: cannot represent strict-span or multipart heading identity.",
                currentV33Bounds = new { composed.ResponseBounds.MaxSubjectParts, composed.ResponseBounds.MaxSelectionStringUtf8Bytes },
            },
            qualificationGate = new
            {
                occurrenceOnlyWire = "NOT_IMPLEMENTED",
                frozenProviderManifest = "NOT_PREPARED",
                responseVolumeProof = "NOT_ESTABLISHED: removing relations alone does not prove a finite exact-span/multipart response fits 49,152 bytes.",
                nextRequiredAuthority = "Declare unary multiplicity and design a source-part-preserving occurrence-only response representation; then derive its source-constrained byte bound.",
                providerExecution = "BLOCKED",
            },
            conclusion = "P5Y correctly separates heading qualification from the relation lane, but an ownedIndex-plus-flags response cannot be frozen yet because it would discard the exact source-span and multipart fidelity required by the production-bound scoring axis.",
        });
    }
}
