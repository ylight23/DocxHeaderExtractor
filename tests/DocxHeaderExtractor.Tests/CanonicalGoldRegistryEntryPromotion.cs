using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Controlled single-authority promotion of one consumer Gold file into <c>gold-current/registry.v1.json</c>:
/// <code>
/// authority's consumer Gold  ->  canonical hash  ->  replace registry[authorityId] only  ->  every other row unchanged
/// </code>
/// It never rebuilds the cohort. <see cref="CanonicalGoldConsolidationTests"/> regenerates the whole registry and every
/// consumer file, which would also rewrite the authorities whose Gold is not yet migrated to a changed source universe.
/// This reads the registry and the one consumer file, recomputes that one row from the file, and fails closed if any
/// other row would change. No provider or model call.
/// </summary>
public static class CanonicalGoldRegistryEntryPromotion
{
    public const string EnvironmentVariable = "A99_PROMOTE_GOLD_REGISTRY_ENTRY";

    /// <summary>Returns the authority ids whose row changed; it is exactly <c>[authorityId]</c> or empty.</summary>
    public static IReadOnlyList<string> Promote(string authorityId, string registryPath, string consumerGoldPath, bool write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityId);
        var registryText = File.ReadAllText(registryPath);
        var registry = JsonNode.Parse(registryText)!.AsObject();
        var rows = registry["authorities"]!.AsArray();

        // The untouched registry must round-trip through the freeze serializer, or "other rows unchanged" means nothing.
        var roundTrip = JsonSerializer.Serialize(registry, FreezeArtifact.Json).ReplaceLineEndings("\n");
        if (roundTrip != registryText.ReplaceLineEndings("\n").TrimEnd('\n'))
            throw new InvalidOperationException("The registry does not round-trip through the freeze serializer; refusing to rewrite it.");

        var before = rows.Select(row => row!.ToJsonString()).ToArray();
        var index = Array.FindIndex(rows.ToArray(), row => row!["authorityId"]!.GetValue<string>() == authorityId);
        if (index < 0) throw new InvalidOperationException($"{authorityId} is not in the registry.");

        using var gold = JsonDocument.Parse(File.ReadAllText(consumerGoldPath));
        var root = gold.RootElement;
        if (root.GetProperty("authorityId").GetString() != authorityId)
            throw new InvalidOperationException($"{consumerGoldPath} is not the Gold of {authorityId}.");
        if (root.GetProperty("providerCalls").GetInt32() != 0 || root.GetProperty("modelCalls").GetInt32() != 0)
            throw new InvalidOperationException("A consumer Gold file must record zero provider and model calls.");

        var capabilities = root.GetProperty("capabilities");
        var semantic = root.GetProperty("semantic");
        var total = semantic.GetProperty("semanticHeadingTotal").GetInt32();
        var materialized = semantic.GetProperty("materializedSemanticClaims").GetInt32();
        var row = rows[index]!.AsObject();
        row["sourceSha256"] = root.GetProperty("source").GetProperty("sourceSha256").GetString();
        row["semanticHeadingTotal"] = total;
        row["materializedSemanticClaims"] = materialized;
        row["semanticCountAuthoritative"] = capabilities.GetProperty("semanticCountAuthoritative").GetBoolean();
        row["semanticClaimsEvaluable"] = materialized == total && total > 0;
        row["occurrenceEvaluable"] = capabilities.GetProperty("occurrenceEvaluable").GetBoolean();
        row["characterSpanEvaluable"] = capabilities.GetProperty("characterSpanEvaluable").GetBoolean();
        row["visualBindingEvaluable"] = capabilities.GetProperty("visualBindingEvaluable").GetBoolean();
        row["goldSha256"] = CanonicalArtifactHash.OfTextFile(consumerGoldPath);

        var after = rows.Select(item => item!.ToJsonString()).ToArray();
        var changed = rows.Where((_, i) => before[i] != after[i])
            .Select(item => item!["authorityId"]!.GetValue<string>()).ToArray();
        if (changed.Length > 1 || (changed.Length == 1 && changed[0] != authorityId))
            throw new InvalidOperationException($"Promotion of {authorityId} would change {string.Join(", ", changed)}.");

        if (write && changed.Length == 1)
            File.WriteAllBytes(registryPath, new UTF8Encoding(false).GetBytes(
                JsonSerializer.Serialize(registry, FreezeArtifact.Json).ReplaceLineEndings("\n") + "\n"));
        return changed;
    }
}

public sealed class CanonicalGoldRegistryEntryPromotionTests
{
    private const string Registry = "eval/a99-closed-loop/gold-current/registry.v1.json";

    [Fact]
    public void Promote_the_requested_authority_only()
    {
        var id = Environment.GetEnvironmentVariable(CanonicalGoldRegistryEntryPromotion.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(id)) return;
        var registryPath = TestRepository.Path(Registry);
        var untouched = ReadRows(registryPath);
        var changed = CanonicalGoldRegistryEntryPromotion.Promote(
            id, registryPath, TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{id}.gold.v1.json"), write: true);
        Assert.Equal([id], changed);
        var now = ReadRows(registryPath);
        Assert.All(untouched.Keys.Where(key => key != id), key => Assert.Equal(untouched[key], now[key]));
    }

    [Fact]
    public void A_promotion_changes_only_its_own_row_and_refuses_anything_else()
    {
        var temp = Path.Combine(Path.GetTempPath(), "a99-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var registry = Path.Combine(temp, "registry.v1.json");
            File.Copy(TestRepository.Path(Registry), registry);
            var gold = TestRepository.Path("eval/a99-closed-loop/gold-current/documents/SRC-029.gold.v1.json");

            // The committed registry is current for SRC-029: promoting it changes nothing.
            Assert.Empty(CanonicalGoldRegistryEntryPromotion.Promote("SRC-029", registry, gold, write: false));

            // A consumer file of one authority cannot be promoted under another id.
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalGoldRegistryEntryPromotion.Promote("SRC-089", registry, gold, write: false));

            // A stale row for SRC-089 is the only thing promotion may rewrite, and the rest stays byte for byte.
            var node = JsonNode.Parse(File.ReadAllText(registry))!.AsObject();
            var target = node["authorities"]!.AsArray().Single(r => r!["authorityId"]!.GetValue<string>() == "SRC-089")!.AsObject();
            target["goldSha256"] = new string('0', 64);
            File.WriteAllText(registry, JsonSerializer.Serialize(node, FreezeArtifact.Json).ReplaceLineEndings("\n") + "\n");
            var others = ReadRows(registry).Where(p => p.Key != "SRC-089").ToDictionary(p => p.Key, p => p.Value);
            var changed = CanonicalGoldRegistryEntryPromotion.Promote(
                "SRC-089", registry, TestRepository.Path("eval/a99-closed-loop/gold-current/documents/SRC-089.gold.v1.json"), write: true);
            Assert.Equal(["SRC-089"], changed);
            var now = ReadRows(registry);
            Assert.All(others, pair => Assert.Equal(pair.Value, now[pair.Key]));
            Assert.Equal(
                CanonicalArtifactHash.OfTextFile(TestRepository.Path("eval/a99-closed-loop/gold-current/documents/SRC-089.gold.v1.json")),
                JsonNode.Parse(now["SRC-089"])!["goldSha256"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static Dictionary<string, string> ReadRows(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["authorities"]!.AsArray()
            .ToDictionary(row => row!["authorityId"]!.GetValue<string>(), row => row!.ToJsonString());
}
