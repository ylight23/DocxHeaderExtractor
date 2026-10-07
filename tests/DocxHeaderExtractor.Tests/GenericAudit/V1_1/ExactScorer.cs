using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests.GenericAudit.V1_1;

/// <summary>
/// GENERIC_EXACT_SCORER_V1 - SRC029_BLIND_SCORE_PROTOCOL_V1's matching, for any document and either
/// media, over any engine's frozen proposal artifact (V1: whole-alias lists; V1.1: parts that may be
/// exact leads).
/// <para>
/// A hypothesis matches a Gold claim only when the ordered tuple of its bound parts (alias:start-end)
/// equals the claim's. PDF parts are bound by the production binder over the structured atom universe;
/// DOCX parts resolve against the alias catalog, whole aliases as their trimmed text. A hypothesis that
/// only overlaps a claim is a claim-level miss. NEEDS_REVIEW never counts as a heading; it has its own
/// capture rate. Nothing is repaired: a hypothesis that does not bind matches nothing.
/// </para>
/// </summary>
internal static class ExactScorer
{
    public const string ScorerId = "GENERIC_EXACT_SCORER_V1";

    internal sealed record Span(string Alias, int Start, int End);

    internal sealed record Hypothesis(
        int Index, string Text, string State, string[] Functions, string? Primary, string? Scope, string[] Roles,
        string TitleRelation, string? InformationType, string[] Evidence, string? Identity, string? UnboundReason, Span[] Spans);

    internal sealed record Claim(
        string Identity, string Text, Span[] Spans, bool HasPartialPart, string[] Functions, string? Primary, string? Scope,
        string[] Roles, string TitleRelation, string? InformationType);

    /// <summary>Resolves (alias, verbatim-or-null) parts to spans for one source.</summary>
    internal sealed class Universe
    {
        private readonly IReadOnlyList<SemanticSourceAtom>? _atoms;
        private readonly IReadOnlyDictionary<string, string>? _docxText;

        private Universe(IReadOnlyList<SemanticSourceAtom>? atoms, IReadOnlyDictionary<string, string>? docxText)
        {
            _atoms = atoms;
            _docxText = docxText;
        }

        public static Universe For(string media, string sourcePath)
        {
            if (media == "PDF") return new Universe(PdfSourceAdapter.Build(sourcePath).Atoms, null);
            var document = new OpenXmlDocumentSource().Read(sourcePath);
            var aliases = SemanticSourceAliasCatalog.FromCatalog(DocumentSourceCatalogBuilder.FromSourceDocument(document));
            return new Universe(null, aliases.ToDictionary(a => a.Alias, a => a.Text, StringComparer.Ordinal));
        }

        public (string? Identity, string? Reason, Span[] Spans) Bind(IReadOnlyList<(string Alias, string? Verbatim)> parts) =>
            Bind(parts.Select(p => (p.Alias, p.Verbatim, (int?)1)).ToArray());

        /// <summary>A verbatim part names its occurrence in the atom; engine leads are always the first.</summary>
        public (string? Identity, string? Reason, Span[] Spans) Bind(IReadOnlyList<(string Alias, string? Verbatim, int? Occurrence)> parts)
        {
            if (_atoms is not null)
            {
                var binding = SemanticSourcePartBinder.Bind(_atoms, new SemanticSourcePartsProposal(parts.Select(p => p.Verbatim is null
                    ? new SemanticSourcePart(p.Alias, "WHOLE_ALIAS")
                    : new SemanticSourcePart(p.Alias, "VERBATIM_TEXT", p.Verbatim, p.Occurrence)).ToArray()));
                if (binding.IsBound)
                    return (binding.Identity, null, binding.Parts.Select(b => new Span(b.Alias, b.Start, b.End)).ToArray());
                return (null, binding.Status.ToString(), parts.Where(p => _atoms.Any(a => a.Alias == p.Alias))
                    .Select(p => new Span(p.Alias, 0, _atoms.First(a => a.Alias == p.Alias).Text.Length)).ToArray());
            }

            var spans = new List<Span>();
            foreach (var (alias, verbatim, _) in parts)
            {
                if (!_docxText!.TryGetValue(alias, out var text)) return (null, "UnknownAlias", spans.ToArray());
                var start = text.Length - text.TrimStart().Length;
                if (verbatim is null) spans.Add(new Span(alias, start, text.TrimEnd().Length));
                else
                {
                    var at = text.IndexOf(verbatim, StringComparison.Ordinal);
                    if (at < 0) return (null, "TextNotInAtom", spans.ToArray());
                    spans.Add(new Span(alias, at, at + verbatim.Length));
                }
            }
            return (Identity(spans), null, spans.ToArray());
        }
    }

