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

    private static IReadOnlyDictionary<string, string?> ProjectFacts(IReadOnlyDictionary<string, string?> facts) =>
        facts.Where(pair => ApprovedStructuralFacts.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
