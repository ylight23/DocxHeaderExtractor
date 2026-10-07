using DocxHeaderExtractor.DocumentProcessing.Source;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0258's first occurrence-level Gold: the 37 headings the user approved as the semantic total,
/// now itemised, bound to the regenerated source (see <see cref="Doc0258SourceRegenerationTests"/>).
/// <para>
/// 24 are the historical headings (strict-gold-occurrence-v1, text re-read from the PDF - the old
/// artifact carried "Eurostat�OECD" where the PDF has an en dash). 13 were added in
/// DOC0258_GOLD37_DELTA_AUDIT_V1 and kept by the user one by one: DAY 1-4, which open each day of the
/// Annex 1 agenda, and the nine organisation names, each of which opens its own attendee list in
/// Annex 2 - the same function the six region names serve in the body, which are in the historical
/// set without being bold. Timed agenda rows are schedule entries and are not headings.
/// </para>
/// <para>
/// Every heading is a whole paragraph of the regenerated source, so every binding is WHOLE_ALIAS and
/// every span starts at 0. Roles are not assigned here: the approval was membership, not role.
/// </para>
/// </summary>
public sealed class Doc0258GoldCorrectionSourceReplacementTests
{
    private const string HistoricalOccurrence = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0258.occurrence-gold-v1.json";

    private static readonly (string Text, string Origin)[] ApprovedHeadings =
    [
        ("MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM INTER-AGENCY COORDINATION GROUP MEETING", "HISTORICAL_24"),
        ("Welcome and meeting objectives", "HISTORICAL_24"),
        ("Regional updates on the ICP 2021 cycle implementation", "HISTORICAL_24"),
        ("Africa", "HISTORICAL_24"),
        ("Asia and the Pacific", "HISTORICAL_24"),
        ("Eurostat–OECD PPP Program", "HISTORICAL_24"),
        ("Commonwealth of Independent States", "HISTORICAL_24"),
        ("Latin America and the Caribbean", "HISTORICAL_24"),
        ("Western Asia", "HISTORICAL_24"),
        ("Global updates on the ICP 2021 cycle implementation", "HISTORICAL_24"),
        ("Data review: Household consumption price and importance data", "HISTORICAL_24"),
        ("Data review: Housing prices and volumes", "HISTORICAL_24"),
        ("Data review: Private Education", "HISTORICAL_24"),
        ("Data review: Government compensation and productivity adjustment", "HISTORICAL_24"),
        ("Data review: Machinery and equipment and construction", "HISTORICAL_24"),
        ("Data review: 2017-2021 National Accounts expenditures", "HISTORICAL_24"),
        ("Data review: 2017-2021 Population and market exchange rates", "HISTORICAL_24"),
        ("Results review: draft revised 2017 and 2021 results", "HISTORICAL_24"),
        ("Planning for the ICP 2024 cycle", "HISTORICAL_24"),
        ("Planning for the 2023 governance activities and ICP 2021 cycle release", "HISTORICAL_24"),
        ("Planning for an approach to produce annual ICP results and forecasts", "HISTORICAL_24"),
        ("Any other business", "HISTORICAL_24"),
        ("Annex 1: Meeting Agenda", "HISTORICAL_24"),
        ("DAY 1: MONDAY, MAY 15, 2023", "DELTA_AUDIT_KEEP"),
        ("DAY 2: TUESDAY, MAY 16, 2023", "DELTA_AUDIT_KEEP"),
        ("DAY 3: WEDNESDAY, MAY 17, 2023", "DELTA_AUDIT_KEEP"),
        ("DAY 4: THURSDAY, MAY 18, 2023", "DELTA_AUDIT_KEEP"),
        ("Annex 2: List of participants", "HISTORICAL_24"),
        ("African Development Bank (AfDB)", "DELTA_AUDIT_KEEP"),
        ("Asian Development Bank (ADB)", "DELTA_AUDIT_KEEP"),
        ("Interstate Statistical Committee of the Commonwealth of Independent States (CIS-STAT)", "DELTA_AUDIT_KEEP"),
        ("Organisation for Economic Co-operation and Development (OECD)", "DELTA_AUDIT_KEEP"),
        ("Statistical Office of the European Communities (Eurostat)", "DELTA_AUDIT_KEEP"),
        ("United Nations Economic Commission for Latin America and the Caribbean (UN-ECLAC)", "DELTA_AUDIT_KEEP"),
        ("United Nations Economic and Social Commission for Western Asia (UN-ESCWA)", "DELTA_AUDIT_KEEP"),
        ("International Monetary Fund (IMF)", "DELTA_AUDIT_KEEP"),
        ("World Bank", "DELTA_AUDIT_KEEP"),
    ];

