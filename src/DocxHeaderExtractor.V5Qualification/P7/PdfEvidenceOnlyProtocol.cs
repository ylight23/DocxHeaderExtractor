using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record EvidenceOnlyRequest(InterpretationRequest SharedInput, string SystemPrompt, string UserMessage);

/// <summary>Treatment A: exact same raw evidence and stage rules as B; no analysis/references.</summary>
internal static class PdfEvidenceOnlyProtocol
{
    public const string Version = "P7_A_RAW_EVIDENCE_ONLY_V1";

    public static EvidenceOnlyRequest Compose(InterpretationRequest shared)
    {
        const string marker = "This is a versioned qualification interpretation treatment";
        var index = shared.SystemPrompt.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("evidence-only-shared-prompt-drift");
        // The common semantic prefix includes the same stage decision rules, including exact
        // heading membership/successor constraints. Only response/analysis instructions differ.
        var prompt = shared.SystemPrompt[..index] + """
            This is a versioned raw-evidence qualification treatment. Return exactly protocolVersion, stage, sourceSha256, evidenceStoreSha256, stageDecision. Copy version/stage/hashes from the input. stageDecision has only the stage-specific decision fields described above. Return one decision per decisionSubject. Context-only items may inform decisions but cannot be selected as subjects. Use supplied raw parser evidence as read-only observations, not semantic proof. Do not output analysis, references, assertions, interpretation, confidence, source text, new spans or additional properties. Return JSON only; use actual issued handles, not example identifiers.
            """;
        var node = JsonNode.Parse(shared.UserMessage)!.AsObject();
        node["protocolVersion"] = Version;
        node.Remove("interpretationCharacterCap"); // B-only output metadata, not source evidence
        return new(shared, prompt, node.ToJsonString(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public static JsonElement Validate(string raw, EvidenceOnlyRequest request, DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (Encoding.UTF8.GetByteCount(raw) > PdfInterpretationProtocol.ResponseUtf8ByteCap)
            throw new InvalidOperationException("evidence-only-response-cap");
        if (!PdfSourceEvidenceStore.Build(source, details).CanonicalBytes().SequenceEqual(request.SharedInput.EvidenceStore.CanonicalBytes()))
            throw new InvalidOperationException("evidence-only-store-source-mismatch");
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        RejectDuplicates(root);
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "evidenceStoreSha256", "protocolVersion", "sourceSha256", "stage", "stageDecision" }))
            throw new InvalidOperationException("evidence-only-root-invalid");
        bool Matches(string name, string expected) => root.GetProperty(name).ValueKind == JsonValueKind.String && root.GetProperty(name).GetString() == expected;
        if (!Matches("protocolVersion", Version) || !Matches("stage", PdfInterpretationProtocol.Name(request.SharedInput.Stage)) ||
            !Matches("sourceSha256", request.SharedInput.EvidenceStore.SourceSha256) || !Matches("evidenceStoreSha256", request.SharedInput.EvidenceStore.StoreSha256))
            throw new InvalidOperationException("evidence-only-identity-invalid");
        var payload = root.GetProperty("stageDecision");
        PdfInterpretationProtocol.ValidateStage(payload, request.SharedInput, source);
        return payload.Clone();
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidOperationException("evidence-only-duplicate-key");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
