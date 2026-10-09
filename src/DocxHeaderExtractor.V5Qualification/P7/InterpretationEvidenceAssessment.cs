using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal enum EvidenceAssessmentStatus { VERIFIED_FACTS, UNVERIFIABLE_ASSERTION, INVALID_REFERENCE }
internal sealed class EvidenceAssessmentException(string code, EvidenceAssessmentStatus status)
    : InvalidOperationException(code)
{
    public EvidenceAssessmentStatus Status { get; } = status;
}
internal sealed record InterpretationEvidenceAssessment(string ContractStatus,
    EvidenceAssessmentStatus? PhysicalEvidenceStatus, EvidenceAssessmentStatus InterpretationStatus,
    string SemanticDecisionStatus, string? FailureCode);

/// <summary>Diagnostic dimensions, never a claim that the interpretation is verified.</summary>
internal static class InterpretationEvidenceAssessor
{
    public static InterpretationEvidenceAssessment Assess(string raw, InterpretationRequest request,
        DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        try
        {
            PdfInterpretationProtocol.Validate(raw, request, source, details);
            return new("ACCEPTED", EvidenceAssessmentStatus.VERIFIED_FACTS,
                EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION, "NOT_EVALUATED", null);
        }
        catch (EvidenceAssessmentException exception)
        {
            return new("REJECTED", exception.Status, EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION,
                "NOT_EVALUATED", exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Malformed protocol/decision schema is not automatically an evidence hallucination.
            return new("REJECTED", null, EvidenceAssessmentStatus.UNVERIFIABLE_ASSERTION,
                "NOT_EVALUATED", exception is JsonException ? "JSON_INVALID" : exception.Message);
        }
    }
}
