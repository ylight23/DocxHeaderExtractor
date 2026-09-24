using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXPLORATORY, gated, real spend: the production PDF harness (qwen/qwen3.7-flash) on DOC-0256, once
/// per call, on either PDF lane - LegacyOccurrence (what an ordinary upload gets) or
/// StructuredSourceParts (the atom lane PDF Gold is bound in). Membership only: placement is off, so
/// every call counted here is a semantic call. Every request and reply is kept in the ledger.
/// Output is evidence for a human decision, not a Gold write.
/// </summary>
public sealed class Doc0256RealHarnessExplorationTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf";
    private const string Model = "qwen/qwen3.7-flash";
    private const string OutputRoot = "eval/a99-closed-loop/doc0256-real-harness-exploration-v1";

    [Fact]
    public void Planned_calls_per_run_are_measured_offline()
    {
        var legacy = PdfCanonicalSourceUniverseBuilder.Build(TestRepository.Path(Pdf)).Evidence.Count;
        var structured = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Pdf));
        Assert.Equal(695, legacy);
        Assert.Equal(6, (int)Math.Ceiling(legacy / 120.0));
        Assert.Equal(546, structured.Atoms.Count);
        Assert.Equal(5, structured.Packs.Count);

        // The one heading the structured lane missed in all three runs: AfDB (L0422) is the
        // second-to-last atom pack 4 owns, while its attendee list and every organisation the model
        // did claim sit in pack 5, which sees L0422 only as margin it may not claim.
        var pack4 = structured.Packs[3];
        Assert.Contains("L0422:S0", pack4.OwnedAliases);
        Assert.Equal("L0423:S1", pack4.OwnedAliases[^1]);
        Assert.DoesNotContain("L0422:S0", structured.Packs[4].OwnedAliases);
        Assert.Contains("L0422:S0", structured.Packs[4].VisibleAliases);
    }

    [Fact]
    public async Task Run_once()
    {
        if (Environment.GetEnvironmentVariable("A99_DOC0256_EXPLORE_RUN") != "1") return;
        var lane = Environment.GetEnvironmentVariable("A99_DOC0256_EXPLORE_LANE") ?? "legacy";
        var repeat = Environment.GetEnvironmentVariable("A99_DOC0256_EXPLORE_REPEAT") ?? "1";
        var ceiling = int.Parse(Environment.GetEnvironmentVariable("A99_DOC0256_EXPLORE_CEILING") ?? "8");
        var profile = lane == "structured"
            ? PdfSemanticAuthorityProfile.StructuredSourceParts
            : PdfSemanticAuthorityProfile.LegacyOccurrence;

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var budgeted = new BudgetedClassifier(
            new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = apiKey, Model = Model }), ceiling)
        {
            DocumentId = "DOC-0256",
            Stage = $"semantic-{lane}",
        };

        string? failure = null;
        IReadOnlyList<object> elements = [];
        string? reason = null;
        try
        {
            var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                TestRepository.Path(Pdf), budgeted, CancellationToken.None, profile: profile, runPlacement: false);
            reason = authority.Reason;
            elements = authority.Structure.Elements
                .Select(e => (object)new { text = e.Text, sourceIds = e.Sources.Select(s => s.SourceId).ToArray() })
                .ToArray();
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
        }

        Directory.CreateDirectory(TestRepository.Path(OutputRoot));
        File.WriteAllText(TestRepository.Path($"{OutputRoot}/{lane}-r{repeat}.v1.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_doc0256_real_harness_exploration",
            documentId = "DOC-0256",
            model = Model,
            profile = profile.ProfileId,
            placement = false,
            repeat,
            providerCallsMade = budgeted.CallsMade,
            failure,
            reason,
            elementCount = elements.Count,
            elements,
            ledger = budgeted.Ledger,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Fail($"lane={lane} repeat={repeat} calls={budgeted.CallsMade} elements={elements.Count} failure={failure}");
    }
}
