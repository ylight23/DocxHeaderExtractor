using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.PdfHeadingAgent;

/// <summary>
/// The production PDF heading selector: the V6 direct agent with the method frozen in D4.12 (docs/experiments/p7-f1-v6-d4.12-owner-trials.md,
/// reference plan of commit 7763b91), promoted on 2026-10-14. Per document:
/// <list type="bullet">
/// <item>the PDF is parsed with the production parser and cut into layout segments (whole pages, else parser layout blocks; at most 48 targets,
/// 20 lines of context each side);</item>
/// <item>each segment is one conversation with qwen/qwen3.7-flash on OpenRouter (provider alibaba only, alibaba/us-east-1 ignored, no fallback,
/// ZDR false, data collection allowed): the request shows alias, page and text and the document style table; the first reply must read the
/// layout facts through get_line_facts; at most three tool rounds, then the answer;</item>
/// <item>3 transport retries, repair turns with no limit of their own, two prompt-cache marks, json_object on every turn, the single-fence unwrap;</item>
/// <item>the budget is derived as in the owner-trial plan (every first request plus every unit's tool rounds, retries and four repairs at the
/// worst case, plus 10%).</item>
/// </list>
/// Nothing is written to disk.
/// </summary>
public sealed class PdfHeadingAgentSelector : IPdfHeadingSelector
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
    /// <param name="instruction">The user instruction sent verbatim; the frozen D4.12 instruction by default.</param>
    /// <param name="firstSegments">Diagnostics: decide only the first N segments (0 = every segment), as the D4.12 trials did for long documents.</param>
    public PdfHeadingAgentSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<string, string?>? onModelReply = null,
        string? instruction = null, int firstSegments = 0)
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

    public string SelectorId => "p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:instruction-" + AgentProtocol.Sha(instruction)[..8];

    public Task<PdfHeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return Task.Run(() =>
        {
            var source = AgentSource.Load(pdfPath, out var failure) ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
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

    /// <summary>Only the grouping step, for lines already selected: asks the model how each run of adjacent selected lines divides into headings.
    /// One request per 40 runs; none when no two selected lines are adjacent.</summary>
    public async Task<(IReadOnlyList<IReadOnlyList<string>> Groups, IReadOnlyList<string> Failures, int Posts, double CostUsd)> GroupSelectedAsync(
        string pdfPath, IReadOnlySet<string> selectedAliases, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return await Task.Run(() =>
        {
            var source = AgentSource.Load(pdfPath, out var failure) ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
            var budget = BudgetFor(source, instruction);
            using var handler = handlerFactory();
            using var model = new AgentModel(handler, apiKey, budget);
            var grouping = AgentGrouping.Group(source, selectedAliases, Traced(model), budget, parallelism, ct, retryDelayMs: 5000, instruction);
            return ((IReadOnlyList<IReadOnlyList<string>>)grouping.Groups, (IReadOnlyList<string>)grouping.Failures, grouping.Posts, budget.ActualUsd);
        }, ct);
    }

    /// <summary>The measurements the placement pass gets: the document's style table and the style of every line that has one. Facts only.</summary>
    public static PdfPlacementContext PlacementContextOf(string pdfPath)
    {
        var source = AgentSource.Load(pdfPath, out var failure) ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
        return PlacementContextOf(source);
    }

    internal static PdfPlacementContext PlacementContextOf(AgentSource source) =>
        new(new AgentTools(source).DocumentStyles().ToJsonString(Compact),
            source.LayoutFacts.Where(kv => kv.Value["style"] is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value["style"]!.ToJsonString(Compact), StringComparer.Ordinal));

    /// <summary>The budget of one document: one first post per segment plus every segment's tool rounds, transport retries and four repairs, and
    /// the reserve of every first request plus those spare posts at the byte cap, plus 10%.</summary>
    internal static AgentBudget BudgetFor(AgentSource source, string instruction = AgentProtocol.Instruction)
    {
        var units = source.Cores.Count;
        var posts = units + units * AgentProtocol.MaxToolTurns + units * ExtraPostsPerUnit;
        var styles = new AgentTools(source).DocumentStyles();
        var first = Enumerable.Range(0, units).Sum(n =>
        {
            var (decide, shown) = source.Scope(n);
            var payload = AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(n), decide, shown.Select(i => source.Lines[i]).ToArray(), styles, instruction);
            return AgentBudget.AttemptUsd(AgentProtocol.Build(payload, new System.Text.Json.Nodes.JsonArray(), "auto").Bytes);
        });
        var spare = (posts - units) * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes);
        // The grouping step: at most one request per 40 runs of adjacent selected lines (a run needs two lines, so at most one chunk per 80 lines),
        // each with its transport retries and repair turns; its requests are far smaller than a segment's.
        var groupPosts = Math.Max(1, (source.Lines.Count + 79) / 80) * (1 + AgentGrouping.MaxRepairs) * (1 + AgentUnit.TransportRetries);
        var groupReserve = groupPosts * AgentBudget.AttemptUsd(60000);
        return new AgentBudget(posts + groupPosts, Math.Ceiling((first + spare + groupReserve) * 1.1 * 100) / 100);
    }

    /// <summary>Runs every segment, parallelism at a time, and collects the lines selected by the units whose answer passed.</summary>
    internal static PdfHeadingSelection Select(AgentSource source, AgentUnit.Model model, AgentBudget budget, int parallelism, CancellationToken ct,
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
        return new PdfHeadingSelection(selected, complete.Length, outcomes.Length, failures, answers, budget.Calls, costOf(), PlacementContextOf(source), grouping.Groups);
    }
}
