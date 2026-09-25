using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Step 7 of SRC029_BLIND_GENERALIZATION_AUDIT_V1: every residual of the committed raw score (adf11f4) gets
/// exactly one cause, and every cause exactly one bucket (A / B-known / B-new / C / D) as the protocol
/// defined them (17c3d4b). The raw score is read, never edited.
/// <para>
/// Causes are assigned by ordered rules over the residual and the atom layout facts - the reviewer's
/// reading of the residuals, written as rules so that each assignment is traceable and can be overruled
/// by cause rather than item by item. They are analysis, not engine code: nothing here feeds the engine.
/// </para>
/// </summary>
public sealed partial class Src029ResidualClassificationTests
{
    private const string Score = Src029BlindScoreTests.Root + "/SRC-029.blind-score.v1.json";

    /// <summary>Each cause, its bucket, and the reason it is in that bucket.</summary>
    private static readonly Dictionary<string, (string Bucket, string Reason)> Causes = new()
    {
        ["WRAPPED_TITLE_NOT_ASSEMBLED"] = ("B_NEW",
            "the engine judges each visual line of a wrapped title on its own and never joins continuation lines into one multipart claim; a PDF line-continuation rule (same column, next row, compatible typography, no terminal punctuation) names no document"),
        ["RUN_IN_LABEL_INSIDE_ATOM"] = ("B_KNOWN_B3",
            "the heading is a leading part of an atom whose rest is body text (margin clause label fused with its body, bold lead); the engine proposes whole atoms only. Pre-registered as B3 for DOCX; this is the same gap on PDF, where the leading bold prefix is available as evidence"),
        ["TOC_OPENER_WITH_REPEATED_TEXT"] = ("B_KNOWN_B1",
            "the contents opener is judged a contents entry because its text reappears elsewhere - pre-registered B1"),
        ["NEXT_LABEL_HIERARCHY_EVIDENCE"] = ("B_NEW",
            "a set-apart title followed directly by a peer-or-stronger label is sent to review as 'opens nothing'; whether the following label is its own first sub-heading is structural evidence (numbering, scope) the engine does not use. Fail-safe held: NEEDS_REVIEW, not FALSE"),
        ["MULTI_COLUMN_ROW"] = ("B_NEW",
            "the occurrence shares its visual row with other segments (table cells, a two-column clause layout); the engine reads a set-apart cell or column line as a standalone label or discards a column heading. PDF table/column region evidence is generic"),
        ["INSTRUCTION_TEXT"] = ("B_NEW",
            "bracketed or italic drafting instructions, often a continuation line of a multi-line bracket; the bracketed-note shape is tested per line, so a line inside a bracket that neither opens nor closes it is not recognised"),
        ["CONNECTIVE_LINE"] = ("B_NEW",
            "a set-apart connective between alternatives (OR, AND, and/or) - a lexical shape the engine lacks"),
        ["PARTIAL_BOLD_RUN_IN"] = ("B_KNOWN_B3",
            "partly bold line: a bold lead-in inside a sentence or list item, read as a label because the atom counts as bold - the run-in lead gap (B3) seen from the other side"),
        ["WEAKLY_SET_APART_MEANING"] = ("A",
            "expressible in the ontology; the evidence cannot settle meaning (weakly set-apart label, date over prose, colon-ended field over prose) and the difference is a reviewer decision"),
        ["SET_APART_MEANING"] = ("A",
            "a strongly set-apart line whose heading status is a meaning decision (sample-table titles, part listings in an overview, cells of forms); expressible in the ontology"),
    };

