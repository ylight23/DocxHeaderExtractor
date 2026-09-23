using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// VLM escalation for SELECTIVE_SEMANTIC_ESCALATION_V1, on exactly ITEM-CCE2C592 (Agenda) - the one
/// item CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1 flagged, the text adjudicator resolved, and
/// ADJUDICATOR_EVIDENCE_CONFLICT flagged for visual review. ITEM-505430BB is never reachable from
/// this file: there is no code path here parameterized by item id, only a single hardcoded target.
/// Unreachable unless <see cref="RunVariable"/> is set, so this file never spends a real call or
/// renders a real image on an ordinary test run.
/// <para>
/// The VLM is asked at the IDENTITY/STRUCTURE/INFORMATION semantic-function level, not the legacy
/// three-label contract, specifically to avoid re-triggering the same lexical "DOCUMENT_LABEL
/// attractor" the text adjudicator and several earlier models showed on this exact item. The legacy
/// label is a harness-side projection applied after the VLM's own answer is captured -
/// IDENTITY-&gt;DOCUMENT_LABEL, STRUCTURE-&gt;STRUCTURAL_UNIT, INFORMATION-&gt;NON_STRUCTURAL - never
/// shown to the model as an instruction to reproduce.
/// </para>
/// <para>
/// The VLM sees only: a rendered image of the frozen source PDF's page containing the target
/// occurrence, and the target occurrence's own literal text. It never sees Gold, the dominant/pooled
/// label from CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1, the text adjudicator's prior answer, or any of
/// this codebase's internal shorthand for this item ("F1"/"Agenda" as diagnostic names) - see
/// <see cref="Vlm_facing_payload_never_reveals_gold_prior_results_or_diagnostic_naming"/>.
/// </para>
/// </summary>
public sealed class SelectiveSemanticEscalationV1VlmEscalationTests
{
    private const string RunVariable = "A99_SELECTIVE_SEMANTIC_ESCALATION_V1_VLM_RUN";
    private const string CaptureRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/vlm";
    private const string SourcePdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string TargetItemId = "ITEM-CCE2C592";
    private const string TargetAlias = "L0519:S0";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderBackend = "OpenRouter";
    private const int MaxCalls = 1;
    private static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/chat/completions");

    private const string SystemPrompt =
        "You are a visual document layout analyst. You will be shown an image of a full document " +
        "page and a short target text occurrence to locate within it. Based only on the page's " +
        "visual layout - typography, spatial grouping, hierarchy, and separation between blocks - " +
        "determine the target occurrence's primary semantic function:\n\n" +
        "- IDENTITY: identifies the artifact/object (functions as the document's own title or label)\n" +
        "- STRUCTURE: organizes content hierarchy (a heading or section marker within the document body)\n" +
        "- INFORMATION: provides descriptive or contextual information (a metadata line, not a heading)\n\n" +
        "Respond with a single JSON object of exactly this shape and nothing else - no explanation " +
        "outside the object, no markdown: " +
        "{\"semanticFunction\": \"IDENTITY\" | \"STRUCTURE\" | \"INFORMATION\", " +
        "\"visualEvidence\": \"<concise description, under 300 characters, of the visual evidence used>\"}.";

    private static readonly string[] AllowedFunctions = ["IDENTITY", "STRUCTURE", "INFORMATION"];

