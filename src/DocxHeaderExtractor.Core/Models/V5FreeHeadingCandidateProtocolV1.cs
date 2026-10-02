using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Qualification-only, ontology-free heading membership request. Never used by the live runtime.</summary>
public sealed record V5FreeHeadingRequestV1(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    string UserMessageSha256,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes);

/// <summary>Source-backed free-heading membership protocol for the P6N ceiling experiment.</summary>
public static class V5FreeHeadingCandidateProtocolV1
{
    public const string Version = "v5-free-reasoning-heading-membership-canonical-locator-1";
    public const string UnschematizedVersion = "v5-free-reasoning-heading-membership-unconstrained-output-1";
    public const string BoundLocatorVersion = "v5-free-reasoning-heading-membership-source-parts-locator-1";
    public const string PdfDocumentAwareBoundLocatorVersion = "v5-free-reasoning-heading-membership-pdf-document-context-locator-1";

    public const string UnschematizedSystemPrompt = """
        You are reading a document represented by source occurrences in document order. Identify the occurrences that you judge to function as headings in this document. Use the document context and the observable source evidence provided. Return the headings you judge to be present in the source, using whatever response format and schema you prefer. Do not use any external answer key.
        """;

    public const string BoundLocatorSystemPrompt = """
        You are reading a document from source occurrences in document order. Identify the occurrences that you judge to function as headings in this document. Use the document context and the observable source evidence provided. Return only headings you judge to be present in the source. Do not use any external answer key.

        Return one JSON object with exactly this shape: {"headings":[{"sourceParts":[{"atom":"A17"}]}]}.
        Each heading has exactly one property, sourceParts. sourceParts is a non-empty ordered array of source locators: the first part is the primary occurrence and later parts are ordered continuations of that same heading. Every part uses only an issued owned atom handle and optional from/to boundary handles. A whole atom is exactly {"atom":"A17"}, with no from/to. A strict proper substring is exactly {"atom":"A17","from":"H123","to":"H145"}; never emit a full-span boundary pair. Additional atoms must be owned and strictly increasing in source order. Select only opaque handles in ownedSubjects; contextOnlyEvidence is reasoning-only and is never selectable. Output no semantic function, heading level, type, reason, confidence, hierarchy, or other property.
        """;

    // Deliberately contains no task ontology, examples, or heading/non-heading heuristics.
    public const string SystemPrompt = """
        You are reading a document from source occurrences in document order. Identify the occurrences that you judge to function as headings in this document. Use the document context and the observable source evidence provided. Return only headings you judge to be present in the source. Do not invent, rewrite, normalize, or merge text except through the supplied locator contract. Do not infer from any external answer key.

        Return one JSON object with exactly this shape: {"headings":[{"locator":{"primary":{"atom":"A17"},"additionalParts":[]}}]}.
        Each heading has exactly one property, locator. Locator has exactly primary and additionalParts. Each part is either a whole atom {"atom":"A17"} with no from/to properties, or a strict proper substring {"atom":"A17","from":"H123","to":"H145"}. Never emit a full-span boundary pair. Additional parts, when present, must be owned atoms in increasing source order after primary. Select only issued opaque handles from ownedSubjects; contextOnlyEvidence is for reasoning and is never selectable. Return no other properties or commentary.
        """;

    private static readonly JsonSerializerOptions JsonOptions = CanonicalJson.Options;

