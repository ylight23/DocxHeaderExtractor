using DocxHeaderExtractor.DocumentProcessing.Source;
using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The pilot's three itemisations, decided by the user on 2026-09-24 and written where Gold is
/// written - the one authored file per document - by a one-shot step that refuses to run twice.
/// <para>
/// The rule applied, in the user's words and in the order they gave it: what the model returns as a
/// heading (document label or structural) is accepted; what is certainly a heading but the model
/// missed is kept rather than dropped; masthead date and meeting-mode lines stay out, as they did
/// for DOC-0256 and DOC-0258. For the two DOCX minutes that raised the approved total from 18 to 20;
/// SRC-055 stays at 4 (its four typographically distinct headings, no model call needed).
/// </para>
/// </summary>
public sealed class PilotGoldItemisationTests
{
    private sealed record Heading(string Text, bool BoldPrefixOnly, string Evidence);

    private static readonly Heading[] Doc0255 =
    [
        new("MEETING MINUTES", false, "model 3/3 DOCUMENT_LABEL"),
        new("Governing Committee (GC) of the Facilitation of Resources to Invest in Strengthening Ukraine Financial Intermediary Fund (F.O.R.T.I.S. Ukraine FIF)", false, "model 3/3 DOCUMENT_LABEL"),
        new("Opening:", false, "model 3/3 STRUCTURAL"),
        new("Present:", false, "model 3/3"),
        new("Item 1: Document Approval.", true, "model 3/3 STRUCTURAL"),
        new("Item 2: Operating Context.", true, "model 3/3 STRUCTURAL"),
        new("Item 3: Strategic Directions.", true, "model 3/3 STRUCTURAL"),
        new("Item 4: Work Plan, Budget, and Fees.", true, "model 3/3 STRUCTURAL"),
        new("Item 5: Communications Strategy.", true, "model 3/3 STRUCTURAL"),
        new("Item 6: Appointment of the Chair.", true, "model 3/3 STRUCTURAL"),
        new("Attachment:Agreed Agenda", false, "model 3/3 DOCUMENT_LABEL"),
        new("AGENDA for FIRST MEETING", false, "model 3/3 DOCUMENT_LABEL"),
        new("Governing Committee (GC) of the Facilitation of Resources to Invest in Strengthening Ukraine Financial Intermediary Fund (F.O.R.T.I.S. Ukraine FIF)", false, "model 3/3 DOCUMENT_LABEL"),
        new("Agenda Items", false, "model 3/3 STRUCTURAL"),
        new("1. Document Approval.", true, "kept: bold agenda-item label, model 0/3"),
        new("2. Operating Context.", true, "kept: bold agenda-item label, model 0/3"),
        new("3. Strategic Directions.", true, "kept: bold agenda-item label, model 0/3"),
        new("4. Work Plan, Budget, and Fees.", true, "kept: bold agenda-item label, model 0/3"),
        new("5. Communications Strategy.", true, "kept: bold agenda-item label, model 0/3"),
        new("6. Appointment of the Chair.", true, "kept: bold agenda-item label, model 0/3"),
    ];

    private static readonly Heading[] Doc0259 =
    [
        new("MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM", false, "model 3/3 DOCUMENT_LABEL"),
        new("TECHNICAL ADVISORY GROUP", false, "model 3/3 DOCUMENT_LABEL"),
        new("Welcome and meeting objectives", false, "model 3/3 STRUCTURAL"),
        new("Session I: ICP 2021 cycle results", false, "model 3/3 STRUCTURAL"),
        new("ICP 2021 results and forecasts", false, "model 3/3 STRUCTURAL"),
        new("Revised ICP 2017 results", false, "model 3/3 STRUCTURAL"),
        new("Revised ICP 2017 – ICP 2021 PPP time series", false, "model 3/3 STRUCTURAL"),
        new("Revised ICP 2017 and ICP 2021 non-benchmark country estimates", false, "model 3/3 STRUCTURAL"),
        new("Session II: Closing", false, "model 3/3 STRUCTURAL"),
        new("Next steps, any other business, and closing remarks", false, "model 3/3 STRUCTURAL"),
        new("International Comparison Program (ICP) TECHNICAL ADVISORY GROUP (TAG)", false, "model 3/3 DOCUMENT_LABEL"),
        new("Agenda", false, "model 3/3"),
        new("Tuesday, April 30, 2024", false, "model 3/3 (2 STRUCTURAL, 1 DOCUMENT_LABEL)"),
        new("SESSION I: ICP 2021 cycle results", false, "kept: bold agenda session row, model 0/3"),
        new("SESSION II: Closing", false, "kept: bold agenda session row, model 0/3"),
        new("Annex 2: List of Participants", false, "model 3/3"),
        new("ICP Technical Advisory Group members", false, "model 3/3"),
        new("ICP experts, guest speakers, and observers", false, "model 3/3"),
        new("ICP Inter-Agency Coordination Group (IACG)", false, "model 3/3"),
        new("World Bank", false, "model 3/3"),
    ];

