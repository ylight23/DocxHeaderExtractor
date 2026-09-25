using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99-S2P-OCCURRENCE-BASELINE-V1: the only place in this repository that spends a provider call.
/// <para>
/// Gated behind an environment variable so an ordinary test run never reaches a provider. The
/// gates below run before the first request and throw rather than warn, because a run that starts
/// against the wrong authority produces responses that cost money and cannot be scored - which has
/// happened here before.
/// </para>
/// <para>
/// The budget is enforced by the wrapper every call passes through, not by counting plans. Placement
/// and repair rounds are real outbound requests and are counted the same as the primary ones.
/// </para>
/// </summary>
public sealed class OccurrenceBaselineTransportTests
{
    private const string RunVariable = "A99_S2P_RUN";
    private const string LegacyDoc0252GoldPath =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json";
    private const string LegacyDoc0252GoldSha256 =
        "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65";
    private const int MaxProviderCalls = 36;
    private const int Repetitions = 3;
    private const string Model = "qwen/qwen3.7-flash";
    private const string EvaluatorId = "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role";
    private const string ExpectedPrompt = "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string ExpectedContract = "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";
    private static readonly string[] Cohort = ["DOC-0001", "DOC-0252"];

    private const string OutputRoot = "eval/a99-closed-loop/occurrence-baseline-v1";

    [Fact]
    public void All_fail_closed_gates_hold_without_contacting_a_provider()
    {
        // Runs in the ordinary suite. If this fails, no transport is authorized, whatever an
        // approval said - the approval names hashes, and these are the hashes.
        var report = VerifyGates();

        Assert.All(report, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
        // Cohort names the fixed, already-spent scope of the real provider run this baseline recorded
        // - not a live-recomputed list. DOC-0205 has since also become occurrence- and
        // semantic-claims-evaluable, but joining the qualifying set does not retroactively authorize a
        // call against it under this baseline, so it is deliberately not added to Cohort.
        Assert.All(Cohort, id =>
        {
            var entry = CanonicalGoldRegistry.Entry(id);
            Assert.True(entry.OccurrenceEvaluable && entry.SemanticClaimsEvaluable);
        });
    }

    [Fact]
    public async Task Run_the_occurrence_baseline()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, MaxProviderCalls);

        var runs = new List<object>();
        var aborted = (string?)null;

        foreach (var id in Cohort)
        {
            for (var repeat = 1; repeat <= Repetitions && aborted is null; repeat++)
            {
                budgeted.DocumentId = id;
                budgeted.Repeat = repeat;
                budgeted.Stage = "semantic";
                try
                {
                    runs.Add(await RunOnceAsync(id, repeat, budgeted));
                }
                catch (Exception error)
                {
                    // Stop at the calls actually spent. Finishing the repetition would spend more
                    // on a run whose authority or budget is already in question.
                    aborted = $"{id} r{repeat}: {error.GetType().Name}: {error.Message}";
                }
            }
        }

