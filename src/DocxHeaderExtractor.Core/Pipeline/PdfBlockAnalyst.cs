using System.Text.Json;
using System.Diagnostics;
using System.Text;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

internal enum PdfBlockRole
{
    DocumentTitle,
    HeadingTopic,
    ListItem,
    BodySentence,
    TableOrChartLabel,
    DecorativeNoise,
    Uncertain,
}

internal enum PdfSemanticRole
{
    DocumentTitle, SectionHeading, TopicHeading, LocalSubheading,
    LegalChapter, LegalSection, LegalArticle, LegalClause, LegalPoint, AppendixHeading,
    MeetingSection, AgendaItem, NoteHeading,
    TableTitle, TableHeader, FigureTitle, FigureCaption, ListItemTopic, RunningHeader, RunningFooter, FormLabel,
    SignatureLabel, TranslationNotice, BodyText, Unknown,
}

internal sealed record PdfBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Reason,
    DocxHeaderExtractor.Core.Models.TextOffsetSpan? HeadingSpan = null,
    string? ProposedParentId = null,
    PdfSemanticRole SemanticRole = PdfSemanticRole.Unknown,
    DocxHeaderExtractor.Core.Models.TextOffsetSpan? ProposedSourceSpan = null,
    string? RawRole = null,
    bool AliasNormalized = false,
    string SpanResponseStatus = "not-requested",
    DocxHeaderExtractor.Core.Models.TextOffsetSpan? ProposedSpan = null);

internal sealed record PdfBlockAnalysis(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyList<PdfBlockDecision> Decisions,
    IReadOnlyList<string> RawResponses)
{
    public IReadOnlyList<string> InputContracts { get; init; } = [];
    public IReadOnlyList<PdfSpanRequestInstrumentation> SpanRequestInstrumentation { get; init; } = [];
    public IReadOnlySet<string> HeadingBlockIds => Decisions
        .Where(d => d.Role == PdfBlockRole.HeadingTopic && d.Confidence >= 0.65)
        .Select(d => d.Id)
        .ToHashSet(StringComparer.Ordinal);
}

internal sealed record PdfPointerSpanParseResult(
    IReadOnlyList<(string Id, DocxHeaderExtractor.Core.Models.TextOffsetSpan? Span)> Spans,
    IReadOnlyDictionary<string, string> StatusById,
    IReadOnlyDictionary<string, DocxHeaderExtractor.Core.Models.TextOffsetSpan?> ProposedSpanById,
    bool ParseFault = false);

/// <summary>Independent execution budget for the semantic lane; visual has its own lifecycle.</summary>
public sealed record SemanticLaneOptions(
    TimeSpan RequestTimeout,
    TimeSpan BatchTimeout,
    TimeSpan LaneDeadline,
    int MaxConcurrency = 1,
    DateTimeOffset? DeadlineUtc = null,
    int MaxBatchSize = 0)
{
    public static readonly SemanticLaneOptions Default = new(
        TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(5));

    public TimeSpan RemainingOr(TimeSpan requested)
    {
        if (DeadlineUtc is not { } deadline) return requested;
        return TimeSpan.FromTicks(Math.Max(0, Math.Min(requested.Ticks, (deadline - DateTimeOffset.UtcNow).Ticks)));
    }
}

/// <summary>
/// LLM analyst for PDF semantic blocks. Deterministic code has already read PDF text, filtered
/// obvious table/repeat/page-number lines, and grouped remaining lines into blocks. The model only
/// classifies each block's semantic role; production routes must still ground and gate any result.
/// </summary>
internal static class PdfBlockAnalyst
{
    internal const int InternalSafePointerPromptUtf8Bytes = 800_000;

    private const string SystemPrompt =
        "You classify candidate PDF text blocks for document outline extraction.\n" +
        "Deterministic code has already removed obvious page numbers, repeated headers/footers, and numeric table noise.\n" +
        "For each block, choose exactly one closed semantic role: document_title, section_heading, topic_heading, local_subheading, legal_chapter, legal_section, legal_article, legal_clause, legal_point, appendix_heading, meeting_section, agenda_item, note_heading, table_title, table_header, figure_title, figure_caption, list_item_topic, running_header, running_footer, form_label, signature_label, translation_notice, body_text, or unknown.\n" +
        "A domain_role_hint is parser evidence, not a request to generate text. Treat amendment_annotation, inline_clause_reference, form_field_label, outline_reference, table_title, and running_artifact as non-heading roles even when visually prominent.\n" +
        "Do not mark a block heading_topic merely because it is bold/uppercase. Prefer heading_topic for concise topic labels such as 'AVAILABILITY OF INFORMATION'.\n" +
        "Classify numbered or indented prose as list_item_topic only when the source facts show a list marker or list layout; numbering alone must not authorize a structural element. This is role pass only. Do not infer heading text, pointer spans, levels, or parents.\n" +
        "Return one compact strict JSON object for every input id. Omit explanations unless needed.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"role\":\"closed_role\",\"confidence\":0.0}]}";

