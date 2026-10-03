using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6S-G diagnosis of the strict-exact pre-projection residuals.</summary>
public sealed class V5P6SGPreProjectionErrorDecompositionTests
{
    private const string CaptureRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string AssociationRoot = "artifacts/v5-p6s-candidate-authority/p6sf-global-association-score";
    private const string GoldRoot = "eval/a99-closed-loop/gold-current/documents";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private sealed record Part(string Alias, int Start, int End);
    private sealed record Prediction(string DocumentId, string PackId, string CandidateId, string Identity, IReadOnlyList<Part> Parts, string Kind);
    private sealed record Gold(string DocumentId, int Ordinal, string Identity, IReadOnlyList<Part> Parts);
    private sealed record ReviewFact(string Verdict, string Pattern);
    private static readonly (string Id, string Pdf)[] Documents =
    [ ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) ];

    [Fact]
    public void P6SG_decomposes_every_pre_projection_strict_exact_false_positive_and_false_negative()
    {
        var capturePath = TestRepository.Path($"{CaptureRoot}/result.v1.json");
        var associationPath = TestRepository.Path($"{AssociationRoot}/full31-global-association-and-exact-score.v1.json");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        using var association = JsonDocument.Parse(File.ReadAllText(associationPath));
        Assert.Equal(31, capture.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(capture.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NONE", capture.RootElement.GetProperty("goldMutation").GetString());
        Assert.Equal(0, association.RootElement.GetProperty("execution").GetProperty("providerCallsDuringScore").GetInt32());

        var plans = Documents.ToDictionary(item => item.Id, item =>
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(item.Pdf));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), item.Id);
        }, StringComparer.Ordinal);
        var (headings, representations) = ReadPredictionsFromRawCapture(capture.RootElement, plans);
        var gold = ReadGold(plans);
        var exactGold = gold.ToDictionary(value => $"{value.DocumentId}:{value.Identity}", StringComparer.Ordinal);
        var associationByGold = association.RootElement.GetProperty("goldOccurrenceDiagnostics").EnumerateArray()
            .ToDictionary(item => $"{item.GetProperty("DocumentId").GetString()}:{item.GetProperty("GoldOrdinal").GetInt32()}", item => item.Clone(), StringComparer.Ordinal);
        var nonExactAssociatedPrediction = associationByGold.Values.Where(item => item.TryGetProperty("Model", out var model) && model.ValueKind == JsonValueKind.Object &&
                model.GetProperty("MatchKind").GetString() != "EXACT")
            .Select(item => $"{item.GetProperty("DocumentId").GetString()}:{item.GetProperty("Model").GetProperty("identity").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        var review = ReviewPatterns();

        var falsePositives = headings.Where(value => !exactGold.ContainsKey(Key(value.DocumentId, value.Identity))).Select(value =>
        {
            var overlapsGold = gold.Where(item => item.DocumentId == value.DocumentId && Overlap(value.Parts, item.Parts)).ToArray();
            var facts = value.Parts.SelectMany(part => review.GetValueOrDefault($"{value.DocumentId}:{part.Alias}") ?? []).Distinct().OrderBy(fact => fact.Verdict, StringComparer.Ordinal).ThenBy(fact => fact.Pattern, StringComparer.Ordinal).ToArray();
            var reason = nonExactAssociatedPrediction.Contains(Key(value.DocumentId, value.Identity)) ? "ASSOCIATED_EXTENT_VARIANT" : overlapsGold.Length > 0 ? "UNMATCHED_OVERLAPPING_VARIANT" : SourceFamily(facts);
            return new { value.DocumentId, value.PackId, value.Identity, value.CandidateId, parts = value.Parts, reason, reviewFacts = facts,
                primaryAlias = value.Parts[0].Alias, overlappingGold = overlapsGold.Select(item => item.Ordinal).ToArray() };
        }).ToArray();
        Assert.Equal(114, falsePositives.Length);
        Assert.Equal(falsePositives.Length, falsePositives.GroupBy(value => value.Identity, StringComparer.Ordinal).Sum(group => group.Count()));

        var falseNegatives = gold.Where(item => !headings.Any(value => value.DocumentId == item.DocumentId && value.Identity == item.Identity)).Select(item =>
        {
            var diagnostic = associationByGold[$"{item.DocumentId}:{item.Ordinal}"];
            var model = diagnostic.GetProperty("Model");
            var representationOverlap = representations.Any(value => value.DocumentId == item.DocumentId && Overlap(value.Parts, item.Parts));
            var reason = model.ValueKind == JsonValueKind.Object ? "NONEXACT_EXTENT_ASSOCIATION" : representationOverlap ? "REPRESENTATION_ROLE_ERROR" : "NO_MODEL_PROPOSAL";
            return new { item.DocumentId, goldOrdinal = item.Ordinal, item.Identity, parts = item.Parts, reason };
        }).ToArray();
        Assert.Equal(36, falseNegatives.Length);

        var src095Fp = falsePositives.Where(value => value.DocumentId == "SRC-095").ToArray();
        var src095Fn = falseNegatives.Where(value => value.DocumentId == "SRC-095").ToArray();
        Assert.Equal(100, src095Fp.Length); Assert.Equal(21, src095Fn.Length);
        Assert.Equal(693, representations.Count(value => value.DocumentId == "SRC-095"));

        var output = new
        {
            schemaVersion = "v5-p6sg-pre-projection-strict-exact-error-decomposition-v1",
            execution = new { providerCalls = 0, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
                rawCaptureSource = $"{CaptureRoot}/result.v1.json", rawCaptureSourceSha256 = Hash(File.ReadAllBytes(capturePath)),
                associationSource = $"{AssociationRoot}/full31-global-association-and-exact-score.v1.json", associationSourceSha256 = Hash(File.ReadAllBytes(associationPath)) },
            authority = new
            {
                metric = "P6S-F strict exact pre-projection: identity equality only; association is diagnostic",
                sourceReview = "Source review authority is verdict plus pattern. A pattern never supplies truth without verdict=NON_HEADING; absent review remains UNREVIEWED_STRICT_FP and never mutates Gold.",
                scope = "diagnosis only; no prompt, candidate, relation, Gold, parser, binder or runtime change",
            },
            totals = new
            {
                strictExact = new { headingPredictions = headings.Count, goldOccurrences = gold.Count, falsePositives = falsePositives.Length, falseNegatives = falseNegatives.Length },
                representationDecisions = representations.Count,
                src095 = new { falsePositives = src095Fp.Length, falseNegatives = src095Fn.Length, representationDecisions = representations.Count(value => value.DocumentId == "SRC-095") },
            },
            falsePositiveFamilies = falsePositives.GroupBy(value => value.reason, StringComparer.Ordinal).OrderByDescending(value => value.Count()).ThenBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new { family = value.Key, count = value.Count(), byDocument = value.GroupBy(item => item.DocumentId).ToDictionary(item => item.Key, item => item.Count()) }).ToArray(),
            falseNegativeFamilies = falseNegatives.GroupBy(value => value.reason, StringComparer.Ordinal).OrderByDescending(value => value.Count()).ThenBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new { family = value.Key, count = value.Count(), byDocument = value.GroupBy(item => item.DocumentId).ToDictionary(item => item.Key, item => item.Count()) }).ToArray(),
            src095CandidateVariantClusters = src095Fp.GroupBy(value => value.primaryAlias, StringComparer.Ordinal).Where(value => value.Count() > 1).OrderByDescending(value => value.Count()).ThenBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new { primaryAlias = value.Key, variants = value.Count(), families = value.GroupBy(item => item.reason).ToDictionary(item => item.Key, item => item.Count()) }).ToArray(),
            falsePositives, falseNegatives,
        };
        FreezeArtifact.AssertJson("artifacts/v5-p6s-candidate-authority/p6sg-preprojection-diagnosis", "strict-exact-residual-decomposition.v1.json", output);
    }

    private static string SourceFamily(IReadOnlyList<ReviewFact> facts) =>
        facts.Any(fact => fact.Verdict == "NON_HEADING" && fact.Pattern == "CONTENTS_ENTRY") ? "REVIEWED_TOC_NAVIGATION_NONHEADING" :
        facts.Any(fact => fact.Verdict == "NON_HEADING") ? "REVIEWED_OTHER_NONHEADING" : "UNREVIEWED_STRICT_FP";

    private static Dictionary<string, IReadOnlyList<ReviewFact>> ReviewPatterns() => Documents.SelectMany(document =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{document.Id}/review-items.json")));
        return json.RootElement.GetProperty("items").EnumerateArray().SelectMany(item => item.GetProperty("parts").EnumerateArray().Select(part =>
            (Key: $"{document.Id}:{part.GetProperty("sourceAlias").GetString()}", Fact: new ReviewFact(item.GetProperty("verdict").GetString()!, item.GetProperty("pattern").GetString()!)))).ToArray();
    }).GroupBy(value => value.Key, StringComparer.Ordinal).ToDictionary(value => value.Key,
        value => (IReadOnlyList<ReviewFact>)value.Select(item => item.Fact).Distinct().OrderBy(item => item.Verdict, StringComparer.Ordinal).ThenBy(item => item.Pattern, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private static List<Gold> ReadGold(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans) => Documents.SelectMany(document =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldRoot}/{document.Id}.gold.v1.json"))); var ordinal = 0;
        return json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
            var bound = SemanticSourcePartBinder.Bind(plans[document.Id].SourceAtoms, sourceParts);
            Assert.True(bound.IsBound, bound.Reason);
            var parts = bound.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
            return new Gold(document.Id, ++ordinal, Identity(parts), parts);
        }).ToArray();
    }).ToList();

    private static (List<Prediction> Headings, List<Prediction> Representations) ReadPredictionsFromRawCapture(JsonElement capture,
        IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        var headings = new List<Prediction>(); var representations = new List<Prediction>();
        foreach (var row in capture.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            var pack = plans[documentId].Packs.Single(item => item.PackId == packId);
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            var raw = row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var binding = SemanticSourcePartBinder.Bind(plans[documentId].SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                var parts = binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
                var prediction = new Prediction(documentId, packId, decision.Candidate.Id, Identity(parts), parts, decision.Kind.ToString());
                if (decision.Kind == V5CandidateDecisionKind.HEADING) headings.Add(prediction); else representations.Add(prediction);
            }
        }
        return (headings, representations);
    }
    private static IReadOnlyList<Part> Parts(JsonElement array) => array.EnumerateArray().Select(item => new Part(item.TryGetProperty("sourceAlias", out var alias) ? alias.GetString()! : item.GetProperty("Alias").GetString()!, item.TryGetProperty("utf16Span", out var span) ? span.GetProperty("start").GetInt32() : item.GetProperty("Start").GetInt32(), item.TryGetProperty("utf16Span", out span) ? span.GetProperty("end").GetInt32() : item.GetProperty("End").GetInt32())).ToArray();
    private static string Identity(IReadOnlyList<Part> parts) => string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static string Key(string documentId, string identity) => $"{documentId}:{identity}";
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
