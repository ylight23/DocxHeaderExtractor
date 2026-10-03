using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>A prepared P6P request produced by the same adapter intended for the promoted PDF route.</summary>
public sealed record PdfHeadingMembershipPreparedPack(
    string DocumentId,
    string PackId,
    int PackOrdinal,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    int MaxCompletionTokens,
    V5FreeHeadingRequestV1 Request,
    RequestLocalLocatorRegistry Registry,
    byte[] ProviderBody,
    string ProviderRequestHash,
    int ProviderRequestBytes);

/// <summary>Exact P05 source universe and all prepared requests for one PDF execution.</summary>
public sealed record PdfHeadingMembershipDocumentPlan(
    string DocumentId,
    string SourceSha256,
    string SourceUniverseSha256,
    int SourceOccurrenceTotal,
    int PhysicalPageTotal,
    IReadOnlyList<SemanticSourceAtom> SourceAtoms,
    IReadOnlyList<PdfHeadingMembershipPreparedPack> Packs);

/// <summary>One frozen-body provider call and the exact production parser/binder outcome.</summary>
public sealed record PdfHeadingMembershipPackExecution(
    FrozenHeaderExecutionResult Provider,
    OccurrenceLocatorResponseResult? Binding,
    string? ParseError);

/// <summary>P6R qualification-only pack: P6P's exact local binder plus read-only document handles.</summary>
public sealed record PdfStructuralIdentityResolutionPreparedPack(
    PdfHeadingMembershipPreparedPack Pack,
    IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedCorrespondenceTargetsByPrimaryAtom,
    IReadOnlySet<string> AllowedContextEvidence);

/// <summary>Prepared source-constrained P6R requests for one document. Never promotes the shared runtime.</summary>
public sealed record PdfStructuralIdentityResolutionDocumentPlan(
    PdfHeadingMembershipDocumentPlan SourcePlan,
    IReadOnlyList<PdfStructuralIdentityResolutionPreparedPack> Packs);

/// <summary>
/// Shared production candidate for the PDF heading-membership lane. It owns P05 packing, document-
/// wide neutral context, the P6N-B prompt/opaque locator request, OpenRouter body creation, and the
/// exact source registry used by ParseAndBind. Qualification and the post-promotion PDF route must
/// call this adapter; callers must not rebuild any of these layers independently.
/// </summary>
public static class PdfHeadingMembershipProductionAdapter
{
    public const string ProtocolVersion = V5FreeHeadingCandidateProtocolV1.PdfDocumentAwareBoundLocatorVersion;
    public const string LayoutAwareProtocolVersion = V5FreeHeadingCandidateProtocolV1.PdfDocumentAwareLayoutBoundLocatorVersion;
    public const string PackingPolicy = SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id;
    public const int WiderContextOccurrencesPerSide = 8;
    public const int WiderContextTextMaxChars = 240;
    public const int PageMapExcerptMaxChars = 64;
    public const int CompletionTokenCeiling = 32_768;
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static PdfHeadingMembershipDocumentPlan Prepare(
        string pdfPath, string documentId, DocumentTaskContract contract)
        => PrepareCore(pdfPath, documentId, contract, includeLayoutFacts: false);

    /// <summary>
    /// P6P-L qualification-only variant. It retains P6P's source, prompt, packing and locator
    /// grammar exactly, adding only parser-observed neutral layout facts for each occurrence.
    /// </summary>
    public static PdfHeadingMembershipDocumentPlan PrepareLayoutAware(
        string pdfPath, string documentId, DocumentTaskContract contract)
        => PrepareCore(pdfPath, documentId, contract, includeLayoutFacts: true);

