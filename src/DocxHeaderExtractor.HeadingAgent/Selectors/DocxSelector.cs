using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>
/// The DOCX heading selector: the V6 method over the paragraphs of a DOCX. The file is read with the production OOXML reader (with the route's
/// extraction options, so that its aliases are the ones the route's source snapshot gives) and cut into fixed windows of 48 paragraphs with 20
/// of context on each side; the first request shows alias, text, the style table and targetFacts with the paragraphs' existing
/// core facts. The facts tool remains available for other paragraphs. The conversation is <see cref="AgentSelector"/>'s.
/// </summary>
public sealed class DocxSelector : AgentSelector, DocxHeaderExtractor.Contracts.Agents.IPrimedDocumentSelector
{
    readonly ExtractionOptions? extraction;
    readonly int boundarySnapParagraphs;
    readonly string? selectionCacheRoot;
    readonly bool qualificationFormatBoundaryEvidence;
    readonly bool qualificationFormatRunEvidence;
    readonly bool qualificationBatchExtentErrors;
    readonly string? qualificationGroupingInstruction;
    readonly bool splitNumberedGroups;

    /// <param name="extraction">The route's extraction options (for example tables left out); null reads everything.</param>
    /// <inheritdoc cref="AgentSelector"/>
    public DocxSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<ModelReply>? onModelReply = null,
        string? instruction = null, int firstSegments = 0, ExtractionOptions? extraction = null, int boundarySnapParagraphs = 0,
        string? selectionCacheRoot = null, bool qualificationFormatBoundaryEvidence = false,
        string? qualificationGroupingInstruction = null, bool qualificationBatchExtentErrors = false, bool qualificationFormatRunEvidence = false,
        bool splitNumberedGroups = false)
        : base(handlerFactory, parallelism, onModelReply, instruction ?? DocxAgentProtocol.Instruction, firstSegments)
    {
        if (boundarySnapParagraphs is < 0 or >= DocxAgentSource.Unit) throw new ArgumentOutOfRangeException(nameof(boundarySnapParagraphs));
        this.extraction = extraction;
        this.boundarySnapParagraphs = boundarySnapParagraphs;
        this.qualificationFormatBoundaryEvidence = qualificationFormatBoundaryEvidence;
        this.qualificationFormatRunEvidence = qualificationFormatRunEvidence;
        this.qualificationBatchExtentErrors = qualificationBatchExtentErrors;
        this.qualificationGroupingInstruction = qualificationGroupingInstruction;
        this.splitNumberedGroups = splitNumberedGroups;
        // Include implementation identities because later tool responses are not in the first request hash.
        this.selectionCacheRoot = selectionCacheRoot is null ? null : Path.Combine(selectionCacheRoot,
            "docx-reuse-v1-" + AgentProtocol.Sha(DocxAgentSource.Version + ":" + DocxAgentSource.FactsVersion + ":" + DocxAgentSource.StylesVersion + ":" + BindingPolicyVersion
                + ":" + typeof(DocxAgentSource).Module.ModuleVersionId
                + ":" + typeof(DocxHeaderExtractor.Sources.Docx.DocxSourceIntegrity).Module.ModuleVersionId
                + ":" + typeof(ExtractionOptions).Module.ModuleVersionId
                + ":" + typeof(DocxHeaderExtractor.Agents.Budget.AgentBudget).Module.ModuleVersionId
                + (qualificationBatchExtentErrors ? ":qualification-batch-extent-errors-v1" : ""))[..16]);
    }

    private protected override string? SelectionCacheRoot => selectionCacheRoot;
    // The complete first request includes the whole-file SHA. A different file therefore misses this cache, including when its visible segment
    // is unchanged. Tool calls can read outside the segment, so removing that SHA from the key alone would not make cross-version reuse safe.
    private protected override bool CacheByRequestOnly => true;
    private protected override bool SplitSeparatelyNumberedGroups => splitNumberedGroups;
    internal static string DefaultGroupingInstruction(string instruction) => instruction + "\n" + DocxAgentProtocol.GroupingBoundaryGuidance;
    private protected override string GroupingInstruction => qualificationGroupingInstruction ?? DefaultGroupingInstruction(InstructionText);

    public override string SelectorId => "docx-v6:" + DocxAgentSource.Version + ":facts-in-request:instruction-" + InstructionTag + ":" + BindingPolicyVersion + ":group-selected-text-v1"
        + (boundarySnapParagraphs == 0 ? "" : ":boundary-snap-backward-v1-n" + boundarySnapParagraphs)
        + (qualificationFormatBoundaryEvidence ? ":qualification-format-boundaries-v1" : "")
        + (qualificationFormatRunEvidence ? ":qualification-format-runs-v1" : "")
        + (qualificationBatchExtentErrors ? ":qualification-batch-extent-errors-v1" : "")
        + ":group-boundary-guidance-v1"
        + (splitNumberedGroups ? ":group-split-numbered-v1" : "");

        private (string Full, DocxHeaderExtractor.Contracts.Docx.ExtractionOptions? Options, DocxHeaderExtractor.Contracts.Models.SourceDocument Document)? primed;
    private readonly object primedLock = new();

    /// <inheritdoc/>
    public void PrimeDocument(string path, DocxHeaderExtractor.Contracts.Docx.ExtractionOptions? extraction, DocxHeaderExtractor.Contracts.Models.SourceDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        lock (primedLock) primed = (System.IO.Path.GetFullPath(path), extraction, document);
    }

    // A DOCX selection call covers the whole document's lines and can take longer than the shared 300-second default (075: 40 lines in one call timed out,
    // so no decision was applied and the outline came back empty). As for the PDF selector, the caller's cancellation token still stops the run.
    private protected override TimeSpan? RequestTimeout => System.Threading.Timeout.InfiniteTimeSpan;

    private protected override IAgentSource Load(string path)
    {
        (string Full, DocxHeaderExtractor.Contracts.Docx.ExtractionOptions? Options, DocxHeaderExtractor.Contracts.Models.SourceDocument Document)? take;
        lock (primedLock) { take = primed; primed = null; }
        if (take is { } given && given.Full == System.IO.Path.GetFullPath(path) && ReferenceEquals(given.Options, extraction))
        {
            var source = DocxAgentSource.From(given.Document, out var builtFailure, boundarySnapParagraphs)
                ?? throw new InvalidOperationException("DOCX_AGENT_SOURCE_REFUSED:" + builtFailure);
            source.QualificationFormatBoundaryEvidence = qualificationFormatBoundaryEvidence;
            source.QualificationFormatRunEvidence = qualificationFormatRunEvidence;
            source.QualificationBatchExtentErrors = qualificationBatchExtentErrors;
            source.Integrity = DocxHeaderExtractor.Sources.Docx.DocxSourceIntegrity.Read(path);
            return source;
        }
        var loaded = DocxAgentSource.Load(path, extraction, out var failure, boundarySnapParagraphs) ?? throw new InvalidOperationException("DOCX_AGENT_SOURCE_REFUSED:" + failure);
        loaded.QualificationFormatBoundaryEvidence = qualificationFormatBoundaryEvidence;
        loaded.QualificationFormatRunEvidence = qualificationFormatRunEvidence;
        loaded.QualificationBatchExtentErrors = qualificationBatchExtentErrors;
        return loaded;
    }
}
