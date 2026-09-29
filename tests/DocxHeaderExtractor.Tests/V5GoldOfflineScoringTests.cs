using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores the frozen 31-pack cohort's 1,203 bound claims against canonical Gold - entirely offline,
/// from the frozen provider responses and the canonical Gold registry alone. GoldRead=true,
/// providerCalls=0. Answers the question the source-selection remediation work deliberately deferred:
/// how good are the claims that already bound, semantically, not just syntactically.
/// <para>
/// SRC-089 and SRC-095's canonical Gold (<see cref="CanonicalGoldRegistry"/>) records
/// <c>bindingCoordinateSystem: STRUCTURED_SOURCE_PART_TUPLE</c> - each claim already carries its own
/// bound parts (alias + UTF-16 span) and a precomputed <c>identity</c> string. This predates
/// <see cref="CanonicalGoldRegistry.ResolveBoundGold"/>'s two known coordinate systems, so this reads
/// the hash-verified Gold document directly (still only through <see cref="CanonicalGoldRegistry.Resolve"/>,
/// never a raw file path) and recomputes each claim's identity from its own <c>boundParts</c> rather
/// than trusting the stored one - the recomputed value is asserted equal to it as a check, not assumed.
/// </para>
/// <para>
/// Gold carries heading occurrences only - no PARENT_OF/REFERENCES/SAME_ENTITY/CONTINUES relation or
/// hierarchy data exists for either document. Only the three unary occurrence predicates
/// (DOCUMENT_IDENTITY, STRUCTURAL_REGION, NAVIGATION_REPRESENTATION - <c>DocumentStructureTask.cs</c>)
/// are scoreable, since Gold has no notion of which one a heading occurrence is, only that it is one.
/// The four relation predicates are reported as NOT_EVALUABLE with their claim counts, never as a
/// fabricated score - the same capability-gated principle <see cref="CanonicalGoldRegistry.RequireCapability"/> enforces.
/// </para>
/// </summary>
public sealed class V5GoldOfflineScoringTests
{
    private const string CohortRoot = "artifacts/v5-provider-cohort-31-windows";
    private static readonly string[] RelationPredicates = ["PARENT_OF", "REFERENCES", "SAME_ENTITY", "CONTINUES"];
    // DocumentStructureTaskContract's three unary predicates (DocumentStructureTask.cs) - Gold has no
    // notion of which one a heading occurrence is, only that it is one, so all three are scored
    // against Gold's single occurrence axis rather than just one of them.
    private static readonly string[] OccurrencePredicates = ["DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION"];

