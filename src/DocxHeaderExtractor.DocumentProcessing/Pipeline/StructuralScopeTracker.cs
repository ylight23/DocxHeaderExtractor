using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Tracks document-local namespaces from source order before any model proposal.</summary>
internal sealed class StructuralScopeTracker
{
    /// <summary>
    /// Optional passive record of how each block's scope was decided. The tracker carries latched
    /// state across blocks, so a scope read in isolation says nothing about why it was assigned; the
    /// sink exists so an audit can ask that without keeping a second copy of the state machine.
    /// </summary>
    private readonly List<StructuralScopeTransition>? _trace;

    /// <summary>
    /// Evaluation-only. Named blocks whose appendix entry is withheld, so a counterfactual can ask
    /// what a single reviewed transition caused. Empty in production, and deliberately not a rule:
    /// the set is a reviewed list of source ids, not a predicate anything could learn.
    /// </summary>
    private readonly IReadOnlySet<string> _withheldAppendixEntries;

    /// <summary>
    /// Evaluation-only, and separate from the appendix set because the two latches fail differently:
    /// the appendix latch has no exit, while the quote latch has one that some documents cannot
    /// reach. Sharing a set would invite treating them as one defect. Empty in production.
    /// </summary>
    private readonly IReadOnlySet<string> _withheldQuoteEntries;

    public StructuralScopeTracker(
        List<StructuralScopeTransition>? trace = null,
        IReadOnlySet<string>? withheldAppendixEntries = null,
        IReadOnlySet<string>? withheldQuoteEntries = null)
    {
        _trace = trace;
        _withheldAppendixEntries = withheldAppendixEntries ?? new HashSet<string>(StringComparer.Ordinal);
        _withheldQuoteEntries = withheldQuoteEntries ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private static readonly Regex AppendixRx = new(@"^\s*(?:appendix|annex|phu\s+luc)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmendmentRx = new(@"(?:amended\s+as\s+follows|replaced\s+as\s+follows|amend(?:ed|ment).*?as\s+follows|sua\s+doi.*?nhu\s+sau|duoc\s+sua\s+doi|bo\s+sung.*?nhu\s+sau)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TargetRx = new(@"\b(?:law|decree|nghi\s+dinh)\s+(?:no\.?\s*)?[A-Z0-9./-]{4,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReferencesHeadingRx = new(@"^\s*(?:\d+(?:\.\d+)*\.?\s+)?references\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IndexHeadingRx = new(@"^\s*(?:\d+(?:\.\d+)*\.?\s+)?index\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private bool _insideQuote;
    private bool _appendix;
    private bool _referenceList;
    private bool _indexTerms;
    private string? _amendmentHost;
    private string? _targetDocument;

    public PdfSourceFacts Apply(PdfSourceFacts facts)
    {
        var text = facts.RawText;
        var appendix = AppendixRx.IsMatch(text) && !_withheldAppendixEntries.Contains(facts.SourceId);
        var referencesHeading = ReferencesHeadingRx.IsMatch(text);
        var indexHeading = IndexHeadingRx.IsMatch(text);
        if (appendix)
        {
            _appendix = true;
            _referenceList = false;
            _indexTerms = false;
        }
        var amendment = AmendmentRx.IsMatch(text);
        if (amendment)
        {
            _amendmentHost = facts.SourceId;
            _targetDocument = TargetRx.Match(text) is { Success: true } target ? target.Value : null;
        }

        var opened = text.Count(character => character == '\u201c') + text.Count(character => character == '"') % 2 > 0;
        var closed = text.Any(character => character is '\u201d' or '\u201f');
        var wasInsideQuote = _insideQuote;
        if (opened && !closed && !_withheldQuoteEntries.Contains(facts.SourceId)) _insideQuote = true;
        var scope = facts.StructuralScope;
        if (scope == "table" && _appendix) scope = "appendix_table";
        else if (_insideQuote || wasInsideQuote) scope = _amendmentHost is null ? "quoted_replacement" : "embedded_amendment";
        else if (_referenceList && !referencesHeading && scope == "document_body") scope = "reference_list";
        else if (_indexTerms && !indexHeading && scope == "document_body") scope = "index_terms";
        else if (_appendix && scope == "document_body") scope = "appendix";
        _trace?.Add(new StructuralScopeTransition(
            facts.SourceId, facts.StructuralScope, scope,
            AppendixLatched: _appendix,
            AppendixTriggeredHere: appendix,
            ReferenceListLatched: _referenceList,
            IndexTermsLatched: _indexTerms,
            InsideQuote: _insideQuote || wasInsideQuote,
            AmendmentTriggeredHere: amendment,
            Page: facts.Page,
            RawText: text)
        {
            QuoteStateBefore = wasInsideQuote,
            QuoteOpened = opened,
            QuoteClosed = closed,
            LeftCurlyQuotes = text.Count(character => character == '\u201c'),
            RightCurlyQuotes = text.Count(character => character is '\u201d' or '\u201f'),
            StraightQuotes = text.Count(character => character == '"'),
        });
        var result = facts with
        {
            StructuralScope = scope,
            ScopeHostSourceId = scope == "embedded_amendment" ? _amendmentHost : null,
            ScopeTargetDocument = scope == "embedded_amendment" ? _targetDocument : null,
            InsideQuote = _insideQuote || wasInsideQuote,
            AmendmentOperation = amendment ? "replace_or_amend" : null,
        };
        if (referencesHeading) _referenceList = true;
        if (indexHeading) _indexTerms = true;
        if (closed) _insideQuote = false;
        return result;
    }
}

/// <summary>
/// One block as the scope tracker saw it: the scope it arrived with, the scope it left with, and the
/// latch state that decided the difference. Observed facts only - whether a transition was correct is
/// a judgement for evaluation to derive.
/// </summary>
internal sealed record StructuralScopeTransition(
    string SourceId,
    string IncomingScope,
    string ResultingScope,
    bool AppendixLatched,
    bool AppendixTriggeredHere,
    bool ReferenceListLatched,
    bool IndexTermsLatched,
    bool InsideQuote,
    bool AmendmentTriggeredHere,
    int Page,
    string RawText)
{
    /// <summary>The latch as this block found it, before this block's own quote facts applied.</summary>
    public bool QuoteStateBefore { get; init; }

    /// <summary>The open and close conditions the tracker actually evaluated for this block.</summary>
    public bool QuoteOpened { get; init; }

    public bool QuoteClosed { get; init; }

    /// <summary>
    /// The raw quote-character counts the conditions were computed from. Recorded separately because
    /// the open and close conditions do not read the same characters, and an audit that saw only the
    /// booleans could not tell whether a block failed to close or was never able to.
    /// </summary>
    public int LeftCurlyQuotes { get; init; }

    public int RightCurlyQuotes { get; init; }

    public int StraightQuotes { get; init; }
}