    public static string Identity(IEnumerable<Span> spans) => string.Join("|", spans.Select(s => $"{s.Alias}:{s.Start}-{s.End}"));

    public static IReadOnlyList<Hypothesis> ReadProposals(string proposalsPath, Universe universe)
    {
        using var frozen = JsonDocument.Parse(File.ReadAllText(proposalsPath));
        return frozen.RootElement.GetProperty("hypotheses").EnumerateArray().Select((h, index) =>
        {
            var parts = h.TryGetProperty("Parts", out var p)
                ? p.EnumerateArray().Select(x => (x.GetProperty("Alias").GetString()!, Nullable(x.GetProperty("Verbatim")))).ToArray()
                : Strings(h.GetProperty("Aliases")).Select(a => (a, (string?)null)).ToArray();
            var (identity, reason, spans) = universe.Bind(parts);
            return new Hypothesis(
                index, h.GetProperty("Text").GetString()!, h.GetProperty("ProposedIsHeading").GetString()!,
                Strings(h.GetProperty("SemanticFunctions")), Nullable(h.GetProperty("PrimaryFunction")), Nullable(h.GetProperty("Scope")),
                Strings(h.GetProperty("OccurrenceRoles")), h.GetProperty("TitleRelation").GetString()!,
                Nullable(h.GetProperty("InformationType")), Strings(h.GetProperty("Evidence")), identity, reason, spans);
        }).ToArray();
    }

