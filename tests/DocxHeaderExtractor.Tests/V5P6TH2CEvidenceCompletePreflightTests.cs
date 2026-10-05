using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes paired H2-C current-vs-evidence-complete request projections without provider or canonical-Gold access.</summary>
public sealed class V5P6TH2CEvidenceCompletePreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string OutputRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const string CaptureRoot = Root + "/p6th2c-evidence-complete-capture-20261005";
    private const string ExecutionConfirmationVariable = "P6TH2C_EVIDENCE_PAIRED_EXECUTION_CONFIRMATION";
    private const string ExecutionConfirmation = "yes-i-authorize-p6th2c-evidence-paired-sixty-two-primary-calls-no-retry";
    private const int ResponseByteCap = 49_152;
    internal const string ProtocolVersion = "v5-function-conditioned-exact-end-pointer-evidence-pair-1";
    internal const string CoordinateClarification = """
        Use source text and all supplied neutral measured physical, geometry, layout, and style facts. Do not output coordinates or parser layout-block IDs; they are read-only evidence, not response handles. Do not infer or output unsupplied coordinates. Do not use hierarchy labels, candidate alternatives, relations, source aliases, rationale, confidence, or unissued evidence.
        """;

    private sealed record SourceAuthority(string DocumentId, string SourceSha256, string SourceUniverseSha256,
        string ModelVisibleEvidenceSha256, int AtomCount, int LayoutBlockCount, int PageCount);
    private sealed record ArmRequest(string DocumentId, string PackId, string Anchor, string SourceSha256,
        string SourceUniverseSha256, string UserMessageSha256, int UserBytes, string ProviderBodySha256,
        int ProviderBodyBytes, int OccurrenceCount, IReadOnlyList<string> OccurrenceHandles,
        int GeometryFactRows, int RichTypographyRows, int TransitionFactRows,
        string SystemPrompt, string UserMessage, byte[] ProviderBody, int MaxCompletionTokens);
    private sealed record PlannedCall(string Arm, ArmRequest Request);
    internal sealed record EvidenceArmRequest(string DocumentId, string PackId, string Anchor, string SourceSha256,
        string SourceUniverseSha256, string UserMessageSha256, string SystemPrompt, string UserMessage,
        byte[] ProviderBody, string ProviderBodySha256, int ProviderBodyBytes, int MaxCompletionTokens,
        IReadOnlyList<string> OccurrenceHandles);

    /// <summary>Rebuilds the frozen Arm-A carrier exactly, with the shared coordinate-output clarification.</summary>
    internal static EvidenceArmRequest BuildArmARequest(P6TH2CEndPointerCanary.Request request)
    {
        using var baseUser = JsonDocument.Parse(request.UserMessage);
        var user = RewriteProtocolVersion(JsonNode.Parse(baseUser.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("h2c-evidence-current-user-invalid"));
        var userMessage = user.ToJsonString(CanonicalJsonOptions);
        var body = BuildBody(BuildEvidencePrompt(), userMessage, request.MaxCompletionTokens);
        return new EvidenceArmRequest(request.Source.DocumentId, request.PackId, request.Anchor,
            request.SourceSha256, request.SourceUniverseSha256, Hash(userMessage), BuildEvidencePrompt(), userMessage,
            body.PayloadBytes, body.Hash, body.Bytes, request.MaxCompletionTokens, request.IssuedOccurrences);
    }

    /// <summary>
    /// Rebuilds Arm A and projects only the pre-existing rich typography object.  In particular it does not
    /// project geometry, parser layout-block identity, gaps, or an engineered typography-transition feature.
    /// </summary>
    internal static EvidenceArmRequest BuildTypographyOnlyRequest(string repo, P6TH2CEndPointerCanary.Request request)
    {
        using var baseUser = JsonDocument.Parse(request.UserMessage);
        var typographyUser = RewriteProtocolVersion(JsonNode.Parse(baseUser.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("h2c-typography-only-current-user-invalid"));
        var occurrences = typographyUser["anchors"]!.AsArray()[0]!["occurrences"]!.AsArray();
        var pdfPath = TestRepository.Path(request.Source.PdfPath);
        IReadOnlyList<PdfLine> lines;
        using (var pdf = PdfDocument.Open(pdfPath)) lines = PdfLineExtraction.ExtractLines(pdf);
        var sourceSha = CanonicalSemanticSourceHash.Compute(pdfPath);
        Assert.Equal(request.SourceSha256, sourceSha);
        var authority = PdfStructuredSourceAuthorityBuilder.Build(lines, sourceSha);
        Assert.Equal(request.SourceUniverseSha256, authority.SourceAliasUniverseHash);
        var aliases = BuildF1OccurrenceAliasMap(repo, request.Source, request.PackId, sourceSha, request.SourceUniverseSha256);
        var start = Array.FindIndex(authority.Atoms.ToArray(), atom => atom.Alias == request.AnchorAlias);
        Assert.True(start >= 0, $"h2c-typography-only-anchor-alias-missing:{request.Source.DocumentId}:{request.Anchor}");
        Assert.Equal(request.IssuedOccurrences.Count, occurrences.Count);
        for (var index = 0; index < occurrences.Count; index++)
        {
            var atomIndex = start + index;
            Assert.True(atomIndex < authority.Atoms.Count, $"h2c-typography-only-tail-out-of-range:{request.Source.DocumentId}:{request.Anchor}");
            var occurrence = occurrences[index]!.AsObject();
            var atom = authority.Atoms[atomIndex];
            Assert.Equal(request.IssuedOccurrences[index], occurrence["occurrence"]!.GetValue<string>());
            Assert.Equal(atom.Alias, aliases[request.IssuedOccurrences[index]]);
            Assert.Equal(atom.Page, occurrence["page"]!.GetValue<int>());
            Assert.Equal(atom.Text, occurrence["text"]!.GetValue<string>());
            Assert.False(occurrence.ContainsKey("geometry"));
            Assert.False(occurrence.ContainsKey("parserLayoutBlockId"));
            Assert.False(occurrence.ContainsKey("transitionFromPrevious"));
            var typography = TypographyFacts(authority.Contexts[atom.SourceId].Source.Typography);
            Assert.NotNull(typography);
            occurrence["typography"] = JsonSerializer.SerializeToNode(typography);
        }

        var userMessage = typographyUser.ToJsonString(CanonicalJsonOptions);
        var body = BuildBody(BuildEvidencePrompt(), userMessage, request.MaxCompletionTokens);
        return new EvidenceArmRequest(request.Source.DocumentId, request.PackId, request.Anchor,
            request.SourceSha256, request.SourceUniverseSha256, Hash(userMessage), BuildEvidencePrompt(), userMessage,
            body.PayloadBytes, body.Hash, body.Bytes, request.MaxCompletionTokens, request.IssuedOccurrences);
    }

    internal static string BuildEvidencePrompt() =>
        P6TH2CCleanPairedBoundaryTreatment.V2SemanticBoundaryInstruction + "\n\n" +
        P6TH2CCleanPairedBoundaryTreatment.SharedContractInstruction.Replace(
            "Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.",
            CoordinateClarification.Trim(), StringComparison.Ordinal);

    internal static EvidenceArmRequest BuildArmBRequest(string repo, P6TH2CEndPointerCanary.Request request)
    {
        var prompt = BuildEvidencePrompt();
        using var baseUser = JsonDocument.Parse(request.UserMessage);
        var completeUser = RewriteProtocolVersion(JsonNode.Parse(baseUser.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("h2c-evidence-current-user-invalid"));
        var occurrences = completeUser["anchors"]!.AsArray()[0]!["occurrences"]!.AsArray();
        var pdfPath = TestRepository.Path(request.Source.PdfPath);
        IReadOnlyList<PdfLine> lines;
        Dictionary<int, (double Width, double Height)> pageSizes;
        using (var pdf = PdfDocument.Open(pdfPath))
        {
            lines = PdfLineExtraction.ExtractLines(pdf);
            pageSizes = pdf.GetPages().ToDictionary(page => page.Number, page => (page.Width, page.Height));
        }
        var sourceSha = CanonicalSemanticSourceHash.Compute(pdfPath);
        Assert.Equal(request.SourceSha256, sourceSha);
        var authority = PdfStructuredSourceAuthorityBuilder.Build(lines, sourceSha);
        Assert.Equal(request.SourceUniverseSha256, authority.SourceAliasUniverseHash);
        var gaps = BuildPageMedianGaps(authority);
        var aliases = BuildF1OccurrenceAliasMap(repo, request.Source, request.PackId, sourceSha, request.SourceUniverseSha256);
        var start = Array.FindIndex(authority.Atoms.ToArray(), atom => atom.Alias == request.AnchorAlias);
        Assert.True(start >= 0, $"h2c-evidence-anchor-alias-missing:{request.Source.DocumentId}:{request.Anchor}");
        Assert.Equal(request.IssuedOccurrences.Count, occurrences.Count);
        for (var index = 0; index < occurrences.Count; index++)
        {
            var atomIndex = start + index;
            Assert.True(atomIndex < authority.Atoms.Count, $"h2c-evidence-tail-out-of-range:{request.Source.DocumentId}:{request.Anchor}");
            var occurrence = occurrences[index]!.AsObject();
            var atom = authority.Atoms[atomIndex];
            Assert.Equal(request.IssuedOccurrences[index], occurrence["occurrence"]!.GetValue<string>());
            Assert.Equal(atom.Alias, aliases[request.IssuedOccurrences[index]]);
            Assert.Equal(atom.Page, occurrence["page"]!.GetValue<int>());
            Assert.Equal(atom.Text, occurrence["text"]!.GetValue<string>());
            var source = authority.Contexts[atom.SourceId].Source;
            var page = pageSizes[atom.Page];
            var width = Math.Max(0, source.Right - source.Left);
            var geometry = new
            {
                left = Round(source.Left, 3), right = Round(source.Right, 3), width = Round(width, 3),
                pageWidth = Round(page.Width, 3), pageHeight = Round(page.Height, 3),
                leftNormalized = Ratio(source.Left, page.Width), rightNormalized = Ratio(source.Right, page.Width),
                widthNormalized = Ratio(width, page.Width), topY = Round(source.TopY, 3), bottomY = Round(source.BottomY, 3),
                topFromPageBottomNormalized = Ratio(source.TopY, page.Height), bottomFromPageBottomNormalized = Ratio(source.BottomY, page.Height),
                verticalPosition = source.VerticalPosition is { } vertical ? Round(vertical, 6) : (double?)null,
            };
            object? transition = null;
            if (index > 0)
            {
                var previous = authority.Contexts[authority.Atoms[atomIndex - 1].SourceId].Source;
                var gapPoints = previous.Page == source.Page ? previous.BottomY - source.TopY : (double?)null;
                var scale = Math.Max(previous.FontSize, source.FontSize);
                var pageMedian = gaps.GetValueOrDefault(source.Page);
                transition = new
                {
                    gapPoints = gapPoints is { } gap ? Round(gap, 3) : (double?)null,
                    gapInFontSizes = gapPoints is { } normalizedGap && scale > 0 ? Round(normalizedGap / scale, 4) : (double?)null,
                    pageMedianGapPoints = pageMedian is { } median ? Round(median, 3) : (double?)null,
                    gapOverPageMedian = gapPoints is { } currentGap && pageMedian is > 0 ? Round(currentGap / pageMedian.Value, 4) : (double?)null,
                };
            }
            var blockId = authority.LayoutBlockByAtom.GetValueOrDefault(atom.SourceId);
            Assert.False(string.IsNullOrWhiteSpace(blockId));
            occurrence["geometry"] = JsonSerializer.SerializeToNode(geometry);
            occurrence["parserLayoutBlockId"] = blockId;
            occurrence["typography"] = JsonSerializer.SerializeToNode(TypographyFacts(source.Typography));
            occurrence["transitionFromPrevious"] = JsonSerializer.SerializeToNode(transition);
        }
        var userMessage = completeUser.ToJsonString(CanonicalJsonOptions);
        var body = BuildBody(prompt, userMessage, request.MaxCompletionTokens);
        return new EvidenceArmRequest(request.Source.DocumentId, request.PackId, request.Anchor,
            request.SourceSha256, request.SourceUniverseSha256, Hash(userMessage), prompt, userMessage,
            body.PayloadBytes, body.Hash, body.Bytes, request.MaxCompletionTokens, request.IssuedOccurrences);
    }

    [Fact]
    public async Task H2C_evidence_complete_pair_freezes_projection_only_delta_and_corrects_coordinate_wording()
    {
        var repo = TestRepository.Root();
        var sourceRequests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        Assert.Equal(31, sourceRequests.Count);
        var prompt = BuildEvidencePrompt();
        Assert.Contains("Do not output coordinates", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not use hierarchy, candidate alternatives, relations, coordinates", prompt, StringComparison.Ordinal);

        var authorityByDocument = new Dictionary<string, PdfStructuredSourceAuthority>(StringComparer.Ordinal);
        var pageSizesByDocument = new Dictionary<string, Dictionary<int, (double Width, double Height)>>(StringComparer.Ordinal);
        var pageMedianGapByDocument = new Dictionary<string, Dictionary<int, double?>>(StringComparer.Ordinal);
        var aliasByOccurrenceByDocument = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sourceAuthorityRows = new List<SourceAuthority>();
        foreach (var request in sourceRequests.GroupBy(value => value.Source.DocumentId, StringComparer.Ordinal).Select(group => group.First()))
        {
            var pdfPath = TestRepository.Path(request.Source.PdfPath);
            IReadOnlyList<PdfLine> lines;
            Dictionary<int, (double Width, double Height)> pageSizes;
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                lines = PdfLineExtraction.ExtractLines(pdf);
                pageSizes = pdf.GetPages().ToDictionary(page => page.Number, page => (page.Width, page.Height));
            }
            var sourceSha = CanonicalSemanticSourceHash.Compute(pdfPath);
            Assert.Equal(request.SourceSha256, sourceSha);
            var authority = PdfStructuredSourceAuthorityBuilder.Build(lines, sourceSha);
            Assert.Equal(request.SourceUniverseSha256, authority.SourceAliasUniverseHash);
            authorityByDocument.Add(request.Source.DocumentId, authority);
            pageSizesByDocument.Add(request.Source.DocumentId, pageSizes);
            pageMedianGapByDocument.Add(request.Source.DocumentId, BuildPageMedianGaps(authority));
            aliasByOccurrenceByDocument.Add(request.Source.DocumentId,
                BuildF1OccurrenceAliasMap(repo, request.Source, request.PackId, sourceSha, request.SourceUniverseSha256));
            sourceAuthorityRows.Add(new SourceAuthority(request.Source.DocumentId, sourceSha,
                authority.SourceAliasUniverseHash, authority.ModelVisibleEvidenceHash, authority.Atoms.Count,
                authority.LayoutBlockByAtom.Values.Distinct(StringComparer.Ordinal).Count(), pageSizes.Count));
        }

        var armA = new List<ArmRequest>();
        var armB = new List<ArmRequest>();
        foreach (var request in sourceRequests)
        {
            using var baseUser = JsonDocument.Parse(request.UserMessage);
            var currentRoot = JsonNode.Parse(baseUser.RootElement.GetRawText())?.AsObject()
                ?? throw new InvalidDataException("h2c-evidence-current-user-invalid");
            var currentUser = RewriteProtocolVersion(currentRoot.DeepClone().AsObject());
            var completeUser = RewriteProtocolVersion(currentRoot.DeepClone().AsObject());
            var occurrencesA = currentUser["anchors"]!.AsArray()[0]!["occurrences"]!.AsArray();
            var occurrencesB = completeUser["anchors"]!.AsArray()[0]!["occurrences"]!.AsArray();
            Assert.Equal(occurrencesA.Count, occurrencesB.Count);

            var authority = authorityByDocument[request.Source.DocumentId];
            var pageSizes = pageSizesByDocument[request.Source.DocumentId];
            var start = Array.FindIndex(authority.Atoms.ToArray(), atom => atom.Alias == request.AnchorAlias);
            Assert.True(start >= 0, $"h2c-evidence-anchor-alias-missing:{request.Source.DocumentId}:{request.Anchor}");
            Assert.Equal(request.IssuedOccurrences.Count, occurrencesB.Count);
            var transitionRows = 0;
            var typographyRows = 0;
            for (var index = 0; index < occurrencesB.Count; index++)
            {
                var occurrenceA = occurrencesA[index]!.AsObject();
                var occurrenceB = occurrencesB[index]!.AsObject();
                var atomIndex = start + index;
                Assert.True(atomIndex < authority.Atoms.Count, $"h2c-evidence-tail-out-of-range:{request.Source.DocumentId}:{request.Anchor}");
                var atom = authority.Atoms[atomIndex];
                var id = occurrenceB["occurrence"]!.GetValue<string>();
                Assert.Equal(request.IssuedOccurrences[index], id);
                Assert.Equal(id, occurrenceA["occurrence"]!.GetValue<string>());
                if (index == 0) Assert.Equal(request.Anchor, id);
                Assert.Equal(atom.Alias, aliasByOccurrenceByDocument[request.Source.DocumentId][id]);
                Assert.Equal(atom.Page, occurrenceB["page"]!.GetValue<int>());
                Assert.Equal(atom.Text, occurrenceB["text"]!.GetValue<string>());
                Assert.Equal(atom.Page, occurrenceA["page"]!.GetValue<int>());
                Assert.Equal(atom.Text, occurrenceA["text"]!.GetValue<string>());
                Assert.Equal(occurrenceA["style"]!.ToJsonString(CanonicalJsonOptions), occurrenceB["style"]!.ToJsonString(CanonicalJsonOptions));
                Assert.Equal(occurrenceA["location"]!.ToJsonString(CanonicalJsonOptions), occurrenceB["location"]!.ToJsonString(CanonicalJsonOptions));

                var source = authority.Contexts[atom.SourceId].Source;
                var page = pageSizes[atom.Page];
                var width = Math.Max(0, source.Right - source.Left);
                var geometry = new
                {
                    left = Round(source.Left, 3),
                    right = Round(source.Right, 3),
                    width = Round(width, 3),
                    pageWidth = Round(page.Width, 3),
                    pageHeight = Round(page.Height, 3),
                    leftNormalized = Ratio(source.Left, page.Width),
                    rightNormalized = Ratio(source.Right, page.Width),
                    widthNormalized = Ratio(width, page.Width),
                    topY = Round(source.TopY, 3),
                    bottomY = Round(source.BottomY, 3),
                    topFromPageBottomNormalized = Ratio(source.TopY, page.Height),
                    bottomFromPageBottomNormalized = Ratio(source.BottomY, page.Height),
                    verticalPosition = source.VerticalPosition is { } vertical ? Round(vertical, 6) : (double?)null,
                };
                var typography = TypographyFacts(source.Typography);
                if (typography is not null) typographyRows++;

                object? transition = null;
                if (index > 0)
                {
                    var previous = authority.Contexts[authority.Atoms[atomIndex - 1].SourceId].Source;
                    var gapPoints = previous.Page == source.Page ? previous.BottomY - source.TopY : (double?)null;
                    var scale = Math.Max(previous.FontSize, source.FontSize);
                    var pageMedian = pageMedianGapByDocument[request.Source.DocumentId].GetValueOrDefault(source.Page);
                    transition = new
                    {
                        gapPoints = gapPoints is { } gap ? Round(gap, 3) : (double?)null,
                        gapInFontSizes = gapPoints is { } normalizedGap && scale > 0 ? Round(normalizedGap / scale, 4) : (double?)null,
                        pageMedianGapPoints = pageMedian is { } median ? Round(median, 3) : (double?)null,
                        gapOverPageMedian = gapPoints is { } currentGap && pageMedian is > 0 ? Round(currentGap / pageMedian.Value, 4) : (double?)null,
                    };
                    transitionRows++;
                }

                var blockId = authority.LayoutBlockByAtom.GetValueOrDefault(atom.SourceId);
                Assert.False(string.IsNullOrWhiteSpace(blockId));
                occurrenceB["geometry"] = JsonSerializer.SerializeToNode(geometry);
                occurrenceB["parserLayoutBlockId"] = blockId;
                occurrenceB["typography"] = JsonSerializer.SerializeToNode(typography);
                occurrenceB["transitionFromPrevious"] = JsonSerializer.SerializeToNode(transition);
            }

            var userA = currentUser.ToJsonString(CanonicalJsonOptions);
            var userB = completeUser.ToJsonString(CanonicalJsonOptions);
            var bodyA = BuildBody(prompt, userA, request.MaxCompletionTokens);
            var bodyB = BuildBody(prompt, userB, request.MaxCompletionTokens);
            Assert.NotEqual(Hash(userA), Hash(userB));
            Assert.Equal(request.IssuedOccurrences.Count - 1, transitionRows);
            armA.Add(new ArmRequest(request.Source.DocumentId, request.PackId, request.Anchor, request.SourceSha256,
                request.SourceUniverseSha256, Hash(userA), Encoding.UTF8.GetByteCount(userA), bodyA.Hash,
                bodyA.Bytes, occurrencesA.Count, request.IssuedOccurrences, 0, 0, 0,
                prompt, userA, bodyA.PayloadBytes, request.MaxCompletionTokens));
            armB.Add(new ArmRequest(request.Source.DocumentId, request.PackId, request.Anchor, request.SourceSha256,
                request.SourceUniverseSha256, Hash(userB), Encoding.UTF8.GetByteCount(userB), bodyB.Hash,
                bodyB.Bytes, occurrencesB.Count, request.IssuedOccurrences, occurrencesB.Count, typographyRows, transitionRows,
                prompt, userB, bodyB.PayloadBytes, request.MaxCompletionTokens));
        }

        Assert.Equal(31, armA.Count);
        Assert.Equal(31, armB.Count);
        Assert.True(armA.Zip(armB).All(pair => pair.First.DocumentId == pair.Second.DocumentId &&
            pair.First.PackId == pair.Second.PackId && pair.First.Anchor == pair.Second.Anchor &&
            pair.First.OccurrenceHandles.SequenceEqual(pair.Second.OccurrenceHandles)));
        var preflightPath = TestRepository.Path(Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json");
        var sanitizedAuditPath = TestRepository.Path(Root + "/p6th2c-clean-boundary-separability-audit/h2c-clean-boundary-separability-sanitized.v1.json");
        var productionLayoutPath = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfHeadingMembershipProductionAdapter.cs");
        var sourceEvidencePath = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfSourceEvidence.cs");
        var sourceBuilderPath = TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfStructuredSourceAuthorityBuilder.cs");

        FreezeArtifact.AssertJson(OutputRoot, "h2c-evidence-complete-preflight.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-complete-preflight-v1",
            status = "DEVELOPMENT_COHORT_PREFLIGHT_FROZEN_PROVIDER_NOT_AUTHORIZED_GOLD_NOT_READ",
            experiment = new
            {
                question = "Does exposing the already available neutral PDF geometry/layout/typography evidence improve direct end-pointer decisions?",
                onlyArmDifference = "MODEL_VISIBLE_EVIDENCE_PROJECTION",
                historicalV2Parity = "ARM_A_REUSES_HISTORICAL_V2_EVIDENCE_FIELDS_BUT_BOTH_ARMS_USE_THE_SAME_COORDINATE-OUTPUT_CLARIFICATION_AND_NEW_SHARED_PROTOCOL_VERSION; NOT_BYTE_IDENTICAL_TO_HISTORICAL_V2",
                developmentOnly = true,
                heldOutQualification = "NOT_ESTABLISHED",
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                temperature = 0,
                route = "OPENROUTER_ALIBABA_PINNED",
                protocolVersion = ProtocolVersion,
                horizon = "SAME_COMPLETE_OWNED_TAIL_FROM_FROZEN_G2A_HAS_ANCHOR",
                outputSchema = "UNCHANGED_H2C_EXACT_END_POINTER_WITH_FIRST_OUTSIDE_ROLE",
                responseFields = new[] { "anchor", "headingMembers", "endOccurrence", "firstOutsideOccurrence", "firstOutsideRole" },
                allowedFirstOutsideRoles = new[] { "NEW_HEADING", "BODY_CONTENT", "PAGE_FURNITURE", "TABLE_OR_STRUCTURED_CONTENT", "OTHER_NON_HEADING", "NO_VISIBLE_SUCCESSOR" },
                prompt = "V2 semantics plus coordinate-output clarification, identical in both arms",
                sharedPromptSha256 = Hash(Encoding.UTF8.GetBytes(prompt)),
                coordinateRule = "Use supplied measured geometry; never output coordinates or parser layout-block IDs; only issued occurrence handles are response identifiers.",
                armA = "CURRENT_H2C_OCCURRENCE_PROJECTION",
                armB = "CURRENT_H2C_PLUS_NEUTRAL_GEOMETRY_BLOCK_ID_RICH_TYPOGRAPHY_AND_ADJACENT_GAP_FACTS",
                armBFields = new[] { "geometry.left", "geometry.right", "geometry.width", "geometry.pageWidth", "geometry.pageHeight", "geometry.leftNormalized", "geometry.rightNormalized", "geometry.widthNormalized", "geometry.topY", "geometry.bottomY", "parserLayoutBlockId", "typography", "transitionFromPrevious.gapPoints", "transitionFromPrevious.pageMedianGapPoints" },
                structuralScope = "NOT_EXPOSED",
                markerSemanticLabels = "NOT_ADDED",
            },
            authorities = new
            {
                currentH2CPreflightSha256 = Hash(File.ReadAllBytes(preflightPath)),
                sanitizedBoundaryAuditSha256 = Hash(File.ReadAllBytes(sanitizedAuditPath)),
                productionLayoutAdapterSha256 = Hash(File.ReadAllBytes(productionLayoutPath)),
                sourceEvidenceBuilderSha256 = Hash(File.ReadAllBytes(sourceEvidencePath)),
                structuredSourceBuilderSha256 = Hash(File.ReadAllBytes(sourceBuilderPath)),
                sourceAuthorities = sourceAuthorityRows,
            },
            cohort = new
            {
                requestCountPerArm = 31,
                documents = armA.Select(row => row.DocumentId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
                anchorsDerivedOnlyFromExistingFrozenG2A = true,
                goldBasedSelection = false,
                armABytes = new { maxUser = armA.Max(row => row.UserBytes), maxBody = armA.Max(row => row.ProviderBodyBytes) },
                armBBytes = new { maxUser = armB.Max(row => row.UserBytes), maxBody = armB.Max(row => row.ProviderBodyBytes) },
            },
            requests = armA.Zip(armB).Select(pair => new
            {
                documentId = pair.First.DocumentId,
                packId = pair.First.PackId,
                anchor = pair.First.Anchor,
                sourceSha256 = pair.First.SourceSha256,
                sourceUniverseSha256 = pair.First.SourceUniverseSha256,
                occurrences = pair.First.OccurrenceCount,
                occurrenceHandlesSha256 = Hash(string.Join("\n", pair.First.OccurrenceHandles) + "\n"),
                armA = new { userMessageSha256 = pair.First.UserMessageSha256, userBytes = pair.First.UserBytes, providerBodySha256 = pair.First.ProviderBodySha256, providerBodyBytes = pair.First.ProviderBodyBytes },
                armB = new { userMessageSha256 = pair.Second.UserMessageSha256, userBytes = pair.Second.UserBytes, providerBodySha256 = pair.Second.ProviderBodySha256, providerBodyBytes = pair.Second.ProviderBodyBytes, geometryFactRows = pair.Second.GeometryFactRows, richTypographyRows = pair.Second.RichTypographyRows, transitionFactRows = pair.Second.TransitionFactRows },
            }).ToArray(),
            safety = new
            {
                providerCalls = 0,
                canonicalGoldArtifactRead = false,
                goldMutation = "NONE",
                sharedRuntimeChanged = false,
                retry = 0,
                repair = false,
                fallback = false,
            },
        });

        var plannedCalls = armA.Zip(armB).SelectMany(pair => new[]
        {
            new PlannedCall("A", pair.First),
            new PlannedCall("B", pair.Second),
        }).ToArray();
        var executionPlan = plannedCalls.Select((item, index) => new
        {
            callOrdinal = index + 1,
            item.Arm,
            item.Request.DocumentId,
            item.Request.PackId,
            item.Request.Anchor,
            item.Request.SourceSha256,
            item.Request.SourceUniverseSha256,
            userMessageSha256 = item.Request.UserMessageSha256,
            systemPromptSha256 = Hash(item.Request.SystemPrompt),
            providerBodySha256 = item.Request.ProviderBodySha256,
            providerBodyBytes = item.Request.ProviderBodyBytes,
            item.Request.MaxCompletionTokens,
            occurrenceHandlesSha256 = Hash(string.Join("\n", item.Request.OccurrenceHandles) + "\n"),
        }).ToArray();
        FreezeArtifact.AssertJson(OutputRoot, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p6th2c-evidence-paired-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED_PROVIDER_CALLS_ZERO_GOLD_CLOSED",
            preflightSha256 = Hash(File.ReadAllBytes(Path.Combine(TestRepository.Root(), OutputRoot.Replace('/', Path.DirectorySeparatorChar), "h2c-evidence-complete-preflight.v1.json"))),
            executionOrder = "INTERLEAVED_A_THEN_B_PER_FROZEN_REQUEST_ROW",
            primaryCalls = 62,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            requests = executionPlan,
        });

        if (Environment.GetEnvironmentVariable(ExecutionConfirmationVariable) == ExecutionConfirmation)
            await ExecuteAuthorizedPairAsync(plannedCalls).ConfigureAwait(false);
    }

    private static JsonObject RewriteProtocolVersion(JsonObject root)
    {
        root["protocolVersion"] = ProtocolVersion;
        return root;
    }

    private static Dictionary<string, string> BuildF1OccurrenceAliasMap(string repo,
        P6TH2CEndPointerCanary.Source source, string packId, string sourceSha256, string sourceUniverseSha256)
    {
        const string snapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
        var snapshotPath = Path.Combine(repo, snapshotRoot.Replace('/', Path.DirectorySeparatorChar), sourceSha256 + ".json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, source.DocumentId);
        Assert.Equal(sourceSha256, plan.SourceSha256);
        Assert.Equal(sourceUniverseSha256, plan.SourceUniverseSha256);
        var pack = plan.Packs.Single(value => value.PackId == packId);
        var f1Path = TestRepository.Path(Root + "/" + source.F1Path);
        using var f1Capture = JsonDocument.Parse(File.ReadAllBytes(f1Path));
        var f1Row = source.Kind switch
        {
            P6TH2CEndPointerCanary.F1Kind.RawCapture => f1Capture.RootElement,
            P6TH2CEndPointerCanary.F1Kind.ResultRow => f1Capture.RootElement.GetProperty("row"),
            P6TH2CEndPointerCanary.F1Kind.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray()
                .Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("h2c-evidence-f1-kind-invalid"),
        };
        Assert.Equal(packId, f1Row.GetProperty("packId").GetString());
        var correspondences = source.F1UsedCorrespondences
            ? BuildCorrespondences(pack)
            : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
        var expectedRequestHash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash)
            ? semanticHash.GetString()
            : f1Row.GetProperty("userMessageSha256").GetString();
        Assert.Equal(expectedRequestHash, prepared.Request.UserMessageSha256);
        var rawResponse = f1Row.GetProperty("rawResponse").GetString()!;
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(prepared, rawResponse);
        Assert.Equal(prepared.Request.Occurrences.Count, parsed.Decisions.Count);
        return prepared.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(
        PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var rows = result.TryGetValue(key, out var existing) ? existing.ToList() : [];
            if (!rows.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                rows.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = rows;
        }
        return result;
    }

    private static (string Hash, int Bytes, byte[] PayloadBytes) BuildBody(string systemPrompt, string userMessage, int maxCompletionTokens)
    {
        var request = new V5FreeHeadingRequestV1(ProtocolVersion, systemPrompt, userMessage,
            Hash(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, maxCompletionTokens);
        return (body.Hash, body.Bytes, body.PayloadBytes);
    }

    private static async Task ExecuteAuthorizedPairAsync(IReadOnlyList<PlannedCall> calls)
    {
        if (calls.Count != 62) throw new InvalidOperationException("h2c-evidence-authorized-call-count-not-62");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            throw new InvalidOperationException("h2c-evidence-openrouter-api-key-missing-provider-calls-zero");

        var repo = TestRepository.Root();
        var capturePath = Path.Combine(repo, CaptureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(capturePath)) throw new InvalidOperationException("h2c-evidence-capture-directory-exists-stop-before-network");
        var manifestPath = Path.Combine(repo, OutputRoot.Replace('/', Path.DirectorySeparatorChar), "execution-manifest.v1.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-evidence-paired-execution-reservation-v1",
            status = "AUTHORIZED_EXECUTION_RESERVED_RAW_CAPTURE_IN_PROGRESS_GOLD_CLOSED",
            executionManifestSha256 = Hash(manifestBytes),
            authorizedCalls = 62,
            providerCallsAlreadySent = 0,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            reentryPolicy = "ANY_EXISTING_CAPTURE_DIRECTORY_STOPS_BEFORE_NETWORK",
        });

        var rows = new List<object>(calls.Count);
        var callOrdinal = 0;
        foreach (var call in calls)
        {
            callOrdinal++;
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(call.Request.ProviderBody,
                    call.Request.MaxCompletionTokens, call.Request.SystemPrompt, call.Request.UserMessage).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            stopwatch.Stop();

            var contractError = observation is null ? "NO_RESPONSE" :
                observation.FinishReason != "stop" ? "FINISH_REASON_NOT_STOP" :
                TryParseEndPointer(observation.Content, call.Request.OccurrenceHandles, call.Request.Anchor) ? null : "INVALID_END_POINTER_LEDGER";
            var armDirectory = Path.Combine(capturePath, call.Arm == "A" ? "arm-a" : "arm-b");
            Directory.CreateDirectory(armDirectory);
            var rawPath = Path.Combine(armDirectory, $"{call.Request.DocumentId}_{call.Request.Anchor}.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th2c-evidence-paired-raw-capture-v1",
                arm = call.Arm,
                providerCallOrdinal = callOrdinal,
                call.Request.DocumentId,
                call.Request.PackId,
                call.Request.Anchor,
                call.Request.SourceSha256,
                call.Request.SourceUniverseSha256,
                systemPromptSha256 = Hash(call.Request.SystemPrompt),
                userMessageSha256 = call.Request.UserMessageSha256,
                providerBodySha256 = call.Request.ProviderBodySha256,
                providerBodyBytes = call.Request.ProviderBodyBytes,
                occurrenceHandlesSha256 = Hash(string.Join("\n", call.Request.OccurrenceHandles) + "\n"),
                occurrenceCount = call.Request.OccurrenceCount,
                reasoningRequested = true,
                reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                completionTokens = Usage(observation?.Usage, "completion_tokens"),
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                contractStatus = contractError is null ? "VALID" : contractError,
                contractError,
                transportError,
                rawSse = observation?.RawSse,
                rawResponse = observation?.Content,
                goldReadDuringCapture = false,
            });

            rows.Add(new
            {
                arm = call.Arm,
                callOrdinal,
                call.Request.DocumentId,
                call.Request.PackId,
                call.Request.Anchor,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                contractStatus = contractError is null ? "VALID" : contractError,
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                transportError,
            });
            File.WriteAllText(Path.Combine(capturePath, "progress.v1.json"),
                JsonSerializer.Serialize(new { schemaVersion = "v5-p6th2c-evidence-paired-progress-v1", callsAttempted = callOrdinal, maxCalls = 62, goldRead = false, rows }, FreezeArtifact.Json),
                new UTF8Encoding(false));
            Console.WriteLine($"[{callOrdinal}/62] Arm {call.Arm} {call.Request.DocumentId}/{call.Request.Anchor}: {observation?.FinishReason ?? "ERROR"}; {contractError ?? "contract-valid"}");
        }

        WriteNew(Path.Combine(capturePath, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-evidence-paired-result-v1",
            status = "ALL_PRIMARY_ATTEMPTS_RAW_FROZEN_GOLD_NOT_READ",
            primaryRequestsPerArm = 31,
            providerCallsAttempted = callOrdinal,
            maxAuthorizedProviderCalls = 62,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            rows,
        });
    }

    internal static bool TryParseEndPointer(string raw, IReadOnlyList<string> issuedOccurrences, string anchor)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(raw) > ResponseByteCap) return false;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) return false;
            var decision = decisions[0];
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 5 ||
                !decision.TryGetProperty("anchor", out var anchorValue) || anchorValue.ValueKind != JsonValueKind.String || anchorValue.GetString() != anchor ||
                !decision.TryGetProperty("headingMembers", out var membersValue) || membersValue.ValueKind != JsonValueKind.Array ||
                !decision.TryGetProperty("endOccurrence", out var endValue) || endValue.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("firstOutsideOccurrence", out var outsideValue) ||
                !decision.TryGetProperty("firstOutsideRole", out var roleValue) || roleValue.ValueKind != JsonValueKind.String) return false;
            var members = membersValue.EnumerateArray().ToArray();
            if (members.Length == 0 || members.Length > issuedOccurrences.Count || members.Any(value => value.ValueKind != JsonValueKind.String)) return false;
            var ids = members.Select(value => value.GetString()!).ToArray();
            if (!ids.SequenceEqual(issuedOccurrences.Take(ids.Length), StringComparer.Ordinal) || ids[0] != anchor || endValue.GetString() != ids[^1]) return false;
            var role = roleValue.GetString();
            if (ids.Length == issuedOccurrences.Count)
                return outsideValue.ValueKind == JsonValueKind.Null && role == "NO_VISIBLE_SUCCESSOR";
            return outsideValue.ValueKind == JsonValueKind.String && outsideValue.GetString() == issuedOccurrences[ids.Length] &&
                role is "NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var key in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json));
        writer.WriteLine();
    }

    private static object? TypographyFacts(PdfLineTypography? typography)
    {
        if (typography is null) return null;
        var glyphs = typography.Glyphs;
        return new
        {
            version = typography.Version.ToString(),
            nominalFontSize = Round(typography.NominalFontSize, 3),
            effectivePointSize = Round(typography.EffectivePointSize, 3),
            fontBoldFlagRatio = Round(typography.FontBoldFlagRatio, 5),
            fontNameBoldRatio = Round(typography.FontNameBoldRatio, 5),
            derivedBoldRatio = Round(typography.DerivedBoldRatio, 5),
            fontName = typography.FontName,
            boldEvidenceSource = typography.BoldEvidenceSource,
            glyphStatistics = glyphs is null ? null : new
            {
                dominantPointSize = Round(glyphs.DominantPointSize, 3),
                medianPointSize = Round(glyphs.MedianPointSize, 3),
                minPointSize = Round(glyphs.MinPointSize, 3),
                maxPointSize = Round(glyphs.MaxPointSize, 3),
                dominantFontName = glyphs.DominantFontName,
                boldGlyphRatio = Round(glyphs.BoldGlyphRatio, 5),
                italicGlyphRatio = Round(glyphs.ItalicGlyphRatio, 5),
            },
        };
    }

    private static Dictionary<int, double?> BuildPageMedianGaps(PdfStructuredSourceAuthority authority)
    {
        return authority.Atoms.Select(atom => authority.Contexts[atom.SourceId].Source)
            .GroupBy(source => source.Page)
            .ToDictionary(group => group.Key, group =>
            {
                var pageRows = group.OrderByDescending(source => source.TopY).ToArray();
                var positiveGaps = Enumerable.Range(1, Math.Max(0, pageRows.Length - 1))
                    .Select(index => pageRows[index - 1].BottomY - pageRows[index].TopY)
                    .Where(value => value >= 0).OrderBy(value => value).ToArray();
                if (positiveGaps.Length == 0) return (double?)null;
                var middle = positiveGaps.Length / 2;
                return positiveGaps.Length % 2 == 0
                    ? (positiveGaps[middle - 1] + positiveGaps[middle]) / 2
                    : positiveGaps[middle];
            });
    }

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
    private static double? Ratio(double value, double divisor) => divisor > 0 ? Round(value / divisor, 6) : null;
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
