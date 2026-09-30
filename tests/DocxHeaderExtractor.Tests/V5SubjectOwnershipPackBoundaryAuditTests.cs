using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Audits the frozen P4 ownership refusals across overlapping source packs.  This is deliberately an
/// offline provenance audit: it reads only frozen requests/responses and canonical Gold, and does not
/// change the fail-closed ownership rule or send a provider request.
/// </summary>
public sealed class V5SubjectOwnershipPackBoundaryAuditTests
{
    private const string CohortRoot = "artifacts/v5-provider-cohort-31-windows";
    private const string P4PopulationPath = "artifacts/v5-semantic-error-decomposition/population.v1.json";
    private const string AuditRoot = "artifacts/v5-subject-ownership-pack-boundary-audit";
    private static readonly HashSet<string> OccurrencePredicates = new(StringComparer.Ordinal)
        { "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION" };

    [Fact]
    public void Freeze_subject_ownership_and_pack_boundary_audit()
    {
        using var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CohortRoot}/preflight/cohort.v1.json")));
        var packRows = cohort.RootElement.GetProperty("rows").EnumerateArray().Select(PackRow.FromJson).ToArray();
        Assert.Equal(31, packRows.Length);
        var byPack = packRows.ToDictionary(row => (row.DocumentId, row.PackId));

