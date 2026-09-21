using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One claim a reviewer makes about a heading inside a source occurrence.
/// <para>
/// Separate from the occurrence because a PDF occurrence is a parser artefact, not a semantic unit.
/// Line grouping fuses neighbouring lines that share geometry and font, so one occurrence can carry
/// more than one heading - in this document S0043, S0460 and S0573 each hold a session heading and
/// a numbered sub-heading in the same two-line block. A shape allowing one answer per occurrence
/// could not say which heading, with which boundary, in which role, and the Gold would be unable to
/// express exactly the partial-span cases I8 exists to address.
/// </para>
/// <para>
/// Every <em>semantic</em> field here maps one-to-one onto <see cref="PdfGoldHeading"/>, so a
/// reviewed pack converts without interpretation. <see cref="ReviewNote"/> is the exception and is
/// not one of them: it records why a reviewer decided as they did, which belongs to the review
/// lineage rather than to what the document is held to contain. Carrying it into semantic Gold
/// would grow the thing every evaluation compares against in order to keep a note.
/// </para>
/// </summary>
public sealed record PdfReviewHeadingClaim
{
    /// <summary>WHOLE_ALIAS when the heading is the entire occurrence, VERBATIM_TEXT when it is part.</summary>
    [JsonPropertyName("selectionMode")] public string? SelectionMode { get; init; }

    /// <summary>The exact heading text, for VERBATIM_TEXT. Must be a substring of the source occurrence.</summary>
    [JsonPropertyName("verbatimText")] public string? VerbatimText { get; init; }

    /// <summary>1-based, only when the text appears more than once inside this occurrence.</summary>
    [JsonPropertyName("occurrence")] public int? Occurrence { get; init; }

    [JsonPropertyName("leftExactContext")] public string? LeftExactContext { get; init; }
    [JsonPropertyName("rightExactContext")] public string? RightExactContext { get; init; }
    [JsonPropertyName("semanticRole")] public string? SemanticRole { get; init; }

    /// <summary>Only where the relation was adjudicated. Null means not adjudicated, not "root".</summary>
    [JsonPropertyName("parentSourceAlias")] public string? ParentSourceAlias { get; init; }

    /// <summary>Review metadata, kept with the review. Never part of the semantic Gold heading.</summary>
    [JsonPropertyName("reviewNote")] public string? ReviewNote { get; init; }
}

/// <summary>
/// One source occurrence as a reviewer sees it: the membership decision, and zero or more headings
/// found inside it.
/// </summary>
public sealed record PdfReviewOccurrence(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("sourceText")] string SourceText)
{
    /// <summary>HEADING, NOT_HEADING or NEEDS_REVIEW. Null means undecided, never "no".</summary>
    [JsonPropertyName("humanDecision")] public string? HumanDecision { get; init; }

    [JsonPropertyName("headingClaims")]
    public IReadOnlyList<PdfReviewHeadingClaim> HeadingClaims { get; init; } = [];
}

/// <summary>
/// Turns a reviewed pack into Gold, and refuses anything it would have to interpret.
/// <para>
/// The conversion is mechanical on purpose. Every judgement belongs to the reviewer; if a row does
/// not say enough to produce a Gold heading, that is reported back rather than filled in with a
/// default, because a default here is a model deciding what a person meant.
/// </para>
/// </summary>
public static class PdfGoldReview
{
    public const string Heading = "HEADING";
    public const string NotHeading = "NOT_HEADING";
    public const string NeedsReview = "NEEDS_REVIEW";

    public const string DecisionMissing = "OCCURRENCE_NOT_DECIDED";
    public const string DecisionUnknown = "UNKNOWN_HUMAN_DECISION";
    public const string NotHeadingCarriesClaims = "NOT_HEADING_CARRIES_HEADING_CLAIMS";
    public const string HeadingWithoutClaim = "HEADING_WITHOUT_A_HEADING_CLAIM";
    public const string StillNeedsReview = "OCCURRENCE_STILL_NEEDS_REVIEW";
    public const string ClaimSelectionMissing = "CLAIM_WITHOUT_SELECTION_MODE";
    public const string ClaimRoleMissing = "CLAIM_WITHOUT_SEMANTIC_ROLE";

