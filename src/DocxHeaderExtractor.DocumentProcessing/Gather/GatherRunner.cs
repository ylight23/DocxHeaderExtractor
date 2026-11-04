using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// The model a gathering run calls. The production implementation sends the request through the agent route; tests use a fake one. The runner never
/// reads the reply as anything but text: it parses it and lets the checker decide what holds.
/// </summary>
public interface IGatherModel
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>The provider ended generation at its output limit. A provider adapter must explicitly map this condition.</summary>
public sealed class GatherCompletionTruncatedException : Exception
{
    public GatherCompletionTruncatedException() : base("FINISH_LENGTH") { }
}

/// <summary>A safe provider failure code and whether another whole-call attempt may help.</summary>
public sealed class GatherModelFailureException(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// One gathering run: the kept facts, the refused and duplicate ones, the batches that failed after their attempts, the units those batches held
/// (never reviewed), and the gaps of the sources. A run is complete only when no batch failed; a partial run must be shown as partial.
/// </summary>
public sealed record GatherRun(
    IReadOnlyList<GatheredFact> Facts,
    IReadOnlyList<FactRefusal> Refused,
    IReadOnlyList<FactDuplicate> Duplicates,
    IReadOnlyList<string> BatchFailures,
    IReadOnlyList<string> UnreviewedUnitIds,
    IReadOnlyList<GatherGap> Gaps,
    int Batches)
{
    /// <summary>Unread source regions (see <see cref="GatherUnits"/>): their text is not in the units, so a result cannot claim to cover them.</summary>
    public IReadOnlyList<GatherGap> UnreadGaps => Gaps.Where(g => GatherUnits.IsUnread(g.Reason)).ToList();

    /// <summary>Processing is complete only if batches finished, no source region was unread, and no proposed fact was rejected.</summary>
    public bool Complete => BatchFailures.Count == 0 && UnreadGaps.Count == 0 && Refused.Count == 0;
}

/// <summary>The model supplies verbatim source quotes; the checker binds them to source spans.</summary>
public static class GatherPrompt
{
    public const string Version = "gather-source-quotes-v3";

    public const string System =
        "Select the information relevant to the goal from the source units you are given. Each unit has a unitId, a heading path (headingCertain says " +
        "whether that path is known), optionally a row label and column header with their unit ids, and a text. " +
        "Answer only with a JSON object {\"facts\":[{\"parts\":[{\"unitId\":\"...\",\"quote\":\"exact source text\",\"role\":\"label|value|text\"}]}]}. " +
        "Each quote must be a nonempty contiguous exact substring of that unit's text, preserving case, whitespace and punctuation. " +
        "Copy enough surrounding source text for the quote to occur only once within its unit. Do not calculate offsets. " +
        "Use only unitIds you were given. Do not paraphrase or invent text. " +
        "Copy the unit's text as it is written, with its own spelling, typos and capitalization; never correct it. " +
        "The heading path, row label and column header are context that is attached to every fact already: never quote them, and never use them as a label. " +
        "A label part must be text inside the unit's own text. Give each quote the unitId of the unit whose text contains it. " +
        "Use a label part and a value part together only when they belong together in the same row or sentence. " +
        "Give one fact per distinct piece of information. If no unit is relevant, answer {\"facts\":[]}.";

    public static string User(string goal, IReadOnlyList<GatherUnit> batch) =>
        "Goal: " + goal + "\n" + GatherCheck.Payload(batch);
}

/// <summary>
/// Reads a model reply into proposed facts. A part that is missing a field or of the wrong kind is kept with a value the checker refuses, so the
/// refusal names the reason. A reply that is not a JSON object with a "facts" array fails the whole batch.
/// </summary>
public static class GatherReply
{
    public static IReadOnlyList<ProposedFact> Parse(string? content)
    {
        if (content is null) throw new FormatException("NO_CONTENT");
        var root = JsonNode.Parse(content) as JsonObject ?? throw new FormatException("NOT_AN_OBJECT");
        var list = root["facts"] as JsonArray ?? throw new FormatException("NO_FACTS_ARRAY");
        var facts = new List<ProposedFact>();
        foreach (var item in list)
        {
            var parts = (item as JsonObject)?["parts"] as JsonArray ?? new JsonArray();
            facts.Add(new ProposedFact(parts.Select(Part).ToList()));
        }
        return facts;
    }

    static FactPart Part(JsonNode? node)
    {
        var o = node as JsonObject;
        var unit = o?["unitId"] is JsonValue u && u.TryGetValue<string>(out var id) ? id : "";
        var quote = o?["quote"] is JsonValue q && q.TryGetValue<string>(out var text) ? text : "";
        var role = o?["role"] is JsonValue r && r.TryGetValue<string>(out var rl) ? rl : "";
        return new FactPart(unit, -1, -1, role, quote);
    }
}

/// <summary>
/// Runs a gathering goal over the units of one or more sources: batches the units, asks the model once per batch, and checks every fact against the
/// units the model was actually shown. A fact that cites a unit from another batch is refused as UNSEEN_UNIT, since the model had no chance to see it.
/// A batch whose call fails or whose reply does not parse is recorded and the other batches still run.
/// </summary>
public static class GatherRunner
{
    /// <summary>Attempts per batch. A failed reply is often one bad sample, so a batch is sent again before it counts as failed.</summary>
    public const int MaxAttempts = 2;

    public static async Task<GatherRun> RunAsync(string goal, IReadOnlyList<GatherSource> sources, IGatherModel model, CancellationToken ct = default,
        GatherCheckpointStore? checkpoints = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(model);
        var units = sources.SelectMany(s => s.Units).ToList();
        var gaps = sources.SelectMany(s => s.Gaps).ToList();
        var batches = GatherBatches.Plan(units);
        var failures = new List<string>();
        var unreviewed = new List<string>();
        var proposed = new List<(int Batch, ProposedFact Fact)>();
        for (var k = 0; k < batches.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var id = $"batch-{k + 1:000}";
            IReadOnlyList<ProposedFact>? facts = null;
            var failure = "";
            var truncated = false;
            for (var attempt = 1; attempt <= MaxAttempts && facts is null; attempt++)
            {
                try
                {
                    var user = GatherPrompt.User(goal, batches[k]);
                    var reply = checkpoints?.Read(GatherPrompt.System, user);
                    if (reply == GatherCheckpointStore.SplitMarker) throw new GatherCompletionTruncatedException();
                    if (reply is null)
                    {
                        reply = await model.CompleteAsync(GatherPrompt.System, user, ct).ConfigureAwait(false);
                        var parsed = GatherReply.Parse(reply);
                        checkpoints?.Write(GatherPrompt.System, user, reply);
                        facts = parsed;
                    }
                    else
                        facts = GatherReply.Parse(reply);
                }
                catch (GatherCompletionTruncatedException)
                {
                    failure = "FINISH_LENGTH";
                    truncated = true;
                    break;
                }
                catch (GatherModelFailureException ex)
                {
                    failure = ex.Code;
                    if (!ex.Retryable) break;
                }
                catch (FormatException ex)
                {
                    failure = ex.Message;
                }
                catch (System.Text.Json.JsonException)
                {
                    failure = "REPLY_NOT_JSON";
                }
                catch (IOException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (UnauthorizedAccessException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = $"MODEL_CALL_FAILED:{ex.GetType().Name}";
                }
            }
            if (truncated)
            {
                // Keep a table row intact; if it or a single unit is too large, report it rather than lose context.
                var groups = GatherBatches.Groups(batches[k]).ToList();
                if (groups.Count > 1)
                {
                    checkpoints?.Write(GatherPrompt.System, GatherPrompt.User(goal, batches[k]), GatherCheckpointStore.SplitMarker);
                    var midpoint = groups.Count / 2;
                    batches[k] = groups.Take(midpoint).SelectMany(g => g).ToList();
                    batches.Insert(k + 1, groups.Skip(midpoint).SelectMany(g => g).ToList());
                    k--;
                    continue;
                }
            }
            if (facts is null)
            {
                failures.Add($"{id}:{failure}");
                unreviewed.AddRange(batches[k].Select(u => u.Id));
                continue;
            }
            foreach (var fact in facts) proposed.Add((k, fact));
        }

        // Facts that cite a unit the model was not shown in their batch are refused before the checker; the checker's indexes are mapped back.
        var shown = batches.Select(b => b.Select(u => u.Id).ToHashSet(StringComparer.Ordinal)).ToList();
        var known = units.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var refused = new List<FactRefusal>();
        var checkable = new List<(int Index, ProposedFact Fact)>();
        for (var i = 0; i < proposed.Count; i++)
        {
            var (batch, fact) = proposed[i];
            if (fact.Parts.Any(p => known.Contains(p.UnitId) && !shown[batch].Contains(p.UnitId)))
                refused.Add(new(i, "UNSEEN_UNIT", fact));
            else
                checkable.Add((i, fact));
        }
        var result = GatherCheck.Check(units, checkable.Select(c => c.Fact));
        refused.AddRange(result.Refused.Select(r => new FactRefusal(checkable[r.Index].Index, r.Reason, r.Proposal)));
        var duplicates = result.Duplicates
            .Select(d => new FactDuplicate(d.KeptIndex, checkable[d.DuplicateIndex].Index, d.Parts)).ToList();
        return new GatherRun(result.Kept, refused, duplicates, failures, unreviewed, gaps, batches.Count);
    }
}
