using System.Text.Encodings.Web;
using System.Text.Json;
using System.Diagnostics;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>One model reply as the diagnostics hook sees it: the unit, the turn, the reasoning the model streamed (null when it streamed none),
/// the tool calls it made, its visible content, and the tool results the request before it added to the conversation. No decision reads any of it.</summary>
public sealed record ModelReply(string Unit, int Turn, string? Reasoning, IReadOnlyList<ModelReplyToolCall> ToolCalls, string? Content, string? FinishReason,
    IReadOnlyList<ModelToolResult> ToolResults);

/// <summary>A tool result the conversation received: the call it answers and the content the tool returned.</summary>
public sealed record ModelToolResult(string? ToolCallId, string Content);

/// <summary>A tool call of a model reply: the tool's name and the arguments as the model wrote them.</summary>
public sealed record ModelReplyToolCall(string? Name, string Arguments);

/// <summary>
/// The heading selector of the V6 method, for any kind of document: the document is read into an <see cref="IAgentSource"/> and cut into
/// segments; each segment is one conversation with qwen/qwen3.7-flash on OpenRouter (provider alibaba only, alibaba/us-east-1 ignored, no
/// fallback, ZDR false, data collection allowed) in which the request shows alias and text and the document style table, the first reply must
/// read the facts through the facts tool, and at most three tool rounds precede the answer; then the selected lines are divided into headings.
/// 3 transport retries, repair turns with no limit of their own, two prompt-cache marks, json_object on every turn, the single-fence unwrap;
/// the budget is every first request plus every unit's tool rounds, retries and four repairs at the worst case, plus 10%. Nothing is written
/// to disk. What differs between a PDF and a DOCX is the source and its flavor (prompt, tools, payload), not this class.
/// </summary>
public abstract class HeadingAgentSelector : IHeadingSelector
{
    const int ExtraPostsPerUnit = AgentUnit.TransportRetries + 2 * 4;
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Func<HttpMessageHandler> handlerFactory;
    readonly int parallelism;
    readonly Action<ModelReply>? onModelReply;
    readonly string instruction;
    readonly int firstSegments;

