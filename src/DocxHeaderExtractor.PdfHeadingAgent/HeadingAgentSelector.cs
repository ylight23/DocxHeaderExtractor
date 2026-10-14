using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.PdfHeadingAgent;

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
    readonly Action<string, string?>? onModelReply;
    readonly string instruction;
    readonly int firstSegments;

    /// <param name="handlerFactory">The HTTP handler of a run; no redirects and no cookies by default.</param>
    /// <param name="parallelism">Segments in flight at once; DHX_PDF_AGENT_PARALLELISM, else 6 (the provider rate-limited above about 6 concurrent requests).</param>
    /// <param name="onModelReply">Diagnostics: called with the unit id and the content of every model reply. Null (the default) records nothing.</param>
    /// <param name="instruction">The user instruction sent verbatim; the production instruction by default.</param>
    /// <param name="firstSegments">Diagnostics: decide only the first N segments (0 = every segment), as the D4.12 trials did for long documents.</param>
    private protected HeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory, int? parallelism, Action<string, string?>? onModelReply, string? instruction, int firstSegments)
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

    AgentUnit.Model Traced(AgentModel model) => (unitId, turn, request) =>
    {
        var reply = model.Turn(unitId, turn, request);
        onModelReply?.Invoke(unitId, reply.Content);
        return reply;
    };

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
            var payload = AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(n), decide, shown.Select(i => source.Lines[i]).ToArray(), styles, instruction, flavor);
            return AgentBudget.AttemptUsd(AgentProtocol.Build(payload, new System.Text.Json.Nodes.JsonArray(), "auto", flavor).Bytes);
        });
        var spare = (posts - units) * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes);
        // The grouping step: at most one request per 40 runs of adjacent selected lines (a run needs two lines, so at most one chunk per 80 lines),
        // each with its transport retries and repair turns; its requests are far smaller than a segment's.
        var groupPosts = Math.Max(1, (source.Lines.Count + 79) / 80) * (1 + AgentGrouping.MaxRepairs) * (1 + AgentUnit.TransportRetries);
        var groupReserve = groupPosts * AgentBudget.AttemptUsd(60000);
        return new AgentBudget(posts + groupPosts, Math.Ceiling((first + spare + groupReserve) * 1.1 * 100) / 100);
    }

    /// <summary>Runs every segment, parallelism at a time, and collects the lines selected by the units whose answer passed.</summary>
    internal static HeadingSelection Select(IAgentSource source, AgentUnit.Model model, AgentBudget budget, int parallelism, CancellationToken ct,
        int retryDelayMs, Func<double> costOf, string instruction = AgentProtocol.Instruction, int firstSegments = 0)
    {
        var outcomes = new AgentUnitOutcome[firstSegments > 0 ? Math.Min(firstSegments, source.Cores.Count) : source.Cores.Count];
        Parallel.For(0, outcomes.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            n => outcomes[n] = AgentUnit.Run(source, n, model, budget, retryDelayMs, instruction));
        var complete = outcomes.Where(o => o.Status == "COMPLETE").ToArray();
        var selected = complete.SelectMany(o => o.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)).ToHashSet(StringComparer.Ordinal);
        var answers = complete.Select(o => JsonSerializer.Serialize(new { unit = o.UnitId, decisions = o.Decisions, reasons = o.Reasons, analysis = o.Analysis }, Compact)).ToArray();
        var failures = outcomes.Where(o => o.Status != "COMPLETE").Select(o => o.UnitId + ":" + o.Status + ":" + (o.Failure ?? "UNKNOWN")).ToList();
        if (budget.HaltReason is { } halt) failures.Add("HALT:" + halt);
        // The selected lines divided into headings: the model decides which adjacent lines form one heading, C# checks the division.
        var grouping = AgentGrouping.Group(source, selected, model, budget, parallelism, ct, retryDelayMs, instruction);
        answers = answers.Concat(grouping.Answers.Select(a => JsonSerializer.Serialize(new { grouping = a }, Compact))).ToArray();
        failures.AddRange(grouping.Failures);
        return new HeadingSelection(selected, complete.Length, outcomes.Length, failures, answers, budget.Calls, costOf(), source.Flavor.PlacementContext(source), grouping.Groups);
    }
}