    [Fact]
    public void Classify_the_residuals()
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Score)));
        var facts = File.ReadAllLines(TestRepository.Path($"{Src029SourceReviewTests.Dir}/atom-layout-facts.tsv"))
            .Select(l => l.Split('\t')).ToDictionary(f => f[0], StringComparer.Ordinal);
        var segmentsByRow = facts.Keys.GroupBy(a => a[..a.IndexOf(':')], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src029BlindScoreTests.GoldPath)));
        var verbatimAtoms = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("sourceParts").EnumerateArray())
            .Where(p => p.GetProperty("selectionMode").GetString() == "VERBATIM_TEXT")
            .Select(p => p.GetProperty("sourceAlias").GetString()!).ToHashSet(StringComparer.Ordinal);

        double Fact(string alias, int column) => double.Parse(facts[alias][column], System.Globalization.CultureInfo.InvariantCulture);
        bool MultiColumn(string alias) => segmentsByRow[alias[..alias.IndexOf(':')]] > 1;

        // Rules for an engine hypothesis that is no Gold claim and touches none.
        string Outside(JsonElement h)
        {
            var aliases = h.GetProperty("Aliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var text = h.GetProperty("Text").GetString()!;
            var evidence = h.GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (text.Contains('[') || text.Contains(']') || aliases.All(a => Fact(a, 6) >= 0.5)) return "INSTRUCTION_TEXT";
            if (Connective().IsMatch(text)) return "CONNECTIVE_LINE";
            if (aliases.Any(MultiColumn)) return "MULTI_COLUMN_ROW";
            if (aliases.All(a => Fact(a, 5) is >= 0.5 and < 0.9)) return "PARTIAL_BOLD_RUN_IN";
            if (evidence.Any(e => e.StartsWith("set apart weakly", StringComparison.Ordinal) || e == "colon-ended label")) return "WEAKLY_SET_APART_MEANING";
            return "SET_APART_MEANING";
        }

        var residuals = score.RootElement.GetProperty("residuals");
        var items = new List<(string Kind, string Text, string Detail, string Cause)>();

        foreach (var r in residuals.GetProperty("goldNotEngineTrue").EnumerateArray())
        {
            var bucket = r.GetProperty("bucket").GetString()!;
            var aliases = r.GetProperty("goldAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var engine = r.GetProperty("engine").EnumerateArray().ToArray();
            var evidence = engine.SelectMany(e => e.GetProperty("Evidence").EnumerateArray().Select(x => x.GetString()!)).ToArray();
            string cause;
            if (aliases.Any(verbatimAtoms.Contains)) cause = "RUN_IN_LABEL_INSIDE_ATOM";
            else if (bucket.StartsWith("PARTIAL_", StringComparison.Ordinal)) cause = "WRAPPED_TITLE_NOT_ASSEMBLED";
            else if (evidence.Any(e => e.StartsWith("contents-list entry", StringComparison.Ordinal))) cause = "TOC_OPENER_WITH_REPEATED_TEXT";
            else if (evidence.Any(e => e.StartsWith("a peer or stronger label follows directly", StringComparison.Ordinal))) cause = "NEXT_LABEL_HIERARCHY_EVIDENCE";
            else if (aliases.Any(MultiColumn)) cause = "MULTI_COLUMN_ROW";
            else cause = "WEAKLY_SET_APART_MEANING";
            items.Add(("GOLD_NOT_ENGINE_TRUE", r.GetProperty("goldText").GetString()!, bucket, cause));
        }

        foreach (var (kind, array) in new[] { ("NON_GOLD_TRUE", "nonGoldTrue"), ("NON_GOLD_NEEDS_REVIEW", "nonGoldNeedsReview") })
            foreach (var r in residuals.GetProperty(array).EnumerateArray())
            {
                var h = r.GetProperty("hypothesis");
                var aliases = h.GetProperty("Aliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
                // A hypothesis touching Gold is a piece of a Gold claim: it shares that claim's cause.
                var cause = !r.GetProperty("touchesGold").GetBoolean() ? Outside(h)
                    : aliases.Any(verbatimAtoms.Contains) ? "RUN_IN_LABEL_INSIDE_ATOM"
                    : "WRAPPED_TITLE_NOT_ASSEMBLED";
                items.Add((kind, h.GetProperty("Text").GetString()!, r.GetProperty("touchesGold").GetBoolean() ? "touches Gold" : "outside Gold", cause));
            }

        Assert.Equal(
            residuals.GetProperty("goldNotEngineTrue").GetArrayLength() + residuals.GetProperty("nonGoldTrue").GetArrayLength()
                + residuals.GetProperty("nonGoldNeedsReview").GetArrayLength(),
            items.Count);

        FreezeArtifact.AssertJson(Src029BlindScoreTests.Root, "SRC-029.residual-classification.v1.json", new
        {
            artifactKind = "a99_generic_audit_residual_classification",
            study = "SRC029_BLIND_GENERALIZATION_AUDIT_V1",
            rawScore = new { path = Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Score)), editedByThisStep = false },
            protocol = Src029BlindScoreTests.Protocol,
            modelProviderVlmCalls = 0,
            status = "REVIEWER_PROPOSAL: cause assignments are the reviewer's reading, for the user to confirm or overrule by cause",
            residuals = items.Count,
            byBucket = items.GroupBy(i => Causes[i.Cause].Bucket).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.GroupBy(i => i.Kind).OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Count())),
            byCause = Causes.Select(c => new
            {
                cause = c.Key,
                bucket = c.Value.Bucket,
                reason = c.Value.Reason,
                counts = items.Where(i => i.Cause == c.Key).GroupBy(i => i.Kind).OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Count()),
            }).ToArray(),
            knownGapsRecurring = new[] { "B1", "B3" },
            knownGapsNotObserved = new[] { "B2 (TOC sequence continuity: no contents entry was proposed TRUE as a heading)", "B4 (outline level on list item: a DOCX property; this source is a PDF)" },
            documentSpecificExceptions = 0,
            documentSpecificNote = "no residual needs a rule that names this document, a text, a page or a total; every cause above is stated in generic evidence terms",
            axisFindings = new[]
            {
                new
                {
                    axis = "repeatStatus",
                    bucket = "C",
                    finding = "the 11 exact true positives marked Gold FIRST / engine REPEATED were checked, and all have an earlier occurrence of the same text that is not a heading (a contents entry, a list item, a mention in prose). The engine counts those, and the Gold rule counts only reviewed set-apart occurrences. OCCURRENCE_SEMANTIC_AXES_V2 does not say what a repeat is a repeat of - an ontology definition gap for the user to settle; the Gold is not changed",
                },
                new
                {
                    axis = "primaryFunction / semanticFunctions / scope",
                    bucket = "A",
                    finding = "form, part and embedded-document titles: Gold IDENTITY with scope FORM / DOCUMENT_PART / EMBEDDED_ARTIFACT, engine STRUCTURE / SECTION. Expressible in the ontology; that a title names an artifact is a meaning the engine has no evidence for",
                },
            },
            partLevelDiagnostic = "reported in the analysis, not a score: atoms of TRUE hypotheses inside Gold claims 533/646, Gold claim atoms covered by TRUE hypotheses 533/677; all 379 TRUE hypotheses touching Gold lie inside exactly one Gold claim",
            items = items.Select(i => new { kind = i.Kind, text = i.Text, detail = i.Detail, cause = i.Cause, bucket = Causes[i.Cause].Bucket }).ToArray(),
        });
    }

    [GeneratedRegex(@"^\s*(?:OR|Or|or|AND|And|and|and/or|AND/OR)\s*$")] private static partial Regex Connective();
}
