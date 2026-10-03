using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6S-G diagnosis of the strict-exact pre-projection residuals.</summary>
public sealed class V5P6SGPreProjectionErrorDecompositionTests
{
    private const string ScoreRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string AssociationRoot = "artifacts/v5-p6s-candidate-authority/p6sf-global-association-score";
    private const string GoldRoot = "eval/a99-closed-loop/gold-current/documents";
    private sealed record Part(string Alias, int Start, int End);
    private sealed record Prediction(string DocumentId, string CandidateId, string Identity, IReadOnlyList<Part> Parts, string Kind);
    private sealed record Gold(string DocumentId, int Ordinal, string Identity, IReadOnlyList<Part> Parts);

    [Fact]
    public void P6SG_decomposes_every_pre_projection_strict_exact_false_positive_and_false_negative()
    {
        var scorePath = TestRepository.Path($"{ScoreRoot}/full31-gold-multiaxis-score.v1.json");
        var associationPath = TestRepository.Path($"{AssociationRoot}/full31-global-association-and-exact-score.v1.json");
        using var score = JsonDocument.Parse(File.ReadAllText(scorePath));
        using var association = JsonDocument.Parse(File.ReadAllText(associationPath));
        Assert.Equal(0, score.RootElement.GetProperty("execution").GetProperty("providerCallsDuringScore").GetInt32());
        Assert.Equal(31, score.RootElement.GetProperty("execution").GetProperty("primaryCalls").GetInt32());
        Assert.Equal(0, association.RootElement.GetProperty("execution").GetProperty("providerCallsDuringScore").GetInt32());

        var headings = ReadPredictions(score.RootElement.GetProperty("modelHeadingCandidatesBeforeOverlapQuarantine"));
        var representations = ReadPredictions(score.RootElement.GetProperty("representationCandidates"));
        var gold = ReadGold();
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
            var patterns = value.Parts.Select(part => review.GetValueOrDefault($"{value.DocumentId}:{part.Alias}")).Where(pattern => pattern is not null).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();
            var reason = nonExactAssociatedPrediction.Contains(Key(value.DocumentId, value.Identity)) ? "ASSOCIATED_EXTENT_VARIANT" : overlapsGold.Length > 0 ? "UNMATCHED_OVERLAPPING_VARIANT" : SourceFamily(patterns);
            return new { value.DocumentId, value.Identity, value.CandidateId, parts = value.Parts, reason, reviewPatterns = patterns,
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
                scoreSource = $"{ScoreRoot}/full31-gold-multiaxis-score.v1.json", scoreSourceSha256 = Hash(File.ReadAllBytes(scorePath)),
                associationSource = $"{AssociationRoot}/full31-global-association-and-exact-score.v1.json", associationSourceSha256 = Hash(File.ReadAllBytes(associationPath)) },
            authority = new
            {
                metric = "P6S-F strict exact pre-projection: identity equality only; association is diagnostic",
                sourceReview = "SRC-089/SRC-095 reviewed pattern where present; absent review item is classified only as unreviewed non-heading, never Gold mutation",
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

    private static string SourceFamily(IReadOnlyList<string> patterns) => patterns.Contains("CONTENTS_ENTRY", StringComparer.Ordinal) ? "TOC_NAVIGATION_NONHEADING" :
        patterns.Contains("TABLE_OR_REFERENCE_LABEL", StringComparer.Ordinal) ? "INDEX_OR_REFERENCE_NONHEADING" :
        patterns.Contains("BOILERPLATE_EMPHASIS", StringComparer.Ordinal) ? "BOILERPLATE_NONHEADING" : patterns.Contains("AUTHOR_LINE", StringComparer.Ordinal) ? "AUTHOR_NONHEADING" : "BODY_OR_UNREVIEWED_NONHEADING";
    private static Dictionary<string, string> ReviewPatterns() => new[] { "SRC-089", "SRC-095" }.SelectMany(document =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{document}/review-items.json")));
        return json.RootElement.GetProperty("items").EnumerateArray().SelectMany(item => item.GetProperty("parts").EnumerateArray().Select(part => (Key: $"{document}:{part.GetProperty("sourceAlias").GetString()}", Pattern: item.GetProperty("pattern").GetString()!))).ToArray();
    }).GroupBy(value => value.Key, StringComparer.Ordinal).ToDictionary(value => value.Key, value => value.Select(item => item.Pattern).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
    private static List<Gold> ReadGold() => new[] { "SRC-089", "SRC-095" }.SelectMany(document =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldRoot}/{document}.gold.v1.json"))); var ordinal = 0;
        return json.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim => new Gold(document, ++ordinal, claim.GetProperty("identity").GetString()!, Parts(claim.GetProperty("boundParts")))).ToArray();
    }).ToList();
    private static List<Prediction> ReadPredictions(JsonElement array) => array.EnumerateArray().Select(item => new Prediction(item.GetProperty("DocumentId").GetString()!, item.GetProperty("CandidateId").GetString()!, item.GetProperty("Identity").GetString()!, Parts(item.GetProperty("Parts")), item.GetProperty("Kind").GetString()!)).ToList();
    private static IReadOnlyList<Part> Parts(JsonElement array) => array.EnumerateArray().Select(item => new Part(item.TryGetProperty("sourceAlias", out var alias) ? alias.GetString()! : item.GetProperty("Alias").GetString()!, item.TryGetProperty("utf16Span", out var span) ? span.GetProperty("start").GetInt32() : item.GetProperty("Start").GetInt32(), item.TryGetProperty("utf16Span", out span) ? span.GetProperty("end").GetInt32() : item.GetProperty("End").GetInt32())).ToArray();
    private static string Key(string documentId, string identity) => $"{documentId}:{identity}";
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
