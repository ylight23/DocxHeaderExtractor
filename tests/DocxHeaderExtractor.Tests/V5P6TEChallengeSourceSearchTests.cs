using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free discovery of strict-Gold multipart source shapes that could host an
/// authority-complete false-anchor-inside-multipart challenge. This is only source/GOLD
/// inventory: it deliberately does not infer F1 or G2A behavior from visual heuristics.
/// </summary>
public sealed class V5P6TEChallengeSourceSearchTests
{
    private const string RegistryPath = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string ArtifactRoot = "eval/a99-closed-loop/policy-audit/p6t-e-challenge-source-search";

    [Fact]
    public void P6T_E_challenge_search_freezes_strict_gold_multipart_source_inventory_without_claiming_g2a_authority()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RegistryPath)));
        var documents = new List<object>();
        var totalMultipart = 0;
        var eligibleDocuments = 0;

        foreach (var authority in registry.RootElement.GetProperty("authorities").EnumerateArray())
        {
            if (!authority.GetProperty("occurrenceEvaluable").GetBoolean() ||
                !authority.TryGetProperty("canonicalGoldPath", out var goldPathElement) ||
                goldPathElement.ValueKind != JsonValueKind.String)
                continue;

            var documentId = authority.GetProperty("authorityId").GetString()!;
            var goldPath = goldPathElement.GetString()!;
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(goldPath)));
            var source = gold.RootElement.GetProperty("source");
            if (!string.Equals(source.GetProperty("mediaType").GetString(), "PDF", StringComparison.OrdinalIgnoreCase))
                continue;

            var sourcePath = source.GetProperty("sourcePath").GetString()!;
            var pdfPath = TestRepository.Path(sourcePath);
            if (!File.Exists(pdfPath))
                continue;

            var expectedSourceHash = source.GetProperty("sourceSha256").GetString()!;
            var live = PdfSourceOccurrenceAdapter.Build(pdfPath);
            Assert.Equal(expectedSourceHash, live.SourceSha256);

            var atomByAlias = live.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var evidenceByAlias = live.Evidence.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
            var claims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
            var multipart = new List<object>();
            var missingAliases = new List<string>();

            foreach (var claim in claims)
            {
                var parts = claim.GetProperty("sourceParts").EnumerateArray().ToArray();
                if (parts.Length < 2) continue;

                var partRows = new List<object>();
                foreach (var part in parts)
                {
                    var alias = part.GetProperty("sourceAlias").GetString()!;
                    if (!atomByAlias.TryGetValue(alias, out var atom) || !evidenceByAlias.TryGetValue(alias, out var evidence))
                    {
                        missingAliases.Add(alias);
                        continue;
                    }

                    partRows.Add(new
                    {
                        alias,
                        atom.Ordinal,
                        atom.Page,
                        atom.Row,
                        atom.Segment,
                        atom.Text,
                        styleFacts = JsonSerializer.SerializeToElement(evidence.StyleFacts),
                        numberingFacts = JsonSerializer.SerializeToElement(evidence.NumberingFacts),
                    });
                }

                var orderedAtoms = parts
                    .Select(part => atomByAlias.GetValueOrDefault(part.GetProperty("sourceAlias").GetString()!))
                    .Where(atom => atom is not null)
                    .ToArray();
                var contiguous = orderedAtoms.Length == parts.Length && orderedAtoms
                    .Zip(orderedAtoms.Skip(1), (left, right) => right.Ordinal == left.Ordinal + 1)
                    .All(value => value);
                var next = orderedAtoms.Length == 0
                    ? null
                    : live.Atoms.FirstOrDefault(atom => atom.Ordinal == orderedAtoms[^1].Ordinal + 1);
                var nextGoldSingleton = next is not null && claims.Any(candidate =>
                    candidate.GetProperty("sourceParts").GetArrayLength() == 1 &&
                    string.Equals(candidate.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString(), next.Alias, StringComparison.Ordinal));

                multipart.Add(new
                {
                    parts = partRows,
                    partsContiguousInSourceOrder = contiguous,
                    nextAtom = next is null ? null : new { next.Alias, next.Ordinal, next.Page, next.Row, next.Segment, next.Text },
                    nextAtomIsGoldSingleton = nextGoldSingleton,
                    lastPartTextSha256 = orderedAtoms.Length == 0 ? null : Hash(orderedAtoms[^1].Text),
                });
            }

            if (multipart.Count > 0) eligibleDocuments++;
            totalMultipart += multipart.Count;
            documents.Add(new
            {
                documentId,
                goldSha256 = authority.GetProperty("goldSha256").GetString(),
                sourceSha256 = live.SourceSha256,
                sourceAliasUniverseSha256 = live.SourceAliasUniverseHash,
                modelVisibleEvidenceSha256 = live.ModelVisibleEvidenceHash,
                atoms = live.Atoms.Count,
                goldClaims = claims.Length,
                multipartGoldClaims = multipart.Count,
                missingGoldAliases = missingAliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                multipartClaims = multipart,
            });
        }

        FreezeArtifact.AssertJson(ArtifactRoot, "strict-gold-multipart-source-inventory.v1.json", new
        {
            schemaVersion = "v5-p6t-e-challenge-source-inventory-v1",
            status = "SOURCE_SEARCH_ONLY_NOT_G2A_QUALIFICATION",
            authority = "current strict Gold registry + source hash verified PdfSourceOccurrenceAdapter output",
            providerCalls = 0,
            goldMutation = "NONE",
            runtimeChanged = false,
            purpose = "Find candidate multipart continuations for a later F1/G2A-qualified false-anchor-inside-true-multipart challenge.",
            interpretationLimit = "Text/style/numbering and adjacency are source evidence only. They do not establish F1 ESTABLISHES_STRUCTURE or G2A HAS_STRUCTURAL_EXTENT.",
            eligiblePdfDocuments = eligibleDocuments,
            totalMultipartGoldClaims = totalMultipart,
            documents,
        });
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
