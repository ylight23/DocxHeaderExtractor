using System.Text.Json;
using DocxHeaderExtractor.PdfHeadingAgent;
using static DocxHeaderExtractor.PdfHeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXPERIMENT (off by default): pattern commitments. The selection gives the pattern a selected heading's first line follows; C# finds the patterns
/// whose lines the segments decided differently, asks once per pattern for the rule and a decision for every member, checks the answer and
/// applies it with every change recorded. No network.
/// </summary>
public sealed class PdfHeadingAgentPatternTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _)!);

    /// <summary>The indices of the lines that open an Article ("Article 12. ..."), found by the normalized opening only.</summary>
    private static int[] Articles() =>
        Enumerable.Range(0, Source.Value.Lines.Count).Where(i => NormalizePattern(Source.Value.Lines[i].Text).StartsWith("article #.", StringComparison.Ordinal)).ToArray();

    private static string A(int i) => Source.Value.Lines[i].Alias;

    /// <summary>Every line decided NOT_SELECT, except the Article lines, of which the first half is SELECT.</summary>
    private static Dictionary<string, string> MixedDecisions()
    {
        var articles = Articles();
        var d = Source.Value.Lines.ToDictionary(l => l.Alias, _ => "NOT_SELECT", StringComparer.Ordinal);
        foreach (var i in articles.Take(articles.Length / 2)) d[A(i)] = "SELECT";
        return d;
    }

    private static (AgentUnit.Model Model, List<JsonElement> Payloads) Scripted(Func<int, JsonElement, string?> script)
    {
        var payloads = new List<JsonElement>();
        var post = 0;
        return ((unit, turn, req) =>
        {
            var n = Interlocked.Increment(ref post);
            using var body = JsonDocument.Parse(req.Body);
            var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!).RootElement.Clone();
            lock (payloads) payloads.Add(payload);
            return new ModelTurn(script(n, payload), [], "stop");
        }, payloads);
    }

    /// <summary>A pattern answer built from the request: every member decided by decide(alias, earlier decision); a changed member carries a reason
    /// and its own alias as evidence unless withEvidence is false.</summary>
    private static string Answer(JsonElement payload, Func<string, string, string> decide, bool withEvidence = true) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = AgentPatterns.Version, pattern = payload.GetProperty("pattern").GetString(), rule = "one rule for these lines",
            decisions = payload.GetProperty("members").EnumerateArray().Select(m =>
            {
                var alias = m.GetProperty("alias").GetString()!;
                var before = m.GetProperty("decision").GetString()!;
                var value = decide(alias, before);
                return value != before && value != "UNRESOLVED" && withEvidence
                    ? (object)new { alias, decision = value, reason = "differs", evidenceRefs = new[] { alias } }
                    : new { alias, decision = value };
            }),
        });

    [Theory]
    [InlineData("  Article 12.  Scope ", "article #. scope")]
    [InlineData("Article #.", "article #.")]
    [InlineData("Điều 3.12", "điều #.#")]
    [InlineData("Section ## (a)", "section # (a)")]
    public void A_pattern_is_normalized_to_lower_case_with_every_number_as_one_hash(string text, string expected) =>
        Assert.Equal(expected, NormalizePattern(text));

    [Theory]
    [InlineData("chapter i", "chapter #", true)]
    [InlineData("chapter iv general provisions", "chapter #", true)]
    [InlineData("article #. scope", "article #.", true)]
    [InlineData("a/ it complies", "#/", true)]
    [InlineData("chapter iv", "chapter #.", false)]
    [InlineData("chapters", "chapter #", false)]
    [InlineData("article #. scope", "article #. other", false)]
    public void A_hash_in_a_pattern_stands_for_one_numbering_token(string line, string pattern, bool opens) =>
        Assert.Equal(opens, AgentPatterns.Opens(line, pattern));

    [Fact]
    public void The_pattern_prompt_asks_for_a_pattern_and_the_production_prompt_is_unchanged()
    {
        Assert.NotEqual(SystemPrompt, SystemPromptPatterns);
        Assert.Contains("Pattern: for a target you select that is the first line of a heading and opens with a number", SystemPromptPatterns);
        Assert.Contains("\"pattern\":\"<the words before the number, the number as #, the punctuation after it>\"", SystemPromptPatterns);
        Assert.Contains("A third key is allowed on a SELECT", SystemPromptPatterns);
        Assert.Contains("Use the facts:", SystemPromptPatterns);
        Assert.DoesNotContain("Use the facts:", SystemPrompt);
        Assert.DoesNotContain("pattern", SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pattern_key_is_refused_unless_patterns_are_on_and_must_be_short()
    {
        var decide = new[] { "L0001:S0", "L0002:S0" };
        var shown = decide.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i);
        string Reply(string pattern) => JsonSerializer.Serialize(new
        {
            protocolVersion = SelectObjectsVersion, status = "COMPLETE", analysis = "a",
            decisions = new object[] { new { alias = decide[0], decision = "SELECT", evidenceRefs = new[] { decide[0] }, reason = "r", pattern }, new { alias = decide[1], decision = "NOT_SELECT" } },
        });

        Assert.Equal("DECISION_KEYS_INVALID", ParseSelect(Reply("Article #."), shown, decide).Failure);
        var on = ParseSelect(Reply("Article #."), shown, decide, patternsAllowed: true);
        Assert.True(on.Ok);
        Assert.Equal("Article #.", on.Patterns![decide[0]]);
        Assert.Equal("PATTERN_INVALID", ParseSelect(Reply(new string('x', MaxPatternChars + 1)), shown, decide, patternsAllowed: true).Failure);
        Assert.Equal("PATTERN_INVALID", ParseSelect(Reply(" "), shown, decide, patternsAllowed: true).Failure);
    }

    [Fact]
    public void A_pattern_whose_lines_were_decided_differently_is_a_conflict_over_every_line_of_the_document_it_opens()
    {
        var articles = Articles();
        Assert.True(articles.Length >= 10);
        var patterns = new Dictionary<string, string> { [A(articles[0])] = "Article 1.", [A(articles[1])] = "Chapter #", [A(articles[2])] = "Article" };

        var (conflicts, skipped) = AgentPatterns.FindConflicts(Source.Value, MixedDecisions(), patterns);

        var c = Assert.Single(conflicts);
        Assert.Equal("article #.", c.Pattern);
        Assert.Equal(articles, c.Members);
        // A pattern that does not open its own line is not used, nor is one without a number: it describes words, not a kind of numbered line.
        Assert.Equal(new[] { "NOT_ITS_OWN_OPENING:" + A(articles[1]), "NO_NUMBER:" + A(articles[2]) }.Order(), skipped.Order());
    }

    [Fact]
    public void A_pattern_whose_lines_were_all_decided_the_same_way_is_not_asked_about()
    {
        var articles = Articles();
        var decisions = MixedDecisions();
        foreach (var i in articles) decisions[A(i)] = "SELECT";
        var (conflicts, skipped) = AgentPatterns.FindConflicts(Source.Value, decisions, new Dictionary<string, string> { [A(articles[0])] = "Article #." });
        Assert.Empty(conflicts);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Every_member_gets_its_own_decision_and_every_change_is_recorded()
    {
        var articles = Articles();
        var decisions = MixedDecisions();
        var odd = A(articles[0]);
        var (model, payloads) = Scripted((_, p) => Answer(p, (alias, _) => alias == odd ? "NOT_SELECT" : "SELECT"));

        var r = AgentPatterns.Reconcile(Source.Value, decisions, new Dictionary<string, string> { [A(articles[0])] = "Article #." },
            new Dictionary<string, string>(), model, new AgentBudget(100, 100), 1, CancellationToken.None, retryDelayMs: 0);

        var payload = Assert.Single(payloads);
        Assert.Equal("article #.", payload.GetProperty("pattern").GetString());
        Assert.Equal(articles.Select(A), payload.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("alias").GetString()));
        Assert.Equal("NOT_SELECT", r.Decisions[odd]);
        Assert.All(articles.Skip(1), i => Assert.Equal("SELECT", r.Decisions[A(i)]));
        // Lines outside the pattern keep their decision.
        Assert.Equal(decisions.Count(d => d.Value == "SELECT") - 1 + (articles.Length - articles.Length / 2), r.Decisions.Count(d => d.Value == "SELECT"));
        var expectedChanges = articles.Skip(articles.Length / 2).Select(A).Append(odd).Order(StringComparer.Ordinal);
        Assert.Equal(expectedChanges, r.Changes.Select(c => c.Alias).Order(StringComparer.Ordinal));
        Assert.All(r.Changes, c => Assert.Equal("article #.", c.Pattern));
        Assert.All(r.Changes, c => Assert.Equal("differs", c.Reason));
        Assert.Equal(1, r.Posts);
        Assert.Empty(r.Failures);
        Assert.Empty(r.Unresolved);
    }

    [Fact]
    public void An_unresolved_member_keeps_its_decision_and_is_reported()
    {
        var articles = Articles();
        var decisions = MixedDecisions();
        var (model, _) = Scripted((_, p) => Answer(p, (_, _) => "UNRESOLVED"));

        var r = AgentPatterns.Reconcile(Source.Value, decisions, new Dictionary<string, string> { [A(articles[0])] = "Article #." },
            new Dictionary<string, string>(), model, new AgentBudget(100, 100), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(decisions, r.Decisions);
        Assert.Empty(r.Changes);
        Assert.Equal(articles.Select(A), r.Unresolved);
    }

    [Fact]
    public void A_change_without_evidence_is_refused_and_after_the_repairs_changes_nothing_and_is_reported()
    {
        var articles = Articles();
        var decisions = MixedDecisions();
        var (model, payloads) = Scripted((_, p) => Answer(p, (_, _) => "SELECT", withEvidence: false));

        var r = AgentPatterns.Reconcile(Source.Value, decisions, new Dictionary<string, string> { [A(articles[0])] = "Article #." },
            new Dictionary<string, string>(), model, new AgentBudget(100, 100), 1, CancellationToken.None, retryDelayMs: 0);

        Assert.Equal(1 + AgentPatterns.MaxRepairs, payloads.Count);
        Assert.Equal(decisions, r.Decisions);
        Assert.Empty(r.Changes);
        Assert.Equal(new[] { "PATTERN-001:PROTOCOL:CHANGE_WITHOUT_EVIDENCE" }, r.Failures);
    }

    private static string Reply(string pattern, params object[] decisions) =>
        JsonSerializer.Serialize(new { protocolVersion = AgentPatterns.Version, pattern, rule = "r", decisions });

    public static TheoryData<string, string?> Answers() => new()
    {
        // Keeping both earlier decisions needs no evidence; a change with a reason and an alias shown is accepted; UNRESOLVED needs none.
        { Reply("p", new { alias = "keep-out", decision = "NOT_SELECT" }, new { alias = "keep-in", decision = "SELECT" }), null },
        { Reply("p", new { alias = "keep-out", decision = "SELECT", reason = "why", evidenceRefs = new[] { "next" } }, new { alias = "keep-in", decision = "UNRESOLVED" }), null },
        { Reply("other", new { alias = "keep-out", decision = "NOT_SELECT" }, new { alias = "keep-in", decision = "SELECT" }), "PROTOCOL:PATTERN_NOT_COPIED" },
        { Reply("p", new { alias = "keep-out", decision = "MAYBE" }, new { alias = "keep-in", decision = "SELECT" }), "PROTOCOL:DECISION_INVALID" },
        { Reply("p", new { alias = "keep-out", decision = "SELECT", reason = "why" }, new { alias = "keep-in", decision = "SELECT" }), "PROTOCOL:CHANGE_WITHOUT_EVIDENCE" },
        { Reply("p", new { alias = "keep-out", decision = "SELECT", reason = "why", evidenceRefs = new[] { "elsewhere" } }, new { alias = "keep-in", decision = "SELECT" }), "PROTOCOL:EVIDENCE_NOT_SHOWN" },
        { Reply("p", new { alias = "keep-out", decision = "NOT_SELECT" }), "PROTOCOL:COVERAGE_INCOMPLETE" },
        { Reply("p", new { alias = "keep-out", decision = "NOT_SELECT" }, new { alias = "keep-in", decision = "SELECT" }, new { alias = "next", decision = "SELECT" }), "PROTOCOL:DECISION_NOT_A_MEMBER" },
        { Reply("p", new { alias = "keep-out", decision = "NOT_SELECT" }, new { alias = "keep-out", decision = "NOT_SELECT" }, new { alias = "keep-in", decision = "SELECT" }), "PROTOCOL:ALIAS_LISTED_TWICE" },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public void A_pattern_answer_is_checked(string reply, string? failure)
    {
        var earlier = new Dictionary<string, string> { ["keep-out"] = "NOT_SELECT", ["keep-in"] = "SELECT" };
        var shown = new HashSet<string>(earlier.Keys) { "next" };
        var parsed = AgentPatterns.Parse(reply, "p", earlier, shown, out var got, out _);
        Assert.Equal(failure, got);
        Assert.Equal(failure is null, parsed is not null);
    }
}
