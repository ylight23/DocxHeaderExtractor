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
        bool allowAnyStyle = false,
        IReadOnlyList<PdfLineBlockAnnotation>? annotations = null)
    {
        var byId = candidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var clusterRoleByStyle = clusterSamples
            .Join(clusterDecisions,
                sample => sample.Id,
                decision => decision.Id,
                (sample, decision) => (sample.Style, decision))
            .GroupBy(x => x.Style)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.decision.Confidence).First().decision);

        var visualLevels = BuildVisualLevels(candidateBlocks, profile);
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

            // Model score is telemetry only for visual proposals. Evidence/source/span validation
            // is the acceptance contract; old text-only analyst decisions retain their legacy gate.
            if (decision.Confidence < 0.65 && !decision.Reason.StartsWith("visual-confirmation:", StringComparison.Ordinal))
            {
                rejected.Add(Reject(decision, "low-block-confidence"));
                continue;
            }

            if (!HasGroundedHeadingEvidence(
                    block,
                    decision.EvidenceTags,
                    decision.Reason.StartsWith("visual-confirmation:", StringComparison.Ordinal),
                    profile,
                    annotations))
            {
                rejected.Add(Reject(decision, "ungrounded-visual-evidence-tags"));
                continue;
            }

            if (!allowAnyStyle && !profile.CandidateStyles.Contains(block.PrimaryStyle))
            {
                rejected.Add(Reject(decision, "not-visual-candidate-style"));
                continue;
            }

            if (!LooksGroundableText(block.Text))
            {
                rejected.Add(Reject(decision, "ungroundable-text-shape"));
                continue;
            }

            var evidence = decision.Reason.StartsWith("visual-confirmation:", StringComparison.Ordinal)
                ? decision.Reason
                : "block-role";
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
                HeadingText(block, decision.HeadingSpan),
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
        PdfStyleClusterProfile profile)
    {
        var styles = candidateBlocks
            .Select(b => b.PrimaryStyle)
            .Where(profile.CandidateStyles.Contains)
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

    private static string HeadingText(PdfSemanticBlock block, DocxHeaderExtractor.Core.Models.SourceTextSpan? span)
    {
        if (span is null) return block.DisplayText;
        return span.IsValidFor(block.Text)
            ? PdfTextUtilities.HeadingReadable(block.Text[span.Start..span.End])
            : block.DisplayText;
    }

    private static bool HasGroundedHeadingEvidence(
        PdfSemanticBlock block,
        IReadOnlyList<string>? tags,
        bool visualConfirmation,
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfLineBlockAnnotation>? annotations)
    {
        // A visual heading must cite an observable tag that PDF text/layout data can corroborate.
        // Old text-only decisions have no tags and remain usable for the audit lane only.
        if (tags is null || tags.Count == 0) return !visualConfirmation;
        var compactLabel = block.LineCount <= 2 && LooksGroundableText(block.Text) &&
                           !PdfTextUtilities.HeadingReadable(block.Text).EndsWith('.');
        var distinctStyle = profile.CandidateStyles.Contains(block.PrimaryStyle);
        var lineAnnotations = annotations is null
            ? []
            : annotations.Where(a => block.Lines.Contains(a.Line)).ToArray();
        var tableOrFurniture = lineAnnotations.Length > 0 &&
                               lineAnnotations.All(a => a.TableLike || a.PageNumber || (a.Repeated && a.HeaderFooterZone));
        if (tableOrFurniture) return false;
        return tags.Any(tag =>
            (tag == "standalone_label" && compactLabel) ||
            (tag == "distinct_heading_style" && distinctStyle) ||
            (tag == "section_boundary" && compactLabel && distinctStyle) ||
            (tag == "opens_content" && compactLabel));
    }

    private static PdfRejectedBlockHeading Reject(PdfBlockDecision decision, string reason) =>
        new(decision.Id, RoleName(decision.Role), decision.Confidence, reason);

    private static string RoleName(PdfBlockRole role) => role switch
    {
        PdfBlockRole.HeadingTopic => "heading_topic",
        PdfBlockRole.DocumentTitle => "document_title",
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
