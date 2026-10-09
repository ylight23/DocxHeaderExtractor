using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

[JsonConverter(typeof(JsonStringEnumConverter<P7ExperimentMode>))]
internal enum P7ExperimentMode { SharedF1Root, ControlledDownstream, NaturalEndToEnd }
[JsonConverter(typeof(JsonStringEnumConverter<P7CaptureArm>))]
internal enum P7CaptureArm { Control, B }
internal sealed record P7CaptureIdentity(string Document, string SourceSha256, string SourceUniverseSha256,
    string Pack, string PackSha256, string IssuedUniverseSha256,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InterpretationStage>))] InterpretationStage Stage, P7CaptureArm Arm,
    P7ExperimentMode Mode, string ProviderBodySha256, string SystemPromptSha256, string UserMessageSha256,
    IReadOnlyList<string> ParentCaptureSha256);
internal sealed record P7CaptureReceipt(P7CaptureIdentity Identity, string Origin, string Status,
    string FinishReason, int HttpAttempts, int RetryCount, bool Repair, bool Fallback, string? FailureClass,
    string ResponseSha256, string SseSha256, string ObservationSha256, string RawFreezeSha256,
    string ParsedDecisionSha256, string EndpointMetadataSnapshotSha256);
internal sealed record P7RawCapture(byte[] ProviderBody, byte[] Response, byte[] Sse, byte[] Observation,
    byte[] RawFreeze, P7CaptureReceipt Receipt);
internal sealed record P7ValidatedCapture(P7CaptureIdentity Identity, string CaptureSha256,
    JsonElement StageDecision, string SemanticStatus = "NOT_VERIFIED_PROTOCOL_ONLY");
internal sealed record P7GeneratedStage(InterpretationRequest Request, P7CaptureArm Arm,
    P7ExperimentMode Mode, IReadOnlyList<string> ParentCaptureSha256);
internal sealed record P7CallBounds(int F1, int G2AUpper, int H2CUpper, int TotalUpper,
    long CompletionTokenCeilingTotal, string ActualDownstreamCounts = "UNKNOWN_UNTIL_VALID_UPSTREAM");

/// <summary>Provider-free capture validation and dependency recipes. No inference, Gold,
/// semantic override or missing-ledger substitution. Synthetic examples belong only in tests.</summary>
internal static class P7UpstreamCaptureReadiness
{
    public const string Version = "P7_UPSTREAM_CAPTURE_READINESS_V1";

