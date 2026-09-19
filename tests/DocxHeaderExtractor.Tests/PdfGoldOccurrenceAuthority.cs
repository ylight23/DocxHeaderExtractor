using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

internal sealed record PdfGoldOccurrenceAuthorityDocument(
    string DocumentId,
    string SourceSha256,
    string SourceUniverseSha256,
    IReadOnlyList<PdfReviewOccurrence> Reviews,
    DocumentSourceCatalog Catalog,
    IReadOnlyList<SemanticSourceAlias> Aliases);

/// <summary>
/// Loads the committed human-owned occurrence worksheet without using row position as identity.
/// </summary>
internal static class PdfGoldOccurrenceAuthorityLoader
{
    public const string DocumentId = "DOC-0252";
    public const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    public const string SourceUniverseSha256 =
        "5dd617b27d7c1479f81fb41468076b5f6ba826a7d86cc9a04f6dfa0fa624c3ec";
    public const int SourceAliasCount = 1013;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static PdfGoldOccurrenceAuthorityDocument LoadFromFiles(
        string authorityPath,
        string sourceUniversePath) =>
        Load(File.ReadAllText(authorityPath), File.ReadAllText(sourceUniversePath));

    public static PdfGoldOccurrenceAuthorityDocument Load(
        string authorityJson,
        string sourceUniverseJson)
    {
        ArgumentNullException.ThrowIfNull(authorityJson);
        ArgumentNullException.ThrowIfNull(sourceUniverseJson);

        using var authority = JsonDocument.Parse(authorityJson);
        using var sourceUniverse = JsonDocument.Parse(sourceUniverseJson);
        var sourceRoot = sourceUniverse.RootElement;
        var authorityRoot = authority.RootElement;

        Require(sourceRoot.GetProperty("documentId").GetString() == DocumentId,
            "source-universe-document-id-mismatch");
        Require(sourceRoot.GetProperty("sourceSha256").GetString() == SourceSha256,
            "source-universe-source-sha-mismatch");
        Require(CanonicalArtifactHash.OfText(sourceUniverseJson) == SourceUniverseSha256,
            "source-universe-canonical-sha-mismatch");

        var sourceRows = sourceRoot.GetProperty("rows").EnumerateArray().ToArray();
        Require(sourceRows.Length == SourceAliasCount, "source-universe-row-count-mismatch");
        Require(sourceRoot.GetProperty("occurrences").GetInt32() == SourceAliasCount,
            "source-universe-occurrence-count-mismatch");

        var sourceAliases = sourceRows
            .Select(row => row.GetProperty("sourceAlias").GetString()!)
            .ToArray();
        Require(sourceAliases.Distinct(StringComparer.Ordinal).Count() == SourceAliasCount,
            "source-universe-duplicate-source-alias");

        Require(authorityRoot.GetProperty("artifactKind").GetString() ==
                "a99_pdf_gold_review_decisions", "authority-artifact-kind-mismatch");
        Require(authorityRoot.GetProperty("schemaVersion").GetString() ==
                "a99-pdf-gold-review-decisions-v1", "authority-schema-version-mismatch");
        Require(authorityRoot.GetProperty("documentId").GetString() == DocumentId,
            "authority-document-id-mismatch");
        Require(authorityRoot.GetProperty("sourceSha256").GetString() == SourceSha256,
            "authority-source-sha-mismatch");
        Require(authorityRoot.GetProperty("sourceUniverseSha256").GetString() == SourceUniverseSha256,
            "authority-source-universe-sha-mismatch");

        var decisions = authorityRoot.GetProperty("decisions").EnumerateArray().ToArray();
        Require(decisions.Length == SourceAliasCount, "authority-row-count-mismatch");

        var decisionAliases = decisions
            .Select(row => row.GetProperty("sourceAlias").GetString()!)
            .ToArray();
        Require(decisionAliases.Distinct(StringComparer.Ordinal).Count() == SourceAliasCount,
            "authority-duplicate-source-alias");

        var sourceByAlias = sourceRows.ToDictionary(
            row => row.GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);
        var decisionByAlias = decisions.ToDictionary(
            row => row.GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);

        var unknown = decisionByAlias.Keys.Except(sourceByAlias.Keys, StringComparer.Ordinal).ToArray();
        Require(unknown.Length == 0, $"authority-unknown-source-alias:{string.Join(',', unknown)}");
        var missing = sourceByAlias.Keys.Except(decisionByAlias.Keys, StringComparer.Ordinal).ToArray();
        Require(missing.Length == 0, $"authority-missing-source-alias:{string.Join(',', missing)}");

        // The join above is by alias. The committed contract additionally freezes document order,
        // so a reordered authority is rejected without ever using position as identity.
        Require(sourceAliases.SequenceEqual(decisionAliases, StringComparer.Ordinal),
            "authority-source-alias-order-mismatch");

        var reviews = sourceRows.Select(sourceRow =>
        {
            var alias = sourceRow.GetProperty("sourceAlias").GetString()!;
            var decision = decisionByAlias[alias];
            var humanDecision = decision.GetProperty("humanDecision");
            var value = humanDecision.ValueKind == JsonValueKind.Null
                ? null
                : humanDecision.GetString();
            var claims = decision.GetProperty("headingClaims")
                .Deserialize<PdfReviewHeadingClaim[]>(Json) ?? [];

            return new PdfReviewOccurrence(
                alias,
                sourceRow.GetProperty("page").GetInt32(),
                sourceRow.GetProperty("ordinal").GetInt32(),
                sourceRow.GetProperty("verbatimText").GetString()!)
            {
                HumanDecision = value,
                HeadingClaims = claims,
            };
        }).ToArray();

        Require(reviews.Count(row => row.HumanDecision is null) == 0,
            "authority-undecided-occurrence");
        Require(reviews.Count(row => row.HumanDecision == PdfGoldReview.NeedsReview) == 0,
            "authority-needs-review-occurrence");

        var catalog = new DocumentSourceCatalog(sourceRows.Select(row =>
        {
            var sourceId = row.GetProperty("sourceId").GetString()!;
            var ordinal = row.GetProperty("ordinal").GetInt32();
            var text = row.GetProperty("verbatimText").GetString()!;
            return new DocumentSourceUnit(
                sourceId,
                ordinal,
                text,
                new SourceAnchor
                {
                    SourceType = "pdf",
                    ParagraphId = sourceId,
                    ParagraphIndex = ordinal,
                    Page = row.GetProperty("page").GetInt32(),
                },
                new StructuralSpan(0, text.Length));
        }));

        var aliases = sourceRows.Select(row =>
        {
            var sourceId = row.GetProperty("sourceId").GetString()!;
            var ordinal = row.GetProperty("ordinal").GetInt32();
            var text = row.GetProperty("verbatimText").GetString()!;
            return new SemanticSourceAlias(
                row.GetProperty("sourceAlias").GetString()!,
                sourceId,
                ordinal,
                text,
                new StructuralSpan(0, text.Length),
                new SourceAnchor
                {
                    SourceType = "pdf",
                    ParagraphId = sourceId,
                    ParagraphIndex = ordinal,
                    Page = row.GetProperty("page").GetInt32(),
                });
        }).ToArray();

        return new PdfGoldOccurrenceAuthorityDocument(
            DocumentId, SourceSha256, SourceUniverseSha256, reviews, catalog, aliases);
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidDataException(code);
    }
}