    /// <summary>
    /// The structural rules, checked before any binding. These are about what the reviewer said,
    /// not about whether it matches the document - <see cref="PdfGoldValidator"/> asks that next.
    /// </summary>
    public static IReadOnlyList<PdfGoldIssue> Check(IReadOnlyList<PdfReviewOccurrence> reviewed)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        var issues = new List<PdfGoldIssue>();

        foreach (var row in reviewed)
        {
            switch (row.HumanDecision)
            {
                case null:
                    issues.Add(new(DecisionMissing, row.SourceAlias, "No decision was recorded."));
                    continue;
                case NotHeading when row.HeadingClaims.Count > 0:
                    issues.Add(new(NotHeadingCarriesClaims, row.SourceAlias,
                        $"decided NOT_HEADING but carries {row.HeadingClaims.Count} heading claims."));
                    continue;
                case NotHeading:
                    continue;
                case Heading when row.HeadingClaims.Count == 0:
                    issues.Add(new(HeadingWithoutClaim, row.SourceAlias,
                        "decided HEADING but names no heading inside the occurrence."));
                    continue;
                case NeedsReview:
                    issues.Add(new(StillNeedsReview, row.SourceAlias,
                        "left as NEEDS_REVIEW; a second pass has to settle it before freezing."));
                    continue;
                case Heading:
                    break;
                default:
                    issues.Add(new(DecisionUnknown, row.SourceAlias, $"'{row.HumanDecision}' is not a decision."));
                    continue;
            }

            foreach (var claim in row.HeadingClaims)
            {
                if (string.IsNullOrWhiteSpace(claim.SelectionMode))
                    issues.Add(new(ClaimSelectionMissing, row.SourceAlias,
                        "a claim does not say whether it is the whole occurrence or part of it."));
                if (string.IsNullOrWhiteSpace(claim.SemanticRole))
                    issues.Add(new(ClaimRoleMissing, row.SourceAlias, "a claim carries no semantic role."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Converts a reviewed pack, or reports why it cannot be converted. Checking is part of the
    /// conversion rather than a step a caller has to remember: a caller that forgot would have
    /// produced Gold carrying empty-string defaults for selection mode and role, and nothing
    /// downstream looked for those - a malformed Gold would have travelled a long way before
    /// anything noticed.
    /// </summary>
    public static bool TryToGoldHeadings(
        IReadOnlyList<PdfReviewOccurrence> reviewed,
        out IReadOnlyList<PdfGoldHeading> headings,
        out IReadOnlyList<PdfGoldIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        issues = Check(reviewed);
        if (issues.Count > 0)
        {
            headings = [];
            return false;
        }

        // Every value below is non-null because Check passed; the shape is what makes that true,
        // not an assumption. Each claim carries the semantic fields of exactly one Gold heading -
        // ReviewNote is deliberately not among them, see the remark on the conversion contract.
        headings = reviewed
            .Where(row => row.HumanDecision == Heading)
            .SelectMany(row => row.HeadingClaims.Select(claim =>
                new PdfGoldHeading(row.SourceAlias, claim.SelectionMode!, claim.SemanticRole!)
                {
                    VerbatimText = claim.VerbatimText,
                    Occurrence = claim.Occurrence,
                    LeftExactContext = claim.LeftExactContext,
                    RightExactContext = claim.RightExactContext,
                    ParentSourceAlias = claim.ParentSourceAlias,
                }))
            .ToArray();
        return true;
    }

    /// <summary>
    /// The same conversion for a pack already known to be complete. Refuses rather than degrades:
    /// a half-answered review is not Gold with gaps, it is a review that is not finished.
    /// </summary>
    public static IReadOnlyList<PdfGoldHeading> ToGoldHeadings(IReadOnlyList<PdfReviewOccurrence> reviewed)
    {
        if (TryToGoldHeadings(reviewed, out var headings, out var issues)) return headings;

        throw new InvalidOperationException(
            "This review cannot be converted to Gold yet:" + Environment.NewLine +
            string.Join(Environment.NewLine, issues.Select(issue => $"  {issue.Code} {issue.SourceAlias}: {issue.Detail}")));
    }
}
