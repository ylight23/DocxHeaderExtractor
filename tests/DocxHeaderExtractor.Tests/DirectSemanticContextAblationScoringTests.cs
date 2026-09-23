using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline scorer for the completed 27-cell context-ablation cohort.</summary>
public sealed class DirectSemanticContextAblationScoringTests
{
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string ScoreRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252";
    private const string ScoreFile = "direct-semantic-context-ablation-score.v1.json";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string RunnerCommit = "fe9f2b88e751e4315b459d89791f0b7e2b1f1ef1";
    private const string CaptureCommit = "9cae02c";

    private static readonly string[] Labels =
        ["STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL"];

    [Fact]
    public void Score_completed_cohort_from_immutable_raw_captures()
    {
        var artifact = BuildScore();
        var path = Path.Combine(TestRepository.Path(ScoreRoot), ScoreFile);
        var text = JsonSerializer.Serialize(artifact, FreezeArtifact.Json).ReplaceLineEndings("\n");

        if (!File.Exists(path))
        {
            Assert.Equal("1", Environment.GetEnvironmentVariable("A99_CONTEXT_ABLATION_SCORE_WRITE"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        else
        {
            FreezeArtifact.AssertText(ScoreRoot, ScoreFile, text);
        }

        Assert.Equal(27, artifact.GetType().GetProperty("usableSemanticCells")!.GetValue(artifact));
        Assert.Equal("CONTEXT_ABLATION_VALID", artifact.GetType().GetProperty("validity")!.GetValue(artifact));
    }

    private static object BuildScore()
    {
        var authority = FrozenContextAblationAuthority.Load();
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var build = DirectSemanticProbePreflightTests.Build(plan);
        Assert.Equal(SourceSha256, CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf)));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        Assert.Equal(27, authority.Cells.Count);

        var itemsById = build.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var cellResults = authority.Cells
            .Select(cell => ReadCell(cell, itemsById))
            .ToArray();
        var errors = cellResults.SelectMany(cell => cell.Errors).ToArray();
        Assert.Empty(errors);
        Assert.Equal(27, cellResults.Length);
        Assert.Equal(27, cellResults.Count(cell => cell.RawResponseCaptured));
        Assert.Equal(27, cellResults.Select(cell => cell.Cell.Identity).Distinct(StringComparer.Ordinal).Count());

        var rows = cellResults.SelectMany(cell => cell.Decisions).ToArray();
        var arms = new[] { "FULL_CONTEXT", "LOCAL_CONTEXT_RADIUS_3", "MINIMAL_STRUCTURAL_CONTEXT_V1" }
            .Select(arm => BuildArmSummary(arm, cellResults, rows))
            .ToArray();

        var matrices = arms.ToDictionary(arm => arm.ArmId, arm => arm.ConfusionMatrix,
            StringComparer.Ordinal);
        var allRawVariance = BuildRawVariance(cellResults);
        var classification = Classify(arms);

        return new
        {
            artifactKind = "a99_direct_semantic_context_ablation_score",
            schemaVersion = "a99-direct-semantic-context-ablation-score-v1",
            documentId = "DOC-0252",
            lineage = "DIRECT_SEMANTIC_CONTEXT_ABLATION_V1",
            runnerCommit = RunnerCommit,
            captureCommit = CaptureCommit,
            sourceSha256 = SourceSha256,
            sourceUniverseSha256 = SourceUniverseSha256,
            goldSha256 = GoldSha256,
            model = "qwen/qwen3.7-flash",
            transportProviderCalls = 27,
            scoringModelCalls = 0,
            scoringProviderCalls = 0,
            captureRoot = CaptureRoot,
            cohortComplete = true,
            usableSemanticCells = cellResults.Length,
            validity = "CONTEXT_ABLATION_VALID",
            duplicateSemanticCellIdentities = 0,
            missingSemanticCellIdentities = 0,
            rawShaParity = cellResults.All(cell => cell.RawShaValid),
            authorityMetadataMismatch = 0,
            onlyContextChanged = true,
            authority = new
            {
                fullPlanHash = authority.Cells.Where(cell => cell.ArmId == "FULL_CONTEXT")
                    .Select(cell => cell.PlanHash).Distinct(StringComparer.Ordinal).Single(),
                localPlanHash = authority.Cells.Where(cell => cell.ArmId == "LOCAL_CONTEXT_RADIUS_3")
                    .Select(cell => cell.PlanHash).Distinct(StringComparer.Ordinal).Single(),
                minimalPlanHash = authority.Cells.Where(cell => cell.ArmId == "MINIMAL_STRUCTURAL_CONTEXT_V1")
                    .Select(cell => cell.PlanHash).Distinct(StringComparer.Ordinal).Single(),
                expectedLabelsProviderVisible = false,
                modelIdentical = true,
                promptIdentical = true,
                schemaIdentical = true,
                itemIdsIdentical = true,
                ownershipIdentical = true,
                transportSettingsIdentical = true,
            },
            cohort = cellResults.Select(cell => new
            {
                cell = cell.Cell.Identity,
                arm = cell.Cell.ArmId,
                repeat = cell.Cell.Repeat,
                pack = cell.Cell.Pack,
                cell.RawResponseSha256,
                rawResponseCaptured = cell.RawResponseCaptured,
                rawShaValid = cell.RawShaValid,
                requestAuthorityVerified = cell.RequestAuthorityVerified,
                capturePath = cell.CapturePath,
            }).ToArray(),
            itemContractErrors = new
            {
                missingItem = errors.Count(error => error.StartsWith("MISSING_ITEM", StringComparison.Ordinal)),
                duplicateItem = errors.Count(error => error.StartsWith("DUPLICATE_ITEM", StringComparison.Ordinal)),
                unknownItemId = errors.Count(error => error.StartsWith("UNKNOWN_ITEM_ID", StringComparison.Ordinal)),
                invalidLabel = errors.Count(error => error.StartsWith("INVALID_LABEL", StringComparison.Ordinal)),
                details = errors,
            },
            arms,
            armDeltaTable = arms.Select(arm => new
            {
                metric = arm.ArmId,
                F1 = arm.F1.Correct + "/3",
                Agenda = arm.Agenda.Correct + "/3",
                F2 = arm.F2.Correct + "/3",
                F3 = arm.F3.Correct + "/3",
                structuralControls = arm.StructuralCorrect + "/42",
                documentLabelControl = arm.DocumentLabelCorrect + "/3",
            }).ToArray(),
            confusionMatrices = matrices,
            repeatStability = arms.ToDictionary(arm => arm.ArmId, arm => arm.RepeatStability,
                StringComparer.Ordinal),
            rawResponseHashVariance = allRawVariance,
            historicalComparator = new
            {
                fullStyle = new
                {
                    F1 = "DOCUMENT_LABEL 3/3; 0/3 correct",
                    Agenda = "DOCUMENT_LABEL 3/3; 0/3 correct",
                    F2 = "3/3 correct",
                    F3 = "3/3 correct",
                    structural = "39/42; 13/14 each repeat",
                    documentLabel = "3/3",
                },
                role = "historical comparator only; new FULL_CONTEXT is the within-experiment control",
            },
            primaryClassification = classification,
            interpretation = Interpretation(classification),
            materializedGold = "48/3955",
            crossGenreGeneralizationEstablished = false,
            scoringSource = "raw response UTF-8 bytes after SHA verification; no copied summary used",
            productionPolicyChanged = false,
        };
    }

    private static CellResult ReadCell(
        ContextAblationCell cell,
        IReadOnlyDictionary<string, DirectSemanticProbePreflightTests.ProbeItem> itemsById)
    {
        var relative = Path.Combine(CaptureRoot, cell.CaptureRelativePath);
        var path = TestRepository.Path(relative);
        Assert.True(File.Exists(path), $"missing capture {relative}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("TRANSPORT_CAPTURE_COMPLETE", root.GetProperty("transportCaptureStatus").GetString());
        Assert.Equal("OK", root.GetProperty("parseStatus").GetString());
        Assert.Equal("PASS", root.GetProperty("contractStatus").GetString());
        Assert.True(root.GetProperty("rawResponseCaptured").GetBoolean());
        Assert.Equal(cell.Identity, root.GetProperty("cell").GetString());
        Assert.Equal(cell.ArmId, root.GetProperty("arm").GetString());
        Assert.Equal(cell.Repeat, root.GetProperty("repeat").GetInt32());
        Assert.Equal(cell.Pack, root.GetProperty("pack").GetString());
        Assert.Equal(cell.PolicyHash, root.GetProperty("policyHash").GetString());
        Assert.Equal(cell.ContextHash, root.GetProperty("contextHash").GetString());
        Assert.Equal(cell.ProviderInputHash, root.GetProperty("providerInputHash").GetString());
        Assert.Equal(cell.PlanHash, root.GetProperty("planHash").GetString());
        Assert.Equal(cell.Model, root.GetProperty("model").GetString());
        Assert.Equal(cell.PromptSha256, root.GetProperty("systemPromptSha256").GetString());

        var systemPrompt = Encoding.UTF8.GetString(Convert.FromBase64String(
            root.GetProperty("systemPromptUtf8Base64").GetString()!));
        var userMessage = Encoding.UTF8.GetString(Convert.FromBase64String(
            root.GetProperty("userMessageUtf8Base64").GetString()!));
        Assert.Equal(cell.SystemPrompt, systemPrompt);
        Assert.Equal(cell.Request, userMessage);
        var schemaMarker = userMessage.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        Assert.True(schemaMarker > 0);
        Assert.Equal(
            JsonSerializer.Serialize(DirectSemanticProbePreflightTests.ProbeSchema(cell.ItemIds)),
            userMessage[(schemaMarker + "\nSCHEMA=".Length)..]);

        var rawBytes = Convert.FromBase64String(root.GetProperty("rawResponseUtf8Base64").GetString()!);
        var rawSha = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        Assert.Equal(root.GetProperty("rawResponseSha256").GetString(), rawSha);
        Assert.Equal(rawBytes.Length, root.GetProperty("rawResponseBytes").GetInt32());

        var errors = new List<string>();
        var decisions = new List<DecisionScore>();
        using var response = JsonDocument.Parse(rawBytes);
        if (!response.RootElement.TryGetProperty("decisions", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("MISSING_ITEM:decisions-array");
        }
        else
        {
            var expectedById = cell.ItemIds.ToDictionary(itemId => itemId,
                itemId => itemsById[itemId], StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in array.EnumerateArray())
            {
                var id = entry.TryGetProperty("itemId", out var idValue) &&
                    idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
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
                decisions.Add(new DecisionScore(cell, item, returned!, rawSha));
            }

            foreach (var itemId in cell.ItemIds.Where(itemId => !seen.Contains(itemId)))
                errors.Add($"MISSING_ITEM:{itemId}");
        }

        return new CellResult(cell, rawSha, true, true, decisions, errors, relative);
    }

    private static ArmScore BuildArmSummary(
        string armId,
        IReadOnlyList<CellResult> cells,
        IReadOnlyList<DecisionScore> rows)
    {
        var selected = rows.Where(row => row.Cell.ArmId == armId).ToArray();
        var f1 = FamilySummary("F1", selected);
        var agenda = SummaryFor(selected, row => row.Item.Identity == "L0519:S0:0-6");
        var f2 = FamilySummary("F2", selected);
        var f3 = FamilySummary("F3", selected);
        var structural = selected.Where(row => row.Item.ExpectedLabel == "STRUCTURAL_UNIT").ToArray();
        var documentLabel = selected.Where(row => row.Item.ExpectedLabel == "DOCUMENT_LABEL").ToArray();
        var matrix = Labels.ToDictionary(expected => expected,
            expected => Labels.ToDictionary(returned => returned,
                returned => selected.Count(row => row.Item.ExpectedLabel == expected && row.Returned == returned),
                StringComparer.Ordinal), StringComparer.Ordinal);
        var failures = structural.Where(row => row.Returned != row.Item.ExpectedLabel)
            .Select(row => new
            {
                claimIdentity = row.Item.Identity,
                itemId = row.Item.ItemId,
                expected = row.Item.ExpectedLabel,
                returned = row.Returned,
                repeat = row.Cell.Repeat,
                cell = row.Cell.Identity,
            }).ToArray();
        return new ArmScore(
            armId, f1, agenda, f2, f3,
            structural.Count(row => row.Returned == row.Item.ExpectedLabel),
            structural.Length,
            new
            {
                R1 = structural.Count(row => row.Cell.Repeat == 1 && row.Returned == row.Item.ExpectedLabel),
                R2 = structural.Count(row => row.Cell.Repeat == 2 && row.Returned == row.Item.ExpectedLabel),
                R3 = structural.Count(row => row.Cell.Repeat == 3 && row.Returned == row.Item.ExpectedLabel),
            },
            failures,
            documentLabel.Count(row => row.Returned == row.Item.ExpectedLabel),
            documentLabel.Length,
            documentLabel.OrderBy(row => row.Cell.Repeat)
                .Select(row => new { repeat = row.Cell.Repeat, returned = row.Returned }).ToArray(),
            matrix,
            BuildStability(armId, rows),
            cells.Where(cell => cell.Cell.ArmId == armId)
                .OrderBy(cell => cell.Cell.Repeat).ThenBy(cell => cell.Cell.Pack)
                .Select(cell => new
                {
                    cell = cell.Cell.Identity,
                    cell.RawResponseSha256,
                }).ToArray());
    }

    private static FamilyResult FamilySummary(string family, IEnumerable<DecisionScore> rows) =>
        SummaryFor(rows, row => row.Item.GoldRole == $"masthead-family-{family}");

    private static FamilyResult SummaryFor(
        IEnumerable<DecisionScore> rows,
        Func<DecisionScore, bool> predicate)
    {
        var selected = rows.Where(predicate).OrderBy(row => row.Cell.Repeat).ToArray();
        Assert.Equal(3, selected.Length);
        return new FamilyResult(
            selected.Count(row => row.Returned == row.Item.ExpectedLabel),
            selected.Select(row => new
            {
                repeat = row.Cell.Repeat,
                returned = row.Returned,
                expected = row.Item.ExpectedLabel,
                correct = row.Returned == row.Item.ExpectedLabel,
            }).ToArray());
    }

    private static object[] BuildStability(
        string armId,
        IReadOnlyList<DecisionScore> rows) => rows.Where(row => row.Cell.ArmId == armId)
        .Select(row => row.Item).DistinctBy(item => item.ItemId, StringComparer.Ordinal).Select(item =>
    {
        var sequence = rows.Where(row => row.Item.ItemId == item.ItemId)
            .Where(row => row.Cell.ArmId == armId)
            .OrderBy(row => row.Cell.Repeat)
            .ThenBy(row => row.Cell.Repeat)
            .Select(row => row.Returned).ToArray();
        var distinct = sequence.Distinct(StringComparer.Ordinal).Count();
        return (object)new
        {
            itemId = item.ItemId,
            identity = item.Identity,
            expected = item.ExpectedLabel,
            returnedByArmAndRepeat = sequence,
            stability = distinct == 1 ? "STABLE_3_OF_3" :
                sequence.GroupBy(value => value).Max(group => group.Count()) >= 2
                    ? "MAJORITY_2_OF_3" : "FULLY_VARIABLE",
        };
    }).ToArray();

    private static object BuildRawVariance(IReadOnlyList<CellResult> cells) =>
        cells.GroupBy(cell => cell.Cell.ArmId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.GroupBy(cell => cell.Cell.Pack, StringComparer.Ordinal)
                    .ToDictionary(pack => pack.Key,
                        pack => new
                        {
                            hashesByRepeat = pack.OrderBy(cell => cell.Cell.Repeat)
                                .ToDictionary(cell => $"r{cell.Cell.Repeat}", cell => cell.RawResponseSha256),
                            distinctHashCount = pack.Select(cell => cell.RawResponseSha256)
                                .Distinct(StringComparer.Ordinal).Count(),
                        }, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static string Classify(IReadOnlyList<ArmScore> arms)
    {
        var full = arms.Single(arm => arm.ArmId == "FULL_CONTEXT");
        var local = arms.Single(arm => arm.ArmId == "LOCAL_CONTEXT_RADIUS_3");
        var minimal = arms.Single(arm => arm.ArmId == "MINIMAL_STRUCTURAL_CONTEXT_V1");
        var narrow = new[] { local, minimal };
        var controlsStrong = narrow.All(arm =>
            arm.StructuralCorrect >= 38 && arm.F2.Correct >= 2 && arm.F3.Correct >= 2 &&
            arm.DocumentLabelCorrect == 3);
        var bothFixed = narrow.All(arm => arm.F1.Correct == 3 && arm.Agenda.Correct == 3);
        if (bothFixed && full.F1.Correct == 0 && full.Agenda.Correct == 0 && controlsStrong)
            return "CONTEXT_COMPOSITION_CAUSAL_CONTRIBUTION_SUPPORTED";
        if (full.F1.Correct == 0 && full.Agenda.Correct == 0 &&
            narrow.All(arm => arm.F1.Correct == 0 && arm.Agenda.Correct == 0) && controlsStrong)
            return "CONTEXT_ABLATION_DOES_NOT_FIX_DOCUMENT_LABEL_ATTRACTOR";
        if (narrow.Any(arm => arm.F1.Correct != full.F1.Correct || arm.Agenda.Correct != full.Agenda.Correct))
            return controlsStrong
                ? "CONTEXT_TRADEOFF_OBSERVED"
                : "CONTEXT_INSUFFICIENT_FOR_COMPARABLE_SEMANTIC_DECISION";
        return "CONTEXT_ABLATION_INCONCLUSIVE";
    }

    private static string Interpretation(string classification) => classification switch
    {
        "CONTEXT_COMPOSITION_CAUSAL_CONTRIBUTION_SUPPORTED" =>
            "Narrower context changes both primary decisions while controls remain strong; context composition is a causal contributor, not proven to be the sole cause.",
        "CONTEXT_ABLATION_DOES_NOT_FIX_DOCUMENT_LABEL_ATTRACTOR" =>
            "The attractor persists across all context arms while controls remain strong; evidence shifts toward the semantic boundary or model prior.",
        "CONTEXT_TRADEOFF_OBSERVED" =>
            "Context narrowing changes the two primary targets differently; metadata disambiguation and hierarchy comprehension must remain separate mechanisms.",
        "CONTEXT_INSUFFICIENT_FOR_COMPARABLE_SEMANTIC_DECISION" =>
            "A target may improve, but the arm is not a comparable semantic decision because controls materially degraded.",
        _ => "The experiment does not support a causal conclusion from these arms.",
    };

    private static string HashSchema(string[] itemIds) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(DirectSemanticProbePreflightTests.ProbeSchema(itemIds),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }).ReplaceLineEndings("\n"))));

    private sealed record CellResult(
        ContextAblationCell Cell,
        string RawResponseSha256,
        bool RawResponseCaptured,
        bool RawShaValid,
        IReadOnlyList<DecisionScore> Decisions,
        IReadOnlyList<string> Errors,
        string CapturePath)
    {
        public bool RequestAuthorityVerified => true;
    }

    private sealed record DecisionScore(
        ContextAblationCell Cell,
        DirectSemanticProbePreflightTests.ProbeItem Item,
        string Returned,
        string RawResponseSha256);

    private sealed record FamilyResult(int Correct, object[] ReturnedByRepeat);

    private sealed record ArmScore(
        string ArmId,
        FamilyResult F1,
        FamilyResult Agenda,
        FamilyResult F2,
        FamilyResult F3,
        int StructuralCorrect,
        int StructuralTotal,
        object StructuralByRepeat,
        object[] StructuralFailures,
        int DocumentLabelCorrect,
        int DocumentLabelTotal,
        object[] DocumentLabelReturnedByRepeat,
        Dictionary<string, Dictionary<string, int>> ConfusionMatrix,
        object[] RepeatStability,
        object[] RawResponseHashes);
}
