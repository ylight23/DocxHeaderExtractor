using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5C1: provider-free representability census over the immutable 31-pack raw cohort. This is a wire
/// shape audit only: it does not read Gold, replay graph/projection, or call a provider.
/// </summary>
public sealed class V5RepresentabilityAuditTests
{
    private const string CohortPath = "artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json";
    private const string CallsRoot = "artifacts/v5-provider-cohort-31-windows/provider/calls";
    private const string SummaryPath = "artifacts/v5-representability-audit/summary.v1.json";

    [Fact]
    public void Frozen_31_pack_subject_and_relation_shapes_fit_owned_decision_v3()
    {
        using var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CohortPath)));
        var totals = new Census();

        foreach (var row in cohort.RootElement.GetProperty("rows").EnumerateArray())
        {
            var ordinal = row.GetProperty("Ordinal").GetInt32();
            var documentId = row.GetProperty("DocumentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            var packSuffix = packId.Split(':').Last();
            var owned = row.GetProperty("ownedAliases").EnumerateArray().Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var visible = row.GetProperty("visibleAliases").EnumerateArray().Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var path = TestRepository.Path($"{CallsRoot}/{ordinal:00}-{documentId}-{packSuffix}/content.txt");
            using var raw = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var claim in raw.RootElement.GetProperty("claims").EnumerateArray())
            {
                totals.RawProposals++;
                var subjectParts = claim.GetProperty("subject").GetProperty("sourceParts").EnumerateArray().ToArray();
                totals.MaxSubjectParts = Math.Max(totals.MaxSubjectParts, subjectParts.Length);
                if (subjectParts.Length == 1) totals.SubjectSingle++; else totals.SubjectMulti++;
                foreach (var part in subjectParts)
                {
                    if (HasVerbatim(part)) totals.SubjectVerbatimParts++;
                    else totals.SubjectAliasOnlyParts++;
                }
                if (subjectParts.Length == 1)
                {
                    if (HasVerbatim(subjectParts[0])) totals.SubjectSingleVerbatim++;
                    else totals.SubjectSingleAliasOnly++;
                }
                else
                {
                    var aliases = subjectParts.Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray();
                    var allOwned = aliases.All(owned.Contains);
                    var containsHalo = aliases.Any(alias => visible.Contains(alias) && !owned.Contains(alias));
                    if (allOwned) totals.SubjectMultiAllOwned++;
                    else totals.SubjectMultiCrossOwnedBoundary++;
                    if (containsHalo) totals.SubjectMultiContainsHalo++;
                }

                if (!claim.TryGetProperty("object", out var target) || target.ValueKind == JsonValueKind.Null)
                    continue;

                totals.RelationClaims++;
                var objectParts = target.GetProperty("sourceParts").EnumerateArray().ToArray();
                totals.MaxObjectParts = Math.Max(totals.MaxObjectParts, objectParts.Length);
                if (objectParts.Length == 1) totals.ObjectSingle++; else totals.ObjectMulti++;
                foreach (var part in objectParts)
                {
                    if (HasVerbatim(part)) totals.ObjectVerbatimParts++;
                    else totals.ObjectAliasOnlyParts++;
                }

                var objectAliases = objectParts.Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray();
                if (objectAliases.All(owned.Contains)) totals.ObjectOwnedOnly++;
                if (objectAliases.Any(alias => visible.Contains(alias) && !owned.Contains(alias))) totals.ObjectContainsHalo++;
                if (objectAliases.Any(alias => !visible.Contains(alias))) totals.ObjectOutOfVisible++;
            }
        }

        Assert.Equal(1448, totals.RawProposals);
        Assert.Equal(1424, totals.SubjectSingle);
        Assert.Equal(24, totals.SubjectMulti);
        Assert.Equal(6, totals.MaxSubjectParts);
        Assert.Equal(903, totals.SubjectSingleAliasOnly);
        Assert.Equal(521, totals.SubjectSingleVerbatim);
        Assert.Equal(23, totals.SubjectMultiAllOwned);
        Assert.Equal(1, totals.SubjectMultiCrossOwnedBoundary);
        Assert.Equal(1, totals.SubjectMultiContainsHalo);
        Assert.Equal(981, totals.SubjectAliasOnlyParts);
        Assert.Equal(525, totals.SubjectVerbatimParts);

        Assert.Equal(709, totals.RelationClaims);
        Assert.Equal(709, totals.ObjectSingle);
        Assert.Equal(0, totals.ObjectMulti);
        Assert.Equal(1, totals.MaxObjectParts);
        Assert.Equal(672, totals.ObjectOwnedOnly);
        Assert.Equal(37, totals.ObjectContainsHalo);
        Assert.Equal(0, totals.ObjectOutOfVisible);
        Assert.Equal(531, totals.ObjectAliasOnlyParts);
        Assert.Equal(178, totals.ObjectVerbatimParts);

        using var summary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(SummaryPath)));
        var root = summary.RootElement;
        Assert.Equal("P5C1_COMPLETE_PROVIDER_FREE", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal(totals.RawProposals, root.GetProperty("rawProposalCount").GetInt32());
        Assert.Equal(totals.SubjectMulti, root.GetProperty("subject").GetProperty("multiPart").GetInt32());
        Assert.Equal(totals.SubjectMultiAllOwned, root.GetProperty("subject").GetProperty("multiPartAllOwned").GetInt32());
        Assert.Equal(totals.SubjectMultiCrossOwnedBoundary, root.GetProperty("subject").GetProperty("multiPartCrossOwnedBoundary").GetInt32());
        Assert.Equal(totals.ObjectContainsHalo, root.GetProperty("relationObject").GetProperty("containsHalo").GetInt32());
        Assert.Equal(totals.ObjectOutOfVisible, root.GetProperty("relationObject").GetProperty("outOfVisible").GetInt32());
    }

    private static bool HasVerbatim(JsonElement part) =>
        part.TryGetProperty("verbatimText", out var value) &&
        value.ValueKind is not JsonValueKind.Null &&
        value.GetString() is { Length: > 0 };

    private sealed class Census
    {
        public int RawProposals;
        public int SubjectSingle;
        public int SubjectMulti;
        public int SubjectSingleAliasOnly;
        public int SubjectSingleVerbatim;
        public int SubjectMultiAllOwned;
        public int SubjectMultiCrossOwnedBoundary;
        public int SubjectMultiContainsHalo;
        public int SubjectAliasOnlyParts;
        public int SubjectVerbatimParts;
        public int MaxSubjectParts;
        public int RelationClaims;
        public int ObjectSingle;
        public int ObjectMulti;
        public int ObjectOwnedOnly;
        public int ObjectContainsHalo;
        public int ObjectOutOfVisible;
        public int ObjectAliasOnlyParts;
        public int ObjectVerbatimParts;
        public int MaxObjectParts;
    }
}
