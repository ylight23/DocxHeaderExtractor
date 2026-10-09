using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal enum PageContextFactStatus { FACT_VERIFIED, FACT_CONTRADICTED, FACT_NOT_VERIFIABLE }
internal sealed record PageContextFactAssessment(string Subject, string SourceAlias, string Field,
    PageContextFactStatus Status, string Code);
internal sealed record PageContextEvidenceValidationResult(bool Accepted, JsonElement StageDecision,
    IReadOnlyList<PageContextFactAssessment> Facts,
    string InterpretationStatus = "UNVERIFIABLE_ASSERTION",
    string SemanticStatus = "NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW");

/// <summary>Qualification-only verification seam, NOT a provider treatment composer or semantic
/// authority. Checks exact parser observations, never a model's relational/semantic interpretation.</summary>
internal static class PdfPageContextEvidenceValidator
{
    public const int ResponseUtf8ByteCap = 262_144;

    // Engineering envelope only; no experiment request/schema is promoted or silently rewritten.
    // Root: sourceSha256,evidenceStoreSha256,contextSha256,stageDecision,analysis.
    // Analysis: subject,references,assertions,interpretation.
    // Reference: sourceAlias,sourceIdSha256,page,spanStart,spanEnd,fields.
    // Assertion: sourceAlias,field,value (exact copied raw field; no relational predicates).
    public static PageContextEvidenceValidationResult Validate(string raw, InterpretationRequest request,
        PdfPageEvidenceContext context, IReadOnlyList<string> expectedContextSubjects,
        PageContextPolicy expectedPolicy, DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (Encoding.UTF8.GetByteCount(raw) > ResponseUtf8ByteCap)
            throw new InvalidOperationException("page-evidence-response-cap");
        var trustedStore = PdfSourceEvidenceStore.Build(source, details);
        if (!trustedStore.CanonicalBytes().SequenceEqual(request.EvidenceStore.CanonicalBytes()))
            throw new InvalidOperationException("page-evidence-store-source-mismatch");
        var trustedContext = PdfPageEvidenceContextBuilder.Build(trustedStore, expectedContextSubjects, expectedPolicy);
        if (!trustedContext.CanonicalBytes().SequenceEqual(context.CanonicalBytes()))
            throw new InvalidOperationException("page-evidence-context-source-policy-mismatch");
        ValidateOriginalScope(request, source);
        using var json = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        RejectDuplicates(root);
        Keys(root, "sourceSha256", "evidenceStoreSha256", "contextSha256", "stageDecision", "analysis");
        var facts = new List<PageContextFactAssessment>();
        if (Text(root, "sourceSha256") != trustedStore.SourceSha256 ||
            Text(root, "evidenceStoreSha256") != trustedStore.StoreSha256 ||
            Text(root, "contextSha256") != trustedContext.ContextSha256)
            return new(false, root.GetProperty("stageDecision").Clone(),
                [new("", "", "identity", PageContextFactStatus.FACT_CONTRADICTED, "snapshot-store-context-identity-mismatch")]);

        // Critical: context is NOT turned into Owned/VisibleAliasByOccurrence. The original
        // issuance still owns decisions, members, endpoint and immediate successor.
        var decision = root.GetProperty("stageDecision");
        PdfInterpretationProtocol.ValidateStage(decision, request, source);
        var entries = trustedStore.Entries.ToDictionary(x => x.SourceAlias, StringComparer.Ordinal);
        var permitted = trustedContext.Pages.SelectMany(x => x.Observations).Select(x => x.SourceAlias)
            .Concat(request.VisibleAliasByOccurrence.Values).ToHashSet(StringComparer.Ordinal);
        var rows = root.GetProperty("analysis");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != request.DecisionSubjects.Count)
            throw new InvalidOperationException("page-evidence-analysis-cardinality");
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            Keys(row, "subject", "references", "assertions", "interpretation");
            var subject = Text(row, "subject");
            if (!request.DecisionSubjects.Contains(subject) || !subjects.Add(subject))
                throw new InvalidOperationException("page-evidence-subject-invalid");
            var interpretation = Text(row, "interpretation");
            if (string.IsNullOrWhiteSpace(interpretation) || interpretation.Length > PdfInterpretationProtocol.InterpretationCharacterCap)
                throw new InvalidOperationException("page-evidence-interpretation-length");
            var references = row.GetProperty("references");
            if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() == 0 || references.GetArrayLength() > permitted.Count)
                throw new InvalidOperationException("page-evidence-reference-cardinality");
            var referred = new HashSet<(string Alias, string Field)>();
            var validAliases = new HashSet<string>(StringComparer.Ordinal);
            var seenAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in references.EnumerateArray())
            {
                Keys(reference, "sourceAlias", "sourceIdSha256", "page", "spanStart", "spanEnd", "fields");
                var alias = Text(reference, "sourceAlias");
                if (!seenAliases.Add(alias)) throw new InvalidOperationException("page-evidence-reference-duplicate");
                if (!permitted.Contains(alias) || !entries.TryGetValue(alias, out var entry))
                { Add(alias, "identity", PageContextFactStatus.FACT_CONTRADICTED, "unknown-or-out-of-context-source"); continue; }
                var page = entry.Fields.Single(x => x.Name == "page");
                if (Text(reference, "sourceIdSha256") != entry.SourceIdSha256 ||
                    reference.GetProperty("spanStart").GetInt32() != entry.SpanStart ||
                    reference.GetProperty("spanEnd").GetInt32() != entry.SpanEnd)
                { Add(alias, "identity", PageContextFactStatus.FACT_CONTRADICTED, "source-span-mismatch"); continue; }
                if (page.Availability != "OBSERVED" || page.Value is null)
                { Add(alias, "page", PageContextFactStatus.FACT_NOT_VERIFIABLE, "parser-page-unavailable"); continue; }
                if (!JsonElement.DeepEquals(reference.GetProperty("page"), page.Value.Value))
                { Add(alias, "page", PageContextFactStatus.FACT_CONTRADICTED, "page-mismatch"); continue; }
                validAliases.Add(alias);
                var names = reference.GetProperty("fields");
                if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0 || names.GetArrayLength() > entry.Fields.Count)
                    throw new InvalidOperationException("page-evidence-field-cardinality");
                foreach (var value in names.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("page-evidence-field-type");
                    var name = value.GetString()!;
                    if (!referred.Add((alias, name))) throw new InvalidOperationException("page-evidence-field-duplicate");
                    var field = entry.Fields.SingleOrDefault(x => x.Name == name);
                    Add(alias, name, field is null ? PageContextFactStatus.FACT_CONTRADICTED :
                        field.Availability != "OBSERVED" || field.Value is null ? PageContextFactStatus.FACT_NOT_VERIFIABLE :
                        PageContextFactStatus.FACT_VERIFIED, field is null ? "unknown-field" :
                        field.Availability != "OBSERVED" || field.Value is null ? "parser-measurement-unavailable" : "parser-reference-verified");
                }
            }
            var assertions = row.GetProperty("assertions");
            // Invalid references must still produce a contradicted assessment rather than
            // accidentally becoming a cardinality error because their fields were not bound.
            if (assertions.ValueKind != JsonValueKind.Array || assertions.GetArrayLength() > references.GetArrayLength() * entries.Values.Max(x => x.Fields.Count))
                throw new InvalidOperationException("page-evidence-assertion-cardinality");
            var asserted = new HashSet<(string Alias, string Field)>();
            foreach (var assertion in assertions.EnumerateArray())
            {
                Keys(assertion, "sourceAlias", "field", "value");
                var alias = Text(assertion, "sourceAlias"); var name = Text(assertion, "field");
                if (!asserted.Add((alias, name))) throw new InvalidOperationException("page-evidence-assertion-duplicate");
                if (!validAliases.Contains(alias) || !referred.Contains((alias, name)))
                { Add(alias, name, PageContextFactStatus.FACT_CONTRADICTED, "assertion-unreferenced-or-invalid-source"); continue; }
                var field = entries[alias].Fields.SingleOrDefault(x => x.Name == name);
                if (field is null) Add(alias, name, PageContextFactStatus.FACT_CONTRADICTED, "unknown-field");
                else if (field.Availability != "OBSERVED" || field.Value is null)
                    Add(alias, name, PageContextFactStatus.FACT_NOT_VERIFIABLE, "parser-measurement-unavailable");
                else Add(alias, name, JsonElement.DeepEquals(assertion.GetProperty("value"), field.Value.Value) ?
                    PageContextFactStatus.FACT_VERIFIED : PageContextFactStatus.FACT_CONTRADICTED,
                    JsonElement.DeepEquals(assertion.GetProperty("value"), field.Value.Value) ? "copied-value-verified" : "copied-value-contradicted");
            }
            void Add(string alias, string field, PageContextFactStatus status, string code) => facts.Add(new(subject, alias, field, status, code));
        }
        return new(facts.All(x => x.Status == PageContextFactStatus.FACT_VERIFIED), decision.Clone(), Array.AsReadOnly(facts.ToArray()));
    }

    private static void ValidateOriginalScope(InterpretationRequest request, DocumentSourceSnapshot source)
    {
        var atoms = source.Atoms.ToDictionary(x => x.Alias, StringComparer.Ordinal);
        if (request.Owned.Count == 0 || request.Owned.Select(x => x.Id).Distinct().Count() != request.Owned.Count ||
            request.Owned.Select(x => x.Atom.Alias).Distinct().Count() != request.Owned.Count ||
            !request.Owned.Select(x => x.Atom.Ordinal).SequenceEqual(request.Owned.Select(x => x.Atom.Ordinal).Order()) ||
            request.Owned.Where((x, i) => x.Id != $"O{i + 1}" || !atoms.TryGetValue(x.Atom.Alias, out var atom) ||
                !SpatialCanonical.Bytes(atom).SequenceEqual(SpatialCanonical.Bytes(x.Atom))).Any() ||
            request.DecisionSubjects.Count == 0 || request.DecisionSubjects.Distinct().Count() != request.DecisionSubjects.Count ||
            request.DecisionSubjects.Any(id => !request.Owned.Any(x => x.Id == id)))
            throw new InvalidOperationException("page-evidence-original-issuance-invalid");
        var owned = request.Owned.ToArray();
        var visible = request.Stage switch
        {
            InterpretationStage.F1 => owned,
            InterpretationStage.G2A => owned.Where((x, i) => request.DecisionSubjects.Contains(x.Id) ||
                i > 0 && request.DecisionSubjects.Contains(owned[i - 1].Id) ||
                i + 1 < owned.Length && request.DecisionSubjects.Contains(owned[i + 1].Id)).ToArray(),
            InterpretationStage.H2C when request.DecisionSubjects.Count == 1 => owned.Skip(Array.FindIndex(owned, x => x.Id == request.DecisionSubjects[0])).ToArray(),
            _ => throw new InvalidOperationException("page-evidence-original-stage-invalid")
        };
        if (request.Stage == InterpretationStage.F1 && !request.DecisionSubjects.SequenceEqual(owned.Select(x => x.Id)) ||
            visible.Length != request.VisibleAliasByOccurrence.Count || visible.Any(x =>
                !request.VisibleAliasByOccurrence.TryGetValue(x.Id, out var alias) || alias != x.Atom.Alias))
            throw new InvalidOperationException("page-evidence-original-visible-scope-invalid");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String ?
        value.GetProperty(name).GetString()! : throw new InvalidOperationException("page-evidence-string-type");
    private static void Keys(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal))) throw new InvalidOperationException("page-evidence-schema-invalid");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidOperationException("page-evidence-duplicate-json-property"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
