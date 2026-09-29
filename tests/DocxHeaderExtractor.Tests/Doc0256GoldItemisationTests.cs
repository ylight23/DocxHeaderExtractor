using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0256's first occurrence-level Gold: the 34 headings the user approved as the semantic total,
/// itemised and bound in the structured atom coordinates of the PDF itself (the source is unchanged).
/// <para>
/// 24 are the historical headings (strict-gold-occurrence-v1, taken against an earlier export of the
/// same PDF - its text is present in this one; its "Eurostat�OECD" is an en dash here). 10 were kept by
/// the user on 2026-09-24: the two-line document title, and the nine Annex 2 organisation names, each
/// opening its own attendee list - the same criterion approved for DOC-0258. The masthead date and
/// meeting-mode lines and timed agenda rows are not headings.
/// </para>
/// <para>
/// Evidence before the decision (eval/a99-closed-loop/doc0256-real-harness-exploration-v1): on the
/// structured lane, three qwen3.7-flash runs matched 33 of these 34 each time; the one miss (AfDB) sits
/// at a pack boundary. Every binding here goes through the production binder, and roles are not
/// assigned: the approval was membership, not role.
/// </para>
/// </summary>
public sealed class Doc0256GoldItemisationTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf";
    private const string HistoricalOccurrence = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0256.occurrence-gold-v1.json";
    private const string PredecessorGoldSha256 = "2c6fdabf5c914d4f7d06097c59dd9755b1f194aae9c4150c27e4495ad43ee8c1";

    private static readonly (string Text, string Origin)[] ApprovedHeadings =
    [
        ("MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM INTER-AGENCY COORDINATION GROUP MEETING", "KEPT_2026_09_24"),
        ("Welcome and meeting objectives", "HISTORICAL_24"),
        ("Regional updates on the ICP 2021 cycle implementation", "HISTORICAL_24"),
        ("Africa", "HISTORICAL_24"),
        ("Asia and the Pacific", "HISTORICAL_24"),
        ("Commonwealth of Independent States", "HISTORICAL_24"),
        ("Eurostat–OECD PPP Program", "HISTORICAL_24"),
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
        ("Planning for the 2023/4 governance activities and ICP 2021 cycle release at regional and global levels", "HISTORICAL_24"),
        ("Planning for the ICP 2024 cycle", "HISTORICAL_24"),
        ("Annex 1: Meeting Agenda", "HISTORICAL_24"),
        ("DAY 1: TUESDAY, OCTOBER 31, 2023", "HISTORICAL_24"),
        ("DAY 2: WEDNESDAY, NOVEMBER 1, 2023", "HISTORICAL_24"),
        ("DAY 3: THURSDAY, NOVEMBER 2, 2023", "HISTORICAL_24"),
        ("DAY 4: FRIDAY, NOVEMBER 3, 2023", "HISTORICAL_24"),
        ("Annex 2: List of participants", "HISTORICAL_24"),
        ("African Development Bank (AfDB)", "KEPT_2026_09_24"),
        ("Asian Development Bank (ADB)", "KEPT_2026_09_24"),
        ("Interstate Statistical Committee of the Commonwealth of Independent States (CIS-STAT)", "KEPT_2026_09_24"),
        ("Organisation for Economic Co-operation and Development (OECD)", "KEPT_2026_09_24"),
        ("Statistical Office of the European Communities (Eurostat)", "KEPT_2026_09_24"),
        ("United Nations Economic Commission for Latin America and the Caribbean (UN-ECLAC)", "KEPT_2026_09_24"),
        ("United Nations Economic and Social Commission for Western Asia (UN-ESCWA)", "KEPT_2026_09_24"),
        ("International Monetary Fund (IMF)", "KEPT_2026_09_24"),
        ("World Bank", "KEPT_2026_09_24"),
    ];

    [Fact]
    public void The_approved_set_is_34_headings_24_historical_plus_10_kept()
    {
        Assert.Equal(34, ApprovedHeadings.Length);
        Assert.Equal(24, ApprovedHeadings.Count(h => h.Origin == "HISTORICAL_24"));
        Assert.Equal(10, ApprovedHeadings.Count(h => h.Origin == "KEPT_2026_09_24"));
    }

    [Fact]
    public void Materialize_structured_occurrence_gold()
    {
        var path = TestRepository.Path(Pdf);
        var authority = PdfStructuredSourceAuthorityBuilder.Build(path);
        var atoms = authority.Atoms;
        var lastIndex = -1;

        var bound = ApprovedHeadings.Select(heading =>
        {
            var (start, count) = FindRun(atoms, heading.Text, lastIndex + 1);
            lastIndex = start + count - 1;
            var parts = atoms.Skip(start).Take(count)
                .Select(atom => new SemanticSourcePart(atom.Alias, "WHOLE_ALIAS"))
                .ToArray();
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
            Assert.True(binding.IsBound, $"'{heading.Text}' did not bind: {binding.Reason}");
            var projected = string.Join(" ", binding.Parts.Select(part => part.Text));
            return new
            {
                approvedWording = heading.Text,
                sourceParts = parts.Select(part => new
                {
                    sourceAlias = part.SourceAlias,
                    selectionMode = part.SelectionMode,
                    verbatimText = (string?)null,
                    occurrence = (int?)null,
                    leftExactContext = (string?)null,
                    rightExactContext = (string?)null,
                }).ToArray(),
                semanticRole = (string?)null,
                identity = binding.Identity,
                projectedText = projected,
                parserTextDiffers = projected != heading.Text,
                origin = heading.Origin,
            };
        }).ToArray();

        // The parser's only text defect among the 34: "IMF" reads back as "IM F". The approved
        // wording is the PDF's own; the binding is still the whole atom.
        Assert.Equal(["International Monetary Fund (IMF)"],
            bound.Where(b => b.parserTextDiffers).Select(b => b.approvedWording));

        FreezeArtifact.AssertJson(
            "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence",
            "DOC-0256.structured-source-parts.occurrence-gold.v1.json",
            new
            {
                artifactKind = "a99_pdf_structured_occurrence_gold",
                schemaVersion = "a99-pdf-structured-occurrence-gold-v1",
                documentId = "DOC-0256",
                sourceSha256 = CanonicalArtifactHash.OfBytes(path),
                coordinateProfile = "STRUCTURED_SOURCE_PARTS",
                coordinateSystem = "STRUCTURED_SOURCE_PART_TUPLE",
                sourceAliasUniverseSha256 = authority.SourceAliasUniverseHash,
                semanticHeadingTotal = 34,
                migration = new
                {
                    kind = "HUMAN_APPROVED_GOLD_CORRECTION",
                    correctedAt = "2026-09-24",
                    predecessorGoldSha256 = PredecessorGoldSha256,
                    predecessorCapability = "SEMANTIC_COUNT_ONLY",
                    semanticMembershipChanged = false,
                    totalChanged = false,
                    itemised = 34,
                    historicalOccurrence = HistoricalOccurrence,
                    historicalOccurrenceSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HistoricalOccurrence)),
                    historicalOccurrenceSourceNote = "Taken against an earlier export of the same PDF (06aec4f8...); all 24 headings' text is present in this source.",
                    kept = ApprovedHeadings.Where(h => h.Origin == "KEPT_2026_09_24").Select(h => h.Text).ToArray(),
                    notHeadings = "Masthead date and meeting-mode lines; timed agenda rows.",
                    evidence = "eval/a99-closed-loop/doc0256-real-harness-exploration-v1",
                    rolesAssigned = false,
                    reviewer = "USER",
                },
                boundOccurrences = bound,
            });
    }

    private static (int Start, int Count) FindRun(IReadOnlyList<SemanticSourceAtom> atoms, string text, int from)
    {
        var target = Normalize(text);
        for (var i = from; i < atoms.Count; i++)
        {
            var joined = "";
            for (var j = i; j < Math.Min(atoms.Count, i + 3); j++)
            {
                joined += atoms[j].Text;
                if (Normalize(joined) == target) return (i, j - i + 1);
                if (!target.StartsWith(Normalize(joined), StringComparison.Ordinal)) break;
            }
        }
        throw new InvalidOperationException($"no atom run for '{text}' after atom {from}");
    }

    // Letters and digits only: the parser drops some punctuation and splits "IMF"; order still decides.
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
