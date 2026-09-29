using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0205's source, re-converted from its original Word 97 file.
/// <para>
/// The earlier conversion (heading_corpus_95_word, b145e31a...) put the whole decree into one
/// paragraph, so its Gold could only name that one paragraph under aliases invented for the purpose
/// (S0001..S0072). LibreOffice reads the .doc's own paragraphs: 66 of the 72 approved headings are a
/// paragraph each, and the other six - the title and the five chapters - are two consecutive
/// paragraphs in the original ("Chương I" / "QUY ĐỊNH CHUNG"), so each is bound to both.
/// </para>
/// <para>
/// The heading set is unchanged. Only the source and the coordinates move, and the move is written
/// where Gold is written - <c>eval/a99-closed-loop/gold/DOC-0205.gold.json</c> - by a one-shot step
/// that refuses to run twice.
/// </para>
/// </summary>
public sealed class Doc0205SourceRegenerationTests
{
    private const string OriginalDoc = "todo10_8/heading_corpus_100/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.doc";
    private const string DocxPath = "todo10_8/generated-docx-v2/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SupersededDocxPath = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SupersededDocxSha256 = "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878";
    private const string AuthoredGold = "eval/a99-closed-loop/gold/DOC-0205.gold.json";
    private const string Manifest = "eval/a99-closed-loop/generated-docx-v2/DOC-0205.conversion-manifest.v1.json";

    [Fact]
    public void Freeze_the_conversion_manifest()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/generated-docx-v2", "DOC-0205.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = "DOC-0205",
            modelCalls = 0,
            providerCalls = 0,
            originalDocument = new { path = OriginalDoc, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(OriginalDoc)), format = "Word 97-2003 (.doc)" },
            regeneratedDocx = new
            {
                path = DocxPath,
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(DocxPath)),
                paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(DocxPath)).Count,
            },
            supersedes = new { path = SupersededDocxPath, sha256 = SupersededDocxSha256, defect = "the whole decree in one paragraph" },
            converter = new
            {
                tool = "LibreOffice (buildid cd7284b4cbbfeb507e630c1aac019f4157393acb)",
                command = "soffice --headless --norestore --convert-to docx <original .doc>",
                carries = "the .doc's own paragraphs, runs and styles as LibreOffice reads them; nothing added",
                bytesAreNotReproducible = "the package embeds conversion timestamps; the committed file is the authority and its sha256 is pinned here",
            },
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }

    [Fact]
    public void Every_approved_heading_is_one_or_two_whole_paragraphs_in_document_order()
    {
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(DocxPath));
        var matches = Locate(paragraphs, ApprovedTexts());
        Assert.Equal(72, matches.Count);
        Assert.Equal(66, matches.Count(m => m.Count == 1));
        Assert.Equal(6, matches.Count(m => m.Count == 2));
    }

    [Fact]
    public void Rebind_authored_gold_to_the_regenerated_source()
    {
        if (Environment.GetEnvironmentVariable("A99_DOC0205_REBIND") != "1") return;

        var goldPath = TestRepository.Path(AuthoredGold);
        var goldText = File.ReadAllText(goldPath);
        var gold = JsonNode.Parse(goldText)!;
        Assert.Equal(SupersededDocxSha256, gold["source"]!["sourceSha256"]!.GetValue<string>());

        var docx = TestRepository.Path(DocxPath);
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(docx);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(
                DocumentSourceCatalogBuilder.FromSourceDocument(new OpenXmlDocumentSource().Read(docx)))
            .ToDictionary(alias => alias.SourceId, StringComparer.Ordinal);

        var oldClaims = gold["occurrence"]!["claims"]!.AsArray();
        var matches = Locate(paragraphs, oldClaims.Select(c => c!["exactText"]!.GetValue<string>()).ToArray());
        var claims = new JsonArray();
        for (var ordinal = 0; ordinal < oldClaims.Count; ordinal++)
        {
            var old = oldClaims[ordinal]!;
            var text = old["exactText"]!.GetValue<string>();
            var (start, count) = matches[ordinal];
            var parts = new JsonArray();
            for (var i = start; i < start + count; i++)
            {
                var alias = aliases[paragraphs[i].StableId];
                var partText = paragraphs[i].Text.Trim();
                var at = alias.Text.IndexOf(partText, StringComparison.Ordinal);
                Assert.True(at >= 0, $"'{partText}' is not in alias {alias.Alias}");
                parts.Add(new JsonObject
                {
                    ["sourceAlias"] = alias.Alias,
                    ["sourceId"] = paragraphs[i].StableId,
                    ["utf16Span"] = new JsonObject { ["start"] = at, ["end"] = at + partText.Length },
                });
            }
            var first = parts[0]!;
            claims.Add(new JsonObject
            {
                ["headingOrdinal"] = ordinal,
                ["sourceAlias"] = first["sourceAlias"]!.GetValue<string>(),
                ["sourceId"] = first["sourceId"]!.GetValue<string>(),
                ["exactText"] = text,
                ["selectionMode"] = "WHOLE_ALIAS",
                ["semanticRole"] = old["semanticRole"]?.DeepClone(),
                ["level"] = old["level"]?.DeepClone(),
                ["utf16Span"] = count == 1 ? first["utf16Span"]!.DeepClone() : null,
                ["parts"] = parts,
            });
        }

        gold["source"]!["sourcePath"] = DocxPath;
        gold["source"]!["sourceSha256"] = CanonicalArtifactHash.OfBytes(docx);
        gold["occurrence"]!["claims"] = claims;
        var provenance = gold["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:DOC-0205:source-reconverted-from-original-doc:2026-09-24",
            ["sha256"] = CanonicalArtifactHash.OfText(goldText),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        provenance.Add(new JsonObject
        {
            ["path"] = Manifest,
            ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Manifest)),
            ["role"] = "SOURCE_REGENERATION",
        });

        File.WriteAllBytes(goldPath, new UTF8Encoding(false).GetBytes(
            gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    private static string[] ApprovedTexts()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(AuthoredGold)));
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("exactText").GetString()!).ToArray();
    }

    /// <summary>Each heading as one paragraph, or two consecutive ones joined by a space, in order.</summary>
    private static List<(int Start, int Count)> Locate(IReadOnlyList<(string StableId, string Text)> paragraphs, IReadOnlyList<string> headings)
    {
        var found = new List<(int, int)>();
        var from = 0;
        foreach (var heading in headings)
        {
            var hit = -1;
            var count = 0;
            for (var i = from; i < paragraphs.Count && hit < 0; i++)
            {
                if (paragraphs[i].Text.Trim() == heading) { hit = i; count = 1; }
                else if (i + 1 < paragraphs.Count
                    && paragraphs[i].Text.Trim() + " " + paragraphs[i + 1].Text.Trim() == heading) { hit = i; count = 2; }
            }
            Assert.True(hit >= 0, $"'{heading}' is neither one paragraph nor two consecutive ones after paragraph {from}");
            found.Add((hit, count));
            from = hit + count;
        }
        return found;
    }
}