    /// <param name="handlerFactory">The HTTP handler of a run; no redirects and no cookies by default.</param>
    /// <param name="parallelism">Segments in flight at once; DHX_PDF_AGENT_PARALLELISM, else 6 (the provider rate-limited above about 6 concurrent requests).</param>
    /// <param name="onModelReply">Diagnostics: called with every model reply (unit, turn, reasoning, tool calls, content). Null (the default) records nothing.</param>
    /// <param name="instruction">The user instruction sent verbatim; the production instruction by default.</param>
    /// <param name="firstSegments">Diagnostics: decide only the first N segments (0 = every segment), as the D4.12 trials did for long documents.</param>
    private protected HeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory, int? parallelism, Action<ModelReply>? onModelReply, string? instruction, int firstSegments)
    {
        this.onModelReply = onModelReply;
        this.instruction = instruction ?? AgentProtocol.Instruction;
        if (firstSegments < 0) throw new ArgumentOutOfRangeException(nameof(firstSegments));
        this.firstSegments = firstSegments;
        this.handlerFactory = handlerFactory ?? (() => AgentModel.CreateHandler());
        this.parallelism = parallelism
            ?? (int.TryParse(Environment.GetEnvironmentVariable("DHX_PDF_AGENT_PARALLELISM"), out var n) && n >= 1 ? n : DefaultParallelism);
        if (this.parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
    }

    public const int DefaultParallelism = 6;

    public abstract string SelectorId { get; }

    /// <summary>The identity of the instruction in the selector id: the first 8 hex digits of its SHA-256.</summary>
    private protected string InstructionTag => AgentProtocol.Sha(instruction)[..8];

    /// <summary>Reads the document into its agent source; throws InvalidOperationException with a code when it cannot be read.</summary>
    private protected abstract IAgentSource Load(string path);

    public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return Task.Run(() =>
        {
            var source = Load(path);
            var budget = BudgetFor(source, instruction);
            using var handler = handlerFactory();
            using var model = new AgentModel(handler, apiKey, budget);
            return Select(source, Traced(model), budget, parallelism, ct, retryDelayMs: 5000, costOf: () => budget.ActualUsd, instruction, firstSegments);
        }, ct);
    }

    AgentUnit.Model Traced(AgentModel model) => Traced(model, onModelReply);

    /// <summary>The model of a run with the diagnostics hook around it: every reply, with the tool results its request added, goes to the hook.</summary>
    internal static AgentUnit.Model Traced(AgentModel model, Action<ModelReply>? onModelReply)
    {
        // Each request carries the whole conversation of its unit, so the tool results a reply has not reported yet are the new tail.
        var reported = new Dictionary<string, int>(StringComparer.Ordinal);
        var gate = new object();
        return (unitId, turn, request) =>
        {
            var results = onModelReply is null ? Array.Empty<ModelToolResult>() : NewToolResults(request.Body, unitId, reported, gate);
            var reply = model.Turn(unitId, turn, request);
            onModelReply?.Invoke(new ModelReply(unitId, turn, reply.Reasoning,
                reply.ToolCalls.Select(c => new ModelReplyToolCall(c.Name, c.Arguments)).ToArray(), reply.Content, reply.FinishReason, results));
            return reply;
        };
    }

    /// <summary>The tool results one request adds to the conversation of one unit, since that unit's previous request.</summary>
    static IReadOnlyList<ModelToolResult> NewToolResults(string body, string unitId, Dictionary<string, int> reported, object gate)
    {
        var all = new List<ModelToolResult>();
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (var m in messages.EnumerateArray())
                    if (m.TryGetProperty("role", out var role) && role.GetString() == "tool")
                        all.Add(new ModelToolResult(
                            m.TryGetProperty("tool_call_id", out var id) ? id.GetString() : null,
                            m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""));
        }
        lock (gate)
        {
            reported.TryGetValue(unitId, out var seen);
            reported[unitId] = all.Count;
            return all.Skip(seen).ToArray();
        }
    }

    /// <summary>The budget of one document: one first post per segment plus every segment's tool rounds, transport retries and four repairs, and
    /// the reserve of every first request plus those spare posts at the byte cap, plus 10%.</summary>
    internal static AgentBudget BudgetFor(IAgentSource source, string instruction = AgentProtocol.Instruction)
    {
        var units = source.Cores.Count;
        var posts = units + units * AgentProtocol.MaxToolTurns + units * ExtraPostsPerUnit;
        var flavor = source.Flavor;
        var styles = flavor.CreateTools(source).DocumentStyles();
        var first = Enumerable.Range(0, units).Sum(n =>
        {
            var (decide, shown) = source.Scope(n);
            var payload = AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(n), decide, shown.Select(i => source.Lines[i]).ToArray(), styles, instruction, flavor,
                flavor.PayloadFacts(source, decide));
            return AgentBudget.AttemptUsd(AgentProtocol.Build(payload, new System.Text.Json.Nodes.JsonArray(), "auto", flavor).Bytes);
        });
        var spare = (posts - units) * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes);
        // The grouping step: at most one request per 40 runs of adjacent selected lines (a run needs two lines, so at most one chunk per 80 lines),
        // each with its transport retries and repair turns; its requests are far smaller than a segment's.
        var groupPosts = 2 * Math.Max(1, (source.Lines.Count + 79) / 80) * (1 + AgentGrouping.MaxRepairs) * (1 + AgentUnit.TransportRetries);
        var groupReserve = groupPosts * AgentBudget.AttemptUsd(60000);
        var reviewPosts = units * AgentSelectionReview.MaxPosts;
        var reviewReserve = reviewPosts * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes);
        var verifyPosts = 4 * Math.Max(1, (source.Lines.Count + AgentGroupVerification.BatchSize - 1) / AgentGroupVerification.BatchSize) * AgentGroupVerification.MaxPosts;
        var verifyReserve = verifyPosts * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes);
        return new AgentBudget(posts + groupPosts + reviewPosts + verifyPosts, Math.Ceiling((first + spare + groupReserve + reviewReserve + verifyReserve) * 1.1 * 100) / 100);
    }

    /// <summary>Runs every segment, parallelism at a time, and collects the lines selected by the units whose answer passed.</summary>
    internal static HeadingSelection Select(IAgentSource source, AgentUnit.Model model, AgentBudget budget, int parallelism, CancellationToken ct,
        int retryDelayMs, Func<double> costOf, string instruction = AgentProtocol.Instruction, int firstSegments = 0, bool verifyGroups = true)
    {
        var phase = Stopwatch.StartNew();
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var outcomes = new AgentUnitOutcome[firstSegments > 0 ? Math.Min(firstSegments, source.Cores.Count) : source.Cores.Count];
        Parallel.For(0, outcomes.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            n => outcomes[n] = AgentUnit.Run(source, n, model, budget, retryDelayMs, instruction));
        var complete = outcomes.Where(o => o.Status == "COMPLETE").ToArray();
        var selected = complete.SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)).ToHashSet(StringComparer.Ordinal);
        var answers = complete.Select(o => JsonSerializer.Serialize(new
        {
            unit = o.UnitId, decisions = o.Decisions, reasons = o.Reasons, analysis = o.Analysis,
            headingExtents = o.Extents is { Count: > 0 } e ? e.ToDictionary(kv => kv.Key, kv => new { start = kv.Value.Start, end = kv.Value.End, text = kv.Value.Text }) : null,
            readingViewIds = o.ReadingChoices is { Count: > 0 } rc ? rc : null,
            coreFactsCoverage = new { targetsWithoutFacts = o.TargetsWithoutFacts ?? Array.Empty<string>(), factsResultsCut = o.FactsResultsCut },
            detailEvidenceCoverage = new { aliasesRead = o.DetailEvidenceAliases ?? Array.Empty<string>() },
        }, Compact)).ToArray();
        // Only the extent of a line that its own unit selected counts; every unit decides its own targets, so no alias has two.
        var readingChoices = complete.Where(o => o.ReadingChoices is not null)
            .SelectMany(o => o.ReadingChoices!.Where(kv => o.Decisions.GetValueOrDefault(kv.Key) == "SELECT"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var extents = complete.Where(o => o.Extents is not null)
            .SelectMany(o => o.Extents!.Where(kv => o.Decisions.GetValueOrDefault(kv.Key) == "SELECT"))
            .ToDictionary(kv => kv.Key, kv => new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(kv.Value.Start, kv.Value.End), StringComparer.Ordinal);
        var failures = outcomes.Where(o => o.Status != "COMPLETE").Select(o => o.UnitId + ":" + o.Status + ":" + (o.Failure ?? "UNKNOWN")
            + (o.Failure is "COVERAGE:CORE_FACTS_MISSING" or "BUDGET:CORE_FACTS_UNCOVERED" ? ":" + o.TargetsWithoutFacts!.Count + "_TARGETS_WITHOUT_CORE_FACTS" : "")).ToList();
        if (budget.HaltReason is { } halt) failures.Add("HALT:" + halt);
        seconds["selection"] = phase.Elapsed.TotalSeconds;
        phase.Restart();
        // The selected lines divided into headings: the model decides which adjacent lines form one heading, C# checks the division.
        var grouping = AgentGrouping.Group(source, selected, model, budget, parallelism, ct, retryDelayMs, instruction);
        seconds["grouping"] = phase.Elapsed.TotalSeconds;
        phase.Restart();
        if (verifyGroups)
            grouping = AgentGroupVerification.Verify(source, grouping, extents, readingChoices, model, budget,
                parallelism, ct, retryDelayMs, instruction);
        seconds["verification"] = phase.Elapsed.TotalSeconds;
        phase.Restart();
        grouping = AgentSelectionReview.Resolve(source, grouping, outcomes, selected, extents, readingChoices,
            model, budget, parallelism, ct, retryDelayMs, instruction, verifyGroups);
        seconds["review"] = phase.Elapsed.TotalSeconds;
        answers = answers.Concat(grouping.Answers.Select(a => JsonSerializer.Serialize(new { grouping = a }, Compact))).ToArray();
        answers = answers.Concat(grouping.Evidence.Select(a => JsonSerializer.Serialize(new { groupingEvidence = a }, Compact))).ToArray();
        failures.AddRange(grouping.Failures);
        var unresolved = grouping.UnresolvedAliases.ToHashSet(StringComparer.Ordinal);
        selected.ExceptWith(unresolved);
        foreach (var alias in unresolved) { extents.Remove(alias); readingChoices.Remove(alias); }
        var unresolvedUnits = Enumerable.Range(0, outcomes.Length).Count(n => outcomes[n].Status == "COMPLETE" && source.Scope(n).Decide.Any(unresolved.Contains));
        var initial = outcomes.SelectMany(o => o.Decisions).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var aliasStates = source.Lines.Where(l => initial.ContainsKey(l.Alias)).ToDictionary(l => l.Alias, l =>
        {
            var unknown = unresolved.Contains(l.Alias) || initial[l.Alias] == "UNRESOLVED";
            var final = selected.Contains(l.Alias) ? "SELECT" : unknown ? "UNRESOLVED" : "NOT_SELECT";
            return new HeadingAliasState(initial[l.Alias], final, grouping.Memberships.GetValueOrDefault(l.Alias),
                final == "SELECT" ? "READY_FOR_BINDING" : final == "UNRESOLVED" ? "UNRESOLVED" : "NOT_SELECTED");
        }, StringComparer.Ordinal);
        answers = answers.Append(JsonSerializer.Serialize(new { stage = "pre-binding-alias-state", aliases = aliasStates }, Compact)).ToArray();
        return new HeadingSelection(selected, complete.Length - unresolvedUnits, outcomes.Length, failures, answers, budget.Calls, costOf(), source.Flavor.PlacementContext(source), grouping.Groups,
            extents.Count > 0 ? extents : null) { HeadingReadings = readingChoices.Count > 0 ? readingChoices : null,
                Grouping = grouping.Observation with { UnresolvedAliases = unresolved.Count },
                StageSeconds = seconds.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)), AliasStates = aliasStates };
    }
}
