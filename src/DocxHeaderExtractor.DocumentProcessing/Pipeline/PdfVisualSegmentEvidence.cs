using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Evidence owned by one visual segment atom. A parser block may contribute context, but it is
/// never the identity of this unit. This is deliberately a shadow capability: the active PDF
/// source universe and transport path do not consume it.
/// </summary>
internal sealed record PdfVisualSegmentEvidenceUnit(
    SemanticSourceAtom Atom,
    string ContextBlockId,
    string StructuralScope,
    IReadOnlyList<string> ObservedEvidence,
    IReadOnlyList<string> LocalBefore,
    IReadOnlyList<string> LocalAfter,
    bool CandidateAttention);

/// <summary>
/// Production-compatible, offline source/evidence authority for visual PDF segments.
/// <para>
/// The segment list and atom catalog are source identity. Legacy parser blocks are consulted only
/// to supply bounded context. Consequently a change in block grouping can change model-visible
/// evidence without changing the source alias universe.
/// </para>
/// </summary>
internal sealed record PdfVisualSegmentEvidenceAuthority(
    string SourceSha256,
    IReadOnlyList<PdfLine> Segments,
    IReadOnlyList<SemanticSourceAtom> Atoms,
    IReadOnlyDictionary<string, PdfCandidateContext> ContextsByAlias,
    IReadOnlyDictionary<string, PdfCandidateContext> ContextsBySourceId,
    IReadOnlyList<PdfVisualSegmentEvidenceUnit> Evidence,
    PdfBlockGrouping ContextGrouping,
    string SourceAliasUniverseSha256,
    string ModelVisibleEvidenceSha256)
{
    public const string ActiveSemanticContractSha256 =
        "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";

    public const string ShadowStructuredSourcePartsContractSha256 =
        "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";

    public const string BlockDiagnosticUniverseSha256 =
        "3b351411e1c773d14bf534f19c4ae721612716a4561396c757bc3ac35ed8b05c";

    public static PdfVisualSegmentEvidenceAuthority Build(
        string pdfPath,
        PdfBlockGrouping contextGrouping = PdfBlockGrouping.LegacyV1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        IReadOnlyList<PdfLine> segments;
        IReadOnlyList<PdfLine> legacyLines;
        using (var document = PdfDocument.Open(pdfPath))
        {
            segments = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3);
        }

        using (var document = PdfDocument.Open(pdfPath))
        {
            legacyLines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.MidpointV1);
        }

        return Build(pdfPath, segments, legacyLines, contextGrouping);
    }

    internal static PdfVisualSegmentEvidenceAuthority Build(
        string pdfPath,
        IReadOnlyList<PdfLine> segments,
        IReadOnlyList<PdfLine> legacyLines,
        PdfBlockGrouping contextGrouping = PdfBlockGrouping.LegacyV1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(legacyLines);

        var atoms = PdfSegmentAtomCatalog.FromSegments(segments);
        var annotations = PdfLineBlockFilter.Analyze(legacyLines);
        var blocks = PdfSemanticBlockGrouper.Build(
            annotations,
            includeRiskLines: true,
            grouping: contextGrouping);
        var blockContexts = PdfCandidateContextBuilder.Build(blocks, annotations);
        var segmentBlocks = segments.Select(segment => FindContextBlock(segment, blocks)).ToArray();
        var contexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var contextsBySourceId = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var evidence = new List<PdfVisualSegmentEvidenceUnit>(atoms.Count);

        for (var index = 0; index < atoms.Count; index++)
        {
            var atom = atoms[index];
            var block = segmentBlocks[index];
            var blockContext = blockContexts[block.Id];
            var facts = blockContext.Source with
            {
                SourceId = atom.Alias,
                RawText = atom.Text,
                Page = atom.Page,
                LineCount = 1,
                Left = segments[index].Left,
                TopY = segments[index].Top ?? segments[index].Y,
                Right = segments[index].Right,
                BottomY = segments[index].Bottom ?? segments[index].Y,
                LineIds = [PdfLineIdentity.Of(segments[index])],
                BoldRatio = segments[index].BoldRatio,
                ItalicRatio = segments[index].ItalicRatio,
                FontSize = segments[index].FontSize,
                Marker = PdfMarkerFactsParser.Parse(atom.Text),
            };
            var previous = atoms
                .Skip(Math.Max(0, index - 2))
                .Take(index - Math.Max(0, index - 2))
                .Select(PromptExcerpt)
                .ToArray();
            var next = atoms
                .Skip(index + 1)
                .Take(2)
                .Select(PromptExcerpt)
                .ToArray();
            var attention = !facts.ObservedEvidence.Contains("page-number", StringComparer.Ordinal) &&
                facts.StructuralScope is not ("running_page_artifact" or "table_of_contents");
            var context = new PdfCandidateContext(
                facts,
                previous,
                next,
                blockContext.AllowedParentIds,
                blockContext.DocumentRegime,
                blockContext.ActiveHeadingStack)
            {
                SiblingStructuralBlocks = blockContext.SiblingStructuralBlocks,
            };
            contexts.Add(atom.Alias, context);
            contextsBySourceId.Add(atom.SourceId, context);
            evidence.Add(new PdfVisualSegmentEvidenceUnit(
                atom,
                block.Id,
                facts.StructuralScope,
                facts.ObservedEvidence,
                previous,
                next,
                attention));
        }

        return new PdfVisualSegmentEvidenceAuthority(
            CanonicalSemanticSourceHash.Compute(pdfPath),
            segments,
            atoms,
            contexts,
            contextsBySourceId,
            evidence,
            contextGrouping,
            HashSourceAliases(atoms),
            HashVisibleEvidence(evidence));
    }

    private static PdfSemanticBlock FindContextBlock(
        PdfLine segment,
        IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var match = blocks
            .SelectMany(block => block.Lines.Select(line => (Block: block, Line: line)))
            .Where(item => SameBand(segment, item.Line))
            .OrderByDescending(item => HorizontalOverlap(segment, item.Line))
            .ThenBy(item => Math.Abs(segment.Y - item.Line.Y))
            .ThenBy(item => item.Block.Id, StringComparer.Ordinal)
            .Select(item => item.Block)
            .FirstOrDefault();
        if (match is not null) return match;

        return blocks
            .Where(block => block.Page == segment.Page)
            .OrderBy(block => Math.Abs(segment.Y - block.TopY))
            .ThenBy(block => block.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No context block for PDF segment at page {segment.Page}, y={segment.Y:R}.");
    }

    private static bool SameBand(PdfLine first, PdfLine second) =>
        first.Page == second.Page &&
        Math.Min(first.Top ?? first.Y, second.Top ?? second.Y) >=
            Math.Max(first.Bottom ?? first.Y, second.Bottom ?? second.Y) - 0.01 &&
        HorizontalOverlap(first, second) > 0;

    private static double HorizontalOverlap(PdfLine first, PdfLine second) =>
        Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);

    private static string PromptExcerpt(SemanticSourceAtom atom) =>
        $"[{atom.Alias}] {PromptExcerpt(atom.Text)}";

    private static string PromptExcerpt(string text) => text.Length <= 180 ? text : text[..180];

    private static string HashSourceAliases(IReadOnlyList<SemanticSourceAtom> atoms) =>
        Hash(new
        {
            schemaVersion = "a99-pdf-visual-segment-source-alias-universe-v1",
            rows = atoms.Select(atom => new
            {
                alias = atom.Alias,
                sourceId = atom.SourceId,
                ordinal = atom.Ordinal,
                page = atom.Page,
                row = atom.Row,
                segment = atom.Segment,
                text = atom.Text,
            }).ToArray(),
        });

    private static string HashVisibleEvidence(IReadOnlyList<PdfVisualSegmentEvidenceUnit> evidence) =>
        Hash(new
        {
            schemaVersion = "a99-pdf-visual-segment-model-evidence-v1",
            rows = evidence.Select(unit => new
            {
                alias = unit.Atom.Alias,
                sourceId = unit.Atom.SourceId,
                ordinal = unit.Atom.Ordinal,
                page = unit.Atom.Page,
                row = unit.Atom.Row,
                segment = unit.Atom.Segment,
                text = unit.Atom.Text,
                contextBlockId = unit.ContextBlockId,
                structuralScope = unit.StructuralScope,
                observedEvidence = unit.ObservedEvidence,
                localBefore = unit.LocalBefore,
                localAfter = unit.LocalAfter,
                candidateAttention = unit.CandidateAttention,
            }).ToArray(),
        });

    private static string Hash(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

internal sealed record PdfVisualSegmentRequestPack(
    int Index,
    IReadOnlyList<string> OwnedAliases,
    IReadOnlyList<string> VisibleAliases,
    string PayloadJson);

internal sealed record PdfVisualSegmentRequestPlan(
    int OwnedPerPack,
    int VisibleMargin,
    IReadOnlyList<PdfVisualSegmentRequestPack> Packs,
    string CallPlanHash,
    string RequestPlanHash,
    string SourcePartsSchemaHash)
{
    public static PdfVisualSegmentRequestPlan Build(
        PdfVisualSegmentEvidenceAuthority authority,
        int ownedPerPack = 120,
        int visibleMargin = 20)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (ownedPerPack <= 0) throw new ArgumentOutOfRangeException(nameof(ownedPerPack));
        if (visibleMargin < 0) throw new ArgumentOutOfRangeException(nameof(visibleMargin));

        var packs = new List<PdfVisualSegmentRequestPack>();
        for (var start = 0; start < authority.Evidence.Count; start += ownedPerPack)
        {
            var owned = authority.Evidence.Skip(start).Take(ownedPerPack).ToArray();
            var visibleStart = Math.Max(0, start - visibleMargin);
            var visible = authority.Evidence
                .Skip(visibleStart)
                .Take(owned.Length + visibleMargin * 2)
                .ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                protocolVersion = SemanticSourcePartsContract.ProtocolVersion,
                sourcePartsSchemaHash = SemanticSourcePartsContract.SchemaHash(),
                ownedSourceAliases = owned.Select(item => item.Atom.Alias).ToArray(),
                sourceEvidence = visible.Select(ToPayload).ToArray(),
            }, JsonOptions());
            packs.Add(new PdfVisualSegmentRequestPack(
                packs.Count,
                owned.Select(item => item.Atom.Alias).ToArray(),
                visible.Select(item => item.Atom.Alias).ToArray(),
                payload));
        }

        var callPlanHash = Hash(new
        {
            schemaVersion = "a99-pdf-visual-segment-call-plan-v1",
            ownedPerPack,
            visibleMargin,
            packs = packs.Select(pack => new
            {
                index = pack.Index,
                ownedAliases = pack.OwnedAliases,
                visibleAliases = pack.VisibleAliases,
            }).ToArray(),
        });
        var requestPlanHash = Hash(new
        {
            schemaVersion = "a99-pdf-visual-segment-request-plan-v1",
            packs = packs.Select(pack => new { index = pack.Index, payload = pack.PayloadJson }).ToArray(),
        });
        return new PdfVisualSegmentRequestPlan(
            ownedPerPack,
            visibleMargin,
            packs,
            callPlanHash,
            requestPlanHash,
            SemanticSourcePartsContract.SchemaHash());
    }

    private static object ToPayload(PdfVisualSegmentEvidenceUnit unit) => new
    {
        sourceAlias = unit.Atom.Alias,
        sourceId = unit.Atom.SourceId,
        ordinal = unit.Atom.Ordinal,
        page = unit.Atom.Page,
        row = unit.Atom.Row,
        segment = unit.Atom.Segment,
        text = unit.Atom.Text,
        structuralScope = unit.StructuralScope,
        observedEvidence = unit.ObservedEvidence,
        localBefore = unit.LocalBefore,
        localAfter = unit.LocalAfter,
        candidateAttention = unit.CandidateAttention,
    };

    private static JsonSerializerOptions JsonOptions() => new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string Hash(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
