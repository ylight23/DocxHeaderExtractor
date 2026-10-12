using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D2.3 (offline): paged registry search, manifest binding, layout evidence, separated protocol reporting.</summary>
public sealed class P7F1V6HierD23Tests
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Instr = "Trích xuất các heading chính";
    const string Proto = "P7_F1_V6_HIER_NODE_V2";
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IReadOnlyList<P7F1V6Hier.Cand> Cands()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Rfc));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return legacy.AllLines.Where(l => l.Page is 1 or 2)
            .Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToArray();
    }

    static P7F1V6Hier.Options Opts() => new(Instr, "src-sha", ShardSize: 6, Context: 2, FanIn: 3, PassUpCap: 8,
        SourceUniverseSha256: "universe-sha", ParserVersion: "parser-v1", EvidenceStoreVersion: "store-v1");

    static bool Even(string alias) => int.Parse(alias[1..5]) % 2 == 0;

    static JsonElement Root(string payload) => JsonDocument.Parse(payload).RootElement;

    static string Quoted(IEnumerable<string> ids) => string.Join(",", ids.Select(i => $"\"{i}\""));

    static IEnumerable<string> ShownIds(string payload) =>
        Root(payload).GetProperty("shown").EnumerateArray().Select(s => s.GetProperty("id").GetString()!);

    static string Node(string nodeId, string items = "", string confirmed = "") =>
        $"{{\"protocolVersion\":\"{Proto}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"items\":[{items}]"
        + (confirmed == "" ? "" : $",\"confirmed\":[{confirmed}]") + "}";

    static string Retrieve(string nodeId, string search, int? offset = null)
    {
        object rt = offset is null ? new { search } : new { search, offset = offset.Value };
        return JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = rt }, Json);
    }

    /// <summary>Local nodes decide by alias parity; reconcile, synthesis and global confirm everything they were shown.</summary>
    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        var r = Root(payload);
        if (kind == P7F1V6Hier.Kind.Local)
        {
            var items = r.GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                return $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{(Even(a) ? "SELECT" : "NOT_SELECT")}\",\"evidenceRefs\":[\"{a}\"]}}";
            });
            return Node(nodeId, string.Join(",", items));
        }
        if (kind == P7F1V6Hier.Kind.Reconcile)
            return Node(nodeId, confirmed: Quoted(r.GetProperty("owned").EnumerateArray().Select(o => o.GetProperty("id").GetString()!)));
        return Node(nodeId, confirmed: Quoted(ShownIds(payload)));
    }

    static IEnumerable<string> AllPayloads(IEnumerable<string> captured) => captured;

    static IReadOnlyList<P7F1V6Hier.Cand> Synthetic()
    {
        string[] texts = ["Intro", "Body text", "Intro", "Appendix", "Body text", "Summary"];
        return texts.Select((t, i) => new P7F1V6Hier.Cand($"L{i + 1:D4}:S0", 1, t)).ToArray();
    }

    // ---------------- paged registry search ----------------

    [Fact] public void Search_pages_carry_totals_and_next_offsets_and_nothing_is_cut_silently()
    {
        var c = Cands();
        var pages = new List<JsonElement>();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var searches = Root(payload).GetProperty("retrieved").GetProperty("searches");
            if (searches.GetArrayLength() == 0) return Retrieve(nodeId, "e");
            if (searches.GetArrayLength() == 1)
            {
                pages.Add(searches[0].Clone());
                var next = searches[0].GetProperty("nextOffset");
                return Retrieve(nodeId, "e", next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null);
            }
            pages.Add(searches[1].Clone());
            return Node(nodeId, confirmed: Quoted(ShownIds(payload)));
        });
        Assert.Equal(2, pages.Count);
        var p0 = pages[0];
        var total = p0.GetProperty("totalMatches").GetInt32();
        Assert.True(total > 20, "the fixture must have more matches than one page");
        Assert.Equal(20, p0.GetProperty("returned").GetInt32());
        Assert.True(p0.GetProperty("hasMore").GetBoolean());
        Assert.Equal(20, p0.GetProperty("nextOffset").GetInt32());
        var p1 = pages[1];
        Assert.Equal(total, p1.GetProperty("totalMatches").GetInt32());
        Assert.Equal(20, p1.GetProperty("offset").GetInt32());
        var ids0 = p0.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        var ids1 = p1.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        Assert.Empty(ids0.Intersect(ids1));
        Assert.Equal(p1.GetProperty("hasMore").GetBoolean() ? 1 : 0, run.Summary.UnpagedSearches);
        Assert.True(run.Summary.SearchPagesWithMore >= 1);
    }

    [Fact] public void Duplicate_text_in_the_registry_is_returned_as_distinct_occurrences()
    {
        var c = Synthetic();
        var captured = new List<string>();
        var run = P7F1V6Hier.Run(c, Opts() with { ShardSize = 2 }, (nodeId, kind, payload) =>
        {
            captured.Add(payload);
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var searches = Root(payload).GetProperty("retrieved").GetProperty("searches");
            return searches.GetArrayLength() == 0 ? Retrieve(nodeId, "Intro") : Node(nodeId, confirmed: Quoted(ShownIds(payload)));
        });
        Assert.Empty(run.Violations);
        var last = Root(captured.Last(p => p.Contains("\"searches\":[{", StringComparison.Ordinal)));
        var search = last.GetProperty("retrieved").GetProperty("searches")[0];
        Assert.Equal(2, search.GetProperty("totalMatches").GetInt32());
        Assert.False(search.GetProperty("hasMore").GetBoolean());
        var aliases = search.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("sourceAlias").GetString()!).ToArray();
        Assert.Equal(new[] { "L0001:S0", "L0003:S0" }, aliases.OrderBy(a => a).ToArray());
        Assert.Equal(2, search.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact] public void A_search_offset_must_be_a_non_negative_integer_and_only_accompany_a_search()
    {
        var c = Cands();
        var negative = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => nodeId == "L1-N0001"
            ? JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = new { search = "e", offset = -1 } }, Json)
            : Honest(nodeId, kind, payload));
        Assert.Contains(negative.Violations, v => v == "L1-N0001:OFFSET_INVALID");
        var orphan = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) => nodeId == "L1-N0001"
            ? JsonSerializer.Serialize(new { protocolVersion = Proto, nodeId, status = "RETRIEVE", retrieve = new { reopen = new[] { "zzzz" }, offset = 3 } }, Json)
            : Honest(nodeId, kind, payload));
        Assert.Contains(orphan.Violations, v => v == "L1-N0001:OFFSET_WITHOUT_SEARCH");
    }

    // ---------------- manifest: refused before any model call, bound into every payload ----------------

    [Fact] public void A_manifest_without_the_source_universe_is_refused_before_any_model_call()
    {
        int calls = 0;
        var ex = Assert.Throws<ArgumentException>(() => P7F1V6Hier.Run(Cands(), Opts() with { SourceUniverseSha256 = "" },
            (a, b, d) => { calls++; return Honest(a, b, d); }));
        Assert.Contains("MANIFEST_INCOMPLETE:sourceUniverseSha256", ex.Message);
        Assert.Equal(0, calls);
    }

    [Fact] public void A_candidate_list_that_differs_from_the_expected_hash_is_refused_before_any_model_call()
    {
        int calls = 0;
        var ex = Assert.Throws<ArgumentException>(() => P7F1V6Hier.Run(Cands(), Opts() with { ExpectedCandidatesSha256 = "0000" },
            (a, b, d) => { calls++; return Honest(a, b, d); }));
        Assert.Contains("CANDIDATES_MISMATCH", ex.Message);
        Assert.Equal(0, calls);
    }

    [Fact] public void Every_payload_carries_the_manifest_and_the_expected_candidate_hash_is_accepted_when_it_matches()
    {
        var c = Cands();
        var first = P7F1V6Hier.Run(c, Opts(), Honest);
        var captured = new List<string>();
        var second = P7F1V6Hier.Run(c, Opts() with { ExpectedCandidatesSha256 = first.Summary.CandidatesSha256 }, (n, k, p) => { captured.Add(p); return Honest(n, k, p); });
        Assert.NotEmpty(captured);
        Assert.All(captured, p =>
        {
            var m = Root(p).GetProperty("manifest");
            Assert.Equal("universe-sha", m.GetProperty("sourceUniverseSha256").GetString());
            Assert.Equal("src-sha", m.GetProperty("sourceSha256").GetString());
            Assert.Equal(first.Summary.CandidatesSha256, m.GetProperty("candidatesSha256").GetString());
        });
        Assert.False(string.IsNullOrEmpty(second.Summary.ManifestSha256));
        Assert.Equal("PROTOCOL_RESOLVED", second.RunStatus);
    }

    // ---------------- layout evidence is declared, never assumed ----------------

    [Fact] public void Layout_is_sent_as_PRESENT_when_the_source_has_it()
    {
        var captured = new List<string>();
        P7F1V6Hier.Run(Cands(), Opts(), (n, k, p) => { captured.Add(p); return Honest(n, k, p); });
        Assert.Contains(captured, p => p.Contains("\"layoutEvidence\":\"PRESENT\"", StringComparison.Ordinal));
        Assert.Contains(captured, p => p.Contains("\"bbox\"", StringComparison.Ordinal));
    }

    [Fact] public void Without_layout_every_candidate_is_declared_ABSENT_and_never_PRESENT()
    {
        var captured = new List<string>();
        P7F1V6Hier.Run(Synthetic(), Opts() with { ShardSize = 2 }, (n, k, p) => { captured.Add(p); return Honest(n, k, p); });
        Assert.Contains(captured, p => p.Contains("\"layoutEvidence\":\"ABSENT\"", StringComparison.Ordinal));
        Assert.DoesNotContain(captured, p => p.Contains("\"layoutEvidence\":\"PRESENT\"", StringComparison.Ordinal));
    }

    // ---------------- protocol reporting: retrieval turns are not rejections ----------------

    [Fact] public void Retrieval_turns_are_reported_apart_from_rejected_and_stopped_nodes()
    {
        var c = Cands();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var searches = Root(payload).GetProperty("retrieved").GetProperty("searches");
            return searches.GetArrayLength() == 0 ? Retrieve(nodeId, "e") : Node(nodeId, confirmed: Quoted(ShownIds(payload)));
        });
        var s = run.Summary;
        Assert.Empty(run.Violations);
        Assert.Equal(1, s.RetrievalTurns);
        Assert.Equal(0, s.RejectedNodes);
        Assert.Equal(0, s.StoppedNodes);
        Assert.Equal(run.Nodes.Count(n => n.Status == "ACCEPTED"), s.AcceptedNodes);
        Assert.True(s.ProtocolResolved);
        Assert.True(s.EvidenceRefsPresent);
    }

    [Fact] public void Global_review_coverage_is_reported_as_a_share_of_occurrences_not_as_a_status()
    {
        var run = P7F1V6Hier.Run(Cands(), Opts(), Honest);
        var s = run.Summary;
        Assert.Equal(s.GlobalReviewed / (double)s.Occurrences, s.GlobalReviewCoverage, 9);
        Assert.InRange(s.GlobalReviewCoverage, 0.0, 1.0);
        Assert.Equal(s.Occurrences, s.Decided);   // every occurrence has a state; coverage is a separate question
    }

    [Fact] public void A_rejected_synthesis_node_is_reported_as_rejected_and_not_as_protocol_resolved()
    {
        var run = P7F1V6Hier.Run(Cands(), Opts(), (nodeId, kind, payload) => nodeId == "L1-N0001" ? "not json" : Honest(nodeId, kind, payload));
        Assert.Equal(1, run.Summary.RejectedNodes);
        Assert.False(run.Summary.ProtocolResolved);
        Assert.NotEqual("PROTOCOL_RESOLVED", run.RunStatus);
    }

    [Fact] public void Unpaged_accounting_is_per_query_so_a_completed_query_cannot_hide_an_unread_one()
    {
        var c = Cands();
        var captured = new List<string>();
        var run = P7F1V6Hier.Run(c, Opts(), (nodeId, kind, payload) =>
        {
            captured.Add(payload);
            if (nodeId != "L1-N0001") return Honest(nodeId, kind, payload);
            var searches = Root(payload).GetProperty("retrieved").GetProperty("searches");
            return searches.GetArrayLength() switch
            {
                0 => Retrieve(nodeId, "e"),        // many matches: only the first page is read
                1 => Retrieve(nodeId, "HTTP"),     // a second query, possibly complete
                _ => Node(nodeId, confirmed: Quoted(ShownIds(payload))),
            };
        });
        Assert.Empty(run.Violations);
        var finalSearches = Root(captured.Last(p => p.Contains("\"searches\":[{", StringComparison.Ordinal)))
            .GetProperty("retrieved").GetProperty("searches");
        var unread = finalSearches.EnumerateArray()
            .Select(x => x.GetProperty("totalMatches").GetInt32() - (x.GetProperty("offset").GetInt32() + x.GetProperty("returned").GetInt32()))
            .Where(u => u > 0).ToArray();
        Assert.NotEmpty(unread);
        Assert.Equal(unread.Length, run.Summary.UnpagedSearches);
        Assert.Equal(unread.Sum(), run.Summary.UnreadMatches);
    }

    [Fact] public void Candidate_pin_covers_layout_and_is_reported_as_pinned_only_when_supplied()
    {
        var c = Cands();
        var unpinned = P7F1V6Hier.Run(c, Opts(), Honest);
        Assert.False(unpinned.Summary.CandidatesPinned);
        var stripped = c.Select(x => new P7F1V6Hier.Cand(x.Alias, x.Page, x.Text)).ToArray();
        Assert.NotEqual(P7F1V6Hier.CandidatesSha256(c), P7F1V6Hier.CandidatesSha256(stripped));
        var pinned = P7F1V6Hier.Run(c, Opts() with { ExpectedCandidatesSha256 = P7F1V6Hier.CandidatesSha256(c) }, Honest);
        Assert.True(pinned.Summary.CandidatesPinned);
        Assert.Equal(P7F1V6Hier.CandidatesSha256(c), pinned.Summary.CandidatesSha256);
    }

    [Fact] public void Presented_share_counts_occurrences_shown_as_text_and_is_not_an_evidence_claim()
    {
        var run = P7F1V6Hier.Run(Cands(), Opts(), Honest);
        Assert.Equal(run.Summary.Occurrences, run.Summary.PresentedOccurrences);
        Assert.Equal(1.0, run.Summary.PresentedShare, 9);
        Assert.True(run.Summary.EvidenceRefsPresent);
    }
}
