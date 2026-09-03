namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Selects represented PDF blocks that existing deterministic candidate producers left unresolved.
/// This is source-fact-only routing: it has no key, expected-title, or model dependency.
/// </summary>
internal static class PdfSemanticRecoverySelector
{
    internal static PdfSemanticRecoverySelection Select(
        IReadOnlyList<PdfSemanticBlock> representedBlocks,
        IReadOnlyList<PdfSemanticBlock> deterministicCandidates,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        PdfSemanticRecoveryOptions? options = null)
    {
        options ??= PdfSemanticRecoveryOptions.CurrentV6;
        var contexts = PdfCandidateContextBuilder.Build(representedBlocks, annotations, options.ContextWindow);
        // Resolution is occurrence-specific. A title may occur in the TOC, a running artifact,
        // and the body; never suppress a source block merely because another block has the same
        // canonical text.
        var resolvedIds = deterministicCandidates
            .Select(block => block.Id)
            .ToHashSet(StringComparer.Ordinal);
        var eligibleSources = representedBlocks
            .Where(block => !resolvedIds.Contains(block.Id))
            .Where(block => block.CanonicalText.Length >= 4 && block.Lines.Count > 0)
            .Where(block => contexts.TryGetValue(block.Id, out var context) && !IsHardExcluded(context.Source))
            // A deterministic producer can only safely emit blocks with a visible boundary.
            // This deliberately admits the complementary, bounded ambiguity: a concise first
            // source line followed by prose in the same visual block. It remains a proposal,
            // never a heading decision.
            .Where(HasWeakHeadingPrefix)
            .Where(HasRecoveryTitleShape)
            .OrderBy(block => block.Page)
            .ThenByDescending(block => block.TopY)
            .ThenBy(block => block.Id, StringComparer.Ordinal)
            .ToArray();
        var recovery = eligibleSources.Select((block, index) => CreateLineRecovery(block,
            WithSiblingStructuralContext(contexts[block.Id], eligibleSources, index, options))).ToArray();
        return new PdfSemanticRecoverySelection(representedBlocks.Count, deterministicCandidates.Count,
            recovery.Select(item => item.Block).ToArray(),
            recovery.ToDictionary(item => item.Block.Id, item => item.Context, StringComparer.Ordinal),
            recovery.ToDictionary(item => item.Block.Id, item => item.Origin, StringComparer.Ordinal));
    }

    private static PdfCandidateContext WithSiblingStructuralContext(
        PdfCandidateContext context,
        IReadOnlyList<PdfSemanticBlock> eligibleSources,
        int index,
        PdfSemanticRecoveryOptions options)
    {
        if (!options.IncludeSiblingStructuralContext) return context;

        // These are only nearby, independently title-shaped unresolved source blocks. They are
        // context, not model or deterministic heading claims.
        var siblings = eligibleSources.Take(index).TakeLast(2)
            .Concat(eligibleSources.Skip(index + 1).Take(2))
            .Select(block => $"{block.Id}: {PdfTextUtilities.Readable(block.Lines[0].MatchText ?? block.Lines[0].Text)}")
            .ToArray();
        return context with { SiblingStructuralBlocks = siblings };
    }

    internal static bool IsHardExcluded(PdfSourceFacts source) =>
        source.StructuralScope is "table" or "running_page_artifact" or "table_of_contents" or
            "code_or_grammar" or "reference_list" or "index_terms" ||
        source.DomainEvidence.ProposesOutlineExclusion;

    internal static bool HasWeakHeadingPrefix(PdfSemanticBlock block)
    {
        if (block.Lines.Count is < 2 or > 8) return false;

        var prefix = PdfTextUtilities.Readable(block.Lines[0].MatchText ?? block.Lines[0].Text).Trim();
        var remainder = string.Join(" ", block.Lines.Skip(1)
            .Select(line => PdfTextUtilities.Readable(line.MatchText ?? line.Text))).Trim();
        if (prefix.Length is < 4 or > 140 || remainder.Length < 24) return false;
        if (prefix.EndsWith(".", StringComparison.Ordinal) || prefix.EndsWith('!') || prefix.EndsWith('?')) return false;
        if (prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 22) return false;

        return prefix.Any(char.IsLetter) && remainder.Any(char.IsLetter);
    }