    [Fact]
    public void Score_the_frozen_cohorts_bound_occurrence_claims_against_canonical_gold()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CohortRoot}/preflight/cohort.v1.json"))).RootElement;
        var packRows = cohort.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => (row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!), row => row);

        var atomsByDoc = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> AtomsFor(string documentId)
        {
            if (!atomsByDoc.TryGetValue(documentId, out var atoms))
            {
                var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                atomsByDoc[documentId] = atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf));
            }
            return atoms;
        }

        var callDirs = Directory.GetDirectories(TestRepository.Path($"{CohortRoot}/provider/calls")).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        Assert.Equal(31, callDirs.Length);

        int totalBound = 0;
        var boundByPredicate = new Dictionary<string, int>(StringComparer.Ordinal);
        // identity ("alias:start-end|alias:start-end...") -> approvedWording, first occurrence kept.
        var occurrenceClaimsByDoc = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["SRC-089"] = new(StringComparer.Ordinal),
            ["SRC-095"] = new(StringComparer.Ordinal),
        };

        foreach (var callDir in callDirs)
        {
            var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json"))).RootElement;
            var documentId = call.GetProperty("documentId").GetString()!;
            var packId = call.GetProperty("packId").GetString()!;
            var atoms = AtomsFor(documentId);
            var row = packRows[(documentId, packId)];
            var ownedAliases = row.GetProperty("ownedAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var visibleAliases = row.GetProperty("visibleAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var scope = ClaimBindingScope.Create(ownedAliases, visibleAliases);

            var content = File.ReadAllText(Path.Combine(callDir, "content.txt"));
            using var document = JsonDocument.Parse(content);
            var response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);

            var binding = ExactClaimBinderV2_1.Bind(packId, response.Claims, atoms, scope);
            foreach (var bound in binding.Bound)
            {
                totalBound++;
                boundByPredicate[bound.Claim.Predicate] = boundByPredicate.GetValueOrDefault(bound.Claim.Predicate) + 1;
                if (!OccurrencePredicates.Contains(bound.Claim.Predicate)) continue;

                var identity = string.Join("|", bound.Claim.Subject.Parts.Select(p => $"{p.Alias}:{p.Start}-{p.End}"));
                occurrenceClaimsByDoc[documentId].TryAdd(identity, bound.Claim.Subject.Parts[0].Text);
            }
        }

        // This is the number the source-selection remediation work deliberately deferred scoring on.
        Assert.Equal(1203, totalBound);

        var documents = new List<object>();
        int microTp = 0, microFp = 0, microFn = 0;
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence);
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.CharacterSpan);
            var entry = CanonicalGoldRegistry.Entry(id);
            using var gold = CanonicalGoldRegistry.Resolve(id);
            var occurrence = gold.RootElement.GetProperty("occurrence");
            var system = occurrence.GetProperty("bindingCoordinateSystem").GetString();
            Assert.Equal("STRUCTURED_SOURCE_PART_TUPLE", system);

            var goldIdentityToWording = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
            {
                var boundParts = claim.GetProperty("boundParts").EnumerateArray().Select(part => (
                    Alias: part.GetProperty("sourceAlias").GetString()!,
                    Start: part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    End: part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray();
                var recomputed = string.Join("|", boundParts.Select(p => $"{p.Alias}:{p.Start}-{p.End}"));
                // Never trust the stored identity blindly - recompute it from the same boundParts and
                // require the two to agree, so a stale precomputed field would fail loudly, not silently.
                Assert.Equal(claim.GetProperty("identity").GetString(), recomputed);
                goldIdentityToWording[recomputed] = claim.GetProperty("approvedWording").GetString()!;
            }
            Assert.Equal(entry.MaterializedSemanticClaims, goldIdentityToWording.Count);

            var predicted = occurrenceClaimsByDoc[id];
            var truePositiveIdentities = goldIdentityToWording.Keys.Intersect(predicted.Keys, StringComparer.Ordinal).ToArray();
            var missed = goldIdentityToWording.Keys.Except(predicted.Keys, StringComparer.Ordinal)
                .Select(identity => new { identity, wording = goldIdentityToWording[identity] }).ToArray();
            var spurious = predicted.Keys.Except(goldIdentityToWording.Keys, StringComparer.Ordinal)
                .Select(identity => new { identity, text = predicted[identity] }).ToArray();

            var tp = truePositiveIdentities.Length;
            var fp = spurious.Length;
            var fn = missed.Length;
            microTp += tp; microFp += fp; microFn += fn;
            var precision = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
            var recall = tp + fn == 0 ? 1 : (double)tp / (tp + fn);

            documents.Add(new
            {
                authorityId = id,
                goldSha256 = entry.GoldSha256,
                semanticHeadingTotal = entry.SemanticHeadingTotal,
                materializedSemanticClaims = entry.MaterializedSemanticClaims,
                predictedOccurrenceClaims = predicted.Count,
                truePositive = tp,
                falsePositive = fp,
                falseNegative = fn,
                precision = Round(precision),
                recall = Round(recall),
                f1 = Round(F1(precision, recall)),
                missed,
                spurious,
            });
        }

        var micPrecision = microTp + microFp == 0 ? 1 : (double)microTp / (microTp + microFp);
        var micRecall = microTp + microFn == 0 ? 1 : (double)microTp / (microTp + microFn);

        var report = new
        {
            schemaVersion = "v5-gold-offline-score-v1",
            sourceCohortCommit = "3a4f69a194f1e0a4598bda8c5eeed06b7c57bb40",
            providerCalls = 0,
            goldRead = true,
            note = "Scores only the three unary occurrence predicates (DOCUMENT_IDENTITY, STRUCTURAL_REGION, " +
                "NAVIGATION_REPRESENTATION) against canonical occurrence Gold, by exact identity match " +
                "(alias:start-end per part, in source order) - Gold has no notion of which one a heading " +
                "occurrence is, only that it is one. PARENT_OF/REFERENCES/SAME_ENTITY/CONTINUES have no " +
                "Gold-side relation or hierarchy data for SRC-089/SRC-095 and are reported as " +
                "NOT_EVALUABLE, never as a fabricated score.",
            totalBoundClaims = totalBound,
            boundByPredicate,
            occurrencePredicatesScored = OccurrencePredicates,
            occurrenceScored = new
            {
                documents,
                micro = new
                {
                    truePositive = microTp,
                    falsePositive = microFp,
                    falseNegative = microFn,
                    precision = Round(micPrecision),
                    recall = Round(micRecall),
                    f1 = Round(F1(micPrecision, micRecall)),
                },
            },
            relationPredicatesNotEvaluable = new
            {
                reason = "canonical Gold for SRC-089/SRC-095 carries heading occurrences only; no relation/hierarchy axis is registered evaluable",
                predicates = RelationPredicates,
                boundClaimCounts = RelationPredicates.ToDictionary(p => p, p => boundByPredicate.GetValueOrDefault(p), StringComparer.Ordinal),
                totalRelationClaimsNotScored = RelationPredicates.Sum(p => boundByPredicate.GetValueOrDefault(p)),
            },
        };

        var path = TestRepository.Path($"{CohortRoot}/diagnosis/gold-offline-score.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

        Assert.True(microTp > 0);
    }

    private static double F1(double precision, double recall) => precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    private static double Round(double value) => Math.Round(value, 4);
}