    private static readonly string[] Src055 =
    [
        "Independent Accountant’s Review Report",
        "Management’s Assertion Regarding Commitments and Disbursements for IDA Projects",
        "IDA Projects Commitment and Disbursement Schedule",
        "Notes on the IDA Projects Commitment and Disbursement Schedule",
    ];

    [Fact]
    public void The_decided_counts_are_twenty_twenty_and_four()
    {
        Assert.Equal(20, Doc0255.Length);
        Assert.Equal(20, Doc0259.Length);
        Assert.Equal(4, Src055.Length);
    }

    [Theory]
    [InlineData("DOC-0255")]
    [InlineData("DOC-0259")]
    public void Every_decided_heading_is_a_whole_paragraph_or_its_bold_lead_in(string id)
    {
        var (headings, docx) = DocxSpec(id);
        var claims = BindDocx(headings, TestRepository.Path(docx));
        Assert.Equal(headings.Length, claims.Count);
    }

    [Fact]
    public void Apply_the_pilot_itemisations_to_authored_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_PILOT_ITEMISE") != "1") return;

        foreach (var id in new[] { "DOC-0255", "DOC-0259" })
        {
            var (headings, docx) = DocxSpec(id);
            var stem = Path.GetFileNameWithoutExtension(docx);
            Apply(id, total: 20, "SOURCE_ALIAS", BindDocx(headings, TestRepository.Path(docx)), gold =>
            {
                gold["source"]!["sourcePath"] = docx;
                gold["source"]!["sourceSha256"] = CanonicalArtifactHash.OfBytes(TestRepository.Path(docx));
                gold["approval"]!["approvedAt"] = "2026-09-24";
            },
            [
                ($"gold-correction:{id}:itemised-total-18-to-20:2026-09-24", "GOLD_CORRECTION_PREDECESSOR"),
                ($"eval/a99-closed-loop/generated-docx-v2/{id}.conversion-manifest.v1.json", "SOURCE_REGENERATION"),
                ($"eval/a99-closed-loop/pilot-real-harness-v1/{id}/docx-r1.v1.json", "PILOT_EVIDENCE"),
                ($"eval/a99-closed-loop/pilot-real-harness-v1/{id}/docx-r2.v1.json", "PILOT_EVIDENCE"),
                ($"eval/a99-closed-loop/pilot-real-harness-v1/{id}/docx-r3.v1.json", "PILOT_EVIDENCE"),
            ]);
        }