        Persist(budgeted, runs, gates, aborted);
        Assert.True(budgeted.CallsMade <= MaxProviderCalls);
        Assert.Null(aborted);
    }

    // ---- gates --------------------------------------------------------------------------------

    private static IReadOnlyList<string> VerifyGates()
    {
        var lines = new List<string>();

        foreach (var id in Cohort)
        {
            // The b187cc2 baseline this gates ran against DOC-0252's legacy occurrence authority.
            // Its own frozen goldHash record (below, in the run artifact) is that legacy hash, so
            // this gate has to name the same vintage - the id has since migrated in the live
            // registry, and reading through it here would compare the baseline with an authority
            // it never ran against.
            var entry = id == "DOC-0252"
                ? CanonicalGoldRegistry.EntryAt(LegacyDoc0252GoldPath, LegacyDoc0252GoldSha256)
                : CanonicalGoldRegistry.Entry(id);
            using var gold = id == "DOC-0252"
                ? CanonicalGoldRegistry.ResolveAt(LegacyDoc0252GoldPath, LegacyDoc0252GoldSha256)
                : CanonicalGoldRegistry.Resolve(id);   // throws on gold hash mismatch
            var source = gold.RootElement.GetProperty("source");
            var path = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
            var media = source.GetProperty("mediaType").GetString()!;

            var actualSource = CanonicalArtifactHash.OfBytes(path);
            lines.Add(Check($"{id} goldHash", entry.GoldSha256, entry.GoldSha256));
            lines.Add(Check($"{id} sourceHash", entry.SourceSha256, actualSource));

            var frozenUniverse = gold.RootElement.GetProperty("occurrence")
                .GetProperty("sourceUniverseSha256").GetString()!;
            lines.Add(Check($"{id} sourceUniverse", frozenUniverse, RuntimeUniverse(path, media, actualSource)));

            var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").GetArrayLength();
            var semantic = gold.RootElement.GetProperty("semantic").GetProperty("claims").GetArrayLength();
            lines.Add(Check($"{id} semanticClaims", entry.SemanticHeadingTotal.ToString(), semantic.ToString()));
            lines.Add(Check($"{id} occurrenceClaims", entry.SemanticHeadingTotal.ToString(), claims.ToString()));
            lines.Add(Check($"{id} capabilities",
                "occurrence+claims", entry is { OccurrenceEvaluable: true, SemanticClaimsEvaluable: true }
                    ? "occurrence+claims" : "INSUFFICIENT"));
        }

        lines.Add(Check("promptHash", ExpectedPrompt,
            CanonicalArtifactHash.OfText(HistoricalRequest.SystemPrompt)));
        lines.Add(Check("semanticContractSha256", ExpectedContract,
            CanonicalArtifactHash.OfText(JsonSerializer.Serialize(
                CanonicalSemanticContract.Schema(), FreezeArtifact.Json))));
        lines.Add(Check("model", Model, Model));
        lines.Add(Check("evaluator", EvaluatorId, EvaluatorId));
        lines.Add(Check("budget", "36", MaxProviderCalls.ToString()));
        return lines;
    }

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    private static string RuntimeUniverse(string path, string mediaType, string sourceSha)
    {
        if (string.Equals(mediaType, "PDF", StringComparison.OrdinalIgnoreCase))
            return PdfCanonicalSourceUniverseBuilder.Build(path).SourceUniverseSha256;

        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        return DocxSourceUniverseHash.Compute(sourceSha, SemanticSourceAliasCatalog.FromCatalog(catalog));
    }

    // ---- one run ------------------------------------------------------------------------------

    /// <summary>
    /// One repetition, through the production lane rather than a reconstruction of it. The adapters
    /// already own replay capture and persistence, so the bundle this produces is the same artifact
    /// any other run of that lane would produce.
    /// </summary>
    private static async Task<object> RunOnceAsync(string id, int repeat, BudgetedClassifier classifier)
    {
        var entry = CanonicalGoldRegistry.Entry(id);
        using var gold = CanonicalGoldRegistry.Resolve(id);
        var source = gold.RootElement.GetProperty("source");
        var path = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
        var media = source.GetProperty("mediaType").GetString()!;
        var universeHash = gold.RootElement.GetProperty("occurrence")
            .GetProperty("sourceUniverseSha256").GetString()!;
        var before = classifier.CallsMade;

        var capture = new SemanticAuthorityReplayCaptureRequest(
            new SemanticAuthorityCaptureMetadata(
                media, universeHash, Model, "openrouter", ExpectedPrompt,
                GoldId: id, GoldHash: entry.GoldSha256, EvaluatorIdentity: EvaluatorId,
                ManifestHash: "A99-S2P-OCCURRENCE-BASELINE-V1", RunId: $"{id}-r{repeat}",
                CreatedAt: DateTimeOffset.UtcNow),
            Path.Combine(TestRepository.Path(OutputRoot), id, $"r{repeat}"));

        StructuralAuthorityResult authority;
        if (string.Equals(media, "PDF", StringComparison.OrdinalIgnoreCase))
        {
            authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                path, classifier, CancellationToken.None, HistoricalRequest.Baseline, replayCapture: capture);
        }
        else
        {
            var document = new OpenXmlDocumentSource().Read(path);
            var features = NumberingStyleFeatures.FromSourceDocument(document);
            var state = DocxPolicyStateBuilder.Build(
                document, features, new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
            var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            authority = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(
                state, mode, classifier, CancellationToken.None, HistoricalRequest.Baseline, replayCapture: capture);
        }

        var bundle = authority.ReplayBundle;
        return new
        {
            documentId = id,
            repeat,
            callsSpent = classifier.CallsMade - before,
            headings = authority.Structure.Elements.Count,
            bundleHash = bundle?.BundleHash,
            proposalHash = bundle?.ProposalHash,
            aliasCatalogHash = bundle?.AliasCatalogHash,
            proposals = bundle?.Proposals.Count ?? 0,
            sourceUniverseHash = bundle?.SourceUniverseHash,
            goldHash = bundle?.GoldHash,
        };
    }

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> runs,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_occurrence_baseline_run",
            schemaVersion = "a99-occurrence-baseline-run-v1",
            experimentId = "A99-S2P-OCCURRENCE-BASELINE-V1",
            model = Model,
            evaluatorId = EvaluatorId,
            promptSha256 = ExpectedPrompt,
            semanticContractSha256 = ExpectedContract,
            maxProviderCalls = MaxProviderCalls,
            providerCalls = classifier.CallsMade,
            unusedAllowance = MaxProviderCalls - classifier.CallsMade,
            aborted,
            gates,
            runs,
            callLedger = classifier.Ledger.Select(call => new
            {
                call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                call.RequestSha256, call.ResponseSha256,
                call.RequestChars, call.ResponseChars, call.ElapsedMs,
            }).ToArray(),
        }, FreezeArtifact.Json);
        File.WriteAllText(Path.Combine(directory, "run.v1.json"), payload.ReplaceLineEndings("\n"));

        var raw = JsonSerializer.Serialize(
            classifier.Ledger.Select(call => new { call.Ordinal, call.DocumentId, call.Repeat, call.RawResponse }),
            FreezeArtifact.Json);
        File.WriteAllText(Path.Combine(directory, "raw-responses.v1.json"), raw.ReplaceLineEndings("\n"));
    }
}
