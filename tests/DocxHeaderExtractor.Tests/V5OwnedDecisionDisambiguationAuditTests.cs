using System.Globalization;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5C3a: asks whether historical occurrence/context selectors on claims that actually survived the
/// frozen cohort binder were necessary for endpoint identity. Provider-free and Gold-free.
/// Historical acceptance comes only from frozen cohort-result; endpoint counterfactuals use the
/// frozen reviewed atom-text facts rather than replaying today's PDF source universe.
/// </summary>
public sealed class V5OwnedDecisionDisambiguationAuditTests
{
    private const string CohortPath = "artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json";
    private const string ResultPath = "artifacts/v5-provider-cohort-31-windows/cohort-result.v1.json";
    private const string CallsRoot = "artifacts/v5-provider-cohort-31-windows/provider/calls";

    [Fact]
    public void Frozen_bound_claims_do_not_require_selector_metadata_to_preserve_endpoint_identity()
    {
        using var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CohortPath)));
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath)));

        var rows = cohort.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => (row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!),
            row => row);
        var packResults = result.RootElement.GetProperty("packs").EnumerateArray().ToDictionary(
            row => (row.GetProperty("documentId").GetString()!, row.GetProperty("packId").GetString()!),
            row => row);

        var contract = DocumentStructureTaskContract.Create();
        var atomsByDocument = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal)
        {
            ["SRC-089"] = FrozenAtoms("SRC-089"),
            ["SRC-095"] = FrozenAtoms("SRC-095"),
        };

        var historicalBoundClaims = 0;
        var boundWithDisambiguation = 0;
        var subjectSelectorClaims = 0;
        var objectSelectorClaims = 0;
        var selectorParts = 0;
        var strippingRedundant = 0;
        var strippingRequired = 0;
        var strippingChangedIdentity = 0;
        var frozenEndpointReplayFailures = 0;

        foreach (var callDirectory in Directory.GetDirectories(TestRepository.Path(CallsRoot)))
        {
            using var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDirectory, "call.v1.json")));
            var documentId = call.RootElement.GetProperty("documentId").GetString()!;
            var packId = call.RootElement.GetProperty("packId").GetString()!;
            _ = rows[(documentId, packId)]; // proves the frozen request pack exists.
            var atoms = atomsByDocument[documentId];

            var frozenPack = packResults[(documentId, packId)];
            var refusedOrdinals = frozenPack.GetProperty("qualification").GetProperty("refusalReasons")
                .EnumerateArray()
                .Select(item => item.GetProperty("key").GetString()!)
                .Where(key => key.StartsWith("proposal-", StringComparison.Ordinal))
                .Select(key => int.Parse(key[9..], CultureInfo.InvariantCulture) - 1)
                .ToHashSet();

            using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDirectory, "content.txt")));
            var quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(raw.RootElement, contract);
            foreach (var indexed in quarantine.Eligible)
            {
                if (refusedOrdinals.Contains(indexed.OriginalOrdinal)) continue;
                historicalBoundClaims++;

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

                if (!originalSubject.IsBound || (originalObject is not null && !originalObject.IsBound))
                {
                    frozenEndpointReplayFailures++;
                    continue;
                }

                var stillBinds =
                    strippedSubject.IsBound &&
                    (strippedObject is null || strippedObject.IsBound);

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

        Assert.Equal(1203, historicalBoundClaims);
        Assert.Equal(0, frozenEndpointReplayFailures);
        Assert.Equal(81, boundWithDisambiguation);
        Assert.Equal(81, subjectSelectorClaims);
        Assert.Equal(43, objectSelectorClaims);
        Assert.Equal(124, selectorParts);
        Assert.Equal(boundWithDisambiguation, strippingRedundant + strippingRequired);
        Assert.Equal(81, strippingRedundant);
        Assert.Equal(0, strippingChangedIdentity);
        Assert.Equal(0, strippingRequired);
    }

    private static IReadOnlyList<SemanticSourceAtom> FrozenAtoms(string documentId)
    {
        var path = TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{documentId}/atom-glyph-facts.tsv");
        var atoms = new List<SemanticSourceAtom>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = line.Split('\t');
            if (columns.Length < 5) throw new InvalidOperationException($"bad-frozen-atom-row:{documentId}");
            var alias = columns[0];
            var page = int.Parse(columns[1], CultureInfo.InvariantCulture);
            var row = int.Parse(columns[2], CultureInfo.InvariantCulture);
            var segment = int.Parse(columns[3], CultureInfo.InvariantCulture);
            var text = columns[^1];
            atoms.Add(new SemanticSourceAtom(alias, alias, row, page, row, segment, text));
        }
        return atoms;
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