    private static bool HasRecoveryTitleShape(PdfSemanticBlock block)
    {
        var prefix = PdfTextUtilities.Readable(block.Lines[0].MatchText ?? block.Lines[0].Text).Trim();
        if (prefix.StartsWith('−') || prefix.StartsWith('-') || prefix.StartsWith('•')) return false;
        if (PdfMarkerFactsParser.Parse(prefix) is not null) return true;

        var words = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('(', ')', '[', ']', ',', ';', ':', '"', '\''))
            .Where(word => word.Any(char.IsLetter))
            .ToArray();
        if (words.Length is < 2 or > 16) return false;

        var titleTokens = words.Count(word => word.First(char.IsLetter) is var letter && char.IsUpper(letter));
        var letters = words.SelectMany(word => word).Where(char.IsLetter).ToArray();
        var allCaps = letters.Length > 0 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.55;
        return allCaps || titleTokens / (double)words.Length >= 0.65;
    }

    private static PdfSemanticRecoveryItem CreateLineRecovery(PdfSemanticBlock block, PdfCandidateContext context)
    {
        var line = block.Lines[0];
        var id = $"{block.Id}/line0";
        var recovery = new PdfSemanticBlock(id, [line], PdfStyleClusterProfile.StyleOf(line), line.Page,
            line.Y, line.Y, line.Left, line.Right, PdfTextUtilities.Readable(line.Text));
        var bodyContext = block.Lines.Skip(1)
            .Select(item => PdfTextUtilities.Readable(item.MatchText ?? item.Text))
            .Where(text => text.Length > 0)
            .ToArray();
        var source = context.Source with
        {
            SourceId = id,
            RawText = recovery.Text,
            Page = line.Page,
            LineCount = 1,
            Left = line.Left,
            TopY = line.Y,
            Right = line.Right,
            BottomY = line.Y,
            LineIds = [id],
        };
        var recoveryContext = context with
        {
            Source = source,
            NextBlocks = bodyContext.Concat(context.NextBlocks).Take(2).ToArray(),
        };
        return new PdfSemanticRecoveryItem(recovery, recoveryContext, new PdfSemanticRecoveryOrigin(block.Id, 0));
    }
}

internal sealed record PdfSemanticRecoverySelection(
    int RepresentedBlockCount,
    int DeterministicCandidateCount,
    IReadOnlyList<PdfSemanticBlock> EligibleBlocks,
    IReadOnlyDictionary<string, PdfCandidateContext> Contexts,
    IReadOnlyDictionary<string, PdfSemanticRecoveryOrigin> Origins);

internal sealed record PdfSemanticRecoveryOrigin(string SourceBlockId, int SourceLineIndex);

internal sealed record PdfSemanticRecoveryItem(
    PdfSemanticBlock Block,
    PdfCandidateContext Context,
    PdfSemanticRecoveryOrigin Origin);

/// <summary>
/// Frozen, source-only context/batching profiles for M7.25. They do not alter eligibility,
/// source spans, canonical mapping, or validator authority.
/// </summary>
public sealed record PdfSemanticRecoveryOptions(
    string Name,
    int ContextWindow,
    int RoleBatchSize,
    bool IncludeSiblingStructuralContext)
{
    public static readonly PdfSemanticRecoveryOptions CurrentV6 = new("current_v6", 2, 8, false);
    public static readonly PdfSemanticRecoveryOptions NeighborhoodMicroBatch = new("neighborhood_microbatch", 3, 3, true);
    public static readonly PdfSemanticRecoveryOptions NeighborhoodSingle = new("neighborhood_single", 3, 1, true);

    public static PdfSemanticRecoveryOptions Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "current_v6" => CurrentV6,
        "neighborhood_microbatch" => NeighborhoodMicroBatch,
        "neighborhood_single" => NeighborhoodSingle,
        _ => throw new ArgumentException(
            "--semantic-recovery-profile phải là current_v6, neighborhood_microbatch, hoặc neighborhood_single."),
    };
}