    public static P7CaptureReceipt ParseReceipt(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        Unique(json.RootElement);
        var identity = json.RootElement.GetProperty("identity");
        Need(Enum.GetNames<InterpretationStage>().Contains(identity.GetProperty("stage").GetString(), StringComparer.Ordinal) &&
            Enum.GetNames<P7CaptureArm>().Contains(identity.GetProperty("arm").GetString(), StringComparer.Ordinal) &&
            Enum.GetNames<P7ExperimentMode>().Contains(identity.GetProperty("mode").GetString(), StringComparer.Ordinal), "unknown-protocol-label");
        return JsonSerializer.Deserialize<P7CaptureReceipt>(bytes, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true }) ?? throw new InvalidOperationException("P7_CAPTURE_RECEIPT_MISSING");
    }

    public static P7CallBounds Bounds(int packs, int ownedOccurrences, bool bothModes)
    {
        if (packs < 1 || ownedOccurrences < packs) throw new ArgumentOutOfRangeException(nameof(packs));
        // Both modes share only the explicitly identical F1 roots. No implicit downstream deduplication.
        var f1 = checked(2 * packs); var multiplier = bothModes ? 4 : 2;
        var g2a = checked(multiplier * packs); var h2c = checked(multiplier * ownedOccurrences);
        var total = checked(f1 + g2a + h2c);
        return new(f1, g2a, h2c, total, (long)total * PdfInferenceWireContract.CompletionTokenCeiling);
    }

    public static P7ValidatedCapture Validate(P7RawCapture? capture, P7CaptureIdentity expected,
        InterpretationRequest request, DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (capture is null) throw new InvalidOperationException("P7_CAPTURE_MISSING_NO_DOWNSTREAM");
        var r = capture.Receipt;
        Need(Enum.IsDefined(expected.Arm) && Enum.IsDefined(expected.Stage) && Enum.IsDefined(expected.Mode), "unknown-protocol-label");
        Need(SpatialCanonical.Bytes(r.Identity).SequenceEqual(SpatialCanonical.Bytes(expected)), "identity-or-lineage-drift");
        Need(r.Origin == "PROVIDER_RAW" && r.Status == "RAW_FROZEN_BEFORE_PARSE" && r.FailureClass is null,
            "not-successful-provider-origin");
        Need(r.FinishReason == "stop" && r.HttpAttempts == 1 && r.RetryCount == 0 && !r.Repair && !r.Fallback,
            "execution-policy-invalid");
        Need(expected.Document == DocumentId(source) && expected.Stage == request.Stage && expected.SourceSha256 == source.SourceSha256 &&
            expected.SourceUniverseSha256 == source.SourceAliasUniverseHash, "source-or-stage-drift");
        Need(expected.Pack.Length > 0 && expected.Document.Length > 0 && IsHash(expected.PackSha256) &&
            expected.IssuedUniverseSha256 == IssueHash(request) && IsHash(r.EndpointMetadataSnapshotSha256), "binding-incomplete");
        Need(SpatialCanonical.Hash(capture.ProviderBody) == expected.ProviderBodySha256 &&
            SpatialCanonical.Hash(capture.Response) == r.ResponseSha256 && SpatialCanonical.Hash(capture.Sse) == r.SseSha256 &&
            SpatialCanonical.Hash(capture.Observation) == r.ObservationSha256 && SpatialCanonical.Hash(capture.RawFreeze) == r.RawFreezeSha256,
            "raw-hash-drift");
        var isB = expected.Arm == P7CaptureArm.B;
        Need(expected.SystemPromptSha256 == TextHash(isB ? request.SystemPrompt : request.ControlSystemPrompt) &&
            expected.UserMessageSha256 == TextHash(isB ? request.UserMessage : request.ControlUserMessage), "request-projection-drift");
        using var body = JsonDocument.Parse(capture.ProviderBody);
        var messages = body.RootElement.GetProperty("messages");
        Need(messages.GetArrayLength() == 2 && TextHash(messages[0].GetProperty("content").GetString()!) == expected.SystemPromptSha256 &&
            TextHash(messages[1].GetProperty("content").GetString()!) == expected.UserMessageSha256, "body-message-hash-drift");
        using var marker = JsonDocument.Parse(capture.RawFreeze);
        Need(marker.RootElement.GetProperty("status").GetString() == "RAW_FROZEN_BEFORE_PARSE", "freeze-boundary-missing");
        var hashes = marker.RootElement.GetProperty("hashes").EnumerateArray().ToArray();
        Need(hashes.Length == 3 && hashes.Select(h => h.GetProperty("file").GetString()).Distinct().Count() == 3,
            "freeze-file-set-invalid");
        foreach (var file in new[] { ("provider-body.json", expected.ProviderBodySha256), ("response.txt", r.ResponseSha256), ("response.sse", r.SseSha256) })
            Need(hashes.Any(h => h.GetProperty("file").GetString() == file.Item1 && h.GetProperty("sha256").GetString() == file.Item2), "freeze-lineage-drift");
        using var observation = JsonDocument.Parse(capture.Observation);
        var o = observation.RootElement;
        Need(o.GetProperty("FinishReason").GetString() == "stop" && o.GetProperty("RetryCount").GetInt32() == 0 &&
            TextHash(o.GetProperty("Content").GetString()!) == r.ResponseSha256 &&
            TextHash(o.GetProperty("RawSse").GetString()!) == r.SseSha256 && capture.Sse.Length > 0, "observation-drift");
        if (expected.Stage == InterpretationStage.F1)
            Need(expected.Mode == P7ExperimentMode.SharedF1Root && expected.ParentCaptureSha256.Count == 0, "f1-root-lineage-invalid");
        else Need(expected.Mode != P7ExperimentMode.SharedF1Root && expected.ParentCaptureSha256.Count == (expected.Stage == InterpretationStage.G2A ? 1 : 2) &&
            expected.ParentCaptureSha256.All(IsHash) && expected.ParentCaptureSha256.Distinct().Count() == expected.ParentCaptureSha256.Count, "upstream-lineage-incomplete");
        var raw = Encoding.UTF8.GetString(capture.Response);
        var cap = isB ? PdfInterpretationProtocol.ResponseUtf8ByteCap : PdfInferenceWireContract.ResponseUtf8ByteCap;
        Need(capture.Response.Length <= cap, "response-cap");
        using var json = JsonDocument.Parse(capture.Response);
        JsonElement decision;
        if (isB) decision = PdfInterpretationProtocol.Validate(raw, request, source, details).StageDecision;
        else { PdfInterpretationProtocol.ValidateStage(json.RootElement, request, source); decision = json.RootElement; }
        var normalized = SpatialCanonical.Bytes(decision);
        Need(SpatialCanonical.Hash(normalized) == r.ParsedDecisionSha256, "parsed-ledger-drift");
        return new(expected with { ParentCaptureSha256 = Array.AsReadOnly(expected.ParentCaptureSha256.ToArray()) },
            SpatialCanonical.Hash(SpatialCanonical.Bytes(r)), decision.Clone());
    }

    public static P7GeneratedStage? G2A(P7ExperimentMode mode, P7CaptureArm arm, P7ValidatedCapture f1,
        DocumentSourceSnapshot source, PdfSourceDetails details, SemanticEvidencePack pack)
    {
        Parent(mode, arm, f1, InterpretationStage.F1, source, pack);
        var request = P7PilotRequestPreflight.G2A(source, details, pack, f1.StageDecision.GetRawText(), "stop");
        return request is null ? null : new(request, arm, mode, Array.AsReadOnly(new[] { f1.CaptureSha256 }));
    }

    public static IReadOnlyList<P7GeneratedStage> H2C(P7ExperimentMode mode, P7CaptureArm arm,
        P7ValidatedCapture f1, P7ValidatedCapture g2a, DocumentSourceSnapshot source, PdfSourceDetails details, SemanticEvidencePack pack)
    {
        Parent(mode, arm, f1, InterpretationStage.F1, source, pack);
        Parent(mode, arm, g2a, InterpretationStage.G2A, source, pack);
        Need(g2a.Identity.Mode == mode && g2a.Identity.ParentCaptureSha256.SequenceEqual(new[] { f1.CaptureSha256 }), "g2a-parent-or-mode-drift");
        return P7PilotRequestPreflight.H2C(source, details, pack, f1.StageDecision.GetRawText(), "stop", g2a.StageDecision.GetRawText(), "stop")
            .Select(request => new P7GeneratedStage(request, arm, mode, Array.AsReadOnly(new[] { f1.CaptureSha256, g2a.CaptureSha256 }))).ToArray();
    }

    public static string IssueHash(InterpretationRequest request) => SpatialCanonical.Hash(SpatialCanonical.Bytes(request.Owned.Select(o =>
        new { occurrence = o.Id, alias = o.Atom.Alias, ordinal = o.Atom.Ordinal, page = o.Atom.Page }).ToArray()));

    private static void Parent(P7ExperimentMode mode, P7CaptureArm arm, P7ValidatedCapture parent,
        InterpretationStage stage, DocumentSourceSnapshot source, SemanticEvidencePack pack)
    {
        Need(mode is P7ExperimentMode.ControlledDownstream or P7ExperimentMode.NaturalEndToEnd, "generation-mode-invalid");
        Need(parent.Identity.Stage == stage && parent.Identity.Arm == (mode == P7ExperimentMode.ControlledDownstream ? P7CaptureArm.Control : arm), "upstream-arm-or-stage-invalid");
        Need(parent.Identity.SourceSha256 == source.SourceSha256 && parent.Identity.SourceUniverseSha256 == source.SourceAliasUniverseHash &&
            parent.Identity.Document == DocumentId(source) && parent.Identity.Pack == pack.PackId &&
            parent.Identity.PackSha256 == SpatialCanonical.Hash(SpatialCanonical.Bytes(pack)), "upstream-source-or-pack-drift");
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string DocumentId(DocumentSourceSnapshot source) => string.IsNullOrEmpty(source.DocumentId) ? "PDF-" + source.SourceSha256 : source.DocumentId;
    private static string TextHash(string text) => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(text));
    private static void Unique(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in v.EnumerateObject()) { Need(names.Add(p.Name), "duplicate-receipt-field"); Unique(p.Value); }
        } else if (v.ValueKind == JsonValueKind.Array) foreach (var row in v.EnumerateArray()) Unique(row);
    }
    private static void Need(bool valid, string error) { if (!valid) throw new InvalidOperationException("P7_CAPTURE_REJECTED:" + error); }
}