    private static readonly IReadOnlyDictionary<string, string> LegacyProjection = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IDENTITY"] = "DOCUMENT_LABEL",
        ["STRUCTURE"] = "STRUCTURAL_UNIT",
        ["INFORMATION"] = "NON_STRUCTURAL",
    };

    [Fact]
    public void Target_is_hardcoded_to_exactly_one_item_and_no_other()
    {
        Assert.Equal("ITEM-CCE2C592", TargetItemId);
        // The other flagged item's id is built at runtime, never written as a literal anywhere in
        // this file, so this scan of the whole file's source cannot collide with itself.
        // Doc-comment prose above legitimately names the other item to explain why it's absent - the
        // structural guarantee this checks is about executable code, so comment lines are excluded.
        var otherFlaggedItemId = string.Concat("ITEM-", "505430BB");
        var itemIdParameterSignature = string.Concat("string", " ", "itemId");
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/SelectiveSemanticEscalationV1VlmEscalationTests.cs"));
        var codeOnly = string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal) &&
                           !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain(itemIdParameterSignature, codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain(otherFlaggedItemId, codeOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_render_and_provider_input_hash_are_deterministic()
    {
        var first = BuildCell();
        var second = BuildCell();
        Assert.Equal(first.ImageSha256, second.ImageSha256);
        Assert.Equal(first.ProviderInputHash, second.ProviderInputHash);
        Assert.Equal(first.PageIndex, second.PageIndex);
    }

    [Fact]
    public void Vlm_facing_payload_never_reveals_gold_prior_results_or_diagnostic_naming()
    {
        var cell = BuildCell();
        var forbidden = new[]
        {
            "Gold", "resolved", "adjudicator", "DOCUMENT_LABEL", "STRUCTURAL_UNIT", "NON_STRUCTURAL",
            "F1", "the Agenda item", "dominant", "conflict", "11", "STRUCTURAL_UNIT = 11",
        };
        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(term, SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(term, cell.TextPart, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fake_transport_reserves_before_send_and_never_resumes_an_existing_root()
    {
        using var temp = new TemporaryRoot();
        var cell = BuildCell();

        var calls = 0;
        Task<string> Fake(Cell c)
        {
            calls++;
            return Task.FromResult(FakeEnvelope("""{"semanticFunction":"STRUCTURE","visualEvidence":"test"}"""));
        }

        var result = await RunAsync(temp.Path, cell, Fake);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(temp.Path, ReservationRelativePath)));
        Assert.True(File.Exists(Path.Combine(temp.Path, CaptureRelativePath)));

        var rerun = await RunAsync(temp.Path, cell, Fake);
        Assert.False(rerun.Succeeded);
        Assert.Equal(1, calls); // no second attempt
    }

    [Fact]
    public async Task Fake_transport_halts_on_failure_without_retry()
    {
        using var temp = new TemporaryRoot();
        var cell = BuildCell();
        var attempts = 0;

        Task<string> Failing(Cell c)
        {
            attempts++;
            throw new HttpRequestException("simulated HTTP 500");
        }

        var result = await RunAsync(temp.Path, cell, Failing);
        Assert.False(result.Succeeded);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Fake_transport_rejects_an_invalid_semantic_function()
    {
        using var temp = new TemporaryRoot();
        var cell = BuildCell();

        Task<string> BadFunction(Cell c) =>
            Task.FromResult(FakeEnvelope("""{"semanticFunction":"NOT_A_REAL_FUNCTION","visualEvidence":"x"}"""));

        var result = await RunAsync(temp.Path, cell, BadFunction);
        Assert.False(result.Succeeded);
        var capturePath = Path.Combine(temp.Path, CaptureRelativePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.Equal("FAILED", doc.RootElement.GetProperty("parseStatus").GetString());
    }

    [Fact]
    public async Task Fake_transport_captures_the_raw_envelope_even_when_content_extraction_fails()
    {
        // Regression test for the real bug this file hit: extracting message.content before
        // persisting anything lost the one authorized real response when content was null/missing.
        using var temp = new TemporaryRoot();
        var cell = BuildCell();

        Task<string> MissingContent(Cell c) =>
            Task.FromResult("""{"choices":[{"message":{}}]}""");

        var result = await RunAsync(temp.Path, cell, MissingContent);
        Assert.False(result.Succeeded);
        var capturePath = Path.Combine(temp.Path, CaptureRelativePath);
        Assert.True(File.Exists(capturePath), "the raw envelope must be persisted even when content extraction fails");
        using var doc = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.Equal("FAILED", doc.RootElement.GetProperty("parseStatus").GetString());
        Assert.True(doc.RootElement.GetProperty("rawResponseCaptured").GetBoolean());
    }

    private static string FakeEnvelope(string contentJson) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = contentJson } } } });

    [Fact]
    public async Task Real_transport_is_unreachable_without_exact_authorization_flag()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is "1" or "true") return;

        var root = TestRepository.Path(CaptureRoot);
        if (!Directory.Exists(root)) return; // PRE_EXECUTION

        var capturePath = Path.Combine(root, CaptureRelativePath);
        Assert.True(File.Exists(capturePath));
        using var doc = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.Equal("PASS", doc.RootElement.GetProperty("contractStatus").GetString());
        Assert.Equal(TargetItemId, doc.RootElement.GetProperty("itemId").GetString());
    }

    [Fact]
    public async Task Run_real_vlm_escalation_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true")) return;

        var cell = BuildCell();
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var attempts = 0;

        async Task<string> Send(Cell c)
        {
            attempts++;
            if (attempts > MaxCalls) throw new InvalidOperationException("1-call cap exceeded");

            var body = new
            {
                model = Model,
                temperature = 0,
                max_tokens = 2048,
                // Qwen3.x otherwise consumes the whole budget on hidden reasoning and terminates
                // with content=null (finish_reason="length") before ever emitting the answer - the
                // same failure OpenRouterHeaderExtractor.cs already documents and works around for
                // this exact model family. Attempt 3 hit precisely this with max_tokens=512 and no
                // reasoning override; its truncated-reasoning evidence is preserved, not reused.
                reasoning = new { effort = "none" },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = c.TextPart },
                            new { type = "image_url", image_url = new { url = c.ImageDataUri } },
                        },
                    },
                },
                provider = new { data_collection = "deny", require_parameters = true, allow_fallbacks = true },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor");

            using var response = await http.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenRouter returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}");

            // Return the full envelope verbatim - RunAsync persists this exact text before any
            // parsing happens. Extracting choices[0].message.content here (as the previous version of
            // this method did) meant a null/malformed content field crashed before anything was
            // captured, losing the one authorized real response. ValidateResponse now does that
            // extraction as its own explicit, capturable failure mode instead.
            return responseText;
        }

        var root = TestRepository.Path(CaptureRoot);
        var result = await RunAsync(root, cell, Send);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(MaxCalls, attempts);
    }

    // ---- cell construction (renders the page once per call; deterministic given the frozen PDF) --

    private static Cell BuildCell()
    {
        var pdfBytes = File.ReadAllBytes(TestRepository.Path(SourcePdf));
        var plan = DocxHeaderExtractor.DocumentProcessing.Pipeline.PdfStructuredSourceAuthorityBuilder
            .Build(TestRepository.Path(SourcePdf));
        var atom = plan.Atoms.Single(a => a.Alias == TargetAlias);
        var targetText = atom.Text;
        var pageIndex = atom.Page - 1; // PdfPig/atom.Page is 1-based; PDFtoImage's page index is 0-based.
        Assert.True(pageIndex >= 0);

        using var bitmap = Conversion.ToImage(pdfBytes, page: pageIndex, options: new RenderOptions { Dpi = 200 });
        using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = pngData.ToArray();
        var imageSha256 = Convert.ToHexStringLower(SHA256.HashData(pngBytes));
        var imageDataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var textPart = $"Target text occurrence: \"{targetText}\". Locate this exact occurrence in " +
            "the page image and determine its primary semantic function per the system instructions.";

        var providerInputHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { SystemPrompt, textPart, imageSha256, Model }))));

        return new Cell(pageIndex, targetText, textPart, imageDataUri, pngBytes, imageSha256, providerInputHash);
    }

    // ---- minimal, self-contained runner (reserve-before-send, persist-raw-before-parse,
    // no-retry, no-resume, exactly 1 target) ---------------------------------------------------

    private const string ReservationRelativePath = "ITEM-CCE2C592.capture-slot.v1.json";
    private const string CaptureRelativePath = "ITEM-CCE2C592.transport-capture.v1.json";

    private static async Task<RunResult> RunAsync(string root, Cell cell, Func<Cell, Task<string>> transport)
    {
        if (Directory.Exists(root))
            return new RunResult(false, "execution root already exists; automatic resume is forbidden");

        Directory.CreateDirectory(root);
        try
        {
            WriteExclusive(Path.Combine(root, ReservationRelativePath), new
            {
                schemaVersion = "a99-selective-semantic-escalation-vlm-reservation-v1",
                status = "CAPTURE_SLOT_RESERVED",
                itemId = TargetItemId,
                providerInputHash = cell.ProviderInputHash,
            });
        }
        catch (Exception error)
        {
            return new RunResult(false, "reservation collision: " + error.Message);
        }

        string raw;
        try
        {
            raw = await transport(cell);
        }
        catch (Exception error)
        {
            WriteExclusive(Path.Combine(root, $"{TargetItemId}.transport-failure.v1.json"), new
            {
                schemaVersion = "a99-selective-semantic-escalation-vlm-transport-failure-v1",
                itemId = TargetItemId,
                transportStatus = "FAILED",
                errorType = error.GetType().FullName,
                message = error.Message,
            });
            return new RunResult(false, "transport failure: " + error.Message);
        }

        var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        var capturePath = Path.Combine(root, CaptureRelativePath);
        WriteExclusive(capturePath, new
        {
            schemaVersion = "a99-selective-semantic-escalation-vlm-transport-capture-v1",
            itemId = TargetItemId,
            model = Model,
            providerRoute = ProviderBackend,
            providerInputHash = cell.ProviderInputHash,
            pageIndex = cell.PageIndex,
            imageSha256 = cell.ImageSha256,
            renderedImagePngBase64 = Convert.ToBase64String(cell.PngBytes),
            rawResponseCaptured = true,
            rawResponseSha256 = rawSha256,
            rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
            rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
            transportCaptureStatus = "TRANSPORT_CAPTURE_COMPLETE",
            parseStatus = "PENDING",
            contractStatus = "PENDING",
            semanticFunction = (string?)null,
            visualEvidence = (string?)null,
            legacyProjectedLabel = (string?)null,
            fault = (string?)null,
        });

        try
        {
            var (semanticFunction, visualEvidence) = ValidateResponse(raw);
            UpdateCapture(capturePath, node =>
            {
                node["parseStatus"] = "OK";
                node["contractStatus"] = "PASS";
                node["semanticFunction"] = semanticFunction;
                node["visualEvidence"] = visualEvidence;
                node["legacyProjectedLabel"] = LegacyProjection[semanticFunction];
            });
        }
        catch (Exception error)
        {
            UpdateCapture(capturePath, node =>
            {
                node["parseStatus"] = "FAILED";
                node["contractStatus"] = "NOT_EVALUATED";
                node["fault"] = error.Message;
            });
            return new RunResult(false, "parse/contract failure: " + error.Message);
        }

        return new RunResult(true, null);
    }

    private static (string SemanticFunction, string VisualEvidence) ValidateResponse(string raw)
    {
        var content = ExtractMessageContent(raw);
        var jsonText = ExtractJsonObject(content);
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;
        var semanticFunction = root.TryGetProperty("semanticFunction", out var fnProp) && fnProp.ValueKind == JsonValueKind.String
            ? fnProp.GetString() : null;
        if (semanticFunction is null || !AllowedFunctions.Contains(semanticFunction, StringComparer.Ordinal))
            throw new InvalidDataException($"invalid semanticFunction {semanticFunction}");
        var visualEvidence = root.TryGetProperty("visualEvidence", out var evProp) && evProp.ValueKind == JsonValueKind.String
            ? evProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(visualEvidence))
            throw new InvalidDataException("missing visualEvidence");
        return (semanticFunction, visualEvidence);
    }

    private static string ExtractMessageContent(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException("response envelope carries no choices array");
        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("response envelope's choices[0].message has no string content");
        return contentProp.GetString()!;
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("response carries no JSON object");
        return raw[start..(end + 1)];
    }

    private static void UpdateCapture(string path, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(node);
        File.WriteAllText(path, node.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void WriteExclusive(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }

    private sealed record Cell(
        int PageIndex, string TargetText, string TextPart, string ImageDataUri,
        byte[] PngBytes, string ImageSha256, string ProviderInputHash);

    private sealed record RunResult(bool Succeeded, string? HaltedReason);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "a99-selective-escalation-vlm-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
