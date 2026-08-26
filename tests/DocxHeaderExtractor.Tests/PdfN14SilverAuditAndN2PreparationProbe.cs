using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.4/N2-S preparation is deliberately offline. It freezes a blind human-audit sample, diagnostics,
/// and the sequential live-run contract before either OpenRouter call exists. Silver labels are used
/// only in the non-reviewer binding and diagnostics; the human packet contains source facts alone.
/// </summary>
public sealed class PdfN14SilverAuditAndN2PreparationProbe
{
    private const int ContextRadius = 2;
    private const int RandomPerLabelPerDocument = 10;
    private static readonly DocumentSpec[] Documents =
    [
        new("003", @"01_phap_quy\003_Luat_Doanh_nghiep_59-2020-QH14.docx"),
        new("029", @"02_hop_dong_mua_sam\029_WB_RFP_Works_DesignBuild_2021.docx"),
        new("042", @"03_tai_chinh_ke_toan\042_IDA_Financial_Statements_June_2025.docx"),
        new("057", @"04_giao_trinh\057_Quantitative_Methods_in_Finance_Lecture_Notes.docx"),
    ];
    private static readonly string[] LiveOrder = ["003", "057"];

    [Fact]
    public void WriteArtifacts()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N14_OUTPUT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        Directory.CreateDirectory(outputDirectory);
        WriteAll(root, outputDirectory);
    }

    [Fact]
    public void CommittedArtifactsReproduceAndReviewerPacketIsBlind()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var outputDirectory = Path.Combine(root, "eval", "benchmark-n0");
        var expectedPaths = new[]
        {
            Path.Combine(outputDirectory, "audit-samples", "n1.4-silver-human-audit-source.v1.json"),
            Path.Combine(outputDirectory, "audit-samples", "n1.4-silver-human-audit-binding.v1.json"),
            Path.Combine(outputDirectory, "diagnostics", "n1.4-silver-ranking.v1.json"),
            Path.Combine(outputDirectory, "n2-s", "manifest.v1.json"),
            Path.Combine(outputDirectory, "n2-s", "preflight.v1.json"),
        };
        if (expectedPaths.Any(path => !File.Exists(path))) return;

        var temporary = Path.Combine(Path.GetTempPath(), "dhx-n14-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAll(root, temporary);
            foreach (var expectedPath in expectedPaths)
            {
                var relative = Path.GetRelativePath(outputDirectory, expectedPath);
                var actualPath = Path.Combine(temporary, relative);
                Assert.Equal(Normalize(File.ReadAllText(expectedPath)), Normalize(File.ReadAllText(actualPath)));
            }

            using var packet = JsonDocument.Parse(File.ReadAllText(expectedPaths[0]));
            AssertNoLeakedSilverOrPipelineFields(packet.RootElement);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void ExistingN2PairIsSequentialAndUsesOneObservedProfile()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "benchmark-n0", "n2-s");
        var firstRun = Path.Combine(directory, "003-n2s-run.v1.json");
        var firstCheckpoint = Path.Combine(directory, "003-n2s-checkpoint.v1.jsonl");
        var secondRun = Path.Combine(directory, "057-n2s-run.v1.json");
        var secondCheckpoint = Path.Combine(directory, "057-n2s-checkpoint.v1.jsonl");
        if (!File.Exists(firstRun) || !File.Exists(firstCheckpoint) || !File.Exists(secondRun) || !File.Exists(secondCheckpoint)) return;

        var expectedRouteHash = DeterministicHash("analystBudget=160|wide=True|supplement=True|semanticHierarchy=False|semanticConcurrency=1");
        var first = ReadRun(firstRun, firstCheckpoint);
        var second = ReadRun(secondRun, secondCheckpoint);
        Assert.Equal("qwen/qwen3.5-9b", first.Model);
        Assert.Equal(first.Model, second.Model);
        Assert.Equal(expectedRouteHash, first.RouteHash);
        Assert.Equal(first.RouteHash, second.RouteHash);
        Assert.Equal("dedc7827c8d6930a2cd174fe95aa943c0ad958281a689369dd028e21dd765b0c", first.SourceSha256);
        Assert.Equal("f7a09e4da3aadd7c9c7ef6769657832cb69d314250be21d19e1c4dcfac804af9", second.SourceSha256);
        Assert.True(first.LastCheckpoint < second.FirstCheckpoint,
            $"003 must finish before 057 starts; observed {first.LastCheckpoint:O} >= {second.FirstCheckpoint:O}.");
    }

    private static void WriteAll(string root, string outputDirectory)
    {
        var audit = BuildAudit(root);
        Write(outputDirectory, "audit-samples/n1.4-silver-human-audit-source.v1.json", audit.ReviewerPacket);
        Write(outputDirectory, "audit-samples/n1.4-silver-human-audit-binding.v1.json", audit.Binding);
        Write(outputDirectory, "diagnostics/n1.4-silver-ranking.v1.json", BuildRankingDiagnostics(root));
        Write(outputDirectory, "n2-s/manifest.v1.json", BuildN2Manifest(root));
        Write(outputDirectory, "n2-s/preflight.v1.json", BuildPreflight(root));
    }

    private static AuditArtifacts BuildAudit(string root)
    {
        var all = new List<AuditCandidate>();
        foreach (var document in Documents)
        {
            var source = LoadSource(root, document);
            all.AddRange(LoadHeadings(root, document, source));
            all.AddRange(LoadNonHeadings(root, document, source));
        }

        var selected = new Dictionary<string, AuditCandidate>(StringComparer.Ordinal);
        foreach (var candidate in all.Where(c => c.IsHeading && c.Confidence == "MEDIUM"))
            selected.Add(candidate.AuditItemId, candidate with { SelectionReason = "all_medium_heading" });

        foreach (var document in Documents)
        {
            foreach (var isHeading in new[] { true, false })
            {
                var sampled = all
                    .Where(c => c.Document.Stem == document.Stem && c.IsHeading == isHeading && !selected.ContainsKey(c.AuditItemId))
                    .OrderBy(c => DeterministicHash($"n1.4-v1|{c.DocumentSha256}|{c.AuditItemId}"), StringComparer.Ordinal)
                    .ThenBy(c => c.AuditItemId, StringComparer.Ordinal)
                    .Take(RandomPerLabelPerDocument)
                    .Select(c => c with { SelectionReason = isHeading ? "stratified_heading" : "stratified_non_heading" });
                foreach (var candidate in sampled) selected.Add(candidate.AuditItemId, candidate);
            }
        }

        var ordered = selected.Values
            .OrderBy(c => c.Document.Stem, StringComparer.Ordinal)
            .ThenBy(c => c.Page)
            .ThenBy(c => c.AuditItemId, StringComparer.Ordinal)
            .ToArray();
        var n0ManifestSha = Sha256(Path.Combine(root, "keys", "benchmark-n0", "manifest.json"));

        var reviewerPacket = new
        {
            schemaVersion = 1,
            artifactKind = "n1_4_blind_human_audit_source_packet",
            parentManifestSha256 = n0ManifestSha,
            selection = new
            {
                policy = "all MEDIUM silver heading occurrences for 029/057 plus deterministic hash-stratified source occurrences per document and source role",
                randomPerLabelPerDocument = RandomPerLabelPerDocument,
                deterministicHash = "sha256(n1.4-v1|documentSha256|auditItemId)",
                reviewerBlindness = "No silver label, confidence, candidate, rank, selection, scope, analyst, or validated-output field is serialized.",
            },
            reviewInstructions = new
            {
                question = "Is this source occurrence a structural document-outline heading?",
                allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                occurrenceRule = "Preserve every listed source line in a wrapped heading occurrence.",
            },
            items = ordered.Select(c => new
            {
                c.AuditItemId,
                documentId = c.DocumentId,
                documentSha256 = c.DocumentSha256,
                page = c.Page,
                sourceLineIds = c.SourceLineIds,
                sourceSpan = new { startLineId = c.SourceLineIds.First(), endLineId = c.SourceLineIds.Last() },
                sourceTextLines = c.SourceTextLines,
                sourceText = string.Join("\n", c.SourceTextLines),
                previousLines = c.PreviousLines,
                nextLines = c.NextLines,
            }),
        };

        var binding = new
        {
            schemaVersion = 1,
            artifactKind = "n1_4_silver_audit_binding",
            parentManifestSha256 = n0ManifestSha,
            labelAuthority = "MODEL_ASSISTED_SILVER_ONLY",
            accuracyClaim = "NOT_HUMAN_ADJUDICATED; this binding exists only for post-review agreement/error analysis.",
            items = ordered.Select(c => new
            {
                c.AuditItemId,
                documentId = c.DocumentId,
                documentSha256 = c.DocumentSha256,
                sourceLineIds = c.SourceLineIds,
                sourceRole = c.IsHeading ? "HEADING" : "NON_HEADING",
                silverLabel = c.IsHeading ? "REVIEWED_HEADING" : "REVIEWED_NON_HEADING",
                silverConfidence = c.Confidence,
                kind = c.Kind,
                selectionReason = c.SelectionReason,
            }),
        };
        return new AuditArtifacts(reviewerPacket, binding);
    }

    private static object BuildRankingDiagnostics(string root)
    {
        var documents = Documents.Select(document =>
        {
            using var silver = JsonDocument.Parse(File.ReadAllText(SilverPath(root, document)));
            using var census = JsonDocument.Parse(File.ReadAllText(CensusPath(root, document)));
            var details = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
                .Select(item => new HeadingDetail(
                    StableId(item),
                    item.GetProperty("kind").GetString()!,
                    item.TryGetProperty("silverConfidence", out var confidence) ? confidence.GetString()! : "NOT_RECORDED"))
                .ToDictionary(item => item.StableId, StringComparer.Ordinal);
            var rows = CensusRows(census.RootElement)
                .Select(row => new RankingRow(
                    row.StableId,
                    details[row.StableId].Kind,
                    details[row.StableId].Confidence,
                    row.Status,
                    row.CoveringRank,
                    row.Selected,
                    row.Bucket))
                .ToArray();
            var fullRanks = rows.Where(r => r.Status == "full" && r.CoveringRank is not null).Select(r => r.CoveringRank!.Value).Order().ToArray();
            return new
            {
                stem = document.Stem,
                documentSha256 = silver.RootElement.TryGetProperty("sourcePacket", out var packet)
                    ? packet.GetProperty("documentSha256").GetString() : silver.RootElement.GetProperty("documentSha256").GetString(),
                denominators = new
                {
                    silverReviewed = rows.Length,
                    fullCandidate = fullRanks.Length,
                    selectedAt160 = rows.Count(r => r.Selected),
                    notFullCandidate = rows.Count(r => r.Status != "full"),
                },
                fullCandidateRank = RankStats(fullRanks),
                rankBuckets = new
                {
                    selectedAt160 = rows.Count(r => r.Status == "full" && r.CoveringRank is <= 160),
                    rank161To200 = rows.Count(r => r.Status == "full" && r.CoveringRank is >= 161 and <= 200),
                    rank201To500 = rows.Count(r => r.Status == "full" && r.CoveringRank is >= 201 and <= 500),
                    rankAbove500 = rows.Count(r => r.Status == "full" && r.CoveringRank is > 500),
                    fullCandidateWithoutRank = rows.Count(r => r.Status == "full" && r.CoveringRank is null),
                    candidateConstructionLoss = rows.Count(r => r.Status != "full"),
                },
                byKind = rows.GroupBy(r => r.Kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => Distribution(group.Key, group)),
                bySilverConfidence = rows.GroupBy(r => r.Confidence, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => Distribution(group.Key, group)),
            };
        }).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n1_4_silver_ranking_diagnostics",
            labelAuthority = "MODEL_ASSISTED_SILVER_ONLY",
            purpose = "Offline diagnosis of candidate/rank losses. It does not change the scorer, selection budget, silver labels, or N2-S profile.",
            rankSemantics = "coveringRank from frozen N1.3-S candidate census; identity is source occurrence, never candidateId across runs.",
            documents,
        };
    }

    private static object BuildN2Manifest(string root)
    {
        var n0Manifest = Path.Combine(root, "keys", "benchmark-n0", "manifest.json");
        var bundle = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "n1.2-silver-bundle-manifest.v1.json");
        return new
        {
            schemaVersion = 1,
            artifactKind = "n2_silver_live_run_manifest",
            parentManifestSha256 = Sha256(n0Manifest),
            silverBundleSha256 = Sha256(bundle),
            labelAuthority = "MODEL_ASSISTED_SILVER_ONLY",
            accuracyClaim = "N2-S is a silver semantic benchmark, not human-gold accuracy evidence.",
            profileProvenance = "The N1 cohort was frozen before these runs. This execution profile is reconciled from the two already-persisted, same-hash sequential run artifacts; it was not used to authorize a retry.",
            execution = new
            {
                ordering = "strictly_sequential",
                documentOrder = LiveOrder,
                prohibition = "057 starts only after 003 ends. The persisted pair proves this order; do not tune, retry, or create a second run to improve either outcome.",
                invalidRunRule = "Only a preflight or infrastructure failure may mark a run invalid; it is recorded, not retried for a better result.",
            },
            frozenProfile = new
            {
                command = "pdf-hierarchy-facts",
                backend = "OpenRouter",
                model = "qwen/qwen3.5-9b",
                wideCandidates = true,
                supplementCandidates = true,
                analystBlocks = 160,
                semanticConcurrency = 1,
                semanticRequestTimeoutSeconds = 90,
                semanticBatchTimeoutSeconds = 120,
                semanticLaneDeadlineSeconds = 300,
                visualRegions = 0,
                retry = false,
                roleAndSpanCheckpoint = true,
            },
            evaluatorContract = new
            {
                identity = "documentSha256 + page + sourceLineIds/sourceSpan; candidateId is diagnostics-only within one run",
                orderedMetrics = new[] { "decision_relevant", "role_survival", "span_resolved", "validated", "grounded", "emitted" },
                laneStatus = new[] { "complete", "partial_timeout", "provider_unavailable", "billing_unavailable", "authentication_unavailable", "cancelled", "pipeline_fault", "not_run" },
                interpretation = "A partial timeout is availability of partial evidence, not provider unavailability. Incomplete runs retain lane evidence but do not produce a final accuracy claim.",
            },
            documents = LiveOrder.Select(stem => N2DocumentBinding(root, Documents.Single(d => d.Stem == stem))),
        };
    }

    private static object BuildPreflight(string root) => new
    {
        schemaVersion = 1,
        artifactKind = "n2_silver_live_preflight",
        liveCallsMade = true,
        status = "verified_existing_sequential_pair",
        preflightTiming = "The N1 cohort was frozen before live execution. This integrity record was materialized after the existing pair and must not be misread as a pre-run authorization or a basis for retry.",
        evidenceIntegrity = LiveOrder.Select(stem =>
        {
            var document = Documents.Single(d => d.Stem == stem);
            return new
            {
                stem,
                sourceDocumentSha256 = Sha256(DocumentPath(root, document)),
                silverArtifactSha256 = Sha256(SilverPath(root, document)),
                censusArtifactSha256 = Sha256(CensusPath(root, document)),
                rawRunArtifact = $"{stem}-n2s-run.v1.json",
                checkpoint = $"{stem}-n2s-checkpoint.v1.jsonl",
                rawArtifactRequiredFields = new[] { "generation.codeRevision", "generation.backend", "generation.model", "rows[].semanticLaneStatus", "rows[].spanLaneStatus" },
                requiredRunEnvelopeFields = new[] { "rawRunArtifactSha256", "checkpointSha256", "sourceDocumentSha256", "silverArtifactSha256", "routeProfileSha256", "semanticLaneStatus", "spanLaneStatus" },
            };
        }),
        requiredRetention = new[] { "source SHA", "silver artifact SHA", "code revision", "route/profile", "semantic lane status", "span lane status", "role checkpoint", "span checkpoint", "run artifact", "hash/index manifest" },
    };

    private static object N2DocumentBinding(string root, DocumentSpec document) => new
    {
        stem = document.Stem,
        documentPath = Path.GetRelativePath(root, DocumentPath(root, document)).Replace('\\', '/'),
        sourceDocumentSha256 = Sha256(DocumentPath(root, document)),
        silverArtifact = Path.GetFileName(SilverPath(root, document)),
        silverArtifactSha256 = Sha256(SilverPath(root, document)),
        censusArtifact = Path.GetFileName(CensusPath(root, document)),
        censusArtifactSha256 = Sha256(CensusPath(root, document)),
        outputArtifact = $"{document.Stem}-n2s-run.v1.json",
        checkpoint = $"{document.Stem}-n2s-checkpoint.v1.jsonl",
    };

    private static IEnumerable<AuditCandidate> LoadHeadings(string root, DocumentSpec document, SourceSnapshot source)
    {
        using var silver = JsonDocument.Parse(File.ReadAllText(SilverPath(root, document)));
        foreach (var item in silver.RootElement.GetProperty("headingOccurrences").EnumerateArray())
        {
            var lineIds = item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var lineIndexes = lineIds.Select(id => source.IndexByLineId[id]).ToArray();
            yield return CreateCandidate(document, source, $"{document.Stem}/heading/{StableId(item)}", lineIds, lineIndexes,
                true, item.GetProperty("kind").GetString()!, item.TryGetProperty("silverConfidence", out var confidence) ? confidence.GetString()! : "NOT_RECORDED");
        }
    }

    private static IEnumerable<AuditCandidate> LoadNonHeadings(string root, DocumentSpec document, SourceSnapshot source)
    {
        using var silver = JsonDocument.Parse(File.ReadAllText(SilverPath(root, document)));
        foreach (var item in silver.RootElement.GetProperty("reviewedItems").EnumerateArray()
                     .Where(item => item.GetProperty("label").GetString() == "REVIEWED_NON_HEADING"))
        {
            var lineId = item.GetProperty("lineId").GetString()!;
            yield return CreateCandidate(document, source, item.GetProperty("reviewItemId").GetString()!, [lineId], [source.IndexByLineId[lineId]],
                false, "SOURCE_LINE", "NOT_RECORDED");
        }
    }

    private static AuditCandidate CreateCandidate(DocumentSpec document, SourceSnapshot source, string auditItemId, string[] lineIds, int[] lineIndexes, bool isHeading, string kind, string confidence)
    {
        var start = lineIndexes.Min();
        var end = lineIndexes.Max();
        return new AuditCandidate(
            auditItemId, document, source.DocumentId, source.DocumentSha256, source.Lines[start].Page,
            lineIds, lineIndexes.Select(index => source.Lines[index].Text).ToArray(),
            Neighbors(source.Lines, start, -1), Neighbors(source.Lines, end, 1), isHeading, kind, confidence, "");
    }

    private static SourceSnapshot LoadSource(string root, DocumentSpec document)
    {
        var path = DocumentPath(root, document);
        var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path).Lines;
        return new SourceSnapshot(Path.GetFileNameWithoutExtension(path), Sha256(path), lines,
            lines.Select((line, index) => (LineId: PdfCandidateProvenance.LineId(line), Index: index))
                .ToDictionary(pair => pair.LineId, pair => pair.Index, StringComparer.Ordinal));
    }

    private static IEnumerable<CensusRow> CensusRows(JsonElement root)
    {
        foreach (var group in root.GetProperty("occurrences").EnumerateObject())
        foreach (var row in group.Value.EnumerateArray())
            yield return new CensusRow(
                row.GetProperty("stableId").GetString()!,
                row.GetProperty("status").GetString()!,
                row.GetProperty("selected").GetBoolean(),
                row.TryGetProperty("coveringRank", out var rank) && rank.ValueKind != JsonValueKind.Null ? rank.GetInt32() : null,
                row.GetProperty("bucket").GetString()!);
    }

    private static object Distribution(string key, IEnumerable<RankingRow> rows)
    {
        var values = rows.ToArray();
        return new
        {
            key,
            reviewed = values.Length,
            fullCandidate = values.Count(row => row.Status == "full"),
            selectedAt160 = values.Count(row => row.Selected),
            candidateConstructionLoss = values.Count(row => row.Status != "full"),
            rank161To200 = values.Count(row => row.CoveringRank is >= 161 and <= 200),
            rank201To500 = values.Count(row => row.CoveringRank is >= 201 and <= 500),
            rankAbove500 = values.Count(row => row.CoveringRank is > 500),
        };
    }

    private static object RankStats(int[] sorted) => new
    {
        count = sorted.Length,
        min = sorted.Length == 0 ? (int?)null : sorted[0],
        p50 = Percentile(sorted, .50),
        p90 = Percentile(sorted, .90),
        max = sorted.Length == 0 ? (int?)null : sorted[^1],
    };

    private static int? Percentile(int[] sorted, double percentile) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];

    private static object[] Neighbors(IReadOnlyList<PdfLine> lines, int index, int direction)
    {
        var result = new List<object>();
        for (var offset = 1; offset <= ContextRadius; offset++)
        {
            var neighborIndex = index + direction * offset;
            if (neighborIndex < 0 || neighborIndex >= lines.Count) break;
            var neighbor = lines[neighborIndex];
            result.Add(new { page = neighbor.Page, lineId = PdfCandidateProvenance.LineId(neighbor), text = neighbor.Text });
        }
        return result.ToArray();
    }

    private static string StableId(JsonElement item) => item.TryGetProperty("goldStableId", out var gold)
        ? gold.GetString()! : item.GetProperty("silverStableId").GetString()!;
    private static string DeterministicHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string DocumentPath(string root, DocumentSpec document) => Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.Relative);
    private static string SilverPath(string root, DocumentSpec document) => Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{document.Stem}-n1.2-silver-model-assisted.v1.json");
    private static string CensusPath(string root, DocumentSpec document) => Path.Combine(root, "eval", "benchmark-n0", "census", $"{document.Stem}-n1.3-census.v1.json");
    private static void Write(string root, string relative, object value)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string json) => json.Replace("\r\n", "\n");

    private static ObservedRun ReadRun(string runPath, string checkpointPath)
    {
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        var generation = run.RootElement.GetProperty("generation");
        var row = run.RootElement.GetProperty("rows")[0];
        var times = File.ReadLines(checkpointPath)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("completedAt").GetDateTimeOffset())
            .Order()
            .ToArray();
        Assert.NotEmpty(times);
        return new ObservedRun(
            generation.GetProperty("model").GetString()!,
            generation.GetProperty("routeConfigSha256").GetString()!,
            row.GetProperty("sourceDocumentSha256").GetString()!,
            times[0], times[^1]);
    }

    private static void AssertNoLeakedSilverOrPipelineFields(JsonElement value)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "silverLabel", "silverConfidence", "selectionReason", "sourceRole", "candidateId", "candidateScore", "rank", "selected", "structuralScope", "analystOutput", "validatedOutput",
        };
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden);
                AssertNoLeakedSilverOrPipelineFields(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertNoLeakedSilverOrPipelineFields(item);
    }

    private sealed record DocumentSpec(string Stem, string Relative);
    private sealed record SourceSnapshot(string DocumentId, string DocumentSha256, IReadOnlyList<PdfLine> Lines, IReadOnlyDictionary<string, int> IndexByLineId);
    private sealed record AuditCandidate(string AuditItemId, DocumentSpec Document, string DocumentId, string DocumentSha256, int Page,
        string[] SourceLineIds, string[] SourceTextLines, object[] PreviousLines, object[] NextLines, bool IsHeading, string Kind, string Confidence, string SelectionReason);
    private sealed record AuditArtifacts(object ReviewerPacket, object Binding);
    private sealed record HeadingDetail(string StableId, string Kind, string Confidence);
    private sealed record CensusRow(string StableId, string Status, bool Selected, int? CoveringRank, string Bucket);
    private sealed record RankingRow(string StableId, string Kind, string Confidence, string Status, int? CoveringRank, bool Selected, string Bucket);
    private sealed record ObservedRun(string Model, string RouteHash, string SourceSha256, DateTimeOffset FirstCheckpoint, DateTimeOffset LastCheckpoint);
}
