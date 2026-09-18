using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// D0 and D2 for DOC-0256, frozen offline.
/// <para>
/// I8 came from this document, and this document has an exhaustive semantic gold, so it is the
/// cheaper and better-grounded place to test the intervention. The PDF lane has no gold yet, and
/// proving a prompt change there would mean measuring the prompt and an unmeasured lane at once.
/// </para>
/// <para>
/// The three targets are Africa, Asia and the Pacific, and Eurostat-OECD PPP Program: headings the
/// model did not propose because each is glued to the participant text that follows it inside one
/// occurrence. Two controls guard the other direction - a heading the model already takes whole
/// must not become partial, and a long prose occurrence must not turn the prompt into an
/// instruction to hunt for heading-shaped substrings everywhere.
/// </para>
/// </summary>
public sealed class DocxExperimentArmPacketTests
{
    private const string Docx =
        "todo10_8/heading_corpus_95_word/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.docx";

    /// <summary>
    /// The gold rows I8 exists to recover, paired with the heading exactly as the source spells it.
    /// They differ for Eurostat, and recording both is the point.
    /// </summary>
    private static readonly (string GoldText, string SourceHeading)[] Targets =
    [
        ("Africa", "Africa"),
        ("Asia and the Pacific", "Asia and the Pacific"),
        ("Eurostat-OECD PPP Program", "Eurostat\u2013OECD PPP Program"),
    ];

