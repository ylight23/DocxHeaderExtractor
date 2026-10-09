using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal enum InterpretationStage { F1, G2A, H2C }
internal sealed record InterpretationRequest(
    InterpretationStage Stage, string SystemPrompt, string UserMessage, string ControlSystemPrompt,
    string ControlUserMessage, PdfSourceEvidenceStore EvidenceStore,
    IReadOnlyList<V5IssuedOccurrenceV1> Owned, IReadOnlyList<string> DecisionSubjects,
    IReadOnlyDictionary<string, string> VisibleAliasByOccurrence)
{
    public string SystemPromptSha256 => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(SystemPrompt));
    public string UserMessageSha256 => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(UserMessage));
}
internal sealed record InterpretationValidationResult(JsonElement StageDecision, JsonElement Analysis,
    string EvidenceStatus = "VERIFIED_FACTS",
    string SemanticStatus = "NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW",
    string InterpretationStatus = "UNVERIFIABLE_ASSERTION");

/// <summary>Qualification B only. No provider, production authority, Gold, or relation ontology.</summary>
internal static class PdfInterpretationProtocol
{
    public const string Version = "P7_B_INTERPRETATION_RECORD_V2";
    // The extended diagnostic response has its own cap; production limits are not silently relaxed.
    public const int ResponseUtf8ByteCap = 262_144;
    public const int InterpretationCharacterCap = 1024;

    public static InterpretationRequest Compose(InterpretationStage stage, DocumentSourceSnapshot source,
        PdfSourceDetails details, IReadOnlyList<SemanticSourceAtom> owned,
        IReadOnlyList<string>? primaryIds = null, string? anchor = null,
        IReadOnlyList<(int Page, string Text)>? context = null,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>? correspondences = null)
    {
        var store = PdfSourceEvidenceStore.Build(source, details);
        var ordered = owned.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        var trusted = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        if (ordered.Length == 0 || ordered.Select(atom => atom.Alias).Distinct().Count() != ordered.Length ||
            ordered.Any(atom => !trusted.TryGetValue(atom.Alias, out var original) ||
                SpatialCanonical.Hash(SpatialCanonical.Bytes(atom)) != SpatialCanonical.Hash(SpatialCanonical.Bytes(original))))
            throw new InvalidOperationException("interpretation-owned-source-mismatch");
        var function = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(ordered, context ?? [],
            correspondences ?? new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>());
        var issued = function.Occurrences.ToArray();
        var byId = issued.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var ids = issued.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        string prompt, controlPrompt, controlUser;
        string[] subjects, visible;
        switch (stage)
        {
            case InterpretationStage.F1:
                if (primaryIds is not null || anchor is not null) throw new InvalidOperationException("interpretation-f1-is-function-only");
                controlPrompt = function.SystemPrompt;
                controlUser = function.UserMessage;
                prompt = Before(controlPrompt, "These labels describe function membership only.");
                subjects = visible = issued.Select(value => value.Id).ToArray();
                break;
            case InterpretationStage.G2A:
                if (anchor is not null || primaryIds is null || primaryIds.Count == 0 ||
                    primaryIds.Distinct().Count() != primaryIds.Count || primaryIds.Any(id => !byId.ContainsKey(id)))
                    throw new InvalidOperationException("interpretation-g2a-issued-invalid");
                subjects = issued.Where(value => primaryIds.Contains(value.Id)).Select(value => value.Id).ToArray();
                visible = issued.Where((value, index) => subjects.Contains(value.Id) ||
                    index > 0 && subjects.Contains(issued[index - 1].Id) ||
                    index + 1 < issued.Length && subjects.Contains(issued[index + 1].Id)).Select(value => value.Id).ToArray();
                controlPrompt = HeadingAnchorProtocolV1.SystemPrompt;
                controlUser = HeadingAnchorProtocolV1.ComposeUserMessage(ordered, ids,
                    subjects.Select(id => (id, byId[id].Atom)).ToArray());
                prompt = Before(controlPrompt, "Return exactly one JSON object with this shape:");
                break;
            case InterpretationStage.H2C:
                if (primaryIds is not null || anchor is null || !byId.ContainsKey(anchor))
                    throw new InvalidOperationException("interpretation-h2c-issued-invalid");
                subjects = [anchor];
                visible = issued.Skip(Array.FindIndex(issued, value => value.Id == anchor)).Select(value => value.Id).ToArray();
                controlPrompt = HeadingExtentProtocolV2.SystemPrompt;
                controlUser = HeadingExtentProtocolV2.ComposeUserMessage(anchor, visible.Select(id => byId[id].Atom.Alias).ToArray(),
                    ids, trusted, source.Evidence.ToDictionary(value => value.SourceAlias, StringComparer.Ordinal));
                prompt = Before(controlPrompt, "Return one JSON object only with root property decisions");
                prompt = prompt.Replace("Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.",
                    "Use supplied source text and parser-owned neutral evidence. Do not infer extent from hierarchy, candidate alternatives, or unissued evidence.", StringComparison.Ordinal);
                break;
            default: throw new InvalidOperationException("interpretation-stage-invalid");
        }
        var mapping = visible.ToDictionary(id => id, id => byId[id].Atom.Alias, StringComparer.Ordinal);
        var entries = store.Entries.ToDictionary(entry => entry.SourceAlias, StringComparer.Ordinal);
        var projection = visible.Select(id => new { occurrence = id, selectable = subjects.Contains(id), source = entries[mapping[id]] }).ToArray();
        var stageName = Name(stage);
        var stageSchema = stage switch
        {
            InterpretationStage.F1 => "stageDecision is {decisions:[{occurrence,function}]}; function is ESTABLISHES_STRUCTURE, REPRESENTS_STRUCTURE, or OTHER. Decide function membership only, never anchors or extent.",
            InterpretationStage.G2A => "stageDecision is {decisions:[{primary,anchor}]}; anchor is HAS_STRUCTURAL_EXTENT or NO_STRUCTURAL_EXTENT. Decide anchor existence only, never extent or function relabeling.",
            _ => "stageDecision is {decisions:[{anchor,headingMembers,endOccurrence,firstOutsideOccurrence,firstOutsideRole}]}; decide exact extent only for the issued anchor. Do not revoke/reclassify anchors. Preserve contiguous prefix, immediate successor, terminal null/NO_VISIBLE_SUCCESSOR, and the five nonterminal roles specified above."
        };
        prompt += "\n\n" + stageSchema + "\n" + """
            This is a versioned qualification interpretation treatment, not the production response contract. Return exactly these root properties: protocolVersion, stage, sourceSha256, evidenceStoreSha256, stageDecision, analysis. Copy version/stage/hashes from this input. stageDecision has only the stage-specific decision fields; do not insert evidence or interpretation into it. Return exactly one stage decision and one analysis record per decisionSubject. Context-only occurrences may be referenced, never selected as decision subjects.
            Each analysis record has exactly subject, references, assertions, interpretation. subject is an issued decisionSubject. references is a nonempty array of {occurrence,sourceAlias,fields}; use only the supplied occurrence-to-sourceAlias mapping and OBSERVED fields. fields is a nonempty list of supplied field names. assertions is an array (may be empty) of {occurrence,sourceAlias,field,value}, each pointing to a field included in that record's references and copying its exact supplied JSON value. Never invent observations or reference NOT_AVAILABLE fields. Coordinates are read-only evidence, not output locators. Exact copied text field values are permitted inside assertions; do not output source text elsewhere. Do not output new spans, confidence, candidate IDs, or relations as authority.
            interpretation is a short decision explanation grounded in those references, at most 1024 characters; not a transcript of internal reasoning. You may describe relevant relationships in your own words; no predefined relationship enum is required. References and copied assertions can be mechanically checked, but your interpretation and semantic decision remain hypotheses to be scored independently. Do not treat geometry, another stage's labels, or reference validity alone as proof of heading semantics. Return JSON only, no additional properties or example identifiers.
            """;
        using var control = JsonDocument.Parse(controlUser);
        var user = SpatialCanonical.Element(new
        {
            protocolVersion = Version, stage = stageName, sourceSha256 = store.SourceSha256,
            evidenceStoreSha256 = store.StoreSha256, sourceAliasUniverseSha256 = store.SourceAliasUniverseSha256,
            decisionSubjects = subjects, stageInput = control.RootElement.Clone(), sourceEvidence = projection,
            interpretationCharacterCap = InterpretationCharacterCap
        }).GetRawText();
        return new(stage, prompt, user, controlPrompt, controlUser, store, Array.AsReadOnly(issued),
            Array.AsReadOnly(subjects), new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(mapping));
    }

