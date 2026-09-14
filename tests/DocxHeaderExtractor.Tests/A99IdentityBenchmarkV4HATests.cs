using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4HATests
{
    private const string ArtifactRoot = "artifacts/identity-benchmark/v4/semantic-adjudication";
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), file)));

    [Fact]
    public void Pack_contains_all_128_exactly_bound_source_pairs()
    {
        using var manifest = Load("manifest.json");
        var m = manifest.RootElement;
        Assert.Equal("READY_FOR_V4H_BLINDED_HUMAN_ADJUDICATION", m.GetProperty("status").GetString());
        Assert.Equal(128, m.GetProperty("frozenCandidates").GetInt32());
        Assert.Equal(128, m.GetProperty("included").GetInt32());
        Assert.Equal(0, m.GetProperty("excluded").GetInt32());
        Assert.Equal("128/128", m.GetProperty("exactSourceBindings").GetString());

        using var items = Load("review-item-manifest.json");
        var rows = items.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(128, rows.Length);
        Assert.Equal(128, rows.Select(x => x.GetProperty("reviewId").GetString()).Distinct().Count());
        Assert.Equal(128, rows.Select(x => x.GetProperty("candidateId").GetString()).Distinct().Count());
        Assert.All(rows, x => Assert.StartsWith("SA-", x.GetProperty("reviewId").GetString()));
    }

    [Fact]
    public void Blinding_firewall_has_no_model_or_gold_reads()
    {
        using var firewall = Load("blinding-firewall.json");
        var f = firewall.RootElement;
        Assert.Equal(0, f.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, f.GetProperty("providerResponseReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("modelPredictionReadCount").GetInt32());
        Assert.Equal(0, f.GetProperty("existingGoldReadCount").GetInt32());
        Assert.True(f.GetProperty("all128Included").GetBoolean());
        Assert.False(f.GetProperty("candidateRankVisible").GetBoolean());
        Assert.False(f.GetProperty("retrievalReasonsVisible").GetBoolean());
    }

    [Fact]
    public void Review_form_exposes_only_human_adjudication_fields()
    {
        using var form = Load("review-form.json");
        var f = form.RootElement;
        var fields = f.GetProperty("fields").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "relation", "confidence", "evidenceNote", "needsMoreSourceInspection", "adjudicator" }, fields);
        var rows = f.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(128, rows.Length);
        Assert.All(rows, x => Assert.Equal(JsonValueKind.Null, x.GetProperty("relation").ValueKind));
        Assert.All(rows, x => Assert.Equal(JsonValueKind.Null, x.GetProperty("confidence").ValueKind));
    }

    [Fact]
    public void Review_item_manifest_is_neutral_and_deterministically_hashable()
    {
        using var manifest = Load("review-item-manifest.json");
        var json = File.ReadAllText(Path.Combine(Root(), ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), "review-item-manifest.json"));
        Assert.DoesNotContain("packetClass", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stableRank", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reasons", json, StringComparison.Ordinal);
        Assert.Equal("SHA256(candidateId|orderSeed) ASC", manifest.RootElement.GetProperty("canonicalOrdering").GetString());
        Assert.Equal(128, manifest.RootElement.GetProperty("items").GetArrayLength());
    }
}
