using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;
namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Structural guards for the promoted source-adapter and heading-authority seam. These assertions
/// deliberately inspect source because a successful build cannot show an accidental dependency on
/// a parser, an OOXML implementation, or a concrete provider.
/// </summary>
public sealed class HeadingAuthorityArchitectureTests
{
    [Fact]
    public void Shared_heading_authority_has_no_parser_or_concrete_provider_dependency()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority");
        var forbidden = new[]
        {
            "UglyToad.PdfPig", "WordprocessingDocument", "OpenRouterInferenceTransport",
            "LmStudioInferenceTransport", "SglangInferenceTransport", "LlamaInferenceTransport",
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Retired_transport_and_pipeline_vocabulary_is_gone_from_production()
    {
        var banned = new[]
        {
            "OpenRouterHeaderExtractor", "LmStudioHeaderExtractor", "SglangHeaderExtractor", "LlamaHeaderExtractor",
            "IHeaderClassifierFactory", "HeaderClassifierFactory", "FrozenHeaderExecutionResult",
            "LeaseBoundFrozenHeaderClassifier", "BorrowedHeaderClassifier",
            "DocxHeadingAuthorityRoute", "PdfHeadingAuthorityRoute", "AuthorityExtractionPipeline",
            "PdfCanonicalExtraction", "CanonicalExtractionDispatcher",
            "CanonicalTextHeadingAuthority", "FunctionConditionedHeadingAuthority", "HeadingProposalBinder",
            "HeadingProposalValidator", "HeaderClassifierCanonicalTextModel", "PdfProductionOpenRouterHeaderClassifier",
            "HeadingPlacementCoordinator", "ResolvedHeadingPlacement", "CanonicalStructureMaterializer", "CanonicalSourceOccurrence",
            "QualifiedInferenceRequestFactory", "PdfQualifiedInferencePolicy",
            "RouteExecutionAudit", "RouteLaneExecutionAudit", "RouteBlockAudit", "RouteBlockDecisionAudit",
            "CanonicalRouteAuditBoundary",
        };
        var retiredSourceSymbols = new[]
        {
            "SourceOccurrenceUniverse", "SourceOccurrence", "HeadingSourceContext", "DocxSourceOccurrenceAdapter",
            "PdfSourceOccurrenceAdapter", "PdfSourceOccurrenceBuildResult", "PdfSourceOccurrenceDetails",
            "DocxAuthoritySource", "DocxAuthorityContext", "HeadingContexts",
            "SourceFactsBuilder",
            "PdfStyleClusterProfile", "PdfStyleClusterStats",
            "PdfSemanticBlock", "PdfSemanticBlockGrouper", "PdfSemanticSourceContext", "PdfSemanticSourceContextBuilder", "HeadingReadable",
        };
        foreach (var file in Directory.EnumerateFiles(TestRepository.Path("src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (var token in banned)
                Assert.False(source.Contains(token, StringComparison.Ordinal), $"{token} in {Path.GetFileName(file)}");
            // Whole-symbol match: prose and longer names that merely contain these words are not the retired symbols.
            foreach (var symbol in retiredSourceSymbols)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(source, $@"\b{symbol}\b"), $"{symbol} in {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public void Pipeline_folder_holds_orchestration_only_and_each_stage_lives_in_its_own_folder()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing");
        string[] orchestrationFiles =
        [
            "DocxExtractionPipeline.cs", "DocxHeadingPipeline.cs", "PdfExtractionPipeline.cs", "PdfHeadingPipeline.cs",
            "PdfLaneExecution.cs", "PdfStageCheckpoint.cs", "ProductionCheckpointScope.cs", "PipelineOptions.cs",
            "PdfSemanticLaneOptions.cs",
        ];
        var pipeline = Path.Combine(root, "Pipeline");
        Assert.Equal(orchestrationFiles.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(pipeline, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(pipeline, path)).Order(StringComparer.Ordinal));
        var layout = new (string Folder, string[] Files)[]
        {
            ("Source/Docx", ["DocxSourceAdapter"]),
            ("Source/Pdf", ["PdfSourceAdapter", "PdfSourceBuildResult", "PdfLineExtraction", "PdfLineIdentity", "PdfLineObservationAnalyzer",
                "PdfSegmentAtomCatalog", "PdfLayoutBlockGrouper", "PdfSourceFacts", "PdfSourceContextBuilder", "PdfSourceEvidence", "PdfSourceTextProjection",
                "PdfStyleKey", "PdfTextUtilities", "PdfVisualLineSegmentation", "PdfReadOnlyCorrespondenceBuilder", "PdfSourceFactsBuilder"]),
            ("Source", ["DocumentSourceCatalogBuilder"]),
            ("Source/Common", ["DocumentOccurrence", "DocumentSourceSnapshot", "OccurrenceContext", "LooseLabelledMarkerParser", "SourceMarkerFactsParser", "SourceMarkerFact"]),
            ("Semantics/HeadingAuthority", ["TextSemanticHeadingAuthority", "FunctionAnchorExtentHeadingAuthority", "HeadingDecisionBinder"]),
            ("Semantics/Canonical", ["CanonicalSemanticEngine", "CanonicalSemanticExperiment", "CanonicalGrounding", "SemanticConflictCensus",
                "SemanticEvidencePackingPolicy", "SemanticRequestVersion"]),
            ("Materialization", ["HeadingParentResolver", "HeadingHierarchyResolver", "HeadingStructureMaterializer", "HeadingStructureAssembler"]),
            ("Projection", ["CanonicalFinalStructureProjection", "DocumentProductOutputProjector", "HeadingOutlineProjection",
                "SectionChunkProjection", "StructuralSectionProjection", "OutputDecisionPolicy"]),
            ("Authority", ["ExecutionAuditBoundary", "HierarchyFactHash", "PipelineExecutionAudit", "HeadingHierarchyFactAudit", "PdfHierarchyFactsInventory", "HeadingPipelineResult"]),
            ("Provenance", ["BuildProvenance"]),
            ("OpenXmlLayer", ["DocxProductWriteback"]),
            ("Inference", ["IFrozenInferenceRequestComposer", "PdfInferenceWireContract"]),
        };
        foreach (var (folder, files) in layout)
            foreach (var file in files)
            {
                Assert.True(File.Exists(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), file + ".cs")), $"{folder}/{file}.cs");
                Assert.False(File.Exists(Path.Combine(root, "Pipeline", file + ".cs")), $"Pipeline/{file}.cs should have moved");
            }
    }

    [Fact]
    public void Source_ownership_has_no_reverse_dependency_on_pipeline()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Source");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("DocxHeaderExtractor.DocumentProcessing.Pipeline", source, StringComparison.Ordinal);
            var folder = Path.GetDirectoryName(Path.GetRelativePath(root, file))!;
            var expectedNamespace = "DocxHeaderExtractor.DocumentProcessing.Source" +
                (folder.Length == 0 ? "" : "." + folder.Replace(Path.DirectorySeparatorChar, '.'));
            Assert.Contains($"namespace {expectedNamespace};", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Document_processing_keeps_provider_names_and_classifier_vocabulary_out()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (var token in new[] { "qwen", "alibaba", "OpenRouterQwen37", "V5ProviderEnvelope", "QualifiedInferenceRequestFactory", "classifier" })
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Materializer_does_not_choose_a_document_format()
    {
        var source = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Materialization/HeadingStructureMaterializer.cs"));

        Assert.DoesNotContain("\"docx\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pdf\"", source, StringComparison.Ordinal);
        Assert.Contains("occurrence.SourceKind", source, StringComparison.Ordinal);
        Assert.Contains("sourceParagraph.BoundarySource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_source_ir_contains_no_pdf_parser_diagnostics()
    {
        var root = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Source/Common");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("PdfPig", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PdfDetails", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PdfSemantic", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LayoutBlockByAtom", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pdf_adapter_keeps_parser_details_outside_common_universe()
    {
        var route = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfExtractionPipeline.cs"));
        var adapter = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Source/Pdf/PdfSourceAdapter.cs"));
        var common = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Source/Common/DocumentSourceSnapshot.cs"));

        Assert.Contains("BuildWithDetails", route, StringComparison.Ordinal);
        Assert.Contains("sourceBuild.Snapshot", route, StringComparison.Ordinal);
        Assert.Contains("sourceBuild.Details", route, StringComparison.Ordinal);
        Assert.Contains("PdfSourceBuildResult", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfDetails", common, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfSemantic", common, StringComparison.Ordinal);
    }

    [Fact]
    public void Docx_source_adapter_does_not_borrow_pdf_semantic_contracts_or_promote_docx()
    {
        var adapter = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Source/Docx/DocxSourceAdapter.cs"));
        var route = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxHeadingPipeline.cs"));

        Assert.DoesNotContain("PdfSourceAdapter", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfSemantic", adapter, StringComparison.Ordinal);
        // DOCX stays on the canonical-text authority until it has its own qualification evidence.
        Assert.Contains("TextSemanticHeadingAuthority", route, StringComparison.Ordinal);
        Assert.DoesNotContain("FunctionAnchorExtentHeadingAuthority", route, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfHeadingPipeline", route, StringComparison.Ordinal);
        Assert.DoesNotContain("IFrozenInferenceTransport", route, StringComparison.Ordinal);
        Assert.DoesNotContain("IPdfProductionAuthorizedInferenceTransport", route, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_authority_route_uses_qualified_transport_without_canonical_text_fallback()
    {
        var extraction = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfExtractionPipeline.cs"));
        var route = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfHeadingPipeline.cs"));
        var authority = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority/FunctionAnchorExtentHeadingAuthority.cs"));

        Assert.Contains("PdfSourceAdapter.Build", extraction, StringComparison.Ordinal);
        Assert.Contains("PdfHeadingPipeline.RunAsync", extraction, StringComparison.Ordinal);
        Assert.Contains("IPdfProductionAuthorizedInferenceTransport", extraction, StringComparison.Ordinal);
        Assert.Contains("IFrozenInferenceTransport", route, StringComparison.Ordinal);
        Assert.Contains("IFrozenInferenceTransport", authority, StringComparison.Ordinal);
        foreach (var source in new[] { extraction, route, authority })
        {
            Assert.DoesNotContain("CanonicalSemanticTextProductionEntryPoint", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TextSemanticHeadingAuthority", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Both_routes_converge_on_the_heading_authority_seam_and_one_assembler()
    {
        var docx = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxHeadingPipeline.cs"));
        var pdf = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfHeadingPipeline.cs"));
        var assembler = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/Materialization/HeadingStructureAssembler.cs"));

        foreach (var route in new[] { docx, pdf })
        {
            Assert.Contains("IHeadingAuthority", route, StringComparison.Ordinal);
            Assert.Contains("HeadingStructureAssembler.AssembleAsync", route, StringComparison.Ordinal);
            // Binding, placement, hierarchy and materialization belong to the assembler only.
            Assert.DoesNotContain("HeadingStructureMaterializer", route, StringComparison.Ordinal);
            Assert.DoesNotContain("HeadingHierarchyResolver", route, StringComparison.Ordinal);
            Assert.DoesNotContain("HeadingParentResolver", route, StringComparison.Ordinal);
        }
        foreach (var token in new[] { "HeadingStructureMaterializer.Materialize", "HeadingHierarchyResolver", "HeadingParentResolver", "HeadingDecisionBinder" })
            Assert.Contains(token, assembler, StringComparison.Ordinal);
        Assert.DoesNotContain("\"docx\"", assembler, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pdf\"", assembler, StringComparison.Ordinal);
        Assert.DoesNotContain("Pdf", assembler.Replace("PdfSource", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Qualified_protocol_wire_identity_is_unchanged()
    {
        var protocols = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Semantics/HeadingAuthority/Protocols");
        var core = TestRepository.Path("src/DocxHeaderExtractor.Core/Models/Inference");
        var text = string.Join(Environment.NewLine, Directory.EnumerateFiles(protocols, "*.cs").Concat(Directory.EnumerateFiles(core, "*.cs")).Select(File.ReadAllText));
        foreach (var identity in new[]
                 {
                     "v5-total-occurrence-function-membership-1", "v5-function-conditioned-anchor-existence-1",
                     "v5-function-conditioned-exact-end-pointer-clean-paired-1", "ESTABLISHES_STRUCTURE",
                     "REPRESENTS_STRUCTURE", "HAS_STRUCTURAL_EXTENT", "NO_STRUCTURAL_EXTENT",
                 })
            Assert.Contains(identity, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_production_source_has_no_reverse_reference_to_qualification_project()
    {
        var project = File.ReadAllText(TestRepository.Path(
            "src/DocxHeaderExtractor.DocumentProcessing/DocxHeaderExtractor.DocumentProcessing.csproj"));
        var production = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing");

        Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification", project, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(production, "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
