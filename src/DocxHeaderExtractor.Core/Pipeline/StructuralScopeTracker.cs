using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Tracks document-local namespaces from source order before any model proposal.</summary>
internal sealed class StructuralScopeTracker
{
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
        var appendix = AppendixRx.IsMatch(text);
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
        if (opened && !closed) _insideQuote = true;
        var scope = facts.StructuralScope;
        if (scope == "table" && _appendix) scope = "appendix_table";
        else if (_insideQuote || wasInsideQuote) scope = _amendmentHost is null ? "quoted_replacement" : "embedded_amendment";
        else if (_referenceList && !referencesHeading && scope == "document_body") scope = "reference_list";
        else if (_indexTerms && !indexHeading && scope == "document_body") scope = "index_terms";
        else if (_appendix && scope == "document_body") scope = "appendix";
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
