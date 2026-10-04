using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free projection of frozen P6T-F1 function membership onto the existing P6S
/// candidate authority. This measures candidate eligibility only; it does not call a provider,
/// perform extent resolution, or mutate Gold.
/// </summary>
public sealed class V5P6TG1FunctionConditionedCandidateProjectionTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string CaptureRoot = "artifacts/v5-p6t-function-membership/p6tf1-preflight";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tg1-candidate-projection";
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089),
        ("SRC-095", SourcePdfCorpus.Src095),
    ];

    private enum ProjectionClass { ALL_ESTABLISHES, MIXED, NO_ESTABLISHES }

    private sealed record Prepared(
        PdfCandidateAuthorityDocumentPlan Plan,
        PdfCandidateAuthorityPreparedPack SourcePack,
        PdfFunctionMembershipPreparedPackF1 FunctionPack,
        IReadOnlyDictionary<string, string> FunctionByAlias);

    [Fact]
    public void P6TG1_projects_frozen_function_membership_onto_existing_candidates_without_provider_or_gold_mutation()
    {
        using var primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/result.v1.json")));
        using var retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/retry-src089-result.v1.json")));
        using var anchorAudit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json")));
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("eval/a99-closed-loop/gold/SRC-089.gold.json")));

        var prepared089 = Prepare("SRC-089", SourcePdfCorpus.Src089, retry.RootElement.GetProperty("row"));
        var prepared095 = Prepare("SRC-095", SourcePdfCorpus.Src095,
            primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095"));

        var src089 = JsonSerializer.SerializeToElement(Project089(prepared089, gold.RootElement, anchorAudit.RootElement));
        var src095 = JsonSerializer.SerializeToElement(Project095(prepared095, anchorAudit.RootElement));
        var goldExtents = src089.GetProperty("goldExtents").EnumerateArray().ToArray();
        var falseEstablishAtoms = src089.GetProperty("falseEstablishAtoms").EnumerateArray().ToArray();
        var reviewedTocOccurrences = src095.GetProperty("reviewedTocOccurrences").GetInt32();
        var eligibleCandidatesTouchingReviewedToc = src095.GetProperty("eligibleCandidatesTouchingReviewedToc").GetInt32();

        Assert.Equal(6, goldExtents.Length);
        Assert.All(goldExtents, item => Assert.True(item.GetProperty("exactCandidateEligible").GetBoolean()));
        Assert.Equal(53, reviewedTocOccurrences);
        Assert.Equal(0, eligibleCandidatesTouchingReviewedToc);
        Assert.Equal(3, falseEstablishAtoms.Length);

        FreezeArtifact.AssertJson(OutputRoot, "function-conditioned-candidate-projection.v1.json", new
        {
            schemaVersion = "v5-p6tg1-function-conditioned-candidate-projection-v1",
            purpose = "PROJECT_FROZEN_F1_FUNCTION_MEMBERSHIP_ONTO_EXISTING_CANDIDATE_AUTHORITY",
            execution = new
            {
                providerCalls = 0,
                rawCaptureMutation = "NONE",
                goldMutation = "NONE",
                sharedRuntime = "UNCHANGED",
                groupingOrExtentModelCall = false,
                rawAuthority = new
                {
                    src089 = CaptureReceipt(retry.RootElement.GetProperty("row")),
                    src095 = CaptureReceipt(primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095")),
                },
            },
            projectionPolicy = new
            {
                candidateAuthority = "EXISTING_P6S_SOURCE_DERIVED_UNIVERSE_UNCHANGED",
                classification = new
                {
                    allEstablishes = "every source atom in candidate has ESTABLISHES_STRUCTURE",
                    mixed = "at least one ESTABLISHES_STRUCTURE and at least one non-ESTABLISHES_STRUCTURE atom",
                    noEstablishes = "no source atom in candidate has ESTABLISHES_STRUCTURE",
                    eligible = "ALL_ESTABLISHES only",
                },
                competingVariantDefinition = "other eligible candidates with same first/primary source alias as the exact Gold extent",
                candidateIdsAreRequestLocal = true,
                relationsUsed = false,
            },
            src089,
            src095,
            answers = new
            {
                reviewedSrc089GoldExtentsStillEligible = goldExtents.Count(item => item.GetProperty("exactCandidateEligible").GetBoolean()),
                reviewedSrc089GoldExtentsTotal = goldExtents.Length,
                reviewedSrc095TocRepresentationsWithAnyEligibleCandidate = src095.GetProperty("tocOccurrencesWithEligibleCandidates").GetInt32(),
                src095EligibleCandidateLeakageCount = eligibleCandidatesTouchingReviewedToc,
                frontMatterFalseEstablishAtoms = falseEstablishAtoms.Length,
                frontMatterSurvivingCandidateUnion = src089.GetProperty("frontMatterSurvivingCandidateUnion").GetInt32(),
            },
            conclusion = "PROVIDER_FREE_ELIGIBILITY_PROJECTION_ONLY; DOES_NOT_ASSERT_SEMANTIC_OR_EXTENT_CORRECTNESS",
        });
    }

    private static Prepared Prepare(string documentId, string sourcePdf, JsonElement captureRow)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePdf));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
        var sourcePack = plan.Packs.Single(pack => pack.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var functionPack = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, sourcePack, Correspondences(sourcePack));
        Assert.Equal(functionPack.Request.UserMessageSha256, captureRow.GetProperty("semanticRequestHash").GetString());
        Assert.Equal(functionPack.ProviderRequestHash, captureRow.GetProperty("providerRequestHash").GetString());
        Assert.Equal(96, functionPack.Request.Occurrences.Count);
        Assert.True(captureRow.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean());
        Assert.Equal("stop", captureRow.GetProperty("finishReason").GetString());

        var raw = captureRow.GetProperty("rawResponse").GetString()!;
        Assert.Equal(captureRow.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw));
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(functionPack, raw);
        Assert.Equal(96, parsed.Decisions.Count);
        var atomByOccurrence = functionPack.Request.Occurrences.ToDictionary(item => item.Id, item => item.Atom, StringComparer.Ordinal);
        var functionByAlias = parsed.Decisions.ToDictionary(item => atomByOccurrence[item.OccurrenceId].Alias,
            item => item.Function.ToString(), StringComparer.Ordinal);
        Assert.Equal(96, functionByAlias.Count);
        return new Prepared(plan, sourcePack, functionPack, functionByAlias);
    }

    private static object Project089(Prepared prepared, JsonElement goldRoot, JsonElement anchorAudit)
    {
        var claims = goldRoot.GetProperty("occurrence").GetProperty("claims").EnumerateArray().ToArray();
        var goldUnits = anchorAudit.GetProperty("src089").GetProperty("goldUnits").EnumerateArray().ToArray();
        Assert.Equal(6, goldUnits.Length);
        var reviewedPrimaries = goldUnits.Select(item => item.GetProperty("primary").GetString()!).ToHashSet(StringComparer.Ordinal);
        var reviewedGold = claims.Select(claim => new
            {
                identity = claim.GetProperty("identity").GetString()!,
                parts = claim.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray(),
            })
            .Where(item => item.parts.Length > 0 && reviewedPrimaries.Contains(item.parts[0]))
            .ToArray();
        Assert.Equal(6, reviewedGold.Length);

        var projection = Classify(prepared);
        var goldRows = reviewedGold.Select(item =>
        {
            var exact = prepared.SourcePack.Universe.Candidates.SingleOrDefault(candidate => candidate.SpanIdentity == item.identity);
            Assert.NotNull(exact);
            var exactEligible = projection[exact!.Id] == ProjectionClass.ALL_ESTABLISHES;
            var competing = prepared.SourcePack.Universe.Candidates
                .Where(candidate => candidate.Id != exact.Id && candidate.Endpoint.Parts[0].Alias == item.parts[0]
                    && projection[candidate.Id] == ProjectionClass.ALL_ESTABLISHES)
                .ToArray();
            return new
            {
                goldIdentity = item.identity,
                primaryAlias = item.parts[0],
                exactCandidateId = exact.Id,
                exactCandidateKind = exact.Kind.ToString(),
                exactCandidateEligible = exactEligible,
                competingEligibleVariantsAtSamePrimary = competing.Length,
                competingKinds = competing.GroupBy(candidate => candidate.Kind.ToString(), StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            };
        }).ToArray();

        var falseEstablishes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["L0002:S0"] = "ISSUER_HEADER",
            ["L0002:S1"] = "NATIONAL_HEADER",
            ["L0003:S1"] = "NATIONAL_MOTTO",
        };
        var byAlias = prepared.Plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var residualAtoms = falseEstablishes.Select(pair =>
        {
            Assert.Equal("ESTABLISHES_STRUCTURE", prepared.FunctionByAlias[pair.Key]);
            var atom = byAlias[pair.Key];
            var touched = prepared.SourcePack.Universe.Candidates.Where(candidate => candidate.Endpoint.Parts.Any(part => part.Alias == pair.Key)).ToArray();
            var eligible = touched.Where(candidate => projection[candidate.Id] == ProjectionClass.ALL_ESTABLISHES).ToArray();
            return new
            {
                alias = pair.Key,
                sourceReview = pair.Value,
                candidateCountTouchingAtom = touched.Length,
                eligibleCandidateCountTouchingAtom = eligible.Length,
                survivingCandidates = eligible.Select(candidate => new
                    {
                        candidateId = candidate.Id,
                        candidateKind = candidate.Kind.ToString(),
                        candidateIdentity = candidate.SpanIdentity,
                        aliases = candidate.Endpoint.Parts.Select(part => part.Alias).ToArray(),
                    })
                    .OrderBy(candidate => candidate.candidateId, StringComparer.Ordinal).ToArray(),
            };
        }).ToArray();
        var frontMatterUnion = prepared.SourcePack.Universe.Candidates
            .Where(candidate => candidate.Endpoint.Parts.Any(part => falseEstablishes.ContainsKey(part.Alias))
                && projection[candidate.Id] == ProjectionClass.ALL_ESTABLISHES)
            .ToArray();

        return new
        {
            candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
            candidateCount = prepared.SourcePack.Universe.Candidates.Count,
            classes = ClassCounts(projection),
            reviewedGoldExtentCount = goldRows.Length,
            exactGoldCoverage = goldRows.Count(row => row.exactCandidateEligible),
            goldExtents = goldRows,
            falseEstablishAtoms = residualAtoms,
            frontMatterSurvivingCandidateUnion = frontMatterUnion.Length,
            frontMatterSurvivingCandidates = frontMatterUnion.Select(candidate => new
                {
                    candidateId = candidate.Id,
                    candidateKind = candidate.Kind.ToString(),
                    candidateIdentity = candidate.SpanIdentity,
                    aliases = candidate.Endpoint.Parts.Select(part => part.Alias).ToArray(),
                })
                .OrderBy(candidate => candidate.candidateId, StringComparer.Ordinal).ToArray(),
        };
    }

    private static object Project095(Prepared prepared, JsonElement anchorAudit)
    {
        var tocRows = anchorAudit.GetProperty("src095").GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(53, tocRows.Length);
        var projection = Classify(prepared);
        var toc = tocRows.Select(row =>
        {
            var occurrenceId = row.GetProperty("occurrence").GetString()!;
            var alias = prepared.FunctionPack.Request.Occurrences.Single(item => item.Id == occurrenceId).Atom.Alias;
            Assert.Equal("REPRESENTS_STRUCTURE", prepared.FunctionByAlias[alias]);
            var touching = prepared.SourcePack.Universe.Candidates.Where(candidate => candidate.Endpoint.Parts.Any(part => part.Alias == alias)).ToArray();
            var eligible = touching.Where(candidate => projection[candidate.Id] == ProjectionClass.ALL_ESTABLISHES).ToArray();
            return new
            {
                occurrence = occurrenceId,
                alias,
                function = prepared.FunctionByAlias[alias],
                candidatesTouchingOccurrence = touching.Length,
                eligibleCandidatesTouchingOccurrence = eligible.Length,
            };
        }).ToArray();
        var allTocAliases = toc.Select(row => row.alias).ToHashSet(StringComparer.Ordinal);
        var eligibleTouchingToc = prepared.SourcePack.Universe.Candidates
            .Where(candidate => candidate.Endpoint.Parts.Any(part => allTocAliases.Contains(part.Alias))
                && projection[candidate.Id] == ProjectionClass.ALL_ESTABLISHES)
            .ToArray();
        return new
        {
            candidateUniverseFingerprint = prepared.SourcePack.Universe.Fingerprint,
            candidateCount = prepared.SourcePack.Universe.Candidates.Count,
            classes = ClassCounts(projection),
            reviewedTocOccurrences = toc.Length,
            tocOccurrencesWithEligibleCandidates = toc.Count(row => row.eligibleCandidatesTouchingOccurrence > 0),
            eligibleCandidatesTouchingReviewedToc = eligibleTouchingToc.Length,
            tocRows = toc,
        };
    }

    private static IReadOnlyDictionary<string, ProjectionClass> Classify(Prepared prepared)
    {
        var result = new Dictionary<string, ProjectionClass>(StringComparer.Ordinal);
        foreach (var candidate in prepared.SourcePack.Universe.Candidates)
        {
            var functions = candidate.Endpoint.Parts.Select(part => prepared.FunctionByAlias.TryGetValue(part.Alias, out var function)
                ? function
                : throw new InvalidOperationException($"candidate-source-alias-not-in-frozen-function-ledger:{part.Alias}")).ToArray();
            var establishes = functions.Count(function => function == "ESTABLISHES_STRUCTURE");
            var classification = establishes == functions.Length ? ProjectionClass.ALL_ESTABLISHES
                : establishes > 0 ? ProjectionClass.MIXED
                : ProjectionClass.NO_ESTABLISHES;
            result.Add(candidate.Id, classification);
        }
        return result;
    }

    private static object ClassCounts(IReadOnlyDictionary<string, ProjectionClass> projection) =>
        Enum.GetValues<ProjectionClass>().ToDictionary(value => value.ToString(), value => projection.Values.Count(item => item == value), StringComparer.Ordinal);

    private static object CaptureReceipt(JsonElement row) => new
    {
        documentId = row.GetProperty("documentId").GetString(),
        semanticRequestHash = row.GetProperty("semanticRequestHash").GetString(),
        providerRequestHash = row.GetProperty("providerRequestHash").GetString(),
        rawResponseSha256 = row.GetProperty("rawResponseSha256").GetString(),
        finishReason = row.GetProperty("finishReason").GetString(),
        parserAccepted = row.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean(),
        retryCount = row.GetProperty("retryCount").GetInt32(),
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(key, out var existing) ? existing.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = values;
        }
        return result;
    }
}
