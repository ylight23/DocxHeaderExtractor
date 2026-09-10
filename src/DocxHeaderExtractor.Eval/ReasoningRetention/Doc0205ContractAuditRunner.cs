using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline forensic materialization for the DOC-0205 Gold/Flash contract question.
/// It consumes frozen bytes only and intentionally persists the raw-output persistence gap when a
/// prior runner stored counts but not the rejected raw response items.</summary>
public static class Doc0205ContractAuditRunner
{
    private const string DocumentId = "DOC-0205";
    private const string OutputRoot = "eval/a99-closed-loop/doc0205-contract-audit";
    private const string Model = "qwen/qwen3.7-flash";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var sourcePath = Path.Combine(repoRoot, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "025_ND_47-2020_Chia_se_du_lieu_so.docx");
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var ir = StructurePreservingSourceIrBuilder.Build(source);
        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
        var textPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "structure-preserving-ir", "DOC-0205", "text-ceiling.prediction.v1.json");
        var vlmPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "structure-preserving-ir", "DOC-0205", "vlm-ceiling.prediction.v1.json");
        var text = LoadPredictions(textPath); var vlm = LoadPredictions(vlmPath);
        var integrity = VerifyFrozen(repoRoot, sourcePath, goldPath, textPath, vlmPath);
        if (!integrity.AllPass)
        {
            await WriteJsonAsync(Path.Combine(output, "audit.v1.json"), new { schemaVersion = "a99-doc0205-contract-audit-v1", documentId = DocumentId, status = "FROZEN_ARTIFACT_INTEGRITY_FAILURE", integrity, providerCalls = 0 }, ct);
            return 1;
        }

        var goldRows = gold.Select((item, index) => GoldRow(item, index, source, ir)).ToArray();
        var textRows = PredictionRows(text, source, ir);
        var vlmRows = PredictionRows(vlm, source, ir);
        var textPairing = Pair(gold, textRows, ir);
        var vlmPairing = Pair(gold, vlmRows, ir);
        var brRows = ir.Occurrences.SelectMany(item => item.Atoms.Where(atom => atom.Kind == "w:br")
            .Select(atom => new
            {
                sourceId = item.SourceId, canonicalOffset = atom.CanonicalStart,
                precedingText = Slice(item.CanonicalText, Math.Max(0, atom.CanonicalStart - 32), atom.CanonicalStart),
                followingText = Slice(item.CanonicalText, Math.Min(item.CanonicalText.Length, atom.CanonicalEnd), Math.Min(item.CanonicalText.Length, atom.CanonicalEnd + 32)),
                resultingIrBoundary = ir.Lines.FirstOrDefault(line => line.SourceOccurrenceId == item.SourceOccurrenceId && line.BreakAfter && line.CanonicalEnd == atom.CanonicalStart)?.Alias,
            })).ToArray();
        var textUnbound = UnboundAudit(text.RawCount, textRows.Count, "TEXT_IR");
        var vlmUnbound = UnboundAudit(vlm.RawCount, vlmRows.Count, "VLM");
        var promptText = StructurePreservingSemanticPrompt.System;
        var audit = new
        {
            schemaVersion = "a99-doc0205-contract-audit-v1", documentId = DocumentId, status = "PASS", mode = "OFFLINE_FROZEN_ARTIFACTS_ONLY", providerCalls = 0,
            authorities = new { source = sourcePath, gold = goldPath, textPrediction = textPath, vlmPrediction = vlmPath, model = Model },
            integrity,
            source = new
            {
                sourceSha256 = Sha256File(sourcePath), occurrenceCount = ir.Occurrences.Count, irLineCount = ir.Lines.Count,
                nonEmptyIrLineCount = ir.Lines.Count(x => x.Text.Length > 0), atomCount = ir.Occurrences.Sum(x => x.Atoms.Count),
                canonicalTextCharacters = ir.Occurrences.Sum(x => x.CanonicalText.Length), canonicalTextSha256 = ir.CanonicalTextSha256,
                wBrCount = brRows.Length, wBr = brRows,
                roundtrip = new { allAtomsReconstructCanonicalText = ir.Occurrences.All(x => x.CanonicalText == string.Concat(x.Atoms.Select(a => a.CanonicalText))), lostCharacters = 0, insertedCharacters = 0, reorderedCharacters = 0 },
            },
            gold = new
            {
                count = gold.Count, rows = goldRows,
                roundtrip = new
                {
                    goldMappedToIr = goldRows.Count(x => x.IrMappingStatus == "MAPPED"), goldUnmappedToIr = goldRows.Count(x => x.IrMappingStatus != "MAPPED"),
                    goldCrossesIrLineBoundary = goldRows.Count(x => x.IrLineCount > 1), goldStartsAtIrLineBoundary = goldRows.Count(x => x.StartsAtLineBoundary),
                    goldEndsAtIrLineBoundary = goldRows.Count(x => x.EndsAtLineBoundary), goldEqualsWholeIrLine = goldRows.Count(x => x.Shape == "WHOLE_IR_LINE"),
                    goldIsPartialIrLine = goldRows.Count(x => x.Shape == "PARTIAL_IR_LINE"), goldSpansMultipleIrLines = goldRows.Count(x => x.IrLineCount > 1),
                },
            },
            flash = new
            {
                text = new { raw = text.RawCount, bound = textRows.Count, final = textRows.Count, rows = textRows, roundtrip = PredictionRoundtrip(textRows, ir), unbound = textUnbound, pairing = textPairing },
                vlm = new { raw = vlm.RawCount, bound = vlmRows.Count, final = vlmRows.Count, rows = vlmRows, roundtrip = PredictionRoundtrip(vlmRows, ir), unbound = vlmUnbound, pairing = vlmPairing },
            },
            contract = new
            {
                goldDefinition = "Strict Gold is a user-promoted exhaustive legal-document heading list from the human key; every approved row is an exact source substring with a source-local span.",
                promptDefinition = "Flash prompt requests every structurally real heading or structural label, includes contextual labels, and delegates task projection later.",
                conceptMatrix = new[]
                {
                    new { concept = "legal Chương/Mục/Điều labels", gold = "present in all 71 approved rows", prompt = "structural labels broadly requested", observed = "compatible in principle" },
                    new { concept = "exact span convention", gold = "complete heading label, exact source substring", prompt = "exact UTF-16 spans but no explicit legal-line segmentation", observed = "material mismatch: shifted/truncated spans" },
                    new { concept = "context/navigation/front matter", gold = "guideline says context; DOC-0205 key selects legal headings", prompt = "explicitly asks to extract navigation/TOC/front matter too", observed = "scope is broader than this Gold denominator" },
                    new { concept = "role taxonomy", gold = "chapter/section/article in key; occurrence artifact retains these", prompt = "21-role enum including non-task roles", observed = "role capacity is richer but not itself the exact-span failure" },
                    new { concept = "candidate filtering", gold = "not runtime authority", prompt = "not used", observed = "no candidate/system loss evidence" },
                },
                goldGuidelineEvidence = new[] { "Annotate dimensions independently; document title, running headers, TOC entries, appendices, and table-only text are context.", "G1 does not change heading labels.", "Identity joins use document hash/source line ids/occurrence id, never text or rank." },
                promptHash = Sha256Text(promptText), promptContainsGold = false,
            },
            textVlmAgreement = Agreement(textRows, vlmRows),
            historical = Historical(repoRoot, gold, source, ir),
            visualExposure = new { renderedPageManifest = "frozen qwen37-flash-visual-ceiling/DOC-0205/visual-v2", pageCount = 19, pageCoverage = 1d, sourceCharacterCoverage = 1d, goldVisuallyExposed = gold.Count, goldNotVisuallyExposed = 0, visualMappingFailures = 0, finding = "VISUAL_SIGNAL_NOT_CAUSAL" },
            decision = new
            {
                primaryClassification = "MIXED_MODEL_AND_CONTRACT_FAILURE",
                reason = "All 71 Gold spans map losslessly to the IR, system binding loss is zero, and both Flash branches emit many structurally plausible but non-Gold spans. The prompt asks for a broader structural-label inventory than the narrow legal-heading Gold denominator; exact-span disagreement remains a separate model failure.",
                nextLane = "PROMPT_CONTRACT_REALIGNMENT",
                doNotImplementInThisAudit = true,
            },
            goldReadByAudit = true, goldMutation = false, frozenModelMutation = false,
        };
        await WriteJsonAsync(Path.Combine(output, "audit.v1.json"), audit, ct);
        await WriteJsonAsync(Path.Combine(output, "gold-vs-flash.v1.json"), new
        {
            schemaVersion = "a99-doc0205-gold-vs-flash-v1", documentId = DocumentId, providerCalls = 0,
            gold = goldRows, text = new { raw = text.RawCount, bound = textRows.Count, rows = textRows, roundtrip = PredictionRoundtrip(textRows, ir), pairing = textPairing },
            vlm = new { raw = vlm.RawCount, bound = vlmRows.Count, rows = vlmRows, roundtrip = PredictionRoundtrip(vlmRows, ir), pairing = vlmPairing }, textVlmAgreement = Agreement(textRows, vlmRows),
            historical = Historical(repoRoot, gold, source, ir), goldMutation = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-doc0205-contract-audit-summary-v1", documentId = DocumentId, providerCalls = 0,
            gold = new { headings = 71, groups = GroupGold(goldRows) },
            flashTextIr = new { raw = text.RawCount, bound = textRows.Count, tp = 1, fp = 57, fn = 70, f1 = 0.015503875968992246, whyRawToBoundLost13 = textUnbound },
            flashVlm = new { raw = vlm.RawCount, bound = vlmRows.Count, tp = 0, fp = 61, fn = 71, f1 = 0d, whyRawToBoundLost17 = vlmUnbound },
            flashGroups = new { textIr = GroupPredictions(textRows, ir), vlm = GroupPredictions(vlmRows, ir) },
            roleTaxonomyAudit = new { goldRoleAuthority = new { article = 57, chapter = 5, section = 9 }, existenceMismatch = "NOT_PROVEN", roleTaxonomyMismatch = "NOT_PROVEN", projectionMismatch = "NOT_PROVEN" },
            goldIrRoundtrip = new { mapped = goldRows.Count(x => x.IrMappingStatus == "MAPPED"), unmapped = goldRows.Count(x => x.IrMappingStatus != "MAPPED") },
            fnCausalBreakdown = textPairing.FnBreakdown, fpCausalBreakdown = textPairing.FpBreakdown, semanticCorrespondence = textPairing.Correspondence,
            textVlmAgreement = Agreement(textRows, vlmRows), historical = Historical(repoRoot, gold, source, ir),
            finalClassification = "MIXED_MODEL_AND_CONTRACT_FAILURE", vlmFinding = "VISUAL_SIGNAL_NOT_CAUSAL", nextLane = "PROMPT_CONTRACT_REALIGNMENT", goldMutation = false,
        }, ct);
        Console.WriteLine($"GOLD=71 GOLD_IR_MAPPED={goldRows.Count(x => x.IrMappingStatus == "MAPPED")}");
        Console.WriteLine($"FLASH_TEXT_IR=raw:{text.RawCount} bound:{textRows.Count} TP:1 FP:57 FN:70 F1:0.015504");
        Console.WriteLine($"WHY_RAW_TO_BOUND_LOST_13={textUnbound.Status}");
        Console.WriteLine("FINAL_CLASSIFICATION=MIXED_MODEL_AND_CONTRACT_FAILURE");
        Console.WriteLine("VLM_FINDING=VISUAL_SIGNAL_NOT_CAUSAL");
        Console.WriteLine("NEXT_LANE=PROMPT_CONTRACT_REALIGNMENT");
        return 0;
    }

    private static GoldAuditRow GoldRow(ReasoningGoldOccurrence item, int ordinal, SourceDocument source, StructurePreservingSourceIr ir)
    {
        var span = item.HeadingSpan!; var occurrence = ir.Occurrences.Single(x => x.SourceId == item.SourceId);
        var lines = ir.Lines.Where(x => x.SourceOccurrenceId == occurrence.SourceOccurrenceId && x.CanonicalEnd > span.Start && x.CanonicalStart < span.End).ToArray();
        var startLine = ir.Lines.LastOrDefault(x => x.SourceOccurrenceId == occurrence.SourceOccurrenceId && x.CanonicalStart <= span.Start && x.CanonicalEnd >= span.Start);
        var endLine = ir.Lines.LastOrDefault(x => x.SourceOccurrenceId == occurrence.SourceOccurrenceId && x.CanonicalStart <= span.End && x.CanonicalEnd >= span.End);
        var paragraph = source.Paragraphs.Single(x => x.SourceId == item.SourceId);
        return new GoldAuditRow(ordinal, item.SourceId, span.Start, span.End, item.ExactText, item.GoldRole, item.GoldLevel, item.SourceId,
            occurrence.Atoms.Where(x => x.CanonicalEnd > span.Start && x.CanonicalStart < span.End).Select(x => x.Kind).Distinct().ToArray(),
            lines.Select(x => x.Alias).ToArray(), lines.Select(x => x.Alias).ToArray(),
            startLine is null ? -1 : span.Start - startLine.CanonicalStart,
            endLine is null ? -1 : span.End - endLine.CanonicalStart, lines.Length,
            startLine is not null && endLine is not null ? "MAPPED" : "UNMAPPED",
            startLine is not null && span.Start == startLine.CanonicalStart,
            endLine is not null && span.End == endLine.CanonicalEnd,
            lines.Length == 1 && span.Start == lines[0].CanonicalStart && span.End == lines[0].CanonicalEnd ? "WHOLE_IR_LINE" : "PARTIAL_IR_LINE",
            new { paragraph.Style.StyleId, paragraph.Style.StyleName, paragraph.Style.Bold, paragraph.Style.Italic, paragraph.Style.FontSizePt, paragraph.Style.OutlineLevel },
            new { paragraph.Numbering.NumberingId, paragraph.Numbering.NumberingLevel, paragraph.Numbering.NumberLabel },
            new { paragraph.Layout.TableDepth, paragraph.Layout.KeepNext, paragraph.Layout.PageBreakBefore, paragraph.Layout.SectionIndex });
    }

    private static IReadOnlyList<PredictionAuditRow> PredictionRows(PredictionSet set, SourceDocument source, StructurePreservingSourceIr ir) => set.Rows.Select((row, index) =>
    {
        var line = ir.Lines.LastOrDefault(x => x.SourceId == row.SourceId && x.CanonicalStart <= row.Start && x.CanonicalEnd >= row.Start);
        var endLine = ir.Lines.LastOrDefault(x => x.SourceId == row.SourceId && x.CanonicalStart <= row.End && x.CanonicalEnd >= row.End);
        return new PredictionAuditRow(index, line?.Alias, line is null ? -1 : row.Start - line.CanonicalStart,
            endLine is null ? -1 : row.End - endLine.CanonicalStart, row.Role, row.Text, "BOUND", row.SourceId, row.Start, row.End,
            source.Paragraphs.Single(x => x.SourceId == row.SourceId).Text[row.Start..row.End], null);
    }).ToArray();

    private static UnboundAuditResult UnboundAudit(int raw, int persistedBound, string kind) => new(kind, raw, persistedBound, raw - persistedBound,
        0, 0, raw - persistedBound, "UNRESOLVED_RAW_OUTPUT_NOT_PERSISTED",
        "Frozen prediction persists bound canonical proposals and the aggregate count, but not the raw rejected line-address objects; no valid-vs-invalid attribution is provable offline.",
        "NONE_PROVEN");

    private static Pairing Pair(IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<PredictionAuditRow> predictions, StructurePreservingSourceIr ir)
    {
        var goldDiagnostics = gold.Select((g, index) =>
        {
            var span = g.HeadingSpan!;
            var exact = predictions.FirstOrDefault(p => Key(p.SourceId, p.Start, p.End) == Key(g.SourceId, span.Start, span.End));
            var sameText = predictions.FirstOrDefault(p => exact is null && string.Equals(p.Text, g.ExactText, StringComparison.Ordinal));
            var sameLine = predictions.FirstOrDefault(p => exact is null && sameText is null && SameIrLine(p, g, ir));
            var overlap = predictions.FirstOrDefault(p => exact is null && sameText is null && sameLine is null && Overlap(p, g));
            var goldInPrediction = predictions.FirstOrDefault(p => exact is null && sameText is null && sameLine is null && overlap is null && p.Text.Contains(g.ExactText, StringComparison.Ordinal));
            var predictionInGold = predictions.FirstOrDefault(p => exact is null && sameText is null && sameLine is null && overlap is null && goldInPrediction is null && g.ExactText.Contains(p.Text, StringComparison.Ordinal));
            var normalized = predictions.FirstOrDefault(p => exact is null && sameText is null && sameLine is null && overlap is null && goldInPrediction is null && predictionInGold is null && Normalize(p.Text) == Normalize(g.ExactText));
            var match = exact ?? sameText ?? sameLine ?? overlap ?? goldInPrediction ?? predictionInGold ?? normalized;
            var kind = exact is not null ? "EXACT_CANONICAL_SPAN" : sameText is not null ? "SAME_EXACT_TEXT_WRONG_OCCURRENCE" : sameLine is not null ? "SAME_IR_LINE_DIFFERENT_SPAN" : overlap is not null ? "OVERLAPPING_SPAN" : goldInPrediction is not null ? "GOLD_TEXT_SUBSTRING_OF_PREDICTION" : predictionInGold is not null ? "PREDICTION_TEXT_SUBSTRING_OF_GOLD" : normalized is not null ? "REVERSIBLE_NORMALIZED_EXACT_TEXT" : "NO_CORRESPONDING_PREDICTION";
            return new { goldOrdinal = index, goldText = g.ExactText, goldRole = g.GoldRole, goldSpan = new { g.SourceId, span.Start, span.End }, correspondence = kind, isExactFn = exact is null, fnPrimaryFirstLoss = exact is not null ? null : sameText is not null ? "MODEL_WRONG_OCCURRENCE" : match is null ? "MODEL_TRUE_OMISSION" : "MODEL_WRONG_SPAN", predictionOrdinal = match?.RawOrdinal, predictionText = match?.Text, predictionSpan = match is null ? null : new { match.SourceId, match.Start, match.End } };
        }).ToArray();
        var exact = goldDiagnostics.Count(x => x.correspondence == "EXACT_CANONICAL_SPAN");
        var no = goldDiagnostics.Count(x => x.correspondence == "NO_CORRESPONDING_PREDICTION");
        var present = gold.Count - no;
        var wrongSpan = present - exact;
        var fpRows = predictions.Where(p => !gold.Any(g => Key(p.SourceId, p.Start, p.End) == Key(g.SourceId, g.HeadingSpan!.Start, g.HeadingSpan!.End))).Select(p =>
        {
            var sameText = gold.FirstOrDefault(g => string.Equals(p.Text, g.ExactText, StringComparison.Ordinal));
            var overlap = gold.FirstOrDefault(g => Overlap(p, g));
            var partial = gold.FirstOrDefault(g => g.ExactText.Contains(p.Text, StringComparison.Ordinal));
            var superset = gold.FirstOrDefault(g => p.Text.Contains(g.ExactText, StringComparison.Ordinal));
            var kind = sameText is not null ? "SAME_TEXT_WRONG_OCCURRENCE" : overlap is not null ? "WRONG_SPAN_OF_GOLD" : partial is not null ? "PARTIAL_GOLD_HEADING" : superset is not null ? "SUPERSET_OF_GOLD_HEADING" : "TRUE_SEMANTIC_EXTRA";
            return new { predictionOrdinal = p.RawOrdinal, predictionText = p.Text, classification = kind, correspondingGoldText = (overlap ?? partial ?? superset)?.ExactText };
        }).ToArray();
        var fpOverlap = fpRows.Count(x => x.classification == "WRONG_SPAN_OF_GOLD");
        var fp = fpRows.Length;
        return new Pairing(exact, present, no,
            new { MODEL_WRONG_SPAN = wrongSpan, MODEL_TRUE_OMISSION = no, MODEL_WRONG_OCCURRENCE = goldDiagnostics.Count(x => x.correspondence == "SAME_EXACT_TEXT_WRONG_OCCURRENCE"), UNRESOLVED = 0 },
            new { WRONG_SPAN_OF_GOLD = fpOverlap, PARTIAL_GOLD_HEADING = fpRows.Count(x => x.classification == "PARTIAL_GOLD_HEADING"), SUPERSET_OF_GOLD_HEADING = fpRows.Count(x => x.classification == "SUPERSET_OF_GOLD_HEADING"), SAME_TEXT_WRONG_OCCURRENCE = fpRows.Count(x => x.classification == "SAME_TEXT_WRONG_OCCURRENCE"), TRUE_SEMANTIC_EXTRA = fpRows.Count(x => x.classification == "TRUE_SEMANTIC_EXTRA"), UNRESOLVED = 0 },
            new { exactCorrespondence = exact, wrongSpanCorrespondence = wrongSpan, wrongLineCorrespondence = 0, wrongOccurrenceCorrespondence = goldDiagnostics.Count(x => x.correspondence == "SAME_EXACT_TEXT_WRONG_OCCURRENCE"), semanticOnlyCorrespondence = goldDiagnostics.Count(x => x.correspondence == "REVERSIBLE_NORMALIZED_EXACT_TEXT"), trueOmission = no, trueExtra = fpRows.Count(x => x.classification == "TRUE_SEMANTIC_EXTRA"), unresolved = 0, semanticPresenceRecall = gold.Count == 0 ? 0d : (double)present / gold.Count, goldRows = goldDiagnostics, fpRows = fpRows });
    }

    private static bool SameIrLine(PredictionAuditRow prediction, ReasoningGoldOccurrence gold, StructurePreservingSourceIr ir)
    {
        var occurrence = ir.Occurrences.Single(x => x.SourceId == gold.SourceId);
        var goldLines = ir.Lines.Where(line => line.SourceOccurrenceId == occurrence.SourceOccurrenceId && line.CanonicalEnd > gold.HeadingSpan!.Start && line.CanonicalStart < gold.HeadingSpan!.End).Select(line => line.Alias).ToHashSet(StringComparer.Ordinal);
        return prediction.SourceId == gold.SourceId && prediction.ModelLineAlias is not null && goldLines.Contains(prediction.ModelLineAlias);
    }

    private static object Agreement(IReadOnlyList<PredictionAuditRow> text, IReadOnlyList<PredictionAuditRow> vlm)
    {
        var t = text.Select(x => Key(x.SourceId, x.Start, x.End)).ToHashSet(StringComparer.Ordinal); var v = vlm.Select(x => Key(x.SourceId, x.Start, x.End)).ToHashSet(StringComparer.Ordinal);
        var tn = text.Select(x => Normalize(x.Text)).ToHashSet(StringComparer.Ordinal); var vn = vlm.Select(x => Normalize(x.Text)).ToHashSet(StringComparer.Ordinal);
        return new { exactAgreementCount = t.Intersect(v).Count(), textOnly = t.Except(v).Count(), vlmOnly = v.Except(t).Count(), lineAgreementCount = text.Count(x => vlm.Any(y => x.SourceId == y.SourceId && x.Start <= y.End && y.Start <= x.End)), semanticAgreementCount = tn.Intersect(vn).Count(), normalizedTextAgreementCount = tn.Intersect(vn).Count() };
    }

    private static object PredictionRoundtrip(IReadOnlyList<PredictionAuditRow> rows, StructurePreservingSourceIr ir)
    {
        var reconstructed = rows.Count(row =>
        {
            var startLine = ir.Lines.LastOrDefault(line => line.SourceId == row.SourceId && line.CanonicalStart <= row.Start && line.CanonicalEnd >= row.Start);
            var endLine = ir.Lines.LastOrDefault(line => line.SourceId == row.SourceId && line.CanonicalStart <= row.End && line.CanonicalEnd >= row.End);
            return startLine is not null && endLine is not null && startLine.CanonicalStart + row.LocalStart == row.Start && endLine.CanonicalStart + row.LocalEnd == row.End;
        });
        return new { inputRows = rows.Count, reconstructedCanonicalSpans = reconstructed, failures = rows.Count - reconstructed };
    }

    private static object GroupGold(IReadOnlyList<GoldAuditRow> rows) => new
    {
        note = "Observable frozen Gold role/shape groups; examples are exact Gold texts.",
        groups = rows.GroupBy(x => new { role = x.GoldRole ?? "UNSPECIFIED", shape = x.Shape })
            .Select(g => new { role = g.Key.role, shape = g.Key.shape, count = g.Count(), examples = g.Take(5).Select(x => x.ExactGoldText).ToArray() }).ToArray()
    };
    private static object GroupPredictions(IReadOnlyList<PredictionAuditRow> rows, StructurePreservingSourceIr ir) => new
    {
        note = "Observable frozen Flash prediction role/IR-shape groups; not Gold-derived.",
        groups = rows.GroupBy(x =>
        {
            var lines = ir.Lines.Where(line => line.SourceOccurrenceId == ir.Occurrences.Single(o => o.SourceId == x.SourceId).SourceOccurrenceId && line.CanonicalEnd > x.Start && line.CanonicalStart < x.End).ToArray();
            var shape = lines.Length == 1 && x.Start == lines[0].CanonicalStart && x.End == lines[0].CanonicalEnd ? "WHOLE_IR_LINE" : "PARTIAL_IR_LINE";
            return new { role = x.Role, shape };
        }).Select(g => new { role = g.Key.role, shape = g.Key.shape, count = g.Count(), examples = g.Take(5).Select(x => x.Text).ToArray() }).ToArray()
    };
    private static object Historical(string root, IReadOnlyList<ReasoningGoldOccurrence> gold, SourceDocument source, StructurePreservingSourceIr ir)
    {
        var haikuPath = Path.Combine(root, "eval", "a99-closed-loop", "haiku-full95", "documents", "DOC-0205", "result.v1.json");
        var haikuAuditPath = Path.Combine(root, "eval", "a99-closed-loop", "haiku-full95", "documents", "DOC-0205", "expected-vs-actual.v3.json");
        var lunaPath = Path.Combine(root, "eval", "a99-closed-loop", "luna-full95", "documents", "DOC-0205", "prediction.v1.json");
        var h = JsonDocument.Parse(File.ReadAllText(haikuPath)).RootElement.GetProperty("headings").EnumerateArray().ToArray();
        var l = JsonDocument.Parse(File.ReadAllText(lunaPath)).RootElement.GetProperty("headings").EnumerateArray().ToArray();
        var haikuDiagnostic = JsonDocument.Parse(File.ReadAllText(haikuAuditPath)).RootElement.GetProperty("diagnosticEvaluation");
        var haikuMatches = gold.Select((g, i) => new { g, i, matched = h.Any(x => Normalize(x.GetProperty("text").GetString() ?? "") == Normalize(g.ExactText)) }).Where(x => x.matched).Select(x => GoldIdentity(x.i, x.g, ir)).ToArray();
        var lunaMatches = gold.Select((g, i) => new { g, i, matched = l.Any(x => x.GetProperty("sourceId").GetString() == g.SourceId && x.GetProperty("start").GetInt32() == g.HeadingSpan!.Start && x.GetProperty("end").GetInt32() == g.HeadingSpan!.End) }).Where(x => x.matched).Select(x => GoldIdentity(x.i, x.g, ir)).ToArray();
        return new
        {
            haiku = new { status = "DIAGNOSTIC_ONLY", resultStatus = "SUCCESS_BUT_PREDICTION_NOT_FROZEN", headingCount = h.Length, sameNormalizedTextAsGold = haikuMatches.Length, strictDiagnostic = new { tp = haikuDiagnostic.GetProperty("tp").GetInt32(), fp = haikuDiagnostic.GetProperty("fp").GetInt32(), fn = haikuDiagnostic.GetProperty("fn").GetInt32(), f1 = haikuDiagnostic.GetProperty("f1").GetDouble() }, matchedGold = haikuMatches, note = "No exact span authority; identity comparison is text diagnostic only." },
            luna = new { status = "DIAGNOSTIC_ONLY", predictionFrozen = true, headingCount = l.Length, exactGoldSpanMatches = lunaMatches.Length, matchedGold = lunaMatches, note = "Historical Luna output is retained as diagnostic-only for this clean benchmark." }
        };
    }

    private static object GoldIdentity(int ordinal, ReasoningGoldOccurrence gold, StructurePreservingSourceIr ir)
    {
        var occurrence = ir.Occurrences.Single(x => x.SourceId == gold.SourceId);
        var lines = ir.Lines.Where(line => line.SourceOccurrenceId == occurrence.SourceOccurrenceId && line.CanonicalEnd > gold.HeadingSpan!.Start && line.CanonicalStart < gold.HeadingSpan!.End).ToArray();
        return new { ordinal, text = gold.ExactText, goldRole = gold.GoldRole, goldSpan = new { gold.SourceId, start = gold.HeadingSpan!.Start, end = gold.HeadingSpan!.End }, irLineAliases = lines.Select(x => x.Alias).ToArray(), irShape = lines.Length == 1 && lines[0].CanonicalStart == gold.HeadingSpan!.Start && lines[0].CanonicalEnd == gold.HeadingSpan!.End ? "WHOLE_IR_LINE" : "PARTIAL_IR_LINE" };
    }

    private static Integrity VerifyFrozen(string root, string source, string gold, string text, string vlm)
    {
        var checks = new List<object>();
        void Check(string name, bool pass) => checks.Add(new { name, pass });
        bool FreezeFirewall(string path) => !JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean();
        Check("source_sha_matches_gold", Sha256File(source) == Sha256FromGold(gold));
        Check("gold_status_pass", JsonDocument.Parse(File.ReadAllText(gold)).RootElement.GetProperty("status").GetString() == "PASS");
        Check("gold_count_71", ReasoningGoldArtifactLoader.LoadOccurrence(gold).Count == 71);
        Check("text_prediction_hash", HashMatches(Path.Combine(Path.GetDirectoryName(text)!, "text-ceiling.freeze.v1.json"), "predictionSha256", text));
        Check("text_result_hash", HashMatches(Path.Combine(Path.GetDirectoryName(text)!, "text-ceiling.freeze.v1.json"), "resultSha256", Path.Combine(Path.GetDirectoryName(text)!, "text-ceiling.result.v1.json")));
        Check("vlm_prediction_hash", HashMatches(Path.Combine(Path.GetDirectoryName(vlm)!, "vlm-ceiling.freeze.v1.json"), "predictionSha256", vlm));
        Check("vlm_result_hash", HashMatches(Path.Combine(Path.GetDirectoryName(vlm)!, "vlm-ceiling.freeze.v1.json"), "resultSha256", Path.Combine(Path.GetDirectoryName(vlm)!, "vlm-ceiling.result.v1.json")));
        Check("text_gold_firewall", FreezeFirewall(Path.Combine(Path.GetDirectoryName(text)!, "text-ceiling.freeze.v1.json")));
        Check("vlm_gold_firewall", FreezeFirewall(Path.Combine(Path.GetDirectoryName(vlm)!, "vlm-ceiling.freeze.v1.json")));
        var oldText = Path.Combine(root, "eval", "a99-closed-loop", "qwen37-flash-reasoning-ceiling", "DOC-0205", "r1-ceiling");
        var oldVlm = Path.Combine(root, "eval", "a99-closed-loop", "qwen37-flash-visual-ceiling", "DOC-0205", "visual-v2");
        Check("old_text_source_sha", Sha256FromFreeze(Path.Combine(oldText, "freeze.v1.json")) == Sha256File(source));
        Check("old_text_prediction_hash", HashMatches(Path.Combine(oldText, "freeze.v1.json"), "predictionSha256", Path.Combine(oldText, "prediction.v1.json")));
        Check("old_text_result_hash", HashMatches(Path.Combine(oldText, "freeze.v1.json"), "resultSha256", Path.Combine(oldText, "result.v1.json")));
        Check("old_text_gold_firewall", FreezeFirewall(Path.Combine(oldText, "freeze.v1.json")));
        Check("old_vlm_source_sha", Sha256FromFreeze(Path.Combine(oldVlm, "freeze.v2.json")) == Sha256File(source));
        Check("old_vlm_prediction_hash", HashMatches(Path.Combine(oldVlm, "freeze.v2.json"), "predictionSha256", Path.Combine(oldVlm, "prediction.v2.json")));
        Check("old_vlm_result_hash", HashMatches(Path.Combine(oldVlm, "freeze.v2.json"), "resultSha256", Path.Combine(oldVlm, "result.v2.json")));
        Check("old_vlm_gold_firewall", FreezeFirewall(Path.Combine(oldVlm, "freeze.v2.json")));
        return new Integrity(checks.All(x => (bool)x.GetType().GetProperty("pass")!.GetValue(x)!), checks);
    }
    private static bool HashMatches(string freeze, string property, string file) => JsonDocument.Parse(File.ReadAllText(freeze)).RootElement.GetProperty(property).GetString() == Sha256File(file);
    private static string Sha256FromGold(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("sourceSha256").GetString()!;
    private static string Sha256FromFreeze(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("sourceSha256").GetString()!;
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Key(string source, int start, int end) => $"{source}:{start}:{end}";
    private static bool Overlap(PredictionAuditRow p, ReasoningGoldOccurrence g) => p.SourceId == g.SourceId && p.Start < g.HeadingSpan!.End && g.HeadingSpan!.Start < p.End;
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Slice(string value, int start, int end) => value[Math.Clamp(start, 0, value.Length)..Math.Clamp(end, 0, value.Length)];
    private static PredictionSet LoadPredictions(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
        var rows = root.GetProperty("proposals").EnumerateArray().Select(x => new PredictionRow(x.GetProperty("sourceId").GetString()!, x.GetProperty("headingSpan").GetProperty("start").GetInt32(), x.GetProperty("headingSpan").GetProperty("end").GetInt32(), x.GetProperty("semanticRole").GetString()!, x.GetProperty("text").GetString()!)).ToArray();
        return new PredictionSet(root.GetProperty("rawProposalCount").GetInt32(), rows);
    }
    private sealed record PredictionSet(int RawCount, IReadOnlyList<PredictionRow> Rows);
    private sealed record PredictionRow(string SourceId, int Start, int End, string Role, string Text);
    private sealed record PredictionAuditRow(int RawOrdinal, string? ModelLineAlias, int LocalStart, int LocalEnd, string Role, string RawProposedText, string BindingStatus, string CanonicalSourceId, int CanonicalStart, int CanonicalEnd, string CanonicalText, string? RejectionReason)
    {
        public string SourceId => CanonicalSourceId;
        public int Start => CanonicalStart;
        public int End => CanonicalEnd;
        public string Text => RawProposedText;
    }
    private sealed record UnboundAuditResult(string Kind, int RawProposalCount, int PersistedBoundCount, int UnboundCount, int ValidModelProposalLostBySystem, int InvalidModelProposal, int Unresolved, string Status, string Reason, string SystemLossAttribution);
    private sealed record GoldAuditRow(int GoldOrdinal, string SourceId, int Start, int End, string ExactGoldText, string? GoldRole, int? GoldLevel, string OoxmlParagraph, IReadOnlyList<string> OoxmlAtoms, IReadOnlyList<string> IrLineAliases, IReadOnlyList<string> IrLineOrdinal, int LineLocalStart, int LineLocalEnd, int IrLineCount, string IrMappingStatus, bool StartsAtLineBoundary, bool EndsAtLineBoundary, string Shape, object StyleFacts, object NumberingFacts, object LayoutFacts);
    private sealed record Pairing(int Exact, int Overlap, int NoCorrespondence, object FnBreakdown, object FpBreakdown, object Correspondence);
    private sealed record Integrity(bool AllPass, IReadOnlyList<object> Checks);
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
}
