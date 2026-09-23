using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline scorer for the completed nine-cell direct semantic probe.</summary>
public sealed class DirectSemanticProbeScoringTests
{
    private const string RetryRoot =
        "eval/a99-closed-loop/direct-semantic-discrimination-probe-retry-v1/DOC-0252";
    private const string ContinuationRoot =
        "eval/a99-closed-loop/direct-semantic-discrimination-probe-continuation-v1/DOC-0252";
    private const string ScoreRoot =
        "eval/a99-closed-loop/direct-semantic-discrimination-probe-score-v1/DOC-0252";
    private const string ScoreFile = "direct-semantic-probe-score.v1.json";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string PromptSha256 =
        "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387";
    private const string SchemaSha256 =
        "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string ProviderInputPlanSha256 =
        "a792fb0f03b2c6c16facecba5485ed48dc7742610f7542e0936ca88ecd59243b";

    private static readonly string[] Labels = ["STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL"];

    [Fact]
    public void Score_the_completed_cohort_from_immutable_raw_bodies()
    {
        var artifact = BuildScore();
        var path = Path.Combine(TestRepository.Path(ScoreRoot), ScoreFile);
        var text = JsonSerializer.Serialize(artifact, FreezeArtifact.Json).ReplaceLineEndings("\n");

        if (!File.Exists(path))
        {
            Assert.Equal("1", Environment.GetEnvironmentVariable("A99_DIRECT_SEMANTIC_PROBE_SCORE_WRITE"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        else
        {
            FreezeArtifact.AssertText(ScoreRoot, ScoreFile, text);
        }

        Assert.Equal(9, artifact.GetType().GetProperty("usableSemanticCells")!.GetValue(artifact));
        Assert.Equal("PROBE_VALID", artifact.GetType().GetProperty("probeValidity")!.GetValue(artifact));
    }

    private static object BuildScore()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var build = DirectSemanticProbePreflightTests.Build(plan);
        Assert.Equal(SourceSha256, CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf)));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        Assert.Equal(PromptSha256, CanonicalArtifactHash.OfText(
            DirectSemanticProbeRetryPreflightTests.RetryProbePrompt));
        Assert.Equal(SchemaSha256, HashSchema(build.Items.Select(item => item.ItemId).ToArray()));

        var itemsByPack = build.Items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key.Split(':')[1],
                group => group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var mapping = ExplicitMapping();
        Assert.Equal(9, mapping.Count);
        Assert.Equal(9, mapping.Select(cell => cell.Identity).Distinct(StringComparer.Ordinal).Count());

        var cells = mapping.Select(cell => ReadCell(cell, itemsByPack[cell.Pack])).ToArray();
        var contractErrors = cells.SelectMany(cell => cell.Errors).ToArray();
        var returnedRows = cells.SelectMany(cell => cell.Decisions.Select(decision => new
        {
            cell.Repeat,
            cell.Pack,
            CellIdentity = cell.Identity,
            ClaimIdentity = decision.Identity,
            RawResponseSha256 = cell.RawResponseSha256,
            ItemId = decision.ItemId,
            decision.Expected,
            decision.Returned,
            decision.Family,
            Correct = decision.Expected == decision.Returned,
        })).ToArray();

        Assert.Equal(9, cells.Length);
        Assert.Equal(9, cells.Count(cell => cell.RawResponseCaptured));
        Assert.Empty(contractErrors);
        Assert.Equal(54, returnedRows.Length);

        var f1 = FamilySummary("F1", returnedRows);
        var f2 = FamilySummary("F2", returnedRows);
        var f3 = FamilySummary("F3", returnedRows);
        var structural = returnedRows.Where(row => row.Expected == "STRUCTURAL_UNIT").ToArray();
        var label = returnedRows.Where(row => row.Expected == "DOCUMENT_LABEL").ToArray();
        var matrix = Labels.ToDictionary(expected => expected,
            expected => Labels.ToDictionary(returned => returned,
                returned => returnedRows.Count(row => row.Expected == expected && row.Returned == returned),
                StringComparer.Ordinal), StringComparer.Ordinal);

        var stability = build.Items.Select(item =>
        {
            var sequence = returnedRows.Where(row => row.ItemId == item.ItemId)
                .OrderBy(row => row.Repeat).Select(row => row.Returned).ToArray();
            var distinct = sequence.Distinct(StringComparer.Ordinal).Count();
            return new
            {
                itemId = item.ItemId,
                identity = item.Identity,
                family = item.ExpectedLabel switch
                {
                    "NON_STRUCTURAL" => FamilyOf(item.Identity),
                    "DOCUMENT_LABEL" => "DOCUMENT_LABEL_CONTROL",
                    _ => "STRUCTURAL_CONTROL",
                },
                expected = item.ExpectedLabel,
                returnedByRepeat = sequence,
                stability = distinct == 1 ? "STABLE_3_OF_3" :
                    sequence.GroupBy(value => value).Max(group => group.Count()) >= 2
                        ? "MAJORITY_2_OF_3" : "FULLY_VARIABLE",
            };
        }).ToArray();

        var rawVariance = cells.GroupBy(cell => cell.Pack, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => new
                {
                    hashesByRepeat = group.OrderBy(cell => cell.Repeat)
                        .ToDictionary(cell => $"r{cell.Repeat}", cell => cell.RawResponseSha256),
                    distinctHashCount = group.Select(cell => cell.RawResponseSha256)
                        .Distinct(StringComparer.Ordinal).Count(),
                }, StringComparer.Ordinal);

        var structuralSound = structural.Count(row => row.Correct) >=
            Math.Ceiling(structural.Length * 0.9);
        var labelSound = label.All(row => row.Correct);
        var f1f2MostlyCorrect = f1.Correct >= 2 && f2.Correct >= 2;
        var validity = contractErrors.Length == 0 ? "PROBE_VALID" : "PROBE_CONTRACT_OR_CONTEXT_INVALID";
        var outcome = validity != "PROBE_VALID" ? "PROBE_CONTRACT_OR_CONTEXT_INVALID" :
            f1f2MostlyCorrect && structuralSound && labelSound
                ? "DIRECT_SEMANTIC_CAPABILITY_PRESENT"
                : (f1.Correct == 0 || f2.Correct == 0) && structuralSound
                    ? "DIRECT_SEMANTIC_DISCRIMINATION_LIMIT_OBSERVED"
                    : "PROBE_CONTRACT_OR_CONTEXT_INVALID";

        return new
        {
            artifactKind = "a99_direct_semantic_probe_score",
            schemaVersion = "a99-direct-semantic-probe-score-v1",
            documentId = "DOC-0252",
            sourceSha256 = SourceSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldSha256 = GoldSha256,
            promptSha256 = PromptSha256,
            schemaSha256 = SchemaSha256,
            providerInputPlanSha256 = ProviderInputPlanSha256,
            model = "qwen/qwen3.7-flash",
            transportLineage = new
            {
                originalRetry = new { successfulCells = 7, http429Failures = 1, reservedUnusedCells = 1 },
                continuation = new { successfulCells = 2, providerCalls = 2 },
                rejectedHttp400LineagePreserved = true,
            },
            canonicalCohort = mapping.Select(cell => new
            {
                identity = cell.Identity,
                repeat = cell.Repeat,
                pack = cell.Pack,
                captureLineage = cell.Lineage,
                capturePath = cell.Path,
                rawResponseSha256 = cells.Single(result => result.Identity == cell.Identity).RawResponseSha256,
            }).ToArray(),
            usableSemanticCells = cells.Length,
            duplicateSemanticCellIdentities = 0,
            missingSemanticCellIdentities = 0,
            rawShaParity = true,
            itemContractErrors = new
            {
                missingItem = contractErrors.Count(error => error.StartsWith("MISSING_ITEM", StringComparison.Ordinal)),
                duplicateItem = contractErrors.Count(error => error.StartsWith("DUPLICATE_ITEM", StringComparison.Ordinal)),
                unknownItemId = contractErrors.Count(error => error.StartsWith("UNKNOWN_ITEM_ID", StringComparison.Ordinal)),
                invalidLabel = contractErrors.Count(error => error.StartsWith("INVALID_LABEL", StringComparison.Ordinal)),
                details = contractErrors,
            },
            probeValidity = validity,
            itemResults = returnedRows,
            primaryFamilies = new
            {
                F1_NON_STRUCTURAL = f1,
                F2_NON_STRUCTURAL = f2,
                F3_NON_STRUCTURAL = f3,
            },
            goldStructuralControls = new
            {
                correct = structural.Count(row => row.Correct),
                total = structural.Length,
                R1 = structural.Count(row => row.Repeat == 1 && row.Correct),
                R2 = structural.Count(row => row.Repeat == 2 && row.Correct),
                R3 = structural.Count(row => row.Repeat == 3 && row.Correct),
                incorrect = structural.Where(row => !row.Correct).Select(row => new
                {
                    claimIdentity = row.ClaimIdentity, cellIdentity = row.CellIdentity,
                    row.ItemId, row.Repeat, row.Expected, row.Returned,
                }).ToArray(),
            },
            documentLabelControl = new
            {
                correct = label.Count(row => row.Correct),
                total = label.Length,
                returnedByRepeat = label.OrderBy(row => row.Repeat)
                    .Select(row => new { repeat = row.Repeat, row.Returned }).ToArray(),
            },
            confusionMatrix = matrix,
            repeatStability = stability,
            rawResponseHashVariance = rawVariance,
            physicalStage1MembershipOnly = new { F1 = "3/3", F2 = "3/3", F3 = "1/3" },
            directProbeClassification = outcome,
            interpretation = outcome switch
            {
                "DIRECT_SEMANTIC_CAPABILITY_PRESENT" =>
                    "The direct closed-set distinction is present under this probe; the observed limitation is more consistent with open-set extraction/membership formulation.",
                "DIRECT_SEMANTIC_DISCRIMINATION_LIMIT_OBSERVED" =>
                    "Qwen3.7 Flash shows a limit on this specific distinction under this policy/context; no broader model or document generalization is permitted.",
                _ => "No capability inference is permitted because the probe contract or context is invalid.",
            },
            secondModelCalls = 0,
            materializedGold = "48/3955",
            crossGenreSemanticCapabilityEstablished = false,
            scoringSource = "raw response UTF-8 bytes after SHA parity; no run summary decisions used",
        };
    }

