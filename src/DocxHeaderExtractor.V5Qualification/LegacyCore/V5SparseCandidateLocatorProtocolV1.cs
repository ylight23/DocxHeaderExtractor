using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>A qualification-only request view; it is deliberately not connected to DocumentAgentRuntime.</summary>
public sealed record V5SparseCandidateModelRequestV1(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes,
    int SemanticSourceContextUtf8Bytes,
    int LocatorDirectoryUtf8Bytes,
    int InstructionsSchemaUtf8Bytes,
    int UserMessageWithoutLocatorDirectoryUtf8Bytes,
    string UserMessageSha256);

/// <summary>Composes the sparse occurrence lane without serializing legacy claim/decision contracts or source identities.</summary>
public static class V5SparseCandidateRequestComposerV1
{
    public const string Version = "v5-heading-occurrence-sparse-locator-1";
    public const string CompactDirectoryVersion = "v5-heading-occurrence-sparse-locator-compact-directory-1";
    public const string CompactDirectoryCanonicalVersion = "v5-heading-occurrence-sparse-locator-compact-directory-2";

    private static readonly HashSet<string> ApprovedStructuralFacts = new(StringComparer.Ordinal)
    {
        "sourceType", "bold", "italic", "fontSize", "relativeFontSize", "bodyFontSize", "fontSizeToBodyRatio",
        "boldRatio", "italicRatio", "lineCount", "width", "height", "grid", "gridType", "structuralScope", "pageRole",
    };

    public const string SystemPrompt = """
        You identify source-backed semantic occurrences for the supplied document-structure task.
        Return only one JSON object with exactly this shape:
        {"occurrences":[{"primary":{"atom":"A#","from":"H#","to":"H#"},"additionalParts":[{"atom":"A#","from":"H#","to":"H#"}],"functions":["DOCUMENT_IDENTITY"]}]}
        Emit zero or more occurrences; an empty array means no semantic proposal. A whole atom uses only its atom handle and omits from/to. Use from and to together only for a strict substring. Additional parts must be owned atoms in increasing source order after primary.
        Select only opaque atom and boundary handles present in the supplied owned locator directory. Never invent handles or output source aliases, ids, source text, offsets, spans, context, claim ids, states, evidence needs, or any relation. Context-only evidence is for reasoning only and is never selectable as a subject or multipart part.
        Each occurrence functions array must be a non-empty unique subset of DOCUMENT_IDENTITY, STRUCTURAL_REGION, NAVIGATION_REPRESENTATION. Functions are not mutually exclusive. Do not return any other fields or commentary.
        """;

    public const string CompactDirectorySystemPrompt = """
        You identify source-backed semantic occurrences for the supplied document-structure task.
        Return only one JSON object with exactly this shape:
        {"occurrences":[{"primary":{"atom":"A#","from":"H#","to":"H#"},"additionalParts":[{"atom":"A#","from":"H#","to":"H#"}],"functions":["DOCUMENT_IDENTITY"]}]}
        Emit zero or more occurrences; an empty array means no semantic proposal. A whole atom uses only its atom handle and omits from/to. Use from and to together only for a strict substring. Additional parts must be owned atoms in increasing source order after primary.
        Each owned atom provides boundaryHandles in Unicode-scalar order: item 0 is the boundary before its first scalar, item 1 is after the first scalar, and the final item is the boundary after the last scalar. Select the exact opaque H# string from that array for each endpoint; never output a character/UTF-16 offset or calculate/write a new handle. The registry resolves each listed handle to its exact source coordinate.
        Select only atom and boundary handles present in the supplied owned locator directory. Never invent handles or output source aliases, ids, source text, offsets, spans, context, claim ids, states, evidence needs, or any relation. Context-only evidence is for reasoning only and is never selectable as a subject or multipart part.
        Each occurrence functions array must be a non-empty unique subset of DOCUMENT_IDENTITY, STRUCTURAL_REGION, NAVIGATION_REPRESENTATION. Functions are not mutually exclusive. Do not return any other fields or commentary.
        """;