    [Fact]
    public void The_approved_set_is_37_headings_24_historical_plus_13_kept()
    {
        Assert.Equal(37, ApprovedHeadings.Length);
        Assert.Equal(24, ApprovedHeadings.Count(h => h.Origin == "HISTORICAL_24"));
        Assert.Equal(13, ApprovedHeadings.Count(h => h.Origin == "DELTA_AUDIT_KEEP"));
        Assert.Equal(37, ApprovedHeadings.Select(h => h.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_approved_heading_is_one_whole_paragraph_in_document_order()
    {
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(
            TestRepository.Path(Doc0258SourceRegenerationTests.DocxPath));
        var previousIndex = -1;
        foreach (var (text, _) in ApprovedHeadings)
        {
            var matches = paragraphs.Select((p, i) => (p, i)).Where(x => x.p.Text == text).ToArray();
            var match = Assert.Single(matches);
            Assert.True(match.i > previousIndex, $"'{text}' is out of document order");
            previousIndex = match.i;
        }
    }

    [Fact]
    public void Materialize_occurrence_gold()
    {
        var path = TestRepository.Path(Doc0258SourceRegenerationTests.DocxPath);
        var sourceSha = CanonicalArtifactHash.OfBytes(path);
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(path);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(
                DocumentSourceCatalogBuilder.FromSourceDocument(new OpenXmlDocumentSource().Read(path)))
            .ToDictionary(alias => alias.SourceId, StringComparer.Ordinal);

        var bound = ApprovedHeadings.Select((heading, ordinal) =>
        {
            var paragraph = paragraphs.Single(p => p.Text == heading.Text);
            var alias = aliases[paragraph.StableId];
            Assert.Equal(heading.Text, alias.Text);
            return new
            {
                headingOrdinal = ordinal,
                sourceAlias = alias.Alias,
                sourceId = paragraph.StableId,
                exactText = heading.Text,
                selectionMode = "WHOLE_ALIAS",
                semanticRole = (string?)null,
                utf16Span = new { start = 0, end = heading.Text.Length },
                origin = heading.Origin,
            };
        }).ToArray();

        FreezeArtifact.AssertJson(
            "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence",
            "DOC-0258.occurrence-gold.v1.json",
            new
            {
                artifactKind = "a99_pdf_occurrence_gold",
                schemaVersion = "a99-pdf-occurrence-gold-v1",
                documentId = "DOC-0258",
                sourcePath = Doc0258SourceRegenerationTests.DocxPath,
                sourceSha256 = sourceSha,
                semanticHeadingTotal = 37,
                semanticHeadingTotalAuthority = new
                {
                    authority = "USER_APPROVED_SEMANTIC_TOTAL",
                    originallyApprovedAt = "2026-09-12",
                    itemisedAndReconfirmedAt = "2026-09-24",
                    reviewer = "USER",
                },
                occurrenceAuthority = new
                {
                    authority = "HISTORICAL_24_PLUS_USER_KEPT_13",
                    historicalOccurrence = HistoricalOccurrence,
                    historicalOccurrenceSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HistoricalOccurrence)),
                    deltaAudit = "DOC0258_GOLD37_DELTA_AUDIT_V1",
                    keptInDeltaAudit = ApprovedHeadings.Where(h => h.Origin == "DELTA_AUDIT_KEEP").Select(h => h.Text).ToArray(),
                    notHeadings = "Timed agenda rows (e.g. '10:45 – 12:00 ICP PPP calculation tools'), agenda sub-bullets, and the masthead date and meeting-mode lines.",
                    rolesAssigned = false,
                    decidedAt = "2026-09-24",
                    reviewer = "USER",
                },
                sourceReplacement = new
                {
                    previousSourcePath = Doc0258SourceRegenerationTests.SupersededDocxPath,
                    previousSourceSha256 = Doc0258SourceRegenerationTests.SupersededDocxSha256,
                    manifest = "eval/a99-closed-loop/generated-docx-v2/DOC-0258.conversion-manifest.v1.json",
                },
                finalAuthority = "USER",
                capabilities = new { semanticEvaluable = true, occurrenceEvaluable = true, hierarchyEvaluable = false },
                providerCalls = 0,
                modelCalls = 0,
                headings = bound.Select(b => new
                {
                    b.sourceAlias,
                    b.selectionMode,
                    semanticRole = (string?)null,
                    verbatimText = (string?)null,
                    occurrence = (string?)null,
                    leftExactContext = (string?)null,
                    rightExactContext = (string?)null,
                    parentSourceAlias = (string?)null,
                }).ToArray(),
                boundOccurrences = bound,
            });
    }
}
