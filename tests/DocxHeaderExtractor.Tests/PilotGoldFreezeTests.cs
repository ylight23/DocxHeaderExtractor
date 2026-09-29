using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0255, DOC-0256 and DOC-0259 frozen as they stand, by the user's decision of 2026-09-24.
/// <para>
/// Their headings were chosen with qwen3.7-flash proposals among the evidence (DOC-0255 and DOC-0259
/// under the rule "accept what the model proposes as a document label or structure"; DOC-0256's count
/// settled after a harness run). Asked whether that makes them circular for a benchmark of the same
/// model, the user ruled the decisions final and asked for them to be frozen, not re-reviewed. The
/// hash pins each authored file: any edit fails here until this freeze is deliberately regenerated.
/// </para>
/// </summary>
public sealed class PilotGoldFreezeTests
{
    private static readonly string[] Frozen = ["DOC-0255", "DOC-0256", "DOC-0259"];

    [Fact]
    public void The_three_pilot_gold_files_are_frozen()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/gold-freeze", "pilot-gold-freeze.v1.json", new
        {
            artifactKind = "a99_gold_freeze",
            decidedBy = "USER",
            decidedAt = "2026-09-24",
            decision = "final as they stand; freeze, do not re-review",
            modelAssistedEvidence = "qwen3.7-flash proposals were among the evidence for these headings; the user accepted that",
            documents = Frozen.Select(id =>
            {
                var path = $"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json";
                using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
                return new
                {
                    documentId = id,
                    path,
                    sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(path)),
                    semanticHeadingTotal = gold.RootElement.GetProperty("semanticHeadingTotal").GetInt32(),
                    claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").GetArrayLength(),
                };
            }).ToArray(),
        });
    }
}
