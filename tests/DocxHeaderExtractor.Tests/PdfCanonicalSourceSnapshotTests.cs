using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6S-A: the canonical snapshot is the qualification lane's cross-platform source authority.
/// Updating it is deliberately explicit: the capture host must first prove live-parser parity;
/// ordinary CI only replays the committed authority and never makes PdfPig output authoritative.
/// </summary>
public sealed class PdfCanonicalSourceSnapshotTests
{
    private const string Root = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private static readonly (string Id, string Pdf, int Packs)[] Documents =
    [
        ("DOC-0252", SourcePdfCorpus.Doc0252, 7),
        ("DOC-0256", SourcePdfCorpus.Doc0256, 6),
        ("SRC-041", SourcePdfCorpus.Src041, 109),
        ("SRC-089", SourcePdfCorpus.Src089, 7),
        ("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void P6SA_compact_snapshot_round_trips_exact_candidate_and_p05_authority()
    {
        var rows = new List<object>();
        var totalAtoms = 0;
        foreach (var spec in Documents)
        {
            var path = TestRepository.Path(spec.Pdf);
            var sourceSha = CanonicalSemanticSourceHash.Compute(path);
            PdfSourceBuildResult? live = null;
            PdfCanonicalSourceSnapshotV1 snapshot;
            if (FreezeArtifact.UpdateRequested)
            {
                live = PdfSourceAdapter.BuildWithDetails(path);
                snapshot = PdfCanonicalSourceSnapshotV1.From(live);
                Assert.Equal(sourceSha, live.Snapshot.SourceSha256);
                FreezeArtifact.AssertJson(Root, $"{sourceSha}.json", snapshot);
            }

            var frozenPath = Path.Combine(TestRepository.Root(), Root.Replace('/', Path.DirectorySeparatorChar), $"{sourceSha}.json");
            var persisted = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(File.ReadAllText(frozenPath), FreezeArtifact.Json);
            Assert.NotNull(persisted);
            var replay = persisted!.Rehydrate();
            totalAtoms += replay.Atoms.Count;
            Assert.Equal(sourceSha, replay.SourceSha256);
            if (live is not null)
            {
                Assert.Equal(live.Snapshot.SourceAliasUniverseHash, replay.SourceAliasUniverseSha256);
                Assert.Equal(live.Snapshot.ModelVisibleEvidenceHash, replay.ModelVisibleEvidenceSha256);
                Assert.Equal(live.Snapshot.Atoms.Select(AtomIdentity), replay.Atoms.Select(AtomIdentity));
                Assert.Equal(live.Details.LayoutBlockByAtom.OrderBy(item => item.Key), replay.LayoutBlockByAtom.OrderBy(item => item.Key));
            }

            var livePacks = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(live?.Snapshot.Evidence ?? replay.Evidence, live?.Details.LayoutBlockByAtom ?? replay.LayoutBlockByAtom);
            var replayPacks = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(replay.Evidence, replay.LayoutBlockByAtom);
            Assert.Equal(spec.Packs, livePacks.Count); Assert.Equal(livePacks.Count, replayPacks.Count);
            Assert.Equal(livePacks.Select(PackIdentity), replayPacks.Select(PackIdentity));

            var liveAtoms = (live?.Snapshot.Atoms ?? replay.Atoms).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var replayAtoms = replay.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var candidateRows = new List<object>();
            foreach (var pair in livePacks.Zip(replayPacks))
            {
                var liveUniverse = V5CandidateUniverseV1.Build(pair.First.Owned.Select(item => liveAtoms[item.SourceAlias]).ToArray(), live?.Snapshot.Atoms ?? replay.Atoms, V5CandidatePolicyV1.Default);
                var replayUniverse = V5CandidateUniverseV1.Build(pair.Second.Owned.Select(item => replayAtoms[item.SourceAlias]).ToArray(), replay.Atoms, V5CandidatePolicyV1.Default);
                Assert.Equal(liveUniverse.Fingerprint, replayUniverse.Fingerprint);
                Assert.Equal(liveUniverse.Candidates.Select(CandidateIdentity), replayUniverse.Candidates.Select(CandidateIdentity));
                Assert.Equal(liveUniverse.Relations.Select(RelationIdentity), replayUniverse.Relations.Select(RelationIdentity));
                candidateRows.Add(new { pack = pair.First.PackId, fingerprint = liveUniverse.Fingerprint, candidates = liveUniverse.Candidates.Count, relations = liveUniverse.Relations.Count });
            }
            rows.Add(new { spec.Id, sourceSha256 = replay.SourceSha256, atoms = replay.Atoms.Count, evidence = replay.Evidence.Count, packs = livePacks.Count, candidatePacks = candidateRows });
        }
        Assert.Equal(14_523, totalAtoms);
        FreezeArtifact.AssertJson(Root, "replay-parity.v1.json", new
        {
            schemaVersion = "p6s-canonical-source-snapshot-replay-v1",
            authority = "compact sourceSha256 + atoms + materialized evidence + layoutBlockByAtom",
            // This rollup freezes the original capture authority's historical builder identity;
            // the live implementation has since been moved behind PdfSourceAdapter.
            captureParity = "A99_FREEZE_UPDATE capture host: live PdfStructuredSourceAuthorityBuilder equals serialized snapshot rehydrate",
            ciReplay = "ordinary runs rehydrate only; raw PdfPig drift is intentionally not a second authority",
            providerCalls = 0, goldRead = false, sharedRuntime = "UNCHANGED", documents = rows,
        });
    }

    private static object AtomIdentity(SemanticSourceAtom atom) => new { atom.Alias, atom.SourceId, atom.Ordinal, atom.Page, atom.Row, atom.Segment, atom.Text };
    private static string PackIdentity(SemanticEvidencePack pack) => $"{pack.PackId}|{string.Join(',', pack.Owned.Select(item => item.SourceAlias))}|{string.Join(',', pack.Visible.Select(item => item.SourceAlias))}";
    private static string CandidateIdentity(V5IssuedCandidateV1 item) => $"{item.Id}|{item.Kind}|{item.SpanIdentity}";
    private static string RelationIdentity(V5IssuedRelationV1 item) => $"{item.Id}|{item.CandidateId}|{item.TargetSpanIdentity}|{item.MatchTier}";
}