    private static CellResult ReadCell(CellMap cell, DirectSemanticProbePreflightTests.ProbeItem[] items)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Path("eval/a99-closed-loop"), cell.Path)));
        var root = document.RootElement;
        Assert.Equal("CAPTURE_COMPLETE", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("rawResponseCaptured").GetBoolean());
        Assert.Equal(PromptSha256, root.GetProperty("promptSha256").GetString());
        Assert.Equal(SourceSha256, root.GetProperty("sourceSha256").GetString());
        Assert.Equal(SourceUniverseSha256, root.GetProperty("sourceUniverseSha256").GetString());

        var rawBytes = Convert.FromBase64String(root.GetProperty("rawResponseUtf8Base64").GetString()!);
        var rawSha = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        Assert.Equal(root.GetProperty("rawResponseSha256").GetString(), rawSha);
        var errors = new List<string>();
        var decisions = new List<DecisionResult>();
        using var response = JsonDocument.Parse(rawBytes);
        if (!response.RootElement.TryGetProperty("decisions", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("MISSING_ITEM:decisions-array");
            return new CellResult(cell.Identity, cell.Pack, cell.Repeat, cell.Lineage,
                root.GetProperty("rawResponseSha256").GetString()!, true, decisions, errors);
        }

        var expectedById = items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.TryGetProperty("itemId", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() : null;
            var returned = entry.TryGetProperty("classification", out var classValue) &&
                classValue.ValueKind == JsonValueKind.String ? classValue.GetString() : null;
            if (id is null || !expectedById.ContainsKey(id))
            {
                errors.Add($"UNKNOWN_ITEM_ID:{id}");
                continue;
            }
            if (!seen.Add(id))
            {
                errors.Add($"DUPLICATE_ITEM:{id}");
                continue;
            }
            if (returned is null || !Labels.Contains(returned, StringComparer.Ordinal))
            {
                errors.Add($"INVALID_LABEL:{id}:{returned}");
                continue;
            }
            var item = expectedById[id];
            decisions.Add(new DecisionResult(id, item.Identity, item.ExpectedLabel, returned,
                FamilyOf(item.Identity)));
        }

        foreach (var item in items.Where(item => !seen.Contains(item.ItemId)))
            errors.Add($"MISSING_ITEM:{item.ItemId}");
        return new CellResult(cell.Identity, cell.Pack, cell.Repeat, cell.Lineage,
            root.GetProperty("rawResponseSha256").GetString()!, true, decisions, errors);
    }

    private static FamilyScore FamilySummary(string family, IEnumerable<dynamic> rows)
    {
        var selected = rows.Where(row => row.Family == family).OrderBy(row => row.Repeat).ToArray();
        return new FamilyScore("NON_STRUCTURAL",
            selected.Select(row => new { repeat = row.Repeat, returned = row.Returned }).Cast<object>().ToArray(),
            selected.Count(row => row.Returned == "NON_STRUCTURAL"), 3);
    }

    private static string FamilyOf(string identity) =>
        identity.Contains("L0513:S0", StringComparison.Ordinal) ? "F1" :
        identity.Contains("L0515:S0", StringComparison.Ordinal) ? "F2" :
        identity.Contains("L0516:S0", StringComparison.Ordinal) ? "F3" :
        identity.Contains("L0000:S0", StringComparison.Ordinal) ? "DOCUMENT_LABEL_CONTROL" :
        "STRUCTURAL_CONTROL";

    private static string HashSchema(string[] itemIds)
    {
        var schema = DirectSemanticProbePreflightTests.ProbeSchema(itemIds);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(schema, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));
    }

    private static IReadOnlyList<CellMap> ExplicitMapping() =>
    [
        new(1, "PACK_001", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r1/PACK_001.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(1, "PACK_005", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r1/PACK_005.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(1, "PACK_006", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r1/PACK_006.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(2, "PACK_001", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r2/PACK_001.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(2, "PACK_005", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r2/PACK_005.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(2, "PACK_006", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r2/PACK_006.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(3, "PACK_001", "direct-semantic-discrimination-probe-retry-v1/DOC-0252/r3/PACK_001.direct-semantic-probe-retry-capture.v1.json", "direct-semantic-discrimination-probe-retry-v1"),
        new(3, "PACK_005", "direct-semantic-discrimination-probe-continuation-v1/DOC-0252/r3/PACK_005.continuation-capture.v1.json", "direct-semantic-discrimination-probe-continuation-v1"),
        new(3, "PACK_006", "direct-semantic-discrimination-probe-continuation-v1/DOC-0252/r3/PACK_006.continuation-capture.v1.json", "direct-semantic-discrimination-probe-continuation-v1"),
    ];

    private sealed record CellMap(int Repeat, string Pack, string Path, string Lineage)
    {
        public string Identity => $"r{Repeat}/{Pack}";
    }

    private sealed record CellResult(string Identity, string Pack, int Repeat, string Lineage,
        string RawResponseSha256, bool RawResponseCaptured, IReadOnlyList<DecisionResult> Decisions,
        IReadOnlyList<string> Errors);

    private sealed record DecisionResult(string ItemId, string Identity, string Expected,
        string Returned, string Family);

    private sealed record FamilyScore(string Expected, object[] Results, int Correct, int Total);
}