        Apply("SRC-055", total: 4, "STRUCTURED_SOURCE_PARTS", BindStructured(Src055,
                TestRepository.Path("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/055_IDA_External_Review_FY25.pdf")),
            _ => { },
            [("gold-correction:SRC-055:itemised-by-typography:2026-09-24", "GOLD_CORRECTION_PREDECESSOR")]);
    }

    private static (Heading[] Headings, string Docx) DocxSpec(string id) => id switch
    {
        "DOC-0255" => (Doc0255, PilotSourceRegenerationTests.DocxPath("075_FORTIS_GC_Minutes_Nov21_2024")),
        "DOC-0259" => (Doc0259, PilotSourceRegenerationTests.DocxPath("079_ICP_TAG_Minutes_Apr_2024")),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static List<JsonObject> BindDocx(Heading[] headings, string docx)
    {
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(docx);
        var leadIns = BoldLeadIns(docx);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(
                DocumentSourceCatalogBuilder.FromSourceDocument(new OpenXmlDocumentSource().Read(docx)))
            .ToDictionary(alias => alias.SourceId, StringComparer.Ordinal);

        var claims = new List<JsonObject>();
        var from = 0;
        foreach (var heading in headings)
        {
            var index = -1;
            for (var i = from; i < paragraphs.Count && index < 0; i++)
            {
                var match = heading.BoldPrefixOnly
                    ? leadIns[i] == heading.Text && paragraphs[i].Text.StartsWith(heading.Text, StringComparison.Ordinal)
                    : paragraphs[i].Text == heading.Text;
                if (match) index = i;
            }
            Assert.True(index >= 0, $"'{heading.Text}' not found after paragraph {from}");
            from = index + 1;
            var alias = aliases[paragraphs[index].StableId];
            var at = alias.Text.IndexOf(heading.Text, StringComparison.Ordinal);
            Assert.True(at >= 0);
            claims.Add(new JsonObject
            {
                ["headingOrdinal"] = claims.Count,
                ["sourceAlias"] = alias.Alias,
                ["sourceId"] = paragraphs[index].StableId,
                ["exactText"] = heading.Text,
                ["selectionMode"] = heading.BoldPrefixOnly ? CanonicalSemanticSelectionMode.VerbatimText : CanonicalSemanticSelectionMode.WholeAlias,
                ["verbatimText"] = heading.BoldPrefixOnly ? heading.Text : null,
                ["semanticRole"] = null,
                ["utf16Span"] = new JsonObject { ["start"] = at, ["end"] = at + heading.Text.Length },
                ["evidence"] = heading.Evidence,
            });
        }
        return claims;
    }

    private static List<JsonObject> BindStructured(string[] texts, string pdf)
    {
        var atoms = PdfSourceAdapter.Build(pdf).Atoms;
        var claims = new List<JsonObject>();
        var from = 0;
        foreach (var text in texts)
        {
            var (start, count) = FindRun(atoms, text, from);
            from = start + count;
            var parts = atoms.Skip(start).Take(count).Select(a => new SemanticSourcePart(a.Alias, "WHOLE_ALIAS")).ToArray();
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
            Assert.True(binding.IsBound, binding.Reason);
            claims.Add(new JsonObject
            {
                ["approvedWording"] = text,
                ["sourceParts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                {
                    ["sourceAlias"] = p.SourceAlias,
                    ["selectionMode"] = p.SelectionMode,
                    ["verbatimText"] = null,
                    ["occurrence"] = null,
                    ["leftExactContext"] = null,
                    ["rightExactContext"] = null,
                }).ToArray()),
                ["semanticRole"] = null,
                ["identity"] = binding.Identity,
                ["projectedText"] = string.Join(" ", binding.Parts.Select(p => p.Text)),
                ["evidence"] = "typography: one of the four bold lines set larger than or apart from the body; user-approved",
            });
        }
        return claims;
    }

    private static void Apply(string id, int total, string coordinateSystem, List<JsonObject> claims,
        Action<JsonNode> editSource, (string Path, string Role)[] provenance)
    {
        var path = TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json");
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot: refuses a document that is already itemised
        Assert.Equal(total, claims.Count);

        editSource(gold);
        gold["semanticHeadingTotal"] = total;
        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = coordinateSystem,
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var list = gold["provenance"]!.AsArray();
        foreach (var (item, role) in provenance)
        {
            var sha = role == "GOLD_CORRECTION_PREDECESSOR"
                ? CanonicalArtifactHash.OfText(before)
                : CanonicalArtifactHash.OfTextFile(TestRepository.Path(item));
            list.Add(new JsonObject { ["path"] = item, ["sha256"] = sha, ["role"] = role });
        }
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    private static IReadOnlyList<string> BoldLeadIns(string docx)
    {
        using var document = WordprocessingDocument.Open(docx, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        return ParagraphWalker.Enumerate(body, new ExtractionOptions()).Select(p =>
        {
            var runs = p.Element.Elements<Run>().ToArray();
            var first = runs.FirstOrDefault();
            return first?.RunProperties?.Bold is not null && runs.Length > 1 ? first.InnerText : string.Empty;
        }).ToArray();
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
        throw new InvalidOperationException($"no atom run for '{text}'");
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