    [Fact]
    public async Task D2_changes_the_shared_prompt_and_nothing_else()
    {
        var baseline = await CaptureAsync(CanonicalSemanticExperiment.Baseline);
        var arm = await CaptureAsync(CanonicalSemanticExperiment.PartialSpanOnly);

        Assert.Equal(baseline.Count, arm.Count);
        for (var index = 0; index < baseline.Count; index++)
        {
            var left = Body(baseline[index]);
            var right = Body(arm[index]);
            Assert.Equal(Field(left, "alias"), Field(right, "alias"));
            Assert.Equal(Field(left, "text"), Field(right, "text"));
            Assert.Equal(Owned(left), Owned(right));
        }

        // Segmentation, source and evidence are byte-identical; only the prompt moves.
        Assert.Equal(
            baseline.Select(request => Sha256(request.UserMessage)),
            arm.Select(request => Sha256(request.UserMessage)));
        Assert.Equal(
            baseline.Select(request => request.ExpectedItemCount),
            arm.Select(request => request.ExpectedItemCount));
        Assert.NotEqual(Sha256(baseline[0].SystemPrompt), Sha256(arm[0].SystemPrompt));
        Assert.StartsWith(baseline[0].SystemPrompt, arm[0].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_disambiguation_hints_are_binding_hints_not_coordinates()
    {
        var arm = await CaptureAsync(CanonicalSemanticExperiment.PartialSpanOnly);
        var prompt = arm[0].SystemPrompt;

        Assert.Contains("occurrence", prompt, StringComparison.Ordinal);
        Assert.Contains("leftExactContext", prompt, StringComparison.Ordinal);
        Assert.Contains("rightExactContext", prompt, StringComparison.Ordinal);
        // The permission to disambiguate must not become permission to return a position.
        Assert.Contains("do not return offsets or coordinates", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not return offsets, spans, pages, boxes, coordinates",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeze()
    {
        var baseline = await CaptureAsync(CanonicalSemanticExperiment.Baseline);
        var arm = await CaptureAsync(CanonicalSemanticExperiment.PartialSpanOnly);
        var located = Targets.Select(target => Locate(baseline, target.GoldText, target.SourceHeading)).ToArray();

        // Every target must actually be somewhere in the packets, or the experiment would be
        // measuring segments that cannot show the effect.
        Assert.All(located, target => Assert.True(target.Segment >= 0, $"target not found: {target.GoldText}"));

        var targetSegments = located.Select(target => target.Segment).Distinct().Order().ToArray();
        // A target cannot also be its own control: the occurrence being asked about is exactly the
        // one whose behaviour is under test.
        var controls = Controls(baseline,
            located.Select(target => target.Alias!).ToHashSet(StringComparer.Ordinal));

        var manifest = new
        {
            artifactKind = "DOCX_I8_EXPERIMENT_MANIFEST",
            document = "DOC-0256",
            file = Path.GetFileName(Docx),
            providerCalls = 0,
            goldCoverage = "EXHAUSTIVE, 24 semantic rows, occurrenceEvaluable false",
            arms = new[]
            {
                Describe("D0-baseline", baseline, CanonicalSemanticExperiment.Baseline),
                Describe("D2-i8-partial-span", arm, CanonicalSemanticExperiment.PartialSpanOnly),
            },
            invariants = new
            {
                aliasesIdentical = true,
                sourceTextIdentical = true,
                ownershipIdentical = true,
                segmentationIdentical = true,
                payloadByteIdentical = true,
                onlySystemPromptDiffers = true,
            },
            targets = located.Select(target => new
            {
                goldText = target.GoldText,
                sourceHeading = target.SourceHeading,
                segment = target.Segment,
                alias = target.Alias,
                occurrenceLength = target.Text!.Length,
                occurrenceHead = target.Text[..Math.Min(140, target.Text.Length)],
                whyPartialSpanIsNeeded =
                    "the heading is a prefix of this occurrence; participant text and prose follow it in the same occurrence",
                sourceHeadingOccurrencesInsideTheOccurrence = target.SourceHeadingOccurrences,
                needsDisambiguation = target.SourceHeadingOccurrences > 1,
                disambiguationNote = target.SourceHeadingOccurrences > 1
                    ? "the heading text repeats inside its own occurrence, so an exact substring alone cannot "
                      + "identify it; occurrence or exact context is required, and neither is a coordinate"
                    : "the heading text is unique inside its occurrence",
                goldTextMatchesTheHeadingAsSpelled = target.GoldTextMatchesTheHeading,
                goldTextFirstIndexInOccurrence = target.GoldTextFirstIndex,
                scoreable = target.GoldTextMatchesTheHeading,
            }).ToArray(),
            referenceDefect = located.Any(target => !target.GoldTextMatchesTheHeading)
                ? new
                {
                    severity = "BLOCKS_SCORING_OF_ONE_TARGET",
                    row = "Eurostat-OECD PPP Program",
                    detail = "Gold spells it with a hyphen, U+002D. The heading in the source is spelled with an "
                        + "en dash, U+2013. The hyphen form does occur in this occurrence, but at index 103, inside "
                        + "the body prose, so the gold text matches a mention of the programme rather than the heading.",
                    consequence = "If I8 works perfectly and the model returns the heading as the source spells it, "
                        + "a strict text join against gold still fails for this row. Two of the three targets are "
                        + "cleanly scoreable; this one is not until the row is adjudicated.",
                    notChanged = true,
                    decisionRequired = "Either correct the gold row to the source spelling, or declare a "
                        + "dash-insensitive comparison in advance. Neither should happen silently mid-experiment.",
                }
                : null,
            controls,
            targetedExperiment = new
            {
                note = "Requests, not calls made. Running the target and control segments under both " +
                    "prompts is the number below, and nothing here has been sent.",
                segmentsUnderTest = targetSegments.Concat(controls.Select(control => control.Segment)).Distinct().Order().ToArray(),
                requestsPerArm = targetSegments.Concat(controls.Select(control => control.Segment)).Distinct().Count(),
                arms = 2,
                totalSemanticRequests = targetSegments.Concat(controls.Select(control => control.Segment)).Distinct().Count() * 2,
                plus = "placement requests are additional and depend on what each reply leaves unresolved",
                fullDocumentAlternative = baseline.Count * 2,
            },
        };

        var directory = Path.Combine(RepositoryRoot(), "eval", "a99-closed-loop", "docx-i8-doc0256");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "experiment-manifest.v1.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        await File.WriteAllTextAsync(Path.Combine(directory, "d0-system-prompt.txt"), baseline[0].SystemPrompt);
        await File.WriteAllTextAsync(Path.Combine(directory, "d2-system-prompt.txt"), arm[0].SystemPrompt);
        foreach (var segment in targetSegments.Concat(controls.Select(control => control.Segment)).Distinct())
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"segment-{segment}-payload.json"), baseline[segment].UserMessage);

        Assert.NotEmpty(controls);
    }

    private static object Describe(
        string name, IReadOnlyList<CapturedRequest> requests, CanonicalSemanticExperiment experiment) => new
        {
            arm = name,
            communicatePartialSpan = experiment.CommunicatePartialSpan,
            requests = requests.Count,
            systemPromptSha256 = Sha256(requests[0].SystemPrompt),
            systemPromptChars = requests[0].SystemPrompt.Length,
            payloadSha256 = requests.Select(request => Sha256(request.UserMessage)).ToArray(),
        };

    private sealed record Target(
        string GoldText, string SourceHeading, int Segment, string? Alias, string? Text,
        int SourceHeadingOccurrences, bool GoldTextMatchesTheHeading, int GoldTextFirstIndex);

    private static Target Locate(IReadOnlyList<CapturedRequest> requests, string goldText, string sourceHeading)
    {
        for (var segment = 0; segment < requests.Count; segment++)
        {
            var body = Body(requests[segment]);
            var owned = Owned(body).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in body.GetProperty("sourceEvidence").EnumerateArray())
            {
                var alias = entry.GetProperty("alias").GetString()!;
                var text = entry.GetProperty("text").GetString()!;
                if (!owned.Contains(alias) || !text.StartsWith(sourceHeading, StringComparison.Ordinal)) continue;

                var occurrences = 0;
                for (var at = text.IndexOf(sourceHeading, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(sourceHeading, at + 1, StringComparison.Ordinal))
                    occurrences++;
                var goldAt = text.IndexOf(goldText, StringComparison.Ordinal);
                return new Target(goldText, sourceHeading, segment, alias, text, occurrences,
                    GoldTextMatchesTheHeading: goldAt == 0, goldAt);
            }
        }

        return new Target(goldText, sourceHeading, -1, null, null, 0, false, -1);
    }

    private sealed record Control(string Kind, int Segment, string Alias, string Text, string Why);

    /// <summary>
    /// Guards the other direction. A prompt that permits partial spans must not be read as an
    /// instruction to look for a heading inside every occurrence.
    /// <para>
    /// The whole-span control is taken from the frozen D0 replies rather than picked by shape, so it
    /// is an occurrence the model demonstrably already proposed entire. A control chosen by guessing
    /// what looks like a heading would prove nothing about what the model does with it.
    /// </para>
    /// </summary>
    private static Control[] Controls(IReadOnlyList<CapturedRequest> requests, IReadOnlySet<string> targetAliases)
    {
        var frozen = FrozenWholeSpanProposals();
        var controls = new List<Control>();
        for (var segment = 0; segment < requests.Count; segment++)
        {
            var body = Body(requests[segment]);
            var owned = Owned(body).ToHashSet(StringComparer.Ordinal);
            var entries = body.GetProperty("sourceEvidence").EnumerateArray()
                .Where(entry => owned.Contains(entry.GetProperty("alias").GetString()!))
                .Select(entry => (Alias: entry.GetProperty("alias").GetString()!,
                    Text: entry.GetProperty("text").GetString()!))
                .ToArray();

            if (!controls.Any(control => control.Kind == "WHOLE_SPAN"))
            {
                var whole = entries.FirstOrDefault(entry =>
                    !targetAliases.Contains(entry.Alias) &&
                    frozen.TryGetValue(entry.Alias, out var proposed) &&
                    string.Equals(proposed, entry.Text, StringComparison.Ordinal));
                if (whole.Alias is not null)
                    controls.Add(new Control("WHOLE_SPAN", segment, whole.Alias, whole.Text,
                        "the model proposed this occurrence entire in the frozen D0 run; under I8 it must "
                        + "stay whole rather than shrink to a substring"));
            }

            if (!controls.Any(control => control.Kind == "NON_HEADING_PROSE"))
            {
                var prose = entries
                    .Where(entry => entry.Text.Length > 400 && !frozen.ContainsKey(entry.Alias) &&
                        !targetAliases.Contains(entry.Alias))
                    .OrderByDescending(entry => entry.Text.Length)
                    .FirstOrDefault();
                if (prose.Alias is not null)
                    controls.Add(new Control("NON_HEADING_PROSE", segment, prose.Alias,
                        prose.Text[..140] + "...",
                        "a long prose occurrence the model proposed nothing for; under I8 it must not become "
                        + "a place to hunt for a heading-shaped substring"));
            }
        }

        return [.. controls];
    }

    /// <summary>Alias to verbatim text for every heading the frozen D0 run proposed.</summary>
    private static IReadOnlyDictionary<string, string> FrozenWholeSpanProposals()
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "DocxHeaderExtractor.Tests", "Assets",
            "DOC-0256.frozen-responses.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("responses").EnumerateArray()
            .SelectMany(reply => JsonDocument.Parse(reply.GetString()!).RootElement
                .GetProperty("headings").EnumerateArray()
                .Select(heading => (
                    Alias: heading.GetProperty("sourceAlias").GetString()!,
                    Text: heading.TryGetProperty("verbatimText", out var text) ? text.GetString() : null)))
            .Where(item => item.Text is not null)
            .GroupBy(item => item.Alias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Text!, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<CapturedRequest>> CaptureAsync(CanonicalSemanticExperiment experiment)
    {
        var path = Path.Combine(RepositoryRoot(), Docx.Replace('/', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(path);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var built = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        var state = new DocxPolicyState(source, features, derived, built.Paragraphs, built.StyleTrust);
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());

        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticDocxAuthorityAdapter.RunAsync(state, mode, capture, CancellationToken.None, experiment);
        return capture.Requests;
    }

    private static JsonElement Body(CapturedRequest request)
    {
        var payload = request.UserMessage;
        var schemaAt = payload.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
        return JsonDocument.Parse(schemaAt < 0 ? payload : payload[..schemaAt]).RootElement.Clone();
    }

    private static string[] Field(JsonElement body, string name) => body.GetProperty("sourceEvidence")
        .EnumerateArray().Select(item => item.GetProperty(name).GetString()!).ToArray();

    private static string[] Owned(JsonElement body) => body.GetProperty("ownedSourceAliases")
        .EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
