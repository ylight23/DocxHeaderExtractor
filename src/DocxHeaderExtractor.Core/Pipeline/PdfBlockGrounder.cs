namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfGroundedBlockHeading(
    string Id,
    int Page,
    int VisualLevel,
    string Text,
    string SourceText,
    string CanonicalText,
    double Confidence,
    string Evidence);

internal sealed record PdfRejectedBlockHeading(
    string Id,
    string Role,
    double Confidence,
    string Reason);

internal sealed record PdfBlockGroundingResult(
    IReadOnlyList<PdfGroundedBlockHeading> Headings,
    IReadOnlyList<PdfRejectedBlockHeading> Rejected);

/// <summary>
/// Grounds LLM block roles back to deterministic PDF blocks. The analyst may assign semantics, but
/// accepted candidates must still exist as extracted blocks, keep their original text, use a visual
/// candidate style, and avoid clusters that the cluster analyst explicitly called table/chart labels.
/// </summary>
internal static class PdfBlockGrounder
{
    public static PdfBlockGroundingResult Ground(
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        IReadOnlyList<PdfBlockDecision> blockDecisions,
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfSemanticClusterSample> clusterSamples,
        IReadOnlyList<PdfSemanticClusterDecision> clusterDecisions,
        bool requireLearnedCandidateStyle = true)
    {
        var byId = candidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var clusterRoleByStyle = clusterSamples
            .Join(clusterDecisions,
                sample => sample.Id,
                decision => decision.Id,
                (sample, decision) => (sample.Style, decision))
            .GroupBy(x => x.Style)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.decision.Confidence).First().decision);

        var visualLevels = BuildVisualLevels(candidateBlocks, profile, requireLearnedCandidateStyle);
        var headings = new List<PdfGroundedBlockHeading>();
        var rejected = new List<PdfRejectedBlockHeading>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decision in blockDecisions)
        {
            if (!seen.Add(decision.Id)) continue;
            if (!byId.TryGetValue(decision.Id, out var block))
            {
                rejected.Add(Reject(decision, "unknown-block-id"));
                continue;
            }

            if (decision.Role != PdfBlockRole.HeadingTopic)
            {
                rejected.Add(Reject(decision, "analyst-role-not-heading"));
                continue;
            }

            if (decision.Confidence < 0.65)
            {
                rejected.Add(Reject(decision, "low-block-confidence"));
                continue;
            }

            if (requireLearnedCandidateStyle && !profile.CandidateStyles.Contains(block.PrimaryStyle))
            {
                rejected.Add(Reject(decision, "not-visual-candidate-style"));
                continue;
            }

            if (!LooksGroundableText(block.Text))
            {
                rejected.Add(Reject(decision, "ungroundable-text-shape"));
                continue;
            }

            var evidence = "block-role";
            if (clusterRoleByStyle.TryGetValue(block.PrimaryStyle, out var clusterDecision))
            {
                if (clusterDecision.Role == PdfSemanticClusterRole.TableOrChartLabel &&
                    clusterDecision.Confidence >= 0.75)
                {
                    // A style cluster is aggregate evidence; it must not erase a direct semantic
                    // decision about this specific block. Keep the block as conflicted evidence so
                    // the route remains review-only until calibration proves this combination safe.
                    evidence = "block-role+cluster-table-conflict";
                }
                else
                {
                    evidence = clusterDecision.Role == PdfSemanticClusterRole.HeadingTopic &&
                               clusterDecision.Confidence >= 0.60
                        ? "block-role+cluster-heading"
                        : $"block-role+cluster-{RoleName(clusterDecision.Role)}";
                }
            }

            headings.Add(new PdfGroundedBlockHeading(
                block.Id,
                block.Page,
                visualLevels.TryGetValue(block.PrimaryStyle, out var level) ? level : 1,
                block.DisplayText,
                block.Text,
                block.CanonicalText,
                decision.Confidence,
                evidence));
        }

        return new PdfBlockGroundingResult(
            headings.OrderBy(h => h.Page).ThenBy(h => byId[h.Id].TopY * -1).ThenBy(h => h.Id).ToArray(),
            rejected.ToArray());
    }

    private static Dictionary<PdfStyleKey, int> BuildVisualLevels(
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        PdfStyleClusterProfile profile,
        bool requireLearnedCandidateStyle)
    {
        var styles = candidateBlocks
            .Select(b => b.PrimaryStyle)
            .Where(style => !requireLearnedCandidateStyle || profile.CandidateStyles.Contains(style))
            .Distinct()
            .OrderByDescending(s => s.FontSizeBucket)
            .ThenBy(s => s.FontName, StringComparer.Ordinal)
            .ThenBy(s => s.FillColorKey, StringComparer.Ordinal)
            .ToList();

        var levels = new Dictionary<PdfStyleKey, int>();
        for (var i = 0; i < styles.Count; i++) levels[styles[i]] = i + 1;
        return levels;
    }

    private static bool LooksGroundableText(string text)
    {
        var t = PdfTextUtilities.HeadingReadable(text);
        if (t.Length is < 3 or > 180) return false;
        if (!t.Any(char.IsLetter)) return false;
        if (t.Count(c => c is '.' or ';') >= 2) return false;
        if (t.Length >= 80 && t.EndsWith('.')) return false;
        return true;
    }

    private static PdfRejectedBlockHeading Reject(PdfBlockDecision decision, string reason) =>
        new(decision.Id, RoleName(decision.Role), decision.Confidence, reason);

    private static string RoleName(PdfBlockRole role) => role switch
    {
        PdfBlockRole.HeadingTopic => "heading_topic",
        PdfBlockRole.BodySentence => "body_sentence",
        PdfBlockRole.TableOrChartLabel => "table_or_chart_label",
        PdfBlockRole.DecorativeNoise => "decorative_noise",
        _ => "uncertain",
    };

    private static string RoleName(PdfSemanticClusterRole role) => role switch
    {
        PdfSemanticClusterRole.HeadingTopic => "heading_topic",
        PdfSemanticClusterRole.BodySentence => "body_sentence",
        PdfSemanticClusterRole.TableOrChartLabel => "table_or_chart_label",
        _ => "uncertain",
    };
}
