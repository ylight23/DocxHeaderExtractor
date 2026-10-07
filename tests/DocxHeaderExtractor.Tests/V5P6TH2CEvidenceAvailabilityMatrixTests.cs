using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>Audits parser/source, sanitized edge-audit, production-adapter, and H2-C request fact availability.</summary>
public sealed class V5P6TH2CEvidenceAvailabilityMatrixTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string H2CPreflightPath = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string SanitizedAuditPath = Root + "/p6th2c-clean-boundary-separability-audit/h2c-clean-boundary-separability-sanitized.v1.json";
    private const string OutputRoot = Root + "/p6th2c-evidence-availability-matrix";

    private sealed record FactRow(
        string Fact,
        bool SourceOrParserAvailable,
        string SanitizedAuditAvailability,
        string ProductionAdapterVisibility,
        string H2CModelVisibility,
        string PairwiseStatus,
        string Finding);

    [Fact]
    public void H2C_evidence_projection_is_compared_to_source_and_production_facts_without_provider_or_Gold_reads()
    {
        var repo = TestRepository.Root();
        var h2cBytes = File.ReadAllBytes(TestRepository.Path(H2CPreflightPath));
        var auditBytes = File.ReadAllBytes(TestRepository.Path(SanitizedAuditPath));
        using var preflight = JsonDocument.Parse(h2cBytes);
        using var audit = JsonDocument.Parse(auditBytes);
        var preflightRoot = preflight.RootElement;
        var auditRoot = audit.RootElement;
        Assert.Equal(31, preflightRoot.GetProperty("requestUniverse").GetArrayLength());
        Assert.Equal(89, auditRoot.GetProperty("edges").GetArrayLength());

        var requests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        Assert.Equal(31, requests.Count);
        var visibleFields = new HashSet<string>(StringComparer.Ordinal);
        var styleFields = new HashSet<string>(StringComparer.Ordinal);
        var locationFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            using var user = JsonDocument.Parse(request.UserMessage);
            var root = user.RootElement;
            Assert.Equal("v5-function-conditioned-exact-end-pointer-clean-paired-1", root.GetProperty("protocolVersion").GetString());
            var occurrences = root.GetProperty("anchors")[0].GetProperty("occurrences");
            Assert.True(occurrences.GetArrayLength() > 1);
            foreach (var occurrence in occurrences.EnumerateArray())
            {
                visibleFields.UnionWith(PropertyNames(occurrence));
                styleFields.UnionWith(PropertyNames(occurrence.GetProperty("style")));
                locationFields.UnionWith(PropertyNames(occurrence.GetProperty("location")));
            }
        }

        Assert.True(visibleFields.SetEquals(["occurrence", "page", "text", "style", "location"]));
        Assert.True(styleFields.SetEquals(["fontSize", "bodyFontSize", "fontSizeToBodyRatio", "boldRatio", "italicRatio", "lineCount"]));
        Assert.True(locationFields.SetEquals(["verticalPosition", "sameNormalizedTextPageCount", "sameNormalizedTextFirstPage", "sameNormalizedTextLastPage"]));

        var evidenceSource = ReadSource("src/DocxHeaderExtractor.DocumentProcessing/Source/Pdf/PdfSourceEvidence.cs");
        var productionAdapter = ReadSource("src/DocxHeaderExtractor.V5Qualification/LegacyPdf/PdfHeadingMembershipProductionAdapter.cs");
        var sourceBuilder = ReadSource("src/DocxHeaderExtractor.DocumentProcessing/Source/Pdf/PdfSourceAdapter.cs");
        var pdfSourceDetails = ReadSource("src/DocxHeaderExtractor.DocumentProcessing/Source/Pdf/PdfSourceBuildResult.cs");
        var endPointerBuilder = ReadSource("src/DocxHeaderExtractor.V5Qualification/P6TH2CEndPointerCanary.cs");
        using var frozenMatrix = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(
            OutputRoot + "/h2c-evidence-availability-matrix.v1.json")));
        var frozenCodeHashes = frozenMatrix.RootElement.GetProperty("artifactAuthorities").GetProperty("code");
        Assert.Contains("fontBoldFlagRatio", evidenceSource, StringComparison.Ordinal);
        Assert.Contains("fontNameBoldRatio", evidenceSource, StringComparison.Ordinal);
        Assert.Contains("dominantPointSize", evidenceSource, StringComparison.Ordinal);
        Assert.Contains("left = Round(source.Left", productionAdapter, StringComparison.Ordinal);
        Assert.Contains("right = Round(source.Right", productionAdapter, StringComparison.Ordinal);
        Assert.Contains("width = Round(Math.Max(0, source.Right - source.Left)", productionAdapter, StringComparison.Ordinal);
        Assert.Contains("LayoutBlockByAtom", pdfSourceDetails, StringComparison.Ordinal);
        Assert.Contains("var layoutBlockByAtom", sourceBuilder, StringComparison.Ordinal);
        Assert.Contains("block = layoutBlockByAtom.GetValueOrDefault(item.SourceId)", sourceBuilder, StringComparison.Ordinal);
        Assert.Contains("Do not use hierarchy, candidate alternatives, relations, coordinates, aliases", endPointerBuilder, StringComparison.Ordinal);

        var sameBlockStats = new
        {
            internalTrue = auditRoot.GetProperty("featureSeparation").GetProperty("sameParserLayoutBlock").GetProperty("internalTrue").GetInt32(),
            internalN = auditRoot.GetProperty("featureSeparation").GetProperty("sameParserLayoutBlock").GetProperty("internalN").GetInt32(),
            exitTrue = auditRoot.GetProperty("featureSeparation").GetProperty("sameParserLayoutBlock").GetProperty("goldExitTrue").GetInt32(),
            exitN = auditRoot.GetProperty("featureSeparation").GetProperty("sameParserLayoutBlock").GetProperty("goldExitN").GetInt32(),
        };
        Assert.Equal(new { internalTrue = 3, internalN = 8, exitTrue = 0, exitN = 27 }, sameBlockStats);

        var rows = new FactRow[]
        {
            new("text", true, "OMITTED_BY_SANITIZATION", "VISIBLE", "DIRECT: occurrence.text", "NOT_APPLICABLE", "Text is directly model-visible."),
            new("page", true, "DIRECT: LeftPage/RightPage", "VISIBLE", "DIRECT: occurrence.page", "DIRECTLY_COMPARABLE", "Page is visible on both adjacent occurrences."),
            new("fontSizeToBodyRatio", true, "PARTIAL: font-size deltas, not the source ratio", "VISIBLE", "DIRECT: style.fontSizeToBodyRatio", "PAIRWISE_DELTA_DERIVABLE", "Raw ratio is visible; the H2-C request does not provide a precomputed edge delta."),
            new("boldRatio", true, "DIRECT: BoldRatioDelta", "VISIBLE", "DIRECT: style.boldRatio", "PAIRWISE_DELTA_DERIVABLE", "Occurrence values are visible; a transition value is not explicitly supplied."),
            new("italicRatio", true, "DIRECT: ItalicRatioDelta", "VISIBLE", "VISIBLE: style.italicRatio", "PAIRWISE_DELTA_DERIVABLE", "Same as bold ratio."),
            new("verticalPosition", true, "DIRECT: normalized top-position values/delta", "VISIBLE", "DIRECT: location.verticalPosition", "PAIRWISE_DELTA_DERIVABLE", "Prompt says not to use coordinates despite this supplied positional measurement."),
            new("left/right/width", true, "DERIVED: normalized left delta, overlap and width ratio", "VISIBLE", "ABSENT", "NOT_DERIVABLE_FROM_H2C_INPUT", "Production layout projection has horizontal box facts that the qualification projection drops."),
            new("sameParserLayoutBlock", true, "DIRECT: SameLayoutBlock", "VISIBLE: block identity", "ABSENT", "NOT_DERIVABLE_FROM_H2C_INPUT", "Audit cue: 3/8 internal edges versus 0/27 Gold exits; high precision, low recall on this development cohort."),
            new("verticalGapToNext", true, "DIRECT: normalized gap and local-median ratio", "AVAILABLE_FROM_VERTICAL_POSITIONS", "NOT_EXPLICIT; derivable from supplied positions with ambiguity", "DERIVABLE_BUT_NOT_EXPLICIT", "The broad coordinates prohibition makes intended use unclear."),
            new("deltaLeftOrIndent", true, "DIRECT: normalized left-edge delta", "AVAILABLE_FROM_HORIZONTAL_BOX_FACTS", "ABSENT", "NOT_DERIVABLE_FROM_H2C_INPUT", "H2-C sends no horizontal position."),
            new("deltaWidth", true, "DIRECT: overlap/width ratio", "AVAILABLE_FROM_HORIZONTAL_BOX_FACTS", "ABSENT", "NOT_DERIVABLE_FROM_H2C_INPUT", "H2-C sends no right edge or width."),
            new("pageTransition", true, "DIRECT: SamePage and adjacent page numbers", "VISIBLE", "DERIVABLE_FROM_PAGE", "DERIVABLE", "H2-C sends page for each occurrence."),
            new("richTypography", true, "PARTIAL: font identity and bold-evidence deltas", "VISIBLE: StyleFacts/Typography projection", "ABSENT: only six base style fields are projected", "NOT_DERIVABLE_FROM_H2C_INPUT", "Available source typography includes font-name/weight evidence and glyph statistics when present."),
            new("pairwiseTransitionFacts", true, "DIRECT: edge-derived feature rows", "DERIVABLE_FROM_LAYOUT_FACTS", "PARTIAL: page/font/bold/italic and vertical-position comparisons only", "PARTIAL", "The task is a boundary transition, but most horizontal/block edge measurements are missing."),
        };

        var artifacts = new
        {
            h2cPreflightSha256 = Hash(h2cBytes),
            sanitizedAuditSha256 = Hash(auditBytes),
            code = new
            {
                // Preserve the frozen audit's capture-time code hashes. Current implementation
                // ownership and observable projection are asserted above; no frozen artifact is rebased.
                pdfSourceEvidenceSha256 = frozenCodeHashes.GetProperty("pdfSourceEvidenceSha256").GetString(),
                productionLayoutAdapterSha256 = frozenCodeHashes.GetProperty("productionLayoutAdapterSha256").GetString(),
                structuredSourceBuilderSha256 = frozenCodeHashes.GetProperty("structuredSourceBuilderSha256").GetString(),
                h2cRequestBuilderSha256 = frozenCodeHashes.GetProperty("h2cRequestBuilderSha256").GetString(),
            },
        };

        FreezeArtifact.AssertJson(OutputRoot, "h2c-evidence-availability-matrix.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-availability-matrix-v1",
            status = "PROVIDER_FREE_REQUEST_PROJECTION_AUDIT",
            artifactAuthorities = artifacts,
            cohort = new
            {
                existingH2CRequests = requests.Count,
                existingH2CDevelopmentDocuments = requests.Select(value => value.Source.DocumentId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
                sanitizedEdgeRows = auditRoot.GetProperty("edges").GetArrayLength(),
                sameParserLayoutBlockInternal = sameBlockStats.internalTrue,
                sameParserLayoutBlockInternalN = sameBlockStats.internalN,
                sameParserLayoutBlockGoldExit = sameBlockStats.exitTrue,
                sameParserLayoutBlockGoldExitN = sameBlockStats.exitN,
                heldOutG2AFullPackAuthorityAvailable = false,
            },
            exactH2CRequestFields = new
            {
                occurrence = visibleFields.OrderBy(value => value, StringComparer.Ordinal),
                style = styleFields.OrderBy(value => value, StringComparer.Ordinal),
                location = locationFields.OrderBy(value => value, StringComparer.Ordinal),
            },
            evidenceAvailability = rows,
            promptAmbiguity = new
            {
                suppliedField = "location.verticalPosition",
                instructionPhrase = "Do not use ... coordinates ...",
                verdict = "AMBIGUOUS_INSTRUCTION_FOR_A_SUPPLIED_MEASURED_POSITIONAL_FACT",
            },
            interpretation = new
            {
                horizontalGeometryDroppedByH2C = true,
                sameLayoutBlockCueHighPrecisionLowRecallOnDevelopmentCohort = true,
                geometrySufficiencyNotProven = true,
                semanticRoleTransitionStillOpen = true,
                noNewTreatmentOrProviderRun = true,
            },
            safety = new
            {
                providerCalls = 0,
                canonicalGoldArtifactRead = false,
                frozenGoldRelationLabelsConsumed = true,
                goldRelationLabelSource = SanitizedAuditPath,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
        });
    }

    private static HashSet<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private static string ReadSource(string relativePath) => File.ReadAllText(TestRepository.Path(relativePath));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
