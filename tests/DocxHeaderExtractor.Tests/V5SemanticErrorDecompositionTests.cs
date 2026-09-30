using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes the evidence boundary for the V5 semantic-quality diagnosis. The authoritative score is
/// read-only; this test writes separate P4 artifacts and never executes provider, graph, or projection
/// behavior. Per-occurrence rows are retained even when the available frozen score cannot support a
/// finer semantic or causal label.
/// </summary>
public sealed class V5SemanticErrorDecompositionTests
{
    private const string ScorePath = "artifacts/v5-provider-cohort-31-windows/diagnosis/gold-offline-score.v1.json";
    private const string P4Root = "artifacts/v5-semantic-error-decomposition";
    private static readonly HashSet<string> OccurrencePredicates = new(StringComparer.Ordinal)
        { "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION" };

    [Fact]
    public void Freeze_observability_bounded_semantic_decomposition()
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ScorePath)));
        var root = score.RootElement;
        var docs = root.GetProperty("occurrenceScored").GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(2, docs.Length);
        var total = root.GetProperty("occurrenceScored").GetProperty("micro");
        Assert.Equal(82, total.GetProperty("truePositive").GetInt32());
        Assert.Equal(519, total.GetProperty("falsePositive").GetInt32());
        Assert.Equal(57, total.GetProperty("falseNegative").GetInt32());
        Assert.Equal(1203, root.GetProperty("totalBoundClaims").GetInt32());
        var byDocument = docs.ToDictionary(d => d.GetProperty("authorityId").GetString()!, StringComparer.Ordinal);
        Assert.Equal((13, 32, 23), (byDocument["SRC-089"].GetProperty("truePositive").GetInt32(), byDocument["SRC-089"].GetProperty("falsePositive").GetInt32(), byDocument["SRC-089"].GetProperty("falseNegative").GetInt32()));
        Assert.Equal((69, 487, 34), (byDocument["SRC-095"].GetProperty("truePositive").GetInt32(), byDocument["SRC-095"].GetProperty("falsePositive").GetInt32(), byDocument["SRC-095"].GetProperty("falseNegative").GetInt32()));

        var fpRows = new List<object>();
        var fpSourceRows = new List<(string DocumentId, string Identity, string Text)>();
        var fnRows = new List<object>();
        var rawCandidates = new Dictionary<string, List<(string Identity, string Text, string Predicate, string PackId, int Ordinal)>>(StringComparer.Ordinal)
            { ["SRC-089"] = [], ["SRC-095"] = [] };
        var boundIdentities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            { ["SRC-089"] = new(StringComparer.Ordinal), ["SRC-095"] = new(StringComparer.Ordinal) };
        var boundByOccurrence = new Dictionary<string, Dictionary<string, (HashSet<string> Predicates, string Text)>>(StringComparer.Ordinal)
            { ["SRC-089"] = new(StringComparer.Ordinal), ["SRC-095"] = new(StringComparer.Ordinal) };
        var boundClaimIdsByOccurrence = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal)
            { ["SRC-089"] = new(StringComparer.Ordinal), ["SRC-095"] = new(StringComparer.Ordinal) };
        var rawPredicateClaims = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundPredicateClaims = new Dictionary<string, int>(StringComparer.Ordinal);
        var goldByDoc = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            using var canonicalGold = CanonicalGoldRegistry.Resolve(id);
            goldByDoc[id] = canonicalGold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .ToDictionary(g => g.GetProperty("identity").GetString()!, g => g.GetProperty("approvedWording").GetString()!, StringComparer.Ordinal);
        }
        var rawClaims = 0;
        var contractAccepted = 0;
        var contractRefused = 0;
        var bindingRefused = 0;
        var binderEligible = 0;
        var totalBoundClaims = 0;
        var binderRefusalRows = new List<object>();
        var binderRefusedGoldExact = new HashSet<string>(StringComparer.Ordinal);
        var binderRefusedGoldNear = new HashSet<string>(StringComparer.Ordinal);
        var binderRefusedGoldExactClaimCount = 0;
        var binderRefusedGoldNearClaimCount = 0;
        var binderRefusedNonGold = 0;
        var binderRefusedUnknown = 0;
        var rowsRoot = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json"))).RootElement;
        var packRows = rowsRoot.GetProperty("rows").EnumerateArray().ToDictionary(
            r => (r.GetProperty("DocumentId").GetString()!, r.GetProperty("PackId").GetString()!), r => r);
        var atomsByDoc = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> Atoms(string docId)
        {
            if (!atomsByDoc.TryGetValue(docId, out var atoms))
                atomsByDoc[docId] = atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(docId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095));
            return atoms;
        }

        foreach (var doc in docs)
        {
            var docId = doc.GetProperty("authorityId").GetString()!;
            foreach (var row in doc.GetProperty("spurious").EnumerateArray())
                fpSourceRows.Add((docId, row.GetProperty("identity").GetString()!, row.GetProperty("text").GetString()!));
        }

        // Re-derive per-claim contract and exact source coordinates from the immutable raw response.
        // This is explicitly offline evidence, not a claimed historical quarantine event stream.
        var callDirs = Directory.GetDirectories(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/provider/calls"));
        Assert.Equal(31, callDirs.Length);
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        foreach (var callDir in callDirs)
        {
            using var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json")));
            var docId = call.RootElement.GetProperty("documentId").GetString()!;
            var packId = call.RootElement.GetProperty("packId").GetString()!;
            var row = packRows[(docId, packId)];
            var owned = row.GetProperty("ownedAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var visible = row.GetProperty("visibleAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var atoms = Atoms(docId);
            using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "content.txt")));
            var quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(raw.RootElement, contract);
            rawClaims += quarantine.RawClaimCount;
            contractAccepted += quarantine.Eligible.Count;
            contractRefused += quarantine.ContractRefusals.Count;
            binderEligible += quarantine.Eligible.Count;
            var scope = ClaimBindingScope.Create(owned, visible);
            var binding = ExactClaimBinderV2_1.Bind(packId, quarantine.Eligible, atoms, scope);
            bindingRefused += binding.Refusals.Count;
            totalBoundClaims += binding.Bound.Count;
            foreach (var refusal in binding.Refusals)
            {
                var ordinal = refusal.Key.StartsWith("proposal-", StringComparison.Ordinal)
                    ? int.Parse(refusal.Key[9..], System.Globalization.CultureInfo.InvariantCulture) - 1 : -1;
                var indexed = quarantine.Eligible.FirstOrDefault(p => p.OriginalOrdinal == ordinal);
                var proposal = indexed?.Proposal;
                var subject = proposal is null ? null : SemanticSourcePartBinder.Bind(atoms,
                    ProviderSourcePartNormalization.ToCanonical(proposal.Subject.SourceParts));
                string? sourceIdentity = subject?.IsBound == true ? subject.Identity : null;
                string disposition;
                if (proposal is not null && OccurrencePredicates.Contains(proposal.Predicate) && sourceIdentity is not null && goldByDoc[docId].ContainsKey(sourceIdentity))
                {
                    disposition = "BINDER_REFUSED_GOLD_EXACT";
                    binderRefusedGoldExact.Add(docId + ":" + sourceIdentity);
                    binderRefusedGoldExactClaimCount++;
                }
                else if (proposal is not null && OccurrencePredicates.Contains(proposal.Predicate) && sourceIdentity is not null)
                {
                    var sourceText = string.Concat(subject!.Parts.Select(p => p.Text));
                    var near = goldByDoc[docId].Any(g => TokenOverlap(sourceText, g.Value) >= 0.5 ||
                        ParseIdentity(sourceIdentity).Select(p => p.Alias).Intersect(ParseIdentity(g.Key).Select(p => p.Alias), StringComparer.Ordinal).Any());
                    if (near)
                    {
                        disposition = "BINDER_REFUSED_GOLD_NEAR_POTENTIAL";
                        binderRefusedGoldNear.Add(docId + ":" + sourceIdentity);
                        binderRefusedGoldNearClaimCount++;
                    }
                    else
                    {
                        disposition = "BINDER_REFUSED_NON_GOLD_EXACT_SOURCE";
                        binderRefusedNonGold++;
                    }
                }
                else
                {
                    disposition = "BINDER_REFUSED_UNKNOWN_OR_RELATION_NOT_EVALUABLE";
                    binderRefusedUnknown++;
                }
                binderRefusalRows.Add(new { documentId = docId, packId, rawOrdinal = ordinal + 1,
                    predicate = proposal?.Predicate, sourceIdentity, refusalReason = refusal.Value, goldAssessment = disposition });
            }
            foreach (var bound in binding.Bound.Where(b => OccurrencePredicates.Contains(b.Claim.Predicate)))
            {
                var identity = string.Join("|", bound.Claim.Subject.Parts.Select(p => $"{p.Alias}:{p.Start}-{p.End}"));
                boundIdentities[docId].Add(identity);
                boundPredicateClaims[bound.Claim.Predicate] = boundPredicateClaims.GetValueOrDefault(bound.Claim.Predicate) + 1;
                if (!boundByOccurrence[docId].TryGetValue(identity, out var occurrence))
                    occurrence = (new HashSet<string>(StringComparer.Ordinal), string.Concat(bound.Claim.Subject.Parts.Select(p => p.Text)));
                occurrence.Predicates.Add(bound.Claim.Predicate);
                boundByOccurrence[docId][identity] = occurrence;
                if (!boundClaimIdsByOccurrence[docId].TryGetValue(identity, out var claimIds))
                    boundClaimIdsByOccurrence[docId][identity] = claimIds = [];
                claimIds.Add(bound.Claim.ClaimId);
            }
            foreach (var indexed in quarantine.Eligible)
            {
                var proposal = indexed.Proposal;
                rawPredicateClaims[proposal.Predicate] = rawPredicateClaims.GetValueOrDefault(proposal.Predicate) + 1;
                if (!OccurrencePredicates.Contains(proposal.Predicate)) continue;
                var sourceParts = ProviderSourcePartNormalization.ToCanonical(proposal.Subject.SourceParts);
                var subject = SemanticSourcePartBinder.Bind(atoms, sourceParts);
                if (subject.IsBound)
                    rawCandidates[docId].Add((subject.Identity, string.Concat(subject.Parts.Select(p => p.Text)), proposal.Predicate, packId, indexed.OriginalOrdinal + 1));
            }
        }
        Assert.Equal(1448, rawClaims);
        Assert.Equal(0, contractRefused);
        Assert.Equal(1448, contractAccepted);
        Assert.Equal(1448, bindingRefused + totalBoundClaims);
        Assert.Equal(1203, totalBoundClaims);
        Assert.Equal(245, binderRefusalRows.Count);
        Assert.Equal(245, binderRefusedGoldExactClaimCount + binderRefusedGoldNearClaimCount + binderRefusedNonGold + binderRefusedUnknown);

        var fnRawExact = 0;
        var fnRawNearOnly = 0;
        var fnBoundNearOnly = 0;
        var fnNoRawExact = 0;
        var fnEvidenceRows = new List<object>();
        var exactGoldRaw = 0;
        var nearGoldRaw = 0;
        var absentGoldRaw = 0;
        var goldExactRawAndBound = 0;
        var goldExactRawButBindingRefused = 0;
        foreach (var doc in docs)
        {
            var docId = doc.GetProperty("authorityId").GetString()!;
            using var canonicalGold = CanonicalGoldRegistry.Resolve(docId);
            var goldRows = canonicalGold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .Select(g => (Identity: g.GetProperty("identity").GetString()!, Wording: g.GetProperty("approvedWording").GetString()!)).ToArray();
            foreach (var gold in goldRows)
            {
                var identity = gold.Identity;
                var wording = gold.Wording;
                var exactRaw = rawCandidates[docId].Any(c => string.Equals(c.Identity, identity, StringComparison.Ordinal));
                var exactBound = boundIdentities[docId].Contains(identity);
                var near = !exactRaw && rawCandidates[docId].Any(c => TokenOverlap(c.Text, wording) >= 0.5 ||
                    c.Identity.Split('|').Select(part => part.Split(':')[0]).Intersect(identity.Split('|').Select(part => part.Split(':')[0]), StringComparer.Ordinal).Any());
                var boundNear = !exactRaw && boundByOccurrence[docId].Any(c =>
                    TokenOverlap(c.Value.Text, wording) >= 0.5 ||
                    c.Key.Split('|').Select(part => part.Split(':')[0]).Intersect(identity.Split('|').Select(part => part.Split(':')[0]), StringComparer.Ordinal).Any());
                if (exactRaw) exactGoldRaw++; else if (near) nearGoldRaw++; else absentGoldRaw++;
                if (exactRaw && exactBound) goldExactRawAndBound++;
                if (exactRaw && !exactBound) goldExactRawButBindingRefused++;
                var officialFn = doc.GetProperty("missed").EnumerateArray().Any(m => m.GetProperty("identity").GetString() == identity);
                if (!officialFn) continue;
                var disposition = exactRaw
                    ? exactBound ? "FN_BOUND_EXACT_BUT_SCORE_MISMATCH" : "FN_RAW_EXACT_BINDING_REFUSED"
                    : boundNear ? "FN_BOUND_NEAR_ONLY" : near ? "FN_RAW_NEAR_ONLY" : "FN_NO_RAW_EXACT_PROPOSAL";
                if (disposition == "FN_RAW_EXACT_BINDING_REFUSED") fnRawExact++;
                else if (disposition == "FN_BOUND_NEAR_ONLY") fnBoundNearOnly++;
                else if (disposition == "FN_RAW_NEAR_ONLY") fnRawNearOnly++;
                else if (disposition == "FN_NO_RAW_EXACT_PROPOSAL") fnNoRawExact++;
                fnEvidenceRows.Add(new { documentId = docId, identity, wording, disposition,
                    evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW", exactRawProposal = exactRaw,
                    exactBoundOccurrence = exactBound,
                    rawCandidateEvidence = rawCandidates[docId].Where(c => TokenOverlap(c.Text, wording) >= 0.5 ||
                        c.Identity.Split('|').Select(part => part.Split(':')[0]).Intersect(identity.Split('|').Select(part => part.Split(':')[0]), StringComparer.Ordinal).Any())
                        .Select(c => new { c.Identity, c.Text, c.Predicate, c.PackId, c.Ordinal }).ToArray(),
                    note = "Near is a deterministic token/alias overlap diagnostic, not an exact Gold identity match." });
            }
        }
        Assert.Equal(57, fnRawExact + fnBoundNearOnly + fnRawNearOnly + fnNoRawExact);
        Assert.Equal(139, exactGoldRaw + nearGoldRaw + absentGoldRaw);
        var binderRefusedUniqueGoldLoss = binderRefusedGoldExact.Count(key =>
        {
            var separator = key.IndexOf(':');
            var docId = key[..separator];
            var identity = key[(separator + 1)..];
            return !boundIdentities[docId].Contains(identity);
        });
        Assert.Equal(goldExactRawButBindingRefused, binderRefusedUniqueGoldLoss);
        Assert.Equal(2, binderRefusedUniqueGoldLoss);
        fnRows.AddRange(fnEvidenceRows);

        var src095Atoms = Atoms("SRC-095").OrderBy(a => a.Ordinal).ToArray();
        var toc = src095Atoms.FirstOrDefault(a => Normalize(a.Text) == "TABLE OF CONTENTS");
        var bodyStart = src095Atoms.FirstOrDefault(a => Normalize(a.Text) == "1. INTRODUCTION" && toc is not null && a.Page > toc.Page);
        var refsStart = src095Atoms.LastOrDefault(a => Normalize(a.Text) == "12. REFERENCES");
        var indexStart = src095Atoms.LastOrDefault(a => Normalize(a.Text) == "INDEX");
        var appendixStart = src095Atoms.FirstOrDefault(a => refsStart is not null && a.Ordinal > refsStart.Ordinal &&
            System.Text.RegularExpressions.Regex.IsMatch(a.Text.Trim(), @"(?i)^(appendix\s+[a-z]|[a-d](?:\.\d+)*\.?\s+)"));
        var repeatedLayoutText = src095Atoms.GroupBy(a => (Text: Normalize(a.Text), a.Row))
            .Where(g => g.Key.Text.Length is > 0 and <= 100 && g.Select(a => a.Page).Distinct().Count() >= 3)
            .Select(g => g.Key.Text).ToHashSet(StringComparer.Ordinal);
        var fpRegionFor = (string documentId, string identity) =>
        {
            if (documentId != "SRC-095") return "UNKNOWN_REGION";
            var labels = ParseIdentity(identity).Select(p => src095Atoms.FirstOrDefault(a => a.Alias == p.Alias))
                .Where(a => a is not null).Select(a => ClassifyRegion(a!, toc, bodyStart, refsStart, appendixStart, indexStart, repeatedLayoutText)).Distinct().ToArray();
            return labels.Length == 1 ? labels[0] : "UNKNOWN_REGION";
        };
        var fpSemanticCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fpContentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fpMechanismCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fp in fpSourceRows)
        {
            var result = ClassifyFp(fp.Identity, fp.Text, goldByDoc[fp.DocumentId]);
            var region = fpRegionFor(fp.DocumentId, fp.Identity);
            var contentClass = ClassifyFpContent(fp.Text, region, result.SemanticClass);
            fpSemanticCounts[result.SemanticClass] = fpSemanticCounts.GetValueOrDefault(result.SemanticClass) + 1;
            fpContentCounts[contentClass] = fpContentCounts.GetValueOrDefault(contentClass) + 1;
            fpMechanismCounts[result.Mechanism] = fpMechanismCounts.GetValueOrDefault(result.Mechanism) + 1;
            fpRows.Add(new { documentId = fp.DocumentId, identity = fp.Identity, text = fp.Text,
                exactMatchDisposition = "OFFICIAL_FP", semanticClass = result.SemanticClass,
                sourceContentClass = contentClass, region,
                identityMismatchMechanism = result.Mechanism, evidenceBasis = "GOLD_COMPARISON",
                rationale = result.Rationale,
                sourceClassificationEvidence = new { region, classificationRule = ContentRule(contentClass) },
                sourceAliasesSpans = ParseIdentity(fp.Identity).Select(p => new { alias = p.Alias, start = p.Start, end = p.End }).ToArray(),
                predicates = rawCandidates[fp.DocumentId].Where(c => c.Identity == fp.Identity).Select(c => c.Predicate).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                packIds = rawCandidates[fp.DocumentId].Where(c => c.Identity == fp.Identity).Select(c => c.PackId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                rawClaimOrdinals = rawCandidates[fp.DocumentId].Where(c => c.Identity == fp.Identity).Select(c => c.Ordinal).Distinct().Order().ToArray(),
                boundClaimIds = boundClaimIdsByOccurrence[fp.DocumentId].GetValueOrDefault(fp.Identity, []),
                sourceLayoutFacts = ParseIdentity(fp.Identity).Select(p => Atoms(fp.DocumentId).FirstOrDefault(a => a.Alias == p.Alias))
                    .Where(a => a is not null).Select(a => new { a!.Alias, a.Page, a.Row, a.Segment, a.Text }).ToArray()
            });
        }
        foreach (var category in new[] { "FP_NAVIGATION_TOC", "FP_RUNNING_HEADER_FOOTER", "FP_DOCUMENT_METADATA", "FP_AUTHOR_OR_MASTHEAD",
                     "FP_BODY_SENTENCE", "FP_LIST_ITEM_OR_CLAUSE", "FP_TABLE_OR_SCHEMA_CONTENT", "FP_CAPTION_OR_LABEL",
                     "FP_REFERENCE_OR_INDEX_ENTRY", "FP_PARTIAL_HEADING_FRAGMENT", "FP_SPLIT_HEADING_COMPONENT",
                     "FP_DUPLICATE_OR_ALTERNATE_OCCURRENCE", "FP_OTHER_STRUCTURAL_TEXT", "FP_UNCLASSIFIED" })
            fpContentCounts.TryAdd(category, 0);
        foreach (var category in new[] { "TRUE_SEMANTIC_FP", "PARTIAL_OVERLAP_WITH_GOLD", "SPLIT_GOLD_OCCURRENCE",
                     "MERGED_GOLD_OCCURRENCES", "SAME_TEXT_WRONG_OCCURRENCE", "OTHER_IDENTITY_NEAR_MISS", "UNCLASSIFIED" })
            fpMechanismCounts.TryAdd(category, 0);
        Assert.Equal(519, fpSemanticCounts.Values.Sum());
        Assert.Equal(519, fpContentCounts.Values.Sum());
        Assert.Equal(519, fpMechanismCounts.Values.Sum());

        var predicateRows = OccurrencePredicates.Order(StringComparer.Ordinal).Select(predicate =>
        {
            var occurrenceRows = boundByOccurrence.SelectMany(doc => doc.Value
                .Where(kv => kv.Value.Predicates.Contains(predicate))
                .Select(kv => (DocumentId: doc.Key, Identity: kv.Key, kv.Value.Text))).ToArray();
            var goldExact = occurrenceRows.Count(kv => goldByDoc[kv.DocumentId].ContainsKey(kv.Identity));
            var nonGold = occurrenceRows.Length - goldExact;
            var nearGold = occurrenceRows.Count(kv => !goldByDoc[kv.DocumentId].ContainsKey(kv.Identity) &&
                goldByDoc[kv.DocumentId].Values.Any(w => TokenOverlap(kv.Text, w) >= 0.5));
            return new
            {
                predicate,
                rawClaims = rawPredicateClaims.GetValueOrDefault(predicate),
                boundClaims = boundPredicateClaims.GetValueOrDefault(predicate),
                uniqueBoundOccurrences = occurrenceRows.Length,
                goldExactOccurrences = goldExact,
                nonGoldOccurrences = nonGold,
                nearGoldOccurrences = nearGold,
                precisionLikeDiagnostic = occurrenceRows.Length == 0 ? 0 : Math.Round((double)goldExact / occurrenceRows.Length, 4),
                evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW_AND_GOLD"
            };
        }).ToArray();
        var predicateCombinations = boundByOccurrence.Values.SelectMany(x => x.Values)
            .GroupBy(v => v.Predicates.Count switch
            {
                1 => v.Predicates.Single(),
                _ => "MULTIPLE_PREDICATES"
            }, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(519, fpRows.Count);
        Assert.Equal(57, fnRows.Count);

        var qualificationPath = TestRepository.Path("artifacts/v5-provider-qualification-v1/provider/provider-run-summary.v1.json");
        using var qualification = JsonDocument.Parse(File.ReadAllText(qualificationPath));
        Assert.Equal(31, qualification.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, qualification.RootElement.GetProperty("semanticInvalidCount").GetInt32());
        Assert.Equal(0, qualification.RootElement.GetProperty("firstAttemptContractValid").GetInt32());
        using var qualificationScore = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-provider-qualification-v1/scoring/score.v1.json")));
        var emptyPrediction = qualificationScore.RootElement.GetProperty("aggregate");
        Assert.Equal(139, emptyPrediction.GetProperty("goldClaims").GetInt32());
        Assert.Equal(0, emptyPrediction.GetProperty("tp").GetInt32());
        Assert.Equal(0, emptyPrediction.GetProperty("fp").GetInt32());
        Assert.Equal(139, emptyPrediction.GetProperty("fn").GetInt32());
        Assert.Equal(31, qualificationScore.RootElement.GetProperty("operational").GetProperty("claimCodecInvalid").GetInt32());
        using var frozenCohortResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/cohort-result.v1.json")));
        var frozenBinderRefusalTaxonomy = frozenCohortResult.RootElement.GetProperty("binding").GetProperty("RefusalTaxonomy").Clone();

        Write("observability.v1.json", new
        {
            schemaVersion = "v5-semantic-error-observability-v1",
            providerCalls = 0,
            goldRead = true,
            sourceCohortCommit = "3a4f69a194f1e0a4598bda8c5eeed06b7c57bb40",
            stages = new[]
            {
                Stage("RAW_PROVIDER_PROPOSAL", "OBSERVED", "FROZEN_HISTORICAL", "provider/calls/*/content.txt"),
                Stage("CONTRACT_DISPOSITION", "OFFLINE_REDERIVED_FROM_FROZEN_RAW", "OFFLINE_REDERIVED_FROM_FROZEN_RAW", "V2.1 codec can be applied to frozen raw response"),
                Stage("BINDER_DISPOSITION", "HISTORICALLY_OBSERVED", "FROZEN_HISTORICAL", "cohort-result.v1.json and frozen score"),
                Stage("BOUND_CLAIM", "HISTORICALLY_OBSERVED", "FROZEN_HISTORICAL", "1203 bound claims in frozen score"),
                Stage("GOLD_EXACT_MATCH", "HISTORICALLY_OBSERVED", "GOLD_COMPARISON", ScorePath),
                Stage("GRAPH_DISPOSITION", "NOT_OBSERVED", "NOT_OBSERVED", "No frozen historical graph dispositions; no replay performed"),
                Stage("PROJECTION_DISPOSITION", "NOT_OBSERVED", "NOT_OBSERVED", "No frozen historical projection dispositions; no replay performed")
            }
        });
        Write("population.v1.json", new
        {
            schemaVersion = "v5-semantic-error-population-v1", providerCalls = 0, goldRead = true,
            evidenceBasis = "FROZEN_HISTORICAL", rawProposalCount = 1448,
            contractAccepted, contractDispositionEvidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW",
            contractRefused, binderEligible, boundClaims = 1203, bindingRefused,
            bindingRefusalTaxonomy = frozenBinderRefusalTaxonomy,
            binderGoldAssessment = new { exactGoldIdentityRefusalClaims = binderRefusedGoldExactClaimCount, exactGoldIdentityRefusalOccurrences = binderRefusedGoldExact.Count, uniqueGoldOccurrenceLossProven = binderRefusedUniqueGoldLoss, goldNearPotentialClaims = binderRefusedGoldNearClaimCount, goldNearPotentialOccurrences = binderRefusedGoldNear.Count, nonGoldExact = binderRefusedNonGold, unknownOrRelationNotEvaluable = binderRefusedUnknown },
            bindingRefusals = binderRefusalRows,
            officialOccurrencePredictions = new { truePositive = 82, falsePositive = 519, total = 601 },
            goldOccurrences = new { truePositive = 82, falseNegative = 57, total = 139 },
            graph = "NOT_OBSERVED", projection = "NOT_OBSERVED",
            note = "Contract disposition is reported as accepted/refused only where frozen cohort aggregate supports it; do not interpret this as a per-claim historical quarantine trace."
        });
        Write("fp-decomposition.v1.json", new
        {
            schemaVersion = "v5-semantic-error-fp-v1", providerCalls = 0, goldRead = true,
            officialCount = 519, accountedExactlyOnce = fpRows.Count == 519,
            taxonomy = fpContentCounts,
            identityMismatchTaxonomy = fpMechanismCounts,
            rows = fpRows
        });
        Write("fn-decomposition.v1.json", new
        {
            schemaVersion = "v5-semantic-error-fn-v1", providerCalls = 0, goldRead = true,
            officialCount = 57, accountedExactlyOnce = fnRows.Count == 57,
            taxonomy = new { FN_NO_RAW_EXACT_PROPOSAL = fnNoRawExact, FN_RAW_NEAR_ONLY = fnRawNearOnly,
                FN_RAW_EXACT_CONTRACT_REFUSED = 0, FN_RAW_EXACT_BINDING_REFUSED = fnRawExact,
                FN_BOUND_NEAR_ONLY = fnBoundNearOnly, FN_CAUSE_NOT_OBSERVABLE = 0 },
            rows = fnRows,
            rawCoverage = new { goldTotal = 139, rawExact = exactGoldRaw, rawNear = nearGoldRaw, rawAbsent = absentGoldRaw,
                goldExactRawAndBound, goldExactRawButBindingRefused }
        });
        Write("predicate-analysis.v1.json", new
        {
            schemaVersion = "v5-semantic-error-predicate-v1", evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW_AND_GOLD",
            providerCalls = 0, goldRead = true, note = "Claim predicates join to exact binder identities and are compared with canonical Gold; this is diagnostic, not predicate-specific task scoring.",
            predicates = new[] { "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION" },
            byPredicate = predicateRows, occurrencePredicateCombinations = predicateCombinations
        });
        var regionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var regionCountsByDoc = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var fpRegionRows = new List<object>();
        foreach (var fp in fpSourceRows)
        {
            var region = "UNKNOWN_REGION";
            if (fp.DocumentId == "SRC-095")
            {
                var atoms = ParseIdentity(fp.Identity).Select(p => src095Atoms.FirstOrDefault(a => a.Alias == p.Alias)).Where(a => a is not null).ToArray();
                var labels = atoms.Select(a => ClassifyRegion(a!, toc, bodyStart, refsStart, appendixStart, indexStart, repeatedLayoutText)).Distinct().ToArray();
                if (labels.Length == 1) region = labels[0];
            }
            regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
            if (!regionCountsByDoc.TryGetValue(fp.DocumentId, out var docCounts)) regionCountsByDoc[fp.DocumentId] = docCounts = new(StringComparer.Ordinal);
            docCounts[region] = docCounts.GetValueOrDefault(region) + 1;
            fpRegionRows.Add(new { documentId = fp.DocumentId, identity = fp.Identity, region,
                evidenceBasis = region == "UNKNOWN_REGION" ? "NOT_OBSERVED" : "OFFLINE_REDERIVED_FROM_FROZEN_SOURCE_LAYOUT" });
        }
        Assert.Equal(487, regionCountsByDoc["SRC-095"].Values.Sum());
        Assert.Equal(0, regionCountsByDoc["SRC-095"].GetValueOrDefault("UNKNOWN_REGION"));
        var regionMarkers = src095Atoms.Where(a =>
            System.Text.RegularExpressions.Regex.IsMatch(a.Text, @"(?i)^(table of contents|1\.\s*introduction|12\.\s*references|appendix\s+[a-z]|[a-d]\.\s|index$)"))
            .Select(a => new { a.Alias, a.Page, a.Ordinal, a.Text }).ToArray();
        Write("region-analysis.v1.json", new
        {
            schemaVersion = "v5-semantic-error-region-v1", evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_SOURCE_LAYOUT",
            providerCalls = 0, goldRead = true, disposition = "DETERMINISTIC_WHERE_MARKERS_AVAILABLE",
            regions = new[] { "FRONT_MATTER", "TOC", "MAIN_BODY", "REFERENCES", "APPENDIX", "INDEX", "RUNNING_HEADER_FOOTER", "UNKNOWN_REGION" },
            note = "SRC-095 boundaries use explicit source-text markers and source atom order/page. Repeated text at a consistent row across at least three pages is separately tagged as running header/footer. Mixed or unsupported coordinates remain UNKNOWN_REGION. No current projection was run.",
            boundaries = new { toc = toc?.Ordinal, mainBody = bodyStart?.Ordinal, references = refsStart?.Ordinal, appendix = appendixStart?.Ordinal, index = indexStart?.Ordinal },
            falsePositiveCounts = regionCounts,
            falsePositiveCountsByDocument = regionCountsByDoc,
            falsePositiveRows = fpRegionRows,
            markerCandidates = regionMarkers
        });
        Write("historical-qualification-separation.v1.json", new
        {
            schemaVersion = "v5-semantic-error-qualification-separation-v1",
            providerResponses = 31, firstAttemptContractValid = 0, validPredictionResponses = 0,
            semanticEvaluationStatus = "NOT_EVALUABLE", reason = "EXECUTION_GATED_EMPTY_PREDICTION",
            historicalEmptyPrediction = new { tp = 0, fp = 0, fn = 139, semanticQualityEvidence = false,
                note = "Empty prediction follows 31 codec-invalid executions and is not a V5 semantic-quality estimate." },
            providerCalls = 0, goldRead = true, evidenceBasis = "FROZEN_HISTORICAL"
        });
        Write("summary.v1.json", new
        {
            schemaVersion = "v5-semantic-error-summary-v1", status = "P4_COMPLETE_OBSERVABILITY_BOUNDED", providerCalls = 0, goldRead = true,
            limitation = "304 FP source-content labels remain UNCLASSIFIED under conservative source-text rules; all rows and rationale are retained. The 32 SRC-089 FP rows remain UNKNOWN_REGION. This is an observability-bounded decomposition, not a graph/projection causal diagnosis.",
            official = new { tp = 82, fp = 519, fn = 57, precision = 0.1364, recall = 0.5899, f1 = 0.2216, evidenceBasis = "GOLD_COMPARISON" },
            observability = new { graph = "NOT_OBSERVED", projection = "NOT_OBSERVED" },
            fp = new { trueSemantic = 0, trueSemanticEvidenceStatus = "NOT_PROVABLE_FROM_EXACT_MATCH_GOLD_ALONE", spanOrIdentityNearMiss = fpMechanismCounts.Where(kv => kv.Key != "UNCLASSIFIED").Sum(kv => kv.Value), unclassified = fpContentCounts.GetValueOrDefault("FP_UNCLASSIFIED"), sourceContentTaxonomy = fpContentCounts },
            fn = new { noRawExactProposal = fnNoRawExact, rawNearOnly = fnRawNearOnly, contractLossProven = 0, binderLossProven = fnRawExact, boundNearOnly = fnBoundNearOnly, causeNotObservable = 0, evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW" },
            rawGoldCoverage = new { exact = exactGoldRaw, near = nearGoldRaw, absent = absentGoldRaw, exactRate = Math.Round(exactGoldRaw / 139d, 4), evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW_AND_GOLD" },
            binderImpact = new { goldExactRawAndBound, goldExactRawButBindingRefused, binderGoldLossProven = fnRawExact, binderGoldLossPotential = binderRefusedGoldNear.Count, binderRefusedNonGold, binderRefusedUnknown, bindingRefused, evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW_AND_FROZEN_BINDING_TOTALS" },
            contractImpact = new { accepted = contractAccepted, refused = contractRefused, refusedExactGold = 0, refusedNearGold = 0, refusedNonGold = 0, refusedUnclassifiable = 0, evidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW" },
            predicateEvidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_RAW_AND_GOLD",
            regionEvidenceBasis = "OFFLINE_REDERIVED_FROM_FROZEN_SOURCE_LAYOUT",
            largestMeasuredFpMechanism = fpContentCounts.OrderByDescending(kv => kv.Value).First().Key,
            largestMeasuredFnMechanism = new[] { (Name: "FN_NO_RAW_EXACT_PROPOSAL", Count: fnNoRawExact), (Name: "FN_RAW_NEAR_ONLY", Count: fnRawNearOnly), (Name: "FN_BOUND_NEAR_ONLY", Count: fnBoundNearOnly), (Name: "FN_RAW_EXACT_BINDING_REFUSED", Count: fnRawExact) }.OrderByDescending(x => x.Count).First().Name,
            largestPredicateFpContributor = predicateRows.OrderByDescending(p => p.nonGoldOccurrences).First().predicate,
            largestRegionFpContributor = regionCounts.OrderByDescending(kv => kv.Value).First().Key,
            historicalQualificationSemanticStatus = "NOT_EVALUABLE",
            fpAccountingExact = fpRows.Count == 519 && fpContentCounts.Values.Sum() == 519,
            fnAccountingExact = fnRows.Count == 57 && fnRawExact + fnBoundNearOnly + fnRawNearOnly + fnNoRawExact == 57
        });

        Assert.True(fpRows.Count == 519 && fnRows.Count == 57);
    }

    private static object Stage(string name, string status, string evidenceBasis, string source) =>
        new { stage = name, status, evidenceBasis, source };

    private static (string SemanticClass, string Mechanism, string Rationale) ClassifyFp(
        string identity, string text, IReadOnlyDictionary<string, string> gold)
    {
        var candidate = ParseIdentity(identity);
        var normalizedText = Normalize(text);
        foreach (var (goldIdentity, wording) in gold)
        {
            var goldParts = ParseIdentity(goldIdentity);
            var candidateAliases = candidate.Select(p => p.Alias).ToHashSet(StringComparer.Ordinal);
            var goldAliases = goldParts.Select(p => p.Alias).ToHashSet(StringComparer.Ordinal);
            var normalizedGold = Normalize(wording);
            if (candidateAliases.Count > 0 && candidateAliases.IsProperSubsetOf(goldAliases) && normalizedGold.Contains(normalizedText, StringComparison.Ordinal))
                return ("FP_SPLIT_HEADING_COMPONENT", "SPLIT_GOLD_OCCURRENCE",
                    "The candidate uses a strict subset of the Gold occurrence aliases and its text is contained in Gold wording.");
            if (candidate.Any(c => goldParts.Any(g => c.Alias == g.Alias && c.Start < g.End && g.Start < c.End)))
                return ("FP_PARTIAL_HEADING_FRAGMENT", "PARTIAL_OVERLAP_WITH_GOLD",
                    "The predicted span overlaps a canonical Gold span on the same source alias but is not an exact occurrence identity.");
            if (string.Equals(normalizedText, normalizedGold, StringComparison.Ordinal))
                return ("FP_UNCLASSIFIED", "SAME_TEXT_WRONG_OCCURRENCE",
                    "Text matches canonical Gold wording but the exact source identity differs; semantic validity is not inferred.");
        }
        return ("FP_UNCLASSIFIED", "UNCLASSIFIED",
            "No deterministic exact-span overlap, strict Gold-alias subset, or same-text match was established; exact-match Gold alone cannot prove semantic falsity.");
    }

    private static string ClassifyFpContent(string text, string region, string exactIdentityClass)
    {
        if (exactIdentityClass is "FP_PARTIAL_HEADING_FRAGMENT" or "FP_SPLIT_HEADING_COMPONENT") return exactIdentityClass;
        if (region == "TOC") return "FP_NAVIGATION_TOC";
        if (region == "RUNNING_HEADER_FOOTER") return "FP_RUNNING_HEADER_FOOTER";
        if (region is "REFERENCES" or "INDEX") return "FP_REFERENCE_OR_INDEX_ENTRY";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)^\s*(figure|table)\s+[a-z0-9.:-]+")) return "FP_CAPTION_OR_LABEL";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*[•▪◦‣]\s*") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)^\s*\d+\.\s+(to|for|if|when|each|all|any|the following)\b")) return "FP_LIST_ITEM_OR_CLAUSE";
        if (text.Contains('|') || System.Text.RegularExpressions.Regex.IsMatch(text, @"\S\s{4,}\S")) return "FP_TABLE_OR_SCHEMA_CONTENT";
        if (region == "FRONT_MATTER" && System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)(rfc\s*\d+|request for comments|internet engineering task force|published|obsoletes|updates)")) return "FP_DOCUMENT_METADATA";
        if (region == "MAIN_BODY" && text.Length >= 50 && System.Text.RegularExpressions.Regex.IsMatch(text.TrimEnd(), @"[.!?;:]$")) return "FP_BODY_SENTENCE";
        if (region == "MAIN_BODY" && System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*\d+(?:\.\d+)*\.\s+.{1,90}$")) return "FP_OTHER_STRUCTURAL_TEXT";
        return "FP_UNCLASSIFIED";
    }

    private static string ContentRule(string contentClass) => contentClass switch
    {
        "FP_NAVIGATION_TOC" => "Source atom falls between explicit Table of Contents and main-body markers.",
        "FP_REFERENCE_OR_INDEX_ENTRY" => "Source atom falls in explicit References or Index region.",
        "FP_RUNNING_HEADER_FOOTER" => "Normalized source text repeats at the same row on at least three pages.",
        "FP_PARTIAL_HEADING_FRAGMENT" => "Predicted source span overlaps but does not equal a Gold span.",
        "FP_SPLIT_HEADING_COMPONENT" => "Candidate alias set is a strict subset of a multi-part Gold occurrence and wording is contained.",
        "FP_CAPTION_OR_LABEL" => "Source text matches an explicit Figure/Table label pattern.",
        "FP_TABLE_OR_SCHEMA_CONTENT" => "Source text contains a table delimiter or repeated column whitespace.",
        "FP_LIST_ITEM_OR_CLAUSE" => "Source text begins with a deterministic bullet marker or enumerated directive pattern.",
        "FP_DOCUMENT_METADATA" => "Front-matter text matches a named RFC/publication metadata marker.",
        "FP_BODY_SENTENCE" => "Main-body atom is at least 50 characters and ends in sentence punctuation.",
        "FP_OTHER_STRUCTURAL_TEXT" => "Main-body atom matches a short numeric structural label pattern.",
        _ => "Available source facts did not satisfy a conservative deterministic content rule; retained as unclassified."
    };

    private static string ClassifyRegion(SemanticSourceAtom atom, SemanticSourceAtom? toc, SemanticSourceAtom? body,
        SemanticSourceAtom? references, SemanticSourceAtom? appendix, SemanticSourceAtom? index, HashSet<string> repeatedText)
    {
        if (repeatedText.Contains(Normalize(atom.Text))) return "RUNNING_HEADER_FOOTER";
        if (index is not null && atom.Ordinal >= index.Ordinal) return "INDEX";
        if (appendix is not null && atom.Ordinal >= appendix.Ordinal && (index is null || atom.Ordinal < index.Ordinal)) return "APPENDIX";
        if (references is not null && atom.Ordinal >= references.Ordinal && (appendix is null || atom.Ordinal < appendix.Ordinal)) return "REFERENCES";
        if (body is not null && atom.Ordinal >= body.Ordinal && (references is null || atom.Ordinal < references.Ordinal)) return "MAIN_BODY";
        if (toc is not null && atom.Ordinal >= toc.Ordinal && (body is null || atom.Ordinal < body.Ordinal)) return "TOC";
        if (toc is not null && atom.Ordinal < toc.Ordinal) return "FRONT_MATTER";
        return "UNKNOWN_REGION";
    }

    private static (string Alias, int Start, int End)[] ParseIdentity(string identity) => identity.Split('|').Select(part =>
    {
        var colon = part.LastIndexOf(':');
        var range = part[(colon + 1)..].Split('-');
        return (part[..colon], int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture));
    }).ToArray();

    private static string Normalize(string value) => System.Text.RegularExpressions.Regex.Replace(value, "\\s+", " ").Trim().ToUpperInvariant();

    private static double TokenOverlap(string a, string b)
    {
        static HashSet<string> Tokens(string value) => System.Text.RegularExpressions.Regex
            .Matches(value.ToUpperInvariant(), "[\\p{L}\\p{N}]+")
            .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var left = Tokens(a);
        var right = Tokens(b);
        return left.Count == 0 || right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / right.Count;
    }

    private static void Write(string name, object value)
    {
        var path = TestRepository.Path($"{P4Root}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
