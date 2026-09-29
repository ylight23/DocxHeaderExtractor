using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The second post-migration correction DOC-0252's structured occurrence Gold has received.
/// <para>
/// ITEM-CCE2C592 - "Agenda", identity <c>L0519:S0:0-6</c> - was carried forward from the original
/// 41-claim migration and reused unchanged by the ITEM-505430BB correction (R1 to R2) as a
/// <c>SectionHeading</c> claim (STRUCTURAL_UNIT). A human re-review on 2026-09-23, informed by
/// textual, structural, and visual (VLM) evidence gathered across the context-ablation, text-
/// adjudicator, and VLM-escalation lineages, determined this occurrence primarily identifies the
/// embedded "Agenda" artifact itself - not an internal structural parent of the DAY/SESSION content
/// beneath it. The DAY/SESSION occurrences remain the structural hierarchy inside that artifact;
/// only the label naming the artifact changes role. This is a pure reclassification (0 added, 0
/// removed, 1 changed), not a membership change, so it is its own correction layered on top of the
/// R2 correction rather than an edit to it.
/// </para>
/// <para>
/// Every experiment that ran before this correction pins the R2 vintage explicitly (see
/// <see cref="HistoricalGoldVintages.Doc0252R2Path"/>) rather than reading whatever DOC-0252
/// resolves to now; their frozen conclusions remain correct descriptions of what they scored under
/// R2 and are not rewritten. ITEM-505430BB's own DOCUMENT_LABEL classification, established by the
/// prior correction, is untouched by this one.
/// </para>
/// </summary>
public sealed class Doc0252GoldCorrectionAgendaIdentityR3Tests
{
    private const string OutputRoot = "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence";
    private const string OutputName = "DOC-0252.structured-source-parts.occurrence-gold.v1.json";
    private const int ClaimCount = 42;

    /// <summary>The 42-claim structured Gold (R2) this correction is layered on top of, pinned by path and hash.</summary>
    private const string PredecessorPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/" +
        "DOC-0252.structured-source-parts.pre-agenda-identity-correction.occurrence-gold.v1.json";

    private const string PredecessorSha256 =
        "b4beacc4e758f858e785d7e37700fd04862f41726326159ec30721ad47e47ec0";

    private const string ItemId = "ITEM-CCE2C592";
    private const string ChangedIdentity = "L0519:S0:0-6";
    private const string OldSemanticRole = "SectionHeading";
    private const string NewSemanticRole = "DocumentTitle";

    [Fact]
    public void Agenda_is_reclassified_as_a_document_title_identity_claim_after_human_review()
    {
        var predecessorText = File.ReadAllText(
            TestRepository.Path(PredecessorPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(PredecessorSha256, CanonicalArtifactHash.OfText(predecessorText));
        using var predecessor = JsonDocument.Parse(predecessorText);
        var predecessorRoot = predecessor.RootElement;
        Assert.Equal(ClaimCount, predecessorRoot.GetProperty("semanticHeadingTotal").GetInt32());
        var predecessorRows = predecessorRoot.GetProperty("boundOccurrences").EnumerateArray()
            .Select(row => row.Clone()).ToArray();
        Assert.Equal(ClaimCount, predecessorRows.Length);

        // Exactly one claim carries this identity, and it is Agenda with its predecessor role -
        // this correction is a reclassification, not a membership change, so nothing here may add
        // or remove a row.
        var agendaPredecessor = Assert.Single(predecessorRows,
            row => row.GetProperty("identity").GetString() == ChangedIdentity);
        Assert.Equal(OldSemanticRole, agendaPredecessor.GetProperty("semanticRole").GetString());
        Assert.Equal("Agenda", agendaPredecessor.GetProperty("approvedWording").GetString());

        // ITEM-505430BB's own prior correction is untouched: same identity, same role, unaffected.
        var f1 = Assert.Single(predecessorRows,
            row => row.GetProperty("identity").GetString() == "L0513:S0:0-38|L0514:S0:0-30");
        Assert.Equal("DocumentTitle", f1.GetProperty("semanticRole").GetString());

        var rows = predecessorRows.Select(row =>
        {
            if (row.GetProperty("identity").GetString() != ChangedIdentity) return (object)row;

            var node = System.Text.Json.Nodes.JsonNode.Parse(row.GetRawText())!.AsObject();
            node["semanticRole"] = NewSemanticRole;
            return (object)JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
        }).ToArray();
        Assert.Equal(ClaimCount, rows.Length);

        FreezeArtifact.AssertJson(OutputRoot, OutputName, new
        {
            artifactKind = "a99_pdf_structured_occurrence_gold",
            schemaVersion = "a99-pdf-structured-occurrence-gold-v1",
            documentId = "DOC-0252",
            sourceSha256 = predecessorRoot.GetProperty("sourceSha256").GetString(),
            coordinateProfile = "STRUCTURED_SOURCE_PARTS",
            coordinateSystem = "STRUCTURED_SOURCE_PART_TUPLE",
            semanticHeadingTotal = ClaimCount,
            migration = new
            {
                kind = "HUMAN_APPROVED_GOLD_CORRECTION",
                correctedAt = "2026-09-23",
                predecessorCoordinateProfile = "STRUCTURED_SOURCE_PARTS",
                predecessorGoldSha256 = PredecessorSha256,
                semanticMembershipChanged = false,
                add = 0,
                remove = 0,
                changed = 1,
                needsReview = 0,
                itemId = ItemId,
                changedIdentity = ChangedIdentity,
                oldSemanticRole = OldSemanticRole,
                newSemanticRole = NewSemanticRole,
                oldExpectedLabel = "STRUCTURAL_UNIT",
                newExpectedLabel = "DOCUMENT_LABEL",
                abstractSemanticFunction = "IDENTITY",
                reason = "Human re-review, informed by textual, structural, and visual (VLM) evidence " +
                    "gathered across the context-ablation, text-adjudicator, and VLM-escalation " +
                    "lineages, determined this occurrence primarily identifies the embedded 'Agenda' " +
                    "artifact rather than serving as an internal structural parent of the content " +
                    "beneath it. The DAY/SESSION occurrences provide the structural hierarchy inside " +
                    "that artifact; only the label naming the artifact itself changes role. The " +
                    "decision is semantic-function based (IDENTITY vs. STRUCTURE), not a claim that " +
                    "any model or scorer proved the prior Gold classification wrong.",
                unaffectedControl = new
                {
                    itemId = "ITEM-505430BB",
                    identity = "L0513:S0:0-38|L0514:S0:0-30",
                    semanticRole = "DocumentTitle",
                    note = "Established by the prior correction (R1 to R2); unaffected by this one.",
                },
                priorMigration = predecessorRoot.GetProperty("migration").Clone(),
                note = "One claim's semantic role changed, none added, none removed, none " +
                    "re-adjudicated beyond Agenda itself. The 42 claims this correction is layered " +
                    "on top of are reused as-is except for Agenda's own row.",
            },
            boundOccurrences = rows,
        });
    }
}