    /// <summary>
    /// P6R starts from the text-only P6P request. It adds only deterministic text-correspondence
    /// candidates and read-only local-context handles; neither is selectable as a heading locator.
    /// </summary>
    public static PdfStructuralIdentityResolutionDocumentPlan PrepareStructuralIdentityResolution(
        string pdfPath, string documentId, DocumentTaskContract contract)
    {
        var sourcePlan = PrepareCore(pdfPath, documentId, contract, includeLayoutFacts: false);
        var documentAtoms = sourcePlan.SourceAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        var global = documentAtoms.Select((atom, index) => (atom, index)).ToDictionary(pair => pair.atom.Alias,
            pair => new GlobalOccurrence($"D{pair.index}", pair.atom), StringComparer.Ordinal);
        var exact = documentAtoms.GroupBy(atom => NormalizeText(atom.Text), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var whitespaceInsensitive = documentAtoms.GroupBy(atom => RemoveWhitespace(NormalizeText(atom.Text)), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var prepared = new List<PdfStructuralIdentityResolutionPreparedPack>(sourcePlan.Packs.Count);

        foreach (var sourcePack in sourcePlan.Packs)
        {
            var localByAlias = sourcePack.Registry.Atoms.Select((atom, index) => (atom.Alias, handle: sourcePack.Registry.AtomHandle(index)))
                .ToDictionary(pair => pair.Alias, pair => pair.handle, StringComparer.Ordinal);
            var correspondence = new List<object>();
            var allowedTargetsByPrimary = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var alias in sourcePack.OwnedAliases)
            {
                var atom = global[alias].Atom;
                var normalized = NormalizeText(atom.Text);
                var compact = RemoveWhitespace(normalized);
                var tierOne = exact.TryGetValue(normalized, out var exactMatches)
                    ? exactMatches.Where(candidate => candidate.Alias != alias).ToArray()
                    : [];
                var candidates = tierOne.Length != 0 ? tierOne.Select(candidate => (Atom: candidate, Tier: "NFKC_WHITESPACE")) :
                    (whitespaceInsensitive.TryGetValue(compact, out var looseMatches) ? looseMatches.Where(candidate => candidate.Alias != alias) : [])
                        .Select(candidate => (Atom: candidate, Tier: "NFKC_WHITESPACE_INSENSITIVE"));
                var materialized = candidates.OrderBy(candidate => candidate.Atom.Ordinal).ThenBy(candidate => candidate.Atom.Alias, StringComparer.Ordinal)
                    .Select(candidate =>
                    {
                        var target = global[candidate.Atom.Alias];
                        return new { target = target.Handle, page = target.Atom.Page, text = target.Atom.Text, matchTier = candidate.Tier };
                    }).ToArray();
                if (materialized.Length != 0)
                {
                    var subject = localByAlias[alias];
                    allowedTargetsByPrimary.Add(subject, materialized.Select(candidate => candidate.target).ToHashSet(StringComparer.Ordinal));
                    correspondence.Add(new { subject, candidates = materialized });
                }
            }

            var owned = sourcePack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
            var contextEvidence = sourcePack.VisibleAliases.Where(alias => !owned.Contains(alias)).Select((alias, index) =>
            {
                var atom = global[alias].Atom;
                return new { handle = $"C{index}", page = atom.Page, text = atom.Text };
            }).ToArray();
            var allowedEvidence = contextEvidence.Select(item => item.handle).ToHashSet(StringComparer.Ordinal);
            using var candidatesJson = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(correspondence, CanonicalJsonOptions));
            using var contextJson = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(contextEvidence, CanonicalJsonOptions));
            var request = V5FreeHeadingCandidateProtocolV1.ComposeStructuralIdentityResolution(sourcePack.Request,
                candidatesJson.RootElement, contextJson.RootElement);
            var body = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(request, sourcePack.MaxCompletionTokens);
            var pack = sourcePack with
            {
                Request = request, ProviderBody = body.PayloadBytes, ProviderRequestHash = body.Hash, ProviderRequestBytes = body.Bytes,
            };
            prepared.Add(new PdfStructuralIdentityResolutionPreparedPack(pack, allowedTargetsByPrimary, allowedEvidence));
        }
        return new PdfStructuralIdentityResolutionDocumentPlan(sourcePlan, prepared);
    }

    /// <summary>P6R strict parse: headings bind through the existing exact locator authority.</summary>
    public static V5FreeHeadingCandidateProtocolV1.StructuralIdentityResolutionResult ParseStructuralIdentityResolution(
        PdfStructuralIdentityResolutionPreparedPack pack, string rawResponse, int responseCap = 49_152)
    {
        ArgumentNullException.ThrowIfNull(pack); ArgumentNullException.ThrowIfNull(rawResponse);
        using var document = JsonDocument.Parse(rawResponse);
        return V5FreeHeadingCandidateProtocolV1.ParseStructuralIdentityResolution(document.RootElement,
            Encoding.UTF8.GetByteCount(rawResponse), responseCap, pack.Pack.Registry,
            Enumerable.Range(0, pack.Pack.Registry.AtomCount).ToHashSet(), pack.AllowedCorrespondenceTargetsByPrimaryAtom, pack.AllowedContextEvidence);
    }

    private static PdfHeadingMembershipDocumentPlan PrepareCore(
        string pdfPath, string documentId, DocumentTaskContract contract, bool includeLayoutFacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();

        var authority = PdfStructuredSourceAuthorityBuilder.Build(pdfPath);
        var graph = V5PdfPreflightBuilder.BuildGraph(authority, documentId);
        var graphByAlias = graph.Nodes.ToDictionary(node => node.SourceAlias, StringComparer.Ordinal);
        var atomByAlias = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var packs = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
        var documentMap = BuildDocumentMap(authority.Atoms);
        var prepared = new List<PdfHeadingMembershipPreparedPack>(packs.Count);
        var seenOwned = new HashSet<string>(StringComparer.Ordinal);
        var packOrdinal = 0;

        foreach (var pack in packs)
        {
            packOrdinal++;
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var visibleAliases = pack.Visible.Select(item => item.SourceAlias).ToArray();
            foreach (var alias in ownedAliases)
                if (!seenOwned.Add(alias)) throw new InvalidOperationException("p6p-owned-source-occurrence-duplicated");
            var ownedSet = ownedAliases.ToHashSet(StringComparer.Ordinal);
            var owned = ownedAliases.Select(alias => graphByAlias[alias]).ToArray();
            var contextOnly = visibleAliases.Where(alias => !ownedSet.Contains(alias)).Select(alias => graphByAlias[alias]).ToArray();
            var packet = new V5SemanticDecisionRequestPacketV3(owned, contextOnly, [], [], [], []);
            var registry = RequestLocalLocatorRegistry.Create(ownedAliases.Select(alias => atomByAlias[alias]).ToArray());

            // This canonical source projection is the exact owned/context/directory authority used by
            // P6N-B. The task-ontology V3 request below is used only to retain P05's frozen completion
            // budget; it is never serialized into the P6P provider body.
            var sparse = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(contract, packet, registry);
            if (includeLayoutFacts)
                sparse = AddNeutralLayoutFacts(sparse, authority, ownedAliases, visibleAliases);
            var wideContext = BuildDocumentContext(documentMap, authority.Atoms, ownedAliases);
            using var contextJson = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(wideContext));
            var request = V5FreeHeadingCandidateProtocolV1.ComposePdfDocumentAwareBoundLocator(sparse, contextJson.RootElement);
            if (request.SystemPrompt != V5FreeHeadingCandidateProtocolV1.BoundLocatorSystemPrompt)
                throw new InvalidOperationException("p6p-prompt-differs-from-p6nb");
            if (includeLayoutFacts)
                request = request with { ProtocolVersion = LayoutAwareProtocolVersion };

            var budgetRequest = V5SemanticDecisionComposerV3.Compose(contract, packet);
            var maxTokens = V5SemanticCompletionBudget.Compute(ownedAliases.Length, visibleAliases.Length,
                budgetRequest.Utf8Bytes, CompletionTokenCeiling);
            var body = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(request, maxTokens);
            prepared.Add(new PdfHeadingMembershipPreparedPack(documentId, pack.PackId, packOrdinal,
                ownedAliases, visibleAliases, maxTokens, request, registry, body.PayloadBytes, body.Hash, body.Bytes));
        }

        if (seenOwned.Count != authority.Atoms.Count || authority.Atoms.Any(atom => !seenOwned.Contains(atom.Alias)))
            throw new InvalidOperationException("p6p-p05-owned-source-conservation-failed");

        return new PdfHeadingMembershipDocumentPlan(documentId, authority.SourceSha256,
            authority.SourceUniverseSha256, authority.Atoms.Count, authority.Atoms.Select(atom => atom.Page).Distinct().Count(),
            authority.Atoms, prepared);
    }

    /// <summary>Production and qualification must bind with the registry in the prepared pack.</summary>
    public static OccurrenceLocatorResponseResult ParseAndBind(
        PdfHeadingMembershipPreparedPack pack, string rawResponse, int responseCap = 49_152)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(rawResponse);
        var bytes = Encoding.UTF8.GetByteCount(rawResponse);
        using var document = JsonDocument.Parse(rawResponse);
        return V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(document.RootElement, bytes,
            responseCap, pack.Registry, Enumerable.Range(0, pack.Registry.AtomCount).ToHashSet());
    }

    /// <summary>
    /// Executes one exact prepared body and immediately routes its content through the production
    /// P6N parser/binder. No semantic retries, repair, or fallback occur here; only the executor's
    /// already-configured transport-only retry policy applies.
    /// </summary>
    public static async Task<PdfHeadingMembershipPackExecution> ExecuteAndBindAsync(
        PdfHeadingMembershipPreparedPack pack, IFrozenRequestHeaderClassifier executor,
        int responseCap = 49_152, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(executor);
        var provider = await executor.ExecuteFrozenRequestAsync(pack.ProviderBody, pack.MaxCompletionTokens,
            pack.Request.SystemPrompt, pack.Request.UserMessage, cancellationToken).ConfigureAwait(false);
        if (string.Equals(provider.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            return new PdfHeadingMembershipPackExecution(provider, null, "finish-reason-length");
        try
        {
            var binding = ParseAndBind(pack, provider.Content, responseCap);
            return new PdfHeadingMembershipPackExecution(provider, binding, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return new PdfHeadingMembershipPackExecution(provider, null, exception.Message);
        }
    }

    private sealed record PageView(int Page, int SourceOrderStart, int SourceOrderEnd, int OccurrenceCount, string FirstText, string LastText);
    private sealed record DocumentMap(int SourceOccurrenceTotal, int PhysicalPageTotal, IReadOnlyList<PageView> Pages,
        IReadOnlyDictionary<string, IReadOnlyList<(int Page, int SourceOrder)>> RepeatedTextPositions);

    private static DocumentMap BuildDocumentMap(IReadOnlyList<SemanticSourceAtom> atoms)
    {
        var ordered = atoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        var pages = ordered.GroupBy(atom => atom.Page).OrderBy(group => group.Key).Select(group =>
        {
            var pageAtoms = group.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
            return new PageView(group.Key, pageAtoms[0].Ordinal, pageAtoms[^1].Ordinal, pageAtoms.Length,
                Excerpt(pageAtoms[0].Text, PageMapExcerptMaxChars), Excerpt(pageAtoms[^1].Text, PageMapExcerptMaxChars));
        }).ToArray();
        var repeated = ordered.GroupBy(atom => NormalizeText(atom.Text), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<(int Page, int SourceOrder)>)group.OrderBy(atom => atom.Ordinal)
                    .ThenBy(atom => atom.Page).Select(atom => (atom.Page, atom.Ordinal)).ToArray(), StringComparer.Ordinal);
        return new DocumentMap(ordered.Length, pages.Length, pages, repeated);
    }

    private static object BuildDocumentContext(DocumentMap map, IReadOnlyList<SemanticSourceAtom> allAtoms,
        IReadOnlyList<string> ownedAliases)
    {
        var ordered = allAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        var indexByAlias = ordered.Select((atom, index) => (atom.Alias, index)).ToDictionary(item => item.Alias, item => item.index, StringComparer.Ordinal);
        var owned = ownedAliases.Select(alias => ordered[indexByAlias[alias]]).OrderBy(atom => atom.Ordinal).ToArray();
        var first = indexByAlias[owned[0].Alias]; var last = indexByAlias[owned[^1].Alias];
        var ownedTexts = owned.Select(atom => NormalizeText(atom.Text)).Where(text => text.Length > 0).ToHashSet(StringComparer.Ordinal);
        var repeats = map.RepeatedTextPositions.Where(pair => ownedTexts.Contains(pair.Key)).Select(pair => new
        {
            normalizedText = pair.Key,
            positions = pair.Value.Select(position => new { page = position.Page, sourceOrder = position.SourceOrder }).ToArray(),
        }).ToArray();
        var before = ordered.Skip(Math.Max(0, first - WiderContextOccurrencesPerSide)).Take(Math.Min(WiderContextOccurrencesPerSide, first))
            .Select(atom => Excerpt(atom.Text, WiderContextTextMaxChars)).ToArray();
        var after = ordered.Skip(last + 1).Take(WiderContextOccurrencesPerSide)
            .Select(atom => Excerpt(atom.Text, WiderContextTextMaxChars)).ToArray();

        return new
        {
            sourceOccurrenceTotal = map.SourceOccurrenceTotal,
            physicalPageTotal = map.PhysicalPageTotal,
            currentOwned = new
            {
                occurrenceCount = owned.Length,
                sourceOrderStart = owned.Min(atom => atom.Ordinal), sourceOrderEnd = owned.Max(atom => atom.Ordinal),
                pageStart = owned.Min(atom => atom.Page), pageEnd = owned.Max(atom => atom.Page),
            },
            pageMap = map.Pages,
            repeatedTextPositions = repeats,
            before,
            after,
        };
    }

    private static V5SparseCandidateModelRequestV1 AddNeutralLayoutFacts(
        V5SparseCandidateModelRequestV1 sparse,
        PdfStructuredSourceAuthority authority,
        IReadOnlyList<string> ownedAliases,
        IReadOnlyList<string> visibleAliases)
    {
        var ownedSet = ownedAliases.ToHashSet(StringComparer.Ordinal);
        var contextAliases = visibleAliases.Where(alias => !ownedSet.Contains(alias)).ToArray();
        var bodyFont = PdfSourceEvidence.Median(authority.Contexts.Values.Select(context => context.Source.FontSize));
        using var source = JsonDocument.Parse(sparse.UserMessage);
        var root = JsonNode.Parse(source.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("p6p-layout-source-request-invalid");
        var owned = root["ownedSubjects"]?.AsArray()
            ?? throw new InvalidOperationException("p6p-layout-owned-subjects-missing");
        var contextOnly = root["contextOnlyEvidence"]?.AsArray()
            ?? throw new InvalidOperationException("p6p-layout-context-only-missing");
        if (owned.Count != ownedAliases.Count || contextOnly.Count != contextAliases.Length)
            throw new InvalidOperationException("p6p-layout-occurrence-order-mismatch");

        for (var index = 0; index < owned.Count; index++)
            owned[index]!.AsObject()["layoutFacts"] = JsonSerializer.SerializeToNode(
                LayoutFacts(authority.Contexts[AliasSourceId(authority, ownedAliases[index])].Source, bodyFont), CanonicalJsonOptions);
        for (var index = 0; index < contextOnly.Count; index++)
            contextOnly[index]!.AsObject()["layoutFacts"] = JsonSerializer.SerializeToNode(
                LayoutFacts(authority.Contexts[AliasSourceId(authority, contextAliases[index])].Source, bodyFont), CanonicalJsonOptions);

        root["protocolVersion"] = LayoutAwareProtocolVersion;
        var message = root.ToJsonString(CanonicalJsonOptions);
        return sparse with
        {
            ProtocolVersion = LayoutAwareProtocolVersion,
            UserMessage = message,
            UserMessageUtf8Bytes = Encoding.UTF8.GetByteCount(message),
            UserMessageSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(message))),
        };
    }

    private static string AliasSourceId(PdfStructuredSourceAuthority authority, string alias) =>
        authority.Atoms.Single(atom => atom.Alias == alias).SourceId;

    private static object LayoutFacts(PdfSourceFacts source, double bodyFontSize) => new
    {
        page = source.Page,
        verticalPosition = source.VerticalPosition is { } vertical ? (double?)Round(vertical, 3) : null,
        left = Round(source.Left, 2),
        right = Round(source.Right, 2),
        width = Round(Math.Max(0, source.Right - source.Left), 2),
        lineCount = source.LineCount,
        boldRatio = Round(source.BoldRatio, 3),
        fontSizeToBodyRatio = bodyFontSize > 0 && source.FontSize > 0 ? (double?)Round(source.FontSize / bodyFontSize, 3) : null,
        sameNormalizedTextPageCount = source.SameNormalizedTextPageCount,
        sameNormalizedTextFirstPage = source.SameNormalizedTextFirstPage,
        sameNormalizedTextLastPage = source.SameNormalizedTextLastPage,
    };

    private static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    private static string NormalizeText(string text)
    {
        var builder = new StringBuilder(text.Length); var pendingSpace = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(rune.ToString());
        }
        return builder.ToString().ToUpperInvariant();
    }

    private static string RemoveWhitespace(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    private sealed record GlobalOccurrence(string Handle, SemanticSourceAtom Atom);

    private static string Excerpt(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var end = maxChars;
        if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        return text[..end];
    }
}
