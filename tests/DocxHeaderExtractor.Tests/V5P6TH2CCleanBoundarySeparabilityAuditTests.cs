using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free comparison of parser-observable PDF facts at Gold-internal and Gold-exit edges.</summary>
public sealed class V5P6TH2CCleanBoundarySeparabilityAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string GoldAuditPath = Root + "/p6th2c-clean-paired-gold-audit/h2c-clean-v1-v2-paired-gold-audit.v1.json";
    private const string OutputRoot = Root + "/p6th2c-clean-boundary-separability-audit";
    private static readonly IReadOnlyDictionary<string, string> PdfPaths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SRC-089"] = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf",
        ["SRC-041"] = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf",
        ["SRC-095"] = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf",
        ["DOC-0252"] = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf",
        ["DOC-0256"] = "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf",
    };

    private sealed record Geometry(
        string Alias, string SourceId, int Page, string Text, double BaselineY, double Left, double Right, double Top, double Bottom,
        double FontSize, string FontName, double BoldRatio, double ItalicRatio, double? FontFlagBoldRatio,
        double? FontNameBoldRatio, string LayoutBlockId, int LayoutBlockLineCount,
        double PageWidth, double PageHeight, double? PageMedianNormalizedGap);

    [Fact]
    public void Gold_internal_edges_and_exit_edges_have_frozen_neutral_geometry_separation_audit()
    {
        if (FrozenHistoryReplayPolicy.RichGeometry("SRC-089") == HistoricalReplayStatus.FrozenEvidenceOnly)
        {
            FrozenHistoryReplayPolicy.AssertFrozenEvidenceOnly("SRC-089", nameof(V5P6TH2CCleanBoundarySeparabilityAuditTests));
            return;
        }

        using var score = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(GoldAuditPath)));
        var scoreRows = score.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var geometryByDocument = new Dictionary<string, Dictionary<string, Geometry>>(StringComparer.Ordinal);
        var sourceOrderByDocument = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sourceAuthority = new List<object>();
        foreach (var (documentId, relativePdf) in PdfPaths)
        {
            var pdfPath = TestRepository.Path(relativePdf);
            var sourceSha = CanonicalSemanticSourceHash.Compute(pdfPath);
            IReadOnlyList<PdfLine> lines;
            Dictionary<int, (double Width, double Height)> pageSizes;
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                lines = PdfLineExtraction.ExtractLines(pdf);
                pageSizes = pdf.GetPages().ToDictionary(page => page.Number, page => (page.Width, page.Height));
            }
            var sourceBuild = PdfSourceAdapter.BuildWithDetails(lines, sourceSha);
            var authority = sourceBuild.Snapshot;
            var pdfDetails = sourceBuild.Details;
            var atoms = authority.Atoms;
            var lineBySourceId = lines.ToDictionary(PdfLineIdentity.Of, StringComparer.Ordinal);
            Assert.Equal(atoms.Count, lineBySourceId.Count);
            var blockLineCount = pdfDetails.LayoutBlockByAtom.Values.GroupBy(value => value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var pageMedianGaps = lines.GroupBy(line => line.Page).ToDictionary(group => group.Key, group =>
            {
                var ordered = group.OrderByDescending(line => line.Y).ThenBy(line => line.Left).ToArray();
                var gaps = Enumerable.Range(1, Math.Max(0, ordered.Length - 1))
                    .Select(index =>
                    {
                        var previous = ordered[index - 1];
                        var next = ordered[index];
                        var scale = Math.Max(previous.FontSize, next.FontSize);
                        return scale > 0 && previous.Y > next.Y ? (previous.Y - next.Y) / scale : (double?)null;
                    })
                    .Where(value => value is not null).Select(value => value!.Value).ToArray();
                return gaps.Length == 0 ? (double?)null : Median(gaps);
            });
            var byAlias = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            foreach (var atom in atoms)
            {
                var line = lineBySourceId[atom.SourceId];
                var facts = pdfDetails.Contexts[atom.SourceId].Source;
                var pageSize = pageSizes[line.Page];
                byAlias.Add(atom.Alias, new Geometry(atom.Alias, atom.SourceId, line.Page, atom.Text,
                    line.Y, facts.Left, facts.Right, facts.TopY, facts.BottomY, line.FontSize, line.FontName,
                    facts.BoldRatio, facts.ItalicRatio, line.Typography?.FontBoldFlagRatio,
                    line.Typography?.FontNameBoldRatio, pdfDetails.LayoutBlockByAtom[atom.SourceId],
                    blockLineCount[pdfDetails.LayoutBlockByAtom[atom.SourceId]],
                    pageSize.Width, pageSize.Height, pageMedianGaps[line.Page]));
            }
            geometryByDocument.Add(documentId, byAlias);
            sourceOrderByDocument.Add(documentId, atoms.Select(atom => atom.Alias).ToArray());
            sourceAuthority.Add(new
            {
                documentId,
                sourceSha256 = sourceSha,
                sourceAliasUniverseSha256 = authority.SourceAliasUniverseHash,
                modelVisibleEvidenceSha256 = authority.ModelVisibleEvidenceHash,
                parserLines = lines.Count,
                atoms = atoms.Count,
                layoutBlocks = pdfDetails.Blocks.Count,
                pages = pageSizes.Count,
                geometryAvailableForEveryAtom = byAlias.Count == atoms.Count,
            });
        }

        var edges = new List<Edge>();
        foreach (var row in scoreRows.Where(value => value.GetProperty("GoldExtent").ValueKind == JsonValueKind.Array))
        {
            var documentId = row.GetProperty("DocumentId").GetString()!;
            var anchor = row.GetProperty("Anchor").GetString()!;
            var extent = row.GetProperty("GoldExtent").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var atoms = sourceOrderByDocument[documentId].Select(alias => geometryByDocument[documentId][alias]).ToArray();
            var start = Array.FindIndex(atoms, value => value.Alias == extent[0]);
            Assert.True(start >= 0, $"Gold start absent from live parser source:{documentId}:{anchor}");
            Assert.True(start + extent.Length <= atoms.Length);
            Assert.Equal(extent, atoms.Skip(start).Take(extent.Length).Select(value => value.Alias).ToArray());
            for (var index = 0; index + 1 < extent.Length; index++)
                edges.Add(Describe(documentId, anchor, "INTERNAL_CONTINUE", atoms[start + index], atoms[start + index + 1]));
            var exitIndex = start + extent.Length - 1;
            if (exitIndex + 1 >= atoms.Length) continue;
            edges.Add(Describe(documentId, anchor, "GOLD_EXIT", atoms[exitIndex], atoms[exitIndex + 1]));
            for (var offset = 1; offset <= 2 && exitIndex + offset + 1 < atoms.Length; offset++)
                edges.Add(Describe(documentId, anchor, $"POST_EXIT_{offset}", atoms[exitIndex + offset], atoms[exitIndex + offset + 1]));
        }

        var internalEdges = edges.Where(edge => edge.EdgeClass == "INTERNAL_CONTINUE").ToArray();
        var exitEdges = edges.Where(edge => edge.EdgeClass == "GOLD_EXIT").ToArray();
        var postExitEdges = edges.Where(edge => edge.EdgeClass.StartsWith("POST_EXIT_", StringComparison.Ordinal)).ToArray();
        var allMeasured = internalEdges.Concat(exitEdges).ToArray();
        Assert.Equal(27, exitEdges.Length);
        FreezeArtifact.AssertJson(OutputRoot, "h2c-clean-boundary-separability-audit.v1.json", new
        {
            schemaVersion = "v5-p6th2c-clean-boundary-separability-audit-v1",
            status = "PROVIDER_FREE_GOLD_EDGE_FEATURE_CENSUS",
            sourceAuthority = sourceAuthority.ToArray(),
            cohort = new
            {
                goldTrueAnchors = exitEdges.Length,
                internalContinuationEdges = internalEdges.Length,
                goldExitEdges = exitEdges.Length,
                nearbyPostExitNegativeEdges = postExitEdges.Length,
                goldReadFrom = "previous frozen paired audit; unchanged canonical Gold hashes recorded there",
                providerCalls = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            featureSeparation = new
            {
                samePage = Compare(internalEdges, exitEdges, edge => edge.SamePage),
                sameParserLayoutBlock = Compare(internalEdges, exitEdges, edge => edge.SameLayoutBlock),
                sameFontName = Compare(internalEdges, exitEdges, edge => edge.SameFontName),
                sameFontSizeWithinHalfPoint = Compare(internalEdges, exitEdges, edge => edge.FontSizeDelta is { } delta && delta <= 0.5),
                sameBoldRatioWithinPointOne = Compare(internalEdges, exitEdges, edge => edge.BoldRatioDelta is { } delta && delta <= 0.1),
                sameItalicRatioWithinPointOne = Compare(internalEdges, exitEdges, edge => edge.ItalicRatioDelta is { } delta && delta <= 0.1),
                sameFontBoldEvidenceWithinPointOne = Compare(internalEdges, exitEdges, edge => edge.FontBoldFlagRatioDelta is { } delta && delta <= 0.1),
                sameCapitalizationPattern = Compare(internalEdges, exitEdges, edge => edge.SameCapitalizationPattern),
                normalizedVerticalGapInFontSizes = CompareNumeric(internalEdges, exitEdges, edge => edge.VerticalGapInFontSizes),
                gapOverPageLocalMedian = CompareNumeric(internalEdges, exitEdges, edge => edge.GapOverLocalMedian),
                normalizedLeftEdgeDelta = CompareNumeric(internalEdges, exitEdges, edge => edge.NormalizedLeftEdgeDelta),
                horizontalOverlapRatio = CompareNumeric(internalEdges, exitEdges, edge => edge.HorizontalOverlapRatio),
                normalizedCenterDelta = CompareNumeric(internalEdges, exitEdges, edge => edge.NormalizedCenterDelta),
                widthRatio = CompareNumeric(internalEdges, exitEdges, edge => edge.WidthRatio),
                centerednessChange = CompareNumeric(internalEdges, exitEdges, edge => edge.CenterednessChange),
                normalizedTopPositionDelta = CompareNumeric(internalEdges, exitEdges, edge => edge.NormalizedTopPositionDelta),
                postExitNegativeControls = new
                {
                    n = postExitEdges.Length,
                    sameLayoutBlock = postExitEdges.Count(edge => edge.SameLayoutBlock),
                    sameFontName = postExitEdges.Count(edge => edge.SameFontName),
                    sameCapitalizationPattern = postExitEdges.Count(edge => edge.SameCapitalizationPattern),
                    verticalGapInFontSizes = Stats(postExitEdges.Where(edge => edge.VerticalGapInFontSizes is not null).Select(edge => edge.VerticalGapInFontSizes!.Value).ToArray()),
                    gapOverLocalMedian = Stats(postExitEdges.Where(edge => edge.GapOverLocalMedian is not null).Select(edge => edge.GapOverLocalMedian!.Value).ToArray()),
                    horizontalOverlapRatio = Stats(postExitEdges.Where(edge => edge.HorizontalOverlapRatio is not null).Select(edge => edge.HorizontalOverlapRatio!.Value).ToArray()),
                    normalizedCenterDelta = Stats(postExitEdges.Where(edge => edge.NormalizedCenterDelta is not null).Select(edge => edge.NormalizedCenterDelta!.Value).ToArray()),
                },
                caseLevelExceptions = allMeasured.Where(edge => edge.SamePage && edge.SameLayoutBlock).Select(edge => new
                {
                    edge.DocumentId, edge.Anchor, edge.EdgeClass, edge.LeftAlias, edge.RightAlias,
                    edge.VerticalGapInFontSizes, edge.GapOverLocalMedian, edge.NormalizedLeftEdgeDelta, edge.HorizontalOverlapRatio,
                }).ToArray(),
                missingness = new
                {
                    internalVerticalGapInFontSizes = internalEdges.Count(edge => edge.VerticalGapInFontSizes is null),
                    exitVerticalGapInFontSizes = exitEdges.Count(edge => edge.VerticalGapInFontSizes is null),
                    internalGapOverLocalMedian = internalEdges.Count(edge => edge.GapOverLocalMedian is null),
                    exitGapOverLocalMedian = exitEdges.Count(edge => edge.GapOverLocalMedian is null),
                    internalHorizontalOverlap = internalEdges.Count(edge => edge.HorizontalOverlapRatio is null),
                    exitHorizontalOverlap = exitEdges.Count(edge => edge.HorizontalOverlapRatio is null),
                },
            },
            edges = edges.Select(edge => edge.ToArtifact()).ToArray(),
            interpretation = new
            {
                positiveClass = "Gold internal continuation edges",
                boundaryClass = "last Gold part to immediate next source atom",
                negativeControls = "the next one or two source edges after each Gold exit; controls are local adjacency observations, not extra independent heading examples",
                descriptiveOnly = "feature overlap on this development cohort is not a causal or held-out qualification result",
                availableGeometry = "live parser lines provide page, bounding box, font identity and parser layout-block membership; canonical snapshots used for requests do not retain the full PdfLine/glyph map",
            },
        });
    }

    private static Edge Describe(string documentId, string anchor, string edgeClass, Geometry left, Geometry right)
    {
        var samePage = left.Page == right.Page;
        var overlap = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var maxWidth = Math.Max(left.Right - left.Left, right.Right - right.Left);
        var leftWidthRatio = left.PageWidth > 0 ? (left.Right - left.Left) / left.PageWidth : 0;
        var rightWidthRatio = right.PageWidth > 0 ? (right.Right - right.Left) / right.PageWidth : 0;
        var normalizedFontGap = samePage && Math.Max(left.FontSize, right.FontSize) > 0
            ? Math.Max(0, left.BaselineY - right.BaselineY) / Math.Max(left.FontSize, right.FontSize)
            : (double?)null;
        var localGapRatio = normalizedFontGap is { } gap && left.PageMedianNormalizedGap is > 0
            ? gap / left.PageMedianNormalizedGap.Value
            : (double?)null;
        double? leftFontBold = left.FontFlagBoldRatio is { } lb && right.FontFlagBoldRatio is { } rb ? Math.Abs(lb - rb) : null;
        double? leftNameBold = left.FontNameBoldRatio is { } lnb && right.FontNameBoldRatio is { } rnb ? Math.Abs(lnb - rnb) : null;
        var sameCaps = string.Equals(Capitalization(left.Text), Capitalization(right.Text), StringComparison.Ordinal);
        var leftCenteredness = left.PageWidth > 0 ? Math.Abs((left.Left + left.Right) / 2 - left.PageWidth / 2) / left.PageWidth : (double?)null;
        var rightCenteredness = right.PageWidth > 0 ? Math.Abs((right.Left + right.Right) / 2 - right.PageWidth / 2) / right.PageWidth : (double?)null;
        var leftTopPosition = left.PageHeight > 0 ? left.Top / left.PageHeight : (double?)null;
        var rightTopPosition = right.PageHeight > 0 ? right.Top / right.PageHeight : (double?)null;
        return new Edge(documentId, anchor, edgeClass, left.Alias, right.Alias, left.Page, right.Page,
            left.Text, right.Text, left.LayoutBlockId, right.LayoutBlockId,
            left.LayoutBlockLineCount, right.LayoutBlockLineCount, left.LayoutBlockId == right.LayoutBlockId,
            samePage ? right.Page == left.Page : false,
            sameCaps, normalizedFontGap, localGapRatio,
            left.PageWidth > 0 ? Math.Abs(left.Left - right.Left) / left.PageWidth : null,
            maxWidth > 0 ? overlap / maxWidth : null,
            left.PageWidth > 0 ? Math.Abs((left.Left + left.Right) / 2 - (right.Left + right.Right) / 2) / left.PageWidth : null,
            maxWidth > 0 ? Math.Min(leftWidthRatio, rightWidthRatio) / Math.Max(leftWidthRatio, rightWidthRatio) : null,
            leftCenteredness, rightCenteredness, leftTopPosition, rightTopPosition,
            leftCenteredness is { } lc && rightCenteredness is { } rc ? Math.Abs(lc - rc) : null,
            leftTopPosition is { } lt && rightTopPosition is { } rt ? Math.Abs(lt - rt) : null,
            string.Equals(left.FontName, right.FontName, StringComparison.Ordinal),
            Math.Abs(left.FontSize - right.FontSize), Math.Abs(left.BoldRatio - right.BoldRatio),
            Math.Abs(left.ItalicRatio - right.ItalicRatio), leftFontBold, leftNameBold,
            left.PageWidth, right.PageWidth, left.PageHeight, right.PageHeight);
    }

    private static object Compare(IReadOnlyList<Edge> positive, IReadOnlyList<Edge> boundary, Func<Edge, bool> predicate) => new
    {
        internalTrue = positive.Count(predicate), internalN = positive.Count,
        goldExitTrue = boundary.Count(predicate), goldExitN = boundary.Count,
        agreementRate = new { internalRate = Rate(positive.Count(predicate), positive.Count), goldExitRate = Rate(boundary.Count(predicate), boundary.Count) },
    };

    private static object CompareNumeric(IReadOnlyList<Edge> positive, IReadOnlyList<Edge> boundary, Func<Edge, double?> selector)
    {
        var a = positive.Select(selector).Where(value => value is not null).Select(value => value!.Value).ToArray();
        var b = boundary.Select(selector).Where(value => value is not null).Select(value => value!.Value).ToArray();
        return new
        {
            internalMissing = positive.Count - a.Length,
            goldExitMissing = boundary.Count - b.Length,
            internalStats = Stats(a),
            goldExit = Stats(b),
            rangeOverlap = a.Length == 0 || b.Length == 0 ? (double?)null : Math.Max(0, Math.Min(a.Max(), b.Max()) - Math.Max(a.Min(), b.Min())),
            pooledRangeWidth = a.Length == 0 || b.Length == 0 ? (double?)null : Math.Max(a.Max(), b.Max()) - Math.Min(a.Min(), b.Min()),
        };
    }

    private static object Stats(double[] values) => new
    {
        n = values.Length,
        min = values.Length == 0 ? (double?)null : values.Min(),
        median = values.Length == 0 ? (double?)null : Median(values),
        max = values.Length == 0 ? (double?)null : values.Max(),
    };

    private static double Rate(int positives, int total) => total == 0 ? 0 : Math.Round((double)positives / total, 4);
    private static double Median(double[] values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static string Capitalization(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return "NO_LETTERS";
        if (letters.All(char.IsUpper)) return "ALL_UPPER";
        if (letters.All(char.IsLower)) return "ALL_LOWER";
        return "MIXED_CASE";
    }

    private sealed record Edge(string DocumentId, string Anchor, string EdgeClass, string LeftAlias, string RightAlias,
        int LeftPage, int RightPage, string LeftText, string RightText, string LeftLayoutBlockId, string RightLayoutBlockId,
        int LeftLayoutBlockLineCount, int RightLayoutBlockLineCount, bool SameLayoutBlock, bool SamePage,
        bool SameCapitalizationPattern, double? VerticalGapInFontSizes, double? GapOverLocalMedian,
        double? NormalizedLeftEdgeDelta, double? HorizontalOverlapRatio, double? NormalizedCenterDelta,
        double? WidthRatio, double? LeftCenteredness, double? RightCenteredness,
        double? LeftTopPosition, double? RightTopPosition, double? CenterednessChange, double? NormalizedTopPositionDelta,
        bool SameFontName, double FontSizeDelta, double BoldRatioDelta,
        double ItalicRatioDelta, double? FontBoldFlagRatioDelta, double? FontNameBoldRatioDelta,
        double LeftPageWidth, double RightPageWidth, double LeftPageHeight, double RightPageHeight)
    {
        public object ToArtifact() => new
        {
            DocumentId, Anchor, EdgeClass, LeftAlias, RightAlias, LeftPage, RightPage,
            SamePage, LeftLayoutBlockId, RightLayoutBlockId, LeftLayoutBlockLineCount, RightLayoutBlockLineCount,
            SameLayoutBlock, SameCapitalizationPattern, SameFontName,
            FontSizeDelta, BoldRatioDelta, ItalicRatioDelta, FontBoldFlagRatioDelta, FontNameBoldRatioDelta,
            VerticalGapInFontSizes, GapOverLocalMedian, NormalizedLeftEdgeDelta, HorizontalOverlapRatio, NormalizedCenterDelta,
            WidthRatio, LeftCenteredness, RightCenteredness, LeftTopPosition, RightTopPosition,
            CenterednessChange, NormalizedTopPositionDelta,
            LeftPageWidth, RightPageWidth, LeftPageHeight, RightPageHeight,
            LeftText = LeftText.Length <= 180 ? LeftText : LeftText[..180],
            RightText = RightText.Length <= 180 ? RightText : RightText[..180],
        };
    }
}
