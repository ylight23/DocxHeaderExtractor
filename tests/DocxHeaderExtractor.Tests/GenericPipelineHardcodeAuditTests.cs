using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1 - every hard-coded rule the production path or the
/// model-visible contract carries, classified by reachability and effect rather than by "grep found
/// a constant".
/// <para>
/// The rows are the reviewer's reading of the code: producer, whether production reaches it, whether
/// the model sees it, whether it can change semantic truth, whether the data has triggered it, and -
/// the deciding column - whether the model would be asked to infer the same fact. A harness
/// conclusion about the question the model answers is removed from the request, not tuned. The
/// measurements are computed: the code scan for document-specific constants, how often each scope
/// label fires on the four PDFs with structured Gold and how many Gold headings it lands on, and the
/// key set of the V2 request itself.
/// </para>
/// </summary>
public sealed partial class GenericPipelineHardcodeAuditTests
{
    private const string Dir = "eval/a99-closed-loop/hardcode-audit-v1";

    private static readonly string[] StructuredGoldPdfs = ["SRC-029", "DOC-0133", "DOC-0252", "DOC-0256"];

    private sealed record Row(
        string Symbol,
        string Value,
        string Producer,
        bool ProductionReachable,
        bool ModelVisibleBefore,
        bool ModelVisibleAfter,
        string SemanticTruthAffecting,
        string DataTriggered,
        bool DocumentSpecific,
        string RawFactAvailable,
        string WouldTheModelBeAskedToInferThisSameFact,
        string Classification,
        string Action);