    public const string CompactDirectoryCanonicalSystemPrompt = """
        Identify source-backed semantic occurrences for the supplied document-structure task.
        Return one JSON object with root shape: {"occurrences":[OCCURRENCE,...]}. Zero occurrences is valid.
        Each occurrence has exactly: primary, additionalParts, functions. Each locator part has exactly one of these two shapes:
        WHOLE ATOM (canonical): {"atom":"A#"}. Use this shape whenever the selected source is the complete atom. Do not include from or to.
        STRICT SUBSTRING ONLY: {"atom":"A#","from":"H#","to":"H#"}. Use this shape only when the selected source is a proper, non-empty substring of the atom.
        The strict-substring shape is INVALID if from is the first boundary handle in that atom's boundaryHandles array AND to is the final boundary handle. That pair covers the complete atom; emit only {"atom":"A#"} instead. Never emit a full-span pair.
        These are mutually exclusive object shapes: whole atom requires atom and forbids from/to; strict substring requires atom, from, and to. No other locator-part properties are allowed.
        Example shapes (handle strings are placeholders; select only handles actually issued in this request): whole atom {"atom":"A17"}; strict substring {"atom":"A17","from":"H123","to":"H145"}.
        Additional parts use the same two-shape rule and must be owned atoms in increasing source order after primary.
        Each owned atom's boundaryHandles are in Unicode-scalar order: first item is before its first scalar; final item is after its last scalar. Compare a proposed pair with those endpoints before choosing the strict-substring shape.
        Select only opaque atom and boundary handles listed in ownedSubjects. Never invent handles or output source aliases, ids, source text, offsets, spans, context, claim ids, states, evidence needs, or relations. Context-only evidence is reasoning-only and never selectable.
        Each functions array is a non-empty unique subset of DOCUMENT_IDENTITY, STRUCTURAL_REGION, NAVIGATION_REPRESENTATION. They are not mutually exclusive. Output no commentary or extra fields.
        """;

    public static V5SparseCandidateModelRequestV1 Compose(DocumentTaskContract contract,
        V5SemanticDecisionRequestPacketV3 packet, RequestLocalLocatorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(registry);
        contract.Validate();
        if (packet.SubjectEvidence.Count != registry.AtomCount ||
            !packet.SubjectEvidence.Select(node => node.SourceAlias).SequenceEqual(registry.Atoms.Select(atom => atom.Alias), StringComparer.Ordinal) ||
            !packet.SubjectEvidence.Select(node => node.Text).SequenceEqual(registry.Atoms.Select(atom => atom.Text), StringComparer.Ordinal))
            throw new InvalidOperationException("sparse-candidate-registry-owned-source-mismatch");
        var ownedAliases = packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        if (packet.ContextOnlyEvidence.Any(node => ownedAliases.Contains(node.SourceAlias)))
            throw new InvalidOperationException("sparse-candidate-owned-context-overlap");

        var directory = registry.Directory();
        var ownedSubjects = directory.Atoms.Select((atom, index) => new
        {
            atom = atom.Atom,
            text = atom.Text,
            modality = packet.SubjectEvidence[index].Modality,
            facts = ProjectFacts(packet.SubjectEvidence[index].Facts),
            boundaries = atom.Boundaries,
        }).ToArray();
        var contextOnly = packet.ContextOnlyEvidence.Select(node => new
        {
            modality = node.Modality,
            text = node.Text,
            facts = ProjectFacts(node.Facts),
        }).ToArray();
        var task = new
        {
            taskId = contract.TaskId,
            description = contract.Description,
            semanticFunctions = contract.Predicates.Where(predicate => predicate.Name is "DOCUMENT_IDENTITY" or "STRUCTURAL_REGION" or "NAVIGATION_REPRESENTATION")
                .OrderBy(predicate => predicate.Name, StringComparer.Ordinal).ToArray(),
        };
        if (task.semanticFunctions.Length != 3) throw new InvalidOperationException("sparse-candidate-task-functions-incomplete");
        var responseContract = new
        {
            root = new[] { "occurrences" },
            occurrence = new[] { "primary", "additionalParts", "functions" },
            locatorPart = new[] { "atom", "from?", "to?" },
            functions = task.semanticFunctions.Select(item => item.Name).ToArray(),
            emptyOccurrencesAllowed = true,
        };
        var userObject = new
        {
            protocolVersion = Version,
            task,
            responseContract,
            ownedSubjects,
            contextOnlyEvidence = contextOnly,
        };
        var userMessage = JsonSerializer.Serialize(userObject, CanonicalJson.Options);
        var withoutDirectory = JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            task,
            responseContract,
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);

        var semanticSourceContext = JsonSerializer.Serialize(new
        {
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);
        var locatorDirectory = JsonSerializer.Serialize(ownedSubjects, CanonicalJson.Options);
        var instructionSchema = JsonSerializer.Serialize(new { systemPrompt = SystemPrompt, task, responseContract }, CanonicalJson.Options);
        return new V5SparseCandidateModelRequestV1(Version, SystemPrompt, userMessage,
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(userMessage),
            Encoding.UTF8.GetByteCount(semanticSourceContext), Encoding.UTF8.GetByteCount(locatorDirectory),
            Encoding.UTF8.GetByteCount(instructionSchema), Encoding.UTF8.GetByteCount(withoutDirectory), Hashing.Sha256(userMessage));
    }

