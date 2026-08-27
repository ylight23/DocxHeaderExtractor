using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Rebinds an answer key to a regenerated document for evaluation only. It deliberately works
/// from reviewed key titles and document order; no resulting anchor is a production writeback
/// anchor or a source fact.
/// </summary>
public static class EvaluationAnchorResolver
{
    public static ResolvedEvaluationKey Resolve(AnswerKey key, IReadOnlyList<SlimParagraph> paragraphs)
    {
        var used = new HashSet<int>();
        var audit = new List<EvaluationAnchorResolution>();
        var entries = new List<AnswerKeyEntry>();

        foreach (var entry in key.Entries)
        {
            if (entry.Excluded || string.IsNullOrWhiteSpace(entry.Text))
            {
                entries.Add(entry);
                audit.Add(new EvaluationAnchorResolution(entry.Text, entry.Index, null, "unresolved", "no-title"));
                continue;
            }

            var expected = Canonical(entry.Text);
            var candidates = paragraphs
                .Where(paragraph => !used.Contains(paragraph.Index))
                .Where(paragraph => !paragraph.InTableOfContents)
                .Where(paragraph => Canonical(paragraph.Text).Contains(expected, StringComparison.Ordinal))
                // The reviewed key is in document order.  Choosing the next unused occurrence
                // keeps a duplicate title source-derived; old paragraph indexes are stale here.
                .OrderBy(paragraph => paragraph.Index)
                .ToArray();
            var chosen = candidates.FirstOrDefault();
            if (chosen is null)
            {
                entries.Add(entry);
                audit.Add(new EvaluationAnchorResolution(entry.Text, entry.Index, null, "unresolved", "normalized-title-not-found"));
                continue;
            }

            used.Add(chosen.Index);
            entries.Add(entry with { Index = chosen.Index, StableId = null });
            audit.Add(new EvaluationAnchorResolution(entry.Text, entry.Index, chosen.Index,
                "resolved", "canonical-title+ordered-occurrence", chosen.StableId, candidates.Length));
        }

        var unresolved = audit.Count(item => item.Status == "unresolved" && !string.IsNullOrWhiteSpace(item.Title));
        var resolvedKey = AnswerKey.FromResolvedEntries(entries, key.Title, key.IsPartial || unresolved > 0);
        return new ResolvedEvaluationKey(resolvedKey, audit, unresolved == 0);
    }

    private static string Canonical(string? value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value ?? string.Empty)
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }
}

public sealed record EvaluationAnchorResolution(
    string? Title,
    int? OriginalIndex,
    int? ResolvedIndex,
    string Status,
    string Method,
    string? ResolvedStableId = null,
    int CandidateCount = 0);

public sealed record ResolvedEvaluationKey(
    AnswerKey Key,
    IReadOnlyList<EvaluationAnchorResolution> Entries,
    bool Complete);