    private static readonly Row[] Rows =
    [
        new("scope", "running_page_artifact (PDF)",
            "PdfCandidateContextBuilder.BuildFacts: every line page-number-shaped, or repeated on >= max(3, pages/3) pages AND in the top/bottom 8% of the extent",
            true, true, false, "prior to the model's decision (plus a non-suppressive review observation)", "measured below",
            false, "location.page, location.pageBand, location.sameNormalizedTextPageCount/FirstPage/LastPage, text",
            "YES - PAGE_FURNITURE is an occurrence role the model decides", "PRE_INTERPRETED_SEMANTIC_PRIOR",
            "REMOVED_FROM_MODEL_VISIBLE; replaced by raw position and recurrence counts"),
        new("scope", "table_of_contents (PDF)",
            "PdfStructuralScopeDetector.DetectTocBlockIds: dot-leader + page-number lines, >= 3 on one page, only in the first min(5, pages/4) pages",
            true, true, false, "prior to the model's decision", "measured below",
            false, "text (the leader and page number are in it), location.page, neighbouring entries",
            "YES - NAVIGATION vs REGION_OPENER is exactly the TOC-opener question (known gap B1)", "PRE_INTERPRETED_SEMANTIC_PRIOR",
            "REMOVED_FROM_MODEL_VISIBLE"),
        new("inTableOfContents", "true (PDF)", "PdfCanonicalSourceUniverseBuilder.EvidenceOf: StructuralScope == table_of_contents",
            true, true, false, "prior to the model's decision", "same as scope=table_of_contents",
            false, "as above", "YES", "PRE_INTERPRETED_SEMANTIC_PRIOR (derived only from the scope label)", "REMOVED_FROM_MODEL_VISIBLE"),
        new("scope", "table / appendix_table (PDF)",
            "PdfLineBlockFilter.ClassifyTableLine: numeric density >= 0.35 or a short numbered line - a text-shape rule, not table geometry",
            true, true, false, "prior to the model's decision", "measured below",
            false, "text, style, neighbouring rows", "YES - BODY_CONTENT / FIELD_LABEL / CAPTION distinctions", "PRE_INTERPRETED_SEMANTIC_PRIOR",
            "REMOVED_FROM_MODEL_VISIBLE"),
        new("scope", "code_or_grammar (PDF, DOCX)", "PdfStructuralScopeDetector.IsFormalSyntax: identifier followed by '=' or '::='",
            true, true, false, "prior to the model's decision", "measured below",
            false, "text", "YES - body content vs label", "PRE_INTERPRETED_SEMANTIC_PRIOR", "REMOVED_FROM_MODEL_VISIBLE"),
        new("scope", "appendix (PDF)",
            "StructuralScopeTracker: latched from the first line starting with 'appendix|annex|phu luc' for the rest of the document",
            true, true, false,
            "prior to the model's decision; still keys pack boundaries under COHERENT_REGION_SEGMENTATION_V1 (opt-in, not the default FIXED_OWNED_COUNT_120)",
            "measured below", false, "text of the annex heading and the occurrences after it",
            "YES - region membership", "PRE_INTERPRETED_SEMANTIC_PRIOR; literal label words",
            "REMOVED_FROM_MODEL_VISIBLE; the opt-in packing key is an open item (SUSPICIOUS, not model-visible)"),
        new("scope", "reference_list / index_terms (PDF)",
            "StructuralScopeTracker: latched after a line reading exactly 'References' or 'Index' (optionally numbered)",
            true, true, false, "prior to the model's decision", "measured below", false, "text",
            "YES - region membership", "PRE_INTERPRETED_SEMANTIC_PRIOR; literal heading words", "REMOVED_FROM_MODEL_VISIBLE"),
        new("scope", "quoted_replacement / embedded_amendment (PDF)",
            "StructuralScopeTracker: open-quote latch, amendment phrases in English and Vietnamese ('amended as follows', 'sua doi ... nhu sau')",
            true, true, false, "prior to the model's decision", "measured below", false, "text",
            "YES - whether a quoted clause is structure of this document", "PRE_INTERPRETED_SEMANTIC_PRIOR; literal phrases",
            "REMOVED_FROM_MODEL_VISIBLE"),
        new("scope", "table_of_contents (DOCX)", "DocxAuthorityPipeline.ScopeOf <- SourceParagraph.InTableOfContents",
            true, true, false, "prior to the model's decision", "measured on DOCX only by the existing corpus tests",
            false, "style.StyleName (TOC n), location.hyperlinkAnchors (_Toc...), text",
            "YES - NAVIGATION", "PRE_INTERPRETED_SEMANTIC_PRIOR", "REMOVED_FROM_MODEL_VISIBLE; raw hyperlink anchors added"),
        new("inTableOfContents", "true (DOCX)",
            "OpenXmlDocumentSource: TOC style, OR a hyperlink to a _Toc/_heading bookmark, OR a monotonic run of lines ending in page numbers (typed contents)",
            true, true, false, "prior to the model's decision", "measured on DOCX only by the existing corpus tests",
            false, "style.StyleName, location.hyperlinkAnchors, text", "YES - the typed-run branch is the harness reading a contents list",
            "MIXED: two raw OOXML facts + one inference", "REMOVED_FROM_MODEL_VISIBLE; the raw facts are sent, the inference is not"),
        new("scope", "table (DOCX)", "DocxAuthorityPipeline.ScopeOf: TableDepth > 0",
            true, true, false, "none beyond tableDepth", "every table paragraph", false, "tableDepth",
            "NO - physical containment", "JUSTIFIED_INVARIANT (mechanical), redundant with tableDepth", "REMOVED_AS_DUPLICATE; tableDepth kept"),
        new("tableDepth", "int (DOCX)", "OOXML table nesting", true, true, true, "none", "n/a", false, "is the raw fact",
            "NO", "JUSTIFIED_INVARIANT (mechanical containment)", "KEEP"),
        new("style", "StyleId, StyleName, OutlineLevel, Bold (DOCX)", "OOXML, as the author declared it", true, true, true, "none", "n/a",
            false, "is the raw fact", "NO - an author's declaration, not the harness's reading", "ACCEPTABLE (source declaration)", "KEEP"),
        new("style", "Bold, Italic (PDF)", "glyph ratio >= 0.5", true, true, true, "none", "n/a", false, "BoldRatio / ItalicRatio",
            "NO", "SUSPICIOUS (global threshold quantizing a measurement)", "KEEP as evidence"),
        new("style", "RelativeFontSize (PDF)", "size / median body size: >= 1.25, >= 1.08, <= 0.85 buckets", true, true, true, "none", "n/a",
            false, "the ratio", "NO", "SUSPICIOUS (global thresholds quantizing a measurement)", "KEEP as evidence"),
        new("markers", "marker-family/signature/depth/is-path/components", "PdfMarkerFactsParser: shape of a leading numbering or label+number prefix, open vocabulary",
            true, true, true, "none", "n/a", false, "is a parse of the text", "NO - a reading of the prefix, not of what the line is",
            "ACCEPTABLE (deterministic lexical parse)", "KEEP"),
        new("block", "layout block id (structured PDF)", "PdfSemanticBlockGrouper continuation grouping", true, true, true, "none", "n/a",
            false, "atom geometry", "NO - which lines the layout groups, not what they mean", "SUSPICIOUS (layout heuristic)", "KEEP as evidence"),
        new("attention", "policy-(non-)candidate / pdf-layout-(non-)candidate", "candidate heuristics (8c117d4)",
            false, true, false, "none after 8c117d4", "every request before 8c117d4", false, "n/a", "YES",
            "HISTORICAL_ONLY (V1_ATTENTION_LEGACY replay)", "REMOVED in 8c117d4"),
        new("openStructuralContext", "active marker-bearing ancestors", "experiment arm CarryStructuralAncestors",
            false, false, false, "none unless the arm is selected", "only in I7 experiment runs", false, "text of earlier occurrences",
            "PARTLY - it is a marker-based guess at open sections", "EXPERIMENT_ONLY", "KEEP OFF by default"),
        new("DocumentDomainPolicy", "regime legal/procurement/financial/meeting; roles by literal words",
            "InferRegime/ClassifyRole: genre branches over literal words (PHAN/CHUONG/DIEU, PART/SECTION+BIDDING, NOTES TO FINANCIAL, MINUTES)",
            true, false, false, "none: recorded as a review observation, never suppressive (PdfOutputDecisionPolicy)", "on every run",
            false, "n/a", "YES", "SUSPICIOUS (genre-specific branch, literal words) - not model-visible, not truth-affecting",
            "RECORD; candidate for retirement"),
        new("PdfHierarchyResolver", "domain-tier levels", "PdfCandidateContracts.PdfHierarchyResolver", false, false, false, "none",
            "no production caller", false, "n/a", "YES", "TEST_ONLY", "RECORD; candidate for deletion"),
        new("DOCX candidate heuristics", "CaptionRx, ObjectLabelPrefixRx, data tables, content controls", "HeadingHeuristics / DocxPolicyStateBuilder",
            true, false, false, "none: requests are byte-identical with the heuristics on or off (ModelVisibleAttentionBoundaryTests)",
            "on every DOCX run", false, "n/a", "YES", "DIAGNOSTIC_ONLY", "KEEP for diagnostics"),
    ];