    public static V5SparseCandidateModelRequestV1 ComposeCompactDirectory(DocumentTaskContract contract,
        V5SemanticDecisionRequestPacketV3 packet, RequestLocalLocatorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(registry);
        contract.Validate();
        if (packet.SubjectEvidence.Count != registry.AtomCount ||
            !packet.SubjectEvidence.Select(node => node.SourceAlias).SequenceEqual(registry.Atoms.Select(atom => atom.Alias), StringComparer.Ordinal) ||
            !packet.SubjectEvidence.Select(node => node.Text).SequenceEqual(registry.Atoms.Select(atom => atom.Text), StringComparer.Ordinal))
            throw new InvalidOperationException("sparse-candidate-registry-owned-source-mismatch");
        var ownedAliases = packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        if (packet.ContextOnlyEvidence.Any(node => ownedAliases.Contains(node.SourceAlias)))
            throw new InvalidOperationException("sparse-candidate-owned-context-overlap");

        var directory = registry.CompactDirectory();
        var ownedSubjects = directory.Atoms.Select((atom, index) => new
        {
            atom = atom.Atom,
            text = atom.Text,
            modality = packet.SubjectEvidence[index].Modality,
            facts = ProjectFacts(packet.SubjectEvidence[index].Facts),
            boundaryHandles = atom.BoundaryHandles,
        }).ToArray();
        var contextOnly = packet.ContextOnlyEvidence.Select(node => new
        {
            modality = node.Modality,
            text = node.Text,
            facts = ProjectFacts(node.Facts),
        }).ToArray();
        var task = new
        {
            taskId = contract.TaskId,
            description = contract.Description,
            semanticFunctions = contract.Predicates.Where(predicate => predicate.Name is "DOCUMENT_IDENTITY" or "STRUCTURAL_REGION" or "NAVIGATION_REPRESENTATION")
                .OrderBy(predicate => predicate.Name, StringComparer.Ordinal).ToArray(),
        };
        if (task.semanticFunctions.Length != 3) throw new InvalidOperationException("sparse-candidate-task-functions-incomplete");
        var responseContract = new
        {
            root = new[] { "occurrences" },
            occurrence = new[] { "primary", "additionalParts", "functions" },
            locatorPart = new[] { "atom", "from?", "to?" },
            functions = task.semanticFunctions.Select(item => item.Name).ToArray(),
            emptyOccurrencesAllowed = true,
        };
        var userObject = new
        {
            protocolVersion = CompactDirectoryVersion,
            boundaryDirectoryEncoding = "ordered-unicode-scalar-boundary-handles-v1",
            task,
            responseContract,
            ownedSubjects,
            contextOnlyEvidence = contextOnly,
        };
        var userMessage = JsonSerializer.Serialize(userObject, CanonicalJson.Options);
        var withoutDirectory = JsonSerializer.Serialize(new
        {
            protocolVersion = CompactDirectoryVersion,
            boundaryDirectoryEncoding = "ordered-unicode-scalar-boundary-handles-v1",
            task,
            responseContract,
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);
        var semanticSourceContext = JsonSerializer.Serialize(new
        {
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);
        var locatorDirectory = JsonSerializer.Serialize(ownedSubjects, CanonicalJson.Options);
        var instructionSchema = JsonSerializer.Serialize(new { systemPrompt = CompactDirectorySystemPrompt, task, responseContract }, CanonicalJson.Options);
        return new V5SparseCandidateModelRequestV1(CompactDirectoryVersion, CompactDirectorySystemPrompt, userMessage,
            Encoding.UTF8.GetByteCount(CompactDirectorySystemPrompt), Encoding.UTF8.GetByteCount(userMessage),
            Encoding.UTF8.GetByteCount(semanticSourceContext), Encoding.UTF8.GetByteCount(locatorDirectory),
            Encoding.UTF8.GetByteCount(instructionSchema), Encoding.UTF8.GetByteCount(withoutDirectory), Hashing.Sha256(userMessage));
    }

    /// <summary>Clarified canonical whole-atom vs strict-substring grammar; parser/binder authority is unchanged.</summary>
    public static V5SparseCandidateModelRequestV1 ComposeCompactDirectoryCanonical(DocumentTaskContract contract,
        V5SemanticDecisionRequestPacketV3 packet, RequestLocalLocatorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(registry);
        contract.Validate();
        if (packet.SubjectEvidence.Count != registry.AtomCount ||
            !packet.SubjectEvidence.Select(node => node.SourceAlias).SequenceEqual(registry.Atoms.Select(atom => atom.Alias), StringComparer.Ordinal) ||
            !packet.SubjectEvidence.Select(node => node.Text).SequenceEqual(registry.Atoms.Select(atom => atom.Text), StringComparer.Ordinal))
            throw new InvalidOperationException("sparse-candidate-registry-owned-source-mismatch");
        var ownedAliases = packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        if (packet.ContextOnlyEvidence.Any(node => ownedAliases.Contains(node.SourceAlias)))
            throw new InvalidOperationException("sparse-candidate-owned-context-overlap");

        var directory = registry.CompactDirectory();
        var ownedSubjects = directory.Atoms.Select((atom, index) => new
        {
            atom = atom.Atom,
            text = atom.Text,
            modality = packet.SubjectEvidence[index].Modality,
            facts = ProjectFacts(packet.SubjectEvidence[index].Facts),
            boundaryHandles = atom.BoundaryHandles,
        }).ToArray();
        var contextOnly = packet.ContextOnlyEvidence.Select(node => new
        {
            modality = node.Modality,
            text = node.Text,
            facts = ProjectFacts(node.Facts),
        }).ToArray();
        var task = new
        {
            taskId = contract.TaskId,
            description = contract.Description,
            semanticFunctions = contract.Predicates.Where(predicate => predicate.Name is "DOCUMENT_IDENTITY" or "STRUCTURAL_REGION" or "NAVIGATION_REPRESENTATION")
                .OrderBy(predicate => predicate.Name, StringComparer.Ordinal).ToArray(),
        };
        if (task.semanticFunctions.Length != 3) throw new InvalidOperationException("sparse-candidate-task-functions-incomplete");
        var responseContract = new
        {
            root = new { type = "object", required = new[] { "occurrences" }, additionalProperties = false },
            occurrence = new { type = "object", required = new[] { "primary", "additionalParts", "functions" }, additionalProperties = false },
            locatorPart = new
            {
                oneOf = new object[]
                {
                    new { type = "object", required = new[] { "atom" }, properties = new { atom = new { type = "string" } }, additionalProperties = false },
                    new { type = "object", required = new[] { "atom", "from", "to" }, properties = new { atom = new { type = "string" }, from = new { type = "string" }, to = new { type = "string" } }, additionalProperties = false },
                },
                canonicalRule = "whole atom uses only atom; from/to shape is valid only for a proper non-empty substring; first+final boundary pair is forbidden",
                shapes = new { wholeAtom = "{\"atom\":\"A#\"}", strictSubstring = "{\"atom\":\"A#\",\"from\":\"H#\",\"to\":\"H#\"}" },
            },
            functions = task.semanticFunctions.Select(item => item.Name).ToArray(),
            emptyOccurrencesAllowed = true,
        };
        var userObject = new
        {
            protocolVersion = CompactDirectoryCanonicalVersion,
            boundaryDirectoryEncoding = "ordered-unicode-scalar-boundary-handles-v1",
            locatorCanonicalRule = "If selected text equals the full owned atom text, return {atom} only. Never encode the first-to-final boundary pair.",
            task,
            responseContract,
            ownedSubjects,
            contextOnlyEvidence = contextOnly,
        };
        var userMessage = JsonSerializer.Serialize(userObject, CanonicalJson.Options);
        var withoutDirectory = JsonSerializer.Serialize(new
        {
            protocolVersion = CompactDirectoryCanonicalVersion,
            boundaryDirectoryEncoding = "ordered-unicode-scalar-boundary-handles-v1",
            locatorCanonicalRule = "If selected text equals the full owned atom text, return {atom} only. Never encode the first-to-final boundary pair.",
            task,
            responseContract,
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);
        var semanticSourceContext = JsonSerializer.Serialize(new
        {
            ownedSubjects = ownedSubjects.Select(item => new { item.modality, item.text, item.facts }),
            contextOnlyEvidence = contextOnly,
        }, CanonicalJson.Options);
        var locatorDirectory = JsonSerializer.Serialize(ownedSubjects, CanonicalJson.Options);
        var instructionSchema = JsonSerializer.Serialize(new { systemPrompt = CompactDirectoryCanonicalSystemPrompt, task, responseContract }, CanonicalJson.Options);
        return new V5SparseCandidateModelRequestV1(CompactDirectoryCanonicalVersion, CompactDirectoryCanonicalSystemPrompt,
            userMessage, Encoding.UTF8.GetByteCount(CompactDirectoryCanonicalSystemPrompt), Encoding.UTF8.GetByteCount(userMessage),
            Encoding.UTF8.GetByteCount(semanticSourceContext), Encoding.UTF8.GetByteCount(locatorDirectory),
            Encoding.UTF8.GetByteCount(instructionSchema), Encoding.UTF8.GetByteCount(withoutDirectory), Hashing.Sha256(userMessage));
    }

    private static IReadOnlyDictionary<string, string?> ProjectFacts(IReadOnlyDictionary<string, string?> facts) =>
        facts.Where(pair => ApprovedStructuralFacts.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