    public static InterpretationValidationResult Validate(string raw, InterpretationRequest request,
        DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (Encoding.UTF8.GetByteCount(raw) > ResponseUtf8ByteCap) throw new InvalidOperationException("interpretation-response-cap");
        var expectedStore = PdfSourceEvidenceStore.Build(source, details);
        if (!expectedStore.CanonicalBytes().SequenceEqual(request.EvidenceStore.CanonicalBytes()))
            throw new EvidenceAssessmentException("interpretation-store-source-mismatch", EvidenceAssessmentStatus.INVALID_REFERENCE);
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        RejectDuplicateProperties(root);
        Keys(root, "protocolVersion", "stage", "sourceSha256", "evidenceStoreSha256", "stageDecision", "analysis");
        if (String(root, "protocolVersion") != Version || String(root, "stage") != Name(request.Stage) ||
            String(root, "sourceSha256") != expectedStore.SourceSha256 || String(root, "evidenceStoreSha256") != expectedStore.StoreSha256)
            throw new EvidenceAssessmentException("interpretation-response-identity-mismatch", EvidenceAssessmentStatus.INVALID_REFERENCE);
        var payload = root.GetProperty("stageDecision");
        ValidateStage(payload, request, source);
        var rows = root.GetProperty("analysis");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != request.DecisionSubjects.Count)
            throw new InvalidOperationException("interpretation-analysis-cardinality");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var storeByAlias = expectedStore.Entries.ToDictionary(entry => entry.SourceAlias, StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            Keys(row, "subject", "references", "assertions", "interpretation");
            var subject = String(row, "subject");
            if (!request.DecisionSubjects.Contains(subject) || !seen.Add(subject)) throw new InvalidOperationException("interpretation-subject-invalid");
            var explanation = String(row, "interpretation");
            if (string.IsNullOrWhiteSpace(explanation) || explanation.Length > InterpretationCharacterCap)
                throw new InvalidOperationException("interpretation-explanation-length");
            var references = row.GetProperty("references");
            if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() == 0 || references.GetArrayLength() > request.VisibleAliasByOccurrence.Count)
                throw new InvalidOperationException("interpretation-reference-cardinality");
            var referenced = new HashSet<(string Occurrence, string Field)>();
            var occurrences = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in references.EnumerateArray())
            {
                Keys(reference, "occurrence", "sourceAlias", "fields");
                var id = String(reference, "occurrence");
                var entry = Resolve(reference);
                if (!occurrences.Add(id)) throw new InvalidOperationException("interpretation-reference-duplicate");
                var fields = reference.GetProperty("fields");
                if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0 || fields.GetArrayLength() > entry.Fields.Count)
                    throw new InvalidOperationException("interpretation-fields-cardinality");
                foreach (var fieldName in fields.EnumerateArray())
                {
                    if (fieldName.ValueKind != JsonValueKind.String) throw new InvalidOperationException("interpretation-field-type");
                    var name = fieldName.GetString()!;
                    Available(entry, name);
                    if (!referenced.Add((id, name))) throw new InvalidOperationException("interpretation-field-duplicate");
                }
            }
            var assertions = row.GetProperty("assertions");
            if (assertions.ValueKind != JsonValueKind.Array || assertions.GetArrayLength() > referenced.Count)
                throw new InvalidOperationException("interpretation-assertion-cardinality");
            var asserted = new HashSet<(string Occurrence, string Field)>();
            foreach (var assertion in assertions.EnumerateArray())
            {
                Keys(assertion, "occurrence", "sourceAlias", "field", "value");
                var id = String(assertion, "occurrence");
                var entry = Resolve(assertion);
                var name = String(assertion, "field");
                if (!referenced.Contains((id, name)) || !asserted.Add((id, name))) throw new InvalidOperationException("interpretation-assertion-unreferenced-or-duplicate");
                var field = Available(entry, name);
                if (!JsonElement.DeepEquals(field.Value!.Value, assertion.GetProperty("value")))
                    throw new EvidenceAssessmentException("interpretation-assertion-value-mismatch", EvidenceAssessmentStatus.INVALID_REFERENCE);
            }
        }
        return new(payload.Clone(), rows.Clone());

        SourceEvidenceEntry Resolve(JsonElement row)
        {
            var id = String(row, "occurrence");
            var alias = String(row, "sourceAlias");
            if (!request.VisibleAliasByOccurrence.TryGetValue(id, out var expected) || expected != alias)
                throw new EvidenceAssessmentException("interpretation-reference-source-mismatch", EvidenceAssessmentStatus.INVALID_REFERENCE);
            return storeByAlias[alias];
        }
        static SourceEvidenceField Available(SourceEvidenceEntry entry, string name)
        {
            var field = entry.Fields.SingleOrDefault(field => field.Name == name);
            if (field is null)
                throw new EvidenceAssessmentException("interpretation-field-not-available", EvidenceAssessmentStatus.INVALID_REFERENCE);
            if (field.Availability != "OBSERVED" || field.Value is null)
                throw new EvidenceAssessmentException("interpretation-field-not-available", EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION);
            return field;
        }
    }

    internal static void ValidateStage(JsonElement payload, InterpretationRequest request, DocumentSourceSnapshot source)
    {
        var raw = payload.GetRawText();
        switch (request.Stage)
        {
            case InterpretationStage.F1:
                OccurrenceFunctionProtocolV1.Parse(payload, Encoding.UTF8.GetByteCount(raw), ResponseUtf8ByteCap, request.Owned);
                break;
            case InterpretationStage.G2A:
                HeadingAnchorProtocolV1.Parse(raw, request.DecisionSubjects);
                break;
            case InterpretationStage.H2C:
                var tail = request.Owned.Where(value => request.VisibleAliasByOccurrence.ContainsKey(value.Id)).Select(value => value.Atom.Alias).ToArray();
                HeadingExtentProtocolV2.Bind(raw, request.DecisionSubjects.Single(), tail,
                    request.Owned.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal),
                    source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                break;
            default: throw new InvalidOperationException("interpretation-stage-invalid");
        }
    }

    private static string Before(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("interpretation-base-prompt-drift");
        return value[..index].TrimEnd();
    }
    internal static string Name(InterpretationStage stage) => stage switch
    { InterpretationStage.F1 => "F1", InterpretationStage.G2A => "G2A", InterpretationStage.H2C => "H2-C", _ => throw new InvalidOperationException("interpretation-stage-invalid") };
    private static string String(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String
        ? value.GetProperty(name).GetString()! : throw new InvalidOperationException("interpretation-string-type");
    private static void Keys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("interpretation-schema-invalid");
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidOperationException("interpretation-duplicate-json-property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
