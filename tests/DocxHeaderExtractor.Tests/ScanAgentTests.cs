using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The scan route over stored evidence: lines of page images with their geometry, OCR confidences, the readings of every view and the vision
/// observations, each marked with its origin; tools that only read that evidence; an answer format in which the agent may take one of the
/// recorded readings; and headings anchored to page, box and view. The agent decides; the program measures, stores and checks.
/// </summary>
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
        Assert.Equal("VLM_OBSERVATION", facts["typography"]!["origin"]!.GetValue<string>());
        Assert.Equal("yes", facts["typography"]!["appearsBold"]!.GetValue<string>());
        Assert.True(facts["ocr"]!["viewsReadItDifferently"]!.GetValue<bool>());
        Assert.Equal(55, facts["ocr"]!["minConfidence"]!.GetValue<double>());
        Assert.False(source.Facts["P001-L001"]["typography"]!["measured"]!.GetValue<bool>());
        // Chương I stands in the middle of the page; the body lines share the page's common left edge.
        Assert.Equal(1, Math.Sign(source.Facts["P001-L002"]["layout"]!["leftEdgeOffsetFromPageCommonPx"]!.GetValue<int>()));
        Assert.Equal(60, source.Facts["P001-L002"]["layout"]!["gapAbovePx"]!.GetValue<int>());
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
        var tools = ScanAgentFlavor.Instance.CreateTools(source);

        var record = Call(tools, ScanAgentTools.TextViews, new { aliases = new[] { "P001-L004" } })["records"]![0]!;

        Assert.Equal(2, record["readings"]!.AsArray().Count);
        var diff = record["differences"]!.AsArray().Single()!;
        Assert.Equal(("Tham", "Thẩm", "UNVERIFIED"), (diff["original"]!.GetValue<string>(), diff["alternative"]!.GetValue<string>(), diff["verificationStatus"]!.GetValue<string>()));
        Assert.Equal(ScanOcrPreflight.UpscaledView, record["vision"]!["choice"]!.GetValue<string>());
        Assert.Equal("VLM_OBSERVATION", record["vision"]!["origin"]!.GetValue<string>());
        Assert.Equal([ScanOcrPreflight.OriginalView, ScanOcrPreflight.UpscaledView], record["readingViewIds"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("Tham", record["readings"]![0]!["lowConfidenceWords"]!.AsArray().Select(w => w!["text"]!.GetValue<string>()));
    }

    [Fact]
    public void Search_reads_every_view_and_says_which_one_holds_the_text()
    {
        var tools = ScanAgentFlavor.Instance.CreateTools(new ScanAgentSource(Evidence()));

        var result = Call(tools, ScanAgentTools.SearchText, new { query = "Thẩm quyền", mode = "literal", offset = 0, limit = 10 });

        var match = result["matches"]!.AsArray().Single()!;
        Assert.Equal("P001-L004", match["alias"]!.GetValue<string>());
        Assert.Equal([ScanOcrPreflight.UpscaledView], match["inViews"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void The_sequence_tool_and_the_integrity_tool_report_observations()
    {
        var tools = ScanAgentFlavor.Instance.CreateTools(new ScanAgentSource(Evidence(), [Vision()]));

        var sequence = Call(tools, ScanAgentTools.Sequence, new { alias = "P001-L002" })["sequence"]!;
        Assert.Equal("Điều #.", sequence["key"]!.GetValue<string>());
        Assert.Equal(2, sequence["linesWithThisKey"]!.GetValue<int>());
        var integrity = Call(tools, ScanAgentTools.Integrity, new { });
        Assert.Equal("OCR_DERIVED", integrity["textOrigin"]!.GetValue<string>());
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
        Assert.Equal(DocxAgentProtocol.Instruction, ScanAgentProtocol.Instruction);
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
    static Func<JsonObject, int, (HttpStatusCode, string)> SelectsArticles(bool firstInvented) => (body, post) =>
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
        var selector = new ScanHeadingAgentSelector(dir, () => handler, parallelism: 1);

        var result = selector.ExtractFrom(Evidence(), [Vision()], "sk-test", CancellationToken.None);

        Assert.Empty(result.UnitFailures);
        Assert.Equal(2, result.Headings.Count);
        var first = result.Headings[0];
        Assert.Equal(("Điều 1. Phạm vi điều chỉnh", "OCR_PRIMARY_VIEW", "UNVERIFIED"), (first.Text, first.TextStatus, first.Verification));
        var second = result.Headings[1];
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", second.Text);
        Assert.Equal("Điều 2. Tham quyền chứng thực", second.PrimaryReading);
        Assert.Equal(("OCR_OTHER_VIEW", "CORROBORATED"), (second.TextStatus, second.Verification));
        var part = second.Parts.Single();
        Assert.Equal((1, "P001-ocr-original-L004", ScanOcrPreflight.UpscaledView, "OCR_DERIVED"), (part.Page, part.LineId, part.ViewId, part.ReadingOrigin));
        Assert.Equal(new PixelBox(380, 330, 520, 40), part.Box);
        Assert.StartsWith("scan-v6:SCAN_AGENT_SOURCE_V1:instruction-", result.SelectorId, StringComparison.Ordinal);
        Assert.Equal(ScanAgentProtocol.SystemPrompt, handler.Bodies[0]["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_reading_nobody_recorded_goes_back_to_the_agent_and_its_own_listed_choice_is_taken()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: true));

        var result = new ScanHeadingAgentSelector(dir, () => handler, parallelism: 1).ExtractFrom(Evidence(), [Vision()], "sk-test", CancellationToken.None);

        Assert.Equal(3, handler.Posts);
        Assert.Contains("PROTOCOL:READING_VIEW_UNKNOWN", handler.Bodies[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Điều 2. Thẩm quyền chứng thực", result.Headings[1].Text);
    }

    [Fact]
    public void A_heading_whose_reading_the_vision_model_did_not_choose_is_a_conflict()
    {
        var handler = new FakeOpenRouter(SelectsArticles(firstInvented: false));

        var result = new ScanHeadingAgentSelector(dir, () => handler, parallelism: 1).ExtractFrom(Evidence(), [Vision(ScanOcrPreflight.OriginalView)], "sk-test", CancellationToken.None);

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
        Assert.Equal(ScanVisionPreflight.SystemPrompt, request["messages"]![0]!["content"]!.GetValue<string>());
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
}
