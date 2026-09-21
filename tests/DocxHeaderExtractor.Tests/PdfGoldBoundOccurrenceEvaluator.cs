using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

internal static class PdfGoldBoundOccurrenceEvaluator
{
    public const string ContractVersion = "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role";

    public static PdfGoldEvaluation Evaluate(
        PdfGoldDocument gold,
        IReadOnlyList<PdfBoundOccurrence> predicted,
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<PdfGoldIssue>? goldIssues = null)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(aliases);

        var goldBound = BindGold(gold, aliases, out var bindingIssues);
        return EvaluateBound(
            goldBound,
            predicted,
            bindingIssues
                .Concat((goldIssues ?? []).Select(issue => $"{issue.Code}:{issue.SourceAlias}"))
                .ToArray(),
            gold.Headings.Count);
    }

    public static IReadOnlyList<PdfBoundOccurrence> BindGold(
        PdfGoldDocument gold,
        IReadOnlyList<SemanticSourceAlias> aliases,
        out IReadOnlyList<string> bindingIssues)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(aliases);

        var proposals = gold.Headings.Select(heading => new CanonicalSemanticProposal(
            heading.SourceAlias,
            true,
            heading.VerbatimText,
            SemanticRole: heading.SemanticRole,
            Occurrence: heading.Occurrence,
            LeftExactContext: heading.LeftExactContext,
            RightExactContext: heading.RightExactContext,
            SelectionMode: heading.SelectionMode)).ToArray();
        var bound = CanonicalSemanticExactBinder.Bind(proposals, aliases, out var observations);
        bindingIssues = observations
            .Where(observation => observation.Status != CanonicalSemanticBindingStatus.Bound)
            .Select(observation =>
                $"{observation.Proposal.SourceAlias}:{observation.Status}:{observation.Reason}")
            .ToArray();
        if (bound.Count != gold.Headings.Count)
            bindingIssues = bindingIssues.Append($"BOUND_COUNT:{bound.Count}/{gold.Headings.Count}").ToArray();

        return bound
            .Select((item, index) => new PdfBoundOccurrence(
                item.Parts,
                item.SemanticRole,
                item.Alias,
                gold.Headings[index].ParentSourceAlias))
            .ToArray();
    }

    internal static PdfGoldEvaluation EvaluateBound(
        IReadOnlyList<PdfBoundOccurrence> gold,
        IReadOnlyList<PdfBoundOccurrence> predicted,
        IReadOnlyList<string> goldIssues = null!,
        int? goldRows = null)
    {
        var goldByKey = gold.ToDictionary(Key, StringComparer.Ordinal);
        var predictedByKey = predicted.ToDictionary(Key, StringComparer.Ordinal);
        var matched = goldByKey.Keys.Where(predictedByKey.ContainsKey).ToArray();
        var missed = goldByKey.Keys.Except(predictedByKey.Keys, StringComparer.Ordinal)
            .Select(key => goldByKey[key].SourceAlias).Order(StringComparer.Ordinal).ToArray();
        var spurious = predictedByKey.Keys.Except(goldByKey.Keys, StringComparer.Ordinal)
            .Select(key => predictedByKey[key].SourceAlias).Order(StringComparer.Ordinal).ToArray();

        var membership = new PdfMembershipScore(matched.Length, spurious.Length, missed.Length)
        {
            MissedAliases = missed,
            SpuriousAliases = spurious,
        };

        var roleMismatches = matched
            .Where(key => !string.Equals(
                goldByKey[key].SemanticRole,
                predictedByKey[key].SemanticRole,
                StringComparison.Ordinal))
            .Select(key =>
                $"{key}: expected {DisplayRole(goldByKey[key].SemanticRole)}, " +
                $"got {DisplayRole(predictedByKey[key].SemanticRole)}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var roleScore = new PdfSemanticRoleScore(
            matched.Length,
            matched.Length - roleMismatches.Length,
            roleMismatches.Length)
        {
            Mismatches = roleMismatches,
        };

        var adjudicated = matched
            .Where(key => goldByKey[key].ParentSourceAlias is { Length: > 0 })
            .ToArray();
        var agreed = 0;
        var notProposed = 0;
        var disagreements = new List<string>();
        foreach (var key in adjudicated)
        {
            var expected = goldByKey[key].ParentSourceAlias!;
            var actual = predictedByKey[key].ParentSourceAlias;
            if (actual is null or "")
            {
                notProposed++;
                disagreements.Add($"{goldByKey[key].SourceAlias}: expected {expected}, none proposed");
            }
            else if (string.Equals(actual, expected, StringComparison.Ordinal)) agreed++;
            else disagreements.Add($"{goldByKey[key].SourceAlias}: expected {expected}, got {actual}");
        }

        var relations = new PdfRelationScore(
            adjudicated.Length,
            agreed,
            adjudicated.Length - agreed - notProposed,
            notProposed)
        {
            Disagreements = disagreements,
        };

        return new PdfGoldEvaluation(membership, relations)
        {
            GoldRows = goldRows ?? gold.Count,
            PredictedRows = predicted.Count,
            GoldIssues = goldIssues ?? [],
            SemanticRole = roleScore,
        };
    }

    private static string Key(PdfBoundOccurrence occurrence) =>
        string.Join(';', occurrence.Parts.Select(part =>
            $"{part.SourceId}:{part.Start}:{part.End}"));

    private static string DisplayRole(string? role) => role ?? "<null>";
}

internal sealed record PdfBoundOccurrence(
    IReadOnlyList<CanonicalSemanticBoundPart> Parts,
    string SemanticRole,
    string SourceAlias,
    string? ParentSourceAlias = null);