    /// <summary>
    /// Builds P6N using the exact P6M source/context/directory payload, while dropping its task
    /// description, ontology, semantic-function list, and ontology-bearing response contract.
    /// </summary>
    public static V5FreeHeadingRequestV1 Compose(V5SparseCandidateModelRequestV1 p6mCanonicalRequest)
    {
        ArgumentNullException.ThrowIfNull(p6mCanonicalRequest);
        using var source = JsonDocument.Parse(p6mCanonicalRequest.UserMessage);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ownedSubjects", out var owned) || owned.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("contextOnlyEvidence", out var context) || context.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6n-source-evidence-envelope-invalid");

        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            ownedSubjects = owned,
            contextOnlyEvidence = context,
        }, JsonOptions);
        return new V5FreeHeadingRequestV1(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
    }

    /// <summary>Composes the same owned/context source evidence but imposes no output schema or locator grammar.</summary>
    public static V5FreeHeadingRequestV1 ComposeUnschematized(V5SparseCandidateModelRequestV1 p6mCanonicalRequest)
    {
        ArgumentNullException.ThrowIfNull(p6mCanonicalRequest);
        using var source = JsonDocument.Parse(p6mCanonicalRequest.UserMessage);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ownedSubjects", out var owned) || owned.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("contextOnlyEvidence", out var context) || context.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6n-source-evidence-envelope-invalid");

        var user = JsonSerializer.Serialize(new { ownedSubjects = owned, contextOnlyEvidence = context }, JsonOptions);
        return new V5FreeHeadingRequestV1(UnschematizedVersion, UnschematizedSystemPrompt, user,
            Hashing.Sha256(user), Encoding.UTF8.GetByteCount(UnschematizedSystemPrompt), Encoding.UTF8.GetByteCount(user));
    }

    /// <summary>Composes free semantic judgement with the minimum sourceParts locator grammar.</summary>
    public static V5FreeHeadingRequestV1 ComposeBoundLocator(V5SparseCandidateModelRequestV1 p6mCanonicalRequest)
        => ComposeBoundLocatorCore(p6mCanonicalRequest, null, BoundLocatorVersion);

    /// <summary>Composes the P6N-B wire plus a neutral, non-selectable PDF-wide context envelope.</summary>
    public static V5FreeHeadingRequestV1 ComposePdfDocumentAwareBoundLocator(
        V5SparseCandidateModelRequestV1 p6mCanonicalRequest, JsonElement documentContext)
    {
        if (documentContext.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("PDF document context must be a JSON object.", nameof(documentContext));
        return ComposeBoundLocatorCore(p6mCanonicalRequest, documentContext, PdfDocumentAwareBoundLocatorVersion);
    }

    private static V5FreeHeadingRequestV1 ComposeBoundLocatorCore(
        V5SparseCandidateModelRequestV1 p6mCanonicalRequest, JsonElement? documentContext, string protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(p6mCanonicalRequest);
        using var source = JsonDocument.Parse(p6mCanonicalRequest.UserMessage);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ownedSubjects", out var owned) || owned.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("contextOnlyEvidence", out var context) || context.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6n-source-evidence-envelope-invalid");

        var userObject = documentContext is { } contextElement
            ? new { protocolVersion, ownedSubjects = owned, contextOnlyEvidence = context, documentContext = (object)contextElement }
            : new { protocolVersion, ownedSubjects = owned, contextOnlyEvidence = context, documentContext = (object?)null };
        var user = documentContext is null
            ? JsonSerializer.Serialize(new { protocolVersion, ownedSubjects = owned, contextOnlyEvidence = context }, JsonOptions)
            : JsonSerializer.Serialize(userObject, JsonOptions);
        return new V5FreeHeadingRequestV1(protocolVersion, BoundLocatorSystemPrompt, user,
            Hashing.Sha256(user), Encoding.UTF8.GetByteCount(BoundLocatorSystemPrompt), Encoding.UTF8.GetByteCount(user));
    }

    /// <summary>Builds the one qualified route's json_object carrier with free reasoning enabled.</summary>
    public static V5ProviderRequestBodyV2_1 BuildProviderBody(V5FreeHeadingRequestV1 request,
        int maxCompletionTokens, int timeoutSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        var provider = new
        {
            order = new[] { "alibaba" },
            allow_fallbacks = false,
            require_parameters = true,
            data_collection = "deny",
            zdr = false,
        };
        var body = new
        {
            model = "qwen/qwen3.7-flash",
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { enabled = true },
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserMessage },
            },
            response_format = new { type = "json_object" },
            provider,
            stream = true,
            usage = new { include = true },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        return new V5ProviderRequestBodyV2_1(bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    /// <summary>Raw provider body with no response_format and no schema enforcement, for P6N's free-output arm.</summary>
    public static V5ProviderRequestBodyV2_1 BuildUnconstrainedProviderBody(V5FreeHeadingRequestV1 request,
        int maxCompletionTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        var provider = new
        {
            order = new[] { "alibaba" },
            allow_fallbacks = false,
            require_parameters = true,
            data_collection = "deny",
            zdr = false,
        };
        var body = new
        {
            model = "qwen/qwen3.7-flash",
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { enabled = true },
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserMessage },
            },
            provider,
            stream = true,
            usage = new { include = true },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        return new V5ProviderRequestBodyV2_1(bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    /// <summary>Builds the P6N-B body: json_object carrier, enabled reasoning, no task ontology/schema enforcement.</summary>
    public static V5ProviderRequestBodyV2_1 BuildBoundLocatorProviderBody(V5FreeHeadingRequestV1 request,
        int maxCompletionTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        var provider = new
        {
            order = new[] { "alibaba" }, allow_fallbacks = false, require_parameters = true,
            data_collection = "deny", zdr = false,
        };
        var body = new
        {
            model = "qwen/qwen3.7-flash", temperature = 0, max_tokens = maxCompletionTokens,
            reasoning = new { enabled = true },
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserMessage },
            },
            response_format = new { type = "json_object" }, provider, stream = true, usage = new { include = true },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        return new V5ProviderRequestBodyV2_1(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    /// <summary>
    /// Parses P6N's headings/locator-only response, then adapts candidate locators to the already
    /// qualified P6M parser/binder. The internal STRUCTURAL_REGION marker is a parser compatibility
    /// sentinel only: it is synthesized locally, never sent, returned, scored, or persisted.
    /// </summary>
    public static OccurrenceLocatorResponseResult ParseAndBind(JsonElement payload, int rawUtf8Bytes,
        int responseCap, RequestLocalLocatorRegistry registry, IReadOnlySet<int> ownedAtomIndices)
    {
        if (rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap || responseCap < 1)
            throw new InvalidOperationException("occurrence-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1 ||
            !payload.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6n-response-root-invalid");

        var adapted = new JsonObject { ["occurrences"] = new JsonArray() };
        var occurrences = adapted["occurrences"]!.AsArray();
        foreach (var heading in headings.EnumerateArray())
        {
            if (heading.ValueKind != JsonValueKind.Object || heading.EnumerateObject().Count() != 1 ||
                !heading.TryGetProperty("locator", out var locator))
            {
                occurrences.Add(new JsonObject());
                continue;
            }

            if (locator.ValueKind != JsonValueKind.Object || locator.EnumerateObject().Count() != 2 ||
                !locator.TryGetProperty("primary", out var primary) ||
                !locator.TryGetProperty("additionalParts", out var additional))
            {
                occurrences.Add(new JsonObject());
                continue;
            }
            var candidate = new JsonObject
            {
                ["primary"] = JsonNode.Parse(primary.GetRawText()),
                ["additionalParts"] = JsonNode.Parse(additional.GetRawText()),
                ["functions"] = new JsonArray("STRUCTURAL_REGION"),
            };
            occurrences.Add(candidate);
        }

        using var adaptedDocument = JsonDocument.Parse(adapted.ToJsonString(JsonOptions));
        return registry.Parse(adaptedDocument.RootElement, rawUtf8Bytes, responseCap, ownedAtomIndices);
    }

    /// <summary>
    /// Parses headings[].sourceParts[] and adapts only its locator parts into P6L's qualified parser/binder.
    /// A local placeholder function is discarded and is never present on the provider wire or scored.
    /// </summary>
    public static OccurrenceLocatorResponseResult ParseAndBindSourceParts(JsonElement payload, int rawUtf8Bytes,
        int responseCap, RequestLocalLocatorRegistry registry, IReadOnlySet<int> ownedAtomIndices)
    {
        if (rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap || responseCap < 1)
            throw new InvalidOperationException("occurrence-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1 ||
            !payload.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6n-response-root-invalid");

        var adapted = new JsonObject { ["occurrences"] = new JsonArray() };
        var occurrences = adapted["occurrences"]!.AsArray();
        foreach (var heading in headings.EnumerateArray())
        {
            if (heading.ValueKind != JsonValueKind.Object || heading.EnumerateObject().Count() != 1 ||
                !heading.TryGetProperty("sourceParts", out var sourceParts) || sourceParts.ValueKind != JsonValueKind.Array || sourceParts.GetArrayLength() == 0)
            {
                occurrences.Add(new JsonObject());
                continue;
            }
            var candidate = new JsonObject
            {
                ["primary"] = JsonNode.Parse(sourceParts[0].GetRawText()),
                ["additionalParts"] = new JsonArray(sourceParts.EnumerateArray().Skip(1).Select(part => JsonNode.Parse(part.GetRawText())).ToArray()),
                ["functions"] = new JsonArray("STRUCTURAL_REGION"),
            };
            occurrences.Add(candidate);
        }
        using var adaptedDocument = JsonDocument.Parse(adapted.ToJsonString(JsonOptions));
        return registry.Parse(adaptedDocument.RootElement, rawUtf8Bytes, responseCap, ownedAtomIndices);
    }
}
