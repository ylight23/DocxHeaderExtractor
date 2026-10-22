using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Tools;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The scan route over stored evidence: lines of page images with their geometry, OCR confidences, the readings of every view and the vision
/// observations, each marked with its origin; tools that only read that evidence; an answer format in which the agent may take one of the
/// recorded readings; and headings anchored to page, box and view. The agent decides; the program measures, stores and checks.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ScanAgentTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-scan-agent-" + Guid.NewGuid().ToString("N"));

    public ScanAgentTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    static OcrLine Line(string view, int n, string text, int left, int top, int width, double min = 95, bool overlap = false) =>
        new($"P001-{view}-L{n:000}", text, new PixelBox(left, top, width, 40), Math.Max(min, 90), min, overlap,
            text.Split(' ').Select((w, k) => new OcrWord(w, new PixelBox(left + 60 * k, top, 50, 40), k == 2 && min < 60 ? min : 95)).ToArray());

    /// <summary>One page, two views. The primary view misreads "Thẩm" as "Tham" in the heading of article 2; the upscaled view reads it.</summary>
    static ScanOcrEvidence Evidence()
    {
        OcrView View(string id, string article2) => new(
            new OcrViewSpec(id, id == ScanOcrPreflight.OriginalView ? "none" : "grey, scaled 1.5x", id == ScanOcrPreflight.OriginalView ? 1 : 1.5, "tesseract", "tesseract 5.3.4", "vie+eng", 1),
            EvidenceOrigin.OcrDerived,
            [new OcrPage(1, new string('a', 64), 1700, 2200, new string('b', 64),
            [
                Line(id, 1, "Chương I", 760, 100, 180),
                Line(id, 2, "Điều 1. Phạm vi điều chỉnh", 380, 200, 600),
                Line(id, 3, "1. Nghị định này quy định về chứng thực", 300, 260, 1100),
                Line(id, 4, article2, 380, 330, 520, min: id == ScanOcrPreflight.OriginalView ? 55 : 92),
                Line(id, 5, "Trưởng phòng thực hiện chứng thực các việc", 300, 390, 1100),
            ])]);
        return new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedImageOnly", 1, new string('d', 64),
            [View(ScanOcrPreflight.OriginalView, "Điều 2. Tham quyền chứng thực"), View(ScanOcrPreflight.UpscaledView, "Điều 2. Thẩm quyền chứng thực")], []);
    }

    static VlmPageObservation Vision(string choice = ScanOcrPreflight.UpscaledView, string? proposal = null) =>
        new(VlmPageObservation.CurrentVersion, 1, new string('a', 64), AgentProtocol.ChatModel, ScanVisionPreflight.PromptVersion,
            ["P001-ocr-original-L001", "P001-ocr-original-L004"], ["P001-ocr-original-L004"],
            [new VlmLineObservation("P001-ocr-original-L004", "yes", "same", "left")],
            [new VlmReadingChoice("P001-ocr-original-L004", choice, proposal)], [], null, null);

    // 023: three grouping batches timed out once at 300 s and the whole batch was withheld; a selection unit retries such a failure once.
    [Theory]
    [InlineData("TRANSPORT:TIMEOUT_OR_CANCELLED", 1, true)]
    [InlineData("TRANSPORT:TIMEOUT_OR_CANCELLED", 2, false)]
    [InlineData("TRANSPORT:SOME_OTHER_FAILURE", 1, false)]
    public void Grouping_retries_a_timeout_once_and_other_call_failures_not_at_all(string code, int failures, bool recovered)
    {
        var source = new ScanAgentSource(Evidence(), [Vision()]);
        var selected = new HashSet<string> { source.Lines[2].Alias, source.Lines[3].Alias };
        var posts = 0;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            if (++posts <= failures) throw new ReplyFailure(code);
            var payload = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!.Text())!;
            var runs = payload["runs"]!.AsArray().Select(r => (JsonNode)new JsonObject
            {
                ["run"] = r!["run"]!.GetValue<string>(),
                ["headings"] = new JsonArray(new JsonArray(r["lines"]!.AsArray().Select(l => (JsonNode)JsonValue.Create(l!["alias"]!.GetValue<string>())!).ToArray())),
            }).ToArray();
            return new AgentProtocol.ModelTurn(new JsonObject { ["protocolVersion"] = AgentGrouping.GroupVersion, ["runs"] = new JsonArray(runs) }.ToJsonString(), [], "stop");
        };

        var result = AgentGrouping.Group(source, selected, model, new AgentBudget(1000, 100), 1, CancellationToken.None, retryDelayMs: 0);

        if (recovered) { Assert.Empty(result.Failures); Assert.Equal(1, result.Observation.ModelDividedRuns); }
        else { Assert.Single(result.Failures); Assert.Equal(1, result.Observation.FallbackRuns); }
        Assert.Equal(failures + (recovered ? 1 : 0), posts);
    }

    [Fact]
    public void Production_grouping_repairs_dispute_shaped_protocol_without_reopening_membership()
    {
        var source = new ScanAgentSource(Evidence(), [Vision()]);
        var calls = new List<string>();
        AgentUnit.Model model = (unit, turn, request) =>
        {
            calls.Add(unit);
            var body = JsonNode.Parse(request.Body)!;
            if (FakeOpenRouter.VerificationResponse(body.AsObject()) is { } verification)
                return new AgentProtocol.ModelTurn(verification, [], "stop");
            var content = body["messages"]![1]!["content"]!;
            var payload = JsonNode.Parse(content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>())!;
            if (payload["stage"]!.GetValue<string>() == "GROUP")
            {
                if (calls.Count(c => c.StartsWith("GROUP-", StringComparison.Ordinal)) > 1)
                {
                    var validRuns = new JsonArray();
                    foreach (var r in payload["runs"]!.AsArray())
                    {
                        var aliases = r!["lines"]!.AsArray().Select(l => l!["alias"]!.DeepClone()).ToArray();
                        validRuns.Add(new JsonObject
                        {
                            ["run"] = r["run"]!.DeepClone(),
                            ["headings"] = new JsonArray(aliases.Select(a => (JsonNode)new JsonArray(a)).ToArray())
                        });
                    }
                    return new AgentProtocol.ModelTurn(new JsonObject
                    { ["protocolVersion"] = AgentGrouping.GroupVersion, ["runs"] = validRuns }.ToJsonString(), [], "stop");
                }
                var runs = payload["runs"]!.AsArray();
                return new AgentProtocol.ModelTurn(new JsonObject
                {
                    ["protocolVersion"] = AgentGrouping.GroupVersion,
                    ["runs"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject
                    {
                        ["run"] = r!["run"]!.DeepClone(),
                        ["dispute"] = new JsonObject
                        {
                            ["aliases"] = new JsonArray(r["lines"]!.AsArray().Select(l => l!["alias"]!.DeepClone()).ToArray()),
                            ["reason"] = "One line appears to be numbered body text."
                        }
                    }).ToArray())
                }.ToJsonString(), [], "stop");
            }
            var review = payload["selectionReview"] is not null;
            var decisions = new JsonArray();
            foreach (var t in payload["targets"]!.AsArray())
            {
                var alias = t!["alias"]!.GetValue<string>();
                var select = review ? alias == "P001-L004" : alias is "P001-L001" or "P001-L003" or "P001-L004";
                var decision = new JsonObject { ["alias"] = alias, ["decision"] = select ? "SELECT" : "NOT_SELECT" };
                if (select)
                {
                    decision["evidenceRefs"] = new JsonArray(JsonValue.Create(alias));
                    decision["reason"] = "Structural heading according to the instruction and context.";
                    decision["readingViewId"] = review ? ScanOcrPreflight.UpscaledView : ScanOcrPreflight.OriginalView;
                    if (review) decision["headingExtent"] = new JsonObject { ["start"] = 0, ["end"] = 7, ["text"] = "Điều 2." };
                }
                decisions.Add(decision);
            }
            return new AgentProtocol.ModelTurn(new JsonObject
            { ["protocolVersion"] = AgentProtocol.SelectScanVersion, ["status"] = "COMPLETE", ["decisions"] = decisions }.ToJsonString(), [], "stop");
        };
        var result = AgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Empty(result.UnitFailures);
        Assert.Equal(new[] { "P001-L001", "P001-L003", "P001-L004" }, result.SelectedAliases.Order());
        Assert.Contains("P001-L004", result.HeadingReadings!.Keys);
        Assert.Contains("P001-L003", result.HeadingReadings.Keys);
        Assert.Equal(3, ScanSelector.Headings(source, result).Headings.Count);
        Assert.Equal(2, calls.Count(c => c.StartsWith("GROUP-", StringComparison.Ordinal)));
        Assert.Equal(0, result.Grouping!.DisputedRuns);
        Assert.Equal(0, result.Grouping.BoundaryDisputes);
        Assert.DoesNotContain(calls, c => c.StartsWith("REVIEW-SEG-", StringComparison.Ordinal));
    }

    // ---------------- the source ----------------

    [Fact]
    public void Every_line_is_an_ocr_line_with_its_readings_and_its_facts_say_where_each_comes_from()
    {
        var source = new ScanAgentSource(Evidence(), [Vision()]);

        Assert.Equal(["P001-L001", "P001-L002", "P001-L003", "P001-L004", "P001-L005"], source.Lines.Select(l => l.Alias));
        Assert.Equal(new Dictionary<string, string> { [ScanOcrPreflight.OriginalView] = "Điều 2. Tham quyền chứng thực", [ScanOcrPreflight.UpscaledView] = "Điều 2. Thẩm quyền chứng thực" },
            source.Readings("P001-L004"));
        var facts = source.Facts["P001-L004"];
        Assert.Equal("OCR_DERIVED", facts["origin"]!.GetValue<string>());
        Assert.Equal("VLM_OBSERVATION", facts["typography"]!["vision"]!["origin"]!.GetValue<string>());
        Assert.Equal("yes", facts["typography"]!["vision"]!["appearsBold"]!.GetValue<string>());
        // The OCR confidence and view-disagreement facts are left out of the request: they describe the OCR, not the text the model reads.
        Assert.False(facts.ContainsKey("ocr"));
        Assert.False(source.Facts["P001-L001"]["typography"]!["measured"]!.GetValue<bool>());
        // Chương I stands in the middle of the page; the body lines share the page's common left edge.
        // A line's layout is its own box and the page; nothing about its neighbours is in it.
        Assert.False(source.Facts["P001-L002"]["layout"]!.AsObject().ContainsKey("gapAbovePx"));
        Assert.False(source.Facts["P001-L002"]["layout"]!.AsObject().ContainsKey("leftEdgeOffsetFromPageCommonPx"));
        // The repetition count is a number of other lines with the same text; no list of them and no label.
        Assert.Equal(0, source.Facts["P001-L002"]["repetition"]!["otherOccurrences"]!.GetValue<int>());
        Assert.Single(source.Cores);
    }

    [Fact]
    public void A_reading_the_vision_model_proposed_is_offered_as_a_correction_candidate_and_never_replaces_the_ocr()
    {
        var source = new ScanAgentSource(Evidence(), [Vision(VlmPageObservation.NoReading, "Điều 2. Thẩm quyền chứng thực.")]);

        var readings = source.Readings("P001-L004");
        Assert.Equal("Điều 2. Thẩm quyền chứng thực.", readings[ScanAgentSource.VlmProposedReading]);
        Assert.Equal("Điều 2. Tham quyền chứng thực", source.Lines[3].Text);
    }

    // ---------------- the tools ----------------

    static JsonNode Call(IAgentTools tools, string name, object args)
    {
        var r = tools.Execute("E1.01", name, JsonSerializer.Serialize(args));
        Assert.Equal("OK", r.Status);
        return JsonNode.Parse(r.Content)!["result"]!;
    }

    [Fact]
    public void The_text_views_tool_shows_every_reading_where_they_differ_and_the_vision_choice_without_choosing()
    {
        var source = new ScanAgentSource(Evidence(), [Vision()]);
        var tools = ScanFormat.Instance.CreateTools(source);

        var record = Call(tools, ScanAgentTools.TextViews, new { aliases = new[] { "P001-L004" } })["records"]![0]!;

        Assert.Equal(2, record["readings"]!.AsArray().Count);
        var diff = record["differences"]!.AsArray().Single()!;
        Assert.Equal(("Tham", "Thẩm"), (diff["original"]!.GetValue<string>(), diff["alternative"]!.GetValue<string>()));
        Assert.Null(diff["verificationStatus"]);
        Assert.Equal(ScanOcrPreflight.UpscaledView, record["vision"]!["choice"]!.GetValue<string>());
        Assert.Equal("VLM_OBSERVATION", record["vision"]!["origin"]!.GetValue<string>());
        Assert.Equal([ScanOcrPreflight.OriginalView, ScanOcrPreflight.UpscaledView], record["readingViewIds"]!.AsArray().Select(v => v!.GetValue<string>()));
        // Every word comes with its confidence; no threshold picks some of them out.
        var words = record["readings"]![0]!["words"]!.AsArray();
        Assert.Contains("Tham", words.Select(w => w!["text"]!.GetValue<string>()));
        Assert.All(words, w => Assert.NotNull(w!["confidence"]));
        Assert.Null(record["readings"]![0]!["lowConfidenceWords"]);
    }

    [Fact]
    public void Search_reads_every_view_and_says_which_one_holds_the_text()
    {
        var tools = ScanFormat.Instance.CreateTools(new ScanAgentSource(Evidence()));

        var result = Call(tools, ScanAgentTools.SearchText, new { query = "Thẩm quyền", mode = "literal", offset = 0, limit = 10 });

        var match = result["matches"]!.AsArray().Single()!;
        Assert.Equal("P001-L004", match["alias"]!.GetValue<string>());
        Assert.Equal([ScanOcrPreflight.UpscaledView], match["inViews"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void The_integrity_tool_reports_observations_and_no_tool_compares_a_line_with_the_others()
    {
        var tools = ScanFormat.Instance.CreateTools(new ScanAgentSource(Evidence(), [Vision()]));

        Assert.DoesNotContain("get_shape_sequence", ScanAgentTools.Names);
        var integrity = Call(tools, ScanAgentTools.Integrity, new { });
        Assert.Equal(5, integrity["textShown"]!["ocrOriginal"]!.GetValue<int>());
        Assert.False(integrity["transcription"]!["asked"]!.GetValue<bool>());
        Assert.Equal(2, integrity["views"]!.AsArray().Count);
        Assert.Equal(1, integrity["vision"]!["pagesObserved"]!.GetValue<int>());
    }

    [Fact]
    public void The_scan_prompt_tells_the_model_it_decides_and_may_take_only_a_listed_reading()
    {
        Assert.Contains("Take only a listed reading: never write a reading of your own", ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("VLM_OBSERVATION: a model's judgement, not a measurement", ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(AgentProtocol.SelectScanVersion, ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("\"readingViewId\":\"<a viewId from get_text_views>\"", ScanAgentProtocol.AnswerBody, StringComparison.Ordinal);
        // The scan instruction is the DOCX one plus the repetition paragraph; the DOCX instruction itself is unchanged.
        Assert.StartsWith(DocxAgentProtocol.Instruction + "\n", ScanAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("repetition count in the facts", ScanAgentProtocol.Instruction, StringComparison.Ordinal);
    }

    // ---------------- the answer contract ----------------

    static readonly Dictionary<string, int> Shown = new() { ["P001-L004"] = 0 };
    static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Readings = new()
    {
        ["P001-L004"] = new Dictionary<string, string> { ["ocr-original"] = "Điều 2. Tham quyền chứng thực", ["ocr-upscaled"] = "Điều 2. Thẩm quyền chứng thực" },
    };

    static string Answer(object decision) => JsonSerializer.Serialize(new { protocolVersion = AgentProtocol.SelectScanVersion, status = "COMPLETE", decisions = new[] { decision } });

    [Fact]
    public void A_select_may_take_a_listed_reading_and_its_extent_is_measured_on_that_reading()
    {
        var reply = AgentProtocol.ParseSelect(Answer(new { alias = "P001-L004", decision = "SELECT", evidenceRefs = new[] { "P001-L004" }, readingViewId = "ocr-upscaled",
            headingExtent = new { start = 0, end = 29, text = "Điều 2. Thẩm quyền chứng thực" } }), Shown, ["P001-L004"], AgentProtocol.SelectScanVersion,
            new Dictionary<string, string> { ["P001-L004"] = "Điều 2. Tham quyền chứng thực" }, Readings);

        Assert.True(reply.Ok, reply.Failure);
        Assert.Equal("ocr-upscaled", reply.ReadingChoices["P001-L004"]);
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", reply.Extents!["P001-L004"].Text);
    }

    [Theory]
    [InlineData("SELECT", "my-own-reading", "READING_VIEW_UNKNOWN")]
    [InlineData("NOT_SELECT", "ocr-upscaled", "READING_NOT_ON_SELECT")]
    public void A_reading_that_is_not_listed_or_not_on_a_select_is_refused(string decision, string view, string code)
    {
        var reply = AgentProtocol.ParseSelect(Answer(decision == "SELECT"
                ? new { alias = "P001-L004", decision, evidenceRefs = new[] { "P001-L004" }, readingViewId = view }
                : (object)new { alias = "P001-L004", decision, readingViewId = view }),
            Shown, ["P001-L004"], AgentProtocol.SelectScanVersion, new Dictionary<string, string> { ["P001-L004"] = "x" }, Readings);

        Assert.Equal(code, reply.Failure);
    }

    [Fact]
    public void The_pdf_and_docx_formats_refuse_a_reading()
    {
        var reply = AgentProtocol.ParseSelect(Answer(new { alias = "P001-L004", decision = "SELECT", evidenceRefs = new[] { "P001-L004" }, readingViewId = "ocr-upscaled" })
            .Replace(AgentProtocol.SelectScanVersion, AgentProtocol.SelectExtentVersion), Shown, ["P001-L004"], AgentProtocol.SelectExtentVersion, new Dictionary<string, string> { ["P001-L004"] = "x" });

        Assert.Equal("DECISION_KEYS_INVALID", reply.Failure);
    }

    // ---------------- end to end over a fake provider ----------------

    /// <summary>Reads the facts, then selects the two article lines; article 2 in the upscaled view. With <paramref name="firstInvented"/> the
    /// first answer names a reading nobody recorded.</summary>
    static Func<JsonObject, int, (HttpStatusCode, string)> SelectsArticles(bool firstInvented, bool namesNoReading = false) => (body, post) =>
    {
        var messages = body["messages"]!.AsArray();
        var targets = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
        if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = FakeOpenRouter.FirstTurnFacts(body, targets) }, "tool_calls"));
        var refused = messages.Any(m => m!["role"]!.GetValue<string>() == "user" && m["content"] is JsonValue v && v.GetValue<string>().Contains("READING_VIEW_UNKNOWN", StringComparison.Ordinal));
        var view = firstInvented && !refused ? "my-own-reading" : ScanOcrPreflight.UpscaledView;
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
            decisions = targets.Select(a => a switch
            {
                "P001-L002" => (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } },
                "P001-L004" when namesNoReading => new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } },
                "P001-L004" => new { alias = a, decision = "SELECT", evidenceRefs = new[] { a }, readingViewId = view },
                _ => new { alias = a, decision = "NOT_SELECT" },
            }),
        });
        return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = answer }, "stop"));
    };

    [Fact]
    public void The_agent_takes_the_reading_it_judges_right_and_the_heading_records_where_its_text_comes_from()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: false));
        var selector = new ScanSelector(dir, () => handler, parallelism: 1);

        var result = selector.ExtractFrom(Evidence(), [Vision()], "sk-test", CancellationToken.None);

        Assert.Empty(result.UnitFailures);
        Assert.Equal(2, result.Headings.Count);
        Assert.DoesNotContain("verification", result.StageSeconds.Keys);
        var first = result.Headings[0];
        Assert.Equal(("Điều 1. Phạm vi điều chỉnh", "OCR_PRIMARY_VIEW", "UNVERIFIED"), (first.Text, first.TextStatus, first.Verification));
        var second = result.Headings[1];
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", second.Text);
        Assert.Equal("Điều 2. Tham quyền chứng thực", second.PrimaryReading);
        Assert.Equal(("OCR_OTHER_VIEW", "CORROBORATED"), (second.TextStatus, second.Verification));
        var part = second.Parts.Single();
        Assert.Equal((1, "P001-ocr-original-L004", ScanOcrPreflight.UpscaledView, "OCR_DERIVED"), (part.Page, part.LineId, part.ViewId, part.ReadingOrigin));
        Assert.Equal(new PixelBox(380, 330, 520, 40), part.Box);
        Assert.StartsWith("scan-v6:" + ScanAgentSource.Version + ":facts-in-request:instruction-", result.SelectorId, StringComparison.Ordinal);
        Assert.Equal(ScanAgentProtocol.SystemPrompt, handler.Bodies[0]["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_reading_nobody_recorded_goes_back_to_the_agent_and_its_own_listed_choice_is_taken()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: true));

        var result = new ScanSelector(dir, () => handler, parallelism: 1).ExtractFrom(Evidence(), [Vision()], "sk-test", CancellationToken.None);

        // The selection audit (SELECTION_AUDIT) is a call of its own and is not part of the selection being tested.
        Assert.Equal(3, handler.Bodies.Count(b => !b.ToJsonString().Contains("SELECTION_AUDIT", StringComparison.Ordinal)));
        Assert.Contains("PROTOCOL:READING_VIEW_UNKNOWN", handler.Bodies.Last(b => !FakeOpenRouter.IsVerification(b) && !b.ToJsonString().Contains("SELECTION_AUDIT", StringComparison.Ordinal))["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.Text(), StringComparison.Ordinal);
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", result.Headings[1].Text);
    }

    [Fact]
    public void A_heading_whose_reading_the_vision_model_did_not_choose_is_a_conflict()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: false));

        var result = new ScanSelector(dir, () => handler, parallelism: 1).ExtractFrom(Evidence(), [Vision(ScanOcrPreflight.OriginalView)], "sk-test", CancellationToken.None);

        Assert.Equal("CONFLICT", result.Headings[1].Verification);
    }

    // ---------------- the vision preflight ----------------

    static Dictionary<string, Dictionary<string, string>> PageReadings(OcrPage page) => page.Lines.ToDictionary(l => l.Id,
        l => new Dictionary<string, string> { [ScanOcrPreflight.OriginalView] = l.Text, [ScanOcrPreflight.UpscaledView] = l.Id.EndsWith("L004") ? "Điều 2. Thẩm quyền chứng thực" : l.Text });

    [Fact]
    public void The_weakest_lines_are_those_the_views_read_differently_or_with_a_low_confidence_word()
    {
        var page = Evidence().Views[0].Pages[0];

        Assert.Equal(["P001-ocr-original-L004"], ScanVisionPreflight.Weakest(page, PageReadings(page), 10).Select(l => l.Id));
    }

    [Fact]
    public void A_vision_reply_keeps_only_answers_about_asked_lines_from_the_closed_sets()
    {
        var page = Evidence().Views[0].Pages[0];
        var ids = page.Lines.Select(l => l.Id).ToArray();
        var reply = JsonSerializer.Serialize(new
        {
            protocolVersion = VlmPageObservation.CurrentVersion,
            lines = new object[]
            {
                new { lineId = ids[0], appearsBold = "yes", relativeSize = "larger", alignment = "center" },
                new { lineId = ids[1], appearsBold = "very", relativeSize = "same", alignment = "left" },
                new { lineId = "P009-ocr-original-L001", appearsBold = "no", relativeSize = "same", alignment = "left" },
            },
            readings = new object[]
            {
                new { lineId = ids[3], choice = ScanOcrPreflight.UpscaledView, proposedReading = "should not be here" },
                new { lineId = ids[0], choice = "NONE" },
            },
        });

        var o = ScanVisionPreflight.Validate(page, reply, ids, [ids[3]], PageReadings(page));

        Assert.Null(o.Failure);
        Assert.Equal([ids[0]], o.Lines.Select(l => l.LineId));
        Assert.Equal(ScanOcrPreflight.UpscaledView, o.Readings.Single().Choice);
        Assert.Null(o.Readings.Single().ProposedReading);
        Assert.Contains($"line:VALUE_NOT_IN_CLOSED_SET:{ids[1]}", o.Rejected);
        Assert.Contains("line:UNKNOWN_LINE_ID:P009-ocr-original-L001", o.Rejected);
        Assert.Contains($"reading:PROPOSAL_NOT_ALLOWED:{ids[3]}", o.Rejected);
        Assert.Contains($"reading:NOT_ASKED:{ids[0]}", o.Rejected);
        Assert.Equal("REPLY_NOT_JSON", ScanVisionPreflight.Validate(page, "not json", ids, [], PageReadings(page)).Failure);
    }

    [Fact]
    public void The_vision_preflight_sends_the_page_and_the_weakest_crops_to_the_pinned_model_and_stores_what_it_saw_once()
    {
        var magick = TesseractOcr.FindImageMagick();
        if (magick is null) return;
        var evidence = Evidence();
        var entry = Path.Combine(dir, "Hồ sơ scan", "entry");
        Directory.CreateDirectory(Path.Combine(entry, "pages"));
        File.WriteAllBytes(Path.Combine(entry, "pages", "page-001.png"), TestPng.Grey(1700, 2200));
        var handler = new FakeOpenRouter((body, post) =>
        {
            var content = body["messages"]![1]!["content"]!.AsArray();
            var lines = JsonNode.Parse(content[0]!["text"]!.GetValue<string>())!["lines"]!.AsArray().Select(l => l!["lineId"]!.GetValue<string>()).ToArray();
            var crops = content.Where(c => c!["type"]!.GetValue<string>() == "text").Skip(1).Select(c => JsonNode.Parse(c!["text"]!.GetValue<string>())!["crop"]!.GetValue<string>()).ToArray();
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
            {
                content = JsonSerializer.Serialize(new
                {
                    protocolVersion = VlmPageObservation.CurrentVersion,
                    lines = lines.Select(id => new { lineId = id, appearsBold = id.EndsWith("L004") ? "yes" : "no", relativeSize = "same", alignment = "left" }),
                    readings = crops.Select(id => new { lineId = id, choice = ScanOcrPreflight.UpscaledView }),
                }),
            }, "stop"));
        });
        var preflight = new ScanVisionPreflight(Path.Combine(dir, "vlm"), () => handler);

        var observed = preflight.Run(evidence, entry, "sk-test", new ScanVisionPreflight.Options(Parallelism: 1)).Single();

        Assert.Null(observed.Failure);
        Assert.Equal(5, observed.Lines.Count);
        Assert.Equal(["P001-ocr-original-L004"], observed.AskedReadings);
        Assert.Equal(ScanOcrPreflight.UpscaledView, observed.Readings.Single().Choice);
        var request = handler.Bodies.Single();
        Assert.Equal(AgentProtocol.ChatModel, request["model"]!.GetValue<string>());
        Assert.Equal(2, request["messages"]![1]!["content"]!.AsArray().Count(c => c!["type"]!.GetValue<string>() == "image_url"));
        Assert.StartsWith("data:image/png;base64,", request["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ScanVisionPreflight.SystemPrompt, request["messages"]![0]!["content"]!.Text());
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "vlm")));

        var again = preflight.Run(evidence, entry, "sk-test", new ScanVisionPreflight.Options(Parallelism: 1)).Single();

        Assert.Equal(1, handler.Posts);
        Assert.Equal(observed.Lines, again.Lines);
    }

    [Fact]
    public void A_vision_call_the_provider_refuses_is_returned_as_a_failure_and_not_stored()
    {
        var magick = TesseractOcr.FindImageMagick();
        if (magick is null) return;
        var entry = Path.Combine(dir, "Hồ sơ scan", "entry");
        Directory.CreateDirectory(Path.Combine(entry, "pages"));
        File.WriteAllBytes(Path.Combine(entry, "pages", "page-001.png"), TestPng.Grey(1700, 2200));
        var handler = new FakeOpenRouter((body, post) => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"image input not supported\"}}"));

        var observed = new ScanVisionPreflight(Path.Combine(dir, "vlm"), () => handler).Run(Evidence(), entry, "sk-test", new ScanVisionPreflight.Options(Parallelism: 1)).Single();

        Assert.Equal("CALL:TRANSPORT:CLIENT_ERROR", observed.Failure);
        Assert.False(Directory.Exists(Path.Combine(dir, "vlm")) && Directory.GetFiles(Path.Combine(dir, "vlm")).Length > 0);
    }

    // ---------------- the blind transcription ----------------

    static TranscriptionSet Transcriptions() => new(TranscriptionSet.CurrentVersion, AgentProtocol.ChatModel, ScanTranscription.PromptVersion, 2.0,
    [
        new LineTranscription("P001-ocr-original-L001", "Chương I", ScanTranscription.Accepted),
        new LineTranscription("P001-ocr-original-L004", "Điều 2. Thẩm quyền chứng thực", ScanTranscription.Accepted),
        new LineTranscription("P001-ocr-original-L005", "1. Nghị định này\nTrưởng phòng thực hiện chứng thực các việc", ScanTranscription.RejectedMultiline),
    ], [], 1, 0.0001);

    [Theory]
    [InlineData("Mục 3\nCHỨNG THỰC CHỮ KÝ", "CHỨNG THỰC CHỮ KÝ", ScanTranscription.RejectedMultiline)]   // 023, line 41 at 1x
    [InlineData("Mục 3 CHỨNG THỰC CHỮ KÝ", "CHỨNG THỰC CHỮ KÝ", ScanTranscription.RejectedNeighbour, "Mục 3")]   // the line above, no line break
    [InlineData("Chương III", "Chương II", ScanTranscription.Accepted)]
    [InlineData("Mục 1", "¬ MueL n ca", ScanTranscription.Accepted, "CHỨNG THỰC CHỮ KÝ")]
    [InlineData("Điều 4. Thẩm quyền và trách nhiệm cấp bản sao từ sổ gốc", "Điều 4. Tham quyền và trách nhiệm cap bản sao từ sô gốc", ScanTranscription.Accepted)]
    // 023 p3: OCR made a line of specks over the heading; its crop shows the heading, which has its own line.
    [InlineData("Điều 4. Thẩm quyền và trách nhiệm cấp bản sao từ sổ gốc", ". À ` là on & z ` AK", ScanTranscription.RejectedNeighbour, "Điều 4. Tham quyền và trách nhiệm cap bản sao từ sô gốc")]
    [InlineData("Điều 4. Thẩm quyền và trách nhiệm cấp bản sao từ sổ gốc", ". À ` là on & z ` AK", ScanTranscription.RejectedLonger)]
    // A form's leaders: the OCR dropped dots, which are not counted.
    [InlineData("- Ông/bà....................................Giấy chứng minh nhân dân/Hộ chiếu", "- Ông/bà.............. ri Giấy chứng minh nhân dân/Hộ chiếu", ScanTranscription.Accepted, "(3) số... ...,")]
    [InlineData("(Bằng chữ...................................................)", "00 1... ................ )", ScanTranscription.Accepted)]
    [InlineData("   ", "Chương I", ScanTranscription.RejectedEmpty)]
    [InlineData(null, "Chương I", ScanTranscription.NotTranscribed)]
    public void A_transcription_is_a_reading_only_on_one_line_its_own_and_not_much_longer_than_the_ocr(string? transcription, string ocr, string status, string? neighbour = null) =>
        Assert.Equal(status, ScanTranscription.Guard(transcription, ocr, neighbour is null ? null : [neighbour]));

    [Fact]
    public void An_accepted_transcription_is_the_text_shown_and_every_other_reading_stays()
    {
        var source = new ScanAgentSource(Evidence(), [Vision()], Transcriptions());

        Assert.Equal("Điều 2. Thẩm quyền chứng thực", source.Lines[3].Text);
        Assert.Equal(ScanAgentSource.VlmTranscribedReading, source.DefaultReading("P001-L004"));
        Assert.Equal("Điều 2. Tham quyền chứng thực", source.Readings("P001-L004")[ScanOcrPreflight.OriginalView]);
        Assert.Equal([ScanOcrPreflight.UpscaledView], source.OcrViewsAgreeingWithTranscription("P001-L004"));
        // A rejected transcription is not a reading: the OCR stays the text shown.
        Assert.Equal(ScanOcrPreflight.OriginalView, source.DefaultReading("P001-L005"));
        Assert.False(source.Readings("P001-L005").ContainsKey(ScanAgentSource.VlmTranscribedReading));
        var text = source.Facts["P001-L004"]["text"]!;
        Assert.Equal((ScanAgentSource.VlmTranscribedReading, ScanTranscription.Accepted), (text["shownReading"]!.GetValue<string>(), text["transcription"]!.GetValue<string>()));
        Assert.Equal(ScanTranscription.RejectedMultiline, source.Facts["P001-L005"]["text"]!["transcription"]!.GetValue<string>());
        Assert.Equal(ScanTranscription.NotTranscribed, source.Facts["P001-L002"]["text"]!["transcription"]!.GetValue<string>());

        var tools = ScanFormat.Instance.CreateTools(source);
        var records = Call(tools, ScanAgentTools.TextViews, new { aliases = new[] { "P001-L004", "P001-L005" } })["records"]!.AsArray();
        var transcribed = records[0]!["readings"]!.AsArray().Single(r => r!["viewId"]!.GetValue<string>() == ScanAgentSource.VlmTranscribedReading)!;
        Assert.Equal("VLM_TRANSCRIBED", transcribed["origin"]!.GetValue<string>());
        Assert.NotEmpty(transcribed["ocrViewsThatReadTheSame"]!.AsArray());
        Assert.Null(transcribed["verificationStatus"]);
        Assert.Equal(ScanAgentSource.VlmTranscribedReading, records[0]!["shownReading"]!.GetValue<string>());
        Assert.Equal(ScanTranscription.RejectedMultiline, records[1]!["transcriptionRejected"]!["status"]!.GetValue<string>());
        var integrity = Call(tools, ScanAgentTools.Integrity, new { });
        Assert.Equal(2, integrity["textShown"]!["vlmTranscribed"]!.GetValue<int>());
        Assert.Equal("only the crop of the line; no OCR reading", integrity["transcription"]!["seenByModel"]!.GetValue<string>());
    }

    [Fact]
    public void A_heading_takes_the_transcription_when_the_agent_names_no_reading_and_keeps_the_ocr_beside_it()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: false, namesNoReading: true));
        var selector = new ScanSelector(dir, () => handler, parallelism: 1);

        var result = selector.ExtractFrom(Evidence(), [Vision()], "sk-test", CancellationToken.None, Transcriptions());

        Assert.Empty(result.UnitFailures);
        Assert.Equal(("Điều 1. Phạm vi điều chỉnh", "OCR_PRIMARY_VIEW"), (result.Headings[0].Text, result.Headings[0].TextStatus));
        var second = result.Headings[1];
        Assert.Equal(("Điều 2. Thẩm quyền chứng thực", "Điều 2. Tham quyền chứng thực"), (second.Text, second.PrimaryReading));
        Assert.Equal(("VLM_TRANSCRIBED", "CORROBORATED"), (second.TextStatus, second.Verification));
        var part = second.Parts.Single();
        Assert.Equal((ScanAgentSource.VlmTranscribedReading, "VLM_TRANSCRIBED"), (part.ViewId, part.ReadingOrigin));
        Assert.Equal([ScanAgentSource.VlmTranscribedReading, ScanOcrPreflight.UpscaledView], part.ReadingsThatAgree);
        Assert.Equal((2, 3), (result.TranscriptionsAccepted, result.TranscribedLines));
        var rejected = result.TranscriptionRejections.Single();
        Assert.Equal(("P001-L005", ScanTranscription.RejectedMultiline, "Trưởng phòng thực hiện chứng thực các việc"), (rejected.Alias, rejected.Status, rejected.OcrReading));
        // The agent saw the transcription as the line's text.
        Assert.Contains("Điều 2. Thẩm quyền chứng thực", handler.Bodies[0]["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcription_preflight_sends_only_crops_to_the_pinned_model_checks_each_line_and_stores_the_batches()
    {
        if (TesseractOcr.FindImageMagick() is null) return;
        var evidence = Evidence();
        var entry = Path.Combine(dir, "Hồ sơ scan", "entry");
        Directory.CreateDirectory(Path.Combine(entry, "pages"));
        File.WriteAllBytes(Path.Combine(entry, "pages", "page-001.png"), TestPng.Grey(1700, 2200));
        var answers = new Dictionary<string, string>
        {
            ["P001-ocr-original-L001"] = "Chương I", ["P001-ocr-original-L002"] = "Điều 1. Phạm vi điều chỉnh",
            ["P001-ocr-original-L003"] = "Chương I\n1. Nghị định này quy định về chứng thực", ["P001-ocr-original-L004"] = "Điều 2. Thẩm quyền chứng thực",
        };
        var handler = new FakeOpenRouter((body, post) =>
        {
            Assert.Equal(ScanTranscription.SystemPrompt, body["messages"]![0]!["content"]!.Text());
            var content = body["messages"]![1]!["content"]!.AsArray();
            var ids = content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()["id ".Length..]).ToArray();
            Assert.Equal(ids.Length, content.Count(c => c!["type"]!.GetValue<string>() == "image_url"));
            Assert.DoesNotContain(content, c => c!["type"]!.GetValue<string>() == "text" && c["text"]!.GetValue<string>().Contains("quyền", StringComparison.Ordinal));
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
            {
                content = JsonSerializer.Serialize(new { protocolVersion = ScanTranscription.PromptVersion, lines = ids.Where(answers.ContainsKey).Select(id => new { id, text = answers[id] }) }),
            }, "stop"));
        });
        var preflight = new ScanTranscriptionPreflight(Path.Combine(dir, "store"), () => handler);

        var set = preflight.Run(evidence, entry, "sk-test", new ScanTranscriptionPreflight.Options(MaxLinesPerRequest: 3, Parallelism: 1));

        Assert.Empty(set.Failures);
        Assert.Equal(2, handler.Posts);
        Assert.Equal(AgentProtocol.ChatModel, handler.Bodies[0]["model"]!.GetValue<string>());
        // Black and white, 2x: a 1-bit PNG twice the size of the line's box with its margin (Chương I: 180x40 + 2x8).
        var png = Convert.FromBase64String(handler.Bodies[0]["messages"]![1]!["content"]!.AsArray()
            .First(c => c!["type"]!.GetValue<string>() == "image_url")!["image_url"]!["url"]!.GetValue<string>()["data:image/png;base64,".Length..]);
        Assert.Equal(1, png[24]);
        Assert.Equal(1024, handler.Bodies[0]["reasoning"]!["max_tokens"]!.GetValue<int>());
        Assert.Equal("1024", set.Reasoning);
        Assert.Equal((2 * 196, 2 * 56), TesseractOcr.ImageSize(png));
        Assert.Equal([ScanTranscription.Accepted, ScanTranscription.Accepted, ScanTranscription.RejectedMultiline, ScanTranscription.Accepted, ScanTranscription.NotTranscribed],
            set.Lines.Select(l => l.Status));
        Assert.Equal(3, set.Accepted);

        var again = preflight.Run(evidence, entry, "sk-test", new ScanTranscriptionPreflight.Options(MaxLinesPerRequest: 3, Parallelism: 1));

        Assert.Equal(2, handler.Posts);   // read back from the store
        Assert.Equal(set.Lines, again.Lines);
        Assert.Equal(0, again.ProviderCalls);
    }

    [Fact]
    public void The_transcription_budget_is_made_for_the_batches_the_byte_cap_cut_not_for_the_line_count()
    {
        // Six crops of 40 KB: two fit a request under the byte cap, so ten lines per request would plan one call where three are needed
        // (on 023 grey 2x crops of body lines fitted about five, and 65 batches were refused against a ceiling planned from the line count).
        var random = new Random(7);
        var crops = Enumerable.Range(0, 6).Select(k =>
        {
            var file = Path.Combine(dir, $"crop-{k}.png");
            var bytes = new byte[40_000];
            random.NextBytes(bytes);
            File.WriteAllBytes(file, bytes);
            return (string?)file;
        }).ToArray();
        var ids = Enumerable.Range(1, 6).Select(k => $"T{k:000}").ToArray();
        var handler = new FakeOpenRouter((body, post) =>
        {
            var asked = body["messages"]![1]!["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()["id ".Length..]);
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
            {
                content = JsonSerializer.Serialize(new { protocolVersion = ScanTranscription.PromptVersion, lines = asked.Select(id => new { id, text = "Điều " + id }) }),
            }, "stop"));
        });
        var planned = 0;
        var failures = new List<string>();

        var (texts, budget) = ScanTranscription.Transcribe(crops, ids, ids, Path.Combine(dir, "t"), "sk-test",
            n => { planned = n; return new AgentBudget(n, n * AgentBudget.AttemptUsd(AgentBudget.MaxRequestBytes)); }, () => handler, failures, "T", 10, 1);

        Assert.Empty(failures);
        Assert.Equal(3, planned);
        Assert.Equal(3, budget.Calls);
        Assert.Equal(ids.Select(id => "Điều " + id), texts);
    }

    [Fact]
    public void A_reply_in_the_wrong_form_is_asked_once_more_and_a_second_one_is_recorded()
    {
        var crop = Path.Combine(dir, "crop.png");
        File.WriteAllBytes(crop, TestPng.Grey(20, 10));
        string Good(JsonObject body) => JsonSerializer.Serialize(new { protocolVersion = ScanTranscription.PromptVersion, lines = new[] { new { id = "T001", text = "Mục 1" } } });
        var once = new FakeOpenRouter((body, post) => (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = post == 1 ? "{\"lines\":[]}" : Good(body) }, "stop")));
        var failures = new List<string>();

        var (texts, _) = ScanTranscription.Transcribe([crop], ["T001"], ["k1"], Path.Combine(dir, "a"), "sk-test", n => new AgentBudget(4 * n, 1), () => once, failures, "T", 10, 1);

        Assert.Empty(failures);
        Assert.Equal(["Mục 1"], texts);
        Assert.Equal(2, once.Posts);

        var never = new FakeOpenRouter((body, post) => (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{\"lines\":[]}" }, "stop")));
        (texts, _) = ScanTranscription.Transcribe([crop], ["T001"], ["k2"], Path.Combine(dir, "b"), "sk-test", n => new AgentBudget(4 * n, 1), () => never, failures, "T", 10, 1);

        Assert.Equal([null], texts);
        Assert.Equal(2, never.Posts);
        Assert.Equal("T-B001:PROTOCOL_VERSION_INVALID", failures.Single());
        Assert.Empty(Directory.Exists(Path.Combine(dir, "b")) ? Directory.GetFiles(Path.Combine(dir, "b")) : []);   // nothing stored
    }

    [Fact]
    public void A_reply_whose_reasoning_used_every_token_is_sent_once_more_on_the_scan_route_and_a_second_one_fails_the_unit()
    {
        var source = new ScanAgentSource(Evidence(), [Vision()], Transcriptions());
        var budget = AgentSelector.BudgetFor(source, ScanAgentProtocol.Instruction);
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: false, namesNoReading: true));
        using var model = new AgentModel(handler, "sk-test", budget);
        const string exhausted = "AUDIT:FINISH_LENGTH,REASONING_EXHAUSTED";
        var calls = 0;
        AgentUnit.Model once = (unit, turn, req) => ++calls == 1 ? throw new ReplyFailure(exhausted) : model.Turn(unit, turn, req);

        var outcome = AgentUnit.Run(source, 0, once, budget, retryDelayMs: 0, ScanAgentProtocol.Instruction);

        Assert.Equal("COMPLETE", outcome.Status);
        Assert.Equal(1, outcome.TransportRetries);
        Assert.Equal(outcome.RequestSha256s[0], outcome.RequestSha256s[1]);   // the same bytes

        AgentUnit.Model always = (_, _, _) => throw new ReplyFailure(exhausted);
        var failed = AgentUnit.Run(source, 0, always, AgentSelector.BudgetFor(source, ScanAgentProtocol.Instruction), retryDelayMs: 0, ScanAgentProtocol.Instruction);

        Assert.Equal(("FAILED", "CALL:" + exhausted), (failed.Status, failed.Failure));
        Assert.Equal(2, failed.Posts);
        Assert.Empty(((IAgentFormat)PdfFormat.Instance).RetryOnceCodes);
        Assert.Empty(((IAgentFormat)DocxFormat.Instance).RetryOnceCodes);

        // A call that timed out is sent once more too.
        calls = 0;
        AgentUnit.Model late = (unit, turn, req) => ++calls == 1 ? throw new ReplyFailure("TRANSPORT:TIMEOUT_OR_CANCELLED") : model.Turn(unit, turn, req);
        Assert.Equal("COMPLETE", AgentUnit.Run(source, 0, late, AgentSelector.BudgetFor(source, ScanAgentProtocol.Instruction), retryDelayMs: 0, ScanAgentProtocol.Instruction).Status);
    }

    [Fact]
    public void A_batch_that_stays_unreadable_is_split_until_only_the_crop_that_breaks_it_is_left_unread()
    {
        var crops = Enumerable.Range(1, 4).Select(k => { var f = Path.Combine(dir, $"c{k}.png"); File.WriteAllBytes(f, TestPng.Grey(20, 10)); return (string?)f; }).ToArray();
        var ids = Enumerable.Range(1, 4).Select(k => $"T{k:000}").ToArray();
        // Any request holding T002 is answered with no text at all.
        var handler = new FakeOpenRouter((body, post) =>
        {
            var asked = body["messages"]![1]!["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()["id ".Length..]).ToArray();
            return asked.Contains("T002")
                ? (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = (string?)null }, "stop"))
                : (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = JsonSerializer.Serialize(new { protocolVersion = ScanTranscription.PromptVersion, lines = asked.Select(id => new { id, text = "dòng " + id }) }) }, "stop"));
        });
        var failures = new List<string>();

        var (texts, _) = ScanTranscription.Transcribe(crops, ids, ids, Path.Combine(dir, "s"), "sk-test", n => new AgentBudget(40, 2), () => handler, failures, "T", 10, 1);

        Assert.Equal(["dòng T001", null, "dòng T003", "dòng T004"], texts);
        Assert.Equal("T-B001ab:CALL:CONTENT:CONTENT_NOT_TEXT", failures.Single());
    }

    [Theory]
    [InlineData(new[] { 30, 30, 60 }, "0-30 30-60 60-108 108-120")]   // whole pages; a long page in slices of 48
    [InlineData(new[] { 10, 10, 40 }, "0-20 20-60")]                  // short pages are packed while the unit stays within 48 lines
    public void Scan_units_follow_the_pages_like_the_pdf(int[] linesPerPage, string expected)
    {
        var pages = linesPerPage.SelectMany((n, p) => Enumerable.Repeat(p + 1, n)).ToArray();
        Assert.Equal(expected, string.Join(" ", ScanAgentSource.PageCores(pages).Select(c => $"{c.Start}-{c.End}")));
    }

    [Fact]
    public void A_line_inside_a_drawn_frame_carries_the_frame_and_a_line_outside_carries_none()
    {
        var frames = new Dictionary<string, DrawnFrame> { ["P001-ocr-original-L004"] = new(10, 20, 900, 300) };
        var source = new ScanAgentSource(Evidence(), frames: frames);

        var inside = source.Facts["P001-L004"]["layout"]!["drawnFrame"]!;
        Assert.Equal(("MEASURED_ON_IMAGE", 900), (inside["origin"]!.GetValue<string>(), inside["width"]!.GetValue<int>()));
        Assert.Null(source.Facts["P001-L002"]["layout"]!["drawnFrame"]);
        Assert.Contains("drawnFrame", ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Unit_boundaries_are_the_same_whatever_the_facts_say()
    {
        // The facts change between these two sources (a vision observation, a measured type); the units do not.
        var plain = new ScanAgentSource(Evidence());
        var measured = new Dictionary<string, LineTypography>
        {
            ["P001-ocr-original-L004"] = new("P001-ocr-original-L004", 9.5, 2.2, 0.4, 900),
        };
        var seen = new ScanAgentSource(Evidence(), [Vision()], null, measured);

        Assert.NotEqual(plain.Facts["P001-L004"]["typography"]!.ToJsonString(), seen.Facts["P001-L004"]["typography"]!.ToJsonString());
        Assert.Equal(plain.Cores, seen.Cores);
    }

    [Fact]
    public void The_facts_carry_the_type_measured_on_the_image_and_a_vision_observation_only_beside_it()
    {
        var measured = new Dictionary<string, LineTypography>
        {
            ["P001-ocr-original-L004"] = new("P001-ocr-original-L004", 6.2, 1.4, 0.21, 400),
        };
        var source = new ScanAgentSource(Evidence(), [Vision()], null, measured);

        var t = source.Facts["P001-L004"]["typography"]!;
        Assert.Equal(("MEASURED_ON_IMAGE", 6.2), (t["origin"]!.GetValue<string>(), t["strokeWidthPx"]!.GetValue<double>()));
        Assert.Equal("VLM_OBSERVATION", t["vision"]!["origin"]!.GetValue<string>());
        Assert.False(source.Facts["P001-L002"]["typography"]!["measured"]!.GetValue<bool>());
        Assert.Contains("measured on the page image", ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scan_unit_carries_its_targets_facts_so_an_answer_on_the_first_turn_is_complete()
    {
        var source = new ScanAgentSource(Evidence(), [], Transcriptions());
        var budget = AgentSelector.BudgetFor(source, ScanAgentProtocol.Instruction);
        // The model answers at once, with no tool call: the facts were in the request.
        var handler = new FakeOpenRouter((body, post) =>
        {
            var payload = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            var targets = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
            Assert.Equal(targets, payload["targetFacts"]!.AsArray().Select(f => f!["alias"]!.GetValue<string>()));
            Assert.Equal(source.Facts[targets[0]].ToJsonString(), payload["targetFacts"]![0]!.ToJsonString());
            var answer = JsonSerializer.Serialize(new
            {
                protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
                decisions = targets.Select(a => a == "P001-L002" ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } } : new { alias = a, decision = "NOT_SELECT" }),
            });
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = answer }, "stop"));
        });
        using var model = new AgentModel(handler, "sk-test", budget);

        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0, ScanAgentProtocol.Instruction);

        Assert.Equal(("COMPLETE", 1), (outcome.Status, outcome.Posts));
        Assert.Empty(outcome.TargetsWithoutFacts!);
        Assert.Equal("SELECT", outcome.Decisions["P001-L002"]);
        Assert.Contains("carries targetFacts", ScanAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        // PDF and DOCX supply their own route facts too; checked with real sources in TargetsFactsInRequestTests.
    }

    [Fact]
    public void The_pipeline_source_binds_extents_to_the_selected_reading_and_keeps_the_original()
    {
        var source = new ScanAgentSource(Evidence(), [], Transcriptions());
        const string alias = "P001-L004";
        var selection = new HeadingSelection(new HashSet<string> { alias }, 1, 1, [], [], 0, 0)
        { HeadingReadings = new Dictionary<string, string> { [alias] = ScanOcrPreflight.UpscaledView } };
        var prepared = ScanSelector.PrepareSelection(source, selection);
        var bound = ScanSourceAdapter.Build(prepared);
        var line = prepared.Lines.Single(l => l.Alias == alias);
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", line.Text);
        Assert.Equal(ScanOcrPreflight.UpscaledView, line.ReadingViewId);
        Assert.Equal("Điều 2. Tham quyền chứng thực", line.PrimaryReading);
        Assert.Equal(line.Text, bound.Catalog.Units.Single(u => u.SourceId == line.LineId).Text);
        Assert.Equal(line.Box, bound.Catalog.Units.Single(u => u.SourceId == line.LineId).SourceAnchor.Scan!.Box);
        Assert.Equal(line.PrimaryReading, line.Readings[ScanOcrPreflight.OriginalView]);
    }

    [Fact]
    public void Surface_facts_reach_the_first_request_and_the_facts_tool_without_becoming_membership_rules()
    {
        var source = new ScanAgentSource(Evidence());
        const string numbered = "P001-L003";
        var facts = source.Facts[numbered];
        Assert.Equal("1.", facts["surface"]!["leadingToken"]!["text"]!.GetValue<string>());
        Assert.Null(facts["surface"]!["nearestPrecedingNamedNumber"]);
        var tools = ScanFormat.Instance.CreateTools(source);
        var called = Call(tools, ScanAgentTools.LineFacts, new { first = numbered, last = numbered, offset = 0 });
        Assert.Equal(facts.ToJsonString(), called["facts"]![0]!.ToJsonString());
        Assert.Equal([numbered], tools.FactsReturnedFor); // mentioned neighbours do not acquire core facts coverage

        using var handler = new FakeOpenRouter((body, _) =>
        {
            var payload = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal(facts.ToJsonString(), payload["targetFacts"]!.AsArray().Single(f => f!["alias"]!.GetValue<string>() == numbered)!.ToJsonString());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = JsonSerializer.Serialize(new
            {
                protocolVersion = AgentProtocol.SelectScanVersion, status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).Select(a => a == numbered
                    ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } }
                    : new { alias = a, decision = "NOT_SELECT" }),
            }) }, "stop"));
        });
        // Even with numeric-prefix facts, C# must retain the line if the model selects it.
        var result = new ScanSelector(dir, () => handler).ExtractFrom(Evidence(), [], "test-key", CancellationToken.None);
        Assert.Equal("1. Nghị định này quy định về chứng thực", Assert.Single(result.Headings).Text);
        Assert.Empty(result.UnitFailures);
    }
}
