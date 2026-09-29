using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// V5's closed occurrence-function vocabulary. What an occurrence IS, never what it means to a
/// domain: no <c>LegalArticle</c>, <c>MeetingSection</c>, <c>AgendaItem</c>, <c>TOCEntry</c> or any
/// other domain-specific label is ever added here.
/// </summary>
public static class SemanticOccurrenceFunctions
{
    public const string DocumentIdentity = "DOCUMENT_IDENTITY";
    public const string RegionLabel = "REGION_LABEL";
    public const string NavigationReference = "NAVIGATION_REFERENCE";
    public const string PageFurniture = "PAGE_FURNITURE";
    public const string ObjectLabel = "OBJECT_LABEL";
    public const string TableStructure = "TABLE_STRUCTURE";
    public const string FootnoteOrSource = "FOOTNOTE_OR_SOURCE";
    public const string BodyInformation = "BODY_INFORMATION";
    public const string Metadata = "METADATA";

    public static readonly string[] All =
    [
        DocumentIdentity, RegionLabel, NavigationReference, PageFurniture, ObjectLabel,
        TableStructure, FootnoteOrSource, BodyInformation, Metadata,
    ];
}

/// <summary>
/// V5's closed relation vocabulary between two nodes of the same reply. No hierarchy level, no
/// numeric depth, no offsets: a relation says what two occurrences are to each other, and the
/// harness derives everything positional from validated edges.
/// </summary>
public static class SemanticRelationTypes
{
    /// <summary>The <c>to</c> node is a structural child of the <c>from</c> node.</summary>
    public const string ParentOf = "PARENT_OF";

    /// <summary>The <c>from</c> node points at or lists the <c>to</c> node; never a hierarchy edge.</summary>
    public const string References = "REFERENCES";

    /// <summary>The two nodes are the same real-world occurrence, addressed twice.</summary>
    public const string SameEntity = "SAME_ENTITY";

    /// <summary>The <c>to</c> node is a direct continuation of the <c>from</c> node.</summary>
    public const string Continues = "CONTINUES";

    public static readonly string[] All = [ParentOf, References, SameEntity, Continues];
}

/// <summary>
/// One graph node as a V5 reply proposed it. <see cref="NodeId"/> is request-local only - it has no
/// meaning outside the reply that used it, is not a source coordinate, and is never a persistent
/// canonical identity. Grounding is the existing structured source-parts vocabulary
/// (<see cref="SemanticSourcePart"/>), reused unchanged: sourceAlias, verbatimText, occurrence,
/// leftExactContext, rightExactContext. <see cref="Function"/> is the node's closed occurrence
/// function and nothing else - no boolean membership, no numeric level.
/// </summary>
public sealed record SemanticGraphNodeProposal(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("sourceParts")] IReadOnlyList<SemanticSourcePart> SourceParts,
    [property: JsonPropertyName("function")] string Function);

/// <summary>One typed edge between two request-local node ids of the same reply.</summary>
public sealed record SemanticGraphRelationProposal(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("fromNodeId")] string FromNodeId,
    [property: JsonPropertyName("toNodeId")] string ToNodeId);

/// <summary>One pack's V5 reply: every node it proposed and every relation it drew between them.</summary>
public sealed record SemanticRelationGraphProposal(
    [property: JsonPropertyName("nodes")] IReadOnlyList<SemanticGraphNodeProposal> Nodes,
    [property: JsonPropertyName("relations")] IReadOnlyList<SemanticGraphRelationProposal> Relations);

/// <summary>Either a decoded proposal, or the closed-contract issues that refused it.</summary>
public sealed record SemanticRelationGraphDecodeResult(
    SemanticRelationGraphProposal? Proposal,
    IReadOnlyList<SemanticContractIssue> Issues)
{
    public bool IsDecoded => Proposal is not null;
}

