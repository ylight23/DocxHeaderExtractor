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
        if (!File.Exists(TestRepository.Path(InputPath)))
        {
            // The source audit carries source text and is local work in progress, not committed. Without it the committed
            // sanitized derivative is the authority and is verified on its own terms - it is never skipped.
            AssertSanitizedDerivativeOnItsOwn();
            return;
        }

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

    private static void AssertSanitizedDerivativeOnItsOwn()
    {
        var text = File.ReadAllText(TestRepository.Path(OutputRoot + "/h2c-clean-boundary-separability-sanitized.v1.json"));
        Assert.DoesNotContain("LeftText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RightText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutBlockId", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("v5-p6th2c-clean-boundary-separability-sanitized-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("PROVIDER_FREE_NUMERIC_AUDIT_SOURCE_TEXT_REMOVED", root.GetProperty("status").GetString());
        Assert.Equal(InputPath, root.GetProperty("sourceArtifact").GetProperty("path").GetString());
        Assert.Equal(64, root.GetProperty("sourceArtifact").GetProperty("sha256").GetString()!.Length);

        var authority = root.GetProperty("sourceAuthority").EnumerateArray().ToArray();
        Assert.Equal(5, authority.Length);
        Assert.All(authority, value =>
        {
            foreach (var field in new[] { "sourceSha256", "sourceAliasUniverseSha256", "modelVisibleEvidenceSha256" })
                Assert.Matches("^[0-9a-f]{64}$", value.GetProperty(field).GetString()!);
            Assert.True(value.GetProperty("atoms").GetInt32() > 0);
            Assert.True(value.GetProperty("geometryAvailableForEveryAtom").GetBoolean());
        });

        var edges = root.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(89, edges.Length);
        Assert.Equal(8, edges.Count(value => value.GetProperty("GoldRelationClass").GetString() == "INTERNAL_CONTINUE"));
        Assert.Equal(27, edges.Count(value => value.GetProperty("GoldRelationClass").GetString() == "GOLD_EXIT"));
        Assert.Equal(27, edges.Count(value => value.GetProperty("GoldRelationClass").GetString() == "POST_EXIT_1"));
        Assert.Equal(27, edges.Count(value => value.GetProperty("GoldRelationClass").GetString() == "POST_EXIT_2"));
        Assert.All(edges, value =>
        {
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("LeftAlias").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("RightAlias").GetString()));
        });

        var privacy = root.GetProperty("privacy");
        Assert.False(privacy.GetProperty("sourceTextIncluded").GetBoolean());
        Assert.False(privacy.GetProperty("rawProviderResponsesIncluded").GetBoolean());
        Assert.False(privacy.GetProperty("GoldMutated").GetBoolean());
        Assert.Equal(0, privacy.GetProperty("providerCalls").GetInt32());
        Assert.False(privacy.GetProperty("runtimeChanged").GetBoolean());
    }

    private static double? ReadNullableDouble(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null ? null : element.GetDouble();

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
