using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Global exact-once ownership over one document's full source universe.</summary>
public sealed record V5CohortOwnershipProof(
    int TotalAliases,
    int OwnedExactlyOnce,
    int UnownedAliases,
    int MultiplyOwnedAliases,
    int OwnedOutsideUniverse,
    bool OwnedSubsetOfVisibleInEveryPack,
    bool OwnershipConserved);

/// <summary>What must be byte-identical about one document's source before its packs may be trusted.</summary>
public sealed record V5CohortDocumentFacts(
    string DocumentId,
    string PdfRelativePath,
    string PdfSha256,
    int AtomCount,
    string AliasesSha256,
    string SourceTextSha256,
    string SourceUniverseHash,
    int PackCount,
    V5CohortOwnershipProof Ownership);

/// <summary>One frozen cohort request. <see cref="ProviderBody"/> is the exact byte sequence a provider run must send.</summary>
public sealed record V5CohortRow(
    int Ordinal,
    string DocumentId,
    string PackId,
    int OwnedCount,
    int ContextOnlyCount,
    string SemanticRequestHash,
    int SemanticRequestBytes,
    string PromptHash,
    string SchemaHash,
    string TaskContractHash,
    string SourceUniverseHash,
    string ProviderRequestHash,
    int ProviderRequestBytes,
    int MaxCompletionTokens,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] ProviderBody { get; init; } = [];
}

public sealed record V5CohortPreflight(
    IReadOnlyList<V5CohortDocumentFacts> Documents,
    IReadOnlyList<V5CohortRow> Rows);

/// <summary>
/// Builds the first V5 measurement cohort provider-free: every P05 pack of SRC-089 and SRC-095, each
/// with its exact frozen provider body. Reuses <see cref="V5PdfPreflightBuilder.BuildV2_1"/> unchanged
/// - no second composer, packer or body builder - and adds only the source-universe and ownership
/// facts a provider run must reproduce byte for byte. Never opens a provider or Gold.
/// </summary>
public static class V5CohortPreflightBuilder
{
    public const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    public const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    public static IReadOnlyList<(string DocumentId, string PdfRelativePath, int ExpectedPacks)> Documents { get; } =
    [
        ("SRC-089", Src089, 7),
        ("SRC-095", Src095, 24),
    ];

    /// <summary>The qualified V5 payload envelope. Model, route and reasoning are frozen here, not read from the environment.</summary>
    public static V5ProviderEnvelope Envelope { get; } =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    public static V5CohortPreflight Build(string repositoryRoot, DocumentTaskContract contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(contract);
        var facts = new List<V5CohortDocumentFacts>();
        var rows = new List<V5CohortRow>();
        foreach (var (documentId, relative, expectedPacks) in Documents)
        {
            var pdf = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var built = V5PdfPreflightBuilder.BuildV2_1(pdf, documentId, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(pdf);
            if (built.Requests.Count != expectedPacks)
                throw new InvalidOperationException($"cohort-pack-count-mismatch:{documentId}:expected={expectedPacks}:actual={built.Requests.Count}");

            var ownership = ProveOwnership(atoms, built.Requests);
            if (!ownership.OwnershipConserved)
                throw new InvalidOperationException($"cohort-ownership-not-conserved:{documentId}");

            facts.Add(new V5CohortDocumentFacts(
                documentId,
                relative,
                Sha256(File.ReadAllBytes(pdf)),
                atoms.Count,
                Sha256(Encoding.UTF8.GetBytes(string.Join("\n", atoms.Select(atom => atom.Alias)))),
                Sha256(JsonSerializer.SerializeToUtf8Bytes(atoms.Select(atom => new[] { atom.Alias, atom.SourceId, atom.Text }).ToArray())),
                built.Preflight.SourceUniverseSha,
                built.Requests.Count,
                ownership));

            foreach (var pack in built.Requests)
            {
                var body = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Envelope);
                // The body built here must be the one preflight already hashed; anything else would
                // freeze a request that was never actually preflighted.
                if (!string.Equals(body.Hash, pack.ProviderRequestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"cohort-provider-body-drift:{documentId}:{pack.PackId}");
                var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
                rows.Add(new V5CohortRow(
                    rows.Count + 1,
                    documentId,
                    pack.PackId,
                    pack.OwnedAliases.Count,
                    pack.VisibleAliases.Count(alias => !owned.Contains(alias)),
                    pack.Request.RequestHash,
                    pack.Request.Utf8Bytes,
                    pack.Request.PromptHash,
                    pack.Request.SchemaHash,
                    built.Preflight.TaskContractHash,
                    built.Preflight.SourceUniverseSha,
                    body.Hash,
                    body.Bytes,
                    pack.MaxCompletionTokens,
                    pack.OwnedAliases,
                    pack.VisibleAliases)
                {
                    ProviderBody = body.PayloadBytes,
                });
            }
        }

        if (rows.Count != V5CohortGate.CohortRequestCount)
            throw new InvalidOperationException($"cohort-request-count-mismatch:{rows.Count}");
        return new V5CohortPreflight(facts, rows);
    }

    public static V5CohortOwnershipProof ProveOwnership(
        IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlyList<V5PackedSourceRequest> packs)
    {
        var universe = atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var ownerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alias in packs.SelectMany(pack => pack.OwnedAliases))
            ownerCounts[alias] = ownerCounts.GetValueOrDefault(alias) + 1;
        var exactlyOnce = universe.Count(alias => ownerCounts.GetValueOrDefault(alias) == 1);
        var unowned = universe.Count(alias => !ownerCounts.ContainsKey(alias));
        var multiply = ownerCounts.Count(item => item.Value > 1);
        var outside = ownerCounts.Keys.Count(alias => !universe.Contains(alias));
        // Halo may overlap visibility across packs; ownership may not. Every owned alias is also visible to its own pack.
        var ownedInVisible = packs.All(pack => pack.OwnedAliases.All(pack.VisibleAliases.Contains));
        return new V5CohortOwnershipProof(
            universe.Count, exactlyOnce, unowned, multiply, outside, ownedInVisible,
            exactlyOnce == universe.Count && unowned == 0 && multiply == 0 && outside == 0 && ownedInVisible);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