    private const string PointerSpanSystemPrompt =
        "You receive PDF source blocks already proposed as heading-like. Return only a source pointer span for each id.\n" +
        "For a block with candidates, choose exactly one supplied candidate_id or null; never return start/end. The code resolves candidate_id to the parser-owned span.\n" +
        "For other blocks, choose start only from allowed_start_offsets and end only from allowed_end_offsets supplied for that block.\n" +
        "Never rewrite, normalize, or return heading text or source_slice. Unknown candidate_id, malformed output, or injected text is unresolved.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"candidate_id\":\"c2\"}]}.";

    private const string CriticSystemPrompt =
        "You audit heading proposals that already have a valid source pointer. Decide whether each proposal should remain a document-outline heading.\n" +
        "Use source text and local context. Reject table labels, captions, body claims, inline references, and decorative labels.\n" +
        "Keep only a standalone topic that opens or organizes document content. If evidence conflicts or is insufficient, choose unresolved.\n" +
        "Return strict JSON only: {\"blocks\":[{\"id\":\"b1\",\"decision\":\"keep|reject|unresolved\"}]}.";

    internal static string PromptProfileSha256 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(SystemPrompt + PointerSpanSystemPrompt + CriticSystemPrompt))).ToLowerInvariant();

    public static async Task<PdfBlockAnalysis> AnalyzeAsync(
        IHeaderClassifier classifier,
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts = null,
        CancellationToken ct = default,
        SemanticLaneOptions? laneOptions = null,
        PdfStageCheckpoint? checkpoint = null)
    {
        if (blocks.Count == 0) return new PdfBlockAnalysis(blocks, [], []);

        if (checkpoint is not null &&
            blocks.All(block => checkpoint.TryGetSemanticDecision(block.Id, out _)))
        {
            return new PdfBlockAnalysis(blocks, blocks.Select(block =>
            {
                checkpoint.TryGetSemanticDecision(block.Id, out var saved);
                return new PdfBlockDecision(block.Id, saved.Role, saved.Confidence, saved.Reason,
                    SemanticRole: saved.SemanticRole, ProposedSourceSpan: saved.ProposedSourceSpan);
            }).ToArray(), []);
        }

        if (blocks.Count > 12)
        {
            var configuredBatchSize = (laneOptions?.MaxBatchSize ?? 0) > 0
                ? laneOptions!.MaxBatchSize
                : contexts is null ? 12 : 8;
            var batches = blocks.Chunk(Math.Max(1, configuredBatchSize)).ToArray();
            var partials = new PdfBlockAnalysis[batches.Length];
            var maximumConcurrency = Math.Max(1, (laneOptions ?? SemanticLaneOptions.Default).MaxConcurrency);
            await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = maximumConcurrency,
                CancellationToken = ct,
            }, async (index, _) =>
            {
                var batch = batches[index];
                var batchContexts = contexts is null ? null : batch
                    .Where(block => contexts.ContainsKey(block.Id))
                    .ToDictionary(block => block.Id, block => contexts[block.Id], StringComparer.Ordinal);
                var effectiveOptions = laneOptions ?? SemanticLaneOptions.Default;
                var batchTimeout = effectiveOptions.RemainingOr(effectiveOptions.BatchTimeout);
                PdfBlockAnalysis partial;
                if (batchTimeout <= TimeSpan.Zero)
                {
                    partial = new PdfBlockAnalysis(batch, batch.Select(block => new PdfBlockDecision(
                        block.Id, PdfBlockRole.Uncertain, 0, "semantic_batch_timeout")).ToArray(), []);
                }
                else
                {
                    using var batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    batchDeadline.CancelAfter(batchTimeout);
                    try
                    {
                        partial = await AnalyzeAsync(classifier, batch, batchContexts, batchDeadline.Token, laneOptions, checkpoint);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        partial = new PdfBlockAnalysis(batch, batch.Select(block => new PdfBlockDecision(
                            block.Id, PdfBlockRole.Uncertain, 0, "semantic_batch_timeout")).ToArray(), []);
                    }
                }
                if (checkpoint is not null)
                    await checkpoint.RecordSemanticBatchAsync(batch, partial.Decisions, ct);
                partials[index] = partial;
            });
            return new PdfBlockAnalysis(blocks, partials.SelectMany(partial => partial.Decisions).ToArray(), partials.SelectMany(partial => partial.RawResponses).ToArray())
            {
                InputContracts = batches
                    .Select(batch => BuildUserPrompt(batch, contexts is null ? null : batch
                        .Where(block => contexts.ContainsKey(block.Id))
                        .ToDictionary(block => block.Id, block => contexts[block.Id], StringComparer.Ordinal)))
                    .ToArray(),
            };
        }

        string raw;
        var inputContracts = new List<string>();
        try
        {
            var prompt = BuildUserPrompt(blocks, contexts);
            inputContracts.Add(prompt);
            var options = laneOptions ?? SemanticLaneOptions.Default;
            var requestTimeout = options.RemainingOr(options.RequestTimeout);
            if (requestTimeout <= TimeSpan.Zero) throw new OperationCanceledException(ct);
            var request = await PdfLaneExecution.RunAsync(
                requestCt => classifier.BoundaryCutAsync(SystemPrompt, prompt, requestCt), requestTimeout, ct);
            if (request.TimedOut) throw new OperationCanceledException(ct);
            if (request.Cancelled) throw new OperationCanceledException(ct);
            if (request.Fault is not null) throw request.Fault;
            raw = request.Value!;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PdfBlockAnalysis(blocks, [], []);
        }

        var decisions = ParseDecisions(raw, blocks).ToList();
        var rawResponses = new List<string> { raw };
        var missing = blocks.Where(block => decisions.All(decision => decision.Id != block.Id)).ToArray();
        if (missing.Length > 0)
        {
            // Closed JSON occasionally omits an ID. Retry only that bounded set; a missing answer
            // must become an explicit Uncertain proposal, never an invisible extraction loss.
            try
            {
                var retry = await classifier.BoundaryCutAsync(
                    SystemPrompt + "\nReturn a decision for every supplied id; no ids may be omitted.",
                    AddRetryPrompt(missing, contexts, inputContracts), ct);
                rawResponses.Add(retry);
                decisions.AddRange(ParseDecisions(retry, missing));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The explicit uncertainty below preserves the failure in the audit trace.
            }
        }

        foreach (var block in blocks.Where(block => decisions.All(decision => decision.Id != block.Id)))
            decisions.Add(new PdfBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "missing-model-decision"));

        return new PdfBlockAnalysis(blocks, decisions, rawResponses) { InputContracts = inputContracts };
    }

    /// <summary>
    /// Pass 2 of the 9B contract. It runs only after semantic triage and returns offsets into the
    /// immutable source block, never model-generated title text. A missing or invalid pointer is
    /// intentionally left null for the validator to mark unresolved.
    /// </summary>
    public static async Task<PdfBlockAnalysis> ResolveHeadingSpansAsync(
        IHeaderClassifier classifier,
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> roleDecisions,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        CancellationToken ct = default,
        PdfStageCheckpoint? checkpoint = null,
        TimeSpan? configuredRequestTimeout = null,
        DateTimeOffset? deadlineUtc = null)
    {
        var byId = roleDecisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var headingBlocks = blocks.Where(block =>
            byId.TryGetValue(block.Id, out var decision) && decision.Role == PdfBlockRole.HeadingTopic).ToArray();
        if (headingBlocks.Length == 0) return new PdfBlockAnalysis(blocks, roleDecisions, []);
        var explicitPairMenuIds = headingBlocks
            .Where(block => byId[block.Id].SemanticRole is
                PdfSemanticRole.LegalChapter or PdfSemanticRole.LegalSection or
                PdfSemanticRole.LegalArticle or PdfSemanticRole.SectionHeading)
            .Select(block => block.Id)
            .ToHashSet(StringComparer.Ordinal);

        var rawResponses = new List<string>();
        var inputContracts = new List<string>();
        var requestInstrumentation = new List<PdfSpanRequestInstrumentation>();
        var batchIndex = 0;
        var pendingBatches = headingBlocks.Chunk(4).ToList();
        while (pendingBatches.Count > 0)
        {
            var batch = pendingBatches[0];
            pendingBatches.RemoveAt(0);
            ct.ThrowIfCancellationRequested();
            var compactSourceMenuIds = new HashSet<string>(StringComparer.Ordinal);
            var initialPrompt = BuildPointerSpanPrompt(batch, contexts, explicitPairMenuIds);
            if (Encoding.UTF8.GetByteCount(initialPrompt) > InternalSafePointerPromptUtf8Bytes)
            {
                if (batch.Length > 1)
                {
                    var midpoint = batch.Length / 2;
                    pendingBatches.Insert(0, batch[midpoint..]);
                    pendingBatches.Insert(0, batch[..midpoint]);
                    continue;
                }

                if (explicitPairMenuIds.Contains(batch[0].Id))
                {
                    compactSourceMenuIds.Add(batch[0].Id);
                    initialPrompt = BuildPointerSpanPrompt(batch, contexts, explicitPairMenuIds, compactSourceMenuIds);
                }

                if (Encoding.UTF8.GetByteCount(initialPrompt) > InternalSafePointerPromptUtf8Bytes)
                {
                    batchIndex++;
                    var guardStartedUtc = DateTimeOffset.UtcNow;
                    var guardStartedTimestamp = Stopwatch.GetTimestamp();
                    var guardSourceIds = batch.Select(block => block.Id).ToArray();
                    var guardSourceOrdinals = batch.Select(block => contexts.TryGetValue(block.Id, out var context)
                        ? context.Source.SourceOrdinal
                        : null).ToArray();
                    var guardRoles = batch.Select(block => byId[block.Id].SemanticRole.ToString()).ToArray();
                    var guardCounts = AllowedSpanCounts(batch, explicitPairMenuIds, compactSourceMenuIds);
                    requestInstrumentation.Add(BuildSpanRequestInstrumentation(
                        Guid.NewGuid().ToString("N"), batchIndex, guardSourceIds, guardSourceOrdinals, guardRoles,
                        initialPrompt.Length, Encoding.UTF8.GetByteCount(initialPrompt), guardCounts, 0,
                        guardStartedUtc, guardStartedTimestamp, configuredRequestTimeout, deadlineUtc,
                        "preflight-guard", null, false, ct.IsCancellationRequested, false, null, null,
                        "pointer prompt exceeds internal safe UTF-8 ceiling after source compaction"));
                    foreach (var block in batch)
                    {
                        if (!byId.TryGetValue(block.Id, out var decision)) continue;
                        byId[block.Id] = decision with { SpanResponseStatus = "request-guard-rejected" };
                    }
                    continue;
                }
            }

            batchIndex++;
            var requestId = Guid.NewGuid().ToString("N");
            var startedUtc = DateTimeOffset.UtcNow;
            var startedTimestamp = Stopwatch.GetTimestamp();
            var cancellationRequestedBefore = ct.IsCancellationRequested;
            var sourceIds = batch.Select(block => block.Id).ToArray();
            var sourceOrdinals = batch.Select(block => contexts.TryGetValue(block.Id, out var context)
                ? context.Source.SourceOrdinal
                : null).ToArray();
            var semanticRoles = batch.Select(block => byId[block.Id].SemanticRole.ToString()).ToArray();
            var allowedSpanCountPerSource = AllowedSpanCounts(batch, explicitPairMenuIds, compactSourceMenuIds);
            const int sourceSliceCharsTotal = 0;
            var promptChars = 0;
            var promptUtf8Bytes = 0;
            string outcome = "unknown-fault";
            Exception? failure = null;
            string? raw = null;
            var responseReceived = false;
            int? responseBytes = null;
            PdfPointerSpanParseResult? parsed = null;
            var semanticExtentMenus = batch
                .Where(block => explicitPairMenuIds.Contains(block.Id))
                .ToDictionary(block => block.Id, block => PdfSemanticExtentCandidateMenu.For(
                    block.Text,
                    contexts.TryGetValue(block.Id, out var context) ? context.Source.SourceTextRuns : null),
                    StringComparer.Ordinal);
            try
            {
                var prompt = compactSourceMenuIds.Count == 0
                    ? initialPrompt
                    : BuildPointerSpanPrompt(batch, contexts, explicitPairMenuIds, compactSourceMenuIds);
                promptChars = prompt.Length;
                promptUtf8Bytes = Encoding.UTF8.GetByteCount(prompt);
                inputContracts.Add(prompt);
                ct.ThrowIfCancellationRequested();
                raw = await classifier.BoundaryCutAsync(PointerSpanSystemPrompt, prompt, ct);
                responseReceived = true;
                responseBytes = Encoding.UTF8.GetByteCount(raw);
                // A provider may ignore cancellation. A late response must not become a durable
                // span fact after the lane deadline has already been crossed.
                ct.ThrowIfCancellationRequested();
                parsed = ParsePointerSpanResponses(raw, batch, explicitPairMenuIds, compactSourceMenuIds,
                    semanticExtentMenus);
                outcome = parsed.ParseFault ? "parse-fault" : "success";
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
                outcome = ct.IsCancellationRequested ? "cancelled" : "timeout";
                requestInstrumentation.Add(BuildSpanRequestInstrumentation(
                    requestId, batchIndex, sourceIds, sourceOrdinals, semanticRoles, promptChars,
                    promptUtf8Bytes, allowedSpanCountPerSource, sourceSliceCharsTotal, startedUtc,
                    startedTimestamp, configuredRequestTimeout, deadlineUtc, outcome, failure,
                    cancellationRequestedBefore, ct.IsCancellationRequested, responseReceived,
                    responseBytes, parsed));
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
                outcome = ClassifySpanRequestFailure(ex);
                requestInstrumentation.Add(BuildSpanRequestInstrumentation(
                    requestId, batchIndex, sourceIds, sourceOrdinals, semanticRoles, promptChars,
                    promptUtf8Bytes, allowedSpanCountPerSource, sourceSliceCharsTotal, startedUtc,
                    startedTimestamp, configuredRequestTimeout, deadlineUtc, outcome, failure,
                    cancellationRequestedBefore, ct.IsCancellationRequested, responseReceived,
                    responseBytes, parsed));
                // Still swallowed - changing that is a behaviour question, not this one. But the
                // batch no longer disappears: the exception type is a fact this frame already holds,
                // and without it a failed span batch is indistinguishable from a healthy one.
                if (checkpoint is not null)
                    await checkpoint.RecordSpanBatchAsync(
                        batch.Select(b => (b.Id, b.Page, LineIdOf(b), LineIdsOf(b), (TextOffsetSpan?)null)).ToArray(),
                        ex.GetType().Name, ct);
                foreach (var block in batch)
                {
                    if (!byId.TryGetValue(block.Id, out var decision)) continue;
                    byId[block.Id] = decision with { SpanResponseStatus = "request-failed" };
                }
                continue;
            }

            rawResponses.Add(raw!);
            foreach (var (id, span) in parsed!.Spans)
            {
                if (!byId.TryGetValue(id, out var decision)) continue;
                byId[id] = decision with
                {
                    HeadingSpan = span,
                    SpanResponseStatus = parsed.StatusById.GetValueOrDefault(id, "null"),
                    ProposedSpan = parsed.ProposedSpanById.GetValueOrDefault(id),
                };
            }

            foreach (var block in batch)
            {
                if (!byId.TryGetValue(block.Id, out var decision)) continue;
                byId[block.Id] = decision with
                {
                    SpanResponseStatus = parsed.StatusById.GetValueOrDefault(block.Id, "null"),
                    ProposedSpan = parsed.ProposedSpanById.GetValueOrDefault(block.Id),
                };
            }

            requestInstrumentation.Add(BuildSpanRequestInstrumentation(
                requestId, batchIndex, sourceIds, sourceOrdinals, semanticRoles, promptChars,
                promptUtf8Bytes, allowedSpanCountPerSource, sourceSliceCharsTotal, startedUtc,
                startedTimestamp, configuredRequestTimeout, deadlineUtc, outcome, failure,
                cancellationRequestedBefore, ct.IsCancellationRequested, responseReceived,
                responseBytes, parsed));

            if (checkpoint is not null)
            {
                ct.ThrowIfCancellationRequested();
                await checkpoint.RecordSpanBatchAsync(
                        batch.Select(b => (b.Id, b.Page,
                        LineIdOf(b),
                        LineIdsOf(b),
                        byId.TryGetValue(b.Id, out var d) ? d.HeadingSpan : null)).ToArray(),
                    null, ct);
            }
        }

        return new PdfBlockAnalysis(blocks, blocks.Where(block => byId.ContainsKey(block.Id)).Select(block => byId[block.Id]).ToArray(), rawResponses)
        { InputContracts = inputContracts, SpanRequestInstrumentation = requestInstrumentation };
    }

    private static PdfSpanRequestInstrumentation BuildSpanRequestInstrumentation(
        string requestId,
        int batchIndex,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<int?> sourceOrdinals,
        IReadOnlyList<string> semanticRoles,
        int promptChars,
        int promptUtf8Bytes,
        IReadOnlyDictionary<string, int> allowedSpanCountPerSource,
        int sourceSliceCharsTotal,
        DateTimeOffset startedUtc,
        long startedTimestamp,
        TimeSpan? configuredRequestTimeout,
        DateTimeOffset? deadlineUtc,
        string outcome,
        Exception? failure,
        bool cancellationRequestedBefore,
        bool cancellationRequestedAfter,
        bool responseReceived,
        int? responseBytes,
        PdfPointerSpanParseResult? parsed,
        string? diagnosticMessage = null)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        var returned = parsed?.Spans.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var status = parsed?.StatusById ?? new Dictionary<string, string>();
        var effectiveResponseReceived = responseReceived || failure is HttpRequestException { StatusCode: not null };
        return new PdfSpanRequestInstrumentation(
            requestId, batchIndex, sourceIds, sourceOrdinals, semanticRoles, sourceIds.Count,
            promptChars, promptUtf8Bytes, allowedSpanCountPerSource.Values.Sum(), allowedSpanCountPerSource,
            sourceSliceCharsTotal, startedUtc, completedUtc,
            (long)Math.Max(0, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds),
            configuredRequestTimeout is { } timeout ? (long)timeout.TotalMilliseconds : null,
            deadlineUtc is { } deadline ? (long)Math.Max(0, (deadline - startedUtc).TotalMilliseconds) : null,
            outcome, failure?.GetType().FullName, diagnosticMessage ?? SanitizeExceptionMessage(failure),
            (failure as HttpRequestException)?.StatusCode is { } httpStatus ? (int)httpStatus : null,
            cancellationRequestedBefore, cancellationRequestedAfter, effectiveResponseReceived, responseBytes,
            returned,
            returned.Where(id => status.GetValueOrDefault(id) == "null").ToArray(),
            returned.Where(id => status.GetValueOrDefault(id) == "malformed").ToArray(),
            returned.Where(id => status.GetValueOrDefault(id) is "invalid-boundary" or "invalid-span").ToArray(),
            returned.Where(id => status.GetValueOrDefault(id) == "invalid-pair").ToArray());
    }

    private static string ClassifySpanRequestFailure(Exception exception) => exception switch
    {
        HttpRequestException => "provider-http-error",
        FormatException or JsonException => "parse-fault",
        _ => "provider-fault",
    };

    private static string? SanitizeExceptionMessage(Exception? exception)
    {
        if (exception is null) return null;
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    /// <summary>Source-line identity for a block, so a checkpoint row can be matched across runs.</summary>
    private static string? LineIdOf(PdfSemanticBlock block) =>
        block.Lines.Count == 0 ? null : PdfCandidateProvenance.LineId(block.Lines[0]);

    private static IReadOnlyList<string> LineIdsOf(PdfSemanticBlock block) =>
        block.Lines.Select(PdfCandidateProvenance.LineId).ToArray();

    /// <summary>Conflict pass for source-grounded proposals. It can only retain or lower a proposal.</summary>
    public static async Task<PdfBlockAnalysis> CritiqueHeadingProposalsAsync(
        IHeaderClassifier classifier,
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        CancellationToken ct = default)
    {
        var byId = decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var eligible = blocks.Where(block => byId.TryGetValue(block.Id, out var decision) &&
            contexts.TryGetValue(block.Id, out var context) && PdfProposalValidator.IsEligibleHeading(decision, context)).ToArray();
        var rawResponses = new List<string>();
        foreach (var batch in eligible.Chunk(6))
        {
            try
            {
                var raw = await classifier.BoundaryCutAsync(CriticSystemPrompt, BuildCriticPrompt(batch, contexts), ct);
                rawResponses.Add(raw);
                foreach (var (id, verdict) in ParseCriticDecisions(raw, batch))
                {
                    if (!byId.TryGetValue(id, out var decision)) continue;
                    byId[id] = verdict switch
                    {
                        "reject" => decision with { Role = PdfBlockRole.BodySentence, Reason = "critic-rejected" },
                        "unresolved" => decision with { Role = PdfBlockRole.Uncertain, Reason = "critic-unresolved" },
                        _ => decision,
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return new PdfBlockAnalysis(blocks, blocks.Where(block => byId.ContainsKey(block.Id)).Select(block => byId[block.Id]).ToArray(), rawResponses);
    }

    internal static string BuildUserPrompt(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts = null)
    {
        var payload = blocks.Select(b => new
        {
            id = b.Id,
            page = b.Page,
            lines = b.LineCount,
            style = new
            {
                font_size = b.PrimaryStyle.FontSizeBucket,
                font = b.PrimaryStyle.FontName,
                color = b.PrimaryStyle.FillColorKey,
            },
            source_text = PromptSourceText(b.Text),
            source_length = b.Text.Length,
            context = contexts is not null && contexts.TryGetValue(b.Id, out var context)
                ? BuildRoleContext(context)
                : null,
        });
        return JsonSerializer.Serialize(new { blocks = payload });
    }

    private static IReadOnlyDictionary<string, object> BuildRoleContext(PdfCandidateContext context)
    {
        var result = new Dictionary<string, object>
        {
            ["scope"] = context.Source.StructuralScope,
            ["domain_role_hint"] = context.Source.DomainRole.ToString(),
            ["document_regime"] = context.DocumentRegime,
            ["active_heading_stack"] = context.ActiveHeadingStack,
            ["allowed_parent_ids"] = context.AllowedParentIds,
            ["observed_facts"] = context.Source.ObservedEvidence,
            ["previous_blocks"] = context.PreviousBlocks,
            ["next_blocks"] = context.NextBlocks,
        };
        if (context.SiblingStructuralBlocks.Count > 0)
            result["sibling_structural_blocks"] = context.SiblingStructuralBlocks;
        return result;
    }

    internal static string BuildPointerSpanPrompt(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlySet<string>? explicitPairMenuIds = null,
        IReadOnlySet<string>? compactSourceMenuIds = null)
    {
        var payload = blocks.Select(block => BuildPointerSpanPromptBlock(block, contexts,
            explicitPairMenuIds?.Contains(block.Id) == true,
            compactSourceMenuIds?.Contains(block.Id) == true));
        return JsonSerializer.Serialize(new { blocks = payload });
    }

    private static object BuildPointerSpanPromptBlock(
        PdfSemanticBlock block,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        bool useExplicitPairMenu,
        bool useCompactPairMenu)
    {
        var context = contexts.TryGetValue(block.Id, out var found)
            ? new
            {
                scope = found.Source.StructuralScope,
                previous_blocks = found.PreviousBlocks,
                next_blocks = found.NextBlocks,
            }
            : null;
        if (useExplicitPairMenu)
        {
            var candidates = PdfSemanticExtentCandidateMenu.For(block.Text,
                contexts.TryGetValue(block.Id, out var sourceContext) ? sourceContext.Source.SourceTextRuns : null);
            return new
            {
                id = block.Id,
                source_text = block.Text,
                source_length = block.Text.Length,
                // The raw source is emitted once. Candidate previews remain parser-owned but are
                // bounded here so a long whole-paragraph candidate cannot repeat the full source
                // inside the provider prompt.
                candidates = candidates.Select(candidate => new
                {
                    id = candidate.Id,
                    start = candidate.Start,
                    end = candidate.End,
                    kind = candidate.Kind,
                    preview = candidate.Preview.Length <= 180
                        ? candidate.Preview
                        : candidate.Preview[..180],
                }),
                context,
            };
        }
        return new
        {
            id = block.Id,
            source_text = block.Text,
            source_length = block.Text.Length,
            allowed_start_offsets = PdfSpanBoundaryMap.For(block.Text),
            allowed_end_offsets = PdfSpanBoundaryMap.For(block.Text),
            context,
        };
    }

    private static IReadOnlyDictionary<string, int> AllowedSpanCounts(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlySet<string> explicitPairMenuIds,
        IReadOnlySet<string> compactSourceMenuIds) => blocks.ToDictionary(
            block => block.Id,
            block => explicitPairMenuIds.Contains(block.Id)
                ? PdfSemanticExtentCandidateMenu.For(block.Text).Count
                : 0,
            StringComparer.Ordinal);

    private static string BuildCriticPrompt(IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts) => JsonSerializer.Serialize(new
    {
        blocks = blocks.Select(block => new
        {
            id = block.Id,
            source_text = block.Text,
            context = contexts.TryGetValue(block.Id, out var context) ? new
            {
                scope = context.Source.StructuralScope,
                observed_facts = context.Source.ObservedEvidence,
                previous_blocks = context.PreviousBlocks,
                next_blocks = context.NextBlocks,
            } : null,
        }),
    });

    private static string PromptSourceText(string text) => text.Length <= 360 ? text : text[..360];

    private static string AddRetryPrompt(IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts, ICollection<string> contracts)
    {
        var prompt = BuildUserPrompt(blocks, contexts);
        contracts.Add(prompt);
        return prompt;
    }

    internal static IReadOnlyList<PdfBlockDecision> ParseDecisions(
        string raw,
        IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var allowed = blocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<PdfBlockDecision>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!allowed.Contains(id)) continue;

                var roleText = item.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
                var confidence = item.TryGetProperty("confidence", out var confProp) &&
                                 confProp.TryGetDouble(out var c)
                    ? Math.Clamp(c, 0, 1)
                    : 0;
                var reason = item.TryGetProperty("reason", out var reasonProp)
                    ? reasonProp.GetString() ?? ""
                    : "";
                var span = TryParseSpan(item);
                var parent = item.TryGetProperty("proposed_parent_id", out var parentProp)
                    ? parentProp.GetString()
                    : null;
                var semanticRole = ParseSemanticRole(roleText);
                var proposedSourceSpan = TryParseSpan(item, "source_span");
                result.Add(new PdfBlockDecision(id, ProjectRole(semanticRole), confidence, reason, span, parent, semanticRole, proposedSourceSpan,
                    roleText.Trim(), IsCompatibilityAlias(roleText), "not-requested"));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static DocxHeaderExtractor.Core.Models.TextOffsetSpan? TryParseSpan(JsonElement item, string propertyName = "heading_span")
    {
        if (!item.TryGetProperty(propertyName, out var span) || span.ValueKind != JsonValueKind.Object ||
            !span.TryGetProperty("start", out var start) || !start.TryGetInt32(out var from) ||
            !span.TryGetProperty("end", out var end) || !end.TryGetInt32(out var to))
            return null;
        return new DocxHeaderExtractor.Core.Models.TextOffsetSpan(from, to);
    }

    internal static IReadOnlyList<(string Id, DocxHeaderExtractor.Core.Models.TextOffsetSpan? Span)> ParsePointerSpans(
        string raw,
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlySet<string>? explicitPairMenuIds = null,
        IReadOnlySet<string>? compactSourceMenuIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<PdfSemanticExtentCandidate>>? semanticExtentMenus = null)
    {
        return ParsePointerSpanResponses(raw, blocks, explicitPairMenuIds, compactSourceMenuIds,
            semanticExtentMenus).Spans;
    }

    internal static PdfPointerSpanParseResult ParsePointerSpanResponses(
        string raw,
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlySet<string>? explicitPairMenuIds = null,
        IReadOnlySet<string>? compactSourceMenuIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<PdfSemanticExtentCandidate>>? semanticExtentMenus = null)
    {
        var byId = blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var result = new List<(string Id, DocxHeaderExtractor.Core.Models.TextOffsetSpan? Span)>();
        var statuses = blocks.ToDictionary(block => block.Id, _ => "null", StringComparer.Ordinal);
        var proposedSpans = blocks.ToDictionary(block => block.Id,
            _ => (DocxHeaderExtractor.Core.Models.TextOffsetSpan?)null, StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array)
                return new PdfPointerSpanParseResult(result,
                    statuses.ToDictionary(item => item.Key, _ => "malformed", StringComparer.Ordinal), proposedSpans, true);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!byId.TryGetValue(id, out var block)) continue;
                var useExplicitPairMenu = explicitPairMenuIds?.Contains(id) == true;

                if (useExplicitPairMenu && semanticExtentMenus?.TryGetValue(id, out var candidates) == true)
                {
                    if (item.TryGetProperty("heading_text", out _) || item.TryGetProperty("source_slice", out _) ||
                        item.TryGetProperty("start", out _) || item.TryGetProperty("end", out _) ||
                        item.TryGetProperty("heading_span", out _))
                    {
                        statuses[id] = "malformed";
                        result.Add((id, null));
                        continue;
                    }

                    if (!item.TryGetProperty("candidate_id", out var candidateId) ||
                        candidateId.ValueKind == JsonValueKind.Null)
                    {
                        statuses[id] = "null";
                        result.Add((id, null));
                        continue;
                    }

                    if (candidateId.ValueKind != JsonValueKind.String ||
                        candidates.FirstOrDefault(candidate => string.Equals(candidate.Id,
                            candidateId.GetString(), StringComparison.Ordinal)) is not { } selected)
                    {
                        statuses[id] = "invalid-candidate";
                        result.Add((id, null));
                        continue;
                    }

                    var resolved = new DocxHeaderExtractor.Core.Models.TextOffsetSpan(selected.Start, selected.End);
                    proposedSpans[id] = resolved;
                    statuses[id] = "valid-candidate";
                    result.Add((id, resolved));
                    continue;
                }

                if (!item.TryGetProperty("heading_span", out var spanElement) || spanElement.ValueKind == JsonValueKind.Null)
                {
                    statuses[id] = "null";
                    result.Add((id, null));
                    continue;
                }

                if (useExplicitPairMenu &&
                    (item.TryGetProperty("source_slice", out _) || item.TryGetProperty("heading_text", out _)))
                {
                    statuses[id] = "malformed";
                    result.Add((id, null));
                    continue;
                }

                if (spanElement.ValueKind != JsonValueKind.Object)
                {
                    statuses[id] = "malformed";
                    result.Add((id, null));
                    continue;
                }

                if (!spanElement.TryGetProperty("start", out var startElement) || !startElement.TryGetInt32(out var start) ||
                    !spanElement.TryGetProperty("end", out var endElement) || !endElement.TryGetInt32(out var end))
                {
                    statuses[id] = "malformed";
                    result.Add((id, null));
                    continue;
                }

                var span = new DocxHeaderExtractor.Core.Models.TextOffsetSpan(start, end);
                proposedSpans[id] = span;
                if (span is not null && (span.Start < 0 || span.End <= span.Start || span.End > block.Text.Length))
                {
                    statuses[id] = "invalid-span";
                    span = null;
                }
                else if (useExplicitPairMenu && span is not null &&
                    !(compactSourceMenuIds?.Contains(id) == true
                        ? PdfSpanCandidateMenu.ContainsCompact(block.Text, span)
                        : PdfSpanCandidateMenu.Contains(block.Text, span)))
                {
                    statuses[id] = "invalid-pair";
                    span = null;
                }
                else if (!useExplicitPairMenu && span is not null &&
                    (!PdfSpanBoundaryMap.Contains(block.Text, span.Start) ||
                     !PdfSpanBoundaryMap.Contains(block.Text, span.End)))
                {
                    statuses[id] = "invalid-boundary";
                    span = null;
                }
                else if (span is not null)
                {
                    statuses[id] = "valid-boundary";
                }

                result.Add((id, span));
            }
        }
        catch (JsonException)
        {
            return new PdfPointerSpanParseResult(result,
                statuses.ToDictionary(item => item.Key, _ => "malformed", StringComparer.Ordinal), proposedSpans);
        }
        return new PdfPointerSpanParseResult(result, statuses, proposedSpans);
    }

    internal static IReadOnlyList<(string Id, string Verdict)> ParseCriticDecisions(string raw,
        IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var allowed = blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<(string Id, string Verdict)>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var verdict = item.TryGetProperty("decision", out var decision) ? decision.GetString()?.Trim().ToLowerInvariant() : null;
                if (allowed.Contains(id) && verdict is "keep" or "reject" or "unresolved") result.Add((id, verdict));
            }
        }
        catch (JsonException) { return []; }
        return result;
    }

    private static PdfSemanticRole ParseSemanticRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "document_title" => PdfSemanticRole.DocumentTitle,
            "section_heading" => PdfSemanticRole.SectionHeading,
            "heading_topic" or "topic_heading" or "heading" or "topic" => PdfSemanticRole.TopicHeading,
            "local_subheading" or "region_subheading" => PdfSemanticRole.LocalSubheading,
            "legal_chapter" => PdfSemanticRole.LegalChapter,
            "legal_section" or "legal_section_heading" => PdfSemanticRole.LegalSection,
            "legal_article" => PdfSemanticRole.LegalArticle,
            "legal_clause" => PdfSemanticRole.LegalClause,
            "legal_point" => PdfSemanticRole.LegalPoint,
            "appendix_heading" => PdfSemanticRole.AppendixHeading,
            "meeting_section" or "session_heading" => PdfSemanticRole.MeetingSection,
            "agenda_item" => PdfSemanticRole.AgendaItem,
            "note_heading" => PdfSemanticRole.NoteHeading,
            "table_title" => PdfSemanticRole.TableTitle,
            "table_header" or "table_or_chart_label" or "table_label" or "chart_label" or "table" or "chart" => PdfSemanticRole.TableHeader,
            "figure_title" => PdfSemanticRole.FigureTitle,
            "figure_caption" or "box_title" => PdfSemanticRole.FigureCaption,
            "list_item_topic" or "list_item" => PdfSemanticRole.ListItemTopic,
            "running_header" => PdfSemanticRole.RunningHeader,
            "running_footer" => PdfSemanticRole.RunningFooter,
            "form_label" or "form_field_label" => PdfSemanticRole.FormLabel,
            "signature_label" => PdfSemanticRole.SignatureLabel,
            "translation_notice" => PdfSemanticRole.TranslationNotice,
            "body_sentence" or "body_text" or "body" or "prose" => PdfSemanticRole.BodyText,
            _ => PdfSemanticRole.Unknown,
        };

    private static bool IsCompatibilityAlias(string role) =>
        string.Equals(role.Trim(), "legal_section_heading", StringComparison.OrdinalIgnoreCase);

    private static PdfBlockRole ProjectRole(PdfSemanticRole role) => role switch
    {
        PdfSemanticRole.DocumentTitle or PdfSemanticRole.SectionHeading or PdfSemanticRole.TopicHeading or
        PdfSemanticRole.LocalSubheading or PdfSemanticRole.LegalChapter or PdfSemanticRole.LegalSection or
        PdfSemanticRole.LegalArticle or PdfSemanticRole.LegalClause or PdfSemanticRole.LegalPoint or
        PdfSemanticRole.AppendixHeading or PdfSemanticRole.MeetingSection or PdfSemanticRole.AgendaItem or
        PdfSemanticRole.NoteHeading => PdfBlockRole.HeadingTopic,
        PdfSemanticRole.TableTitle or PdfSemanticRole.TableHeader or PdfSemanticRole.FigureCaption => PdfBlockRole.TableOrChartLabel,
        PdfSemanticRole.ListItemTopic => PdfBlockRole.ListItem,
        PdfSemanticRole.RunningHeader or PdfSemanticRole.RunningFooter or PdfSemanticRole.FormLabel or
        PdfSemanticRole.SignatureLabel or PdfSemanticRole.TranslationNotice => PdfBlockRole.DecorativeNoise,
        PdfSemanticRole.BodyText => PdfBlockRole.BodySentence,
        _ => PdfBlockRole.Uncertain,
    };

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
