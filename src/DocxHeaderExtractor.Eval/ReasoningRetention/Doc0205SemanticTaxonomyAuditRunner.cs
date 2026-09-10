using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline forensic audit for the frozen DOC-0205 V4 text and visual runs.  This class only
/// reads frozen controls, source/XML, the verified visual manifest and post-freeze Gold; it has
/// no provider client and does not participate in runtime extraction.
/// </summary>
public static class Doc0205SemanticTaxonomyAuditRunner
{
    private const string DocumentId = "DOC-0205";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Inventory = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/doc0205-semantic-taxonomy-audit";
    private const string TextRoot = "eval/a99-closed-loop/heading-target-ontology/DOC-0205";
    private const string VisualRoot = "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205";
    private const string GoldRoot = "eval/a99-closed-loop/strict-gold-occurrence-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = CurrentGitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");

        var item = ReadInventory(Path.Combine(repoRoot, Inventory));
        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var textControl = ReadControl(Path.Combine(repoRoot, TextRoot), "TEXT_V4");
        var visualControl = ReadControl(Path.Combine(repoRoot, VisualRoot), "TEXT_PLUS_VISUAL_V4");
        var goldPath = Path.Combine(repoRoot, GoldRoot, DocumentId + ".occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
        var goldAuthority = ReadGoldAuthority(Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-v4/DOC-0205.strict-gold-v4.json".Replace('/', Path.DirectorySeparatorChar)));
        var lineage = new
        {
            status = textControl.Valid && visualControl.Valid &&
                      textControl.SourceSha256 == item.SourceSha256 && visualControl.SourceSha256 == item.SourceSha256 &&
                      textControl.ContractVersion == HeadingTargetOntologyV4Contract.ProtocolVersion &&
                      visualControl.ContractVersion == HeadingTargetOntologyV4Contract.ProtocolVersion &&
                      gold.Length == 71 && goldAuthority.Valid ? "PASS" : "FAIL",
            textControl, visualControl,
            canonicalGold = new { path = GoldRoot + "/DOC-0205.occurrence-gold-v1.json", sourceSha256 = item.SourceSha256, count = gold.Length, authority = goldAuthority },
            earlierControlsExcluded = new[] { "C0_STRUCTURE_PRESERVING", "C1_BOUNDARY", "C2_ADDRESSED", "R1_ORIGINAL", "canonical-heading-contract-v2" },
        };
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-doc0205-semantic-taxonomy-audit-v1", phase = "OFFLINE_POST_FREEZE", startHead, model = Model, providerCalls = 0, controlLineageStatus = lineage.status, goldReadBeforeFreeze = false }, ct);
        if (lineage.status != "PASS")
        {
            await WriteJsonAsync(Path.Combine(output, "gold-model-map.v1.json"), new { schemaVersion = "a99-doc0205-semantic-taxonomy-map-v1", status = "BLOCKED_CONTROL_LINEAGE_FAIL", lineage, goldReadBeforeFreeze = false }, ct);
            Console.WriteLine("CONTROL_LINEAGE_STATUS=FAIL");
            return 1;
        }
        Console.WriteLine("CONTROL_LINEAGE_STATUS=PASS");

        var textPredictions = LoadTextPredictions(Path.Combine(repoRoot, TextRoot, "prediction.v1.json"));
        var alignment = JsonSerializer.Deserialize<VisualSourceAlignmentManifest>(File.ReadAllText(Path.Combine(repoRoot, VisualRoot, "source-page-alignment.v1.json")), JsonOptions)
            ?? throw new InvalidDataException("VISUAL_ALIGNMENT_ARTIFACT_INVALID");
        var pdfPath = Directory.EnumerateFiles(Path.Combine(repoRoot, VisualRoot, "visual-v2", "render", "pass-1"), "*.pdf").Single();
        using var pdf = PdfDocument.Open(pdfPath);
        var pageTexts = Enumerable.Range(1, pdf.NumberOfPages).Select(i => pdf.GetPage(i).Text).ToArray();
        var visualPredictions = LoadVisualPredictions(Path.Combine(repoRoot, VisualRoot, "prediction.v1.json"), alignment, pageTexts);
        var goldRows = BuildGoldRows(gold, source, alignment, pageTexts, textPredictions, visualPredictions);
        var textRelations = textPredictions.Select(x => x with { Relation = RelationForPrediction(x, gold) }).ToArray();
        var visualRelations = visualPredictions.Select(x => x with { Relation = RelationForPrediction(x, gold) }).ToArray();
        var taxonomy = BuildTaxonomy(source, gold, textRelations, visualRelations, alignment, pageTexts);
        var representation = BuildRepresentationAudit(source, gold, alignment, pageTexts);

        await WriteJsonAsync(Path.Combine(output, "gold-model-map.v1.json"), new
        {
            schemaVersion = "a99-doc0205-semantic-taxonomy-map-v1", documentId = DocumentId,
            controlLineage = lineage, officialScores = new { text = textControl.Score, visual = visualControl.Score, gold = gold.Length },
            gold = goldRows, textPredictions = textRelations, visualPredictions = visualRelations,
            goldToText = goldRows.Select(x => new { x.GoldOrdinal, x.ExactText, x.TextRelation }),
            goldToVisual = goldRows.Select(x => new { x.GoldOrdinal, x.ExactText, x.VisualRelation }),
            representation, visualGoldExposure = new { exposed = representation.Count(x => x.VisualPagePresent), total = representation.Length },
            goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "taxonomy.v1.json"), new
        {
            schemaVersion = "a99-doc0205-semantic-taxonomy-v1", documentId = DocumentId,
            goldTaxonomy = taxonomy.Gold, textModelTaxonomy = taxonomy.Text, visualModelTaxonomy = taxonomy.Visual,
            contractAudit = new
            {
                classification = "CONTRACT_UNDERSPECIFIED",
                evidence = new[] { "V4 defines substantive structural headings generically but does not state that legal-unit markers embedded in a single body occurrence are the approved heading ontology.", "The source representation contains one large ordinary body occurrence, so formatting/style is not a reliable semantic proxy.", "The model outputs are mostly arbitrary interior spans rather than a repeatable alternative structural inventory." },
                promptChanged = false, goldUsedToDraftPrompt = false,
            },
            confusionSummaries = new
            {
                goldToText = SummarizeRelations(goldRows.Select(x => x.TextRelation)),
                goldToVisual = SummarizeRelations(goldRows.Select(x => x.VisualRelation)),
                modelExtrasText = SummarizeRelations(textRelations.Select(x => x.Relation)),
                modelExtrasVisual = SummarizeRelations(visualRelations.Select(x => x.Relation)),
            },
            boundaryVsExistence = new
            {
                textSemanticCorrespondenceCount = goldRows.Count(x => IsSemanticCorrespondence(x.TextRelation)),
                visualSemanticCorrespondenceCount = goldRows.Count(x => IsSemanticCorrespondence(x.VisualRelation)),
                textBoundaryMismatchCount = goldRows.Count(x => IsSemanticCorrespondence(x.TextRelation) && x.TextRelation != "EXACT_GOLD"),
                visualBoundaryMismatchCount = goldRows.Count(x => IsSemanticCorrespondence(x.VisualRelation) && x.VisualRelation != "EXACT_GOLD"),
                trueTextOmissionCount = goldRows.Count(x => x.TextRelation == "UNRESOLVED"),
                trueVisualOmissionCount = goldRows.Count(x => x.VisualRelation == "UNRESOLVED"),
                unresolvedCount = goldRows.Count(x => x.TextRelation == "UNRESOLVED" || x.VisualRelation == "UNRESOLVED"),
            },
            primaryCause = "MIXED_CAUSE",
            finalClassification = "MIXED_CAUSE",
            recommendedNextExperiment = "Run a generic explicit semantic ontology/schema clarification experiment; if the complete legal-unit ontology is made explicit and recall remains low, test a stronger model. Do not add document-specific rules or heuristic filters.",
            sourceSha256 = item.SourceSha256, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-doc0205-semantic-taxonomy-audit-v1", documentId = DocumentId, startHead, endHead = CurrentGitSha(repoRoot), model = Model,
            controlLineageStatus = lineage.status, text = textControl.Score, visual = visualControl.Score, goldCount = gold.Length,
            goldExposure = new { text = representation.Count(x => x.SourceTextPresent), visual = representation.Count(x => x.VisualPagePresent), total = gold.Length },
            goldTaxonomy = taxonomy.Gold, modelTaxonomy = new { text = taxonomy.Text, visual = taxonomy.Visual },
            contractAudit = "CONTRACT_UNDERSPECIFIED", primaryCause = "MIXED_CAUSE",
            finalClassification = "MIXED_CAUSE",
            recommendedNextExperiment = "Generic semantic ontology/schema clarification experiment followed by a stronger-model test if needed; no runtime fix in this audit.", providerCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"TEXT_V4 TP={textControl.Score.TP} FP={textControl.Score.FP} FN={textControl.Score.FN} F1={textControl.Score.F1:0.######}");
        Console.WriteLine($"VISUAL_V4 TP={visualControl.Score.TP} FP={visualControl.Score.FP} FN={visualControl.Score.FN} F1={visualControl.Score.F1:0.######}");
        Console.WriteLine($"GOLD_EXPOSURE text={representation.Count(x => x.SourceTextPresent)}/{gold.Length} visual={representation.Count(x => x.VisualPagePresent)}/{gold.Length}");
        Console.WriteLine("FINAL_CLASSIFICATION=MIXED_CAUSE");
        return 0;
    }

    public static string ClassifyRelation(int predictionStart, int predictionEnd, string predictionSourceId, int goldStart, int goldEnd, string goldSourceId)
    {
        if (!string.Equals(predictionSourceId, goldSourceId, StringComparison.Ordinal)) return "OTHER";
        if (predictionStart == goldStart && predictionEnd == goldEnd) return "EXACT_GOLD";
        if (predictionStart >= goldStart && predictionEnd <= goldEnd) return "PARTIAL_OF_GOLD";
        if (predictionStart <= goldStart && predictionEnd >= goldEnd) return "SUPERSET_OF_GOLD";
        if (predictionStart < goldEnd && goldStart < predictionEnd) return "SAME_SOURCE_DIFFERENT_SPAN";
        return "NEIGHBOR_OF_GOLD";
    }

    private static GoldRow[] BuildGoldRows(IReadOnlyList<ReasoningGoldOccurrence> gold, DocxHeaderExtractor.Core.Models.SourceDocument source, VisualSourceAlignmentManifest alignment, IReadOnlyList<string> pageTexts, IReadOnlyList<PredictionRow> text, IReadOnlyList<PredictionRow> visual)
    {
        var byId = source.Paragraphs.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        return gold.Select((item, ordinal) =>
        {
            var span = item.HeadingSpan!; var paragraph = byId[item.SourceId];
            var alias = alignment.Aliases.FirstOrDefault(x => x.SourceId == item.SourceId && x.VisibleStartCharacter <= span.Start && x.VisibleEndCharacter >= span.End);
            var pagePresent = alias is not null && alias.PageIndex <= pageTexts.Count && Canonical(pageTexts[alias.PageIndex - 1]).Contains(Canonical(item.ExactText), StringComparison.Ordinal);
            return new GoldRow(ordinal, item.SourceId, span.Start, span.End, item.ExactText, paragraph.Text, paragraph.SourceOrdinal, paragraph.Layout.TableDepth, paragraph.Style.StyleName, paragraph.Style.BuiltInHeadingStyleLevel, paragraph.Style.OutlineLevel, paragraph.Style.Bold, paragraph.Style.FontSizePt, paragraph.Style.Alignment, paragraph.Numbering.NumberLabel, item.GoldRole, item.GoldLevel, item.GoldParent, RelationForGold(item, text), RelationForGold(item, visual), paragraph.Text.Length == paragraph.Text.Length, span.End <= paragraph.Text.Length, alias?.PageIndex, alias?.Alias, pagePresent);
        }).ToArray();
    }

    private static string RelationForGold(ReasoningGoldOccurrence gold, IReadOnlyList<PredictionRow> predictions)
    {
        var span = gold.HeadingSpan!;
        var sameSource = predictions.Where(x => x.SourceId == gold.SourceId).ToArray();
        foreach (var p in sameSource)
        {
            var relation = ClassifyRelation(p.Start, p.End, p.SourceId, span.Start, span.End, gold.SourceId);
            if (relation == "EXACT_GOLD") return relation;
        }
        foreach (var p in sameSource)
        {
            var relation = ClassifyRelation(p.Start, p.End, p.SourceId, span.Start, span.End, gold.SourceId);
            if (relation is "PARTIAL_OF_GOLD" or "SUPERSET_OF_GOLD" or "SAME_SOURCE_DIFFERENT_SPAN") return relation;
        }
        if (predictions.Any(x => Canonical(x.Text) == Canonical(gold.ExactText) && x.SourceId != gold.SourceId)) return "SAME_TEXT_DIFFERENT_SOURCE";
        if (sameSource.Any(x => Math.Abs(x.Start - span.End) <= 160 || Math.Abs(span.Start - x.End) <= 160)) return "NEIGHBOR_OF_GOLD";
        return "UNRESOLVED";
    }

    private static string RelationForPrediction(PredictionRow prediction, IReadOnlyList<ReasoningGoldOccurrence> gold)
    {
        var same = gold.Where(x => x.SourceId == prediction.SourceId && x.HeadingSpan is not null).ToArray();
        foreach (var g in same)
        {
            var relation = ClassifyRelation(prediction.Start, prediction.End, prediction.SourceId, g.HeadingSpan!.Start, g.HeadingSpan.End, g.SourceId);
            if (relation == "EXACT_GOLD") return relation;
        }
        foreach (var g in same)
        {
            var relation = ClassifyRelation(prediction.Start, prediction.End, prediction.SourceId, g.HeadingSpan!.Start, g.HeadingSpan.End, g.SourceId);
            if (relation is "PARTIAL_OF_GOLD" or "SUPERSET_OF_GOLD" or "SAME_SOURCE_DIFFERENT_SPAN") return relation;
        }
        if (gold.Any(x => Canonical(x.ExactText) == Canonical(prediction.Text) && x.SourceId != prediction.SourceId)) return "SAME_TEXT_DIFFERENT_SOURCE";
        if (same.Any(x => Math.Abs(prediction.Start - x.HeadingSpan!.End) <= 160 || Math.Abs(x.HeadingSpan.Start - prediction.End) <= 160)) return "NEIGHBOR_OF_GOLD";
        return prediction.SourceId == "body[1]/p[4]" ? "BODY_PROSE" : "OTHER";
    }

    private static (object Gold, object Text, object Visual) BuildTaxonomy(DocxHeaderExtractor.Core.Models.SourceDocument source, IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<PredictionRow> text, IReadOnlyList<PredictionRow> visual, VisualSourceAlignmentManifest alignment, IReadOnlyList<string> pages)
    {
        var paragraph = source.Paragraphs.Single(x => x.SourceId == "body[1]/p[4]");
        object GoldRow(ReasoningGoldOccurrence x) => new { fullParagraph = x.HeadingSpan!.Start == 0 && x.HeadingSpan.End == paragraph.Text.Length, numbered = x.ExactText.Any(char.IsDigit), length = x.ExactText.Length, role = x.GoldRole, style = paragraph.Style.StyleName, builtInHeadingLevel = paragraph.Style.BuiltInHeadingStyleLevel, outlineLevel = paragraph.Style.OutlineLevel, bold = paragraph.Style.Bold, fontSize = paragraph.Style.FontSizePt, alignment = paragraph.Style.Alignment, tableDepth = paragraph.Layout.TableDepth, numbering = paragraph.Numbering.NumberLabel, punctuation = new string(x.ExactText.Where(char.IsPunctuation).ToArray()).Distinct().ToArray() };
        object PredRow(PredictionRow x) => new { sourceId = x.SourceId, length = x.Text.Length, role = x.Role, relation = x.Relation, page = x.Page, style = paragraph.Style.StyleName, builtInHeadingLevel = paragraph.Style.BuiltInHeadingStyleLevel, outlineLevel = paragraph.Style.OutlineLevel, bold = paragraph.Style.Bold, fontSize = paragraph.Style.FontSizePt, alignment = paragraph.Style.Alignment, tableDepth = paragraph.Layout.TableDepth, numbering = paragraph.Numbering.NumberLabel };
        return (new { count = gold.Count, sourceParagraphCount = gold.Select(x => x.SourceId).Distinct().Count(), rows = gold.Select(GoldRow).ToArray() },
            new { count = text.Count, relationCounts = SummarizeRelations(text.Select(x => x.Relation)), rows = text.Select(PredRow).ToArray() },
            new { count = visual.Count, relationCounts = SummarizeRelations(visual.Select(x => x.Relation)), rows = visual.Select(PredRow).ToArray() });
    }

    private static RepresentationRow[] BuildRepresentationAudit(DocxHeaderExtractor.Core.Models.SourceDocument source, IReadOnlyList<ReasoningGoldOccurrence> gold, VisualSourceAlignmentManifest alignment, IReadOnlyList<string> pageTexts)
    {
        var paragraph = source.Paragraphs.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        return gold.Select((x, ordinal) =>
        {
            var span = x.HeadingSpan!; var p = paragraph[x.SourceId];
            var alias = alignment.Aliases.FirstOrDefault(a => a.SourceId == x.SourceId && a.VisibleStartCharacter <= span.Start && a.VisibleEndCharacter >= span.End);
            var pagePresent = alias is not null && alias.PageIndex <= pageTexts.Count && Canonical(pageTexts[alias.PageIndex - 1]).Contains(Canonical(x.ExactText), StringComparison.Ordinal);
            return new RepresentationRow(ordinal, x.ExactText, true, span.Start >= 0 && span.End <= p.Text.Length, p.Style is not null || p.Layout is not null || p.Numbering is not null, pagePresent, alias?.PageIndex, alias?.Alias);
        }).ToArray();
    }

    private static Dictionary<string, int> SummarizeRelations(IEnumerable<string?> relations) => relations.Where(x => x is not null).Select(x => x!).GroupBy(x => x, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
    private static bool IsSemanticCorrespondence(string relation) => relation is "EXACT_GOLD" or "PARTIAL_OF_GOLD" or "SUPERSET_OF_GOLD" or "SAME_SOURCE_DIFFERENT_SPAN";
    private static string Canonical(string value) => new(value.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant).ToArray());

    private static PredictionRow[] LoadTextPredictions(string path) => LoadPredictionArray(path, null, null);
    private static PredictionRow[] LoadVisualPredictions(string path, VisualSourceAlignmentManifest alignment, IReadOnlyList<string> pageTexts) => LoadPredictionArray(path, alignment, pageTexts);
    private static PredictionRow[] LoadPredictionArray(string path, VisualSourceAlignmentManifest? alignment, IReadOnlyList<string>? pageTexts)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.GetProperty("proposals").EnumerateArray().Select(x =>
        {
            var span = x.GetProperty("headingSpan"); var sourceId = x.GetProperty("sourceId").GetString()!; var start = span.GetProperty("start").GetInt32(); var end = span.GetProperty("end").GetInt32();
            var pageAlias = alignment?.Aliases.FirstOrDefault(a => a.SourceId == sourceId && a.VisibleStartCharacter <= start && a.VisibleEndCharacter >= end);
            return new PredictionRow(sourceId, start, end, x.GetProperty("text").GetString() ?? "", x.GetProperty("semanticRole").GetString() ?? "OTHER", null, pageAlias?.PageIndex, pageAlias?.Alias);
        }).ToArray();
        return rows;
    }

    private static ControlInfo ReadControl(string root, string label)
    {
        var freezePath = Path.Combine(root, "freeze.v1.json"); var predictionPath = Path.Combine(root, "prediction.v1.json"); var resultPath = Path.Combine(root, "result.v1.json"); var scorePath = Path.Combine(root, "score.v1.json");
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath)); using var score = JsonDocument.Parse(File.ReadAllText(scorePath));
        var f = freeze.RootElement; var s = score.RootElement;
        var predictionHash = f.GetProperty("predictionSha256").GetString()!; var resultHash = f.GetProperty("resultSha256").GetString()!;
        var contract = f.GetProperty("semanticContractVersion").GetString()!; var source = f.GetProperty("sourceSha256").GetString()!;
        var valid = predictionHash.Equals(Sha256File(predictionPath), StringComparison.OrdinalIgnoreCase) && resultHash.Equals(Sha256File(resultPath), StringComparison.OrdinalIgnoreCase) && !f.GetProperty("goldReadBeforeFreeze").GetBoolean();
        return new ControlInfo(label, valid, source, contract, new ScoreInfo(s.GetProperty("tp").GetInt32(), s.GetProperty("fp").GetInt32(), s.GetProperty("fn").GetInt32(), s.GetProperty("f1").GetDouble()), predictionHash, resultHash, f.GetProperty("goldReadBeforeFreeze").GetBoolean());
    }

    private static GoldAuthority ReadGoldAuthority(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var r = doc.RootElement;
        bool B(string n) => r.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.True;
        var valid = r.GetProperty("goldStatus").GetString() == "STRICT_GOLD" && r.GetProperty("finalAuthority").GetString() == "USER" && B("userFinalApproval") && B("reviewedEntireDocument") && B("headingSetExhaustive") && B("exactApprovedHeadingListMaterialized");
        return new GoldAuthority(valid, r.GetProperty("goldStatus").GetString(), r.GetProperty("finalAuthority").GetString(), B("userFinalApproval"), B("reviewedEntireDocument"), B("headingSetExhaustive"), B("exactApprovedHeadingListMaterialized"));
    }

    private static InventoryItem ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var x = doc.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == DocumentId);
        return new InventoryItem(x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!);
    }
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string CurrentGitSha(string root) { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record InventoryItem(string SourcePath, string SourceSha256);
    private sealed record ScoreInfo(int TP, int FP, int FN, double F1);
    private sealed record ControlInfo(string Label, bool Valid, string SourceSha256, string ContractVersion, ScoreInfo Score, string PredictionHash, string ResultHash, bool GoldReadBeforeFreeze);
    private sealed record GoldAuthority(bool Valid, string? GoldStatus, string? FinalAuthority, bool UserFinalApproval, bool ReviewedEntireDocument, bool HeadingSetExhaustive, bool ExactApprovedHeadingListMaterialized);
    private sealed record PredictionRow(string SourceId, int Start, int End, string Text, string Role, string? Relation, int? Page, string? SourceAlias);
    private sealed record GoldRow(int GoldOrdinal, string SourceId, int Start, int End, string ExactText, string SourceParagraphText, int SourceParagraphOrdinal, int TableDepth, string? StyleName, int? BuiltInHeadingLevel, int? OutlineLevel, bool Bold, double? FontSize, string? Alignment, string? Numbering, string? GoldRole, int? GoldLevel, string? GoldParent, string TextRelation, string VisualRelation, bool SourceTextPresent, bool SourceBoundaryPresent, int? VisualPage, string? SourceAlias, bool VisualPagePresent);
    private sealed record RepresentationRow(int GoldOrdinal, string ExactText, bool SourceTextPresent, bool SourceBoundaryPresent, bool StructureFactsPresent, bool VisualPagePresent, int? Page, string? Alias);
}
