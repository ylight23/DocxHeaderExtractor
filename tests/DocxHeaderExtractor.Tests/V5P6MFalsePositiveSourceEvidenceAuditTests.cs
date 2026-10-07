using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free source-evidence decomposition of the 126 P6M heading false positives.</summary>
public sealed partial class V5P6MFalsePositiveSourceEvidenceAuditTests
{
    private const string ResultPath = "artifacts/v5-p6m-p6l-full31-qualification/result.v1.json";
    private const string Root = "artifacts/v5-p6m-p6l-full31-qualification";
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    [Fact]
    public void Decompose_P6M_false_positives_with_source_evidence_and_nearest_true_positives()
    {
        using var resultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath)));
        var result = resultDoc.RootElement;
        Assert.Equal("v5-p6m-p6l-full31-result-v1", result.GetProperty("schemaVersion").GetString());
        Assert.Equal(31, result.GetProperty("providerCalls").GetInt32());

        var gold = ReadGold();
        var candidates = new Dictionary<string, Dictionary<string, Candidate>>(StringComparer.Ordinal);
        var source = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms,
            IReadOnlyDictionary<string, VisualEvidence> Visual, Dictionary<string, string> ReviewedPatterns, HashSet<string> GoldAliases)>(StringComparer.Ordinal);
        var parserQuarantines = 0;

        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            if (!source.TryGetValue(documentId, out var corpus))
            {
                var pdf = PdfFor(documentId);
                var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), documentId, Contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope, FrozenSourceSnapshots.V1Root);
                var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf), FrozenSourceSnapshots.V1Root).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
                var visual = ReadVisualEvidence(documentId);
                var reviewed = ResidualFamilies.ReviewedNonHeadings(documentId);
                var goldAliases = gold[documentId].SelectMany(item => item.Parts).Select(part => part.Alias).ToHashSet(StringComparer.Ordinal);
                corpus = (packs, atoms, visual, reviewed, goldAliases);
                source.Add(documentId, corpus);
                candidates.Add(documentId, new Dictionary<string, Candidate>(StringComparer.Ordinal));
            }

            var pack = corpus.Packs.Single(item => item.PackId == row.GetProperty("packId").GetString());
            var ownedAtoms = pack.OwnedAliases.Select(alias => corpus.Atoms[alias]).ToArray();
            var registry = RequestLocalLocatorRegistry.Create(ownedAtoms);
            Assert.Equal(row.GetProperty("registryFingerprint").GetString(), registry.Fingerprint);
            using var response = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var parsed = registry.Parse(response.RootElement, row.GetProperty("rawResponseBytes").GetInt32(), 49_152,
                Enumerable.Range(0, ownedAtoms.Length).ToHashSet());
            parserQuarantines += parsed.Quarantined.Count;
            foreach (var locator in parsed.Response.Occurrences)
            {
                var endpoint = registry.Decode(locator);
                var parts = endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
                var identity = Identity(documentId, parts);
                if (candidates[documentId].TryGetValue(identity, out var existing))
                {
                    candidates[documentId][identity] = existing with
                    {
                        Functions = existing.Functions.Union(locator.Functions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    };
                }
                else
                {
                    candidates[documentId].Add(identity, new Candidate(documentId, identity, parts,
                        locator.Functions.Order(StringComparer.Ordinal).ToArray(), string.Join(" ", endpoint.Parts.Select(part => part.Text)),
                        row.GetProperty("parentOrdinal").GetInt32()));
                }
            }
        }

        Assert.Equal(0, parserQuarantines);
        var falsePositives = new List<ClassifiedCandidate>();
        var goldOverlappingHeadingPredictions = new List<ClassifiedCandidate>();
        var goldCoveredOccurrences = 0;
        foreach (var documentId in gold.Keys.Order(StringComparer.Ordinal))
        {
            var pageByAlias = ResidualFamilies.PageOfAlias(documentId);
            goldCoveredOccurrences += gold[documentId].Count(heading => candidates[documentId].Values.Any(candidate =>
                candidate.IsHeadingMember && Overlaps(candidate.Parts, heading.Parts)));
            foreach (var candidate in candidates[documentId].Values.Where(item => item.IsHeadingMember))
            {
                var goldOverlap = gold[documentId].Any(heading => Overlaps(candidate.Parts, heading.Parts));
                var primary = candidate.Parts[0];
                var style = source[documentId].Visual.GetValueOrDefault(primary.Alias);
                var enriched = Enrich(candidate, style, pageByAlias, source[documentId].Atoms);
                if (goldOverlap)
                {
                    goldOverlappingHeadingPredictions.Add(enriched);
                    continue;
                }

                var verdict = SourceReviewVerdict(documentId, candidate.Parts.Select(part => part.Alias));
                Assert.Equal("NON_HEADING", verdict);
                var familyIdentity = string.Join("|", candidate.Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
                var pageRange = documentId == "SRC-095" ? (From: 2, To: 4) : (From: 0, To: 0);
                var indexFrom = documentId == "SRC-095" ? 54 : 0;
                var family = ResidualFamilies.FalsePositiveFamily(familyIdentity, candidate.Text,
                    string.Join("+", candidate.Functions.Where(function => function is "DOCUMENT_IDENTITY" or "STRUCTURAL_REGION")),
                    pageByAlias.GetValueOrDefault(primary.Alias, -1), pageRange, indexFrom,
                    source[documentId].GoldAliases, source[documentId].ReviewedPatterns);
                falsePositives.Add(enriched with { Family = family, SourceReviewVerdict = verdict,
                    ExplicitReviewPattern = candidate.Parts.Select(part => source[documentId].ReviewedPatterns.GetValueOrDefault(part.Alias))
                        .FirstOrDefault(pattern => pattern is not null) });
            }
        }

        Assert.Equal(126, falsePositives.Count);
        Assert.Equal(122, goldOverlappingHeadingPredictions.Count);
        Assert.Equal(119, goldCoveredOccurrences);

        var paired = falsePositives.Select(falsePositive =>
        {
            var nearest = goldOverlappingHeadingPredictions.Where(candidate => candidate.DocumentId == falsePositive.DocumentId)
                .OrderBy(candidate => FunctionSetDistance(falsePositive.Functions, candidate.Functions))
                .ThenBy(candidate => candidate.PageBand == falsePositive.PageBand ? 0 : 1)
                .ThenBy(candidate => candidate.Bold == falsePositive.Bold ? 0 : 1)
                .ThenBy(candidate => Math.Abs(candidate.FontSize - falsePositive.FontSize))
                .ThenBy(candidate => Math.Abs(candidate.Text.Length - falsePositive.Text.Length))
                .ThenBy(candidate => Math.Abs(candidate.Page - falsePositive.Page))
                .ThenBy(candidate => candidate.Identity, StringComparer.Ordinal)
                .First();
            return new
            {
                falsePositive = ToArtifact(falsePositive),
                nearestTruePositive = ToArtifact(nearest),
                comparison = new
                {
                    sameFunctionSet = falsePositive.Functions.SequenceEqual(nearest.Functions, StringComparer.Ordinal),
                    samePageBand = falsePositive.PageBand == nearest.PageBand,
                    sameBoldClass = falsePositive.Bold == nearest.Bold,
                    fontSizeDeltaPt = Math.Round(Math.Abs(falsePositive.FontSize - nearest.FontSize), 2),
                    textLengthDeltaChars = Math.Abs(falsePositive.Text.Length - nearest.Text.Length),
                    pageDelta = Math.Abs(falsePositive.Page - nearest.Page),
                },
            };
        }).ToArray();

        var families = falsePositives.GroupBy(item => item.Family, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.ToArray();
                var sameProfileTruePositives = goldOverlappingHeadingPredictions.Where(candidate => members.Any(fp =>
                    fp.Functions.SequenceEqual(candidate.Functions, StringComparer.Ordinal) && fp.PageBand == candidate.PageBand && fp.Bold == candidate.Bold)).ToArray();
                return new
                {
                    family = group.Key,
                    count = members.Length,
                    sourceReviewPatternCounts = members.Where(item => item.ExplicitReviewPattern is not null)
                        .GroupBy(item => item.ExplicitReviewPattern!, StringComparer.Ordinal)
                        .ToDictionary(pattern => pattern.Key, pattern => pattern.Count(), StringComparer.Ordinal),
                    evidenceProfile = Profile(members),
                    matchedTruePositiveEvidenceProfile = Profile(sameProfileTruePositives),
                    sameFunctionPageBandBoldTruePositiveCount = sameProfileTruePositives.Length,
                    examples = members.OrderBy(item => item.Page).ThenBy(item => item.Identity, StringComparer.Ordinal).Take(8)
                        .Select(ToArtifact).ToArray(),
                };
            }).ToArray();

        FreezeArtifact.AssertJson(Root, "false-positive-source-evidence.v1.json", new
        {
            schemaVersion = "v5-p6m-false-positive-source-evidence-v1",
            source = new { result = ResultPath, score = "artifacts/v5-p6m-p6l-full31-qualification/gold-occurrence-score.v1.json" },
            providerCalls = 0,
            goldRead = true,
            goldMutation = "NONE",
            runtimeChanged = false,
            authority = new
            {
                gold = "canonical occurrence Gold, read-only",
                candidateUnit = "same document-scoped bound locator identity and heading-membership rule as P6M Gold score",
                sourceFamilyTaxonomy = "existing ResidualFamilies.FalsePositiveFamily; heuristic page bands only for SRC-095 contents (2-4) and index (54+); SRC-089 has no page-band shortcut",
                sourceReview = "source-review-v1 explicit patterns take precedence; every unmatched P6M proposal was already adjudicated NON_HEADING by the P6M scorer source-review authority",
                nearestTruePositive = "same document, lexicographic: minimum function-set symmetric difference; then same page band; same bold class; font-size delta; text-length delta; page delta; identity tie-break",
                scope = "diagnostic family and nearest-neighbor signals, not a new Gold label or causal attribution",
            },
            totals = new
            {
                falsePositiveHeadingUnits = falsePositives.Count,
                goldOverlappingHeadingPredictionUnits = goldOverlappingHeadingPredictions.Count,
                goldOccurrencesCovered = goldCoveredOccurrences,
                parserQuarantines = parserQuarantines,
                explicitReviewPatternMappedFalsePositives = falsePositives.Count(item => item.ExplicitReviewPattern is not null),
                families = families.Select(item => new { item.family, item.count, item.sameFunctionPageBandBoldTruePositiveCount }).ToArray(),
            },
            featureComparisons = new
            {
                bySourceFamily = families,
                textForm = new
                {
                    falsePositives = Profile(falsePositives),
                    goldOverlappingHeadingPredictions = Profile(goldOverlappingHeadingPredictions),
                },
                functionSetCounts = new
                {
                    falsePositives = falsePositives.GroupBy(item => string.Join("+", item.Functions), StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                    goldOverlappingHeadingPredictions = goldOverlappingHeadingPredictions.GroupBy(item => string.Join("+", item.Functions), StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                },
            },
            falsePositiveToNearestTruePositive = paired,
        });
    }

    private static object Profile(IReadOnlyCollection<ClassifiedCandidate> rows) => new
    {
        count = rows.Count,
        pageBandCounts = rows.GroupBy(item => item.PageBand, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        boldCount = rows.Count(item => item.Bold),
        boldRate = rows.Count == 0 ? 0 : Math.Round((double)rows.Count(item => item.Bold) / rows.Count, 4),
        medianFontSizePt = Median(rows.Select(item => item.FontSize)),
        medianTextChars = Median(rows.Select(item => (double)item.Text.Length)),
        sentenceLikeCount = rows.Count(item => item.SentenceLike),
        allCapsCount = rows.Count(item => item.AllCaps),
        numberedPrefixCount = rows.Count(item => item.NumberedPrefix),
        colonEndingCount = rows.Count(item => item.ColonEnding),
        multipartCount = rows.Count(item => item.Parts.Count > 1),
        repeatedAcrossPagesCount = rows.Count(item => item.RepeatedTextPageCount > 1),
        multiSegmentPrimaryRowCount = rows.Count(item => item.PrimaryRowAtomCount > 1),
    };

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return Math.Round(sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle], 2);
    }

    private static ClassifiedCandidate Enrich(Candidate candidate, VisualEvidence? visual,
        IReadOnlyDictionary<string, int> pageByAlias, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        visual ??= VisualEvidence.Empty(candidate.Parts[0].Alias, pageByAlias.GetValueOrDefault(candidate.Parts[0].Alias, -1), atoms[candidate.Parts[0].Alias].Row);
        var primaryAtom = atoms[candidate.Parts[0].Alias];
        var normalizedText = WhitespaceRegex().Replace(candidate.Text, " ").Trim();
        var repetition = atoms.Values.Where(atom => string.Equals(WhitespaceRegex().Replace(atom.Text, " ").Trim(), normalizedText, StringComparison.OrdinalIgnoreCase))
            .Select(atom => atom.Page).Distinct().Count();
        return new ClassifiedCandidate(candidate.DocumentId, candidate.Identity, candidate.Parts, candidate.Functions, candidate.Text,
            candidate.ParentOrdinal, pageByAlias.GetValueOrDefault(candidate.Parts[0].Alias, -1), PageBand(candidate.DocumentId, pageByAlias.GetValueOrDefault(candidate.Parts[0].Alias, -1)),
            visual.BoldShare >= 0.5, visual.FontSize, visual.BoldShare, visual.Fonts, visual.X0, visual.X1, visual.Y,
            atoms.Values.Count(atom => atom.Page == primaryAtom.Page && atom.Row == primaryAtom.Row), repetition,
            SentenceLikeRegex().IsMatch(candidate.Text), AllCapsRegex().IsMatch(candidate.Text), NumberedPrefixRegex().IsMatch(candidate.Text),
            candidate.Text.TrimEnd().EndsWith(':'));
    }

    private static string PageBand(string documentId, int page) => documentId switch
    {
        "SRC-095" when page is >= 2 and <= 4 => "CONTENTS",
        "SRC-095" when page >= 54 => "INDEX",
        _ when page <= 1 => "FRONT",
        _ => "BODY",
    };

    private static IReadOnlyDictionary<string, VisualEvidence> ReadVisualEvidence(string documentId)
    {
        var path = ResidualFamilies.FrozenReview(documentId, "atom-glyph-facts.tsv");
        return File.ReadAllLines(TestRepository.Path(path)).Select(line => line.Split('\t')).ToDictionary(
            fields => fields[0], fields =>
            {
                var fontTokens = fields[5].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var totalGlyphs = fontTokens.Sum(token => ParseCount(token));
                var boldGlyphs = fontTokens.Where(token => token.Contains("Bold", StringComparison.OrdinalIgnoreCase)).Sum(ParseCount);
                return new VisualEvidence(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture),
                    int.Parse(fields[3], CultureInfo.InvariantCulture), double.Parse(fields[4], CultureInfo.InvariantCulture),
                    totalGlyphs == 0 ? 0 : (double)boldGlyphs / totalGlyphs, fields[5],
                    double.Parse(fields[6], CultureInfo.InvariantCulture), double.Parse(fields[7], CultureInfo.InvariantCulture),
                    double.Parse(fields[8], CultureInfo.InvariantCulture), fields[9]);
            }, StringComparer.Ordinal);
    }

    private static int ParseCount(string token)
    {
        var at = token.LastIndexOf(':');
        return at >= 0 && int.TryParse(token[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<GoldHeading>> ReadGold()
    {
        var output = new Dictionary<string, IReadOnlyList<GoldHeading>>(StringComparer.Ordinal);
        foreach (var documentId in new[] { "SRC-089", "SRC-095" })
        {
            FrozenHistoryGold.RequireCapability(documentId, GoldCapability.Occurrence);
            using var gold = FrozenHistoryGold.Resolve(documentId);
            output[documentId] = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim =>
                new GoldHeading(claim.GetProperty("identity").GetString()!, claim.GetProperty("boundParts").EnumerateArray().Select(part => new Part(
                    part.GetProperty("sourceAlias").GetString()!, part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray())).ToArray();
        }
        return output;
    }

    private static string SourceReviewVerdict(string documentId, IEnumerable<string> aliases)
    {
        IEnumerable<(string Alias, string Verdict)> verdicts = documentId == "SRC-089"
            ? Src089SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => (part.SourceAlias, item.Verdict)))
            : Src095SourceReviewTests.Items().SelectMany(item => item.Parts.Select(part => (part.SourceAlias, item.Verdict)));
        var matches = verdicts.Where(item => aliases.Contains(item.Alias, StringComparer.Ordinal))
            .Select(item => item.Verdict).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("source-review-conflicting-verdict");
        return matches.SingleOrDefault() ?? "NON_HEADING";
    }

    private static int FunctionSetDistance(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Except(right, StringComparer.Ordinal).Count() + right.Except(left, StringComparer.Ordinal).Count();

    private static bool Overlaps(IReadOnlyList<Part> left, IReadOnlyList<Part> right) =>
        left.Any(a => right.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));

    private static string Identity(string documentId, IReadOnlyList<Part> parts) =>
        $"{documentId}|" + string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));

    private static object ToArtifact(ClassifiedCandidate item) => new
    {
        item.Identity,
        item.Parts,
        item.Functions,
        item.Text,
        item.ParentOrdinal,
        item.Page,
        item.PageBand,
        item.Bold,
        item.BoldShare,
        item.FontSize,
        item.Fonts,
        item.X0,
        item.X1,
        item.Y,
        item.PrimaryRowAtomCount,
        item.RepeatedTextPageCount,
        item.SentenceLike,
        item.AllCaps,
        item.NumberedPrefix,
        item.ColonEnding,
        family = item.Family,
        item.SourceReviewVerdict,
        item.ExplicitReviewPattern,
    };

    private static string PdfFor(string documentId) => documentId switch
    {
        "SRC-089" => SourcePdfCorpus.Src089,
        "SRC-095" => SourcePdfCorpus.Src095,
        _ => throw new InvalidOperationException($"unknown-P6M-document:{documentId}"),
    };

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"[.!?](?:\s|$)")] private static partial Regex SentenceLikeRegex();
    [GeneratedRegex(@"^[A-Z\d][A-Z\d .:/()\-–—]{5,}$")] private static partial Regex AllCapsRegex();
    [GeneratedRegex(@"^(?:\d+|[A-Z])(?:\.\d+)*\.?\s+")] private static partial Regex NumberedPrefixRegex();

    private sealed record Part(string Alias, int Start, int End);
    private sealed record GoldHeading(string Identity, IReadOnlyList<Part> Parts);
    private sealed record Candidate(string DocumentId, string Identity, IReadOnlyList<Part> Parts,
        IReadOnlyList<string> Functions, string Text, int ParentOrdinal)
    {
        public bool IsHeadingMember => Functions.Contains("DOCUMENT_IDENTITY", StringComparer.Ordinal) ||
            Functions.Contains("STRUCTURAL_REGION", StringComparer.Ordinal);
    }
    private sealed record VisualEvidence(string Alias, int Page, int Row, int Segment, double FontSize, double BoldShare,
        string Fonts, double X0, double X1, double Y, string Text)
    {
        public static VisualEvidence Empty(string alias, int page, int row) => new(alias, page, row, 0, 0, 0, "", 0, 0, 0, "");
    }
    private sealed record ClassifiedCandidate(string DocumentId, string Identity, IReadOnlyList<Part> Parts,
        IReadOnlyList<string> Functions, string Text, int ParentOrdinal, int Page, string PageBand, bool Bold,
        double FontSize, double BoldShare, string Fonts, double X0, double X1, double Y,
        int PrimaryRowAtomCount, int RepeatedTextPageCount, bool SentenceLike, bool AllCaps, bool NumberedPrefix,
        bool ColonEnding, string Family = "", string? SourceReviewVerdict = null, string? ExplicitReviewPattern = null);
}
