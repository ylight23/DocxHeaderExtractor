using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.4 over PDF_SOURCE_FACTS_V3 (unchanged, frozen at 81bcb33), frozen before its held-out source
/// (089_ND_195-2013_Luat_Xuat_ban_EN, an English translation of a Vietnamese decree - a legal genre no development
/// document has) is pre-registered. V1.4 is the last development cycle before the final verdict (user, 2026-09-26).
/// A change to anything pinned here after this point is a new version.
/// </summary>
public sealed class GenericAuditEngineV14FreezeTests
{
    internal const string HeldOut = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string FactsFreeze = "eval/a99-closed-loop/pdf-source-facts-v3/PDF_SOURCE_FACTS_V3.freeze.json";

    [Fact]
    public void Freeze_generic_audit_engine_v1_4()
    {
        var rows = GenericAuditEngineV14Tests.Documents.Select(d =>
        {
            var id = (string)d[0];
            var path = $"{GenericAuditEngineV14Tests.Root}/{id}.dev-score.v1_4.json";
            using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
            double F1(string key) => score.RootElement.GetProperty(key).GetProperty("headline").GetProperty("f1").GetDouble();
            return new
            {
                documentId = id,
                v1_3_overFactsV3 = F1("v1_3_overFactsV3"),
                v1_4_overFactsV3 = F1("v1_4_overFactsV3"),
                artifact = new { path, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(path)) },
            };
        }).ToArray();
        Assert.All(rows, r => Assert.True(r.v1_4_overFactsV3 >= r.v1_3_overFactsV3, r.documentId));

        FreezeArtifact.AssertJson(GenericAuditEngineV14Tests.Root, "GENERIC_AUDIT_ENGINE_V1.4.freeze.json", new
        {
            artifactKind = "a99_generic_audit_engine_freeze",
            engineIdentity = GenericAuditEngineV14Tests.EngineIdentity(),
            sourceFacts = new { path = FactsFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(FactsFreeze)), status = "PDF_SOURCE_FACTS_V3, unchanged" },
            decidedBy = "user, 2026-09-26: fix first - V1.4 fixes only the five generic families SRC-095 classified as B_NEW (c587694); the seven A cases (index group letters) stay in review; no text, document, Gold count or page special case; the last development cycle before the final verdict",
            gaps = new[]
            {
                "23 an assembled label is judged whole: a section number set apart from its title in its own segment is a part of the label - for the figures-only test, for the region scan (the next such row is a peer), and for the furniture test (such a number at a page's foot is no page number)",
                "24 cross-reference lists: a line of references separated by semicolons is no caption, no contents line (its last number is a label's) and no label, even where a bold reference begins it",
                "25 contact blocks: a short unnumbered label directly under a stronger label, over nothing but short lines among them a contact line, names an address block: metadata",
            },
            families = new[]
            {
                new { family = "SPLIT_NUMBERED_HEADING", gap = 23 },
                new { family = "INDEX_LOCATOR_WRAP", gap = 24 },
                new { family = "INDEX_ENTRY_VS_TOC_POINTER", gap = 24 },
                new { family = "CROSS_REFERENCE_VS_HEADING", gap = 24 },
                new { family = "AUTHOR_CONTACT_BLOCK", gap = 25 },
            },
            leftToReviewByDesign = new[]
            {
                "labels no glyph fact sets apart whose heading status is a meaning decision (SRC-095 index group letters, S095_Q2) keep the review fail-safe",
                "an identically set label directly under another (sibling or child) keeps the review fail-safe",
                "italic labels and colon-ended labels (meaning decisions, bucket A)",
            },
            development = new
            {
                role = "DEVELOPMENT - every document here was developed on or revealed (SRC-095 since c587694); nothing is a generalization result",
                gold = "each document's current authored Gold; SRC-044 R2 (203) and SRC-054 R3 (298), user-approved 2026-09-26 (cb9420c) before V1.4 was scored",
                rows,
                noRegression = "V1.4 is at or above V1.3 on every development document (asserted above)",
            },
            heldOut = new
            {
                path = HeldOut,
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(HeldOut)),
                status = "chosen by the user before this freeze; no Gold, count or occurrence list of this PDF exists; its headings were not inspected",
                exposure = new[]
                {
                    "the PDF_SOURCE_FACTS_V3 audit (81bcb33) covered it as one of 98 PDFs: atom universe unchanged, 582 lines, median size 12, 47 lines with bold evidence - read once, to check that the facts can represent it (not an SRC-053-type source)",
                    "converted DOCX copies of the same text were DEV items of earlier LLM campaigns (DOC-0079, DOC-0269); no engine version ever ran on them",
                    "a count is listed beside its name in handoff.md (origin unknown); it is neither engine input nor target",
                    "an outline JSON of it exists (todo10_8/outline_json); it is not opened",
                },
            },
            modelProviderVlmCalls = 0,
        });
    }
}
