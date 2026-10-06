using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// PDF-only heading authority promoted from the F1 → G2A → H2-C V2 qualification chain.
/// Each stage is strict and fail-closed: a rejected F1/G2A pack emits no headings and a rejected
/// boundary decision withholds only its anchor. This class intentionally has no semantic fallback.
/// </summary>
internal static class PdfFunctionConditionedHeadingAuthorityAdapter
{
    internal const string AuthorityId = "pdf-function-conditioned-heading-authority-v1";
    private const int ResponseCap = PdfQualifiedInferencePolicy.ResponseUtf8ByteCap;
    // Qualification serializes with the framework default encoder.  Do not use the
    // relaxed encoder here: escaping is part of the provider-body identity.
    private const int P05CompletionTokens = PdfQualifiedInferencePolicy.CompletionTokenCeiling;
    internal static readonly string G2APrompt = QualifiedPromptText.Canonicalize("""
        Decide anchor existence only. Each issued primary occurrence has an upstream ESTABLISHES_STRUCTURE eligibility signal, but that signal is not proof that a valid local structural heading extent begins at this primary.

        For every issued O#, return exactly one anchor: HAS_STRUCTURAL_EXTENT if at least one valid local structural heading extent begins at that primary; otherwise NO_STRUCTURAL_EXTENT. Do not choose or describe any extent. Do not infer an answer from context-only items.

        Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","anchor":"HAS_STRUCTURAL_EXTENT"},{"primary":"O28","anchor":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and anchor. Do not output source text, candidate IDs, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
        """);
    internal static readonly string BoundaryPromptV2 = QualifiedPromptText.Canonicalize("""
        Locate the exact boundary of the single heading occurrence that begins at the issued anchor occurrence. A heading may contain one or more consecutive source occurrences, but it ends immediately before the first occurrence that is not literally part of that same heading occurrence.

        Treat an occurrence as outside the heading when it begins a new heading, starts body or prose content, starts a table or other structured content, is page furniture, or otherwise is not literal heading text. Do not extend the heading merely because a later occurrence belongs to the same section, topic, agenda item, document region, or discusses the same subject.

        Choose the last literal heading occurrence and its immediate successor as one boundary pair: headingMembers must end at endOccurrence, and firstOutsideOccurrence must be the next issued occurrence immediately after it.

        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
        """);

