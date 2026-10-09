using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record F1TreatmentDeltaResult(int IssuedOccurrences, bool CarrierEqual,
    bool EmbeddedStageInputByteIdentical, bool IssuedUniverseEqual, bool ParserEvidenceEqual,
    bool SemanticDefinitionsEqual, string Interpretation = "UNVERIFIABLE_ASSERTION_NOT_SEMANTIC_AUTHORITY");

/// <summary>
/// Audits frozen Control/B pairs, not predictions or semantic accuracy. Allowed composite
/// treatment: registered B envelope, raw source evidence and output instructions only.
/// Scoring/response caps are external registered policy, never provider carrier changes.
/// </summary>
internal static class P7F1TreatmentDeltaAudit
{
    public const string Version = "P7_F1_FROZEN_COMPOSITE_B_DELTA_AUDIT_V1";
    public static F1TreatmentDeltaResult Validate(byte[] controlBody, byte[] bBody, byte[] storeBytes,
        byte[] issuedUniverseBytes, string frozenBPromptSha256)
    {
        using var control = JsonDocument.Parse(controlBody); using var b = JsonDocument.Parse(bBody);
        using var store = JsonDocument.Parse(storeBytes); using var issued = JsonDocument.Parse(issuedUniverseBytes);
        var c = control.RootElement; var t = b.RootElement; var s = store.RootElement;
        NoDuplicates(c); NoDuplicates(t); NoDuplicates(s); NoDuplicates(issued.RootElement);
        Keys(t, c.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var p in c.EnumerateObject().Where(p => p.Name != "messages"))
            Need(JsonElement.DeepEquals(p.Value, t.GetProperty(p.Name)), "carrier-drift:" + p.Name);
        var cm = c.GetProperty("messages"); var tm = t.GetProperty("messages");
        Need(cm.GetArrayLength() == 2 && tm.GetArrayLength() == 2, "message-cardinality-drift");
        for (var i = 0; i < 2; i++)
        {
            Keys(cm[i], "role", "content"); Keys(tm[i], "role", "content");
            Need(cm[i].GetProperty("role").GetString() == (i == 0 ? "system" : "user") &&
                JsonElement.DeepEquals(cm[i].GetProperty("role"), tm[i].GetProperty("role")), "message-role-drift");
        }
        var cPrompt = cm[0].GetProperty("content").GetString()!;
        var tPrompt = tm[0].GetProperty("content").GetString()!;
        Need(cPrompt == OccurrenceFunctionProtocolV1.SystemPrompt, "control-prompt-drift");
        Need(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(tPrompt)) == frozenBPromptSha256, "unregistered-B-prompt");
        var marker = cPrompt.IndexOf("These labels describe function membership only.", StringComparison.Ordinal);
        Need(marker >= 0 && tPrompt.StartsWith(cPrompt[..marker].TrimEnd(), StringComparison.Ordinal), "semantic-definition-drift");
        var controlText = cm[1].GetProperty("content").GetString()!;
        using var cu = JsonDocument.Parse(controlText); using var tu = JsonDocument.Parse(tm[1].GetProperty("content").GetString()!);
        NoDuplicates(cu.RootElement); NoDuplicates(tu.RootElement);
        Keys(cu.RootElement, "protocolVersion", "occurrences", "contextOnlyEvidence");
        Keys(tu.RootElement, "protocolVersion", "stage", "sourceSha256", "evidenceStoreSha256", "sourceAliasUniverseSha256",
            "decisionSubjects", "stageInput", "sourceEvidence", "interpretationCharacterCap");
        var u = tu.RootElement;
        Need(u.GetProperty("stageInput").GetRawText() == controlText, "stage-input-byte-drift");
        Need(cu.RootElement.GetProperty("protocolVersion").GetString() == OccurrenceFunctionProtocolV1.Version &&
            u.GetProperty("protocolVersion").GetString() == PdfInterpretationProtocol.Version && u.GetProperty("stage").GetString() == "F1" &&
            u.GetProperty("interpretationCharacterCap").GetInt32() == PdfInterpretationProtocol.InterpretationCharacterCap, "B-contract-drift");
        Need(u.GetProperty("sourceSha256").GetString() == s.GetProperty("sourceSha256").GetString() &&
            u.GetProperty("sourceAliasUniverseSha256").GetString() == s.GetProperty("sourceAliasUniverseSha256").GetString() &&
            u.GetProperty("evidenceStoreSha256").GetString() == SpatialCanonical.Hash(storeBytes), "store-identity-drift");
        var occurrences = cu.RootElement.GetProperty("occurrences").EnumerateArray().ToArray();
        var subjects = u.GetProperty("decisionSubjects").EnumerateArray().Select(p => p.GetString()!).ToArray();
        var evidence = u.GetProperty("sourceEvidence").EnumerateArray().ToArray();
        var mapping = issued.RootElement.EnumerateArray().ToArray();
        Need(subjects.Length > 0 && subjects.Distinct(StringComparer.Ordinal).Count() == subjects.Length &&
            subjects.Length == occurrences.Length && subjects.Length == mapping.Length && subjects.Length == evidence.Length, "issued-cardinality-drift");
        var entries = s.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);
        for (var i = 0; i < subjects.Length; i++)
        {
            var row = evidence[i]; var fact = row.GetProperty("source");
            Keys(row, "occurrence", "selectable", "source"); Keys(mapping[i], "occurrence", "alias", "ordinal", "page");
            Need(occurrences[i].GetProperty("id").GetString() == subjects[i] && mapping[i].GetProperty("occurrence").GetString() == subjects[i] &&
                row.GetProperty("occurrence").GetString() == subjects[i] && row.GetProperty("selectable").GetBoolean(), "issued-order-or-selectability-drift");
            var alias = mapping[i].GetProperty("alias").GetString()!;
            Need(fact.GetProperty("sourceAlias").GetString() == alias && entries.TryGetValue(alias, out var original) &&
                JsonElement.DeepEquals(fact, original) && fact.GetProperty("ordinal").GetInt32() == mapping[i].GetProperty("ordinal").GetInt32(), "parser-evidence-or-alias-binding-drift");
            var fields = fact.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
            Need(fields["text"].GetProperty("value").GetString() == occurrences[i].GetProperty("text").GetString() &&
                fields["page"].GetProperty("value").GetInt32() == occurrences[i].GetProperty("page").GetInt32() &&
                occurrences[i].GetProperty("page").GetInt32() == mapping[i].GetProperty("page").GetInt32(), "source-text-or-page-drift");
        }
        return new(subjects.Length, true, true, true, true, true);
    }
    private static void Keys(JsonElement v, params string[] expected) => Need(
        v.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), "unregistered-fields");
    private static void NoDuplicates(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in v.EnumerateObject()) { Need(names.Add(p.Name), "duplicate-field"); NoDuplicates(p.Value); }
        }
        else if (v.ValueKind == JsonValueKind.Array) foreach (var item in v.EnumerateArray()) NoDuplicates(item);
    }
    private static void Need(bool valid, string reason) { if (!valid) throw new InvalidOperationException("P7_F1_DELTA_REJECTED:" + reason); }
}
