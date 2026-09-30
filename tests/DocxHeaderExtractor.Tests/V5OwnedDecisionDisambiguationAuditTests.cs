using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5C3a: asks whether the historical occurrence/context selectors on claims that actually bound
/// are redundant. Provider-free and Gold-free. A failure of the zero-required hypothesis is a
/// design finding: V3 must preserve disambiguation rather than silently simplifying it away.
/// </summary>
public sealed class V5OwnedDecisionDisambiguationAuditTests
{
    private const string CohortPath = "artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json";
    private const string CallsRoot = "artifacts/v5-provider-cohort-31-windows/provider/calls";

    [Fact]
    public void Bound_claims_do_not_require_model_supplied_disambiguation_after_alias_and_verbatim_are_fixed()
    {
        using var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CohortPath)));
        var rows = cohort.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => (row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!),
            row => row);

        var contract = DocumentStructureTaskContract.Create();
        var atomsByDocument = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> Atoms(string documentId)
        {
            if (!atomsByDocument.TryGetValue(documentId, out var atoms))
            {
                var relative = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(relative));
                atomsByDocument[documentId] = atoms;
            }
            return atoms;
        }

        var boundClaims = 0;
        var boundWithDisambiguation = 0;
        var subjectSelectorClaims = 0;
        var objectSelectorClaims = 0;
        var strippingRedundant = 0;
        var strippingRequired = 0;
        var strippingChangedIdentity = 0;

        foreach (var callDirectory in Directory.GetDirectories(TestRepository.Path(CallsRoot)))
        {
            using var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDirectory, "call.v1.json")));
            var documentId = call.RootElement.GetProperty("documentId").GetString()!;
            var packId = call.RootElement.GetProperty("packId").GetString()!;
            var row = rows[(documentId, packId)];
            var owned = row.GetProperty("ownedAliases").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var visible = row.GetProperty("visibleAliases").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var scope = ClaimBindingScope.Create(owned, visible);
            var atoms = Atoms(documentId);

            using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDirectory, "content.txt")));
            var quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(raw.RootElement, contract);
            foreach (var indexed in quarantine.Eligible)
            {
                var original = ExactClaimBinderV2_1.Bind(packId, [indexed], atoms, scope);
                if (original.Bound.Count != 1) continue;
                boundClaims++;

                var proposal = indexed.Proposal;
                var subjectHas = HasDisambiguation(proposal.Subject);
                var objectHas = proposal.Object is not null && HasDisambiguation(proposal.Object);
                if (!subjectHas && !objectHas) continue;

                boundWithDisambiguation++;
                if (subjectHas) subjectSelectorClaims++;
                if (objectHas) objectSelectorClaims++;

                var strippedProposal = proposal with
                {
                    Subject = Strip(proposal.Subject),
                    Object = proposal.Object is null ? null : Strip(proposal.Object),
                };
                var stripped = ExactClaimBinderV2_1.Bind(
                    packId,
                    [new IndexedSemanticClaimProposalV2_1(indexed.OriginalOrdinal, strippedProposal)],
                    atoms,
                    scope);

                if (stripped.Bound.Count != 1)
                {
                    strippingRequired++;
                    continue;
                }

                var before = original.Bound[0].Claim;
                var after = stripped.Bound[0].Claim;
                if (!string.Equals(before.Subject.Identity, after.Subject.Identity, StringComparison.Ordinal) ||
                    !string.Equals(before.Object?.Identity, after.Object?.Identity, StringComparison.Ordinal))
                {
                    strippingChangedIdentity++;
                    strippingRequired++;
                    continue;
                }

                strippingRedundant++;
            }
        }

        Assert.Equal(1203, boundClaims);
        Assert.True(boundWithDisambiguation > 0);
        Assert.Equal(boundWithDisambiguation, strippingRedundant + strippingRequired);
        Assert.Equal(0, strippingChangedIdentity);

        // P5C simplification hypothesis. If this fails, the actual count is the evidence that V3
        // must retain a selector/disambiguation representation before any provider promotion.
        Assert.Equal(0, strippingRequired);
    }

    private static bool HasDisambiguation(ClaimSourceEndpointV2_1 endpoint) =>
        endpoint.SourceParts.Any(part =>
            part.Occurrence is not null ||
            part.LeftExactContext is not null ||
            part.RightExactContext is not null);

    private static ClaimSourceEndpointV2_1 Strip(ClaimSourceEndpointV2_1 endpoint) =>
        new(endpoint.SourceParts.Select(part => new ProviderSourcePartV2_1(
            part.SourceAlias,
            part.VerbatimText)).ToArray());
}
