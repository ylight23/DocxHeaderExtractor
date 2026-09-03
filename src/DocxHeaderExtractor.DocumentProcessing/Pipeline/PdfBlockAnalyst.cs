using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

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
    DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan? HeadingSpan = null,
    string? ProposedParentId = null,
    PdfSemanticRole SemanticRole = PdfSemanticRole.Unknown,
    DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan? ProposedSourceSpan = null);

internal sealed record PdfBlockAnalysis(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyList<PdfBlockDecision> Decisions,
    IReadOnlyList<string> RawResponses)
{
    public IReadOnlyList<string> InputContracts { get; init; } = [];
    public IReadOnlySet<string> HeadingBlockIds => Decisions
        .Where(d => d.Role == PdfBlockRole.HeadingTopic && d.Confidence >= 0.65)
        .Select(d => d.Id)
        .ToHashSet(StringComparer.Ordinal);
}

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
        "The span must select exactly the heading prefix inside source_text using zero-based start and exclusive end offsets.\n" +
        "Never rewrite, normalize, or return heading text. If a heading span cannot be determined from source_text, return null.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":19}}]}.";

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
        PdfStageCheckpoint? checkpoint = null)
    {
        var byId = roleDecisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var headingBlocks = blocks.Where(block =>
            byId.TryGetValue(block.Id, out var decision) && decision.Role == PdfBlockRole.HeadingTopic).ToArray();
        if (headingBlocks.Length == 0) return new PdfBlockAnalysis(blocks, roleDecisions, []);

        var rawResponses = new List<string>();
        var inputContracts = new List<string>();
        foreach (var batch in headingBlocks.Chunk(4))
        {
            string raw;
            try
            {
                var prompt = BuildPointerSpanPrompt(batch, contexts);
                inputContracts.Add(prompt);
                ct.ThrowIfCancellationRequested();
                raw = await classifier.BoundaryCutAsync(PointerSpanSystemPrompt, prompt, ct);
                // A provider may ignore cancellation. A late response must not become a durable
                // span fact after the lane deadline has already been crossed.
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Still swallowed - changing that is a behaviour question, not this one. But the
                // batch no longer disappears: the exception type is a fact this frame already holds,
                // and without it a failed span batch is indistinguishable from a healthy one.
                if (checkpoint is not null)
                    await checkpoint.RecordSpanBatchAsync(
                        batch.Select(b => (b.Id, b.Page, LineIdOf(b), LineIdsOf(b), (TextOffsetSpan?)null)).ToArray(),
                        ex.GetType().Name, ct);
                continue;
            }

            rawResponses.Add(raw);
            foreach (var (id, span) in ParsePointerSpans(raw, batch))
            {
                if (!byId.TryGetValue(id, out var decision)) continue;
                byId[id] = decision with { HeadingSpan = span };
            }

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
        { InputContracts = inputContracts };
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
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        var payload = blocks.Select(block => new
        {
            id = block.Id,
            source_text = block.Text,
            source_length = block.Text.Length,
            context = contexts.TryGetValue(block.Id, out var context)
                ? new
                {
                    scope = context.Source.StructuralScope,
                    previous_blocks = context.PreviousBlocks,
                    next_blocks = context.NextBlocks,
                }
                : null,
        });
        return JsonSerializer.Serialize(new { blocks = payload });
    }

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
                result.Add(new PdfBlockDecision(id, ProjectRole(semanticRole), confidence, reason, span, parent, semanticRole, proposedSourceSpan));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan? TryParseSpan(JsonElement item, string propertyName = "heading_span")
    {
        if (!item.TryGetProperty(propertyName, out var span) || span.ValueKind != JsonValueKind.Object ||
            !span.TryGetProperty("start", out var start) || !start.TryGetInt32(out var from) ||
            !span.TryGetProperty("end", out var end) || !end.TryGetInt32(out var to))
            return null;
        return new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(from, to);
    }

    internal static IReadOnlyList<(string Id, DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan? Span)> ParsePointerSpans(
        string raw,
        IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var allowed = blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<(string Id, DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan? Span)>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!allowed.Contains(id)) continue;
                result.Add((id, TryParseSpan(item)));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
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
            "legal_section" => PdfSemanticRole.LegalSection,
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
