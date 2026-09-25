using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Tests.GenericAudit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1: blind replay on the calibration set (DOC-0123, DOC-0133) and the checks
/// that the engine is generic in fact, not only in name.
/// <para>
/// Order matters and is enforced by the split: <see cref="Propose_and_freeze"/> runs the engine from
/// the source and freezes its hypotheses without touching Gold; only <see cref="Replay_against_gold"/>
/// joins the frozen proposals with Gold, afterwards. The replay reports true/false positives and
/// negatives, exact composite matches and axis disagreements - discrepancies are reported, never
/// patched. The source paths are this test's input data; the engine never sees an identifier.
/// </para>
/// </summary>
public sealed partial class GenericAuditEngineTests
{
    private const string Root = "eval/a99-closed-loop/generic-audit-v1";

    /// <summary>Input data, not engine code: document, media, source.</summary>
    public static TheoryData<string, string, string> CalibrationSet => new()
    {
        { "DOC-0123", "DOCX", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx" },
        { "DOC-0133", "PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf" },
    };

    private static IReadOnlyList<SemanticHypothesis> Run(string media, string path)
    {
        var profile = media == "DOCX"
            ? SourceEvidenceProfile.FromDocx(TestRepository.Path(path))
            : SourceEvidenceProfile.FromPdf(TestRepository.Path(path));
        return SemanticAuditEngine.Propose(profile);
    }

    [Theory]
    [MemberData(nameof(CalibrationSet))]
    public void Propose_and_freeze(string id, string media, string path)
    {
        var hypotheses = Run(media, path);
        var again = Run(media, path);
        Assert.Equal(JsonSerializer.Serialize(hypotheses), JsonSerializer.Serialize(again)); // deterministic

        FreezeArtifact.AssertJson(Root, $"{id}.proposals.v1.json", new
        {
            artifactKind = "a99_generic_audit_proposals",
            engine = SemanticAuditEngine.EngineId,
            ontology = OccurrenceSemanticAxesTests.OntologyId,
            documentId = id,
            source = new { path, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(path)), media },
            modelCalls = 0,
            goldReadBeforeFreeze = false,
            engineConstants = SemanticAuditEngine.Constants,
            counts = new
            {
                occurrences = hypotheses.Sum(h => h.Aliases.Length),
                hypotheses = hypotheses.Count,
                byProposal = hypotheses.GroupBy(h => h.ProposedIsHeading).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                byRoles = hypotheses.GroupBy(h => string.Join("+", h.OccurrenceRoles)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            // Every TRUE and NEEDS_REVIEW hypothesis, and every FALSE one that was set apart from the body.
            hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
        });
    }

    [Theory]
    [MemberData(nameof(CalibrationSet))]
    public void Replay_against_gold(string id, string media, string path)
    {
        _ = media;
        _ = path;
        // The frozen proposals first; Gold only after.
        using var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{id}.proposals.v1.json")));
        var proposals = frozen.RootElement.GetProperty("hypotheses").EnumerateArray().Select(h => new
        {
            aliases = h.GetProperty("Aliases").EnumerateArray().Select(a => a.GetString()!).ToArray(),
            text = h.GetProperty("Text").GetString()!,
            proposal = h.GetProperty("ProposedIsHeading").GetString()!,
            primary = h.GetProperty("PrimaryFunction").GetString(),
            scope = h.GetProperty("Scope").GetString(),
            roles = h.GetProperty("OccurrenceRoles").EnumerateArray().Select(r => r.GetString()!).ToArray(),
            evidence = h.GetProperty("Evidence").EnumerateArray().Select(e => e.GetString()!).ToArray(),
        }).ToArray();

        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json")));
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(c =>
        {
            var parts = c.TryGetProperty("parts", out var p) ? p : c.GetProperty("sourceParts");
            var axes = c.GetProperty("semanticAxes");
            return new
            {
                aliases = parts.EnumerateArray().Select(x => x.GetProperty("sourceAlias").GetString()!).ToArray(),
                text = c.TryGetProperty("exactText", out var t) ? t.GetString()! : c.GetProperty("approvedWording").GetString()!,
                primary = axes.GetProperty("primaryFunction").GetString(),
                scope = axes.GetProperty("scope").GetString(),
                roles = axes.GetProperty("occurrenceRoles").EnumerateArray().Select(r => r.GetString()!).ToArray(),
            };
        }).ToArray();

        var byAlias = proposals.SelectMany(p => p.aliases.Select(a => (a, p))).ToDictionary(x => x.a, x => x.p, StringComparer.Ordinal);
        var goldAliases = claims.SelectMany(c => c.aliases).ToHashSet(StringComparer.Ordinal);
        var matched = claims.Select(c => new { gold = c, engine = c.aliases.Select(a => byAlias.GetValueOrDefault(a)).FirstOrDefault(p => p is not null) }).ToArray();

        var tp = matched.Where(m => m.engine?.proposal == "TRUE").ToArray();
        var fnReview = matched.Where(m => m.engine?.proposal == "NEEDS_REVIEW").ToArray();
        var fnFalse = matched.Where(m => m.engine is null || m.engine.proposal == "FALSE").ToArray();
        var fp = proposals.Where(p => p.proposal == "TRUE" && !p.aliases.Any(goldAliases.Contains)).ToArray();
        var reviewNotGold = proposals.Where(p => p.proposal == "NEEDS_REVIEW" && !p.aliases.Any(goldAliases.Contains)).ToArray();

        object Row(dynamic m) => new { goldText = (string)m.gold.text, engineText = (string?)m.engine?.text, engineRoles = (string[]?)m.engine?.roles, engineEvidence = (string[]?)m.engine?.evidence };

        FreezeArtifact.AssertJson(Root, $"{id}.replay.v1.json", new
        {
            artifactKind = "a99_generic_audit_replay",
            engine = SemanticAuditEngine.EngineId,
            documentId = id,
            proposalsSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/{id}.proposals.v1.json")),
            goldTotal = claims.Length,
            membership = new
            {
                truePositives = tp.Length,
                falsePositives = fp.Length,
                falseNegativesProposedForReview = fnReview.Length,
                falseNegativesProposedFalse = fnFalse.Length,
                nonGoldProposedForReview = reviewNotGold.Length,
                exactCompositeMatches = tp.Count(m => m.engine!.aliases.ToHashSet().SetEquals(m.gold.aliases)),
                compositeMismatches = tp.Where(m => !m.engine!.aliases.ToHashSet().SetEquals(m.gold.aliases))
                    .Select(m => new { goldAliases = m.gold.aliases, engineAliases = m.engine!.aliases, goldText = m.gold.text, engineText = m.engine.text }).ToArray(),
            },
            axisDisagreementsOnTruePositives = new
            {
                primaryFunction = tp.Count(m => m.engine!.primary != m.gold.primary),
                scope = tp.Count(m => m.engine!.scope != m.gold.scope),
                roles = tp.Count(m => !m.engine!.roles.ToHashSet().SetEquals(m.gold.roles)),
                scopePairs = tp.Where(m => m.engine!.scope != m.gold.scope).GroupBy(m => $"{m.gold.scope} <- engine {m.engine!.scope}")
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            genericity = GenericityScan(),
            falseNegativesForReview = fnReview.Select(Row).ToArray(),
            falseNegativesProposedFalseItems = fnFalse.Select(Row).ToArray(),
            falsePositives = fp.Select(p => new { p.text, p.roles, p.evidence }).ToArray(),
            nonGoldForReview = reviewNotGold.Select(p => new { p.text, p.roles, p.evidence }).ToArray(),
        });
    }

    // ---- SRC029_BLIND_GENERALIZATION_AUDIT_V1 ------------------------------------------------------

    private const string Src029 = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";

    private static object EngineIdentity() => new
    {
        engine = SemanticAuditEngine.EngineId,
        files = EngineFiles.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray(),
        constants = SemanticAuditEngine.Constants,
    };

    /// <summary>
    /// Pre-registration, committed before the blind run: the engine as it is at 73eff5a (its files'
    /// hashes and constants), the pinned source, the four gaps known before SRC-029 and the protocol.
    /// </summary>
    [Fact]
    public void Preregister_src029()
    {
        FreezeArtifact.AssertJson(Root, "SRC-029.preregistration.v1.json", new
        {
            artifactKind = "a99_generic_audit_preregistration",
            study = "SRC029_BLIND_GENERALIZATION_AUDIT_V1",
            engineCommit = "73eff5a",
            engineIdentity = EngineIdentity(),
            source = new { path = Src029, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src029)), media = "PDF" },
            sourceNote = "The Gold source of SRC-029 is the original PDF since 187aee9; the retired pdf2docx DOCX (475f76...0fe9e) is not this study's source. The approved total 356 was counted on that DOCX and is provenance only.",
            knownBeforeSrc029 = new[]
            {
                new { id = "B1", gap = "TOC opener with repeated text" },
                new { id = "B2", gap = "TOC sequence continuity evidence" },
                new { id = "B3", gap = "DOCX bold run-in lead" },
                new { id = "B4", gap = "outline level on list item" },
            },
            forbidden = new[]
            {
                "model/provider/VLM calls",
                "Gold loading before the proposals are committed",
                "the semantic total 356 as engine input or target",
                "engine code, threshold or configuration changes",
            },
            protocol = new[]
            {
                "1 freeze engine 73eff5a and the known gaps (this artifact)",
                "2 run the engine blind on SRC-029",
                "3 commit the source evidence profile and proposals before any Gold is read",
                "4 independent source-only review and human decisions (engine proposals are not an authority)",
                "5 freeze SRC-029 occurrence Gold (any total; 356 is provenance)",
                "6 join and score: TP/FP/FN, NEEDS_REVIEW recall, axis disagreements",
                "7 classify residuals A/B/C/D; known gaps recurred vs new gaps",
                "8 fix generic B gaps only",
                "9 freeze engine V1.1 (SRC-029 becomes development evidence)",
                "10 test V1.1 blind on SRC-041",
            },
            modelCalls = 0,
        });
    }

    // ---- generic in fact -------------------------------------------------------------------------

    private static readonly string[] EngineFiles =
    [
        "tests/DocxHeaderExtractor.Tests/GenericAudit/SourceEvidence.cs",
        "tests/DocxHeaderExtractor.Tests/GenericAudit/SemanticAuditEngine.cs",
    ];

    [GeneratedRegex(@"\b(?:DOC|SRC)-\d{3,4}\b")] private static partial Regex DocumentId();
    [GeneratedRegex(@"todo10_8|heading_corpus|eval/|\.gold|gold-current|Gold[A-Z]", RegexOptions.IgnoreCase)] private static partial Regex SourcePathOrGold();
    [GeneratedRegex(@"(?:FontSize|Size|Page|Left|Row)\s*(?:==|>=|<=|>|<)\s*\d")] private static partial Regex AbsoluteGate();
    [GeneratedRegex(@"\.(?:StartsWith|EndsWith|Contains|Equals)\(\s*""|Text\s*==\s*""|""\s*==\s*\w*Text")] private static partial Regex LiteralTextComparison();
    [GeneratedRegex(@"\b(?:359|362|112|123)\b")] private static partial Regex GoldTotals();

    /// <summary>Code only: comments and XML docs may mention anything.</summary>
    private static string CodeOf(string file) => string.Join("\n", File.ReadAllLines(TestRepository.Path(file))
        .Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    private static object GenericityScan()
    {
        var engine = CodeOf(EngineFiles[1]);
        var evidence = CodeOf(EngineFiles[0]);
        var lexicalStart = evidence.IndexOf("internal static partial class LexicalShape", StringComparison.Ordinal);
        var lexicalEnd = evidence.IndexOf("internal sealed class SourceEvidenceProfile", StringComparison.Ordinal);
        var outsideLexical = evidence[..lexicalStart] + evidence[lexicalEnd..];
        var all = engine + "\n" + evidence;
        return new
        {
            documentIdReferences = DocumentId().Matches(all).Count,
            sourcePathOrGoldReferences = SourcePathOrGold().Matches(all).Count,
            absoluteTypographyOrPageGates = AbsoluteGate().Matches(all).Count,
            literalTextComparisonsInEngine = LiteralTextComparison().Matches(engine).Count,
            regexInEngine = Regex.Matches(engine, @"\bRegex\b").Count,
            literalTextOutsideLexicalShapes = LiteralTextComparison().Matches(outsideLexical).Count,
            goldTotalLiterals = GoldTotals().Matches(all).Count,
            lexicalShapes = Regex.Matches(evidence[lexicalStart..lexicalEnd], @"public static bool (\w+)").Select(m => m.Groups[1].Value).ToArray(),
            modelCalls = 0,
        };
    }

    [Fact]
    public void The_engine_is_generic_in_its_code()
    {
        var scan = JsonSerializer.SerializeToElement(GenericityScan());
        foreach (var name in new[] { "documentIdReferences", "sourcePathOrGoldReferences", "absoluteTypographyOrPageGates",
                     "literalTextComparisonsInEngine", "regexInEngine", "literalTextOutsideLexicalShapes", "goldTotalLiterals" })
            Assert.True(scan.GetProperty(name).GetInt32() == 0, $"{name} = {scan.GetProperty(name).GetInt32()}");
    }
}
