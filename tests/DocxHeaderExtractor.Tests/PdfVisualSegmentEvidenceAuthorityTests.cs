using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The visual-segment evidence capability is deliberately tested separately from the active PDF
/// authority. These tests prove source identity, bounded block context, and request planning without
/// invoking a model or changing the canonical source universe.
/// </summary>
public sealed class PdfVisualSegmentEvidenceAuthorityTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string StructuredPartsArtifact =
        "eval/a99-closed-loop/representation/doc-0252-structured-source-parts.v1.json";

    [Fact]
    public void DOC0252_has_650_atom_owned_evidence_units_with_no_block_id_lookup()
    {
        var authority = Build();

        Assert.Equal(650, authority.Atoms.Count);
        Assert.Equal(650, authority.ContextsByAlias.Count);
        Assert.Equal(650, authority.ContextsBySourceId.Count);
        Assert.Equal(650, authority.Evidence.Count);
        Assert.Equal(650, authority.Atoms.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            authority.Atoms.Select(atom => atom.Alias),
            authority.Evidence.Select(unit => unit.Atom.Alias));
        Assert.All(authority.Evidence, unit =>
        {
            Assert.True(unit.ContextBlockId.Length > 0);
            Assert.DoesNotContain(unit.ContextBlockId, authority.ContextsByAlias.Keys);
            Assert.True(authority.ContextsByAlias.ContainsKey(unit.Atom.Alias));
            Assert.Same(authority.ContextsByAlias[unit.Atom.Alias],
                authority.ContextsBySourceId[unit.Atom.SourceId]);
        });
    }

    [Fact]
    public void Context_is_optional_metadata_and_does_not_change_atom_source_hash()
    {
        var legacy = Build(PdfBlockGrouping.LegacyV1);
        var continuation = Build(PdfBlockGrouping.ContinuationV2);

        Assert.Equal(legacy.SourceAliasUniverseSha256, continuation.SourceAliasUniverseSha256);
        Assert.Equal(legacy.Atoms.Select(atom => atom with { }), continuation.Atoms);
        Assert.NotEqual(legacy.ModelVisibleEvidenceSha256, continuation.ModelVisibleEvidenceSha256);
    }

    [Fact]
    public void S0616_style_cross_row_heading_is_available_as_two_source_atoms()
    {
        var authority = Build();
        var first = Assert.Single(authority.Atoms, atom => atom.Alias == "L0359:S0");
        var second = Assert.Single(authority.Atoms, atom => atom.Alias == "L0360:S0");

        Assert.Equal("L0359:S0", first.Alias);
        Assert.Equal("L0360:S0", second.Alias);
        Assert.Equal(SemanticSourceLocality.NextRowCompatible,
            SemanticSourcePartBinder.Locality(first, second));
        Assert.Equal(first.Text, authority.ContextsByAlias[first.Alias].Source.RawText);
        Assert.Equal(second.Text, authority.ContextsByAlias[second.Alias].Source.RawText);
    }

    [Fact]
    public void Structured_source_part_authority_still_represents_all_41_DOC0252_headings()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path(StructuredPartsArtifact)));
        var root = artifact.RootElement;
        var audit = root.GetProperty("audit");
        Assert.Equal(41, audit.GetProperty("approvedHeadings").GetInt32());
        Assert.Equal(41, audit.GetProperty("sourceHeadingsRepresentable").GetInt32());
        Assert.Equal(16, audit.GetProperty("goldTextMigrationRequired").GetInt32());
    }

    [Fact]
    public void Request_plan_is_atom_keyed_deterministic_and_schema_ready()
    {
        var authority = Build();
        var first = PdfVisualSegmentRequestPlan.Build(authority);
        var second = PdfVisualSegmentRequestPlan.Build(authority);

        Assert.Equal(6, first.Packs.Count);
        Assert.Equal(first.CallPlanHash, second.CallPlanHash);
        Assert.Equal(first.RequestPlanHash, second.RequestPlanHash);
        Assert.Equal(SemanticSourcePartsContract.SchemaHash(), first.SourcePartsSchemaHash);
        Assert.All(first.Packs, pack =>
        {
            Assert.NotEmpty(pack.OwnedAliases);
            Assert.NotEmpty(pack.VisibleAliases);
            Assert.DoesNotContain(pack.OwnedAliases, alias => alias.StartsWith("b", StringComparison.Ordinal));
            Assert.Contains(SemanticSourcePartsContract.ProtocolVersion, pack.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("sourcePartsSchemaHash", pack.PayloadJson, StringComparison.Ordinal);
        });
        Assert.Equal(
            authority.Atoms.Select(atom => atom.Alias),
            first.Packs.SelectMany(pack => pack.OwnedAliases));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            sourceSha256 = authority.SourceSha256,
            segmentAtoms = authority.Atoms.Count,
            evidenceUnits = authority.Evidence.Count,
            sourceAliasUniverseSha256 = authority.SourceAliasUniverseSha256,
            modelVisibleEvidenceSha256 = authority.ModelVisibleEvidenceSha256,
            contextPacks = first.Packs.Count,
            primaryCallsPerRepeat = first.Packs.Count,
            totalPrimaryCalls = first.Packs.Count,
            callPlanHash = first.CallPlanHash,
            requestPlanHash = first.RequestPlanHash,
            sourcePartsSchemaHash = first.SourcePartsSchemaHash,
        }));
    }

    [Fact]
    public void Rebuilding_the_shadow_authority_is_deterministic()
    {
        var first = Build();
        var second = Build();

        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(first.SourceAliasUniverseSha256, second.SourceAliasUniverseSha256);
        Assert.Equal(first.ModelVisibleEvidenceSha256, second.ModelVisibleEvidenceSha256);
        Assert.Equal(
            first.Atoms.Select(atom => (atom.Alias, atom.SourceId, atom.Ordinal, atom.Page, atom.Row,
                atom.Segment, atom.Text)),
            second.Atoms.Select(atom => (atom.Alias, atom.SourceId, atom.Ordinal, atom.Page, atom.Row,
                atom.Segment, atom.Text)));
        Assert.Equal(
            first.Evidence.Select(unit => (unit.Atom.Alias, unit.ContextBlockId, unit.StructuralScope,
                unit.CandidateAttention, unit.ObservedEvidence.ToArray(), unit.LocalBefore.ToArray(),
                unit.LocalAfter.ToArray())),
            second.Evidence.Select(unit => (unit.Atom.Alias, unit.ContextBlockId, unit.StructuralScope,
                unit.CandidateAttention, unit.ObservedEvidence.ToArray(), unit.LocalBefore.ToArray(),
                unit.LocalAfter.ToArray())));
    }

    private static PdfVisualSegmentEvidenceAuthority Build(
        PdfBlockGrouping grouping = PdfBlockGrouping.LegacyV1) =>
        PdfVisualSegmentEvidenceAuthority.Build(Path(Pdf), grouping);

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(),
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