    [Fact]
    public async Task Freeze_the_audit()
    {
        var scan = ScanProductionSource();
        var labels = StructuredGoldPdfs.Select(MeasureScopeLabels).ToArray();
        var contract = await MeasureV2Contract();

        Assert.Empty(scan.DocumentIds);
        Assert.Empty(scan.SourceFileNames);
        Assert.Empty(scan.HashConstants);
        Assert.Empty(scan.PageConditions);
        Assert.Empty(contract.Forbidden);

        FreezeArtifact.AssertJson(Dir, "A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1.json", new
        {
            artifactKind = "a99_generic_pipeline_hardcode_audit",
            study = "A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1",
            principle = "HARNESS MAY REPORT OBSERVABLE SOURCE FACTS. HARNESS MUST NOT PRE-INTERPRET THEIR SEMANTIC MEANING.",
            decidingColumn = "wouldTheModelBeAskedToInferThisSameFact: if yes, the harness does not send its conclusion",
            modelProviderCalls = 0,
            reviewer = "rows are the reviewer's reading of the code; measurements below are computed",
            rows = Rows,
            codeScan = new
            {
                scope = "src/**/*.cs, excluding bin/obj",
                sourceFileNamePattern = "corpus folders (todo10_8, heading_corpus, generated-docx, bench/) or numbered corpus file names; generic I/O names are not hits",
                documentIds = scan.DocumentIds,
                sourceFileNames = scan.SourceFileNames,
                sha256Constants = scan.HashConstants,
                pageNumberConditions = scan.PageConditions,
            },
            scopeLabelsOnStructuredGoldPdfs = labels,
            modelVisibleContractV2 = contract,
            gate = new
            {
                documentIds = scan.DocumentIds.Count,
                literalHeadingDecisionsModelVisible = contract.Forbidden.Count(f => f.StartsWith("scope", StringComparison.Ordinal)),
                semanticCandidateScoreOrGate = contract.Forbidden.Count(f => f.Contains("candidate", StringComparison.OrdinalIgnoreCase)),
                attentionPrior = contract.Forbidden.Count(f => f.Contains("attention", StringComparison.Ordinal)),
                preclassifiedTocRunningHeaderCaption = contract.Forbidden.Count,
                passed = contract.Forbidden.Count == 0 && scan.DocumentIds.Count == 0,
            },
            openItems = new[]
            {
                "COHERENT_REGION_SEGMENTATION_V1 (opt-in packing) still splits packs on the appendix latch; not the default and not model-visible",
                "DocumentDomainPolicy genre branches remain for review observations; retire or keep as diagnostics",
                "PdfHierarchyResolver has no production caller",
                "PDF Bold/Italic/RelativeFontSize are thresholded measurements; the raw ratios could replace the buckets",
            },
        });
    }

