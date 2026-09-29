using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Experimental V4 response contract. The model writes one closed primary semantic function; the
/// runtime derives membership from it and never accepts an independent membership field.
/// </summary>
public static class SemanticFunctionMembershipContractV1
{
    public const string ProtocolVersion = "a99-semantic-function-membership-v1";

    public static readonly string[] Functions =
    [
        "DOCUMENT_IDENTITY", "REGION_STRUCTURE", "NAVIGATION", "PAGE_FURNITURE", "OBJECT_CAPTION",
        "TABLE_STRUCTURE", "FOOTNOTE_OR_SOURCE", "BODY_INFORMATION", "METADATA",
    ];

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        sourceParts = new
                        {
                            type = "array",
                            minItems = 1,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    sourceAlias = new { type = "string", minLength = 1 },
                                    verbatimText = new { type = "string" },
                                    occurrence = new { type = "integer", minimum = 1 },
                                    leftExactContext = new { type = "string" },
                                    rightExactContext = new { type = "string" },
                                },
                                required = new[] { "sourceAlias" },
                            },
                        },
                        semanticFunction = new { type = "string", @enum = Functions },
                    },
                    required = new[] { "sourceParts", "semanticFunction" },
                },
            },
        },
        required = new[] { "headings" },
    };

    public static string SchemaHash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Schema(), new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n"))));

    public static bool IsMember(string function) => function is "DOCUMENT_IDENTITY" or "REGION_STRUCTURE";

    public static IReadOnlyList<SemanticContractIssue> ValidateJson(JsonElement payload)
    {
        var issues = new List<SemanticContractIssue>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("headings", out var headings) ||
            headings.ValueKind != JsonValueKind.Array)
            return [new("MISSING_HEADINGS", null, "A semantic-function reply must carry a headings array.")];

        var ordinal = 0;
        foreach (var entry in headings.EnumerateArray())
        {
            ordinal++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("MALFORMED_ENTRY", ordinal.ToString(), "An entry must be an object."));
                continue;
            }
            foreach (var field in entry.EnumerateObject())
                if (field.Name is not ("sourceParts" or "semanticFunction"))
                    issues.Add(new("FIELD_NOT_IN_CONTRACT", field.Name, "V4 has only sourceParts and semanticFunction."));
            if (!entry.TryGetProperty("semanticFunction", out var function) || function.ValueKind != JsonValueKind.String ||
                !Functions.Contains(function.GetString(), StringComparer.Ordinal))
                issues.Add(new("UNKNOWN_SEMANTIC_FUNCTION", ordinal.ToString(), "semanticFunction must be a closed V4 value."));
            if (!entry.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
                issues.Add(new("MISSING_SOURCE_PARTS", ordinal.ToString(), "An entry must name at least one source part."));
        }
        return issues;
    }

    public static SemanticProposalDecodeResult Decode(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("semanticFunction", out var function) ||
            function.ValueKind != JsonValueKind.String || !Functions.Contains(function.GetString(), StringComparer.Ordinal) ||
            !entry.TryGetProperty("sourceParts", out var parts))
            return new([], [new("SEMANTIC_FUNCTION_ENTRY_UNREADABLE", "An entry must carry a closed semanticFunction and sourceParts.")]);

        // Delegate coordinate parsing to the established V2 source-parts decoder. The synthetic
        // boolean is harness-derived from the function and never appears in V4's schema or on wire.
        using var translated = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            isHeading = IsMember(function.GetString()!),
            sourceParts = JsonSerializer.Deserialize<JsonElement>(parts.GetRawText()),
        }));
        var decoded = SemanticSourcePartsV2.Decode(translated.RootElement);
        return new SemanticProposalDecodeResult(
            decoded.Proposals.Select(proposal => proposal with { SemanticRole = function.GetString()! }).ToArray(),
            decoded.Failures);
    }

    public static readonly SemanticCoordinateBinding Binding =
        SemanticSourcePartsV2.Binding with { BindingId = "SEMANTIC_FUNCTION_MEMBERSHIP_V1" };
}

/// <summary>V4's coordinate instruction: source grounding is unchanged, terminology is not.</summary>
public static class PdfSemanticFunctionMembershipPromptClause
{
    public const string Text = """

        Name each classified occurrence by the sourceAlias it occupies, in reading order. When it is
        only part of one occurrence, give verbatimText copied character for character. If that text
        repeats, add its 1-based occurrence or exact adjacent context. A classification that wraps
        across occurrences uses ordered sourceParts. Never return offsets, spans, pages, boxes,
        coordinates, generated text, or any field not present in the schema.
        """;
}