        var atomsByDocument = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> Atoms(string documentId)
        {
            if (!atomsByDocument.TryGetValue(documentId, out var atoms))
            {
                var source = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                atomsByDocument[documentId] = atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(source));
            }
            return atoms;
        }

        var snapshots = new Dictionary<(string DocumentId, string PackId), PackSnapshot>();
        var allBoundOccurrences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["SRC-089"] = new(StringComparer.Ordinal), ["SRC-095"] = new(StringComparer.Ordinal)
        };
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        foreach (var row in packRows)
        {
            var callDirectory = Directory.GetDirectories(TestRepository.Path($"{CohortRoot}/provider/calls"))
                .Single(directory => Path.GetFileName(directory).EndsWith($"-{row.DocumentId}-{PackSuffix(row.PackId)}", StringComparison.Ordinal));
            using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDirectory, "content.txt")));
            var quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(raw.RootElement, contract);
            var binding = ExactClaimBinderV2_1.Bind(row.PackId, quarantine.Eligible, Atoms(row.DocumentId),
                ClaimBindingScope.Create(row.OwnedAliases, row.VisibleAliases));
            var snapshot = new PackSnapshot(row, quarantine.Eligible, binding);
            snapshots.Add((row.DocumentId, row.PackId), snapshot);
            foreach (var bound in binding.Bound.Where(bound => OccurrencePredicates.Contains(bound.Claim.Predicate)))
                allBoundOccurrences[row.DocumentId].Add(AliasIdentity(bound.Claim.Subject.Parts));
        }
        Assert.Equal(1203, snapshots.Values.Sum(snapshot => snapshot.Binding.Bound.Count));

        using var p4 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P4PopulationPath)));
        var p4Refusals = p4.RootElement.GetProperty("bindingRefusals").EnumerateArray()
            .Where(row => row.GetProperty("refusalReason").GetString()!.StartsWith("subject-alias-not-owned:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(85, p4Refusals.Length);

        var goldByDocument = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var documentId in new[] { "SRC-089", "SRC-095" })
        {
            using var gold = CanonicalGoldRegistry.Resolve(documentId);
            goldByDocument[documentId] = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("identity").GetString()!).ToHashSet(StringComparer.Ordinal);
        }

        var rows = new List<AuditRow>();
        foreach (var p4Refusal in p4Refusals)
        {
            var documentId = p4Refusal.GetProperty("documentId").GetString()!;
            var refusedPackId = p4Refusal.GetProperty("packId").GetString()!;
            var rawOrdinal = p4Refusal.GetProperty("rawOrdinal").GetInt32();
            var refusedSnapshot = snapshots[(documentId, refusedPackId)];
            var proposal = refusedSnapshot.Eligible.Single(candidate => candidate.OriginalOrdinal + 1 == rawOrdinal).Proposal;
            var alias = p4Refusal.GetProperty("refusalReason").GetString()!["subject-alias-not-owned:".Length..];
            var owner = packRows.Single(row => row.DocumentId == documentId && row.OwnedAliases.Contains(alias, StringComparer.Ordinal));
            var ownerSnapshot = snapshots[(documentId, owner.PackId)];
            var bodyEvidence = InspectFrozenBody(refusedSnapshot.Row, alias);
            var atoms = Atoms(documentId);
            var atom = atoms.Single(atom => atom.Alias == alias);
            var ownedOrdinals = refusedSnapshot.Row.OwnedAliases.Select(a => atoms.Single(atom => atom.Alias == a).Ordinal).ToArray();
            var haloSide = atom.Ordinal < ownedOrdinals.Min() ? "LEFT_HALO" : atom.Ordinal > ownedOrdinals.Max() ? "RIGHT_HALO" : "NOT_HALO";
            var sourceBinding = SemanticSourcePartBinder.Bind(atoms, ProviderSourcePartNormalization.ToCanonical(proposal.Subject.SourceParts));
            var sourceIdentity = sourceBinding.IsBound ? sourceBinding.Identity : null;
            var isExactGoldOccurrence = OccurrencePredicates.Contains(proposal.Predicate) && sourceIdentity is not null && goldByDocument[documentId].Contains(sourceIdentity);
            var ownerClaims = ownerSnapshot.Eligible.Where(candidate => candidate.Proposal.Subject.SourceParts.Any(part => part.SourceAlias == alias)).ToArray();
            var ownerBound = ownerSnapshot.Binding.Bound.Where(bound => bound.Claim.Subject.Parts.Any(part => part.Alias == alias)).ToArray();
            var retained = sourceIdentity is not null && allBoundOccurrences[documentId].Contains(sourceIdentity);
            var outcome = !isExactGoldOccurrence
                ? "NON_GOLD_OR_RELATION_NOT_EVALUABLE"
                : retained ? "GOLD_RETAINED_BY_BOUND_OCCURRENCE"
                : ownerClaims.Length == 0 ? "CROSS_PACK_GOLD_LOSS_OWNER_OMITTED"
                : "CROSS_PACK_GOLD_LOSS_OWNER_UNBOUND";
            rows.Add(new AuditRow(documentId, refusedPackId, rawOrdinal, alias, proposal.Predicate, sourceIdentity,
                owner.PackId, haloSide, bodyEvidence.InContextOnly, bodyEvidence.InSubjectEvidence,
                ownerClaims.Length > 0, ownerBound.Length > 0,
                ownerClaims.Select(candidate => candidate.Proposal.Predicate).Distinct(StringComparer.Ordinal).Order().ToArray(),
                ownerBound.Select(bound => bound.Claim.Predicate).Distinct(StringComparer.Ordinal).Order().ToArray(),
                isExactGoldOccurrence, retained, outcome));
        }

        Assert.Equal(85, rows.Count);
        Assert.All(rows, row => Assert.True(row.InContextOnlyEvidence && !row.InSubjectEvidence));
        Assert.All(rows, row => Assert.True(row.HaloSide is "LEFT_HALO" or "RIGHT_HALO"));
        var exactGoldRefusalRows = rows.Where(row => row.IsExactGoldOccurrence).ToArray();
        Assert.Equal(10, exactGoldRefusalRows.Length);
        var exactGoldRefusalOccurrences = exactGoldRefusalRows.Select(row => row.DocumentId + ":" + row.SourceIdentity).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(9, exactGoldRefusalOccurrences.Length);
        var losses = rows.Where(row => row.Outcome.StartsWith("CROSS_PACK_GOLD_LOSS_", StringComparison.Ordinal))
            .GroupBy(row => row.DocumentId + ":" + row.SourceIdentity, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, losses.Length);
        Assert.Contains(losses, loss => loss.Key.EndsWith(":L1472:S0:0-19", StringComparison.Ordinal) && loss.All(row => row.Outcome == "CROSS_PACK_GOLD_LOSS_OWNER_OMITTED"));
        Assert.Contains(losses, loss => loss.Key.EndsWith(":L1710:S0:0-21", StringComparison.Ordinal) && loss.All(row => row.Outcome == "CROSS_PACK_GOLD_LOSS_OWNER_UNBOUND"));

        var uniqueAliasRows = rows.GroupBy(row => (row.DocumentId, row.RefusedPackId, row.SubjectAlias)).Select(group =>
        {
            var first = group.First();
            return new
            {
                documentId = first.DocumentId,
                refusedPackId = first.RefusedPackId,
                subjectAlias = first.SubjectAlias,
                ownerPackId = first.OwnerPackId,
                haloSide = first.HaloSide,
                haloClaims = group.Select(row => new { rawOrdinal = row.RawOrdinal, predicate = row.Predicate, sourceIdentity = row.SourceIdentity }).ToArray(),
                ownerClaimed = group.Any(row => row.OwnerClaimed),
                ownerBound = group.Any(row => row.OwnerBound),
                ownerPredicates = group.SelectMany(row => row.OwnerPredicates).Distinct(StringComparer.Ordinal).Order().ToArray(),
                ownerBoundPredicates = group.SelectMany(row => row.OwnerBoundPredicates).Distinct(StringComparer.Ordinal).Order().ToArray(),
                outcomes = group.Select(row => row.Outcome).Distinct(StringComparer.Ordinal).Order().ToArray()
            };
        }).OrderBy(row => row.documentId).ThenBy(row => row.refusedPackId).ThenBy(row => row.subjectAlias).ToArray();

        Write("ownership-refusals.v1.json", new
        {
            schemaVersion = "v5-subject-ownership-refusals-v1", providerCalls = 0, goldRead = true,
            evidenceBasis = "FROZEN_HISTORICAL_PLUS_OFFLINE_REDERIVATION",
            scope = "All P4 subject-alias-not-owned binder refusals; no provider, runtime, binder, graph, or projection execution.",
            refusalClaims = rows.Count, refusalTaxonomyCount = 85,
            allRefusalsWereVisibleContextOnly = rows.All(row => row.InContextOnlyEvidence && !row.InSubjectEvidence),
            rows = rows.OrderBy(row => row.DocumentId).ThenBy(row => row.RefusedPackId).ThenBy(row => row.RawOrdinal)
                .Select(RowJson).ToArray()
        });
        Write("pack-boundary-matrix.v1.json", new
        {
            schemaVersion = "v5-subject-ownership-pack-boundary-matrix-v1", providerCalls = 0, goldRead = true,
            note = "One row per refused pack/subject alias. Owner claims/bindings are independently rederived from the frozen owner response.",
            rows = uniqueAliasRows
        });
        Write("summary.v1.json", new
        {
            schemaVersion = "v5-subject-ownership-pack-boundary-summary-v1", status = "P5A_COMPLETE_OFFLINE",
            providerCalls = 0, goldRead = true, refusalClaims = rows.Count, refusedPackAliasPairs = uniqueAliasRows.Length,
            contextOnlyNotSubjectEvidence = rows.Count(row => row.InContextOnlyEvidence && !row.InSubjectEvidence),
            byHaloSide = rows.GroupBy(row => row.HaloSide).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            exactGoldRefusalClaims = exactGoldRefusalRows.Length, exactGoldRefusalOccurrences = exactGoldRefusalOccurrences.Length,
            retainedExactGoldOccurrences = exactGoldRefusalOccurrences.Length - losses.Length,
            crossPackGoldLossProven = losses.Length,
            lossMechanisms = losses.OrderBy(loss => loss.Key).Select(loss => new { identity = loss.Key, outcome = loss.First().Outcome, refusedPackId = loss.First().RefusedPackId, ownerPackId = loss.First().OwnerPackId }).ToArray(),
            conclusion = "Ownership enforcement correctly fails closed. Two exact Gold losses are cross-pack ownership/provenance losses: one owner omission and one owner unbound retyping; this audit does not justify weakening the binder.",
            nextQuestion = "Any contract change must preserve deterministic harness ownership while preventing context-only aliases from being emitted as semantic claim subjects."
        });
    }

    private static object RowJson(AuditRow row) => new
    {
        documentId = row.DocumentId, refusedPackId = row.RefusedPackId, rawOrdinal = row.RawOrdinal,
        subjectAlias = row.SubjectAlias, predicate = row.Predicate, sourceIdentity = row.SourceIdentity,
        ownerPackId = row.OwnerPackId, haloSide = row.HaloSide,
        frozenRequestEvidence = new { contextOnlyEvidence = row.InContextOnlyEvidence, subjectEvidence = row.InSubjectEvidence },
        owner = new { claimed = row.OwnerClaimed, bound = row.OwnerBound, predicates = row.OwnerPredicates, boundPredicates = row.OwnerBoundPredicates },
        gold = new { exactOccurrence = row.IsExactGoldOccurrence, retainedByAnyBoundOccurrence = row.Retained },
        outcome = row.Outcome, evidenceBasis = "FROZEN_HISTORICAL_PLUS_OFFLINE_REDERIVATION"
    };

    private static (bool InContextOnly, bool InSubjectEvidence) InspectFrozenBody(PackRow row, string alias)
    {
        using var body = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CohortRoot}/preflight/{row.BodyFile}")));
        var content = body.RootElement.GetProperty("messages").EnumerateArray().Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        using var semanticRequest = JsonDocument.Parse(content);
        var packet = semanticRequest.RootElement.GetProperty("packet");
        bool Contains(string property) => packet.GetProperty(property).EnumerateArray()
            .Any(evidence => evidence.GetProperty("sourceAlias").GetString() == alias);
        return (Contains("contextOnlyEvidence"), Contains("subjectEvidence"));
    }

    private static string PackSuffix(string packId) => packId[(packId.LastIndexOf(':') + 1)..];

    private static string AliasIdentity(IEnumerable<BoundSourcePart> parts) => string.Join("|", parts.Select(part =>
        $"{part.Alias}:{part.Start}-{part.End}"));

    private static void Write(string name, object value)
    {
        var path = TestRepository.Path($"{AuditRoot}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }

    private sealed record PackRow(string DocumentId, string PackId, string BodyFile, IReadOnlyList<string> OwnedAliases, IReadOnlyList<string> VisibleAliases)
    {
        public static PackRow FromJson(JsonElement row) => new(row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!,
            row.GetProperty("bodyFile").GetString()!, row.GetProperty("ownedAliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray(),
            row.GetProperty("visibleAliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray());
    }

    private sealed record PackSnapshot(PackRow Row, IReadOnlyList<IndexedSemanticClaimProposalV2_1> Eligible, ClaimBindingResultV2_1 Binding);

    private sealed record AuditRow(string DocumentId, string RefusedPackId, int RawOrdinal, string SubjectAlias, string Predicate,
        string? SourceIdentity, string OwnerPackId, string HaloSide, bool InContextOnlyEvidence, bool InSubjectEvidence,
        bool OwnerClaimed, bool OwnerBound, IReadOnlyList<string> OwnerPredicates, IReadOnlyList<string> OwnerBoundPredicates,
        bool IsExactGoldOccurrence, bool Retained, string Outcome);
}
