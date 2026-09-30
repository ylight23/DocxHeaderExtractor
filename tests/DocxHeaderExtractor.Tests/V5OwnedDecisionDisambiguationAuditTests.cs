using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5C3a: asks whether historical occurrence/context selectors on claims that actually survived the
/// real batch binder are necessary for source identity. Provider-free and Gold-free.
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
        var selectorParts = 0;
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
            var batch = ExactClaimBinderV2_1.Bind(packId, quarantine.Eligible, atoms, scope);
            boundClaims += batch.Bound.Count;

            var refusedOrdinals = batch.Refusals.Keys
                .Where(key => key.StartsWith("proposal-", StringComparison.Ordinal))
                .Select(key => int.Parse(key[9..], System.Globalization.CultureInfo.InvariantCulture) - 1)
                .ToHashSet();

            foreach (var indexed in quarantine.Eligible)
            {
                if (refusedOrdinals.Contains(indexed.OriginalOrdinal)) continue;

                var proposal = indexed.Proposal;
                var subjectHas = HasDisambiguation(proposal.Subject);
                var objectHas = proposal.Object is not null && HasDisambiguation(proposal.Object);
                if (!subjectHas && !objectHas) continue;

                boundWithDisambiguation++;
                if (subjectHas) subjectSelectorClaims++;
                if (objectHas) objectSelectorClaims++;
                selectorParts += CountDisambiguatedParts(proposal.Subject);
                if (proposal.Object is not null) selectorParts += CountDisambiguatedParts(proposal.Object);

                var originalSubject = Bind(proposal.Subject, atoms);
                var strippedSubject = Bind(Strip(proposal.Subject), atoms);
                var originalObject = proposal.Object is null ? null : Bind(proposal.Object, atoms);
                var strippedObject = proposal.Object is null ? null : Bind(Strip(proposal.Object), atoms);

                var stillBinds =
                    originalSubject.IsBound && strippedSubject.IsBound &&
                    (originalObject is null || (originalObject.IsBound && strippedObject!.IsBound));

                if (!stillBinds)
                {
                    strippingRequired++;
                    continue;
                }

                var identityChanged =
                    !string.Equals(originalSubject.Identity, strippedSubject.Identity, StringComparison.Ordinal) ||
                    !string.Equals(originalObject?.Identity, strippedObject?.Identity, StringComparison.Ordinal);

                if (identityChanged)
                {
                    strippingChangedIdentity++;
                    strippingRequired++;
                    continue;
                }

                strippingRedundant++;
            }
        }

        Assert.Equal(1203, boundClaims);
        Assert.Equal(81, boundWithDisambiguation);
        Assert.Equal(81, subjectSelectorClaims);
        Assert.Equal(43, objectSelectorClaims);
        Assert.Equal(124, selectorParts);
        Assert.Equal(boundWithDisambiguation, strippingRedundant + strippingRequired);
        Assert.Equal(81, strippingRedundant);
        Assert.Equal(0, strippingChangedIdentity);
        Assert.Equal(0, strippingRequired);
    }

    private static SemanticSourcePartsBinding Bind(
        ClaimSourceEndpointV2_1 endpoint,
        IReadOnlyList<SemanticSourceAtom> atoms) =>
        SemanticSourcePartBinder.Bind(atoms, ProviderSourcePartNormalization.ToCanonical(endpoint.SourceParts));

    private static bool HasDisambiguation(ClaimSourceEndpointV2_1 endpoint) =>
        endpoint.SourceParts.Any(part =>
            part.Occurrence is not null ||
            part.LeftExactContext is not null ||
            part.RightExactContext is not null);

    private static int CountDisambiguatedParts(ClaimSourceEndpointV2_1 endpoint) =>
        endpoint.SourceParts.Count(part =>
            part.Occurrence is not null ||
            part.LeftExactContext is not null ||
            part.RightExactContext is not null);

    private static ClaimSourceEndpointV2_1 Strip(ClaimSourceEndpointV2_1 endpoint) =>
        new(endpoint.SourceParts.Select(part => new ProviderSourcePartV2_1(
            part.SourceAlias,
            part.VerbatimText)).ToArray());
}
