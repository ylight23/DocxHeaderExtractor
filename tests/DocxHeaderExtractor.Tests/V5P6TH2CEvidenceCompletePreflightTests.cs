using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.V5Qualification;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes paired H2-C current-vs-evidence-complete request projections without provider or canonical-Gold access.</summary>
public sealed class V5P6TH2CEvidenceCompletePreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string OutputRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const int ResponseByteCap = 49_152;
    private const string ProtocolVersion = "v5-function-conditioned-exact-end-pointer-evidence-pair-1";
    private const string CoordinateClarification = """
        Use source text and all supplied neutral measured physical, geometry, layout, and style facts. Do not output coordinates or parser layout-block IDs; they are read-only evidence, not response handles. Do not infer or output unsupplied coordinates. Do not use hierarchy labels, candidate alternatives, relations, source aliases, rationale, confidence, or unissued evidence.
        """;

    private sealed record SourceAuthority(string DocumentId, string SourceSha256, string SourceUniverseSha256,
        string ModelVisibleEvidenceSha256, int AtomCount, int LayoutBlockCount, int PageCount);
    private sealed record ArmRequest(string DocumentId, string PackId, string Anchor, string SourceSha256,
        string SourceUniverseSha256, string UserMessageSha256, int UserBytes, string ProviderBodySha256,
        int ProviderBodyBytes, int OccurrenceCount, IReadOnlyList<string> OccurrenceHandles,
        int GeometryFactRows, int RichTypographyRows, int TransitionFactRows);

    [Fact]
    public void H2C_evidence_complete_pair_freezes_projection_only_delta_and_corrects_coordinate_wording()
    {
        var repo = TestRepository.Root();
        var sourceRequests = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        Assert.Equal(31, sourceRequests.Count);
        var prompt = P6TH2CCleanPairedBoundaryTreatment.V2SemanticBoundaryInstruction + "\n\n" +
                     P6TH2CCleanPairedBoundaryTreatment.SharedContractInstruction.Replace(
                         "Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.",
                         CoordinateClarification.Trim(), StringComparison.Ordinal);
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
                bodyA.Bytes, occurrencesA.Count, request.IssuedOccurrences, 0, 0, 0));
            armB.Add(new ArmRequest(request.Source.DocumentId, request.PackId, request.Anchor, request.SourceSha256,
                request.SourceUniverseSha256, Hash(userB), Encoding.UTF8.GetByteCount(userB), bodyB.Hash,
                bodyB.Bytes, occurrencesB.Count, request.IssuedOccurrences, occurrencesB.Count, typographyRows, transitionRows));
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

    private static (string Hash, int Bytes) BuildBody(string systemPrompt, string userMessage, int maxCompletionTokens)
    {
        var request = new V5FreeHeadingRequestV1(ProtocolVersion, systemPrompt, userMessage,
            Hash(userMessage), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, maxCompletionTokens);
        return (body.Hash, body.Bytes);
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