    public static async Task<StructuralAuthorityResult> RunAsync(
        string pdfPath,
        IHeaderClassifier? classifier,
        SemanticLaneOptions? semanticLaneOptions,
        CancellationToken ct)
    {
        var authority = PdfStructuredSourceAuthorityBuilder.Build(pdfPath);
        if (authority.ParserLineCount == 0 || authority.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        if (classifier is null)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-function-conditioned-llm-disabled") { SourceCatalog = authority.Catalog };
        if (classifier is not IFrozenRequestHeaderClassifier frozen)
            throw new InvalidOperationException("PDF_H2C_PRODUCTION_ROUTE_REQUIRES_FROZEN_REQUEST_TRANSPORT");

        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(pdfPath));
        await checkpoint.RecordSelectionAsync(
            authority.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var execution = await PdfLaneExecution.RunAsync(
            (lease, laneCt) => RunCoreAsync(authority, frozen, lease, laneCt),
            (semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline,
            ct).ConfigureAwait(false);
        await checkpoint.StopAcceptingWritesAndDrainAsync().ConfigureAwait(false);
        if (execution.State == PdfLaneExecutionState.TimedOut)
            throw new TimeoutException("PDF semantic execution exceeded its lane deadline.");
        if (execution.State == PdfLaneExecutionState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == PdfLaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException("PDF semantic execution failed.");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException("PDF semantic result lost its execution lease.");
        return execution.Value;
    }

    private static async Task<StructuralAuthorityResult> RunCoreAsync(PdfStructuredSourceAuthority authority, IFrozenRequestHeaderClassifier frozen, PdfLaneExecutionLease lease, CancellationToken ct)
    {
        var leaseBound = new LeaseBoundFrozenHeaderClassifier(frozen, lease);
        var atoms = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var decisions = new List<PdfBlockDecision>();
        var raw = new List<string>();
        foreach (var pack in SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom))
        {
            ct.ThrowIfCancellationRequested();
            if (!lease.IsActive) throw new PdfExecutionLeaseLostException();
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var owned = ownedAliases.Select(alias => atoms[alias]).ToArray();
            var visible = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var context = visible.Where(alias => !ownedAliases.Contains(alias, StringComparer.Ordinal)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
            var f1Request = V5TotalOccurrenceFunctionProtocolF1.ComposeWithReadOnlyCorrespondences(owned, context, Correspondences(owned, authority.Atoms));
            var f1 = await ExecuteAsync(leaseBound, f1Request.SystemPrompt, f1Request.UserMessage, P05CompletionTokens, ct).ConfigureAwait(false);
            if (f1 is null) continue;
            raw.Add(f1.Content);
            V5TotalOccurrenceFunctionResultF1 functions;
            try { using var json = JsonDocument.Parse(f1.Content); functions = V5TotalOccurrenceFunctionProtocolF1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(f1.Content), ResponseCap, f1Request.Occurrences); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }
            var byId = f1Request.Occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
            var establishes = functions.Decisions.Where(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE).Select(value => byId[value.OccurrenceId]).ToArray();
            if (establishes.Length == 0) continue;

            var idByAlias = f1Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
            var g2aUser = ComposeG2AUserMessage(owned, idByAlias, establishes.Select(value => (value.Id, value.Atom)).ToArray());
            var g2aBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(G2APrompt, g2aUser, P05CompletionTokens, Envelope);
            var g2a = await ExecuteAsync(leaseBound, G2APrompt, g2aUser, P05CompletionTokens, ct, g2aBody.PayloadBytes).ConfigureAwait(false);
            if (g2a is null) continue;
            raw.Add(g2a.Content);
            HashSet<string> has;
            try { has = ParseG2A(g2a.Content, establishes.Select(value => value.Id)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }

            foreach (var anchor in establishes.Where(value => has.Contains(value.Id)))
            {
                var start = Array.IndexOf(ownedAliases, anchor.Atom.Alias);
                if (start < 0) continue;
                var tail = ownedAliases.Skip(start).ToArray(); // terminal anchors intentionally remain issued.
                var rows = tail.Select(alias => BasicOccurrence(idByAlias[alias], atoms[alias], authority.Evidence.Single(e => e.SourceAlias == alias))).ToArray();
                var user = JsonSerializer.Serialize(new { protocolVersion = "v5-function-conditioned-exact-end-pointer-clean-paired-1", anchors = new[] { new { anchor = anchor.Id, occurrences = rows } } });
                var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(BoundaryPromptV2, user, P05CompletionTokens, Envelope);
                var boundary = await ExecuteAsync(leaseBound, BoundaryPromptV2, user, P05CompletionTokens, ct, body.PayloadBytes).ConfigureAwait(false);
                if (boundary is null) continue;
                raw.Add(boundary.Content);
                try { decisions.Add(BindBoundary(boundary.Content, anchor.Id, tail, idByAlias, atoms)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            }
        }

        var validated = PdfSemanticProposalBinder.BindAndValidate(authority.Contexts, decisions);
        var bound = decisions.Select(decision => ToBound(decision, atoms)).ToArray();
        var placed = await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(bound, leaseBound, ct).ConfigureAwait(false);
        var hierarchy = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(placed);
        var structures = hierarchy.ToDictionary(item => item.SourceId, item => new PdfValidatedStructure(item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review") { StructuralScope = authority.Contexts[item.SourceId].Source.StructuralScope }, StringComparer.Ordinal);
        var occurrences = authority.Contexts.ToDictionary(pair => pair.Key, pair => new CanonicalSourceOccurrence(pair.Key, authority.OrdinalBySourceId.GetValueOrDefault(pair.Key), pair.Value.Source.RawText, null), StringComparer.Ordinal);
        var structure = CanonicalStructureMaterializer.Materialize(validated, structures, occurrences, "pdf", StructuralDecisionOrigin.Model, structures.Keys.ToHashSet(StringComparer.Ordinal));
        var sourceBlocks = authority.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray();
        var audit = CanonicalRouteAuditBoundary.Create(
            AuthorityId,
            authority.Blocks.Count,
            authority.Blocks.Count,
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            sourceBlocks,
            sourceBlocks,
            decisions.Select(decision => new RouteBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            validated.Select(item => item.SourceId).ToArray()) with
        {
            RawAnalystResponses = raw,
            ModelInputContracts = ["v5-total-occurrence-function-f1", "v5-function-conditioned-anchor-existence-1", "v5-function-conditioned-exact-end-pointer-clean-paired-1"],
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, authority.Contexts),
            SemanticLane = new RouteLaneExecutionAudit("complete", authority.Atoms.Count, decisions.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("exact-end-pointer", decisions.Count, validated.Count, 0, decisions.Count - validated.Count),
        };
        return new StructuralAuthorityResult(structure, audit, AuthorityId, structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)) { SourceCatalog = authority.Catalog };
    }

    private static CanonicalSemanticBoundHeading ToBound(PdfBlockDecision decision, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        var primary = atoms.Values.Single(atom => atom.SourceId == decision.Id);
        var parts = decision.Parts ?? [];
        return new CanonicalSemanticBoundHeading(primary.Alias, primary.SourceId, primary.Ordinal, string.Join(" ", parts.Select(value => value.Text)), "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, primary.Text.Length) { Parts = parts };
    }

    internal static PdfBlockDecision BindBoundary(string raw, string anchor, IReadOnlyList<string> tail, IReadOnlyDictionary<string, string> idByAlias, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        using var document = JsonDocument.Parse(raw); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) throw new InvalidOperationException("h2c-root-invalid");
        var item = decisions[0]; var names = item.EnumerateObject().Select(value => value.Name).OrderBy(value => value).ToArray();
        if (!names.SequenceEqual(new[] { "anchor", "endOccurrence", "firstOutsideOccurrence", "firstOutsideRole", "headingMembers" })) throw new InvalidOperationException("h2c-schema-invalid");
        if (item.GetProperty("anchor").GetString() != anchor) throw new InvalidOperationException("h2c-anchor-invalid");
        var members = item.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        var issued = tail.Select(alias => idByAlias[alias]).ToArray();
        if (members.Length == 0 || !members.SequenceEqual(issued.Take(members.Length), StringComparer.Ordinal) || item.GetProperty("endOccurrence").GetString() != members[^1]) throw new InvalidOperationException("h2c-prefix-invalid");
        var expectedOutside = members.Length == issued.Length ? null : issued[members.Length];
        var outsideProperty = item.GetProperty("firstOutsideOccurrence");
        var outside = outsideProperty.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => outsideProperty.GetString(),
            _ => throw new InvalidOperationException("h2c-successor-type-invalid"),
        };
        if (outside != expectedOutside) throw new InvalidOperationException("h2c-successor-invalid");
        var roleProperty = item.GetProperty("firstOutsideRole");
        if (roleProperty.ValueKind != JsonValueKind.String) throw new InvalidOperationException("h2c-role-type-invalid");
        var role = roleProperty.GetString();
        if (expectedOutside is null)
        {
            if (role != "NO_VISIBLE_SUCCESSOR") throw new InvalidOperationException("h2c-terminal-role-invalid");
        }
        else if (role is not ("NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING"))
        {
            throw new InvalidOperationException("h2c-nonterminal-role-invalid");
        }
        var parts = members.Select(id => atoms[tail[Array.IndexOf(issued, id)]]).Select(atom => new CanonicalSemanticBoundPart(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text, 0, atom.Text.Length)).ToArray();
        return new PdfBlockDecision(parts[0].SourceId, "pdf-exact-heading-boundary-v1", new TextOffsetSpan(0, parts[0].Text.Length), SemanticFunction: "ESTABLISHES_STRUCTURE", Parts: parts);
    }

    /// <summary>Canonical G2A request composer shared by qualification and the production PDF route.</summary>
    internal static string ComposeG2AUserMessage(IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyDictionary<string, string> idByAlias, IReadOnlyList<(string Id, SemanticSourceAtom Atom)> establishes)
    {
        var indexByAlias = owned.Select((atom, index) => (atom.Alias, index)).ToDictionary(value => value.Alias, value => value.index, StringComparer.Ordinal);
        var occurrences = establishes.Select(value =>
        {
            object? Neighbor(int index)
            {
                if (index < 0 || index >= owned.Count) return null;
                var adjacent = owned[index];
                return new { occurrence = idByAlias[adjacent.Alias], page = adjacent.Page, text = adjacent.Text, selectable = false };
            }

            var index = indexByAlias[value.Atom.Alias];
            return new
            {
                primary = value.Id,
                page = value.Atom.Page,
                text = value.Atom.Text,
                upstreamFunction = "ESTABLISHES_STRUCTURE",
                previous = Neighbor(index - 1),
                next = Neighbor(index + 1),
            };
        }).ToArray();
        return JsonSerializer.Serialize(new { protocolVersion = "v5-function-conditioned-anchor-existence-1", occurrences });
    }

    private static object BasicOccurrence(string occurrence, SemanticSourceAtom atom, CanonicalSemanticSourceEvidence evidence)
    {
        var style = JsonSerializer.SerializeToElement(evidence.StyleFacts); var location = evidence.LocationFacts is null ? default(JsonElement?) : JsonSerializer.SerializeToElement(evidence.LocationFacts);
        return new { occurrence, page = atom.Page, text = atom.Text, style = new { fontSize = style.GetProperty("fontSize"), bodyFontSize = style.GetProperty("bodyFontSize"), fontSizeToBodyRatio = style.GetProperty("fontSizeToBodyRatio"), boldRatio = style.GetProperty("boldRatio"), italicRatio = style.GetProperty("italicRatio"), lineCount = style.GetProperty("lineCount") }, location = new { verticalPosition = location?.GetProperty("verticalPosition") ?? default, sameNormalizedTextPageCount = location?.GetProperty("sameNormalizedTextPageCount") ?? default, sameNormalizedTextFirstPage = location?.GetProperty("sameNormalizedTextFirstPage") ?? default, sameNormalizedTextLastPage = location?.GetProperty("sameNormalizedTextLastPage") ?? default } };
    }

    private static async Task<FrozenHeaderExecutionResult?> ExecuteAsync(IFrozenRequestHeaderClassifier classifier, string prompt, string user, int maxTokens, CancellationToken ct, byte[]? body = null)
    {
        body ??= OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(prompt, user, maxTokens, Envelope).PayloadBytes;
        var result = await classifier.ExecuteFrozenRequestAsync(body, maxTokens, prompt, user, ct).ConfigureAwait(false);
        return string.Equals(result.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetByteCount(result.Content) <= ResponseCap ? result : null;
    }

    private sealed class LeaseBoundFrozenHeaderClassifier : IFrozenRequestHeaderClassifier
    {
        private readonly IFrozenRequestHeaderClassifier _inner;
        private readonly PdfLaneExecutionLease _lease;

        public LeaseBoundFrozenHeaderClassifier(IFrozenRequestHeaderClassifier inner, PdfLaneExecutionLease lease)
        {
            _inner = inner;
            _lease = lease;
        }

        public string ModelName => _inner.ModelName;
        public int ContextSize => _inner.ContextSize;
        public string RuntimeDescription => _inner.RuntimeDescription;
        public int SharedPrefixTokens => _inner.SharedPrefixTokens;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            StartAndObserve(() => _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount));
        public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            StartAndObserve(() => _inner.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken));
        public void Dispose() { }

        private Task<T> StartAndObserve<T>(Func<Task<T>> start)
        {
            Task<T>? task = null;
            if (!_lease.TryStartDownstream(() => task = start())) throw new PdfExecutionLeaseLostException();
            return ObserveAsync(task!, _lease);
        }

        private static async Task<T> ObserveAsync<T>(Task<T> task, PdfLaneExecutionLease lease)
        {
            var result = await task.ConfigureAwait(false);
            if (!lease.IsActive) throw new PdfExecutionLeaseLostException();
            return result;
        }
    }

    internal static HashSet<string> ParseG2A(string raw, IEnumerable<string> issued)
    {
        using var document = JsonDocument.Parse(raw); var root = document.RootElement; var expected = issued.ToHashSet(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != expected.Count) throw new InvalidOperationException("g2a-cardinality-invalid");
        var result = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 ||
                !row.TryGetProperty("primary", out var primaryProperty) || primaryProperty.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("anchor", out var anchorProperty) || anchorProperty.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("g2a-schema-invalid");
            var primary = primaryProperty.GetString()!;
            var anchor = anchorProperty.GetString()!;
            if (!expected.Contains(primary) || !seen.Add(primary) || (anchor is not "HAS_STRUCTURAL_EXTENT" and not "NO_STRUCTURAL_EXTENT")) throw new InvalidOperationException("g2a-ledger-invalid");
            if (anchor == "HAS_STRUCTURAL_EXTENT") result.Add(primary);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(
        IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> all) =>
        PdfReadOnlyCorrespondenceBuilder.Build(owned, all);

    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
}
