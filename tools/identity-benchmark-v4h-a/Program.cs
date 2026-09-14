using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV4HA;

internal static class Program
{
    private const string SampleRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/sample.json";
    private const string SourceRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/semantic-adjudication";
    private const string OrderSeed = "a99-v4h-a-neutral-review-order-v1";
    private const int ExpectedSample = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V4H_A_STATUS=BLINDED_ADJUDICATION_PACK_FROZEN PROVIDER_CALLS=0 MODEL_CALLS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4H_A_ERROR={ex}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var samplePath = Full(root, SampleRelative);
        var sourcePath = Full(root, SourceRelative);
        Require(File.Exists(samplePath) && File.Exists(sourcePath), "V4H_A_SOURCE_INPUT_MISSING");
        using var sample = Read(samplePath);
        using var source = Read(sourcePath);
        var sampleCandidates = sample.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.Clone()).ToArray();
        Require(sampleCandidates.Length == ExpectedSample, "V4H_A_SAMPLE_COUNT");
        Require(sampleCandidates.Select(x => Text(x, "pairId")!).Distinct(StringComparer.Ordinal).Count() == ExpectedSample, "V4H_A_SAMPLE_DUPLICATE");

        var catalogHash = Sha256File(sourcePath);
        var catalogFingerprint = Text(source.RootElement, "catalogFingerprint")!;
        var docs = source.RootElement.GetProperty("sourceDocuments").EnumerateArray().ToDictionary(x => Text(x, "documentId")!, x => x.Clone(), StringComparer.Ordinal);
        var occurrences = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(x => new Occurrence(
            Text(x, "nodeId")!, Text(x, "text")!, x.GetProperty("documentOrder").GetInt32())).ToArray();
        var byId = occurrences.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
        var byDocument = occurrences.GroupBy(x => DocumentOf(x.NodeId), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.DocumentOrder).ToArray(), StringComparer.Ordinal);
        var rawItems = new List<ReviewItem>(ExpectedSample);
        foreach (var candidate in sampleCandidates)
        {
            var candidateId = Text(candidate, "pairId")!;
            var documentId = Text(candidate, "documentId")!;
            var leftId = Text(candidate, "left")!;
            var rightId = Text(candidate, "right")!;
            Require(docs.ContainsKey(documentId), "V4H_A_UNKNOWN_DOCUMENT:" + candidateId);
            Require(byId.TryGetValue(leftId, out var left), "V4H_A_UNKNOWN_LEFT:" + candidateId);
            Require(byId.TryGetValue(rightId, out var right), "V4H_A_UNKNOWN_RIGHT:" + candidateId);
            Require(DocumentOf(left!.NodeId) == documentId && DocumentOf(right!.NodeId) == documentId, "V4H_A_DOCUMENT_BINDING:" + candidateId);
            rawItems.Add(new ReviewItem(candidateId, documentId, left!, right!, docs[documentId].Clone(), byDocument[documentId], catalogFingerprint));
        }

        var ordered = rawItems.OrderBy(x => Sha256Text(x.CandidateId + "|" + OrderSeed), StringComparer.Ordinal).ToArray();
        var reviewItems = ordered.Select((item, index) => item.ToReview(index + 1)).ToArray();
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);

        await WriteAsync(Path.Combine(output, "adjudication-contract.json"), new
        {
            schemaVersion = "a99-v4h-a-adjudication-contract-v1",
            allowedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT_SEMANTIC_NODE", "UNRESOLVED" },
            continuationIsDirectional = true,
            confidence = new[] { "HIGH", "MEDIUM", "LOW" },
            unresolvedMustNotBeForced = true,
            sourceAuthority = "SOURCE_DOCUMENT_PLUS_HUMAN_ADJUDICATION",
            modelOutputFields = Array.Empty<string>(),
        });
        await WriteAsync(Path.Combine(output, "review-item-manifest.json"), new
        {
            schemaVersion = "a99-v4h-a-review-item-manifest-v1",
            itemCount = reviewItems.Length,
            orderSeed = OrderSeed,
            canonicalOrdering = "SHA256(candidateId|orderSeed) ASC",
            sourceCatalogFingerprint = catalogFingerprint,
            sourceCatalogSha256 = catalogHash,
            items = reviewItems.Select(x => new { x.ReviewId, x.CandidateId, x.DocumentId, x.LeftOccurrenceId, x.RightOccurrenceId, x.SourceBindingSha256 }).ToArray(),
        });
        await WriteAsync(Path.Combine(output, "review-form.json"), new
        {
            schemaVersion = "a99-v4h-a-review-form-v1",
            instructions = "Complete only from source evidence. Do not add model-derived fields.",
            fields = new[] { "relation", "confidence", "evidenceNote", "needsMoreSourceInspection", "adjudicator" },
            items = reviewItems.Select(x => new
            {
                x.ReviewId,
                x.CandidateId,
                x.DocumentId,
                x.LeftOccurrenceId,
                x.RightOccurrenceId,
                sourceEvidence = new
                {
                    left = new { occurrenceId = x.LeftOccurrenceId, text = x.LeftText, documentOrder = x.LeftDocumentOrder },
                    right = new { occurrenceId = x.RightOccurrenceId, text = x.RightText, documentOrder = x.RightDocumentOrder },
                    leftNearby = x.LeftContext,
                    rightNearby = x.RightContext,
                },
                originalSourceNavigation = new { sourcePath = x.SourcePath, sourceSha256 = x.SourceSha256, leftDocumentOrder = x.LeftDocumentOrder, rightDocumentOrder = x.RightDocumentOrder },
                relation = (string?)null,
                confidence = (string?)null,
                evidenceNote = (string?)null,
                needsMoreSourceInspection = (bool?)null,
                adjudicator = (string?)null,
            }).ToArray(),
        });
        await WriteAsync(Path.Combine(output, "blinding-firewall.json"), new
        {
            schemaVersion = "a99-v4h-a-blinding-firewall-v1",
            providerCalls = 0,
            modelCalls = 0,
            providerResponseReadCount = 0,
            modelPredictionReadCount = 0,
            existingGoldReadCount = 0,
            sampleChanged = false,
            all128Included = reviewItems.Length == ExpectedSample,
            candidateRankVisible = false,
            retrievalReasonsVisible = false,
            sourceOnlyInputs = new[] { SampleRelative, SourceRelative },
            forbiddenInputs = new[] { "V4G-B parsed-results", "V4G-B execution", "V4G-C audit", "V4F-E predictions", "OLD predictions", "model rationales", "existing Gold" },
        });
        await File.WriteAllTextAsync(Path.Combine(output, "review-pack.md"), BuildReviewPack(reviewItems), new UTF8Encoding(false));
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v4h-a-manifest-v1",
            status = "READY_FOR_V4H_BLINDED_HUMAN_ADJUDICATION",
            source = "FROZEN_V4F_D_SAMPLE",
            expectedHead = "39e410e84c989929c533d2f2287e697165c390b8",
            frozenCandidates = ExpectedSample,
            included = reviewItems.Length,
            excluded = ExpectedSample - reviewItems.Length,
            exactSourceBindings = reviewItems.Count(x => x.SourceBindingValid) + "/" + ExpectedSample,
            authority = "BLINDED_SOURCE_BACKED_USER_ADJUDICATION",
            independentHumanGold = false,
            providerCalls = 0,
            modelCalls = 0,
            providerResponseReadCount = 0,
            modelPredictionReadCount = 0,
            existingGoldReadCount = 0,
            sampleChanged = false,
            all128Included = reviewItems.Length == ExpectedSample,
            reviewItemManifestSha256 = Sha256File(Path.Combine(output, "review-item-manifest.json")),
            reviewPackSha256 = Sha256File(Path.Combine(output, "review-pack.md")),
        });
    }

    private static string BuildReviewPack(IReadOnlyList<FrozenReviewItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# V4H-A — blinded source-backed semantic adjudication pack");
        builder.AppendLine();
        builder.AppendLine("Review every item from source evidence only. Do not infer or add model fields. UNRESOLVED is allowed.");
        builder.AppendLine();
        foreach (var item in items)
        {
            builder.AppendLine($"## {item.ReviewId}");
            builder.AppendLine();
            builder.AppendLine($"- Document: `{item.DocumentId}`");
            builder.AppendLine($"- Source binding: `{item.CandidateId}`");
            builder.AppendLine($"- Left occurrence: `{item.LeftOccurrenceId}` (order {item.LeftDocumentOrder})");
            builder.AppendLine($"- Left text: {item.LeftText}");
            builder.AppendLine($"- Right occurrence: `{item.RightOccurrenceId}` (order {item.RightDocumentOrder})");
            builder.AppendLine($"- Right text: {item.RightText}");
            builder.AppendLine();
            builder.AppendLine("### Source navigation");
            builder.AppendLine();
            builder.AppendLine($"- Source path: `{item.SourcePath}`");
            builder.AppendLine($"- Source SHA256: `{item.SourceSha256}`");
            builder.AppendLine();
            builder.AppendLine("### Nearby source occurrences");
            builder.AppendLine();
            builder.AppendLine("Left context:");
            foreach (var context in item.LeftContext) builder.AppendLine($"- `{context.NodeId}` order {context.DocumentOrder}: {context.Text}");
            builder.AppendLine("Right context:");
            foreach (var context in item.RightContext) builder.AppendLine($"- `{context.NodeId}` order {context.DocumentOrder}: {context.Text}");
            builder.AppendLine();
            builder.AppendLine("### Human review");
            builder.AppendLine();
            builder.AppendLine("- relation: `SAME_SEMANTIC_REPEAT | CONTINUATION_OF | DISTINCT_SEMANTIC_NODE | UNRESOLVED`");
            builder.AppendLine("- continuation direction, if applicable:");
            builder.AppendLine("- confidence: `HIGH | MEDIUM | LOW`");
            builder.AppendLine("- evidence note:");
            builder.AppendLine("- needs more source inspection: `true | false`");
            builder.AppendLine("- adjudicator:");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static string? Text(JsonElement node, string property) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record Occurrence(string NodeId, string Text, int DocumentOrder);

    private sealed class ReviewItem
    {
        public ReviewItem(string candidateId, string documentId, Occurrence left, Occurrence right, JsonElement document, IReadOnlyList<Occurrence> documentOccurrences, string catalogFingerprint)
        {
            CandidateId = candidateId; DocumentId = documentId; Left = left; Right = right;
            SourcePath = Text(document, "sourcePath")!; SourceSha256 = Text(document, "sourceSha256")!;
            SourceBindingSha256 = Sha256Text(string.Join("|", candidateId, documentId, left.NodeId, right.NodeId, catalogFingerprint));
            LeftContext = Context(documentOccurrences, left.DocumentOrder);
            RightContext = Context(documentOccurrences, right.DocumentOrder);
        }
        public string CandidateId { get; }
        public string DocumentId { get; }
        public Occurrence Left { get; }
        public Occurrence Right { get; }
        public string SourcePath { get; }
        public string SourceSha256 { get; }
        public string SourceBindingSha256 { get; }
        public IReadOnlyList<Occurrence> LeftContext { get; }
        public IReadOnlyList<Occurrence> RightContext { get; }
        public bool SourceBindingValid => true;
        private static IReadOnlyList<Occurrence> Context(IReadOnlyList<Occurrence> all, int order)
        {
            var previous = all.Where(x => x.DocumentOrder < order).TakeLast(3);
            var next = all.Where(x => x.DocumentOrder > order).Take(3);
            return previous.Concat(next).ToArray();
        }
        public FrozenReviewItem ToReview(int number) => new($"SA-{number:D4}", CandidateId, DocumentId, Left.NodeId, Left.Text, Left.DocumentOrder, Right.NodeId, Right.Text, Right.DocumentOrder, SourcePath, SourceSha256, SourceBindingSha256, LeftContext, RightContext, SourceBindingValid);
    }

    private sealed record FrozenReviewItem(string ReviewId, string CandidateId, string DocumentId, string LeftOccurrenceId, string LeftText, int LeftDocumentOrder, string RightOccurrenceId, string RightText, int RightDocumentOrder, string SourcePath, string SourceSha256, string SourceBindingSha256, IReadOnlyList<Occurrence> LeftContext, IReadOnlyList<Occurrence> RightContext, bool SourceBindingValid);
}
