using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Produces a source-text-free derivative of the provider-free boundary feature audit.</summary>
public sealed class V5P6TH2CCleanBoundarySanitizedAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string InputPath = Root + "/p6th2c-clean-boundary-separability-audit/h2c-clean-boundary-separability-audit.v1.json";
    private const string OutputRoot = Root + "/p6th2c-clean-boundary-separability-audit";

    private sealed record SanitizedEdge(
        string DocumentId, string Anchor, string GoldRelationClass, string LeftAlias, string RightAlias,
        int LeftPage, int RightPage, bool SamePage, int LeftLayoutBlockLineCount, int RightLayoutBlockLineCount,
        bool SameParserLayoutBlock, bool SameCapitalizationPattern, bool SameFontName,
        double FontSizeDelta, double BoldRatioDelta, double ItalicRatioDelta,
        double? FontBoldFlagRatioDelta, double? FontNameBoldRatioDelta,
        double? VerticalGapInFontSizes, double? GapOverLocalMedian,
        double NormalizedLeftEdgeDelta, double HorizontalOverlapRatio, double NormalizedCenterDelta,
        double WidthRatio, double LeftCenteredness, double RightCenteredness,
        double LeftTopPosition, double RightTopPosition, double CenterednessChange,
        double NormalizedTopPositionDelta, double LeftPageWidth, double RightPageWidth,
        double LeftPageHeight, double RightPageHeight);

    [Fact]
    public void Sanitized_boundary_audit_preserves_numeric_evidence_and_hash_authority_without_source_text()
    {
        var inputBytes = File.ReadAllBytes(TestRepository.Path(InputPath));
        using var input = JsonDocument.Parse(inputBytes);
        var root = input.RootElement;
        var sourceAuthority = root.GetProperty("sourceAuthority").EnumerateArray().Select(value => new
        {
            documentId = value.GetProperty("documentId").GetString(),
            sourceSha256 = value.GetProperty("sourceSha256").GetString(),
            sourceAliasUniverseSha256 = value.GetProperty("sourceAliasUniverseSha256").GetString(),
            modelVisibleEvidenceSha256 = value.GetProperty("modelVisibleEvidenceSha256").GetString(),
            parserLines = value.GetProperty("parserLines").GetInt32(),
            atoms = value.GetProperty("atoms").GetInt32(),
            layoutBlocks = value.GetProperty("layoutBlocks").GetInt32(),
            pages = value.GetProperty("pages").GetInt32(),
            geometryAvailableForEveryAtom = value.GetProperty("geometryAvailableForEveryAtom").GetBoolean(),
        }).ToArray();

        var edges = root.GetProperty("edges").EnumerateArray().Select(value => new SanitizedEdge(
            value.GetProperty("DocumentId").GetString()!,
            value.GetProperty("Anchor").GetString()!,
            value.GetProperty("EdgeClass").GetString()!,
            value.GetProperty("LeftAlias").GetString()!,
            value.GetProperty("RightAlias").GetString()!,
            value.GetProperty("LeftPage").GetInt32(),
            value.GetProperty("RightPage").GetInt32(),
            value.GetProperty("SamePage").GetBoolean(),
            value.GetProperty("LeftLayoutBlockLineCount").GetInt32(),
            value.GetProperty("RightLayoutBlockLineCount").GetInt32(),
            value.GetProperty("SameLayoutBlock").GetBoolean(),
            value.GetProperty("SameCapitalizationPattern").GetBoolean(),
            value.GetProperty("SameFontName").GetBoolean(),
            value.GetProperty("FontSizeDelta").GetDouble(),
            value.GetProperty("BoldRatioDelta").GetDouble(),
            value.GetProperty("ItalicRatioDelta").GetDouble(),
            ReadNullableDouble(value, "FontBoldFlagRatioDelta"),
            ReadNullableDouble(value, "FontNameBoldRatioDelta"),
            ReadNullableDouble(value, "VerticalGapInFontSizes"),
            ReadNullableDouble(value, "GapOverLocalMedian"),
            value.GetProperty("NormalizedLeftEdgeDelta").GetDouble(),
            value.GetProperty("HorizontalOverlapRatio").GetDouble(),
            value.GetProperty("NormalizedCenterDelta").GetDouble(),
            value.GetProperty("WidthRatio").GetDouble(),
            value.GetProperty("LeftCenteredness").GetDouble(),
            value.GetProperty("RightCenteredness").GetDouble(),
            value.GetProperty("LeftTopPosition").GetDouble(),
            value.GetProperty("RightTopPosition").GetDouble(),
            value.GetProperty("CenterednessChange").GetDouble(),
            value.GetProperty("NormalizedTopPositionDelta").GetDouble(),
            value.GetProperty("LeftPageWidth").GetDouble(),
            value.GetProperty("RightPageWidth").GetDouble(),
            value.GetProperty("LeftPageHeight").GetDouble(),
            value.GetProperty("RightPageHeight").GetDouble())).ToArray();

        Assert.Equal(89, edges.Length);
        Assert.Equal(8, edges.Count(value => value.GoldRelationClass == "INTERNAL_CONTINUE"));
        Assert.Equal(27, edges.Count(value => value.GoldRelationClass == "GOLD_EXIT"));
        Assert.Equal(27, edges.Count(value => value.GoldRelationClass == "POST_EXIT_1"));
        Assert.Equal(27, edges.Count(value => value.GoldRelationClass == "POST_EXIT_2"));
        Assert.All(edges, value =>
        {
            Assert.False(string.IsNullOrWhiteSpace(value.LeftAlias));
            Assert.False(string.IsNullOrWhiteSpace(value.RightAlias));
        });

        var sanitized = JsonSerializer.Serialize(new
        {
            schemaVersion = "v5-p6th2c-clean-boundary-separability-sanitized-v1",
            status = "PROVIDER_FREE_NUMERIC_AUDIT_SOURCE_TEXT_REMOVED",
            sourceArtifact = new
            {
                path = InputPath,
                sha256 = Hash(inputBytes),
            },
            sourceAuthority,
            cohort = root.GetProperty("cohort").Clone(),
            featureSeparation = root.GetProperty("featureSeparation").Clone(),
            edges,
            interpretation = root.GetProperty("interpretation").Clone(),
            privacy = new
            {
                sourceTextIncluded = false,
                rawProviderResponsesIncluded = false,
                GoldMutated = false,
                providerCalls = 0,
                runtimeChanged = false,
            },
        });

        Assert.DoesNotContain("LeftText", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("RightText", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutBlockId", sanitized, StringComparison.Ordinal);
        using var sanitizedDocument = JsonDocument.Parse(sanitized);
        Assert.Equal(89, sanitizedDocument.RootElement.GetProperty("edges").GetArrayLength());
        FreezeArtifact.AssertJson(OutputRoot, "h2c-clean-boundary-separability-sanitized.v1.json", sanitizedDocument.RootElement);
    }

    private static double? ReadNullableDouble(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null ? null : element.GetDouble();

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
