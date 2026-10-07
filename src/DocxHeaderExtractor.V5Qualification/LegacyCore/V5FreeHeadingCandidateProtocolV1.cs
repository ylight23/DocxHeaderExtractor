using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
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
    public const string PdfDocumentAwareLayoutBoundLocatorVersion = "v5-free-reasoning-heading-membership-pdf-document-context-layout-locator-1";
    public const string PdfStructuralIdentityResolutionVersion = "v5-free-reasoning-heading-membership-pdf-structural-identity-resolution-1";
    public const string PdfLocalHeadingPrecedenceVersion = "v5-free-reasoning-heading-membership-pdf-local-heading-precedence-1";

    public const string UnschematizedSystemPrompt = """
        You are reading a document represented by source occurrences in document order. Identify the occurrences that you judge to function as headings in this document. Use the document context and the observable source evidence provided. Return the headings you judge to be present in the source, using whatever response format and schema you prefer. Do not use any external answer key.
        """;

    public const string BoundLocatorSystemPrompt = """
        You are reading a document from source occurrences in document order. Identify the occurrences that you judge to function as headings in this document. Use the document context and the observable source evidence provided. Return only headings you judge to be present in the source. Do not use any external answer key.

        Return one JSON object with exactly this shape: {"headings":[{"sourceParts":[{"atom":"A17"}]}]}.
        Each heading has exactly one property, sourceParts. sourceParts is a non-empty ordered array of source locators: the first part is the primary occurrence and later parts are ordered continuations of that same heading. Every part uses only an issued owned atom handle and optional from/to boundary handles. A whole atom is exactly {"atom":"A17"}, with no from/to. A strict proper substring is exactly {"atom":"A17","from":"H123","to":"H145"}; never emit a full-span boundary pair. Additional atoms must be owned and strictly increasing in source order. Select only opaque handles in ownedSubjects; contextOnlyEvidence is reasoning-only and is never selectable. Output no semantic function, heading level, type, reason, confidence, hierarchy, or other property.
        """;

    /// <summary>
    /// P6R deliberately supplies no document-genre or semantic-region labels.  It makes only the
    /// source-derived recurrence relation addressable, so the model can distinguish a structural
    /// owner from an occurrence that represents structure elsewhere.
    /// </summary>
    public const string StructuralIdentityResolutionSystemPrompt = """
        Identify the structural heading occurrences in the document. Some source occurrences repeat, summarize, list, or refer to structural content that occurs elsewhere.

        When correspondence candidates are supplied, resolve which occurrence establishes a structural region at its own location and which occurrence merely represents or refers to structure elsewhere. Return local structural headings in headings. Return source occurrences that only represent structure elsewhere in representations, with supplied corresponding document handles. A representation may instead cite supplied read-only context evidence when it is being used to point to or list content elsewhere.

        Return one JSON object with exactly this shape: {"headings":[{"sourceParts":[{"atom":"A17"}]}],"representations":[{"sourceParts":[{"atom":"A18"}],"correspondsTo":["D243"]}]}. Every heading and representation has sourceParts: a non-empty ordered array of source locators. The first part is primary and later parts are ordered continuations. Every part uses only an issued owned atom handle and optional from/to boundary handles. A whole atom is exactly {"atom":"A17"}; a strict proper substring is exactly {"atom":"A17","from":"H123","to":"H145"}; never emit a full-span boundary pair. Additional atoms must be owned and strictly increasing in source order. Only ownedSubjects are selectable. contextOnlyEvidence, document targets D#, and read-only context handles C# are never selectable as sourceParts. For each representation use exactly one of correspondsTo (only D# supplied for that source occurrence) or evidenceParts (only supplied C#). Do not classify by typography alone. Output no semantic function, heading level, type, reason, confidence, hierarchy, or other property. Do not use any external answer key.
        """;

    /// <summary>
    /// P6S fixes the failure mode observed in P6R: correspondence is secondary evidence and must
    /// never split a multipart local heading or demote a heading merely because its text recurs.
    /// The wording stays document-generic: no TOC/index/document-specific labels are supplied.
    /// </summary>
    public const string LocalHeadingPrecedenceSystemPrompt = """
        Identify heading occurrences in the document. Judge each occurrence by what it does at its own source location. A heading establishes or names a document, part, section, subsection, or local group at that location. Correspondence with repeated or related text elsewhere is secondary evidence only and never by itself makes the current occurrence non-heading.

        Preserve heading extent. When adjacent owned source occurrences jointly form one heading, return them together as one headings item using ordered sourceParts. Do not split one local heading into separate heading items. Do not move only a continuation part of a local multipart heading into representations. If an occurrence both corresponds to structure elsewhere and establishes a heading locally, keep the complete occurrence in headings.

        Use representations only for an occurrence that does not establish a heading at its own location and instead merely lists, points to, summarizes, or navigates to structure elsewhere. A dense run of title-like entries is representation-only when the entries point elsewhere and do not open their own local content there. Conversely, a short label that opens a local group is still a heading even when nearby material is list-like.

        Return one JSON object with exactly this shape: {"headings":[{"sourceParts":[{"atom":"A17"}]}],"representations":[{"sourceParts":[{"atom":"A18"}],"correspondsTo":["D243"]}]}. Every heading and representation has sourceParts: a non-empty ordered array of source locators. The first part is primary and later parts are ordered continuations. Every part uses only an issued owned atom handle and optional from/to boundary handles. A whole atom is exactly {"atom":"A17"}; a strict proper substring is exactly {"atom":"A17","from":"H123","to":"H145"}; never emit a full-span boundary pair. Additional atoms must be owned and strictly increasing in source order. Only ownedSubjects are selectable. contextOnlyEvidence, document targets D#, and read-only context handles C# are never selectable as sourceParts. For each representation use exactly one of correspondsTo (only D# supplied for that source occurrence) or evidenceParts (only supplied C#). A source occurrence must not appear in both headings and representations. Do not classify by typography alone. Output no semantic function, heading level, type, reason, confidence, hierarchy, or other property. Do not use any external answer key.
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
        var version = p6mCanonicalRequest.ProtocolVersion == PdfDocumentAwareLayoutBoundLocatorVersion
            ? PdfDocumentAwareLayoutBoundLocatorVersion
            : PdfDocumentAwareBoundLocatorVersion;
        return ComposeBoundLocatorCore(p6mCanonicalRequest, documentContext, version);
    }

    /// <summary>Adds deterministic, read-only structural-correspondence evidence to a P6P request.</summary>
    public static V5FreeHeadingRequestV1 ComposeStructuralIdentityResolution(
        V5FreeHeadingRequestV1 documentAwareRequest, JsonElement correspondenceCandidates, JsonElement readOnlyContextEvidence)
    {
        ArgumentNullException.ThrowIfNull(documentAwareRequest);
        if (correspondenceCandidates.ValueKind != JsonValueKind.Array || readOnlyContextEvidence.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("P6R correspondence and read-only context evidence must be arrays.");
        using var source = JsonDocument.Parse(documentAwareRequest.UserMessage);
        var root = JsonNode.Parse(source.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("p6r-source-request-invalid");
        root["protocolVersion"] = PdfStructuralIdentityResolutionVersion;
        root["correspondenceCandidates"] = JsonNode.Parse(correspondenceCandidates.GetRawText());
        root["readOnlyContextEvidence"] = JsonNode.Parse(readOnlyContextEvidence.GetRawText());
        var user = root.ToJsonString(JsonOptions);
        return new V5FreeHeadingRequestV1(PdfStructuralIdentityResolutionVersion, StructuralIdentityResolutionSystemPrompt, user,
            Hashing.Sha256(user), Encoding.UTF8.GetByteCount(StructuralIdentityResolutionSystemPrompt), Encoding.UTF8.GetByteCount(user));
    }

    /// <summary>
    /// P6S uses the same P6R source/correspondence envelope but fixes task semantics so local
    /// heading identity and multipart extent take precedence over recurrence/correspondence.
    /// </summary>
    public static V5FreeHeadingRequestV1 ComposeLocalHeadingPrecedence(
        V5FreeHeadingRequestV1 documentAwareRequest, JsonElement correspondenceCandidates, JsonElement readOnlyContextEvidence)
    {
        ArgumentNullException.ThrowIfNull(documentAwareRequest);
        if (correspondenceCandidates.ValueKind != JsonValueKind.Array || readOnlyContextEvidence.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("P6S correspondence and read-only context evidence must be arrays.");
        using var source = JsonDocument.Parse(documentAwareRequest.UserMessage);
        var root = JsonNode.Parse(source.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("p6s-source-request-invalid");
        root["protocolVersion"] = PdfLocalHeadingPrecedenceVersion;
        root["correspondenceCandidates"] = JsonNode.Parse(correspondenceCandidates.GetRawText());
        root["readOnlyContextEvidence"] = JsonNode.Parse(readOnlyContextEvidence.GetRawText());
        var user = root.ToJsonString(JsonOptions);
        return new V5FreeHeadingRequestV1(PdfLocalHeadingPrecedenceVersion, LocalHeadingPrecedenceSystemPrompt, user,
            Hashing.Sha256(user), Encoding.UTF8.GetByteCount(LocalHeadingPrecedenceSystemPrompt), Encoding.UTF8.GetByteCount(user));
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

    /// <summary>P6R's heading binding plus read-only representation-resolution audit.</summary>
    public sealed record StructuralIdentityResolutionResult(
        OccurrenceLocatorResponseResult Headings,
        int RepresentationsAccepted,
        IReadOnlyList<string> RepresentationQuarantine);

    /// <summary>
    /// P6S uses the P6R wire grammar but additionally enforces disjoint heading/representation
    /// ownership. This prevents a multipart heading continuation from being simultaneously
    /// retyped as representation-only.
    /// </summary>
    public static StructuralIdentityResolutionResult ParseLocalHeadingPrecedence(
        JsonElement payload, int rawUtf8Bytes, int responseCap, RequestLocalLocatorRegistry registry,
        IReadOnlySet<int> ownedAtomIndices, IReadOnlyDictionary<string, IReadOnlySet<string>> allowedCorrespondenceTargetsByPrimaryAtom,
        IReadOnlySet<string> allowedContextEvidence)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array ||
            !payload.TryGetProperty("representations", out var representations) || representations.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6s-response-root-invalid");

        var headingAtoms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in headings.EnumerateArray())
        {
            if (heading.ValueKind != JsonValueKind.Object ||
                !heading.TryGetProperty("sourceParts", out var sourceParts) || sourceParts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in sourceParts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("atom", out var atom) && atom.ValueKind == JsonValueKind.String)
                    headingAtoms.Add(atom.GetString()!);
        }

        foreach (var representation in representations.EnumerateArray())
        {
            if (representation.ValueKind != JsonValueKind.Object ||
                !representation.TryGetProperty("sourceParts", out var sourceParts) || sourceParts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in sourceParts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("atom", out var atom) && atom.ValueKind == JsonValueKind.String &&
                    headingAtoms.Contains(atom.GetString()!))
                    throw new InvalidOperationException("p6s-heading-representation-source-overlap");
        }

        return ParseStructuralIdentityResolution(payload, rawUtf8Bytes, responseCap, registry, ownedAtomIndices,
            allowedCorrespondenceTargetsByPrimaryAtom, allowedContextEvidence);
    }

    public static StructuralIdentityResolutionResult ParseStructuralIdentityResolution(
        JsonElement payload, int rawUtf8Bytes, int responseCap, RequestLocalLocatorRegistry registry,
        IReadOnlySet<int> ownedAtomIndices, IReadOnlyDictionary<string, IReadOnlySet<string>> allowedCorrespondenceTargetsByPrimaryAtom,
        IReadOnlySet<string> allowedContextEvidence)
    {
        if (rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap || responseCap < 1)
            throw new InvalidOperationException("occurrence-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 2 ||
            !payload.TryGetProperty("headings", out var headings) || headings.ValueKind != JsonValueKind.Array ||
            !payload.TryGetProperty("representations", out var representations) || representations.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("p6r-response-root-invalid");

        using var headingRoot = JsonDocument.Parse(JsonSerializer.Serialize(new { headings }, JsonOptions));
        var headingBinding = ParseAndBindSourceParts(headingRoot.RootElement, rawUtf8Bytes, responseCap, registry, ownedAtomIndices);
        var accepted = 0;
        var quarantine = new List<string>();
        var representationIndex = 0;
        foreach (var representation in representations.EnumerateArray())
        {
            representationIndex++;
            if (representation.ValueKind != JsonValueKind.Object || !representation.TryGetProperty("sourceParts", out var sourceParts) ||
                sourceParts.ValueKind != JsonValueKind.Array || sourceParts.GetArrayLength() == 0)
            {
                quarantine.Add($"representation-{representationIndex}:source-parts-invalid");
                continue;
            }
            var hasTargets = representation.TryGetProperty("correspondsTo", out var targets);
            var hasEvidence = representation.TryGetProperty("evidenceParts", out var evidence);
            var primaryAtom = sourceParts[0].ValueKind == JsonValueKind.Object && sourceParts[0].TryGetProperty("atom", out var primaryAtomValue)
                ? primaryAtomValue.GetString() : null;
            if (representation.EnumerateObject().Count() != 2 || hasTargets == hasEvidence ||
                (hasTargets && (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() == 0 ||
                    primaryAtom is null || !allowedCorrespondenceTargetsByPrimaryAtom.TryGetValue(primaryAtom, out var allowedTargets) ||
                    targets.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !allowedTargets.Contains(item.GetString()!)))) ||
                (hasEvidence && (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0 ||
                    evidence.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !allowedContextEvidence.Contains(item.GetString()!)))))
            {
                quarantine.Add($"representation-{representationIndex}:relation-handle-invalid");
                continue;
            }
            using var representationRoot = JsonDocument.Parse(JsonSerializer.Serialize(new { headings = new[] { new { sourceParts } } }, JsonOptions));
            var binding = ParseAndBindSourceParts(representationRoot.RootElement, rawUtf8Bytes, responseCap, registry, ownedAtomIndices);
            if (binding.Quarantined.Count != 0 || binding.Response.Occurrences.Count != 1)
            {
                quarantine.Add($"representation-{representationIndex}:locator-invalid");
                continue;
            }
            accepted++;
        }
        return new StructuralIdentityResolutionResult(headingBinding, accepted, quarantine);
    }
}