internal static class PdfGoldOccurrenceMaterializer
{
    public const int ApprovedSemanticHeadingTotal = 41;

    public static PdfGoldDocument Convert(PdfGoldOccurrenceAuthorityDocument authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!PdfGoldReview.TryToGoldHeadings(
                authority.Reviews, out var headings, out var reviewIssues))
            throw new InvalidDataException(string.Join(';', reviewIssues.Select(issue =>
                $"{issue.Code}:{issue.SourceAlias}")));

        var gold = new PdfGoldDocument(authority.DocumentId, authority.SourceSha256, headings)
        {
            FinalAuthority = "OCCURRENCE_REVIEW_PENDING_RECONCILIATION",
            ProviderCalls = 0,
        };
        var issues = PdfGoldValidator.Validate(gold, authority.Catalog, authority.Aliases);
        if (issues.Count > 0)
            throw new InvalidDataException(string.Join(';', issues.Select(issue =>
                $"{issue.Code}:{issue.SourceAlias}")));
        return gold;
    }

    public static PdfGoldDocument Freeze(PdfGoldOccurrenceAuthorityDocument authority)
    {
        var converted = Convert(authority);
        if (converted.Headings.Count != ApprovedSemanticHeadingTotal)
            throw new InvalidDataException(
                $"occurrence-gold-reconciliation-mismatch:{converted.Headings.Count}:{ApprovedSemanticHeadingTotal}");

        var frozen = converted with
        {
            ArtifactKind = "a99_pdf_occurrence_gold",
            SchemaVersion = "a99-pdf-occurrence-gold-v1",
            SemanticHeadingTotal = ApprovedSemanticHeadingTotal,
            SemanticHeadingTotalAuthority = new PdfGoldAuthorityRecord(
                "USER_APPROVED_SEMANTIC_TOTAL", "2026-09-12")
            {
                Reviewer = "USER",
            },
            OccurrenceAuthority = new PdfGoldAuthorityRecord(
                "USER_APPROVED_OCCURRENCE_REVIEW", "2026-09-20")
            {
                Reviewer = "USER",
                SourceUniverseSha256 = authority.SourceUniverseSha256,
            },
            FinalAuthority = "USER_APPROVED_OCCURRENCE_REVIEW",
            Capabilities = new PdfGoldCapabilities
            {
                SemanticEvaluable = true,
                OccurrenceEvaluable = true,
                HierarchyEvaluable = false,
            },
            ProviderCalls = 0,
        };

        var finalIssues = PdfGoldValidator.Validate(frozen, authority.Catalog, authority.Aliases);
        if (finalIssues.Count > 0)
            throw new InvalidDataException(string.Join(';', finalIssues.Select(issue =>
                $"{issue.Code}:{issue.SourceAlias}")));
        return frozen;
    }
}