    /// <summary>
    /// The V2 contract, frozen once the audit passed: the prompts it sends and the exact request bytes
    /// it composes for one structured PDF and one DOCX. From here a change to anything the model sees
    /// is a new request version with its own freeze, never an edit that moves this one. It was frozen
    /// under PDF_SOURCE_FACTS_V1, and replays under it.
    /// </summary>
    [Fact]
    public Task Freeze_the_v2_model_visible_contract() =>
        FreezeContract("MODEL_VISIBLE_CONTRACT_V2.freeze.json", PdfSourceFactsVersion.V1_NominalFontSize, extra: null);

    /// <summary>
    /// The same request version over PDF_SOURCE_FACTS_V2, production's current source facts: the PDF
    /// evidence now carries the raw typography behind Bold and RelativeFontSize. A freeze of its own, so the
    /// V1-facts contract above stays reproducible.
    /// </summary>
    [Fact]
    public Task Freeze_the_v2_model_visible_contract_over_pdf_source_facts_v2() =>
        FreezeContract("MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V2.freeze.json", PdfSourceFactsVersion.V2_EffectivePointSize,
            extra: new
            {
                pdfSourceFacts = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V2_EffectivePointSize),
                supersedesForProduction = "MODEL_VISIBLE_CONTRACT_V2.freeze.json (PDF_SOURCE_FACTS_V1), kept for replay",
                change = "PDF style facts add Typography { sourceFacts, effectivePointSize, fontName, fontBoldFlag, derivedBold, boldEvidenceSource }; Bold and RelativeFontSize are computed from the effective point size and the derived weight. DOCX requests are unchanged",
            });

    /// <summary>
    /// The same request version over PDF_SOURCE_FACTS_V3: the PDF typography facts become robust glyph statistics
    /// (dominant / median / min / max size, dominant font, bold and italic glyph ratios). A freeze of its own.
    /// </summary>
    [Fact]
    public Task Freeze_the_v2_model_visible_contract_over_pdf_source_facts_v3() =>
        FreezeContract("MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V3.freeze.json", PdfSourceFactsVersion.V3_RobustGlyphStatistics,
            extra: new
            {
                pdfSourceFacts = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V3_RobustGlyphStatistics),
                change = "PDF style facts carry Typography { sourceFacts, dominantPointSize, medianPointSize, minPointSize, maxPointSize, dominantFontName, fontBoldFlag, derivedBold, boldEvidenceSource, boldGlyphRatio, italicGlyphRatio }; RelativeFontSize is computed from the dominant size. DOCX requests are unchanged",
            });

    private static async Task FreezeContract(string name, PdfSourceFactsVersion facts, object? extra)
    {
        const string pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
        const string docx = "bench/01-style-chuan.docx";
        using var pdfCapture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), pdfCapture, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts, sourceFacts: facts);
        var source = new OpenXmlDocumentSource().Read(TestRepository.Path(docx));
        var state = DocxPolicyStateBuilder.Build(source,
            NumberingStyleFeatures.FromSourceDocument(source),
            new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
        var mode = DocumentModeClassifier.Measure(
            state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        using var docxCapture = new RequestCapturingClassifier();
        await DocxAuthorityPipeline.RunAsync(state, mode, docxCapture);

        static string Plan(IEnumerable<CapturedRequest> requests) => CanonicalSemanticRequestComposer.Hash(string.Join(
            "\u0000", requests.Select(r => CanonicalSemanticRequestComposer.Hash(r.UserMessage))));

        var contract = new
        {
            artifactKind = "a99_model_visible_contract_freeze",
            requestVersion = SemanticRequestVersions.ProductionDefault.ToString(),
            gate = "A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1 passed",
            systemPromptSha256 = CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPrompt),
            stage1MembershipPromptSha256 = CanonicalArtifactHash.OfText(CanonicalSemanticEngine.Stage1MembershipPrompt),
            structuredPdf = new
            {
                path = pdf,
                modelVisibleEvidenceSha256 = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf), facts).ModelVisibleEvidenceHashV2,
                requests = pdfCapture.Requests.Count,
                providerModelInputPlanSha256 = Plan(pdfCapture.Requests),
            },
            docx = new
            {
                path = docx,
                requests = docxCapture.Requests.Count,
                providerModelInputPlanSha256 = Plan(docxCapture.Requests),
            },
        };
        if (extra is null) FreezeArtifact.AssertJson(Dir, name, contract);
        else FreezeArtifact.AssertJson(Dir, name, new { contract, sourceFacts = extra });
    }

    private sealed record Scan(
        IReadOnlyList<string> DocumentIds, IReadOnlyList<string> SourceFileNames,
        IReadOnlyList<string> HashConstants, IReadOnlyList<string> PageConditions);

    private static Scan ScanProductionSource()
    {
        var root = TestRepository.Path("src");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<string> Hits(Regex rx) => files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            // Code, not prose: a comment that mentions the corpus it was measured on decides nothing.
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && rx.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(TestRepository.Root(), x.f).Replace('\\', '/')}:{x.i + 1}")
            .ToList();
        return new Scan(Hits(DocumentId()), Hits(SourceFileName()), Hits(Sha256Constant()), Hits(PageCondition()));
    }

    private static object MeasureScopeLabels(string id)
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
        var pdf = gold.RootElement.GetProperty("source").GetProperty("sourcePath").GetString()!;
        var evidence = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Evidence;
        var scopeByAlias = evidence.ToDictionary(e => e.SourceAlias, e => e.StructuralScope, StringComparer.Ordinal);
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray())
            .ToArray();
        return new
        {
            documentId = id,
            atoms = evidence.Count,
            goldHeadings = claims.Length,
            byScope = evidence.GroupBy(e => e.StructuralScope).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
            {
                scope = g.Key,
                atoms = g.Count(),
                goldHeadingsTouching = claims.Count(parts => parts.Any(a => scopeByAlias.GetValueOrDefault(a) == g.Key)),
            }).ToArray(),
        };
    }

    private sealed record ContractMeasurement(IReadOnlyList<string> OwnedKeys, IReadOnlyList<string> LocationKeys, IReadOnlyList<string> Forbidden);

    private static async Task<ContractMeasurement> MeasureV2Contract()
    {
        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"),
            capture, CancellationToken.None, profile: PdfSemanticAuthorityProfile.StructuredSourceParts);
        var owned = new SortedSet<string>(StringComparer.Ordinal);
        var location = new SortedSet<string>(StringComparer.Ordinal);
        var forbidden = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var request in capture.Requests)
        {
            var end = request.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
            using var packet = JsonDocument.Parse(request.UserMessage[..end]);
            foreach (var item in packet.RootElement.GetProperty("sourceEvidence").EnumerateArray()
                         .Where(i => i.GetProperty("owned").GetBoolean()))
            {
                foreach (var p in item.EnumerateObject()) owned.Add(p.Name);
                if (item.TryGetProperty("location", out var l)) foreach (var p in l.EnumerateObject()) location.Add(p.Name);
            }

            // The evidence packet only: after it comes the response schema, where "scope" is the
            // model's own output field, not something the harness tells it.
            foreach (Match m in ForbiddenLabel().Matches(request.UserMessage[..end])) forbidden.Add(m.Value);
            foreach (Match m in ForbiddenLabel().Matches(request.SystemPrompt)) forbidden.Add(m.Value);
        }

        return new ContractMeasurement([.. owned], [.. location], [.. forbidden]);
    }

    [GeneratedRegex(@"""(DOC-\d{4}|SRC-\d{3})""")] private static partial Regex DocumentId();
    // A corpus source: its folder or its numbered file name. Generic I/O names ("upload.docx",
    // "approved.outline.docx", a "*.docx" glob) name no document and are not hits.
    [GeneratedRegex(@"todo10_8|heading_corpus|generated-docx|bench/|\b\d{3}_[A-Za-z][A-Za-z0-9_.-]*\.(pdf|docx)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFileName();
    [GeneratedRegex(@"""[0-9a-f]{64}""")] private static partial Regex Sha256Constant();
    [GeneratedRegex(@"\b[Pp]age\s*==\s*\d")] private static partial Regex PageCondition();
    [GeneratedRegex(@"""(scope|inTableOfContents|attention)""\s*:|running_page_artifact|table_of_contents|policy-(non-)?candidate|pdf-layout-(non-)?candidate|candidateScore")]
    private static partial Regex ForbiddenLabel();
}