/// <summary>
/// The V5 semantic relation graph contract.
/// <para>
/// V5 replaces V4's flat "one closed function per claim" reply with an explicit graph: nodes carry
/// only occurrence function and exact source grounding; relations carry only a closed type between
/// two nodes of the same reply. There is no boolean membership, no model-authored hierarchy level, no
/// model-authored offset, page or box - every one of those is either absent from the schema or an
/// explicit rejection in <see cref="ValidateJson"/>, never silently dropped.
/// </para>
/// <para>
/// This contract does not derive <c>CanonicalSemanticProposal</c>, does not set <c>IsHeading</c>, and
/// is never routed through <c>SemanticCoordinateContract</c>'s proposal decoder. Reusing that seam
/// would make V5 an encoding of V4's authority path instead of a different one; this type is
/// deliberately self-contained.
/// </para>
/// </summary>
public static class SemanticRelationGraphContractV1
{
    public const string ProtocolVersion = "a99-semantic-relation-graph-v1";

    private static readonly HashSet<string> AllowedNodeFields =
        new(StringComparer.Ordinal) { "nodeId", "sourceParts", "function" };

    private static readonly HashSet<string> AllowedRelationFields =
        new(StringComparer.Ordinal) { "type", "fromNodeId", "toNodeId" };

    private static readonly HashSet<string> AllowedSourcePartFields =
        new(StringComparer.Ordinal)
        {
            "sourceAlias", "verbatimText", "occurrence", "leftExactContext", "rightExactContext",
        };

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            nodes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        nodeId = new { type = "string", minLength = 1 },
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
                        function = new { type = "string", @enum = SemanticOccurrenceFunctions.All },
                    },
                    required = new[] { "nodeId", "sourceParts", "function" },
                },
            },
            relations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        type = new { type = "string", @enum = SemanticRelationTypes.All },
                        fromNodeId = new { type = "string", minLength = 1 },
                        toNodeId = new { type = "string", minLength = 1 },
                    },
                    required = new[] { "type", "fromNodeId", "toNodeId" },
                },
            },
        },
        required = new[] { "nodes", "relations" },
    };

    public static string SchemaHash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Schema(), new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n"))));

    /// <summary>
    /// Fail-closed structural validation. Every rule here refuses the reply outright rather than
    /// repairing or ignoring the offending part - a V5 reply is accepted whole or not at all.
    /// </summary>
    public static IReadOnlyList<SemanticContractIssue> ValidateJson(JsonElement payload)
    {
        var issues = new List<SemanticContractIssue>();
        if (payload.ValueKind != JsonValueKind.Object)
            return [new("PAYLOAD_NOT_AN_OBJECT", null, "A V5 reply must be a JSON object.")];

        foreach (var field in payload.EnumerateObject())
            if (field.Name is not ("nodes" or "relations"))
                issues.Add(new("FIELD_NOT_IN_CONTRACT", field.Name, "V5 has only nodes and relations."));

        if (!payload.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            issues.Add(new("MISSING_NODES", null, "A V5 reply must carry a nodes array, even if empty."));
        if (!payload.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            issues.Add(new("MISSING_RELATIONS", null, "A V5 reply must carry a relations array, even if empty."));
        if (issues.Count > 0) return issues;

        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var declaredNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeOrdinal = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            nodeOrdinal++;
            var tag = $"node[{nodeOrdinal}]";
            if (node.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("MALFORMED_NODE", tag, "A node entry must be an object."));
                continue;
            }
            foreach (var field in node.EnumerateObject())
                if (!AllowedNodeFields.Contains(field.Name))
                    issues.Add(new(ForbiddenOrUnknownNodeField(field.Name), $"{tag}.{field.Name}",
                        "A node carries only nodeId, sourceParts and function."));

            string? nodeId = null;
            if (!node.TryGetProperty("nodeId", out var nodeIdElement) ||
                nodeIdElement.ValueKind != JsonValueKind.String || nodeIdElement.GetString() is not { Length: > 0 } id)
                issues.Add(new("MISSING_NODE_ID", tag, "A node must carry a non-empty nodeId."));
            else
            {
                nodeId = id;
                declaredNodeIds.Add(id);
                if (!seenNodeIds.Add(id))
                    issues.Add(new("DUPLICATE_NODE_ID", id, "The same nodeId was declared more than once."));
            }

            if (!node.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.String ||
                !SemanticOccurrenceFunctions.All.Contains(function.GetString(), StringComparer.Ordinal))
                issues.Add(new("UNKNOWN_FUNCTION", nodeId ?? tag, "function must be a closed V5 occurrence function."));

            if (!node.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array ||
                parts.GetArrayLength() == 0)
            {
                issues.Add(new("MISSING_SOURCE_PARTS", nodeId ?? tag, "A node must name at least one source part."));
                continue;
            }
            var partOrdinal = 0;
            foreach (var part in parts.EnumerateArray())
            {
                partOrdinal++;
                if (part.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new("MALFORMED_SOURCE_PART", $"{nodeId ?? tag}[{partOrdinal}]", "A source part must be an object."));
                    continue;
                }
                foreach (var field in part.EnumerateObject())
                    if (!AllowedSourcePartFields.Contains(field.Name))
                        issues.Add(new(ForbiddenOrUnknownPartField(field.Name), $"{nodeId ?? tag}[{partOrdinal}].{field.Name}",
                            "A source part carries only sourceAlias, verbatimText, occurrence, leftExactContext, rightExactContext."));
                if (!part.TryGetProperty("sourceAlias", out var alias) || alias.ValueKind != JsonValueKind.String ||
                    alias.GetString() is not { Length: > 0 })
                    issues.Add(new("MISSING_SOURCE_ALIAS", $"{nodeId ?? tag}[{partOrdinal}]", "A source part must name a non-empty sourceAlias."));
            }
        }

        var relationOrdinal = 0;
        foreach (var relation in relations.EnumerateArray())
        {
            relationOrdinal++;
            var tag = $"relation[{relationOrdinal}]";
            if (relation.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("MALFORMED_RELATION", tag, "A relation entry must be an object."));
                continue;
            }
            foreach (var field in relation.EnumerateObject())
                if (!AllowedRelationFields.Contains(field.Name))
                    issues.Add(new("FIELD_NOT_IN_CONTRACT", $"{tag}.{field.Name}",
                        "A relation carries only type, fromNodeId and toNodeId."));

            string? type = relation.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() : null;
            if (type is null || !SemanticRelationTypes.All.Contains(type, StringComparer.Ordinal))
                issues.Add(new("UNKNOWN_RELATION_TYPE", tag, "type must be a closed V5 relation type."));

            var from = TextOrNull(relation, "fromNodeId");
            var to = TextOrNull(relation, "toNodeId");
            if (from is not { Length: > 0 }) issues.Add(new("MISSING_RELATION_ENDPOINT", $"{tag}.fromNodeId", "A relation must name fromNodeId."));
            if (to is not { Length: > 0 }) issues.Add(new("MISSING_RELATION_ENDPOINT", $"{tag}.toNodeId", "A relation must name toNodeId."));
            if (from is { Length: > 0 } && to is { Length: > 0 })
            {
                if (string.Equals(from, to, StringComparison.Ordinal))
                    issues.Add(new("SELF_RELATION", tag, $"'{from}' cannot be related to itself."));
                if (!declaredNodeIds.Contains(from))
                    issues.Add(new("UNDEFINED_RELATION_ENDPOINT", $"{tag}.fromNodeId", $"'{from}' does not name a node in this reply."));
                if (!declaredNodeIds.Contains(to))
                    issues.Add(new("UNDEFINED_RELATION_ENDPOINT", $"{tag}.toNodeId", $"'{to}' does not name a node in this reply."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Decodes a validated reply into request-local proposal objects. Source parts are decoded with
    /// <see cref="SemanticSourcePartCanonicalizer.PendingSelectionMode"/>, exactly like the V2/V4
    /// decoders, so the selection mode is still the harness's to derive, never the model's to state.
    /// </summary>
    public static SemanticRelationGraphDecodeResult Decode(JsonElement payload)
    {
        var issues = ValidateJson(payload);
        if (issues.Count > 0) return new(null, issues);

        var nodes = payload.GetProperty("nodes").EnumerateArray()
            .Select(node => new SemanticGraphNodeProposal(
                node.GetProperty("nodeId").GetString()!,
                node.GetProperty("sourceParts").EnumerateArray().Select(DecodePart).ToArray(),
                node.GetProperty("function").GetString()!))
            .ToArray();
        var relations = payload.GetProperty("relations").EnumerateArray()
            .Select(relation => new SemanticGraphRelationProposal(
                relation.GetProperty("type").GetString()!,
                relation.GetProperty("fromNodeId").GetString()!,
                relation.GetProperty("toNodeId").GetString()!))
            .ToArray();
        return new(new SemanticRelationGraphProposal(nodes, relations), []);
    }

    private static SemanticSourcePart DecodePart(JsonElement part) => new(
        part.GetProperty("sourceAlias").GetString()!,
        SemanticSourcePartCanonicalizer.PendingSelectionMode,
        TextOrNull(part, "verbatimText"),
        part.TryGetProperty("occurrence", out var occurrence) && occurrence.ValueKind == JsonValueKind.Number
            ? occurrence.GetInt32() : null,
        TextOrNull(part, "leftExactContext"),
        TextOrNull(part, "rightExactContext"));

    private static string? TextOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Names the field-specific code for a field V5 never accepts on a node, so the offending name
    /// - <c>isHeading</c>, <c>level</c>, <c>page</c>, <c>start</c>/<c>end</c>, <c>box</c> - is
    /// visible in the issue itself rather than folded into one generic rejection.
    /// </summary>
    private static string ForbiddenOrUnknownNodeField(string name) => name switch
    {
        "isHeading" or "membership" or "level" or "semanticRole" or "relationHints" => "FORBIDDEN_V4_FIELD",
        "start" or "end" or "page" or "box" or "offset" or "offsets" or "coordinates" => "FORBIDDEN_COORDINATE_FIELD",
        _ => "FIELD_NOT_IN_CONTRACT",
    };

    private static string ForbiddenOrUnknownPartField(string name) => name switch
    {
        "selectionMode" => "FORBIDDEN_HARNESS_DERIVED_FIELD",
        "start" or "end" or "page" or "box" or "offset" or "offsets" or "coordinates" => "FORBIDDEN_COORDINATE_FIELD",
        _ => "FIELD_NOT_IN_CONTRACT",
    };
}

/// <summary>
/// V5's coordinate teaching. Source grounding is unchanged from V2/V4 (sourceAlias, verbatimText,
/// occurrence, leftExactContext, rightExactContext); what is new is the split between what an
/// occurrence IS (function) and what it is TO another occurrence (a typed relation).
/// </summary>
public static class SemanticRelationGraphPromptClauseV1
{
    public const string Text = """

        Return a graph, not a flat list. Every node is one occurrence you name by sourceAlias (and,
        when you mean only part of it, verbatimText copied character for character); nodeId is a
        label for THIS reply only, never a source coordinate and never remembered afterward.

        Three separate questions, never conflated:
        1. Occurrence function - what this occurrence IS (DOCUMENT_IDENTITY, REGION_LABEL,
           NAVIGATION_REFERENCE, PAGE_FURNITURE, OBJECT_LABEL, TABLE_STRUCTURE, FOOTNOTE_OR_SOURCE,
           BODY_INFORMATION, METADATA). Exactly one, on the node itself.
        2. Semantic relation - what this occurrence is TO another occurrence in this same reply
           (PARENT_OF, REFERENCES, SAME_ENTITY, CONTINUES). A relation is a separate object naming
           two nodeIds; it never changes either node's function.
        3. Source grounding - where the text physically is. Always sourceAlias/verbatimText/
           occurrence/context, never an offset, a page, a box or a coordinate you computed.

        A NAVIGATION_REFERENCE occurrence (a table of contents line, an index entry, a cross
        reference) may REFERENCES a REGION_LABEL occurrence elsewhere. That REFERENCES edge does not
        make the navigation occurrence itself a REGION_LABEL - it stays NAVIGATION_REFERENCE even
        when its displayed text is identical to the region it points at. Two occurrences are the same
        entity only when you assert SAME_ENTITY or CONTINUES; identical wording alone is never enough
        - two different sections can share a title.

        Never return isHeading, membership, a numeric level, an offset, a page number or a box. The
        harness derives hierarchy depth from the PARENT_OF edges you return; you never state a level.
        """;
}
