using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Gold-stratifies only the conflict inventory already frozen from raw G2A/H2 outputs.</summary>
public sealed class V5P6TH3PopulationConflictGoldStratificationTests
{
    private const string RegistryPath = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string InventoryPath = "artifacts/v5-p6t-function-membership/p6th3-population-conflict-inventory/population-conflict-inventory.v1.json";
    private const string ArtifactRoot = "artifacts/v5-p6t-function-membership/p6th3-population-conflict-inventory";

    [Fact]
    public void Frozen_raw_conflicts_are_stratified_against_canonical_Gold_without_changing_inventory()
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(InventoryPath)));
        Assert.Equal("FROZEN_RAW_ONLY_BEFORE_GOLD_STRATIFICATION",
            inventory.RootElement.GetProperty("status").GetString());
        Assert.False(inventory.RootElement.GetProperty("goldRead").GetBoolean());
        var frozenConflicts = inventory.RootElement.GetProperty("conflicts").EnumerateArray().ToArray();
        Assert.Equal(2, frozenConflicts.Length);

        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RegistryPath)));
        var rows = new List<object>();
        foreach (var conflict in frozenConflicts)
        {
            var documentId = conflict.GetProperty("documentId").GetString()!;
            var left = conflict.GetProperty("left").GetString()!;
            var right = conflict.GetProperty("right").GetString()!;
            var (leftAlias, rightAlias) = documentId == "SRC-041"
                ? ReadSrc041Aliases(left, right)
                : ReadSrc089Aliases(left, right);

            var authority = registry.RootElement.GetProperty("authorities").EnumerateArray()
                .Single(item => item.GetProperty("authorityId").GetString() == documentId);
            var goldPath = authority.GetProperty("canonicalGoldPath").GetString()!;
            var goldBytes = File.ReadAllBytes(TestRepository.Path(goldPath));
            var goldSha = Hash(goldBytes);
            Assert.Equal(authority.GetProperty("goldSha256").GetString(), goldSha);
            using var gold = JsonDocument.Parse(goldBytes);
            var claims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray())
                .ToArray();

            var sharedClaim = claims.SingleOrDefault(parts =>
            {
                var leftIndex = Array.IndexOf(parts, leftAlias);
                return leftIndex >= 0 && leftIndex + 1 < parts.Length && parts[leftIndex + 1] == rightAlias;
            });
            var rightStartsGoldClaim = claims.Any(parts => parts.Length > 0 && parts[0] == rightAlias);
            var classification = sharedClaim is not null
                ? "CONTINUES_LEFT_STRUCTURAL_UNIT"
                : rightStartsGoldClaim ? "NEW_STRUCTURAL_UNIT" : "AMBIGUOUS_OR_GOLD_AUTHORITY_GAP";

            rows.Add(new
            {
                documentId,
                anchor = conflict.GetProperty("anchor").GetString(),
                leftOccurrence = left,
                leftAlias,
                rightOccurrence = right,
                rightAlias,
                h2Boundary = conflict.GetProperty("h2Boundary").GetString(),
                g2aRight = conflict.GetProperty("g2aRight").GetString(),
                goldClassification = classification,
                exactGoldMultipart = sharedClaim,
                canonicalGoldPath = goldPath,
                canonicalGoldSha256 = goldSha,
                sourceSha256 = gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString(),
            });
        }

        Assert.Equal(1, rows.Count(row => JsonSerializer.Serialize(row).Contains("CONTINUES_LEFT_STRUCTURAL_UNIT", StringComparison.Ordinal)));
        Assert.Equal(1, rows.Count(row => JsonSerializer.Serialize(row).Contains("NEW_STRUCTURAL_UNIT", StringComparison.Ordinal)));
        Assert.DoesNotContain(rows, row => JsonSerializer.Serialize(row).Contains("AMBIGUOUS_OR_GOLD_AUTHORITY_GAP", StringComparison.Ordinal));

        FreezeArtifact.AssertJson(ArtifactRoot, "population-conflict-gold-stratification.v1.json", new
        {
            schemaVersion = "v5-p6th3-conflict-gold-stratification-v1",
            status = "FROZEN_PROVIDER_FREE_GOLD_STRATIFICATION",
            inventoryPath = InventoryPath,
            inventoryConflictCount = frozenConflicts.Length,
            goldRead = true,
            goldMutation = "NONE",
            inventoryMutation = "NONE",
            providerCallsDuringAudit = 0,
            runtimeChanged = false,
            counts = new
            {
                newStructuralUnit = rows.Count(row => JsonSerializer.Serialize(row).Contains("NEW_STRUCTURAL_UNIT", StringComparison.Ordinal)),
                continuesLeftStructuralUnit = rows.Count(row => JsonSerializer.Serialize(row).Contains("CONTINUES_LEFT_STRUCTURAL_UNIT", StringComparison.Ordinal)),
                ambiguousOrAuthorityGap = rows.Count(row => JsonSerializer.Serialize(row).Contains("AMBIGUOUS_OR_GOLD_AUTHORITY_GAP", StringComparison.Ordinal)),
            },
            rows,
            conclusion = "THE_TWO_FROZEN_CHALLENGE_CONFLICTS_HAVE_OPPOSITE_GOLD_LABELS; AVAILABLE_CAPTURE_SET_IS_TOO_SMALL_AND_TARGETED_FOR_POPULATION_OR_H3_QUALIFICATION_CLAIMS",
        });
    }

    private static (string Left, string Right) ReadSrc041Aliases(string left, string right)
    {
        var path = TestRepository.Path("artifacts/v5-p6t-function-membership/p6te-src041-e-challenge/g2a.raw-capture.v1.json");
        using var raw = JsonDocument.Parse(File.ReadAllText(path));
        var decisions = raw.RootElement.GetProperty("parsed").GetProperty("decisions").EnumerateArray().ToArray();
        return (AliasFor(decisions, left), AliasFor(decisions, right));
    }

    private static (string Left, string Right) ReadSrc089Aliases(string left, string right)
    {
        var path = TestRepository.Path("artifacts/v5-p6t-function-membership/p6tg2a-anchor-existence-preflight/anchor-existence-preflight.v1.json");
        using var preflight = JsonDocument.Parse(File.ReadAllText(path));
        var rows = preflight.RootElement.GetProperty("callPlan").GetProperty("primaryOccurrences").EnumerateArray().ToArray();
        return (AliasFor(rows, left), AliasFor(rows, right));
    }

    private static string AliasFor(JsonElement[] decisions, string occurrence) => decisions
        .Single(item =>
        {
            var key = item.TryGetProperty("primary", out _) ? "primary" : "occurrence";
            return item.GetProperty(key).GetString() == occurrence;
        })
        .GetProperty("alias").GetString()!;

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
