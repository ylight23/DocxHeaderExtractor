using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D1: read tools search_document_text and get_page_text on real development PDFs. Offline only.</summary>
public sealed class P7F1V6ToolsTests
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Fin = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/049_IDA_Financial_Statements_September_2024.pdf";
    const string Form = "todo10_8/heading_corpus_100/06_dich_song_ngu/087_ND_53-2022_An_ninh_mang_EN.pdf";
    const string VnDecree = "todo10_8/heading_corpus_100/01_phap_quy/024_ND_15-2020_Xu_phat_BC_VT_CNTT.pdf";

    static (P7F1QEvidenceTools Legacy, P7F1V6Tools Tools) Load(string pdf, IReadOnlySet<int>? granted = null)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(pdf));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return (legacy, new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, legacy.AllLines, granted));
    }

    static JsonElement Body(P7F1V6Tools.Result r) => JsonDocument.Parse(r.Content).RootElement;
    static JsonElement Res(P7F1V6Tools.Result r) => Body(r).GetProperty("result");
    static string[] Aliases(JsonElement arr) => arr.EnumerateArray().Select(x => x.GetProperty("sourceAlias").GetString()!).ToArray();

    [Fact] public void Every_page_is_reachable_and_paging_loses_no_line_or_duplicates_one()
    {
        foreach (var pdf in new[] { Rfc, Fin, Form })
        {
            var (legacy, tools) = Load(pdf);
            foreach (var page in tools.Pages)
            {
                var expected = legacy.AllLines.Where(l => l.Page == page).Select(l => l.Alias).ToArray();
                var got = new List<string>(); int offset = 0; int? total = null;
                while (true)
                {
                    var r = tools.GetPageText("E.p", "REQ", page, offset, 7);
                    Assert.Equal("OK", r.Status);
                    var res = Res(r); total ??= res.GetProperty("totalLines").GetInt32();
                    got.AddRange(Aliases(res.GetProperty("lines")));
                    if (!res.GetProperty("hasMore").GetBoolean()) break;
                    offset = res.GetProperty("nextOffset").GetInt32();
                }
                Assert.Equal(expected, got);                 // same lines, same reading order, nothing lost
                Assert.Equal(expected.Length, total);
                Assert.Equal(got.Count, got.Distinct().Count());
            }
        }
    }

    [Fact] public void Literal_search_returns_every_match_in_source_order_and_paging_reassembles_them()
    {
        var (legacy, tools) = Load(Rfc);
        var expected = legacy.AllLines.Where(l => l.Page is not null && tools.Pages.Contains(l.Page.Value) && l.Text.Contains("HTTP/2", StringComparison.Ordinal))
            .Select(l => l.Alias).ToArray();
        Assert.NotEmpty(expected);
        var all = new List<string>(); int offset = 0;
        while (true)
        {
            var r = tools.SearchDocumentText("E.s", "REQ", "HTTP/2", "literal", offset, 5);
            Assert.Equal("OK", r.Status);
            var res = Res(r);
            Assert.Equal(expected.Length, res.GetProperty("totalMatches").GetInt32());
            all.AddRange(Aliases(res.GetProperty("matches")));
            if (!res.GetProperty("hasMore").GetBoolean()) break;
            offset = res.GetProperty("nextOffset").GetInt32();
        }
        Assert.Equal(expected, all);                         // complete and in source order
    }

    [Fact] public void Normalised_search_matches_the_TOC_and_the_body_copy_of_the_same_text_on_different_pages()
    {
        var (_, tools) = Load(Rfc);
        var r = tools.SearchDocumentText("E.n", "REQ", "introduction", "normalized", 0, 50);
        var pages = Res(r).GetProperty("matches").EnumerateArray().Select(x => x.GetProperty("page").GetInt32()).Distinct().Order().ToArray();
        Assert.Contains(2, pages);                           // the table-of-contents entry
        Assert.True(pages.Length >= 2, "the same text must be found on at least one other page");
    }

    [Fact] public void Normalised_search_folds_Vietnamese_diacritics_and_ignores_parser_spacing()
    {
        var (legacy, tools) = Load(VnDecree);
        // The title is printed in capitals with diacritics. A query without diacritics and with other spacing must still match.
        var lineText = legacy.AllLines.Where(l => l.Page == 1).Select(l => l.Text).ToArray();
        Assert.Contains(lineText, t => t.Contains("CHÍNH", StringComparison.Ordinal));
        var r = tools.SearchDocumentText("E.v", "REQ", "chinh phu", "normalized", 0, 50);
        Assert.True(Res(r).GetProperty("totalMatches").GetInt32() > 0, "normalised search must fold diacritics and spacing");
        Assert.Equal(1, Res(r).GetProperty("matches").EnumerateArray().First().GetProperty("page").GetInt32());
    }

    [Fact] public void A_search_with_no_result_is_an_explicit_empty_page_not_an_error()
    {
        var (_, tools) = Load(Rfc);
        var r = tools.SearchDocumentText("E.0", "REQ", "zzqxv_no_such_text", "literal", 0, 10);
        Assert.Equal("OK", r.Status);
        var res = Res(r);
        Assert.Equal(0, res.GetProperty("totalMatches").GetInt32());
        Assert.False(res.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, res.GetProperty("nextOffset").ValueKind);
    }

    [Fact] public void Long_page_is_truncated_by_the_limit_only_with_an_explicit_next_offset()
    {
        var (legacy, tools) = Load(Rfc);
        var total = legacy.AllLines.Count(l => l.Page == 16);
        Assert.True(total > 5, "page 16 must need paging at limit 5");
        var first = Res(tools.GetPageText("E.l", "REQ", 16, 0, 5));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(5, first.GetProperty("nextOffset").GetInt32());
        Assert.Equal(5, first.GetProperty("returned").GetInt32());
        Assert.Equal(total, first.GetProperty("totalLines").GetInt32());
    }

    [Fact] public void Every_error_has_an_explicit_code_and_writes_a_transcript_record()
    {
        var (_, tools) = Load(Rfc);
        string Code(P7F1V6Tools.Result r) { Assert.Equal("ERROR", r.Status); return r.ErrorCode!; }
        Assert.Equal("QUERY_EMPTY", Code(tools.SearchDocumentText("E.e1", "REQ", "  ", "literal", 0, 5)));
        Assert.Equal("QUERY_TOO_LONG", Code(tools.SearchDocumentText("E.e2", "REQ", new string('x', 201), "literal", 0, 5)));
        Assert.Equal("MODE_INVALID", Code(tools.SearchDocumentText("E.e3", "REQ", "x", "semantic", 0, 5)));
        Assert.Equal("OFFSET_INVALID", Code(tools.SearchDocumentText("E.e4", "REQ", "x", "literal", -1, 5)));
        Assert.Equal("LIMIT_INVALID", Code(tools.SearchDocumentText("E.e5", "REQ", "x", "literal", 0, 51)));
        Assert.Equal("PAGE_NOT_IN_DOCUMENT", Code(tools.GetPageText("E.e6", "REQ", 9999, 0, 5)));
        var rec = tools.GetPageText("E.e7", "REQ", 9999, 0, 5).Record;
        Assert.Equal("PAGE_NOT_IN_DOCUMENT", rec.ErrorCode);
        Assert.Equal("E.e7", rec.EvidenceId);
    }

    [Fact] public void Scope_is_enforced_and_a_denied_page_says_so()
    {
        var (_, tools) = Load(Rfc, new HashSet<int> { 1 });
        Assert.Equal("OK", tools.GetPageText("E.g1", "REQ", 1, 0, 5).Status);
        Assert.Equal("SCOPE_DENIED", tools.GetPageText("E.g2", "REQ", 2, 0, 5).ErrorCode);
        var s = Res(tools.SearchDocumentText("E.g3", "REQ", "HTTP/2", "literal", 0, 50));
        Assert.All(s.GetProperty("matches").EnumerateArray(), m => Assert.Equal(1, m.GetProperty("page").GetInt32()));
    }

    [Fact] public void Provenance_is_complete_and_the_content_hash_recomputes()
    {
        var (legacy, tools) = Load(Rfc);
        var r = tools.GetPageText("E.p1", "REQ-P", 1, 0, 10);
        Assert.Equal(legacy.SourceSha256, r.Record.SourceSha256);
        Assert.Equal(legacy.SourceAliasUniverseSha256, r.Record.UniverseSha256);
        Assert.Equal(legacy.EvidenceStoreSha256, r.Record.StoreSha256);
        Assert.Equal("REQ-P", r.Record.RequestId);
        Assert.Equal("get_page_text", r.Record.Tool);
        Assert.Equal(P7F1V6Tools.Version, r.Record.Version);
        Assert.Equal(SpatialCanonical.Hash(r.Content), r.Record.ContentSha256);
        var aliases = Aliases(Res(r).GetProperty("lines"));
        Assert.Equal(aliases.ToHashSet(StringComparer.Ordinal), r.Record.ReturnedAliases.ToHashSet(StringComparer.Ordinal));
    }

    [Fact] public void Observed_and_searched_scopes_are_separate_and_match_what_was_returned()
    {
        var (_, tools) = Load(Rfc);
        var s = tools.SearchDocumentText("E.o1", "REQ", "HTTP/2", "literal", 0, 3);
        var (observed, _, searched) = tools.ScopeLedger;
        Assert.Equal(3, observed.Count);                      // only what was returned to the model
        Assert.Equal(observed.Order(), searched.Order());
        var pageRead = tools.GetPageText("E.o2", "REQ", 1, 0, 50);
        var (observed2, pages2, searched2) = tools.ScopeLedger;
        Assert.Contains(1, pages2);
        Assert.True(observed2.Count > observed.Count);
        Assert.Equal(3, searched2.Count);                     // a page read is not a search
    }

    [Fact] public void Lines_the_parser_cannot_place_are_counted_and_reported_never_dropped()
    {
        var (legacy, _) = Load(Rfc);
        var lines = legacy.AllLines.ToList();
        var orphan = lines[0] with { Page = null, Alias = "ZZZZ:S0", Ordinal = -1 };
        var t = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, [orphan, .. lines]);
        Assert.Equal(1, t.UnplacedLines);
        var s = Res(t.SearchDocumentText("E.u", "REQ", "HTTP/2", "literal", 0, 50));
        Assert.Equal("PARTIAL_PLACEMENT", s.GetProperty("sourceStatus").GetString());
        Assert.Equal(1, s.GetProperty("unplacedLinesExcluded").GetInt32());
    }
}