    public static IReadOnlyList<Claim> ReadGold(string goldPath, Universe universe)
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var occurrence = gold.RootElement.GetProperty("occurrence");
        var structured = occurrence.GetProperty("coordinateSystem").GetString() == "STRUCTURED_SOURCE_PARTS";
        return occurrence.GetProperty("claims").EnumerateArray().Select(c =>
        {
            var axes = c.GetProperty("semanticAxes");
            Span[] spans;
            bool partial;
            if (structured)
            {
                var parts = c.GetProperty("sourceParts").EnumerateArray().ToArray();
                partial = parts.Any(x => x.GetProperty("selectionMode").GetString() == "VERBATIM_TEXT");
                var bound = universe.Bind(parts.Select(x => (x.GetProperty("sourceAlias").GetString()!,
                    x.GetProperty("selectionMode").GetString() == "VERBATIM_TEXT" ? x.GetProperty("verbatimText").GetString() : null,
                    x.TryGetProperty("occurrence", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : (int?)null)).ToArray());
                Assert.True(bound.Identity is not null, $"a Gold claim does not bind: {bound.Reason}");
                spans = bound.Spans;
            }
            else
            {
                spans = c.GetProperty("parts").EnumerateArray().Select(x => new Span(x.GetProperty("sourceAlias").GetString()!,
                    x.GetProperty("utf16Span").GetProperty("start").GetInt32(), x.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray();
                var whole = universe.Bind(spans.Select(s => (s.Alias, (string?)null)).ToArray()).Spans;
                partial = spans.Zip(whole).Any(z => z.First != z.Second);
            }
            return new Claim(
                Identity(spans),
                c.TryGetProperty("approvedWording", out var w) ? w.GetString()! : c.GetProperty("exactText").GetString()!,
                spans, partial,
                Strings(axes.GetProperty("semanticFunctions")), Nullable(axes.GetProperty("primaryFunction")),
                Nullable(axes.GetProperty("scope")), Strings(axes.GetProperty("occurrenceRoles")),
                axes.GetProperty("titleRelation").GetString()!, Nullable(axes.GetProperty("informationType")));
        }).ToArray();
    }

    private static bool Overlaps(Span[] a, Span[] b) => a.Any(x => b.Any(y => x.Alias == y.Alias && x.Start < y.End && y.Start < x.End));

    /// <summary>Headline numbers, Gold buckets, axis disagreements on exact matches, and every residual.</summary>
    public static Score Compute(IReadOnlyList<Claim> claims, IReadOnlyList<Hypothesis> hypotheses)
    {
        var claimByIdentity = claims.GroupBy(c => c.Identity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var exactByIdentity = hypotheses.Where(h => h.Identity is not null)
            .GroupBy(h => h.Identity!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var rows = claims.Select(c =>
        {
            var exact = exactByIdentity.GetValueOrDefault(c.Identity);
            var overlapping = hypotheses.Where(h => h.Identity != c.Identity && Overlaps(h.Spans, c.Spans)).ToArray();
            var bucket = exact is { Length: > 0 } ? "EXACT_" + exact[0].State
                : overlapping.Any(h => h.State == "TRUE") ? "PARTIAL_TRUE"
                : overlapping.Any(h => h.State == "NEEDS_REVIEW") ? "PARTIAL_NEEDS_REVIEW"
                : overlapping.Length > 0 ? "PARTIAL_FALSE"
                : "UNPROPOSED";
            return new Row(c, exact?[0], overlapping, bucket);
        }).ToArray();
        return new Score(claims, hypotheses, rows, claimByIdentity);
    }

    internal sealed record Row(Claim Claim, Hypothesis? Exact, Hypothesis[] Overlapping, string Bucket);

    internal sealed class Score(IReadOnlyList<Claim> claims, IReadOnlyList<Hypothesis> hypotheses, Row[] rows, IReadOnlyDictionary<string, Claim> claimByIdentity)
    {
        public Row[] Rows => rows;
        public int Count(string bucket) => rows.Count(r => r.Bucket == bucket);
        public int TruePositives => Count("EXACT_TRUE");
        public Hypothesis[] TrueHypotheses => hypotheses.Where(h => h.State == "TRUE").ToArray();
        public Hypothesis[] FalsePositives => TrueHypotheses.Where(h => h.Identity is null || !claimByIdentity.ContainsKey(h.Identity)).ToArray();
        public Hypothesis[] ReviewOutsideGold => hypotheses.Where(h => h.State == "NEEDS_REVIEW" && (h.Identity is null || !claimByIdentity.ContainsKey(h.Identity))).ToArray();
        public bool TouchesGold(Hypothesis h) => claims.Any(c => Overlaps(h.Spans, c.Spans));

        private static double Ratio(int a, int b) => b == 0 ? 0 : Math.Round((double)a / b, 4);

        public object Headline()
        {
            var tp = TruePositives;
            var precision = Ratio(tp, TrueHypotheses.Length);
            var recall = Ratio(tp, claims.Count);
            var notTrue = claims.Count - tp;
            return new
            {
                goldClaims = claims.Count,
                hypothesesListed = hypotheses.Count,
                TRUE = TrueHypotheses.Length,
                NEEDS_REVIEW = hypotheses.Count(h => h.State == "NEEDS_REVIEW"),
                unbindable = hypotheses.Count(h => h.Identity is null),
                truePositives = tp,
                falsePositives = FalsePositives.Length,
                falseNegatives = notTrue,
                truePrecision = precision,
                trueRecall = recall,
                f1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4),
                needsReviewCaptureOfGold = Ratio(Count("EXACT_NEEDS_REVIEW"), notTrue),
                needsReviewCaptureIncludingPartial = Ratio(Count("EXACT_NEEDS_REVIEW") + Count("PARTIAL_NEEDS_REVIEW"), notTrue),
                falseOnGold = Count("EXACT_FALSE") + Count("UNPROPOSED"),
                partialOnly = Count("PARTIAL_TRUE") + Count("PARTIAL_NEEDS_REVIEW") + Count("PARTIAL_FALSE"),
                reviewNoiseOutsideGold = ReviewOutsideGold.Count(h => !TouchesGold(h)),
                nonGoldReviewTouchingGold = ReviewOutsideGold.Count(TouchesGold),
                falsePositivesTouchingGold = FalsePositives.Count(TouchesGold),
                goldMultipartClaims = claims.Count(c => c.Spans.Length > 1),
                exactMultipartMatches = rows.Count(r => r.Bucket.StartsWith("EXACT_", StringComparison.Ordinal) && r.Claim.Spans.Length > 1),
                goldClaimsWithPartialPart = claims.Count(c => c.HasPartialPart),
                exactMatchesWithPartialPart = rows.Count(r => r.Bucket.StartsWith("EXACT_", StringComparison.Ordinal) && r.Claim.HasPartialPart),
                goldBuckets = new[] { "EXACT_TRUE", "EXACT_NEEDS_REVIEW", "EXACT_FALSE", "PARTIAL_TRUE", "PARTIAL_NEEDS_REVIEW", "PARTIAL_FALSE", "UNPROPOSED" }
                    .ToDictionary(b => b, Count),
            };
        }

        /// <summary>OCCURRENCE_SEMANTIC_AXES_V3 axes on exact TRUE matches (no repeatStatus).</summary>
        public object Axes()
        {
            var pairs = rows.Where(r => r.Bucket == "EXACT_TRUE").Select(r => (Gold: r.Claim, Engine: r.Exact!)).ToArray();
            object Axis(Func<Claim, string> gold, Func<Hypothesis, string> engine) => new
            {
                disagreements = pairs.Count(p => gold(p.Gold) != engine(p.Engine)),
                pairs = pairs.Where(p => gold(p.Gold) != engine(p.Engine))
                    .GroupBy(p => $"gold {gold(p.Gold)} <- engine {engine(p.Engine)}")
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
            };
            static string Set(string[] values) => string.Join("+", values.Order(StringComparer.Ordinal));
            static string Scalar(string? value) => value ?? "null";
            return new
            {
                compared = pairs.Length,
                semanticFunctions = Axis(c => Set(c.Functions), h => Set(h.Functions)),
                primaryFunction = Axis(c => Scalar(c.Primary), h => Scalar(h.Primary)),
                scope = Axis(c => Scalar(c.Scope), h => Scalar(h.Scope)),
                occurrenceRoles = Axis(c => Set(c.Roles), h => Set(h.Roles)),
                titleRelation = Axis(c => c.TitleRelation, h => h.TitleRelation),
                informationType = Axis(c => Scalar(c.InformationType), h => Scalar(h.InformationType)),
            };
        }

        public object Residuals()
        {
            object Brief(Hypothesis h) => new { h.Index, h.Text, state = h.State, h.Roles, h.Evidence, identity = h.Identity, h.UnboundReason };
            return new
            {
                goldNotEngineTrue = rows.Where(r => r.Bucket != "EXACT_TRUE").Select(r => new
                {
                    bucket = r.Bucket,
                    goldIdentity = r.Claim.Identity,
                    goldText = r.Claim.Text,
                    engine = r.Exact is not null ? new[] { Brief(r.Exact) } : r.Overlapping.Select(Brief).ToArray(),
                }).ToArray(),
                nonGoldTrue = FalsePositives.Select(h => new { hypothesis = Brief(h), touchesGold = TouchesGold(h) }).ToArray(),
                nonGoldNeedsReview = ReviewOutsideGold.Select(h => new { hypothesis = Brief(h), touchesGold = TouchesGold(h) }).ToArray(),
            };
        }
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string? Nullable(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
}
