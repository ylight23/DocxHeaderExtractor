using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline comparison of frozen raw semantic responses for DOC-0205. This runner reads
/// only the two frozen prediction sets, never Gold, and never contacts a model/provider.</summary>
public static class A99V6ResponseDiffRunner
{
    private const string DocumentId = "DOC-0205";
    private const string OldRoot = "eval/a99-closed-loop/production-acceptance/runs/flash-restart-20260912-02";
    private const string NewRoot = "eval/a99-closed-loop/production-v6-accuracy-full-e2e";
    private const string OutputPath = "eval/a99-closed-loop/request-equivalence/DOC-0205/response-diff.v1.json";
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var repeats = new List<RepeatDiff>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var oldPath = Path.Combine(repoRoot, OldRoot.Replace('/', Path.DirectorySeparatorChar), DocumentId, $"r{repeat}", "prediction.v1.json");
            var newPath = Path.Combine(repoRoot, NewRoot.Replace('/', Path.DirectorySeparatorChar), DocumentId, $"r{repeat}", "prediction.v1.json");
            var oldPrediction = await LoadPredictionAsync(oldPath, ct);
            var newPrediction = await LoadPredictionAsync(newPath, ct);
            repeats.Add(Diff(repeat, oldPrediction, newPrediction));
        }

        var allChanges = repeats.SelectMany(item => item.Changes).ToArray();
        var summary = new
        {
            sameAliasSameText = allChanges.Count(item => item.Classification == ResponseDiffClass.SAME_ALIAS_SAME_TEXT),
            modelTextDrift = allChanges.Count(item => item.Classification == ResponseDiffClass.MODEL_TEXT_DRIFT),
            aliasPresentAbsent = allChanges.Count(item => item.Classification == ResponseDiffClass.ALIAS_PRESENT_ABSENT),
            newExtraAlias = allChanges.Count(item => item.Classification == ResponseDiffClass.NEW_EXTRA_ALIAS),
            unchangedRole = allChanges.Count(item => item.OldText is not null && item.NewText is not null && item.OldRole == item.NewRole),
            roleChanged = allChanges.Count(item => item.OldText is not null && item.NewText is not null && item.OldRole != item.NewRole),
            oldRawCount = repeats.Sum(item => item.OldRawCount),
            newRawCount = repeats.Sum(item => item.NewRawCount),
            oldFinalCount = repeats.Sum(item => item.OldFinalCount),
            newFinalCount = repeats.Sum(item => item.NewFinalCount),
            finalKeyIntersection = repeats.Sum(item => item.FinalKeyIntersection),
            oldFinalOnly = repeats.Sum(item => item.OldFinalOnly.Count),
            newFinalOnly = repeats.Sum(item => item.NewFinalOnly.Count),
        };
        var report = new
        {
            schemaVersion = "a99-v6-doc0205-response-diff-v1",
            documentId = DocumentId,
            oldRoot = OldRoot,
            newRoot = NewRoot,
            startHead = GitSha(repoRoot),
            modelCalls = 0,
            providerCalls = 0,
            goldRead = false,
            predictionRead = true,
            joinKey = "sourceAlias + 1-based response ordinal within alias; exact alias+text multiset matching is applied before ordinal fallback",
            classificationSemantics = new
            {
                sameAliasSameText = "same frozen raw proposal; downstream/runtime is the remaining suspect for any final-score difference",
                modelTextDrift = "same alias slot exists but quoted text differs",
                aliasPresentAbsent = "old alias has unmatched raw proposal and new has no corresponding slot",
                newExtraAlias = "new alias has unmatched raw proposal",
            },
            repeats,
            aggregate = summary,
            conclusion = summary.modelTextDrift > 0
                ? "Frozen raw semantic outputs differ; the 71-to-49 regression cannot be attributed solely to binder/evaluator without first accounting for MODEL_TEXT_DRIFT."
                : "No raw text drift was found; downstream/runtime or evaluator investigation is warranted next."
        };
        var output = Path.Combine(repoRoot, OutputPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, OutputOptions) + Environment.NewLine, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"DOC0205_RESPONSE_DIFF={OutputPath}");
        Console.WriteLine($"MODEL_TEXT_DRIFT={summary.modelTextDrift}");
        Console.WriteLine($"SAME_ALIAS_SAME_TEXT={summary.sameAliasSameText}");
        Console.WriteLine($"ALIAS_PRESENT_ABSENT={summary.aliasPresentAbsent}");
        Console.WriteLine($"NEW_EXTRA_ALIAS={summary.newExtraAlias}");
        return 0;
    }

    private static RepeatDiff Diff(int repeat, FrozenPrediction oldPrediction, FrozenPrediction newPrediction)
    {
        var oldGroups = oldPrediction.Raw.GroupBy(item => item.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var newGroups = newPrediction.Raw.GroupBy(item => item.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var changes = new List<ResponseChange>();
        foreach (var alias in oldGroups.Keys.Union(newGroups.Keys, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            oldGroups.TryGetValue(alias, out var oldItems);
            newGroups.TryGetValue(alias, out var newItems);
            oldItems ??= [];
            newItems ??= [];
            var oldUsed = new bool[oldItems.Length];
            var newUsed = new bool[newItems.Length];

            // First match identical alias+text occurrences as a multiset, independent of
            // response ordering. This prevents a reorder from being mislabeled as text drift.
            for (var oldIndex = 0; oldIndex < oldItems.Length; oldIndex++)
            {
                var newIndex = Enumerable.Range(0, newItems.Length)
                    .Where(index => !newUsed[index] && string.Equals(oldItems[oldIndex].Text, newItems[index].Text, StringComparison.Ordinal))
                    .DefaultIfEmpty(-1)
                    .First();
                if (newIndex >= 0 && newIndex < newItems.Length && !newUsed[newIndex])
                {
                    oldUsed[oldIndex] = true;
                    newUsed[newIndex] = true;
                    changes.Add(CreateChange(alias, oldIndex + 1, oldItems[oldIndex], newItems[newIndex], ResponseDiffClass.SAME_ALIAS_SAME_TEXT));
                }
            }

            var oldRemaining = Enumerable.Range(0, oldItems.Length).Where(index => !oldUsed[index]).ToArray();
            var newRemaining = Enumerable.Range(0, newItems.Length).Where(index => !newUsed[index]).ToArray();
            var paired = Math.Min(oldRemaining.Length, newRemaining.Length);
            for (var index = 0; index < paired; index++)
            {
                var oldItem = oldItems[oldRemaining[index]];
                var newItem = newItems[newRemaining[index]];
                changes.Add(CreateChange(alias, oldRemaining[index] + 1, oldItem, newItem, ResponseDiffClass.MODEL_TEXT_DRIFT));
            }
            for (var index = paired; index < oldRemaining.Length; index++)
                changes.Add(CreateChange(alias, oldRemaining[index] + 1, oldItems[oldRemaining[index]], null, ResponseDiffClass.ALIAS_PRESENT_ABSENT));
            for (var index = paired; index < newRemaining.Length; index++)
                changes.Add(CreateChange(alias, newRemaining[index] + 1, null, newItems[newRemaining[index]], ResponseDiffClass.NEW_EXTRA_ALIAS));
        }

        var oldFinal = oldPrediction.Final.Select(item => FinalKey(item)).ToHashSet(StringComparer.Ordinal);
        var newFinal = newPrediction.Final.Select(item => FinalKey(item)).ToHashSet(StringComparer.Ordinal);
        return new RepeatDiff(
            $"r{repeat}", oldPrediction.Raw.Count, newPrediction.Raw.Count,
            oldPrediction.Final.Count, newPrediction.Final.Count,
            oldPrediction.RawSha256, newPrediction.RawSha256,
            oldFinal.Intersect(newFinal, StringComparer.Ordinal).Count(),
            oldFinal.Except(newFinal, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            newFinal.Except(oldFinal, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            changes.OrderBy(item => item.Alias, StringComparer.Ordinal).ThenBy(item => item.Ordinal).ToArray());
    }

    private static ResponseChange CreateChange(string alias, int ordinal, RawHeading? oldItem, RawHeading? newItem, ResponseDiffClass classification)
    {
        var oldText = oldItem?.Text;
        var newText = newItem?.Text;
        return new ResponseChange(
            alias, ordinal, classification, oldText, newText, oldItem?.Role, newItem?.Role,
            oldItem?.Occurrence, newItem?.Occurrence,
            oldText is null || newText is null ? null : EditDistance(oldText, newText),
            oldText is null || newText is null ? null : TextDelta(oldText, newText));
    }

    private static TextDeltaInfo TextDelta(string oldText, string newText)
    {
        var prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
               oldText[oldText.Length - suffix - 1] == newText[newText.Length - suffix - 1]) suffix++;
        var oldPunctuation = new string(oldText.Where(char.IsPunctuation).ToArray());
        var newPunctuation = new string(newText.Where(char.IsPunctuation).ToArray());
        var oldNormalized = oldText.Normalize(NormalizationForm.FormKC);
        var newNormalized = newText.Normalize(NormalizationForm.FormKC);
        return new TextDeltaInfo(
            prefix, suffix, oldText.Length - prefix - suffix, newText.Length - prefix - suffix,
            oldPunctuation, newPunctuation, !string.Equals(oldPunctuation, newPunctuation, StringComparison.Ordinal),
            !string.Equals(oldText, newText, StringComparison.Ordinal) && string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal));
    }

    private static int EditDistance(string oldText, string newText)
    {
        var previous = Enumerable.Range(0, newText.Length + 1).ToArray();
        for (var i = 1; i <= oldText.Length; i++)
        {
            var current = new int[newText.Length + 1];
            current[0] = i;
            for (var j = 1; j <= newText.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (oldText[i - 1] == newText[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    private static async Task<FrozenPrediction> LoadPredictionAsync(string path, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var root = document.RootElement;
        var raw = root.GetProperty("rawModelHeadings").EnumerateArray().Select(item => new RawHeading(
            item.GetProperty("source").GetString()!, item.GetProperty("text").GetString()!,
            item.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null,
            item.TryGetProperty("occurrence", out var occurrence) && occurrence.ValueKind != JsonValueKind.Null ? occurrence.GetInt32() : null)).ToArray();
        var final = root.TryGetProperty("finalHeadings", out var finalValue) && finalValue.ValueKind == JsonValueKind.Array
            ? finalValue.EnumerateArray().Select(item => new FinalHeading(
                item.GetProperty("sourceId").GetString()!, item.GetProperty("start").GetInt32(),
                item.GetProperty("end").GetInt32(), item.GetProperty("text").GetString()!)).ToArray()
            : Array.Empty<FinalHeading>();
        return new FrozenPrediction(raw, final, Sha256Text(root.GetProperty("rawModelHeadings").GetRawText()));
    }

    private static string FinalKey(FinalHeading item) => $"{item.SourceId}:{item.Start}:{item.End}";

    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false });
        process?.WaitForExit();
        return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED";
    }

    private enum ResponseDiffClass { SAME_ALIAS_SAME_TEXT, MODEL_TEXT_DRIFT, ALIAS_PRESENT_ABSENT, NEW_EXTRA_ALIAS }
    private sealed record RawHeading(string Source, string Text, string? Role, int? Occurrence);
    private sealed record FrozenPrediction(IReadOnlyList<RawHeading> Raw, IReadOnlyList<FinalHeading> Final, string RawSha256);
    private sealed record FinalHeading(string SourceId, int Start, int End, string Text);
    private sealed record RepeatDiff(string Repeat, int OldRawCount, int NewRawCount, int OldFinalCount, int NewFinalCount, string OldRawSha256, string NewRawSha256, int FinalKeyIntersection, IReadOnlyList<string> OldFinalOnly, IReadOnlyList<string> NewFinalOnly, IReadOnlyList<ResponseChange> Changes);
    private sealed record ResponseChange(string Alias, int Ordinal, ResponseDiffClass Classification, string? OldText, string? NewText, string? OldRole, string? NewRole, int? OldOccurrence, int? NewOccurrence, int? EditDistance, TextDeltaInfo? TextDelta);
    private sealed record TextDeltaInfo(int CommonPrefixLength, int CommonSuffixLength, int OldChangedRegionLength, int NewChangedRegionLength, string OldPunctuation, string NewPunctuation, bool PunctuationChanged, bool NormalizationOnlyChange);
}
