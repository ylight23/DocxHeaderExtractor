using System.Globalization;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval.R18;

public enum R18DiagnosticStatus
{
    [JsonStringEnumMemberName("NOT_APPLICABLE")] NotApplicable,
    [JsonStringEnumMemberName("PASS")] Pass,
    [JsonStringEnumMemberName("FAIL")] Fail,
}

public enum R18DiagnosticKind
{
    [JsonStringEnumMemberName("MARKER_SEQUENCE")] MarkerSequence,
    [JsonStringEnumMemberName("HIERARCHY_CONSTRAINT")] HierarchyConstraint,
    [JsonStringEnumMemberName("SIBLING_CONSISTENCY")] SiblingConsistency,
}

public sealed record R18DiagnosticObservation
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("diagnostic")] public required R18DiagnosticKind Diagnostic { get; init; }
    [JsonPropertyName("status")] public required R18DiagnosticStatus Status { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record R18DiagnosticMetrics
{
    [JsonPropertyName("diagnostic")] public required R18DiagnosticKind Diagnostic { get; init; }
    [JsonPropertyName("applicable")] public int Applicable { get; init; }
    [JsonPropertyName("alerts")] public int Alerts { get; init; }
    [JsonPropertyName("trueErrorAlerts")] public int? TrueErrorAlerts { get; init; }
    [JsonPropertyName("falseAlerts")] public int? FalseAlerts { get; init; }
    [JsonPropertyName("relevantErrors")] public int? RelevantErrors { get; init; }
    [JsonPropertyName("detectedErrors")] public int? DetectedErrors { get; init; }
    [JsonPropertyName("precision")] public double? Precision { get; init; }
    [JsonPropertyName("recall")] public double? Recall { get; init; }
    [JsonPropertyName("pFinalErrorGivenFail")] public double? PFinalErrorGivenFail { get; init; }
    [JsonPropertyName("pFinalErrorGivenPass")] public double? PFinalErrorGivenPass { get; init; }
}

public sealed record R18DeterministicDiagnosticsReport
{
    [JsonPropertyName("observations")] public required IReadOnlyList<R18DiagnosticObservation> Observations { get; init; }
    [JsonPropertyName("metrics")] public required IReadOnlyList<R18DiagnosticMetrics> Metrics { get; init; }
    [JsonPropertyName("qualityClaim")] public string QualityClaim { get; init; } = "NOT_MEASURED_WITHOUT_REFERENCE";
}

/// <summary>
/// Evaluation-only checks over parser and resolved structure facts. These checks are intentionally
/// tri-state and never reject a candidate or mutate the production structure.
/// </summary>
public static class R18DeterministicDiagnostics
{
    public static R18DeterministicDiagnosticsReport Analyze(
        SourceDocument source,
        AuthorityPipelineExecutionResult execution,
        IReadOnlyList<R18DecisionObservation> decisionObservations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(decisionObservations);

        var auditFacts = execution.CompatibilityOutline.RouteAudit?.HierarchyFacts ?? [];
        var factsBySourceId = auditFacts
            .GroupBy(fact => fact.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceById = source.Paragraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var elementsBySourceId = execution.Result.Structure.Elements
            .SelectMany(element => element.Sources.Select(reference => (reference.SourceId, Element: element)))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Element, StringComparer.Ordinal);
        var elementsById = execution.Result.Structure.Elements
            .ToDictionary(element => element.Id, StringComparer.Ordinal);
        var observations = new List<R18DiagnosticObservation>(source.Paragraphs.Count * 3);

        foreach (var paragraph in source.Paragraphs.OrderBy(item => item.SourceOrdinal))
        {
            factsBySourceId.TryGetValue(paragraph.SourceId, out var fact);
            elementsBySourceId.TryGetValue(paragraph.SourceId, out var element);
            observations.Add(MarkerSequence(paragraph, fact));
            observations.Add(HierarchyConstraint(paragraph, fact, element, elementsById, sourceById));
            observations.Add(SiblingConsistency(fact, auditFacts));
        }

        var metrics = Enum.GetValues<R18DiagnosticKind>()
            .Select(kind => BuildMetrics(kind, observations, decisionObservations))
            .ToArray();
        var hasComparableReference = decisionObservations.Any(item => item.Reference.IsComparable);
        return new R18DeterministicDiagnosticsReport
        {
            Observations = observations,
            Metrics = metrics,
            QualityClaim = hasComparableReference
                ? "MEASURED_AGAINST_REFERENCE_BACKED_OBSERVATIONS"
                : "NOT_MEASURED_WITHOUT_REFERENCE",
        };
    }

    private static R18DiagnosticObservation MarkerSequence(SourceParagraph paragraph, PdfHierarchyFactAudit? fact)
    {
        if (fact is not null && (fact.MarkerFamily is not null || fact.MarkerPath is not null || fact.MarkerComponents.Count > 0))
        {
            if (fact.MarkerDepth is not > 0)
                return Failure(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "marker-depth-missing");

            var path = ParsePath(fact.MarkerPath);
            if (path is not null && fact.MarkerComponents.Count > 0 && !path.SequenceEqual(fact.MarkerComponents))
                return Failure(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "marker-path-components-disagree");
            if (fact.MarkerComponents.Count > 0 && fact.MarkerDepth != fact.MarkerComponents.Count)
                return Failure(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "marker-depth-components-disagree");
            if (path is not null && fact.ResolvedLevel is { } resolved && path.Length != resolved)
                return Failure(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "marker-path-level-disagree");
            return Pass(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "parser-marker-facts-coherent");
        }

        var numbering = paragraph.Numbering;
        var hasNumberingEvidence = numbering.NumberingId is not null ||
            numbering.NumberingLevel is not null ||
            !string.IsNullOrWhiteSpace(numbering.NumberLabel);
        if (!hasNumberingEvidence)
            return NotApplicable(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "no-marker-facts");
        return numbering.NumberingLevel is < 0 or > 8
            ? Failure(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "numbering-level-out-of-range")
            : Pass(paragraph.SourceId, R18DiagnosticKind.MarkerSequence, "source-numbering-facts-coherent");
    }

    private static R18DiagnosticObservation HierarchyConstraint(
        SourceParagraph paragraph,
        PdfHierarchyFactAudit? fact,
        ValidatedStructuralElement? element,
        IReadOnlyDictionary<string, ValidatedStructuralElement> elementsById,
        IReadOnlyDictionary<string, SourceParagraph> sourceById)
    {
        if (fact?.ResolvedLevel is not { } resolvedLevel || element is null)
            return NotApplicable(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "resolved-level-or-element-not-observable");
        if (resolvedLevel is < 1 or > 9)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "resolved-level-out-of-range");
        if (element.Level != resolvedLevel)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "final-level-differs-from-resolved-marker-level");

        var parent = element.ParentId is not null && elementsById.TryGetValue(element.ParentId, out var parentElement)
            ? parentElement
            : null;
        if (resolvedLevel == 1 && parent is not null)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "level-one-has-parent");
        if (resolvedLevel > 1 && parent is null)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "nested-level-missing-parent");
        if (parent is null)
            return Pass(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "root-level-constraint-satisfied");

        var parentSourceId = parent.Sources.FirstOrDefault()?.SourceId;
        if (parentSourceId is null || !sourceById.TryGetValue(parentSourceId, out var parentSource))
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "parent-source-not-grounded");
        if (parentSource.SourceOrdinal >= paragraph.SourceOrdinal)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "parent-does-not-precede-child");
        if (parent.Level is not { } parentLevel || parentLevel >= resolvedLevel)
            return Failure(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "parent-level-not-less-than-child");
        return Pass(paragraph.SourceId, R18DiagnosticKind.HierarchyConstraint, "parent-and-level-constraints-satisfied");
    }

    private static R18DiagnosticObservation SiblingConsistency(
        PdfHierarchyFactAudit? fact,
        IReadOnlyList<PdfHierarchyFactAudit> facts)
    {
        if (fact is null || fact.MarkerPrefixParentCandidate is null)
            return NotApplicable(fact?.Id ?? "unknown", R18DiagnosticKind.SiblingConsistency,
                "sibling-relation-not-observable");
        var parent = facts.FirstOrDefault(item => string.Equals(item.Id,
            fact.MarkerPrefixParentCandidate, StringComparison.Ordinal));
        return parent is not null && parent.SourceOrder < fact.SourceOrder
            ? Pass(fact.Id, R18DiagnosticKind.SiblingConsistency, "marker-prefix-neighbor-precedes-source")
            : Failure(fact.Id, R18DiagnosticKind.SiblingConsistency, "marker-prefix-neighbor-not-grounded");
    }

    private static R18DiagnosticMetrics BuildMetrics(
        R18DiagnosticKind kind,
        IReadOnlyList<R18DiagnosticObservation> diagnosticObservations,
        IReadOnlyList<R18DecisionObservation> decisions)
    {
        var observations = diagnosticObservations.Where(item => item.Diagnostic == kind).ToArray();
        var bySource = decisions.GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var evaluated = observations
            .Select(item => (Observation: item, Error: bySource.TryGetValue(item.SourceId, out var decision)
                ? FinalError(decision)
                : null))
            .Where(item => item.Error is not null)
            .ToArray();
        var alerts = observations.Count(item => item.Status == R18DiagnosticStatus.Fail);
        if (evaluated.Length == 0)
            return new R18DiagnosticMetrics { Diagnostic = kind, Applicable = observations.Count(item => item.Status != R18DiagnosticStatus.NotApplicable), Alerts = alerts };

        var trueAlerts = evaluated.Count(item => item.Observation.Status == R18DiagnosticStatus.Fail && item.Error == true);
        var falseAlerts = evaluated.Count(item => item.Observation.Status == R18DiagnosticStatus.Fail && item.Error == false);
        var relevantErrors = evaluated.Count(item => item.Error == true);
        var pass = evaluated.Count(item => item.Observation.Status == R18DiagnosticStatus.Pass);
        var passErrors = evaluated.Count(item => item.Observation.Status == R18DiagnosticStatus.Pass && item.Error == true);
        return new R18DiagnosticMetrics
        {
            Diagnostic = kind,
            Applicable = observations.Count(item => item.Status != R18DiagnosticStatus.NotApplicable),
            Alerts = alerts,
            TrueErrorAlerts = trueAlerts,
            FalseAlerts = falseAlerts,
            RelevantErrors = relevantErrors,
            DetectedErrors = trueAlerts,
            Precision = alerts == 0 ? null : (double)trueAlerts / alerts,
            Recall = relevantErrors == 0 ? null : (double)trueAlerts / relevantErrors,
            PFinalErrorGivenFail = alerts == 0 ? null : (double)trueAlerts / alerts,
            PFinalErrorGivenPass = pass == 0 ? null : (double)passErrors / pass,
        };
    }

    private static bool? FinalError(R18DecisionObservation item)
    {
        if (!item.Reference.IsComparable) return null;
        var expected = item.Reference;
        var compared = false;
        var error = false;
        if (expected.ExpectedRole is not null)
        {
            if (item.FinalPresent == false) return true;
            if (item.FinalRoleStatus != R18ObservationStatus.Observable) return null;
            compared = true;
            error |= !string.Equals(expected.ExpectedRole, item.FinalRole, StringComparison.Ordinal);
        }
        if (expected.ExpectedLevel is not null)
        {
            if (item.FinalPresent == false) return true;
            if (item.FinalLevelStatus != R18ObservationStatus.Observable) return null;
            compared = true;
            error |= expected.ExpectedLevel != item.FinalLevel;
        }
        if (expected.ExpectedParentId is not null)
        {
            if (item.FinalPresent == false) return true;
            if (item.FinalParentStatus != R18ObservationStatus.Observable) return null;
            compared = true;
            error |= !string.Equals(expected.ExpectedParentId, item.FinalParentId, StringComparison.Ordinal);
        }
        if (expected.ExpectedSpan is not null)
        {
            if (item.FinalPresent == false) return true;
            if (item.FinalSpanStatus != R18ObservationStatus.Observable) return null;
            compared = true;
            error |= expected.ExpectedSpan != item.FinalSpan;
        }
        return compared ? error : null;
    }

    private static int[]? ParsePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out result[index])) return null;
        return result;
    }

    private static R18DiagnosticObservation NotApplicable(string sourceId, R18DiagnosticKind diagnostic, string reason) =>
        new() { SourceId = sourceId, Diagnostic = diagnostic, Status = R18DiagnosticStatus.NotApplicable, Reason = reason };

    private static R18DiagnosticObservation Pass(string sourceId, R18DiagnosticKind diagnostic, string reason) =>
        new() { SourceId = sourceId, Diagnostic = diagnostic, Status = R18DiagnosticStatus.Pass, Reason = reason };

    private static R18DiagnosticObservation Failure(string sourceId, R18DiagnosticKind diagnostic, string reason) =>
        new() { SourceId = sourceId, Diagnostic = diagnostic, Status = R18DiagnosticStatus.Fail, Reason = reason };
}
